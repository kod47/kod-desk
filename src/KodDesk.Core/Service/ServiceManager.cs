using System.ComponentModel;
using System.Diagnostics;
using System.ServiceProcess;

namespace KodDesk.Core.Service;

/// <summary>
/// Operații asupra serviciului KodDesk. Serviciul în sine este instalat de installer-ul MSI.
/// </summary>
public static class ServiceManager
{
    public static bool IsInstalled()
    {
        try
        {
            using var sc = new ServiceController(KodDeskService.Name);
            _ = sc.Status;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public static bool IsRunning()
    {
        try
        {
            using var sc = new ServiceController(KodDeskService.Name);
            return sc.Status == ServiceControllerStatus.Running;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Pornește aplicația cu drepturi de administrator. Returnează false dacă utilizatorul a refuzat UAC.</summary>
    public static bool RunElevated(string arguments, out int exitCode)
    {
        exitCode = -1;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
            })!;
            p.WaitForExit();
            exitCode = p.ExitCode;
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            return false;
        }
    }

    /// <summary>Repornește serviciul (necesită drepturi de administrator).</summary>
    public static void Restart()
    {
        using var sc = new ServiceController(KodDeskService.Name);
        if (sc.Status != ServiceControllerStatus.Stopped)
        {
            if (sc.Status != ServiceControllerStatus.StopPending) sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
        }
        sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Restricționează %ProgramData%\KodDesk la SYSTEM și Administratori (cheia parolei, secretul relay).
    /// Apelat de serviciu la fiecare pornire; operația este idempotentă.
    /// </summary>
    public static void LockDownDataDir()
    {
        var dir = AppPaths.DataDir;
        Directory.CreateDirectory(dir);
        Icacls(dir, "/setowner", "*S-1-5-32-544", "/T", "/C", "/Q");
        Icacls(dir, "/inheritance:r", "/grant:r", "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F", "/C", "/Q");
        if (Directory.EnumerateFileSystemEntries(dir).Any())
            Icacls(Path.Combine(dir, "*"), "/reset", "/T", "/C", "/Q");
    }

    private static void Icacls(string path, params string[] args)
    {
        var psi = new ProcessStartInfo("icacls.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(path);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"icacls {path} {string.Join(' ', args)} a eșuat ({p.ExitCode}): {output.Trim()}");
    }
}
