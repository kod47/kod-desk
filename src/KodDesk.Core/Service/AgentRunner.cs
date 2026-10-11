using KodDesk.Core.Host;
using static KodDesk.Core.Native.Win32;

namespace KodDesk.Core.Service;

/// <summary>
/// Procesul agent („KodDesk.exe --agent TOKEN”), pornit de serviciu ca SYSTEM în sesiunea consolei.
/// Rulează o singură sesiune, apoi se închide.
/// </summary>
public static class AgentRunner
{
    public static int Run(string token)
    {
        Log.Init("agent", machineWide: true);
        // Explorer-ul utilizatorului trebuie să poată citi fișierele virtuale din clipboard (obiecte COM ale agentului SYSTEM).
        int hr = Native.Win32Clipboard.CoInitializeSecurity(0, -1, 0, 0, 0 /* DEFAULT */, 2 /* IDENTIFY */, 0, 0, 0);
        if (hr != 0) Log.Warn($"CoInitializeSecurity: 0x{hr:X8}");
        try
        {
            var settings = HostSettings.Load();
            var session = new HostSession(settings, token);

            session.Authenticated += _ => Notify(ControlPipe.CmdAgentAuth);
            session.SecureAttentionRequested += () => Notify(ControlPipe.CmdSas);

            // Serviciul ne poate cere oprirea (o sesiune nouă o înlocuiește pe aceasta, sau „Deconectează”).
            using var stop = new EventWaitHandle(false, EventResetMode.ManualReset,
                AgentSessionLauncher.StopEventName(Environment.ProcessId));
            ThreadPool.RegisterWaitForSingleObject(stop, (_, _) =>
            {
                Log.Info("Oprire cerută de serviciu");
                session.Stop();
            }, null, Timeout.Infinite, true);

            // Dacă se schimbă sesiunea afișată pe monitor (alt utilizator, deconectare), sesiunea noastră nu mai vede ecranul.
            ProcessIdToSessionId((uint)Environment.ProcessId, out var mySession);
            using var watch = new Timer(_ =>
            {
                var active = WTSGetActiveConsoleSessionId();
                if (active != INVALID_SESSION && active != mySession)
                {
                    Log.Info($"Sesiunea consolei s-a schimbat ({mySession} -> {active}); închid");
                    session.Stop();
                }
            }, null, 2000, 2000);

            var outcome = session.RunAsync(CancellationToken.None).GetAwaiter().GetResult();
            if (outcome == SessionOutcome.AuthFailed) Notify(ControlPipe.CmdAgentAuthFail);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("Agentul s-a oprit cu eroare", ex);
            return 1;
        }
    }

    private static void Notify(string cmd)
    {
        var resp = ControlPipe.SendAsync(cmd).GetAwaiter().GetResult();
        if (resp?.Ok != true) Log.Warn($"Serviciul nu a confirmat „{cmd}”: {resp?.Error ?? "fără răspuns"}");
    }
}
