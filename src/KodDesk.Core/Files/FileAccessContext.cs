using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using KodDesk.Core.Native;

namespace KodDesk.Core.Files;

/// <summary>
/// Identitatea cu care se accesează fișierele. Agentul rulează ca SYSTEM, dar fișierele se citesc și se scriu
/// în numele utilizatorului logat pe consolă: fișierele primite îi aparțin lui, iar exploratorul la distanță
/// nu vede mai mult decât vede utilizatorul.
/// </summary>
public interface IFileAccessContext
{
    /// <summary>Rulează o operație (deschidere/creare fișier, listare) cu identitatea utilizatorului.</summary>
    T Run<T>(Func<T> operation);

    /// <summary>Calea unui folder cunoscut al utilizatorului (Desktop, Descărcări...), sau null.</summary>
    string? KnownFolder(Guid folderId);
}

public static class KnownFolders
{
    public static readonly Guid Desktop = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
    public static readonly Guid Downloads = new("374DE290-123F-4565-9164-39C4925E467B");
    public static readonly Guid Documents = new("FDD39AD0-238F-46AF-ADB4-6C85480369C7");
}

public static class FileAccessContexts
{
    /// <summary>Contextul potrivit pentru procesul curent: utilizatorul consolei dacă rulăm ca SYSTEM, altfel utilizatorul curent.</summary>
    public static IFileAccessContext ForThisProcess()
    {
        using var me = WindowsIdentity.GetCurrent();
        return me.IsSystem ? new ConsoleUserContext() : new CurrentUserContext();
    }
}

/// <summary>Procesul rulează deja ca utilizatorul (viewer-ul, sau aplicația fără serviciu).</summary>
public sealed class CurrentUserContext : IFileAccessContext
{
    public T Run<T>(Func<T> operation) => operation();

    public string? KnownFolder(Guid folderId) => FolderPath.Get(folderId, 0);
}

/// <summary>Agentul (SYSTEM): impersonează utilizatorul logat în sesiunea agentului.</summary>
public sealed class ConsoleUserContext : IFileAccessContext
{
    private readonly Lock _lock = new();
    private SafeAccessTokenHandle? _token;

    private SafeAccessTokenHandle Token()
    {
        lock (_lock)
        {
            if (_token is { IsInvalid: false }) return _token;
            Win32.ProcessIdToSessionId((uint)Environment.ProcessId, out var session);
            if (!Win32.WTSQueryUserToken(session, out var raw))
                throw new InvalidOperationException("Niciun utilizator nu este autentificat pe calculatorul la distanță.");
            _token = new SafeAccessTokenHandle(raw);
            return _token;
        }
    }

    public T Run<T>(Func<T> operation) => WindowsIdentity.RunImpersonated(Token(), operation);

    public string? KnownFolder(Guid folderId)
    {
        try { return FolderPath.Get(folderId, Token().DangerousGetHandle()); }
        catch (InvalidOperationException) { return null; }
    }
}

internal static class FolderPath
{
    public static string? Get(Guid id, nint token)
    {
        if (Win32.SHGetKnownFolderPath(ref id, 0, token, out var p) != 0) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }
}
