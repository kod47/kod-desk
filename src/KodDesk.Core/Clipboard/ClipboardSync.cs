using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using KodDesk.Core.Files;
using KodDesk.Protocol;
using static KodDesk.Core.Native.Win32Clipboard;

namespace KodDesk.Core.Clipboard;

/// <summary>
/// Sincronizează clipboard-ul între cele două calculatoare, în ambele sensuri:
///   text și imagini — conținutul se trimite imediat;
///   fișiere — se trimite doar lista; conținutul se transferă la lipire (fișiere virtuale, vezi <see cref="VirtualFileDataObject"/>).
/// </summary>
public sealed unsafe class ClipboardSync : IDisposable
{
    private const int MaxTextChars = 4 * 1024 * 1024;
    private const int MaxImageBytes = 64 * 1024 * 1024;

    private readonly MessageChannel _ch;
    private readonly FileTransferChannel _files;
    private readonly ClipboardThread _thread;
    private uint _ownSequence;
    private VirtualFileDataObject? _virtual;
    private string? _lastContent;
    private volatile bool _enabled = true;
    private bool _disposed;

    /// <param name="sendCurrent">Trimite imediat conținutul actual (viewer-ul, la conectare).</param>
    public ClipboardSync(MessageChannel ch, FileTransferChannel files, bool sendCurrent)
    {
        _ch = ch;
        _files = files;
        _thread = new ClipboardThread();
        _thread.Changed += OnLocalChanged;
        if (sendCurrent) _thread.Post(OnLocalChanged);
    }

    /// <summary>Sincronizarea poate fi oprită din interfață; când e oprită nu se trimite și nu se aplică nimic.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    // ---------------------------------------------------------------- local -> cealaltă parte

    private void OnLocalChanged()
    {
        if (!_enabled || _disposed) return;
        if (GetClipboardSequenceNumber() == _ownSequence) return;
        if (_virtual != null && OleIsCurrentClipboard(_virtual) == 0) return;

        string[]? paths = null;
        string? text = null;
        byte[]? dib = null;
        if (!TryOpenClipboard()) return;
        try
        {
            // Fișiere virtuale puse de altă instanță KodDesk de pe același calculator: nu le retrimitem.
            if (IsClipboardFormatAvailable(VirtualFileDataObject.CfKodDeskMarker)) return;
            if (IsClipboardFormatAvailable(CF_HDROP)) paths = ReadFileList();
            else if (IsClipboardFormatAvailable(CF_UNICODETEXT)) text = ReadText();
            else if (IsClipboardFormatAvailable(CF_DIB)) dib = ReadDib();
        }
        finally
        {
            CloseClipboard();
        }

        if (paths is { Length: > 0 })
        {
            var signature = "F:" + string.Join('|', paths);
            if (signature == _lastContent) return;
            _lastContent = signature;
            _ = Task.Run(() => OfferFiles(paths));
        }
        else if (text != null)
        {
            var signature = "T:" + Hash(Encoding.UTF8.GetBytes(text));
            if (signature == _lastContent) return;
            _lastContent = signature;
            _ch.Send(MessageType.ClipboardText, new PacketWriter().Bytes(Encoding.UTF8.GetBytes(text)));
        }
        else if (dib != null)
        {
            var signature = "I:" + Hash(dib);
            if (signature == _lastContent) return;
            _lastContent = signature;
            using var ms = new MemoryStream();
            using (var z = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true)) z.Write(dib);
            _ch.Send(MessageType.ClipboardImage, new PacketWriter().U32((uint)dib.Length).Bytes(ms.ToArray()), lowPriority: true);
        }
    }

    private void OfferFiles(string[] paths)
    {
        try
        {
            var files = _files.Context.Run(() => FileSetBuilder.Build(paths));
            if (files.Count == 0) return;
            var setId = _files.Offer(files);
            _ch.Send(MessageType.ClipboardFiles, new PacketWriter().U32(setId).WriteEntries(files.Select(f => f.Entry).ToList()));
            Log.Info($"Clipboard: ofer {files.Count} elemente");
        }
        catch (Exception ex)
        {
            Log.Warn("Clipboard: nu pot oferi fișierele copiate", ex);
        }
    }

    private bool TryOpenClipboard()
    {
        // Altă aplicație poate ține clipboard-ul deschis câteva milisecunde.
        for (int i = 0; i < 10; i++)
        {
            if (OpenClipboard(_thread.Hwnd)) return true;
            Thread.Sleep(20);
        }
        Log.Warn("Clipboard: nu pot deschide clipboard-ul");
        return false;
    }

    private static string? ReadText()
    {
        var h = GetClipboardData(CF_UNICODETEXT);
        if (h == 0) return null;
        var p = GlobalLock(h);
        try
        {
            int maxChars = (int)Math.Min((long)GlobalSize(h) / 2, MaxTextChars);
            var span = new ReadOnlySpan<char>((char*)p, maxChars);
            int end = span.IndexOf('\0');
            return new string(end >= 0 ? span[..end] : span);
        }
        finally
        {
            GlobalUnlock(h);
        }
    }

    private static byte[]? ReadDib()
    {
        var h = GetClipboardData(CF_DIB);
        if (h == 0) return null;
        var size = (long)GlobalSize(h);
        if (size <= 0 || size > MaxImageBytes) return null;
        var p = GlobalLock(h);
        try { return new ReadOnlySpan<byte>((byte*)p, (int)size).ToArray(); }
        finally { GlobalUnlock(h); }
    }

    private static string[] ReadFileList()
    {
        var h = GetClipboardData(CF_HDROP);
        if (h == 0) return [];
        uint count = DragQueryFile(h, 0xFFFFFFFF, null, 0);
        var result = new string[count];
        for (uint i = 0; i < count; i++)
        {
            uint len = DragQueryFile(h, i, null, 0);
            var buf = new char[len + 1];
            fixed (char* b = buf) DragQueryFile(h, i, b, len + 1);
            result[i] = new string(buf, 0, (int)len);
        }
        return result;
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    // ---------------------------------------------------------------- cealaltă parte -> local

    /// <summary>Tratează un mesaj de clipboard. Returnează false dacă mesajul nu este de acest tip.</summary>
    public bool Handle(MessageType type, ReadOnlySpan<byte> payload)
    {
        switch (type)
        {
            case MessageType.ClipboardText:
            {
                var bytes = new PacketReader(payload).Bytes().ToArray();
                if (_enabled) _thread.Post(() => ApplyText(Encoding.UTF8.GetString(bytes), "T:" + Hash(bytes)));
                return true;
            }
            case MessageType.ClipboardImage:
            {
                var r = new PacketReader(payload);
                int rawLength = (int)r.U32();
                var compressed = r.Bytes().ToArray();
                if (_enabled && rawLength > 0 && rawLength <= MaxImageBytes) _thread.Post(() => ApplyImage(compressed, rawLength));
                return true;
            }
            case MessageType.ClipboardFiles:
            {
                var r = new PacketReader(payload);
                uint setId = r.U32();
                var entries = FilePayloads.ReadEntries(ref r);
                if (_enabled) _thread.Post(() => ApplyFiles(setId, entries));
                return true;
            }
            default:
                return false;
        }
    }

    private void ApplyText(string text, string signature)
    {
        if (_disposed) return;
        _lastContent = signature;
        var bytes = (text.Length + 1) * 2;
        SetData(CF_UNICODETEXT, bytes, p =>
        {
            fixed (char* src = text) Buffer.MemoryCopy(src, (void*)p, bytes, text.Length * 2);
            ((char*)p)[text.Length] = '\0';
        });
    }

    private void ApplyImage(byte[] compressed, int rawLength)
    {
        if (_disposed) return;
        var dib = new byte[rawLength];
        using (var z = new DeflateStream(new MemoryStream(compressed), CompressionMode.Decompress))
            z.ReadExactly(dib);
        _lastContent = "I:" + Hash(dib);
        SetData(CF_DIB, dib.Length, p => Marshal.Copy(dib, 0, (nint)p, dib.Length));
    }

    private void ApplyFiles(uint setId, List<FileEntry> entries)
    {
        if (_disposed) return;
        ReleaseVirtual();
        var data = new VirtualFileDataObject(entries, i => new PullStream(_files, setId, i, entries[i]));
        int hr = 0;
        for (int attempt = 0; attempt < 10; attempt++)
        {
            hr = OleSetClipboard(data);
            if (hr != unchecked((int)0x800401D0)) break; // CLIPBRD_E_CANT_OPEN: altă aplicație ține clipboard-ul
            Thread.Sleep(20);
        }
        if (hr != 0)
        {
            Log.Warn($"Clipboard: OleSetClipboard a eșuat (0x{hr:X8})");
            data.Dispose();
            return;
        }
        _virtual = data;
        _ownSequence = GetClipboardSequenceNumber();
        _lastContent = null;
        Log.Info($"Clipboard: {entries.Count} elemente disponibile pentru lipire");
    }

    private void SetData(uint format, int size, Action<nint> fill)
    {
        ReleaseVirtual();
        if (!TryOpenClipboard()) return;
        try
        {
            EmptyClipboard();
            var h = GlobalAlloc(GMEM_MOVEABLE, (nuint)size);
            if (h == 0) return;
            fill(GlobalLock(h));
            GlobalUnlock(h);
            if (SetClipboardData(format, h) == 0) GlobalFree(h);
        }
        finally
        {
            CloseClipboard();
            _ownSequence = GetClipboardSequenceNumber();
        }
    }

    private void ReleaseVirtual()
    {
        if (_virtual == null) return;
        if (OleIsCurrentClipboard(_virtual) == 0) OleSetClipboard(null);
        _virtual.Dispose();
        _virtual = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _thread.Post(() =>
        {
            // Fișierele virtuale nu mai pot fi citite după închiderea sesiunii: le scoatem din clipboard.
            ReleaseVirtual();
            _disposed = true;
        });
        _thread.Dispose();
        _disposed = true;
    }
}
