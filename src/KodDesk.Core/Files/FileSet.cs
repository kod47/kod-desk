using KodDesk.Protocol;

namespace KodDesk.Core.Files;

/// <summary>Un element dintr-un set oferit: descrierea trimisă celeilalte părți + calea locală.</summary>
public sealed record OfferedFile(FileEntry Entry, string? FullPath);

public static class FileSetBuilder
{
    private const int MaxEntries = 200_000;

    /// <summary>
    /// Construiește setul pentru fișierele și folderele date. Folderele se parcurg recursiv; fiecare folder apare
    /// înaintea conținutului său. Căile relative încep cu numele elementului selectat.
    /// </summary>
    public static List<OfferedFile> Build(IEnumerable<string> paths)
    {
        var result = new List<OfferedFile>();
        foreach (var raw in paths)
        {
            var path = Path.GetFullPath(raw).TrimEnd('\\');
            var name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(name)) name = path.Replace(":", "").Replace("\\", ""); // rădăcina unei unități
            if (Directory.Exists(path))
                AddDirectory(result, path, name);
            else if (File.Exists(path))
                AddFile(result, new FileInfo(path), name);
        }
        return result;
    }

    private static void AddFile(List<OfferedFile> result, FileInfo fi, string rel)
    {
        Check(result);
        result.Add(new OfferedFile(new FileEntry(rel, false, fi.Length, fi.LastWriteTimeUtc.Ticks), fi.FullName));
    }

    private static void AddDirectory(List<OfferedFile> result, string dir, string rel)
    {
        Check(result);
        var di = new DirectoryInfo(dir);
        result.Add(new OfferedFile(new FileEntry(rel, true, 0, di.LastWriteTimeUtc.Ticks), di.FullName));

        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var sub in di.EnumerateDirectories("*", options).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            AddDirectory(result, sub.FullName, rel + "\\" + sub.Name);
        foreach (var f in di.EnumerateFiles("*", options).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            AddFile(result, f, rel + "\\" + f.Name);
    }

    private static void Check(List<OfferedFile> result)
    {
        if (result.Count >= MaxEntries) throw new InvalidOperationException($"Prea multe fișiere (maxim {MaxEntries}).");
    }
}

public static class SafePath
{
    private static readonly char[] s_invalid = Path.GetInvalidFileNameChars();

    /// <summary>
    /// Combină folderul țintă cu o cale relativă primită de la cealaltă parte, refuzând orice ar ieși din folder
    /// (căi absolute, „..”, nume invalide, fluxuri alternative „:”).
    /// </summary>
    public static string Combine(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            throw new InvalidDataException("Cale invalidă: " + relative);
        var parts = relative.Split('\\', '/');
        foreach (var p in parts)
        {
            if (p.Length == 0 || p == "." || p == ".." || p.IndexOfAny(s_invalid) >= 0 || p.EndsWith(' ') || p.EndsWith('.'))
                throw new InvalidDataException("Cale invalidă: " + relative);
        }
        var rootFull = Path.GetFullPath(root).TrimEnd('\\') + "\\";
        var full = Path.GetFullPath(Path.Combine(rootFull, string.Join('\\', parts)));
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Cale invalidă: " + relative);
        return full;
    }

    /// <summary>„nume.ext” -> „nume (2).ext” până găsește un nume liber.</summary>
    public static string Unique(string path, Func<string, bool> exists)
    {
        if (!exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!exists(candidate)) return candidate;
        }
    }
}
