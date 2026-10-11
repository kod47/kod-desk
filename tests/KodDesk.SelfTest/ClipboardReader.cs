using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ComTypes = System.Runtime.InteropServices.ComTypes;

/// <summary>
/// Citește fișierele virtuale din clipboard exact cum o face Explorer la „Lipire”: FileGroupDescriptorW,
/// apoi FileContents (IStream) pentru elementul cerut. Rulează într-un proces separat, ca să testeze
/// și transmiterea obiectelor COM între procese.
/// </summary>
internal static unsafe class ClipboardReader
{
    [DllImport("ole32.dll")] private static extern int OleInitialize(nint reserved);
    [DllImport("ole32.dll")] private static extern int OleGetClipboard(out ComTypes.IDataObject data);
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref ComTypes.STGMEDIUM medium);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint h);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint h);

    public static int Run(string fileName)
    {
        int exit = 1;
        var t = new Thread(() =>
        {
            try
            {
                OleInitialize(0);
                Marshal.ThrowExceptionForHR(OleGetClipboard(out var data));

                var fd = new ComTypes.FORMATETC
                {
                    cfFormat = unchecked((short)RegisterClipboardFormat("FileGroupDescriptorW")),
                    dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = ComTypes.TYMED.TYMED_HGLOBAL,
                };
                data.GetData(ref fd, out var med);
                int index = -1;
                long size = 0;
                var p = (byte*)GlobalLock(med.unionmember);
                int count = *(int*)p;
                for (int i = 0; i < count; i++)
                {
                    byte* d = p + 4 + 592 * i;
                    var name = new string((char*)(d + 72));
                    if (name == fileName) { index = i; size = ((long)*(uint*)(d + 64) << 32) | *(uint*)(d + 68); }
                }
                GlobalUnlock(med.unionmember);
                ReleaseStgMedium(ref med);
                if (index < 0) { Console.WriteLine($"NOTFOUND {count}"); return; }

                var fc = new ComTypes.FORMATETC
                {
                    cfFormat = unchecked((short)RegisterClipboardFormat("FileContents")),
                    dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT, lindex = index, tymed = ComTypes.TYMED.TYMED_ISTREAM,
                };
                data.GetData(ref fc, out var med2);
                var stream = (ComTypes.IStream)Marshal.GetObjectForIUnknown(med2.unionmember);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buf = new byte[256 * 1024];
                var readPtr = Marshal.AllocHGlobal(4);
                long total = 0;
                while (true)
                {
                    stream.Read(buf, buf.Length, readPtr);
                    int n = Marshal.ReadInt32(readPtr);
                    if (n == 0) break;
                    sha.AppendData(buf, 0, n);
                    total += n;
                }
                Marshal.FreeHGlobal(readPtr);
                ReleaseStgMedium(ref med2);
                Console.WriteLine($"HASH {Convert.ToHexString(sha.GetHashAndReset())} {total} {size}");
                exit = 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR " + ex.Message);
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return exit;
    }
}
