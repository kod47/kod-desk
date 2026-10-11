using System.Runtime.InteropServices;
using KodDesk.Core.Files;
using KodDesk.Protocol;
using ComTypes = System.Runtime.InteropServices.ComTypes;
using static KodDesk.Core.Native.Win32Clipboard;

namespace KodDesk.Core.Clipboard;

/// <summary>
/// Fișiere „virtuale” puse în clipboard: Explorer vede lista (FileGroupDescriptorW) și, la lipire, cere
/// conținutul fiecărui fișier (FileContents) ca IStream. Datele se transferă de la cealaltă parte abia atunci,
/// iar Explorer afișează propria fereastră de progres.
/// </summary>
[ComVisible(true)]
public sealed class VirtualFileDataObject : ComTypes.IDataObject, IDataObjectAsyncCapability, IDisposable
{
    public static readonly short CfFileDescriptor = unchecked((short)RegisterClipboardFormat("FileGroupDescriptorW"));
    public static readonly short CfFileContents = unchecked((short)RegisterClipboardFormat("FileContents"));
    public static readonly short CfPreferredDropEffect = unchecked((short)RegisterClipboardFormat("Preferred DropEffect"));

    /// <summary>Marchează datele puse de KodDesk (evită bucle când viewer și host rulează pe același calculator).</summary>
    public static readonly uint CfKodDeskMarker = RegisterClipboardFormat("KodDesk.VirtualFiles");

    private const int DV_E_FORMATETC = unchecked((int)0x80040064);
    private const int DV_E_LINDEX = unchecked((int)0x80040068);
    private const int OLE_E_ADVISENOTSUPPORTED = unchecked((int)0x80040003);
    private const int DATA_S_SAMEFORMATETC = 0x00040130;
    private const int E_NOTIMPL = unchecked((int)0x80004001);

    private const int DescriptorSize = 592;
    private const uint FD_ATTRIBUTES = 0x4, FD_WRITESTIME = 0x20, FD_FILESIZE = 0x40, FD_PROGRESSUI = 0x4000, FD_UNICODE = 0x80000000;

    private readonly IReadOnlyList<FileEntry> _entries;
    private readonly Func<int, PullStream> _open;
    private readonly List<PullStream> _streams = [];
    private bool _inOperation;
    private bool _disposed;

    public VirtualFileDataObject(IReadOnlyList<FileEntry> entries, Func<int, PullStream> open)
    {
        // Explorer acceptă nume de cel mult 259 de caractere.
        _entries = entries.Where(e => e.RelativePath.Length < 260).ToList();
        _open = open;
    }

    public IReadOnlyList<FileEntry> Entries => _entries;

    private ComTypes.FORMATETC[] Formats() =>
    [
        Fmt(CfFileDescriptor, ComTypes.TYMED.TYMED_HGLOBAL),
        Fmt(CfFileContents, ComTypes.TYMED.TYMED_ISTREAM),
        Fmt(CfPreferredDropEffect, ComTypes.TYMED.TYMED_HGLOBAL),
        Fmt(unchecked((short)CfKodDeskMarker), ComTypes.TYMED.TYMED_HGLOBAL),
    ];

    private static ComTypes.FORMATETC Fmt(short cf, ComTypes.TYMED tymed) => new()
    {
        cfFormat = cf,
        dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT,
        lindex = -1,
        ptd = 0,
        tymed = tymed,
    };

    // ---------------------------------------------------------------- IDataObject

    public void GetData(ref ComTypes.FORMATETC format, out ComTypes.STGMEDIUM medium)
    {
        medium = default;
        if (_disposed) throw new COMException("Datele nu mai sunt disponibile", DV_E_FORMATETC);

        if (format.cfFormat == CfFileDescriptor && format.tymed.HasFlag(ComTypes.TYMED.TYMED_HGLOBAL))
        {
            medium.tymed = ComTypes.TYMED.TYMED_HGLOBAL;
            medium.unionmember = BuildDescriptor();
            return;
        }
        if (format.cfFormat == CfFileContents && format.tymed.HasFlag(ComTypes.TYMED.TYMED_ISTREAM))
        {
            int i = format.lindex;
            if (i < 0 || i >= _entries.Count || _entries[i].IsDirectory)
                throw new COMException("Index invalid", DV_E_LINDEX);
            var stream = _open(i);
            lock (_streams) _streams.Add(stream);
            medium.tymed = ComTypes.TYMED.TYMED_ISTREAM;
            medium.unionmember = Marshal.GetComInterfaceForObject(stream, typeof(ComTypes.IStream));
            return;
        }
        if (format.cfFormat == CfPreferredDropEffect && format.tymed.HasFlag(ComTypes.TYMED.TYMED_HGLOBAL))
        {
            medium.tymed = ComTypes.TYMED.TYMED_HGLOBAL;
            medium.unionmember = GlobalInt(1); // DROPEFFECT_COPY
            return;
        }
        if (format.cfFormat == unchecked((short)CfKodDeskMarker) && format.tymed.HasFlag(ComTypes.TYMED.TYMED_HGLOBAL))
        {
            medium.tymed = ComTypes.TYMED.TYMED_HGLOBAL;
            medium.unionmember = GlobalInt(Environment.ProcessId);
            return;
        }
        throw new COMException("Format nesuportat", DV_E_FORMATETC);
    }

    public void GetDataHere(ref ComTypes.FORMATETC format, ref ComTypes.STGMEDIUM medium) =>
        throw new COMException("Nesuportat", E_NOTIMPL);

    public int QueryGetData(ref ComTypes.FORMATETC format)
    {
        foreach (var f in Formats())
            if (f.cfFormat == format.cfFormat && (f.tymed & format.tymed) != 0) return 0;
        return DV_E_FORMATETC;
    }

    public int GetCanonicalFormatEtc(ref ComTypes.FORMATETC formatIn, out ComTypes.FORMATETC formatOut)
    {
        formatOut = formatIn;
        formatOut.ptd = 0;
        return DATA_S_SAMEFORMATETC;
    }

    public void SetData(ref ComTypes.FORMATETC formatIn, ref ComTypes.STGMEDIUM medium, bool release)
    {
        // Explorer raportează aici rezultatul (Performed DropEffect, Paste Succeeded); nu ne interesează.
        if (release) ReleaseStgMedium(ref medium);
    }

    public ComTypes.IEnumFORMATETC EnumFormatEtc(ComTypes.DATADIR direction)
    {
        if (direction != ComTypes.DATADIR.DATADIR_GET) throw new COMException("Nesuportat", E_NOTIMPL);
        return new FormatEnumerator(Formats());
    }

    public int DAdvise(ref ComTypes.FORMATETC pFormatetc, ComTypes.ADVF advf, ComTypes.IAdviseSink adviseSink, out int connection)
    {
        connection = 0;
        return OLE_E_ADVISENOTSUPPORTED;
    }

    public void DUnadvise(int connection) => throw new COMException("Nesuportat", OLE_E_ADVISENOTSUPPORTED);

    public int EnumDAdvise(out ComTypes.IEnumSTATDATA enumAdvise)
    {
        enumAdvise = null!;
        return OLE_E_ADVISENOTSUPPORTED;
    }

    // ---------------------------------------------------------------- IDataObjectAsyncCapability
    // Spune Explorer-ului că poate extrage fișierele pe un thread de fundal (nu îi blocăm interfața).

    public void SetAsyncMode(int fDoOpAsync) { }
    public void GetAsyncMode(out int pfIsOpAsync) => pfIsOpAsync = 1;
    public void StartOperation(nint pbcReserved) => _inOperation = true;
    public void InOperation(out int pfInAsyncOp) => pfInAsyncOp = _inOperation ? 1 : 0;
    public void EndOperation(int hResult, nint pbcReserved, uint dwEffects) => _inOperation = false;

    // ---------------------------------------------------------------- utilitare

    private unsafe nint BuildDescriptor()
    {
        int n = _entries.Count;
        var h = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, (nuint)(4 + DescriptorSize * n));
        var p = (byte*)GlobalLock(h);
        try
        {
            *(uint*)p = (uint)n;
            for (int i = 0; i < n; i++)
            {
                var e = _entries[i];
                byte* d = p + 4 + DescriptorSize * i;
                *(uint*)d = FD_ATTRIBUTES | FD_WRITESTIME | FD_FILESIZE | FD_PROGRESSUI | FD_UNICODE;
                *(uint*)(d + 36) = e.IsDirectory ? 0x10u : 0x80u; // FILE_ATTRIBUTE_DIRECTORY / NORMAL
                long ft = e.LastWriteUtcTicks > 0 ? new DateTime(e.LastWriteUtcTicks, DateTimeKind.Utc).ToFileTimeUtc() : 0;
                *(long*)(d + 56) = ft;
                *(uint*)(d + 64) = (uint)(e.Size >> 32);
                *(uint*)(d + 68) = (uint)(e.Size & 0xFFFFFFFF);
                var name = e.RelativePath.AsSpan();
                fixed (char* src = name)
                    Buffer.MemoryCopy(src, d + 72, 520, name.Length * 2);
            }
        }
        finally
        {
            GlobalUnlock(h);
        }
        return h;
    }

    private static unsafe nint GlobalInt(int value)
    {
        var h = GlobalAlloc(GMEM_MOVEABLE, 4);
        *(int*)GlobalLock(h) = value;
        GlobalUnlock(h);
        return h;
    }

    public void Dispose()
    {
        _disposed = true;
        lock (_streams)
        {
            foreach (var s in _streams) s.Close();
            _streams.Clear();
        }
    }

    private sealed class FormatEnumerator(ComTypes.FORMATETC[] formats) : ComTypes.IEnumFORMATETC
    {
        private int _pos;

        public int Next(int celt, ComTypes.FORMATETC[] rgelt, int[]? pceltFetched)
        {
            int fetched = 0;
            while (fetched < celt && _pos < formats.Length) rgelt[fetched++] = formats[_pos++];
            if (pceltFetched is { Length: > 0 }) pceltFetched[0] = fetched;
            return fetched == celt ? 0 : 1;
        }

        public int Skip(int celt)
        {
            _pos = Math.Min(_pos + celt, formats.Length);
            return _pos < formats.Length ? 0 : 1;
        }

        public int Reset()
        {
            _pos = 0;
            return 0;
        }

        public void Clone(out ComTypes.IEnumFORMATETC newEnum) => newEnum = new FormatEnumerator(formats) { _pos = _pos };
    }
}

[ComImport, Guid("3D8B0590-F691-11d2-8EA9-006097DF5BD4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDataObjectAsyncCapability
{
    void SetAsyncMode(int fDoOpAsync);
    void GetAsyncMode(out int pfIsOpAsync);
    void StartOperation(nint pbcReserved);
    void InOperation(out int pfInAsyncOp);
    void EndOperation(int hResult, nint pbcReserved, uint dwEffects);
}

/// <summary>IStream care citește conținutul unui fișier de la cealaltă parte, pe măsură ce Explorer îl cere.</summary>
[ComVisible(true)]
public sealed class PullStream : ComTypes.IStream
{
    private const int STG_E_INVALIDFUNCTION = unchecked((int)0x80030001);

    private readonly FileTransferChannel _files;
    private readonly uint _setId;
    private readonly int _index;
    private readonly FileEntry _entry;
    private readonly Lock _lock = new();
    private volatile FileStreamReader? _reader;
    private long _position;
    private volatile bool _closed;

    public PullStream(FileTransferChannel files, uint setId, int index, FileEntry entry)
    {
        _files = files;
        _setId = setId;
        _index = index;
        _entry = entry;
    }

    public void Read(byte[] pv, int cb, nint pcbRead)
    {
        int total = 0;
        lock (_lock)
        {
            if (_closed) throw new IOException("Transferul a fost întrerupt.");
            _reader ??= _files.OpenStream(_setId, _index, _position);
            while (total < cb)
            {
                int n = _reader.Read(pv, total, cb - total);
                if (n == 0) break;
                total += n;
            }
            _position += total;
        }
        if (pcbRead != 0) Marshal.WriteInt32(pcbRead, total);
    }

    public void Seek(long dlibMove, int dwOrigin, nint plibNewPosition)
    {
        lock (_lock)
        {
            long target = dwOrigin switch
            {
                0 => dlibMove,                 // STREAM_SEEK_SET
                1 => _position + dlibMove,     // STREAM_SEEK_CUR
                2 => _entry.Size + dlibMove,   // STREAM_SEEK_END
                _ => throw new COMException("Origine invalidă", STG_E_INVALIDFUNCTION),
            };
            if (target != _position)
            {
                // Înainte de prima citire putem începe de oriunde; după aceea fluxul este doar secvențial.
                if (_reader != null || target < 0 || target > _entry.Size)
                    throw new COMException("Căutare nesuportată", STG_E_INVALIDFUNCTION);
                _position = target;
            }
            if (plibNewPosition != 0) Marshal.WriteInt64(plibNewPosition, _position);
        }
    }

    public void Stat(out ComTypes.STATSTG pstatstg, int grfStatFlag)
    {
        long ft = _entry.LastWriteUtcTicks > 0 ? new DateTime(_entry.LastWriteUtcTicks, DateTimeKind.Utc).ToFileTimeUtc() : 0;
        pstatstg = new ComTypes.STATSTG
        {
            type = 2, // STGTY_STREAM
            cbSize = _entry.Size,
            pwcsName = (grfStatFlag & 1) != 0 ? null! : Path.GetFileName(_entry.RelativePath),
            mtime = new ComTypes.FILETIME { dwLowDateTime = (int)(ft & 0xFFFFFFFF), dwHighDateTime = (int)(ft >> 32) },
        };
    }

    public void CopyTo(ComTypes.IStream pstm, long cb, nint pcbRead, nint pcbWritten)
    {
        var buf = new byte[256 * 1024];
        long copied = 0;
        var readPtr = Marshal.AllocHGlobal(4);
        try
        {
            while (copied < cb)
            {
                int want = (int)Math.Min(buf.Length, cb - copied);
                Read(buf, want, readPtr);
                int n = Marshal.ReadInt32(readPtr);
                if (n == 0) break;
                pstm.Write(n == buf.Length ? buf : buf[..n], n, 0);
                copied += n;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(readPtr);
        }
        if (pcbRead != 0) Marshal.WriteInt64(pcbRead, copied);
        if (pcbWritten != 0) Marshal.WriteInt64(pcbWritten, copied);
    }

    public void Commit(int grfCommitFlags) { }

    public void Write(byte[] pv, int cb, nint pcbWritten) => throw new NotImplementedException();
    public void SetSize(long libNewSize) => throw new NotImplementedException();
    public void Revert() => throw new NotImplementedException();
    public void LockRegion(long libOffset, long cb, int dwLockType) => throw new NotImplementedException();
    public void UnlockRegion(long libOffset, long cb, int dwLockType) => throw new NotImplementedException();
    public void Clone(out ComTypes.IStream ppstm) => throw new NotImplementedException();

    /// <summary>Oprește transferul (clipboard-ul s-a schimbat sau sesiunea s-a închis); deblochează o citire în curs.</summary>
    public void Close()
    {
        _closed = true;
        _reader?.Abort("Transferul a fost întrerupt.");
    }
}
