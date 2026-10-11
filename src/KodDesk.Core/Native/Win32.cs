using System.Runtime.InteropServices;

namespace KodDesk.Core.Native;

internal static unsafe partial class Win32
{
    public const uint INPUT_MOUSE = 0;
    public const uint INPUT_KEYBOARD = 1;

    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    public const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    public const uint MOUSEEVENTF_XDOWN = 0x0080;
    public const uint MOUSEEVENTF_XUP = 0x0100;
    public const uint MOUSEEVENTF_WHEEL = 0x0800;
    public const uint MOUSEEVENTF_HWHEEL = 0x1000;
    public const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_SCANCODE = 0x0008;

    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint SendInput(uint nInputs, INPUT* pInputs, int cbSize);

    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetrics(int nIndex);

    [LibraryImport("winmm.dll")]
    public static partial uint timeBeginPeriod(uint uPeriod);

    [LibraryImport("winmm.dll")]
    public static partial uint timeEndPeriod(uint uPeriod);

    // Hook de tastatură (viewer)
    public const int WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP = 0x0105;
    public const uint LLKHF_EXTENDED = 0x01;
    public const uint LLKHF_INJECTED = 0x10;
    public const uint LLKHF_UP = 0x80;

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    [LibraryImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowsHookExW")]
    public static partial nint SetWindowsHookEx(int idHook, delegate* unmanaged<int, nint, nint, nint> lpfn, nint hMod, uint dwThreadId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWindowsHookEx(nint hhk);

    [LibraryImport("user32.dll")]
    public static partial nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetModuleHandle(string? lpModuleName);

    // ---- Desktop-uri (normal / Winlogon / UAC) ----
    public const uint MAXIMUM_ALLOWED = 0x02000000;
    public const int UOI_NAME = 2;

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint OpenInputDesktop(uint dwFlags, [MarshalAs(UnmanagedType.Bool)] bool fInherit, uint dwDesiredAccess);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetThreadDesktop(nint hDesktop);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint GetThreadDesktop(uint dwThreadId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseDesktop(nint hDesktop);

    [LibraryImport("user32.dll", SetLastError = true, EntryPoint = "GetUserObjectInformationW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetUserObjectInformation(nint hObj, int nIndex, void* pvInfo, uint nLength, out uint lpnLengthNeeded);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    // ---- Sesiuni, token-uri, procese (serviciu) ----
    public const uint INVALID_SESSION = 0xFFFFFFFF;
    public const uint TOKEN_ALL_ACCESS = 0xF01FF;
    public const int SecurityImpersonation = 2;
    public const int TokenPrimary = 1;
    public const int TokenSessionId = 12;
    public const uint CREATE_NO_WINDOW = 0x08000000;
    public const uint NORMAL_PRIORITY_CLASS = 0x00000020;

    [StructLayout(LayoutKind.Sequential)]
    public struct STARTUPINFOW
    {
        public int cb;
        public nint lpReserved;
        public nint lpDesktop;
        public nint lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public nint lpReserved2;
        public nint hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public nint hProcess;
        public nint hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [LibraryImport("kernel32.dll")]
    public static partial uint WTSGetActiveConsoleSessionId();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ProcessIdToSessionId(uint dwProcessId, out uint pSessionId);

    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint h);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DuplicateTokenEx(nint hExistingToken, uint dwDesiredAccess, nint lpTokenAttributes,
        int impersonationLevel, int tokenType, out nint phNewToken);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetTokenInformation(nint tokenHandle, int tokenInformationClass, void* tokenInformation, uint length);

    [LibraryImport("advapi32.dll", SetLastError = true, EntryPoint = "CreateProcessAsUserW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateProcessAsUser(nint hToken, string? lpApplicationName, char* lpCommandLine,
        nint lpProcessAttributes, nint lpThreadAttributes, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags, nint lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFOW lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetNamedPipeClientProcessId(nint pipe, out uint clientProcessId);

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WTSQueryUserToken(uint sessionId, out nint token);

    [LibraryImport("shell32.dll")]
    public static partial int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, nint hToken, out nint ppszPath);

    /// <summary>Simulează Ctrl+Alt+Del. Funcționează doar dintr-un serviciu (cu politica SoftwareSASGeneration).</summary>
    [LibraryImport("sas.dll")]
    public static partial void SendSAS([MarshalAs(UnmanagedType.Bool)] bool asUser);
}
