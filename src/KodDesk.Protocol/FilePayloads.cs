namespace KodDesk.Protocol;

/// <summary>Un element dintr-un set de fișiere oferit celeilalte părți (cale relativă, cu „\” ca separator).</summary>
public sealed record FileEntry(string RelativePath, bool IsDirectory, long Size, long LastWriteUtcTicks);

public enum DirItemKind : byte
{
    File = 0,
    Directory = 1,
    Drive = 2,
    /// <summary>Loc rapid: Desktop, Descărcări, Documente.</summary>
    Place = 3,
}

/// <summary>Element afișat în exploratorul de fișiere la distanță.</summary>
public sealed record DirItem(string Name, string FullPath, DirItemKind Kind, long Size, long LastWriteUtcTicks);

public static class FilePayloads
{
    public static PacketWriter WriteEntries(this PacketWriter w, IReadOnlyList<FileEntry> entries)
    {
        w.U32((uint)entries.Count);
        foreach (var e in entries)
            w.Str(e.RelativePath).Bool(e.IsDirectory).I64(e.Size).I64(e.LastWriteUtcTicks);
        return w;
    }

    public static List<FileEntry> ReadEntries(ref PacketReader r)
    {
        int n = (int)r.U32();
        if (n > 1_000_000) throw new InvalidDataException("Prea multe fișiere");
        var list = new List<FileEntry>(n);
        for (int i = 0; i < n; i++)
            list.Add(new FileEntry(r.Str(), r.Bool(), r.I64(), r.I64()));
        return list;
    }

    public static PacketWriter WriteDirItems(this PacketWriter w, IReadOnlyList<DirItem> items)
    {
        w.U32((uint)items.Count);
        foreach (var i in items)
            w.Str(i.Name).Str(i.FullPath).U8((byte)i.Kind).I64(i.Size).I64(i.LastWriteUtcTicks);
        return w;
    }

    public static List<DirItem> ReadDirItems(ref PacketReader r)
    {
        int n = (int)r.U32();
        if (n > 1_000_000) throw new InvalidDataException("Prea multe elemente");
        var list = new List<DirItem>(n);
        for (int i = 0; i < n; i++)
            list.Add(new DirItem(r.Str(), r.Str(), (DirItemKind)r.U8(), r.I64(), r.I64()));
        return list;
    }
}
