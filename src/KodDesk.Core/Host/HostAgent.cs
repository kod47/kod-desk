using System.Net.WebSockets;
using KodDesk.Protocol;

namespace KodDesk.Core.Host;

public enum HostStatus
{
    NotConfigured,
    Connecting,
    Online,
    Error,
}

/// <summary>Pornește sesiunile anunțate de relay (în același proces sau într-un proces agent).</summary>
public interface ISessionLauncher
{
    /// <summary>Numărul de sesiuni autentificate active s-a schimbat.</summary>
    event Action<int>? ActiveSessionsChanged;

    /// <summary>O încercare de conectare a eșuat la autentificare (parolă greșită).</summary>
    event Action? AuthFailed;

    void Launch(string token);

    /// <summary>Închide toate sesiunile active.</summary>
    void DisconnectAll();
}

/// <summary>
/// Menține conexiunea de control cu relay-ul (calculatorul apare „online” cu ID-ul său)
/// și transmite fiecare conexiune primită către <see cref="ISessionLauncher"/>.
/// După prea multe parole greșite, conexiunile sunt ignorate temporar.
/// </summary>
public sealed class HostAgent : IAsyncDisposable
{
    private const int MaxFailuresPerWindow = 5;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(5);

    private readonly Func<HostSettings> _settingsProvider;
    private readonly ISessionLauncher _launcher;
    private readonly CancellationTokenSource _cts = new();
    private readonly Queue<DateTime> _authFailures = new();
    private readonly Lock _lock = new();
    private CancellationTokenSource? _connectionCts;
    private Task? _loop;

    public HostAgent(Func<HostSettings> settingsProvider, ISessionLauncher launcher)
    {
        _settingsProvider = settingsProvider;
        _launcher = launcher;
        _launcher.AuthFailed += () =>
        {
            lock (_lock) _authFailures.Enqueue(DateTime.UtcNow);
        };
        _launcher.ActiveSessionsChanged += n =>
        {
            ActiveSessions = n;
            ActiveSessionsChanged?.Invoke(n);
        };
    }

    public HostStatus Status { get; private set; } = HostStatus.Connecting;
    public string? StatusMessage { get; private set; }
    public int ActiveSessions { get; private set; }

    public event Action? StatusChanged;

    /// <summary>Numărul de sesiuni active (cineva controlează acest calculator).</summary>
    public event Action<int>? ActiveSessionsChanged;

    public void Start() => _loop ??= Task.Run(RunAsync);

    /// <summary>Reconectare imediată (ex. după schimbarea setărilor relay-ului).</summary>
    public void Reconnect()
    {
        lock (_lock) _connectionCts?.Cancel();
    }

    public void DisconnectActive() => _launcher.DisconnectAll();

    private void SetStatus(HostStatus status, string? message = null)
    {
        Status = status;
        StatusMessage = message;
        StatusChanged?.Invoke();
    }

    private async Task RunAsync()
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!_cts.IsCancellationRequested)
        {
            using var connCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            lock (_lock) _connectionCts = connCts;

            HostSettings settings;
            try
            {
                settings = _settingsProvider();
            }
            catch (Exception ex)
            {
                SetStatus(HostStatus.Error, "Nu pot citi setările: " + ex.Message);
                try { await Task.Delay(TimeSpan.FromSeconds(10), connCts.Token); } catch (OperationCanceledException) { }
                continue;
            }

            if (!settings.IsRelayConfigured)
            {
                SetStatus(HostStatus.NotConfigured, "Serverul relay nu este configurat");
                try { await Task.Delay(Timeout.Infinite, connCts.Token); } catch (OperationCanceledException) { }
                continue;
            }

            SetStatus(HostStatus.Connecting);
            try
            {
                await RunConnectionAsync(settings, connCts.Token, () => backoff = TimeSpan.FromSeconds(1));
                SetStatus(HostStatus.Connecting, "Conexiunea cu relay-ul s-a închis");
            }
            catch (OperationCanceledException) when (connCts.IsCancellationRequested)
            {
                if (_cts.IsCancellationRequested) break;
                backoff = TimeSpan.Zero; // reconectare cerută explicit
            }
            catch (Exception ex)
            {
                Log.Warn("Conexiune relay eșuată", ex);
                SetStatus(HostStatus.Error, ex.Message);
            }

            try { await Task.Delay(backoff, _cts.Token); } catch (OperationCanceledException) { break; }
            backoff = TimeSpan.FromSeconds(Math.Clamp(backoff.TotalSeconds * 2, 1, 30));
        }
    }

    private async Task RunConnectionAsync(HostSettings settings, CancellationToken ct, Action onRegistered)
    {
        using var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        ws.Options.KeepAliveTimeout = TimeSpan.FromSeconds(45);
        await ws.ConnectAsync(RelayUrls.Host(settings.ActiveRelayAddress, settings.ActiveRelayKey), ct);

        await WebSocketText.SendAsync(ws, new RelayMessage
        {
            Type = RelayMessage.Register,
            Id = settings.HostId,
            Secret = settings.RelaySecret,
        }, ct);

        var reply = await WebSocketText.ReceiveAsync(ws, ct) ?? throw new IOException("Relay-ul a închis conexiunea");
        if (reply.Type == RelayMessage.Error) throw new InvalidOperationException(reply.Message ?? "Eroare relay");
        if (reply.Type != RelayMessage.Registered) throw new InvalidDataException("Răspuns neașteptat de la relay: " + reply.Type);

        Log.Info($"Online pe relay {settings.ActiveRelayAddress} cu ID {settings.HostId}");
        SetStatus(HostStatus.Online);
        onRegistered();

        while (!ct.IsCancellationRequested)
        {
            var msg = await WebSocketText.ReceiveAsync(ws, ct);
            if (msg == null) return;
            if (msg.Type == RelayMessage.Incoming && !string.IsNullOrEmpty(msg.Session))
                OnIncoming(msg.Session);
        }
    }

    private void OnIncoming(string token)
    {
        lock (_lock)
        {
            while (_authFailures.Count > 0 && DateTime.UtcNow - _authFailures.Peek() > FailureWindow)
                _authFailures.Dequeue();
            if (_authFailures.Count >= MaxFailuresPerWindow)
            {
                Log.Warn("Prea multe încercări eșuate; ignor conexiunea");
                return;
            }
        }
        try
        {
            _launcher.Launch(token);
        }
        catch (Exception ex)
        {
            Log.Error("Nu pot porni sesiunea", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _launcher.DisconnectAll();
        if (_loop != null)
        {
            try { await _loop; } catch { }
        }
        _cts.Dispose();
    }
}

/// <summary>Rulează sesiunile în procesul curent (aplicația pornită fără serviciu).</summary>
public sealed class InProcessSessionLauncher(Func<HostSettings> settingsProvider) : ISessionLauncher
{
    private readonly Lock _lock = new();
    private HostSession? _active;
    private int _activeCount;

    public event Action<int>? ActiveSessionsChanged;
    public event Action? AuthFailed;

    public void Launch(string token)
    {
        var session = new HostSession(settingsProvider(), token);
        bool counted = false;
        session.Authenticated += s =>
        {
            HostSession? previous;
            lock (_lock)
            {
                previous = _active;
                _active = s;
            }
            previous?.Stop();
            counted = true;
            ActiveSessionsChanged?.Invoke(Interlocked.Increment(ref _activeCount));
        };
        session.SecureAttentionRequested += () =>
            Log.Warn("Ctrl+Alt+Del este disponibil doar când KodDesk este instalat ca serviciu");

        _ = Task.Run(async () =>
        {
            try
            {
                var outcome = await session.RunAsync(CancellationToken.None);
                if (outcome == SessionOutcome.AuthFailed) AuthFailed?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Error("Sesiune terminată cu eroare", ex);
            }
            finally
            {
                lock (_lock)
                    if (_active == session) _active = null;
                if (counted) ActiveSessionsChanged?.Invoke(Interlocked.Decrement(ref _activeCount));
            }
        });
    }

    public void DisconnectAll()
    {
        lock (_lock) _active?.Stop();
    }
}
