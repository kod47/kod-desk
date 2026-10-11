using System.Runtime.InteropServices;

namespace KodDesk.Core.Video;

/// <summary>
/// Buffer dublu BGRA între thread-ul de decodare (scrie în „back”) și thread-ul UI (citește „front”).
/// Dacă UI-ul rămâne în urmă, cadrele intermediare sunt pur și simplu suprascrise.
/// </summary>
public sealed unsafe class FrameBuffer : IDisposable
{
    private struct Buf
    {
        public byte* Ptr;
        public int Width, Height, Size;
    }

    private readonly Lock _lock = new();
    private Buf _back, _front;
    private bool _hasNew;

    /// <summary>Thread-ul de decodare: obține bufferul în care să scrie (realocat la schimbarea dimensiunii).</summary>
    public (nint ptr, int stride) GetBackBuffer(int width, int height)
    {
        int size = width * height * 4;
        if (_back.Ptr == null || _back.Size != size)
        {
            if (_back.Ptr != null) NativeMemory.AlignedFree(_back.Ptr);
            _back.Ptr = (byte*)NativeMemory.AlignedAlloc((nuint)size, 64);
            _back.Size = size;
        }
        _back.Width = width;
        _back.Height = height;
        return ((nint)_back.Ptr, width * 4);
    }

    public void Publish()
    {
        lock (_lock)
        {
            (_back, _front) = (_front, _back);
            _hasNew = true;
        }
    }

    public delegate void FrameReader(nint ptr, int width, int height, int stride);

    /// <summary>Thread-ul UI: dacă există un cadru nou, îl transmite cititorului (sub lock).</summary>
    public bool TryReadLatest(FrameReader reader)
    {
        lock (_lock)
        {
            if (!_hasNew || _front.Ptr == null) return false;
            _hasNew = false;
            reader((nint)_front.Ptr, _front.Width, _front.Height, _front.Width * 4);
            return true;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_back.Ptr != null) NativeMemory.AlignedFree(_back.Ptr);
            if (_front.Ptr != null) NativeMemory.AlignedFree(_front.Ptr);
            _back = default;
            _front = default;
        }
    }
}
