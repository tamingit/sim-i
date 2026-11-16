// The integrated file system: a folder on the host is its root ("/"). Like the IFS's root file
// system, names are found whatever their case, and kept as they were made. A path never leads out
// of the folder, through .. or a symbolic link. /QSYS.LIB paths aren't files here: they name
// libraries and members, which the Machine looks after.

namespace SimI;

public sealed class Ifs
{
    public Ifs(string root)
    {
        Directory.CreateDirectory(root);
        Root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
    }

    /// <summary>The host folder that is "/".</summary>
    public string Root { get; }

    /// <summary>An absolute IFS path, with . and .. done (.. at the root stays there), from a path that may be relative to <paramref name="current"/>.</summary>
    public static string Absolute(string path, string current = "/")
    {
        path = path.Trim().Replace('\\', '/');
        var parts = new List<string>();
        foreach (var part in ((path.StartsWith('/') ? "" : current.TrimEnd('/') + "/") + path).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(part);
        }
        return "/" + string.Join('/', parts);
    }

    /// <summary>Whether the path is in /QSYS.LIB, the libraries and members.</summary>
    public static bool InQsys(string absolute) =>
        absolute.Equals("/QSYS.LIB", StringComparison.OrdinalIgnoreCase) || absolute.StartsWith("/QSYS.LIB/", StringComparison.OrdinalIgnoreCase);

    /// <summary>A /QSYS.LIB path's library, file and member: /QSYS.LIB/MYLIB.LIB/QRPGLESRC.FILE/HELLO.MBR. Null when it isn't one.</summary>
    public static (string Library, string File, string Member)? QsysMember(string absolute)
    {
        var parts = absolute.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(p => p.ToUpperInvariant()).ToList();
        if (parts is not ["QSYS.LIB", var lib, var file, var member]) return null;
        if (!lib.EndsWith(".LIB") || !file.EndsWith(".FILE") || !member.EndsWith(".MBR")) return null;
        return (lib[..^4], file[..^5], member[..^4]);
    }

    /// <summary>
    /// The host path of an IFS path: each name that's there is found whatever its case, and the rest
    /// are kept as written. Throws <see cref="UnauthorizedAccessException"/> for a path that would
    /// lead out of the root (through a symbolic link).
    /// </summary>
    public string HostPath(string path, string current = "/")
    {
        var absolute = Absolute(path, current);
        var host = Root;
        foreach (var part in absolute.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var exact = Path.Combine(host, part);
            if (!Path.Exists(exact) && Directory.Exists(host))
            {
                var match = Directory.EnumerateFileSystemEntries(host).FirstOrDefault(e => Path.GetFileName(e).Equals(part, StringComparison.OrdinalIgnoreCase));
                if (match is not null) exact = match;
            }
            host = exact;
            Confine(host);
        }
        return host;
    }

    /// <summary>Throws when the host path, or what a link there points to, is outside the root.</summary>
    private void Confine(string host)
    {
        var full = Path.GetFullPath(host);
        if (!Inside(full)) throw new UnauthorizedAccessException("The path leads out of the integrated file system.");
        var info = new FileInfo(full);
        if (info.Exists || Directory.Exists(full))
        {
            FileSystemInfo entry = info.Exists ? info : new DirectoryInfo(full);
            if (entry.LinkTarget is not null && entry.ResolveLinkTarget(returnFinalTarget: true) is { } target && !Inside(Path.GetFullPath(target.FullName)))
                throw new UnauthorizedAccessException("The path leads out of the integrated file system.");
        }
    }

    private bool Inside(string full) =>
        full == Root || full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    public bool FileExists(string path, string current = "/") => File.Exists(HostPath(path, current));

    public bool DirectoryExists(string path, string current = "/") => Directory.Exists(HostPath(path, current));

    /// <summary>A stream file's text (UTF-8), or null when there's no such file.</summary>
    public string? ReadText(string path, string current = "/")
    {
        var host = HostPath(path, current);
        return File.Exists(host) ? File.ReadAllText(host).Replace("\r\n", "\n") : null;
    }

    /// <summary>Writes a stream file's text, replacing it. Its directory has to be there.</summary>
    public void WriteText(string path, string text, string current = "/")
    {
        var host = HostPath(path, current);
        if (!Directory.Exists(Path.GetDirectoryName(host))) throw new DirectoryNotFoundException($"The directory of {Absolute(path, current)} isn't there.");
        File.WriteAllText(host, text);
    }

    /// <summary>The entries of a directory, as IFS paths, directories first; null when there's no such directory.</summary>
    public IReadOnlyList<(string Name, bool Directory, long Size, DateTime Changed)>? List(string path, string current = "/")
    {
        var host = HostPath(path, current);
        if (!Directory.Exists(host)) return null;
        return [.. new DirectoryInfo(host).EnumerateFileSystemInfos()
            .Select(e => (e.Name, e is DirectoryInfo, e is FileInfo f ? f.Length : 0L, e.LastWriteTime))
            .OrderBy(e => e.Item2 ? 0 : 1).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];
    }
}
