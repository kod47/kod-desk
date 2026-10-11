using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using KodDesk.Core.Clipboard;
using KodDesk.Core.Files;
using KodDesk.Core.Video;
using KodDesk.Protocol;

namespace KodDesk.Core.Viewer;

public sealed record ViewerStats(double Fps, double Mbps, int RttMs, string Encoder, bool Yuv444, int Width, int Height);

/// <summary>
/// Sesiune pe partea care controlează: primește video, îl decodează pe un thread separat
/// și trimite input-ul utilizatorului.
/// </summary>
public sealed class ViewerSession : IAsyncDisposable
{
    private readonly MessageChannel _ch;
    private readonly CancellationTokenSource _cts = new();
    private readonly BlockingCollection<DecodeItem> _decodeQueue = new(new ConcurrentQueue<DecodeItem>(), 64);
    private readonly Thread _decodeThread;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Task? _receiveLoop, _pingLoop;
    private VideoFormat? _format;
    private int _rttMs;
    private int _framesDecoded;
    private long _statsBytes, _statsTime;
    private int _statsFrames;
    private ViewerStats? _stats;
    private string? _closeReason;

    private abstract record DecodeItem;
    private sealed record FormatItem(VideoFormat Format) : DecodeItem;
    private sealed record FrameItem(uint Seq, bool Keyframe, byte[] Data) : DecodeItem;

    private ViewerSession(MessageChannel ch, string hostId, string hostName, byte[] passwordKey)
    {
        _ch = ch;
        HostId = hostId;
        HostName = hostName;
        PasswordKey = passwordKey;
        _decodeThread = new Thread(DecodeLoop) { IsBackground = true, Name = "KodDesk decode", Priority = ThreadPriority.AboveNormal };
    }

    public string HostId { get; }
    public string HostName { get; }

    /// <summary>Cheia derivată din parolă, folosită la autentificare (se poate memora).</summary>
    public byte[] PasswordKey { get; }
    public FrameBuffer Frames { get; } = new();

    /// <summary>Transferul de fișiere (încărcare, explorare, descărcare).</summary>
    public FileTransferChannel Files { get; private set; } = null!;

    /// <summary>Sincronizarea clipboard-ului (null dacă nu a putut porni).</summary>
    public ClipboardSync? Clipboard { get; private set; }
    public SessionInfo? Info { get; private set; }
    public VideoFormat? Format => _format;

    public event Action<SessionInfo>? SessionInfoReceived;
    public event Action<VideoFormat>? FormatChanged;

    /// <summary>Un cadru nou e disponibil în <see cref="Frames"/> (apelat din thread-ul de decodare).</summary>
    public event Action? FrameReady;

    /// <summary>Sesiunea s-a încheiat; argumentul este motivul (null dacă închiderea a fost cerută local).</summary>
    public event Action<string?>? Closed;

    /// <param name="password">Parola introdusă, sau null dacă se folosește <paramref name="savedKey"/>.</param>
    /// <param name="savedKey">Cheia memorată la o conectare anterioară.</param>
    public static async Task<ViewerSession> ConnectAsync(ViewerSettings settings, string hostId, string? password,
        byte[]? savedKey, CancellationToken ct)
    {
        hostId = SecureHandshake.NormalizeId(hostId);
        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        try
        {
            await ws.ConnectAsync(RelayUrls.Connect(settings.ActiveRelayAddress, settings.ActiveRelayKey, hostId), ct);
        }
        catch (WebSocketException ex)
        {
            ws.Dispose();
            throw new IOException("Nu mă pot conecta la serverul relay: " + ex.Message, ex);
        }

        MessageChannel? ch = null;
        try
        {
            var reply = await WebSocketText.ReceiveAsync(ws, ct) ?? throw new IOException("Relay-ul a închis conexiunea");
            if (reply.Type == RelayMessage.Error) throw new IOException(reply.Message ?? "Eroare relay");
            if (reply.Type != RelayMessage.Connected) throw new InvalidDataException("Răspuns neașteptat de la relay");

            ch = new MessageChannel(ws);
            using var hsTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hsTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            var result = await SecureHandshake.RunViewerAsync(ch, hostId, password, password == null ? savedKey : null, hsTimeout.Token);

            var session = new ViewerSession(ch, result.HostId, result.HostName, result.PasswordKey);
            session.Start(new QualitySettings(settings.BitrateKbps, settings.MaxFps));
            Log.Info($"Conectat la {result.HostName} ({result.HostId})");
            return session;
        }
        catch
        {
            if (ch != null) await ch.DisposeAsync();
            else ws.Dispose();
            throw;
        }
    }

    private void Start(QualitySettings quality)
    {
        _ch.SendFailed += ex => Close("Conexiune pierdută: " + ex.Message);
        _ch.StartSending();
        _ch.Send(MessageType.SetQuality, quality.Write());
        Files = new FileTransferChannel(_ch, new CurrentUserContext(), serveRequests: false);
        try
        {
            Clipboard = new ClipboardSync(_ch, Files, sendCurrent: true);
        }
        catch (Exception ex)
        {
            Log.Warn("Clipboard indisponibil", ex);
        }
        _decodeThread.Start();
        _receiveLoop = Task.Run(ReceiveLoopAsync);
        _pingLoop = Task.Run(PingLoopAsync);
    }

    private async Task ReceiveLoopAsync()
    {
        var ct = _cts.Token;
        string? reason = "Conexiunea a fost închisă de calculatorul la distanță.";
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var msg = await _ch.ReceiveAsync(ct);
                if (msg is null) break;
                var payload = msg.Value.Payload;
                switch (msg.Value.Type)
                {
                    case MessageType.SessionInfo:
                        Info = SessionInfo.Read(payload.Span);
                        SessionInfoReceived?.Invoke(Info);
                        break;

                    case MessageType.VideoFormat:
                        _decodeQueue.Add(new FormatItem(VideoFormat.Read(payload.Span)), ct);
                        break;

                    case MessageType.VideoFrame:
                    {
                        var (seq, flags, _, data) = VideoFrameHeader.Read(payload);
                        Interlocked.Add(ref _statsBytes, payload.Length);
                        _decodeQueue.Add(new FrameItem(seq, flags.HasFlag(VideoFrameFlags.Keyframe), data.ToArray()), ct);
                        break;
                    }

                    case MessageType.Pong:
                    {
                        var sent = new PacketReader(payload.Span).I64();
                        _rttMs = (int)(_clock.ElapsedMilliseconds - sent);
                        break;
                    }

                    case MessageType.Disconnect:
                        Close("Sesiunea a fost închisă de calculatorul la distanță.");
                        return;

                    default:
                        if (!Files.Handle(msg.Value.Type, payload.Span))
                            Clipboard?.Handle(msg.Value.Type, payload.Span);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            reason = null;
        }
        catch (Exception ex)
        {
            Log.Warn("Eroare la recepție (viewer)", ex);
            reason = "Eroare de conexiune: " + ex.Message;
        }
        Close(reason);
    }

    private async Task PingLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(_cts.Token))
                _ch.Send(MessageType.Ping, new PacketWriter().I64(_clock.ElapsedMilliseconds));
        }
        catch (OperationCanceledException) { }
    }

    private void DecodeLoop()
    {
        VideoDecoder? decoder = null;
        bool waitingForKeyframe = true;
        long lastKeyRequest = 0;
        try
        {
            foreach (var item in _decodeQueue.GetConsumingEnumerable(_cts.Token))
            {
                switch (item)
                {
                    case FormatItem f:
                        decoder?.Dispose();
                        decoder = new VideoDecoder(f.Format.FullRange);
                        _format = f.Format;
                        waitingForKeyframe = true;
                        Log.Info($"Format video: {f.Format.Width}x{f.Format.Height} {f.Format.EncoderName} {(f.Format.Yuv444 ? "4:4:4" : "4:2:0")}");
                        FormatChanged?.Invoke(f.Format);
                        break;

                    case FrameItem fr:
                        bool ok = false;
                        if (decoder != null && (!waitingForKeyframe || fr.Keyframe))
                        {
                            try
                            {
                                if (decoder.Decode(fr.Data, Frames))
                                {
                                    Interlocked.Increment(ref _framesDecoded);
                                    FrameReady?.Invoke();
                                }
                                waitingForKeyframe = false;
                                ok = true;
                            }
                            catch (Exception ex)
                            {
                                Log.Warn("Eroare la decodare", ex);
                                waitingForKeyframe = true;
                            }
                        }
                        if (!ok && _clock.ElapsedMilliseconds - lastKeyRequest > 1000)
                        {
                            lastKeyRequest = _clock.ElapsedMilliseconds;
                            _ch.Send(MessageType.RequestKeyframe);
                        }
                        // Confirmarea permite host-ului să trimită cadrul următor (controlul fluxului).
                        _ch.Send(MessageType.FrameAck, new PacketWriter().U32(fr.Seq));
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("Thread-ul de decodare s-a oprit", ex);
            Close("Eroare la decodarea video: " + ex.Message);
        }
        finally
        {
            decoder?.Dispose();
        }
    }

    /// <summary>Statistici actualizate (apelați periodic, ex. o dată pe secundă).</summary>
    public ViewerStats GetStats()
    {
        long now = _clock.ElapsedMilliseconds;
        long dt = now - _statsTime;
        if (dt >= 500 || _stats == null)
        {
            int frames = Volatile.Read(ref _framesDecoded);
            long bytes = Interlocked.Exchange(ref _statsBytes, 0);
            double sec = Math.Max(dt, 1) / 1000.0;
            var f = _format;
            _stats = new ViewerStats((frames - _statsFrames) / sec, bytes * 8 / 1e6 / sec, _rttMs,
                f?.EncoderName ?? "-", f?.Yuv444 ?? false, f?.Width ?? 0, f?.Height ?? 0);
            _statsFrames = frames;
            _statsTime = now;
        }
        return _stats;
    }

    // ---- Input ----

    public void SendMouseMove(ushort x, ushort y) =>
        _ch.Send(MessageType.MouseMove, new PacketWriter(4).U16(x).U16(y));

    public void SendMouseButton(MouseButtonKind button, bool down) =>
        _ch.Send(MessageType.MouseButton, new PacketWriter(2).U8((byte)button).Bool(down));

    public void SendMouseWheel(int delta, bool horizontal) =>
        _ch.Send(MessageType.MouseWheel, new PacketWriter(3).I16((short)Math.Clamp(delta, short.MinValue, short.MaxValue)).Bool(horizontal));

    public void SendKey(ushort scanCode, bool extended, bool down) =>
        _ch.Send(MessageType.Key, new PacketWriter(4).U16(scanCode).Bool(extended).Bool(down));

    public void SendReleaseAllKeys() => _ch.Send(MessageType.ReleaseAllKeys);

    public void SelectMonitor(int index) => _ch.Send(MessageType.SelectMonitor, new PacketWriter(1).U8((byte)index));

    public void SetQuality(QualitySettings q) => _ch.Send(MessageType.SetQuality, q.Write());

    public void RequestKeyframe() => _ch.Send(MessageType.RequestKeyframe);

    public void SendSecureAttention() => _ch.Send(MessageType.SecureAttention);

    private void Close(string? reason)
    {
        lock (_cts)
        {
            if (_closeReason != null || _cts.IsCancellationRequested) return;
            _closeReason = reason ?? "";
            _cts.Cancel();
        }
        Closed?.Invoke(reason);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_cts.IsCancellationRequested)
        {
            try { _ch.Send(MessageType.Disconnect); await Task.Delay(100); } catch { }
        }
        Close(null);
        if (_receiveLoop != null) try { await _receiveLoop; } catch { }
        if (_pingLoop != null) try { await _pingLoop; } catch { }
        if (_decodeThread.IsAlive) _decodeThread.Join(TimeSpan.FromSeconds(2));
        Clipboard?.Dispose();
        Files?.Dispose();
        await _ch.DisposeAsync();
        Frames.Dispose();
        _decodeQueue.Dispose();
    }
}
