using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using KodDesk.Core.Capture;
using KodDesk.Core.Clipboard;
using KodDesk.Core.Files;
using KodDesk.Core.Input;
using KodDesk.Core.Native;
using KodDesk.Core.Video;
using KodDesk.Protocol;

namespace KodDesk.Core.Host;

public enum SessionOutcome
{
    ConnectFailed,
    AuthFailed,
    Completed,
}

/// <summary>
/// O sesiune pe partea calculatorului controlat: handshake, flux video, input.
/// Fluxul video rulează pe un thread dedicat; controlul fluxului se face prin confirmări (FrameAck)
/// de la viewer, astfel încât latența nu crește când rețeaua e mai lentă decât encoderul.
/// </summary>
public sealed class HostSession
{
    /// <summary>Numărul maxim de cadre trimise și încă neconfirmate.</summary>
    private const int MaxInFlight = 3;

    /// <summary>După o modificare a ecranului, se mai trimit câteva cadre identice ca encoderul să rafineze calitatea.</summary>
    private const int RefreshFrames = 3;
    private const int RefreshIntervalMs = 150;

    private readonly HostSettings _settings;
    private readonly string _token;
    private readonly InputInjector _input = new();
    private readonly BlockingCollection<Action> _inputQueue = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly AutoResetEvent _ackEvent = new(false);
    private MessageChannel? _ch;
    private VideoEncoder? _encoder;
    private FileTransferChannel? _files;
    private ClipboardSync? _clipboard;

    // Stare partajată între bucla de recepție și thread-ul video.
    private volatile bool _forceKeyframe = true;
    private volatile bool _encoderDirty;
    private volatile bool _monitorDirty;
    private volatile int _requestedMonitor;
    private volatile QualitySettings _quality = new(12000, 60);
    private volatile MonitorInfo? _currentMonitor;
    private uint _sentSeq;
    private uint _ackedSeq;

    public HostSession(HostSettings settings, string token)
    {
        _settings = settings;
        _token = token;
    }

    /// <summary>Apelat după autentificare reușită (înainte de pornirea fluxului).</summary>
    public event Action<HostSession>? Authenticated;

    /// <summary>Viewer-ul a cerut Ctrl+Alt+Del (se poate executa doar de serviciu).</summary>
    public event Action? SecureAttentionRequested;

    public void Stop() => _cts.Cancel();

    public async Task<SessionOutcome> RunAsync(CancellationToken outer)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(outer, _cts.Token);
        var ct = linked.Token;

        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        try
        {
            await ws.ConnectAsync(RelayUrls.Session(_settings.ActiveRelayAddress, _settings.ActiveRelayKey, _token), ct);
        }
        catch (Exception ex)
        {
            Log.Warn("Nu pot deschide canalul de sesiune", ex);
            ws.Dispose();
            return SessionOutcome.ConnectFailed;
        }

        await using var ch = new MessageChannel(ws);
        _ch = ch;

        SecureHandshake.HostResult auth;
        try
        {
            using var hsTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hsTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            auth = await SecureHandshake.RunHostAsync(ch, _settings.HostId, Environment.MachineName,
                _settings.GetPasswordKey(), hsTimeout.Token);
        }
        catch (Exception ex)
        {
            Log.Warn("Handshake eșuat", ex);
            return SessionOutcome.AuthFailed;
        }
        if (!auth.Success)
        {
            Log.Warn("Conexiune respinsă: " + auth.Error);
            return SessionOutcome.AuthFailed;
        }

        Log.Info("Viewer autentificat; pornesc sesiunea");
        Authenticated?.Invoke(this);
        ch.SendFailed += ex => { Log.Warn("Trimitere eșuată", ex); _cts.Cancel(); };
        ch.StartSending();

        _requestedMonitor = ScreenCapturer.PrimaryIndex();
        ch.Send(MessageType.SessionInfo, new SessionInfo(Environment.MachineName, ScreenCapturer.GetMonitors(), _requestedMonitor).Write());

        var thread = new Thread(() => StreamLoop(ct))
        {
            IsBackground = true,
            Name = "KodDesk video",
            Priority = ThreadPriority.AboveNormal,
        };
        thread.Start();

        // Input-ul se injectează de pe un thread dedicat, care se poate muta pe desktopul activ (login, UAC).
        var inputThread = new Thread(InputLoop) { IsBackground = true, Name = "KodDesk input" };
        inputThread.Start();

        // Fișiere (în numele utilizatorului logat) și clipboard.
        _files = new FileTransferChannel(ch, FileAccessContexts.ForThisProcess(), serveRequests: true);
        try
        {
            _clipboard = new ClipboardSync(ch, _files, sendCurrent: false);
        }
        catch (Exception ex)
        {
            Log.Warn("Clipboard indisponibil", ex);
        }

        try
        {
            await ReceiveLoopAsync(ch, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn("Eroare la recepție", ex);
        }
        finally
        {
            _cts.Cancel();
            thread.Join(TimeSpan.FromSeconds(3));
            _inputQueue.Add(_input.ReleaseAll);
            _inputQueue.CompleteAdding();
            inputThread.Join(TimeSpan.FromSeconds(2));
            _clipboard?.Dispose();
            _files.Dispose();
            Log.Info("Sesiune închisă");
        }
        return SessionOutcome.Completed;
    }

    private async Task ReceiveLoopAsync(MessageChannel ch, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var msg = await ch.ReceiveAsync(ct);
            if (msg is null) return;
            var payload = msg.Value.Payload;

            switch (msg.Value.Type)
            {
                case MessageType.FrameAck:
                    Volatile.Write(ref _ackedSeq, new PacketReader(payload.Span).U32());
                    _ackEvent.Set();
                    break;

                case MessageType.RequestKeyframe:
                    _forceKeyframe = true;
                    break;

                case MessageType.SetQuality:
                {
                    var q = QualitySettings.Read(payload.Span);
                    _quality = new QualitySettings(Math.Clamp(q.BitrateKbps, 500, 100_000), Math.Clamp(q.MaxFps, 5, 120));
                    _encoderDirty = true;
                    break;
                }

                case MessageType.SelectMonitor:
                    _requestedMonitor = new PacketReader(payload.Span).U8();
                    _monitorDirty = true;
                    break;

                case MessageType.MouseMove:
                {
                    var r = new PacketReader(payload.Span);
                    ushort x = r.U16(), y = r.U16();
                    if (_currentMonitor is { } mon) Input(() => _input.MouseMove(x, y, mon));
                    break;
                }

                case MessageType.MouseButton:
                {
                    var r = new PacketReader(payload.Span);
                    var button = (MouseButtonKind)r.U8();
                    bool down = r.Bool();
                    Input(() => _input.MouseButton(button, down));
                    break;
                }

                case MessageType.MouseWheel:
                {
                    var r = new PacketReader(payload.Span);
                    var delta = r.I16();
                    bool horizontal = r.Bool();
                    Input(() => _input.MouseWheel(delta, horizontal));
                    break;
                }

                case MessageType.Key:
                {
                    var r = new PacketReader(payload.Span);
                    ushort scan = r.U16();
                    bool ext = r.Bool();
                    bool down = r.Bool();
                    Input(() => _input.Key(scan, ext, down));
                    break;
                }

                case MessageType.ReleaseAllKeys:
                    Input(_input.ReleaseAll);
                    break;

                case MessageType.SecureAttention:
                    Log.Info("Viewer-ul a cerut Ctrl+Alt+Del");
                    SecureAttentionRequested?.Invoke();
                    break;

                case MessageType.Ping:
                    ch.Send(MessageType.Pong, payload.Span);
                    break;

                case MessageType.Disconnect:
                    return;

                default:
                    if (!_files!.Handle(msg.Value.Type, payload.Span))
                        _clipboard?.Handle(msg.Value.Type, payload.Span);
                    break;
            }
        }
    }

    private void Input(Action action)
    {
        if (!_inputQueue.IsAddingCompleted) _inputQueue.TryAdd(action);
    }

    private void InputLoop()
    {
        foreach (var action in _inputQueue.GetConsumingEnumerable())
        {
            try { action(); }
            catch (Exception ex) { Log.Warn("Eroare la injectarea input-ului", ex); }
        }
    }

    private void StreamLoop(CancellationToken ct)
    {
        Win32.timeBeginPeriod(1);
        var clock = Stopwatch.StartNew();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                _monitorDirty = false;
                using var cap = new ScreenCapturer(_requestedMonitor);
                try
                {
                    CaptureLoop(cap, clock, ct);
                }
                finally
                {
                    _encoder?.Dispose();
                    _encoder = null;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Eroare în fluxul video", ex);
            _cts.Cancel();
        }
        finally
        {
            Win32.timeEndPeriod(1);
        }
    }

    private void CaptureLoop(ScreenCapturer cap, Stopwatch clock, CancellationToken ct)
    {
        long lastEncode = -100_000;
        int refreshLeft = 0;
        long blockedSince = -1;

        while (!ct.IsCancellationRequested && !_monitorDirty)
        {
            // Controlul fluxului: nu trimitem mai mult de MaxInFlight cadre neconfirmate.
            uint inFlight = _sentSeq - Volatile.Read(ref _ackedSeq);
            if (inFlight >= MaxInFlight)
            {
                if (blockedSince < 0) blockedSince = clock.ElapsedMilliseconds;
                _ackEvent.WaitOne(50);
                if (clock.ElapsedMilliseconds - blockedSince > 5000)
                {
                    Log.Warn("Nicio confirmare de cadre de 5 s; resetez controlul fluxului");
                    Volatile.Write(ref _ackedSeq, _sentSeq);
                    blockedSince = -1;
                }
                continue;
            }
            blockedSince = -1;

            var q = _quality;
            int frameInterval = 1000 / q.MaxFps;
            bool force = _forceKeyframe;
            long now = clock.ElapsedMilliseconds;
            int timeout = force ? 0
                : refreshLeft > 0 ? (int)Math.Clamp(lastEncode + RefreshIntervalMs - now, 0, 30)
                : 30;

            var status = cap.TryCapture(timeout);
            if (status == CaptureStatus.Unavailable || !cap.HasFrame) continue;

            bool changed = status == CaptureStatus.NewFrame;
            if (changed) refreshLeft = RefreshFrames;
            _currentMonitor = cap.Monitor;

            if (_encoder == null || _encoderDirty || _encoder.Width != (cap.Width & ~1) || _encoder.Height != (cap.Height & ~1))
            {
                _encoderDirty = false;
                _encoder?.Dispose();
                _encoder = null;
                _encoder = VideoEncoder.Create(cap.Width, cap.Height, q.BitrateKbps, q.MaxFps);
                _ch!.Send(MessageType.VideoFormat,
                    new VideoFormat(_encoder.Width, _encoder.Height, "h264", _encoder.Yuv444, _encoder.FullRange, _encoder.Name, cap.MonitorIndex).Write());
                force = true;
            }

            now = clock.ElapsedMilliseconds;
            bool refresh = !changed && !force && refreshLeft > 0 && now - lastEncode >= RefreshIntervalMs;
            if (!changed && !force && !refresh) continue;

            long wait = lastEncode + frameInterval - now;
            if (wait > 0) Thread.Sleep((int)wait);

            if (force) _forceKeyframe = false;
            if (refresh) refreshLeft--;
            lastEncode = clock.ElapsedMilliseconds;
            long ts = lastEncode;
            _encoder.Encode(cap.Data, cap.Stride, ts, force, (data, key) => SendFrame(data, key, ts));
        }
    }

    private void SendFrame(ReadOnlySpan<byte> data, bool keyframe, long timestampMs)
    {
        var seq = ++_sentSeq;
        var buf = new byte[VideoFrameHeader.Size + data.Length];
        VideoFrameHeader.Write(buf, seq, keyframe ? VideoFrameFlags.Keyframe : VideoFrameFlags.None, timestampMs);
        data.CopyTo(buf.AsSpan(VideoFrameHeader.Size));
        _ch!.Send(MessageType.VideoFrame, buf, lowPriority: true);
    }
}
