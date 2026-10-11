using KodDesk.Core.Native;
using KodDesk.Protocol;
using static KodDesk.Core.Native.Win32;

namespace KodDesk.Core.Input;

/// <summary>
/// Simulează mouse-ul și tastatura pe calculatorul host prin SendInput.
/// Ține evidența tastelor/butoanelor apăsate, ca să le poată elibera la deconectare.
/// Toate metodele trebuie apelate de pe același thread (thread-ul se mută pe desktopul activ, ex. ecranul de login).
/// </summary>
public sealed unsafe class InputInjector
{
    private readonly HashSet<(ushort scan, bool ext)> _keysDown = [];
    private readonly HashSet<MouseButtonKind> _buttonsDown = [];
    private readonly Lock _lock = new();

    /// <summary>Mută cursorul. x, y sunt normalizate 0..65535 în cadrul monitorului capturat.</summary>
    public void MouseMove(ushort x, ushort y, MonitorInfo monitor)
    {
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = Math.Max(GetSystemMetrics(SM_CXVIRTUALSCREEN), 2);
        int vh = Math.Max(GetSystemMetrics(SM_CYVIRTUALSCREEN), 2);

        double px = monitor.Left + x / 65535.0 * (monitor.Width - 1);
        double py = monitor.Top + y / 65535.0 * (monitor.Height - 1);
        int ax = (int)Math.Round((px - vx) * 65535.0 / (vw - 1));
        int ay = (int)Math.Round((py - vy) * 65535.0 / (vh - 1));

        SendMouse(ax, ay, 0, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK);
    }

    public void MouseButton(MouseButtonKind button, bool down)
    {
        lock (_lock)
        {
            if (down) _buttonsDown.Add(button); else _buttonsDown.Remove(button);
        }
        var (flags, data) = button switch
        {
            MouseButtonKind.Left => (down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP, 0u),
            MouseButtonKind.Right => (down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP, 0u),
            MouseButtonKind.Middle => (down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP, 0u),
            MouseButtonKind.X1 => (down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, 1u),
            MouseButtonKind.X2 => (down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, 2u),
            _ => (0u, 0u),
        };
        if (flags != 0) SendMouse(0, 0, data, flags);
    }

    public void MouseWheel(short delta, bool horizontal) =>
        SendMouse(0, 0, unchecked((uint)delta), horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL);

    public void Key(ushort scanCode, bool extended, bool down)
    {
        lock (_lock)
        {
            if (down) _keysDown.Add((scanCode, extended)); else _keysDown.Remove((scanCode, extended));
        }
        SendKey(scanCode, extended, down);
    }

    public void ReleaseAll()
    {
        (ushort scan, bool ext)[] keys;
        MouseButtonKind[] buttons;
        lock (_lock)
        {
            keys = [.. _keysDown];
            buttons = [.. _buttonsDown];
            _keysDown.Clear();
            _buttonsDown.Clear();
        }
        foreach (var (scan, ext) in keys) SendKey(scan, ext, false);
        foreach (var b in buttons) MouseButton(b, false);
    }

    private static void SendKey(ushort scan, bool extended, bool down)
    {
        var input = new INPUT { type = INPUT_KEYBOARD };
        input.u.ki.wScan = scan;
        input.u.ki.dwFlags = KEYEVENTF_SCANCODE | (extended ? KEYEVENTF_EXTENDEDKEY : 0) | (down ? 0 : KEYEVENTF_KEYUP);
        Send(ref input);
    }

    private static void SendMouse(int dx, int dy, uint data, uint flags)
    {
        var input = new INPUT { type = INPUT_MOUSE };
        input.u.mi.dx = dx;
        input.u.mi.dy = dy;
        input.u.mi.mouseData = data;
        input.u.mi.dwFlags = flags;
        Send(ref input);
    }

    [ThreadStatic] private static long t_lastDesktopSync;

    private static void Send(ref INPUT input)
    {
        // Verificăm periodic dacă desktopul activ s-a schimbat (login, blocare, UAC).
        long now = Environment.TickCount64;
        if (now - t_lastDesktopSync > 250)
        {
            t_lastDesktopSync = now;
            InputDesktop.SyncCurrentThread();
        }
        fixed (INPUT* p = &input)
        {
            if (SendInput(1, p, sizeof(INPUT)) != 0) return;
            // Probabil desktopul tocmai s-a schimbat: ne mutăm și reîncercăm o dată.
            if (InputDesktop.SyncCurrentThread() && SendInput(1, p, sizeof(INPUT)) != 0) return;
        }
        LogFailure();
    }

    private static long s_lastFailureLog;

    private static void LogFailure()
    {
        // Eșuează de obicei pe desktopul securizat (UAC, ecran de blocare) când nu rulăm ca serviciu.
        var now = Environment.TickCount64;
        if (now - s_lastFailureLog < 5000) return;
        s_lastFailureLog = now;
        Log.Warn($"SendInput a eșuat (eroare {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()})");
    }
}
