using System.Runtime.InteropServices;
using KodDesk.Core.Native;
using KodDesk.Protocol;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace KodDesk.Core.Capture;

public enum CaptureStatus
{
    NewFrame,
    NoChange,
    /// <summary>Captura nu e disponibilă momentan (schimbare de desktop, rezoluție etc.); se reîncearcă automat.</summary>
    Unavailable,
}

/// <summary>
/// Captură de ecran prin DXGI Desktop Duplication. Imaginea este copiată într-un buffer BGRA propriu,
/// astfel încât ultimul cadru poate fi re-encodat oricând (keyframe la cerere, rafinare calitate).
/// </summary>
public sealed unsafe class ScreenCapturer : IDisposable
{
    private readonly record struct OutputRef(uint Adapter, uint Output, MonitorInfo Info);

    private readonly int _monitorIndex;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _dup;
    private ID3D11Texture2D? _staging;
    private bool _needFirstFrame;
    private long _nextInitAttempt;
    private string? _lastInitError;

    private byte* _buffer;
    private int _bufferSize;

    public ScreenCapturer(int monitorIndex)
    {
        _monitorIndex = monitorIndex;
        TryInit();
    }

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Stride => Width * 4;
    public nint Data => (nint)_buffer;

    /// <summary>True după ce a fost capturat cel puțin un cadru la dimensiunea curentă.</summary>
    public bool HasFrame { get; private set; }

    /// <summary>Monitorul capturat (coordonate în desktop-ul virtual, pixeli fizici).</summary>
    public MonitorInfo? Monitor { get; private set; }

    public int MonitorIndex { get; private set; }

    public static IReadOnlyList<MonitorInfo> GetMonitors() => Enumerate().Select(o => o.Info).ToList();

    public static int PrimaryIndex()
    {
        var list = Enumerate();
        var i = list.FindIndex(o => o.Info.Primary);
        return i < 0 ? 0 : i;
    }

    private static List<OutputRef> Enumerate()
    {
        var list = new List<OutputRef>();
        using var factory = CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            using (adapter)
            {
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                {
                    using (output)
                    {
                        var d = output.Description;
                        if (!d.AttachedToDesktop) continue;
                        var r = d.DesktopCoordinates;
                        list.Add(new OutputRef(a, o, new MonitorInfo(
                            d.DeviceName.TrimStart('\\', '.'), r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
                            r.Left == 0 && r.Top == 0)));
                    }
                }
            }
        }
        return list;
    }

    private bool TryInit()
    {
        if (Environment.TickCount64 < _nextInitAttempt) return false;
        ReleaseDuplication();
        try
        {
            // Ecranul de login, blocare sau UAC rulează pe alt desktop („Winlogon”).
            InputDesktop.SyncCurrentThread();

            var outputs = Enumerate();
            if (outputs.Count == 0) throw new InvalidOperationException("Niciun monitor activ");
            var idx = _monitorIndex >= 0 && _monitorIndex < outputs.Count ? _monitorIndex : outputs.FindIndex(o => o.Info.Primary);
            if (idx < 0) idx = 0;
            var target = outputs[idx];

            using var factory = CreateDXGIFactory1<IDXGIFactory1>();
            factory.EnumAdapters1(target.Adapter, out var adapter).CheckError();
            using (adapter)
            {
                adapter.EnumOutputs(target.Output, out var output).CheckError();
                using (output)
                {
                    D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                        [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out _device, out _context).CheckError();
                    using var output1 = output.QueryInterface<IDXGIOutput1>();
                    _dup = output1.DuplicateOutput(_device);
                }
            }

            var mode = _dup.Description.ModeDescription;
            int w = (int)mode.Width, h = (int)mode.Height;
            if (w != Width || h != Height)
            {
                Width = w;
                Height = h;
                HasFrame = false;
                AllocBuffer();
            }

            _staging = _device!.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)w,
                Height = (uint)h,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None,
            });

            Monitor = target.Info with { Width = w, Height = h };
            MonitorIndex = idx;
            _needFirstFrame = true;
            Log.Info($"Captură pornită: {target.Info.Name} {w}x{h} la ({target.Info.Left},{target.Info.Top})");
            _lastInitError = null;
            return true;
        }
        catch (Exception ex)
        {
            // Aceeași eroare se repetă de câteva ori pe secundă (ex. desktop securizat fără drepturi SYSTEM): o jurnalizăm o singură dată.
            if (ex.Message != _lastInitError)
            {
                Log.Warn("Nu pot porni captura ecranului", ex);
                _lastInitError = ex.Message;
            }
            ReleaseDuplication();
            _nextInitAttempt = Environment.TickCount64 + 500;
            return false;
        }
    }

    private void AllocBuffer()
    {
        if (_buffer != null) NativeMemory.AlignedFree(_buffer);
        _bufferSize = Width * Height * 4;
        _buffer = (byte*)NativeMemory.AlignedAlloc((nuint)_bufferSize, 64);
        new Span<byte>(_buffer, _bufferSize).Clear();
    }

    public CaptureStatus TryCapture(int timeoutMs)
    {
        if (_dup == null && !TryInit())
        {
            Thread.Sleep(Math.Min(timeoutMs, 100));
            return CaptureStatus.Unavailable;
        }

        var hr = _dup!.AcquireNextFrame((uint)timeoutMs, out var info, out var resource);
        if (hr == Vortice.DXGI.ResultCode.WaitTimeout)
            return CaptureStatus.NoChange;
        if (hr.Failure)
        {
            // AccessLost: schimbare de mod video, desktop securizat (UAC/blocare) etc.
            Log.Info($"Captura întreruptă (0x{hr.Code:X8}); reinițializez");
            ReleaseDuplication();
            return CaptureStatus.Unavailable;
        }

        try
        {
            // LastPresentTime == 0 înseamnă că s-a schimbat doar cursorul, nu și imaginea.
            if (info.LastPresentTime == 0 && !_needFirstFrame)
                return CaptureStatus.NoChange;
            using var tex = resource.QueryInterface<ID3D11Texture2D>();
            _context!.CopyResource(_staging!, tex);
        }
        finally
        {
            resource.Dispose();
            _dup.ReleaseFrame();
        }

        var map = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var src = (byte*)map.DataPointer;
            int rowBytes = Width * 4;
            if (map.RowPitch == rowBytes)
            {
                Buffer.MemoryCopy(src, _buffer, _bufferSize, _bufferSize);
            }
            else
            {
                for (int y = 0; y < Height; y++)
                    Buffer.MemoryCopy(src + (long)y * map.RowPitch, _buffer + (long)y * rowBytes, rowBytes, rowBytes);
            }
        }
        finally
        {
            _context.Unmap(_staging!, 0);
        }

        _needFirstFrame = false;
        HasFrame = true;
        return CaptureStatus.NewFrame;
    }

    private void ReleaseDuplication()
    {
        _dup?.Dispose(); _dup = null;
        _staging?.Dispose(); _staging = null;
        _context?.Dispose(); _context = null;
        _device?.Dispose(); _device = null;
    }

    public void Dispose()
    {
        ReleaseDuplication();
        if (_buffer != null)
        {
            NativeMemory.AlignedFree(_buffer);
            _buffer = null;
        }
    }
}
