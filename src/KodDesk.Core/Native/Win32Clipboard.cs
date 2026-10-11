using System.Runtime.InteropServices;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace KodDesk.Core.Native;

/// <summary>Ferestre „message-only”, clipboard Win32 și OLE.</summary>
internal static unsafe partial class Win32Clipboard
{
    public const uint WM_CLIPBOARDUPDATE = 0x031D;
    public const uint WM_APP = 0x8000;
    public static readonly nint HWND_MESSAGE = -3;

    public const uint CF_DIB = 8;
    public const uint CF_UNICODETEXT = 13;
    public const uint CF_HDROP = 15;

    public const uint GMEM_MOVEABLE = 0x0002;
    public const uint GMEM_ZEROINIT = 0x0040;

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public delegate* unmanaged<nint, uint, nint, nint, nint> lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public nint lpszMenuName;
        public nint lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int ptX, ptY;
        public uint lPrivate;
    }

    [LibraryImport("user32.dll", SetLastError = true, EntryPoint = "RegisterClassExW")]
    public static partial ushort RegisterClassEx(ref WNDCLASSEXW wc);

    [LibraryImport("user32.dll", SetLastError = true, EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowEx(uint exStyle, string className, string? windowName, uint style,
        int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    public static partial nint DefWindowProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    public static partial int GetMessage(out MSG msg, nint hwnd, uint min, uint max);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(ref MSG msg);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static partial nint DispatchMessage(ref MSG msg);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    public static partial void PostQuitMessage(int exitCode);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AddClipboardFormatListener(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RemoveClipboardFormatListener(nint hwnd);

    [LibraryImport("user32.dll")]
    public static partial uint GetClipboardSequenceNumber();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenClipboard(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseClipboard();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EmptyClipboard();

    [LibraryImport("user32.dll")]
    public static partial nint GetClipboardData(uint format);

    [LibraryImport("user32.dll")]
    public static partial nint SetClipboardData(uint format, nint hMem);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsClipboardFormatAvailable(uint format);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterClipboardFormat(string name);

    [LibraryImport("kernel32.dll")]
    public static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll")]
    public static partial nint GlobalLock(nint hMem);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalUnlock(nint hMem);

    [LibraryImport("kernel32.dll")]
    public static partial nuint GlobalSize(nint hMem);

    [LibraryImport("kernel32.dll")]
    public static partial nint GlobalFree(nint hMem);

    [LibraryImport("shell32.dll", EntryPoint = "DragQueryFileW")]
    public static partial uint DragQueryFile(nint hDrop, uint index, char* buffer, uint cch);

    [LibraryImport("ole32.dll")]
    public static partial int OleInitialize(nint reserved);

    [LibraryImport("ole32.dll")]
    public static partial void OleUninitialize();

    // Interfețele COM necesită marshalling clasic (DllImport).
    [DllImport("ole32.dll")]
    public static extern int OleSetClipboard(ComTypes.IDataObject? dataObject);

    [DllImport("ole32.dll")]
    public static extern int OleIsCurrentClipboard(ComTypes.IDataObject dataObject);

    [DllImport("ole32.dll")]
    public static extern void ReleaseStgMedium(ref ComTypes.STGMEDIUM medium);

    /// <summary>
    /// Fără descriptor de securitate: COM nu verifică apelanții. Necesar în agent (SYSTEM), ca Explorer-ul
    /// utilizatorului să poată citi fișierele virtuale puse în clipboard.
    /// </summary>
    [LibraryImport("ole32.dll")]
    public static partial int CoInitializeSecurity(nint pSecDesc, int cAuthSvc, nint asAuthSvc, nint pReserved1,
        uint dwAuthnLevel, uint dwImpLevel, nint pAuthList, uint dwCapabilities, nint pReserved3);
}
