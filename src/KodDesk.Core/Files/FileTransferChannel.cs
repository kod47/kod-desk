using System.Collections.Concurrent;
using System.Threading.Channels;
using KodDesk.Protocol;

namespace KodDesk.Core.Files;

public sealed record TransferProgress(long Done, long Total, string? CurrentFile);

public sealed record UploadResult(bool Ok, string Message, string? SavedTo);

/// <summary>
/// Transferul de fișiere peste canalul criptat al sesiunii, identic pe ambele părți.
///
/// Partea care deține fișierele le „oferă” ca set (ID + listă); cealaltă parte deschide fluxuri pentru
/// elementele setului (FileStreamOpen) și primește datele în bucăți, confirmând periodic cât a consumat,
/// astfel încât în tranzit să nu fie niciodată mai mult de <see cref="SendWindow"/> octeți.
///
/// Doar host-ul răspunde la cereri de încărcare, listare și descărcare (<c>serveRequests</c>); viewer-ul
/// expune exclusiv fișierele pe care le-a oferit explicit (drag &amp; drop, meniu, clipboard).
/// </summary>
public sealed class FileTransferChannel : IDisposable
{
    private const int ChunkSize = 256 * 1024;
    private const long SendWindow = 4L * 1024 * 1024;
    private const long AckEvery = 1L * 1024 * 1024;
    private const int MaxSets = 32;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    private readonly MessageChannel _ch;
    private readonly IFileAccessContext _ctx;
    private readonly bool _serveRequests;
    private readonly CancellationTokenSource _cts = new();

    private readonly Lock _setsLock = new();
    private readonly Dictionary<uint, OwnedSet> _sets = [];
    private readonly Queue<uint> _setOrder = new();
    private int _nextSetId;

    private readonly ConcurrentDictionary<uint, Outgoing> _outgoing = new();
    private readonly ConcurrentDictionary<uint, FileStreamReader> _incoming = new();
    private int _nextStreamId;

    private int _nextRequestId;
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<UploadResult>> _uploads = new();
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<(string Path, List<DirItem> Items)>> _dirLists = new();
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<(uint SetId, List<FileEntry> Entries)>> _downloads = new();

    public FileTransferChannel(MessageChannel ch, IFileAccessContext ctx, bool serveRequests)
    {
        _ch = ch;
        _ctx = ctx;
        _serveRequests = serveRequests;
    }

    public IFileAccessContext Context => _ctx;

    /// <summary>Octeți trimiși dintr-un set oferit (setId, octeți).</summary>
    public event Action<uint, long>? BytesSent;

    private sealed class OwnedSet(List<OfferedFile> files)
    {
        public List<OfferedFile> Files { get; } = files;
        public CancellationTokenSource Cts { get; } = new();
    }

    private sealed class Outgoing(CancellationTokenSource cts)
    {
        public long Acked;
        public SemaphoreSlim Signal { get; } = new(0);
        public CancellationTokenSource Cts { get; } = cts;
    }

    // ------------------------------------------------------------------ seturi oferite

    public uint Offer(List<OfferedFile> files)
    {
        lock (_setsLock)
        {
            var id = (uint)Interlocked.Increment(ref _nextSetId);
            _sets[id] = new OwnedSet(files);
            _setOrder.Enqueue(id);
            while (_setOrder.Count > MaxSets)
            {
                var old = _setOrder.Dequeue();
                if (_sets.Remove(old, out var s)) s.Cts.Cancel();
            }
            return id;
        }
    }

    /// <summary>Retrage un set: fluxurile încă deschise pentru el se opresc.</summary>
    public void Release(uint setId)
    {
        lock (_setsLock)
        {
            if (_sets.Remove(setId, out var s)) s.Cts.Cancel();
        }
    }

    // ------------------------------------------------------------------ recepție mesaje

    /// <summary>Tratează un mesaj de fișiere. Returnează false dacă mesajul nu este de acest tip.</summary>
    public bool Handle(MessageType type, ReadOnlySpan<byte> payload)
    {
        var r = new PacketReader(payload);
        switch (type)
        {
            case MessageType.FileStreamOpen:
            {
                uint streamId = r.U32(), setId = r.U32(), index = r.U32();
                long offset = r.I64();
                StartOutgoing(streamId, setId, (int)index, offset);
                return true;
            }
            case MessageType.FileStreamAck:
            {
                uint streamId = r.U32();
                long consumed = r.I64();
                if (_outgoing.TryGetValue(streamId, out var o))
                {
                    Volatile.Write(ref o.Acked, consumed);
                    o.Signal.Release();
                }
                return true;
            }
            case MessageType.FileStreamClose:
                if (_outgoing.TryGetValue(r.U32(), out var closed)) closed.Cts.Cancel();
                return true;

            case MessageType.FileStreamData:
            {
                uint streamId = r.U32();
                bool eof = r.U8() != 0;
                var data = r.Rest().ToArray();
                if (_incoming.TryGetValue(streamId, out var reader)) reader.Push(data, eof);
                return true;
            }
            case MessageType.FileStreamError:
            {
                uint streamId = r.U32();
                var message = r.Str();
                if (_incoming.TryGetValue(streamId, out var reader)) reader.Fail(message);
                return true;
            }

            case MessageType.FileUpload when _serveRequests:
            {
                uint setId = r.U32();
                var dest = r.Str();
                var entries = FilePayloads.ReadEntries(ref r);
                _ = Task.Run(() => ServeUploadAsync(setId, dest, entries));
                return true;
            }
            case MessageType.DirListRequest when _serveRequests:
            {
                uint reqId = r.U32();
                var path = r.Str();
                _ = Task.Run(() => ServeDirList(reqId, path));
                return true;
            }
            case MessageType.DownloadRequest when _serveRequests:
            {
                uint reqId = r.U32();
                int n = r.U16();
                var paths = new List<string>(n);
                for (int i = 0; i < n; i++) paths.Add(r.Str());
                _ = Task.Run(() => ServeDownload(reqId, paths));
                return true;
            }

            case MessageType.FileUploadResult:
            {
                uint setId = r.U32();
                bool ok = r.Bool();
                var message = r.Str();
                var savedTo = r.Str();
                if (_uploads.TryRemove(setId, out var tcs)) tcs.TrySetResult(new UploadResult(ok, message, savedTo));
                return true;
            }
            case MessageType.DirListResponse:
            {
                uint reqId = r.U32();
                bool ok = r.Bool();
                var error = r.Str();
                var path = r.Str();
                var items = FilePayloads.ReadDirItems(ref r);
                if (_dirLists.TryRemove(reqId, out var tcs))
                {
                    if (ok) tcs.TrySetResult((path, items));
                    else tcs.TrySetException(new IOException(error));
                }
                return true;
            }
            case MessageType.DownloadResponse:
            {
                uint reqId = r.U32();
                bool ok = r.Bool();
                var error = r.Str();
                uint setId = r.U32();
                var entries = FilePayloads.ReadEntries(ref r);
                if (_downloads.TryRemove(reqId, out var tcs))
                {
                    if (ok) tcs.TrySetResult((setId, entries));
                    else tcs.TrySetException(new IOException(error));
                }
                return true;
            }

            case MessageType.FileUpload or MessageType.DirListRequest or MessageType.DownloadRequest:
                Log.Warn($"Cerere de fișiere ignorată ({type}): această parte nu servește cereri");
                return true;

            default:
                return false;
        }
    }

    // ------------------------------------------------------------------ trimitere (partea care deține fișierele)

    private void StartOutgoing(uint streamId, uint setId, int index, long offset)
    {
        OwnedSet? set;
        lock (_setsLock) _sets.TryGetValue(setId, out set);
        var file = set != null && index >= 0 && index < set.Files.Count ? set.Files[index] : null;
        if (set == null || file == null || file.Entry.IsDirectory || file.FullPath == null)
        {
            SendError(streamId, "Fișierul nu mai este disponibil.");
            return;
        }

        var o = new Outgoing(CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, set.Cts.Token));
        _outgoing[streamId] = o;
        _ = Task.Run(() => SendFileAsync(streamId, setId, file.FullPath, offset, o));
    }

    private async Task SendFileAsync(uint streamId, uint setId, string path, long offset, Outgoing o)
    {
        var ct = o.Cts.Token;
        try
        {
            await using var fs = _ctx.Run(() => new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan));
            if (offset > 0) fs.Seek(offset, SeekOrigin.Begin);
            var buf = new byte[ChunkSize];
            long sent = 0;
            while (true)
            {
                while (sent - Volatile.Read(ref o.Acked) >= SendWindow)
                    await o.Signal.WaitAsync(ct);
                int n = await fs.ReadAsync(buf, ct);
                var w = new PacketWriter(n + 8).U32(streamId).U8(n == 0 ? (byte)1 : (byte)0).Raw(buf.AsSpan(0, n));
                _ch.Send(MessageType.FileStreamData, w, lowPriority: true);
                if (n == 0) break;
                sent += n;
                BytesSent?.Invoke(setId, n);
            }
        }
        catch (OperationCanceledException)
        {
            SendError(streamId, "Transfer anulat.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Nu pot trimite {path}", ex);
            SendError(streamId, ex.Message);
        }
        finally
        {
            _outgoing.TryRemove(streamId, out _);
            o.Cts.Dispose();
        }
    }

    private void SendError(uint streamId, string message) =>
        _ch.Send(MessageType.FileStreamError, new PacketWriter().U32(streamId).Str(Truncate(message)));

    private static string Truncate(string s) => s.Length > 1000 ? s[..1000] : s;

    // ------------------------------------------------------------------ citire (partea care primește)

    /// <summary>Deschide un flux pentru elementul <paramref name="index"/> al unui set oferit de cealaltă parte.</summary>
    public FileStreamReader OpenStream(uint setId, int index, long offset = 0)
    {
        var id = (uint)Interlocked.Increment(ref _nextStreamId);
        var reader = new FileStreamReader(this, id);
        _incoming[id] = reader;
        if (_cts.IsCancellationRequested) reader.Fail("Sesiunea s-a închis.");
        else _ch.Send(MessageType.FileStreamOpen, new PacketWriter().U32(id).U32(setId).U32((uint)index).I64(offset));
        return reader;
    }

    internal void SendAck(uint streamId, long consumed) =>
        _ch.Send(MessageType.FileStreamAck, new PacketWriter().U32(streamId).I64(consumed));

    internal void OnReaderDisposed(FileStreamReader reader, bool finished)
    {
        _incoming.TryRemove(reader.StreamId, out _);
        if (!finished && !_cts.IsCancellationRequested)
            _ch.Send(MessageType.FileStreamClose, new PacketWriter().U32(reader.StreamId));
    }

    /// <summary>
    /// Primește un set întreg în <paramref name="targetDir"/>. Elementele de pe primul nivel care există deja
    /// primesc un nume nou („nume (2).ext”); nimic nu se suprascrie.
    /// </summary>
    public async Task ReceiveSetAsync(uint setId, IReadOnlyList<FileEntry> entries, string targetDir,
        IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        long total = entries.Where(e => !e.IsDirectory).Sum(e => e.Size), done = 0;
        _ctx.Run(() => Directory.CreateDirectory(targetDir));

        var topNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string MapTopLevel(string rel)
        {
            int sep = rel.IndexOf('\\');
            var top = sep < 0 ? rel : rel[..sep];
            if (!topNames.TryGetValue(top, out var mapped))
            {
                var candidate = SafePath.Combine(targetDir, top);
                mapped = Path.GetFileName(_ctx.Run(() => SafePath.Unique(candidate, p => File.Exists(p) || Directory.Exists(p))));
                topNames[top] = mapped;
            }
            return sep < 0 ? mapped : mapped + rel[sep..];
        }

        var buf = new byte[ChunkSize];
        long lastReport = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var full = SafePath.Combine(targetDir, MapTopLevel(e.RelativePath));
            if (e.IsDirectory)
            {
                _ctx.Run(() => Directory.CreateDirectory(full));
                continue;
            }

            progress?.Report(new TransferProgress(done, total, e.RelativePath));
            using var reader = OpenStream(setId, i);
            var fs = _ctx.Run(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                return new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1);
            });
            try
            {
                await using (fs)
                {
                    int n;
                    while ((n = await reader.ReadAsync(buf, ct)) > 0)
                    {
                        await fs.WriteAsync(buf.AsMemory(0, n), ct);
                        done += n;
                        if (done - lastReport >= 4 * 1024 * 1024)
                        {
                            lastReport = done;
                            progress?.Report(new TransferProgress(done, total, e.RelativePath));
                        }
                    }
                }
            }
            catch
            {
                try { _ctx.Run(() => { File.Delete(full); return 0; }); } catch { }
                throw;
            }
            try { _ctx.Run(() => { File.SetLastWriteTimeUtc(full, new DateTime(e.LastWriteUtcTicks, DateTimeKind.Utc)); return 0; }); }
            catch { }
        }
        progress?.Report(new TransferProgress(done, total, null));
    }

    // ------------------------------------------------------------------ API viewer

    /// <summary>Trimite fișiere locale pe calculatorul la distanță (în <paramref name="remoteFolder"/> sau pe Desktop).</summary>
    public async Task<UploadResult> UploadAsync(IReadOnlyList<string> localPaths, string? remoteFolder,
        IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var files = await Task.Run(() => _ctx.Run(() => FileSetBuilder.Build(localPaths)), ct);
        if (files.Count == 0) return new UploadResult(false, "Nu am găsit nimic de trimis.", null);

        long total = files.Where(f => !f.Entry.IsDirectory).Sum(f => f.Entry.Size), done = 0;
        var setId = Offer(files);
        var tcs = new TaskCompletionSource<UploadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _uploads[setId] = tcs;
        long lastReport = 0;
        void OnSent(uint id, long n)
        {
            if (id != setId) return;
            var d = Interlocked.Add(ref done, n);
            if (d - Interlocked.Read(ref lastReport) >= 1024 * 1024 || d == total)
            {
                Interlocked.Exchange(ref lastReport, d);
                progress?.Report(new TransferProgress(d, total, null));
            }
        }
        BytesSent += OnSent;
        try
        {
            progress?.Report(new TransferProgress(0, total, null));
            _ch.Send(MessageType.FileUpload, new PacketWriter()
                .U32(setId).Str(remoteFolder ?? "").WriteEntries(files.Select(f => f.Entry).ToList()));
            using (ct.Register(() => tcs.TrySetResult(new UploadResult(false, "Transfer anulat.", null))))
                return await tcs.Task;
        }
        finally
        {
            BytesSent -= OnSent;
            _uploads.TryRemove(setId, out _);
            Release(setId);
        }
    }

    /// <summary>Listează un folder de pe calculatorul la distanță ("" = locuri rapide și unități).</summary>
    public async Task<(string Path, List<DirItem> Items)> ListDirectoryAsync(string path, CancellationToken ct)
    {
        var id = (uint)Interlocked.Increment(ref _nextRequestId);
        var tcs = new TaskCompletionSource<(string, List<DirItem>)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dirLists[id] = tcs;
        try
        {
            _ch.Send(MessageType.DirListRequest, new PacketWriter().U32(id).Str(path));
            return await tcs.Task.WaitAsync(RequestTimeout, ct);
        }
        finally
        {
            _dirLists.TryRemove(id, out _);
        }
    }

    /// <summary>Cere host-ului să ofere fișierele/folderele date; descărcarea se face apoi cu <see cref="ReceiveSetAsync"/>.</summary>
    public async Task<(uint SetId, List<FileEntry> Entries)> RequestDownloadAsync(IReadOnlyList<string> remotePaths, CancellationToken ct)
    {
        var id = (uint)Interlocked.Increment(ref _nextRequestId);
        var tcs = new TaskCompletionSource<(uint, List<FileEntry>)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _downloads[id] = tcs;
        try
        {
            var w = new PacketWriter().U32(id).U16((ushort)remotePaths.Count);
            foreach (var p in remotePaths) w.Str(p);
            _ch.Send(MessageType.DownloadRequest, w);
            return await tcs.Task.WaitAsync(RequestTimeout, ct);
        }
        finally
        {
            _downloads.TryRemove(id, out _);
        }
    }

    // ------------------------------------------------------------------ cereri servite de host

    private async Task ServeUploadAsync(uint setId, string dest, List<FileEntry> entries)
    {
        try
        {
            var target = string.IsNullOrEmpty(dest)
                ? _ctx.KnownFolder(KnownFolders.Desktop) ?? throw new InvalidOperationException("Nu găsesc Desktop-ul utilizatorului.")
                : dest;
            Log.Info($"Primesc {entries.Count} elemente în {target}");
            await ReceiveSetAsync(setId, entries, target, null, _cts.Token);
            _ch.Send(MessageType.FileUploadResult, new PacketWriter().U32(setId).Bool(true).Str("").Str(target));
        }
        catch (Exception ex)
        {
            Log.Warn("Primirea fișierelor a eșuat", ex);
            _ch.Send(MessageType.FileUploadResult, new PacketWriter().U32(setId).Bool(false).Str(Truncate(ex.Message)).Str(""));
        }
    }

    private void ServeDirList(uint reqId, string path)
    {
        try
        {
            string normalized;
            List<DirItem> items;
            if (string.IsNullOrEmpty(path))
            {
                normalized = "";
                items = Roots();
            }
            else
            {
                normalized = Path.GetFullPath(path);
                items = _ctx.Run(() => ListFolder(normalized));
            }
            _ch.Send(MessageType.DirListResponse, new PacketWriter().U32(reqId).Bool(true).Str("").Str(normalized).WriteDirItems(items));
        }
        catch (Exception ex)
        {
            _ch.Send(MessageType.DirListResponse, new PacketWriter().U32(reqId).Bool(false).Str(Truncate(ex.Message)).Str(path).WriteDirItems([]));
        }
    }

    private List<DirItem> Roots()
    {
        var items = new List<DirItem>();
        foreach (var (name, id) in new[] { ("Desktop", KnownFolders.Desktop), ("Descărcări", KnownFolders.Downloads), ("Documente", KnownFolders.Documents) })
            if (_ctx.KnownFolder(id) is { } p) items.Add(new DirItem(name, p, DirItemKind.Place, 0, 0));
        foreach (var d in _ctx.Run(DriveInfo.GetDrives))
        {
            try
            {
                if (!d.IsReady) continue;
                var letter = d.Name.TrimEnd('\\');
                var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "Unitate locală" : d.VolumeLabel;
                items.Add(new DirItem($"{label} ({letter})", d.RootDirectory.FullName, DirItemKind.Drive, d.TotalFreeSpace, 0));
            }
            catch { }
        }
        return items;
    }

    private static List<DirItem> ListFolder(string path)
    {
        var di = new DirectoryInfo(path);
        if (!di.Exists) throw new DirectoryNotFoundException("Folderul nu există: " + path);
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };
        var dirs = di.EnumerateDirectories("*", options).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(d => new DirItem(d.Name, d.FullName, DirItemKind.Directory, 0, d.LastWriteTimeUtc.Ticks));
        var files = di.EnumerateFiles("*", options).OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(f => new DirItem(f.Name, f.FullName, DirItemKind.File, f.Length, f.LastWriteTimeUtc.Ticks));
        return [.. dirs, .. files];
    }

    private void ServeDownload(uint reqId, List<string> paths)
    {
        try
        {
            var files = _ctx.Run(() => FileSetBuilder.Build(paths));
            var setId = Offer(files);
            Log.Info($"Ofer {files.Count} elemente pentru descărcare");
            _ch.Send(MessageType.DownloadResponse, new PacketWriter().U32(reqId).Bool(true).Str("").U32(setId)
                .WriteEntries(files.Select(f => f.Entry).ToList()));
        }
        catch (Exception ex)
        {
            _ch.Send(MessageType.DownloadResponse, new PacketWriter().U32(reqId).Bool(false).Str(Truncate(ex.Message)).U32(0).WriteEntries([]));
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var r in _incoming.Values) r.Fail("Sesiunea s-a închis.");
        foreach (var t in _uploads.Values) t.TrySetResult(new UploadResult(false, "Sesiunea s-a închis.", null));
        foreach (var t in _dirLists.Values) t.TrySetException(new IOException("Sesiunea s-a închis."));
        foreach (var t in _downloads.Values) t.TrySetException(new IOException("Sesiunea s-a închis."));
        lock (_setsLock)
        {
            foreach (var s in _sets.Values) s.Cts.Cancel();
            _sets.Clear();
        }
    }
}

/// <summary>Fluxul de date al unui fișier primit de la cealaltă parte. Citirea blochează până sosesc datele.</summary>
public sealed class FileStreamReader : IDisposable
{
    private readonly FileTransferChannel _owner;
    private readonly Channel<(byte[]? Data, bool Eof, string? Error)> _chunks =
        Channel.CreateUnbounded<(byte[]?, bool, string?)>(new UnboundedChannelOptions { SingleReader = true });
    private byte[]? _current;
    private int _pos;
    private bool _eof;
    private long _consumed, _acked;
    private bool _disposed;

    internal FileStreamReader(FileTransferChannel owner, uint streamId)
    {
        _owner = owner;
        StreamId = streamId;
    }

    public uint StreamId { get; }

    internal void Push(byte[] data, bool eof) => _chunks.Writer.TryWrite((data, eof, null));

    internal void Fail(string message) => _chunks.Writer.TryWrite((null, true, message));

    public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct = default)
    {
        while (true)
        {
            if (_current != null && _pos < _current.Length)
            {
                int n = Math.Min(destination.Length, _current.Length - _pos);
                _current.AsMemory(_pos, n).CopyTo(destination);
                _pos += n;
                _consumed += n;
                if (_consumed - _acked >= 1024 * 1024)
                {
                    _acked = _consumed;
                    _owner.SendAck(StreamId, _consumed);
                }
                return n;
            }
            if (_eof) return 0;

            var (data, eof, error) = await _chunks.Reader.ReadAsync(ct);
            if (error != null)
            {
                _eof = true;
                throw new IOException(error);
            }
            _current = data;
            _pos = 0;
            _eof = eof;
        }
    }

    /// <summary>Oprește fluxul: citirea în curs (dacă există) eșuează imediat, iar partea cealaltă nu mai trimite.</summary>
    public void Abort(string message)
    {
        Fail(message);
        Dispose();
    }

    /// <summary>Variantă sincronă (pentru IStream-ul COM folosit de clipboard).</summary>
    public int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        bool finished = _eof && (_current == null || _pos >= _current.Length);
        _owner.OnReaderDisposed(this, finished);
    }
}
