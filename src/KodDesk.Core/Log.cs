using System.Text;

namespace KodDesk.Core;

/// <summary>
/// Jurnal simplu în fișier, câte unul pe zi:
///   aplicația (utilizator):  %LocalAppData%\KodDesk\logs
///   serviciul și agentul:    %ProgramData%\KodDesk\logs
/// </summary>
public static class Log
{
    private static readonly Lock s_lock = new();
    private static string? s_dir;
    private static string s_tag = "app";

    public static string? Directory => s_dir;

    public static void Init(string tag, bool machineWide)
    {
        s_tag = tag;
        try
        {
            s_dir = Path.Combine(machineWide ? AppPaths.DataDir : AppPaths.UserDataDir, "logs");
            System.IO.Directory.CreateDirectory(s_dir);
            foreach (var f in System.IO.Directory.GetFiles(s_dir, "*.log"))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-14)) File.Delete(f);
        }
        catch
        {
            s_dir = null;
        }
    }

    public static void Info(string msg) => Write("INF", msg, null);
    public static void Warn(string msg, Exception? ex = null) => Write("WRN", msg, ex);
    public static void Error(string msg, Exception? ex = null) => Write("ERR", msg, ex);

    private static void Write(string level, string msg, Exception? ex)
    {
        var line = new StringBuilder()
            .Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append(' ')
            .Append(level).Append(" [").Append(s_tag).Append(':').Append(Environment.ProcessId).Append("] ")
            .Append(msg);
        if (ex != null) line.Append(" | ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
        System.Diagnostics.Debug.WriteLine(line);
        if (s_dir == null) return;
        try
        {
            lock (s_lock)
                File.AppendAllText(Path.Combine(s_dir, $"kod-desk-{DateTime.Now:yyyyMMdd}.log"), line.Append(Environment.NewLine).ToString());
        }
        catch { }
    }
}

public static class AppPaths
{
    /// <summary>Folder comun: setările calculatorului controlat și jurnalele serviciului.</summary>
    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "KodDesk");

    /// <summary>Folder per utilizator: setările de conectare, parolele memorate, jurnalele aplicației.</summary>
    public static string UserDataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KodDesk");

    /// <summary>Unde se instalează aplicația când devine serviciu.</summary>
    public static string InstallDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "KodDesk");
}
