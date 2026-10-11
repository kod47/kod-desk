using KodDesk.Core;
using KodDesk.Core.Host;
using KodDesk.Core.Service;

namespace KodDesk.App;

/// <summary>Starea „acestui calculator” afișată în fereastra principală.</summary>
public interface IHostStatusSource : IDisposable
{
    HostStatus Status { get; }
    string? Message { get; }
    string? HostId { get; }
    bool HasPassword { get; }
    int ActiveSessions { get; }

    /// <summary>Se declanșează pe un thread oarecare.</summary>
    event Action? Changed;

    void Start();
    void Disconnect();
}

/// <summary>Fără serviciu: aplicația însăși ține calculatorul online și rulează sesiunile.</summary>
public sealed class LocalHostStatus : IHostStatusSource
{
    private HostAgent? _agent;
    private string? _error;

    public HostSettings? Settings { get; private set; }

    public HostStatus Status => _error != null ? HostStatus.Error : _agent?.Status ?? HostStatus.Connecting;
    public string? Message => _error ?? _agent?.StatusMessage;
    public string? HostId => Settings?.HostId;
    public bool HasPassword => Settings?.HasPassword ?? false;
    public int ActiveSessions => _agent?.ActiveSessions ?? 0;

    public event Action? Changed;

    public void Start()
    {
        try
        {
            Settings = HostSettings.Load();
        }
        catch (Exception ex)
        {
            _error = "Nu pot citi setările calculatorului: " + ex.Message;
            Changed?.Invoke();
            return;
        }
        _agent = new HostAgent(() => Settings!, new InProcessSessionLauncher(() => Settings!));
        _agent.StatusChanged += () => Changed?.Invoke();
        _agent.ActiveSessionsChanged += _ => Changed?.Invoke();
        _agent.Start();
    }

    /// <summary>Setările s-au schimbat (relay, parolă): reconectare.</summary>
    public void Apply(HostSettings settings)
    {
        Settings = settings;
        _agent?.Reconnect();
        Changed?.Invoke();
    }

    public void Disconnect() => _agent?.DisconnectActive();

    public void Dispose()
    {
        try { _agent?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)); } catch { }
        _agent = null;
    }
}

/// <summary>Cu serviciu instalat: starea se citește periodic de la serviciu.</summary>
public sealed class ServiceHostStatus : IHostStatusSource
{
    private Timer? _timer;
    private int _polling;

    public HostStatus Status { get; private set; } = HostStatus.Connecting;
    public string? Message { get; private set; }
    public string? HostId { get; private set; }
    public bool HasPassword { get; private set; }
    public int ActiveSessions { get; private set; }

    public event Action? Changed;

    public void Start() => _timer = new Timer(_ => _ = PollAsync(), null, 0, 2000);

    public void Refresh() => _ = PollAsync();

    private async Task PollAsync()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;
        try
        {
            var resp = await ControlPipe.SendAsync(ControlPipe.CmdStatus);
            if (resp?.Ok == true)
            {
                Status = Enum.TryParse<HostStatus>(resp.Status, out var st) ? st : HostStatus.Error;
                Message = resp.Message;
                HostId = resp.HostId;
                HasPassword = resp.HasPassword;
                ActiveSessions = resp.Active;
            }
            else
            {
                Status = HostStatus.Error;
                Message = ServiceManager.IsRunning()
                    ? "Serviciul KodDesk nu răspunde"
                    : "Serviciul KodDesk este oprit";
                ActiveSessions = 0;
            }
            Changed?.Invoke();
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    public void Disconnect() => _ = ControlPipe.SendAsync(ControlPipe.CmdDisconnect);

    public void Dispose() => _timer?.Dispose();
}
