using System.Runtime.InteropServices;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace KodDesk.App;

/// <summary>
/// Drag &amp; drop OLE direct, cu un IDataObject propriu (fișierele virtuale de la distanță): WPF nu transmite
/// corect FileContents cu index, așa că apelăm DoDragDrop din ole32.
/// </summary>
internal static class OleDrag
{
    private const int DROPEFFECT_COPY = 1;

    [DllImport("ole32.dll")]
    private static extern int DoDragDrop(ComTypes.IDataObject data, IDropSource source, int okEffects, out int effect);

    /// <summary>Rulează operația de drag (blochează până la eliberarea mouse-ului). Returnează true dacă s-a lăsat undeva.</summary>
    public static bool Run(ComTypes.IDataObject data) =>
        DoDragDrop(data, new DropSource(), DROPEFFECT_COPY, out var effect) == 0x00040100 /* DRAGDROP_S_DROP */ && effect != 0;

    [ComImport, Guid("00000121-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDropSource
    {
        [PreserveSig] int QueryContinueDrag(int fEscapePressed, int grfKeyState);
        [PreserveSig] int GiveFeedback(int dwEffect);
    }

    private sealed class DropSource : IDropSource
    {
        private const int MK_LBUTTON = 1;

        public int QueryContinueDrag(int fEscapePressed, int grfKeyState)
        {
            if (fEscapePressed != 0) return 0x00040101; // DRAGDROP_S_CANCEL
            if ((grfKeyState & MK_LBUTTON) == 0) return 0x00040100; // DRAGDROP_S_DROP
            return 0;
        }

        public int GiveFeedback(int dwEffect) => 0x00040102; // DRAGDROP_S_USEDEFAULTCURSORS
    }
}
