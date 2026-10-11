using System.ServiceProcess;
using KodDesk.Core.Host;
using KodDesk.Core.Native;

namespace KodDesk.Core.Service;

/// <summary>
/// Serviciul Windows „KodDesk” (rulează ca SYSTEM, pornește automat cu Windows).
/// Ține calculatorul online pe relay și pornește câte un agent pentru fiecare conexiune.
/// </summary>
public sealed class KodDeskService : ServiceBase
{
    public const string Name = "KodDesk";

    private AgentSessionLauncher? _launcher;
    private HostAgent? _agent;
    private ControlPipeServer? _pipe;
    private HostSettings? _settings;

    public KodDeskService()
    {
        ServiceName = Name;
        CanStop = true;
        CanShutdown = true;
        CanHandleSessionChangeEvent = true;
    }

    protected override void OnStart(string[] args)
    {
        Log.Init("service", machineWide: true);
        Log.Info("Serviciul pornește, versiunea " + typeof(KodDeskService).Assembly.GetName().Version);
        try
        {
            ServiceManager.LockDownDataDir();
        }
        catch (Exception ex)
        {
            Log.Error("Nu pot restricționa accesul la folderul de setări", ex);
        }

        _launcher = new AgentSessionLauncher();
        _agent = new HostAgent(() => _settings = HostSettings.Load(), _launcher);
        _agent.StatusChanged += () => Log.Info($"Stare: {_agent.Status} {_agent.StatusMessage}");
        _pipe = new ControlPipeServer(HandleRequest);
        _pipe.Start();
        _agent.Start();
    }

    protected override void OnStop()
    {
        Log.Info("Serviciul se oprește");
        _pipe?.Dispose();
        // Așteptăm agenții, ca la actualizare installer-ul să poată înlocui fișierele.
        _launcher?.StopAll(TimeSpan.FromSeconds(5));
        try { _agent?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)); } catch { }
    }

    protected override void OnShutdown() => OnStop();

    protected override void OnSessionChange(SessionChangeDescription changeDescription) =>
        Log.Info($"Schimbare de sesiune: {changeDescription.Reason} (sesiunea {changeDescription.SessionId})");

    private ControlResponse HandleRequest(ControlRequest req, int clientPid)
    {
        var agent = _agent!;
        var launcher = _launcher!;
        switch (req.Cmd)
        {
            case ControlPipe.CmdStatus:
            {
                var s = _settings;
                return new ControlResponse
                {
                    Ok = true,
                    Status = agent.Status.ToString(),
                    Message = agent.StatusMessage,
                    HostId = s?.HostId,
                    HasPassword = s?.HasPassword ?? false,
                    Relay = s?.ActiveRelayAddress,
                    Active = agent.ActiveSessions,
                };
            }

            case ControlPipe.CmdDisconnect:
                Log.Info($"Deconectare cerută local (PID {clientPid})");
                agent.DisconnectActive();
                return new ControlResponse { Ok = true };

            case ControlPipe.CmdAgentAuth:
                launcher.OnAgentAuthenticated(clientPid);
                return new ControlResponse { Ok = launcher.IsAgent(clientPid) };

            case ControlPipe.CmdAgentAuthFail:
                launcher.OnAgentAuthFailed(clientPid);
                return new ControlResponse { Ok = true };

            case ControlPipe.CmdSas:
                if (!launcher.IsAgent(clientPid))
                    return new ControlResponse { Ok = false, Error = "Permis doar agenților" };
                Log.Info("Trimit Ctrl+Alt+Del");
                Win32.SendSAS(false);
                return new ControlResponse { Ok = true };

            default:
                return new ControlResponse { Ok = false, Error = "Comandă necunoscută: " + req.Cmd };
        }
    }
}
