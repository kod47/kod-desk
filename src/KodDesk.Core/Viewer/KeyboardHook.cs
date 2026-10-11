using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static KodDesk.Core.Native.Win32;

namespace KodDesk.Core.Viewer;

/// <summary>
/// Hook global de tastatură (WH_KEYBOARD_LL), activ doar cât fereastra viewer-ului are focus.
/// Permite trimiterea combinațiilor pe care Windows le-ar intercepta local (Win, Alt+Tab, Alt+F4...).
/// Trebuie instalat pe un thread cu buclă de mesaje (thread-ul UI).
/// </summary>
public sealed unsafe class KeyboardHook : IDisposable
{
    /// <summary>(scancode, extins, apăsat). Tasta este consumată local.</summary>
    public delegate void KeyHandler(ushort scanCode, bool extended, bool down);

    private static KeyboardHook? s_active;
    private readonly KeyHandler _handler;
    private nint _hook;

    public KeyboardHook(KeyHandler handler) => _handler = handler;

    public bool IsInstalled => _hook != 0;

    public void Install()
    {
        if (_hook != 0) return;
        s_active?.Uninstall();
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, &HookProc, GetModuleHandle(null), 0);
        if (_hook == 0)
            Log.Warn($"SetWindowsHookEx a eșuat ({Marshal.GetLastPInvokeError()})");
        else
            s_active = this;
    }

    public void Uninstall()
    {
        if (_hook == 0) return;
        UnhookWindowsHookEx(_hook);
        _hook = 0;
        if (s_active == this) s_active = null;
    }

    [UnmanagedCallersOnly]
    private static nint HookProc(int nCode, nint wParam, nint lParam)
    {
        var self = s_active;
        if (nCode >= 0 && self != null)
        {
            var k = (KBDLLHOOKSTRUCT*)lParam;
            // Ignorăm evenimentele injectate (ex. de alte programe sau de noi înșine, la testare pe același PC).
            if ((k->flags & LLKHF_INJECTED) == 0)
            {
                bool down = (k->flags & LLKHF_UP) == 0;
                bool extended = (k->flags & LLKHF_EXTENDED) != 0;
                ushort scan = (ushort)k->scanCode;
                try { self._handler(scan, extended, down); }
                catch (Exception ex) { Log.Warn("Eroare în hook-ul de tastatură", ex); }
                return 1;
            }
        }
        return CallNextHookEx(0, nCode, wParam, lParam);
    }

    public void Dispose() => Uninstall();
}
