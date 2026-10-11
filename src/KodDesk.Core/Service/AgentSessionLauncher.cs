using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using KodDesk.Core.Host;
using static KodDesk.Core.Native.Win32;

namespace KodDesk.Core.Service;

/// <summary>
/// Folosit de serviciu: fiecare conexiune primită pornește un proces agent („KodDesk.exe --agent TOKEN”)
/// ca SYSTEM în sesiunea consolei (cea afișată pe monitor), unde poate captura ecranul — inclusiv
/// ecranul de login și UAC — și poate injecta input.
/// </summary>
public sealed class AgentSessionLauncher : ISessionLauncher
{
    private readonly Lock _lock = new();
    private readonly Dictionary<int, Process> _agents = [];
    private readonly HashSet<int> _authenticated = [];

    public event Action<int>? ActiveSessionsChanged;
    public event Action? AuthFailed;

    public static string StopEventName(int pid) => $@"Global\KodDesk.Agent.Stop.{pid}";

    public void Launch(string token)
    {
        uint session = WTSGetActiveConsoleSessionId();
        if (session == INVALID_SESSION)
        {
            Log.Warn("Nicio sesiune activă pe consolă; nu pot porni agentul");
            return;
        }

        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Calea executabilului necunoscută");
        int pid = StartInSession(session, $"\"{exe}\" --agent {token}");
        Log.Info($"Agent pornit: PID {pid}, sesiunea {session}");

        Process proc;
        try
        {
            proc = Process.GetProcessById(pid);
            proc.EnableRaisingEvents = true;
        }
        catch (ArgumentException)
        {
            return; // s-a terminat deja
        }

        lock (_lock) _agents[pid] = proc;
        proc.Exited += (_, _) => OnExited(pid);
        if (proc.HasExited) OnExited(pid);
    }

    public bool IsAgent(int pid)
    {
        lock (_lock) return _agents.ContainsKey(pid);
    }

    /// <summary>Agentul a autentificat viewer-ul: sesiunile mai vechi sunt închise.</summary>
    public void OnAgentAuthenticated(int pid)
    {
        int[] others;
        int count;
        lock (_lock)
        {
            if (!_agents.ContainsKey(pid)) return;
            others = _authenticated.Where(p => p != pid).ToArray();
            _authenticated.Add(pid);
            count = _authenticated.Count;
        }
        ActiveSessionsChanged?.Invoke(count);
        foreach (var p in others) StopAgent(p);
    }

    public void OnAgentAuthFailed(int pid)
    {
        if (IsAgent(pid)) AuthFailed?.Invoke();
    }

    public void DisconnectAll()
    {
        int[] pids;
        lock (_lock) pids = [.. _agents.Keys];
        foreach (var p in pids) StopAgent(p);
    }

    /// <summary>Oprește toți agenții și așteaptă închiderea lor (la oprirea serviciului / actualizare).</summary>
    public void StopAll(TimeSpan timeout)
    {
        Process[] procs;
        lock (_lock) procs = [.. _agents.Values];
        foreach (var p in procs) SignalStop(p.Id);
        var deadline = DateTime.UtcNow + timeout;
        foreach (var p in procs)
        {
            try
            {
                var left = deadline - DateTime.UtcNow;
                if (left > TimeSpan.Zero && p.WaitForExit(left)) continue;
                if (!p.HasExited)
                {
                    Log.Warn($"Agentul {p.Id} nu s-a oprit; îl închid forțat");
                    p.Kill();
                    p.WaitForExit(2000);
                }
            }
            catch { }
        }
    }

    private static void SignalStop(int pid)
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(StopEventName(pid), out var ev))
                using (ev) ev.Set();
        }
        catch (Exception ex)
        {
            Log.Warn($"Nu pot semnala agentul {pid}", ex);
        }
    }

    private void OnExited(int pid)
    {
        bool wasActive;
        int count;
        lock (_lock)
        {
            if (_agents.Remove(pid, out var proc)) proc.Dispose();
            wasActive = _authenticated.Remove(pid);
            count = _authenticated.Count;
        }
        Log.Info($"Agentul {pid} s-a închis");
        if (wasActive) ActiveSessionsChanged?.Invoke(count);
    }

    /// <summary>Cere agentului să se oprească ordonat (eliberează tastele apăsate); dacă nu răspunde, îl termină.</summary>
    private void StopAgent(int pid)
    {
        Process? proc;
        lock (_lock) _agents.TryGetValue(pid, out proc);
        if (proc == null) return;

        SignalStop(pid);

        _ = Task.Run(async () =>
        {
            await Task.Delay(3000);
            try
            {
                if (!proc.HasExited)
                {
                    Log.Warn($"Agentul {pid} nu s-a oprit; îl închid forțat");
                    proc.Kill();
                }
            }
            catch { }
        });
    }

    private static unsafe int StartInSession(uint sessionId, string commandLine)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ALL_ACCESS, out var token))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "OpenProcessToken");
        try
        {
            if (!DuplicateTokenEx(token, MAXIMUM_ALLOWED, 0, SecurityImpersonation, TokenPrimary, out var dup))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "DuplicateTokenEx");
            try
            {
                uint sid = sessionId;
                if (!SetTokenInformation(dup, TokenSessionId, &sid, sizeof(uint)))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetTokenInformation");

                var desktop = Marshal.StringToHGlobalUni(@"winsta0\default");
                try
                {
                    var si = new STARTUPINFOW { cb = sizeof(STARTUPINFOW), lpDesktop = desktop };
                    var cmd = (commandLine + "\0").ToCharArray();
                    fixed (char* pCmd = cmd)
                    {
                        if (!CreateProcessAsUser(dup, null, pCmd, 0, 0, false, CREATE_NO_WINDOW | NORMAL_PRIORITY_CLASS,
                                0, AppContext.BaseDirectory, ref si, out var pi))
                            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateProcessAsUser");
                        CloseHandle(pi.hThread);
                        CloseHandle(pi.hProcess);
                        return pi.dwProcessId;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(desktop);
                }
            }
            finally
            {
                CloseHandle(dup);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }
}
