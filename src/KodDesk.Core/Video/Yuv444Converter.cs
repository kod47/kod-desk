namespace KodDesk.Core.Video;

/// <summary>
/// Conversie rapidă YUV 4:4:4 planar -> BGRA (BT.709), paralelizată pe benzi de rânduri.
/// swscale nu are o cale optimizată pentru yuv444p -> bgra, iar la rezoluții mari devenea gâtul de sticlă.
/// </summary>
public sealed unsafe class Yuv444Converter
{
    private const int Shift = 16;

    private readonly int[] _y = new int[256];
    private readonly int[] _rv = new int[256];
    private readonly int[] _gu = new int[256];
    private readonly int[] _gv = new int[256];
    private readonly int[] _bu = new int[256];

    public Yuv444Converter(bool fullRange)
    {
        // BT.709: Kr = 0.2126, Kb = 0.0722
        const double kr = 0.2126, kb = 0.0722, kg = 1 - kr - kb;
        double yScale = fullRange ? 1.0 : 255.0 / 219.0;
        double cScale = fullRange ? 1.0 : 255.0 / 224.0;
        int yOffset = fullRange ? 0 : 16;
        double one = 1 << Shift;

        for (int i = 0; i < 256; i++)
        {
            double y = (i - yOffset) * yScale;
            double c = (i - 128) * cScale;
            _y[i] = (int)Math.Round(y * one) + (1 << (Shift - 1)); // include rotunjirea
            _rv[i] = (int)Math.Round(2 * (1 - kr) * c * one);
            _bu[i] = (int)Math.Round(2 * (1 - kb) * c * one);
            _gu[i] = (int)Math.Round(-2 * (1 - kb) * kb / kg * c * one);
            _gv[i] = (int)Math.Round(-2 * (1 - kr) * kr / kg * c * one);
        }
    }

    public void Convert(byte* yPlane, int yStride, byte* uPlane, int uStride, byte* vPlane, int vStride,
        byte* dst, int dstStride, int width, int height)
    {
        int bands = Math.Clamp(Environment.ProcessorCount, 1, 16);
        int rowsPerBand = (height + bands - 1) / bands;
        nint py = (nint)yPlane, pu = (nint)uPlane, pv = (nint)vPlane, pd = (nint)dst;

        Parallel.For(0, bands, band =>
        {
            int y0 = band * rowsPerBand;
            int y1 = Math.Min(height, y0 + rowsPerBand);
            fixed (int* ty = _y, trv = _rv, tgu = _gu, tgv = _gv, tbu = _bu)
            {
                for (int row = y0; row < y1; row++)
                {
                    byte* sy = (byte*)py + (long)row * yStride;
                    byte* su = (byte*)pu + (long)row * uStride;
                    byte* sv = (byte*)pv + (long)row * vStride;
                    uint* d = (uint*)((byte*)pd + (long)row * dstStride);
                    for (int x = 0; x < width; x++)
                    {
                        int yy = ty[sy[x]];
                        int u = su[x], v = sv[x];
                        int r = (yy + trv[v]) >> Shift;
                        int g = (yy + tgu[u] + tgv[v]) >> Shift;
                        int b = (yy + tbu[u]) >> Shift;
                        r = r < 0 ? 0 : r > 255 ? 255 : r;
                        g = g < 0 ? 0 : g > 255 ? 255 : g;
                        b = b < 0 ? 0 : b > 255 ? 255 : b;
                        d[x] = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
                    }
                }
            }
        });
    }
}
