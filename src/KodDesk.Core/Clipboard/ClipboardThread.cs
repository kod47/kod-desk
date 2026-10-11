using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using static KodDesk.Core.Native.Win32Clipboard;

namespace KodDesk.Core.Clipboard;

/// <summary>
/// Thread STA cu o fereastră „message-only” care primește notificările de schimbare a clipboard-ului
/// și pe care se execută toate operațiile cu clipboard-ul (Win32 și OLE).
/// </summary>
internal sealed unsafe class ClipboardThread : IDisposable
{
    private const string ClassName = "KodDesk.ClipboardWindow";
    private static readonly ConcurrentDictionary<nint, ClipboardThread> s_windows = new();
    private static readonly Lock s_classLock = new();
    private static bool s_classRegistered;

    private readonly Thread _thread;
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly ManualResetEventSlim _ready = new();
    private nint _hwnd;
    private Exception? _startError;

    /// <summary>Clipboard-ul s-a schimbat (apelat pe thread-ul clipboard-ului).</summary>
    public event Action? Changed;

    public ClipboardThread()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "KodDesk clipboard" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
        if (_startError != null) throw new InvalidOperationException("Nu pot porni monitorizarea clipboard-ului", _startError);
    }

    public nint Hwnd => _hwnd;

    /// <summary>Execută o acțiune pe thread-ul clipboard-ului.</summary>
    public void Post(Action action)
    {
        _queue.Enqueue(action);
        if (_hwnd != 0) PostMessage(_hwnd, WM_APP, 0, 0);
    }

    private void Run()
    {
        try
        {
            OleInitialize(0);
            RegisterClass();
            _hwnd = CreateWindowEx(0, ClassName, null, 0, 0, 0, 0, 0, HWND_MESSAGE, 0, Marshal.GetHINSTANCE(typeof(ClipboardThread).Module), 0);
            if (_hwnd == 0) throw new InvalidOperationException($"CreateWindowEx a eșuat ({Marshal.GetLastPInvokeError()})");
            s_windows[_hwnd] = this;
            if (!AddClipboardFormatListener(_hwnd))
                throw new InvalidOperationException($"AddClipboardFormatListener a eșuat ({Marshal.GetLastPInvokeError()})");
        }
        catch (Exception ex)
        {
            _startError = ex;
            _ready.Set();
            return;
        }
        _ready.Set();

        while (GetMessage(out var msg, 0, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        RemoveClipboardFormatListener(_hwnd);
        s_windows.TryRemove(_hwnd, out _);
        DestroyWindow(_hwnd);
        OleUninitialize();
    }

    private static void RegisterClass()
    {
        lock (s_classLock)
        {
            if (s_classRegistered) return;
            var name = Marshal.StringToHGlobalUni(ClassName); // rămâne alocat cât trăiește procesul
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = Marshal.GetHINSTANCE(typeof(ClipboardThread).Module),
                lpszClassName = name,
            };
            if (RegisterClassEx(ref wc) == 0)
                throw new InvalidOperationException($"RegisterClassEx a eșuat ({Marshal.GetLastPInvokeError()})");
            s_classRegistered = true;
        }
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (s_windows.TryGetValue(hwnd, out var self))
        {
            try
            {
                if (msg == WM_CLIPBOARDUPDATE)
                {
                    self.Changed?.Invoke();
                    return 0;
                }
                if (msg == WM_APP)
                {
                    while (self._queue.TryDequeue(out var action)) action();
                    return 0;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Eroare pe thread-ul clipboard-ului", ex);
                return 0;
            }
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        Post(() => PostQuitMessage(0));
        _thread.Join(TimeSpan.FromSeconds(2));
    }
}
