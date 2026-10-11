using static KodDesk.Core.Native.Win32;

namespace KodDesk.Core.Native;

/// <summary>
/// Atașează thread-ul curent la desktopul care primește acum input-ul: „Default” (sesiunea normală),
/// „Winlogon” (ecranul de login / blocare, UAC securizat) etc. Funcționează complet doar când procesul
/// rulează ca SYSTEM (agentul pornit de serviciu); pentru un utilizator obișnuit, desktopul Winlogon
/// rămâne inaccesibil și thread-ul rămâne pe desktopul curent.
/// </summary>
public static unsafe class InputDesktop
{
    [ThreadStatic] private static nint t_attached;

    /// <summary>Returnează true dacă thread-ul a fost mutat pe alt desktop.</summary>
    public static bool SyncCurrentThread()
    {
        var input = OpenInputDesktop(0, false, MAXIMUM_ALLOWED);
        if (input == 0) return false;

        var inputName = GetName(input);
        var currentName = GetName(GetThreadDesktop(GetCurrentThreadId()));
        if (inputName == null || inputName == currentName || !SetThreadDesktop(input))
        {
            CloseDesktop(input);
            return false;
        }

        if (t_attached != 0) CloseDesktop(t_attached);
        t_attached = input;
        Log.Info($"Thread {GetCurrentThreadId()} mutat pe desktopul „{inputName}”");
        return true;
    }

    public static string? CurrentInputDesktopName()
    {
        var input = OpenInputDesktop(0, false, MAXIMUM_ALLOWED);
        if (input == 0) return null;
        try { return GetName(input); }
        finally { CloseDesktop(input); }
    }

    private static string? GetName(nint desktop)
    {
        if (desktop == 0) return null;
        char* buf = stackalloc char[128];
        if (!GetUserObjectInformation(desktop, UOI_NAME, buf, 256, out _)) return null;
        return new string(buf);
    }
}
