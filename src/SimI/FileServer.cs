// The IBM i file server (as-file, server ID E002, port 8473; 9473 for TLS): the integrated file
// system by path, as JTOpen's IFSFile streams and ACS reach it. It signs a client on as the other
// host servers do (random seeds, then start server), then answers:
//
//   22 exchange attributes -> 8009: datastream level 24, path names in CCSID 1200 (UTF-16)
//    2 open                -> 8002: a handle, the file's dates and its size (64-bit at byte 89);
//                             for reading, writing or both, with the open option (create or open,
//                             create or replace, create but fail if there, open or fail, replace)
//    3 read                -> 8003: the data as code point 0005; none at the end of the file
//    4 write               -> 800B: the return code at 22, the length not written at 28
//    9 close               -> 8004
//   12 delete file, 13 create folder, 14 delete folder (only an empty one), 15 rename (never
//                             over a name in use) -> 8001 with return code 0, or a refusal
//   10 list attributes     -> a chain of 8005, one per entry of a folder that matches the
//                             pattern (folder/*), then return code 18 (no more files); return
//                             code 2 alone when nothing matches (an empty folder). Each entry has
//                             a restart ID at 77; a listing goes on after a restart ID (code point
//                             14) or name (7)
//
// Anything it refuses is reply 8001 with a return code at byte 22: 2 no such file, 3 no such
// folder, 4 a name already in use (or a folder opened as a file), 6 no such handle, 9 a folder
// that isn't empty, 13 not authorised (a path out of the IFS, a write into /QSYS.LIB, or writing
// through a handle opened for reading), 23 a request it doesn't support. The reading requests
// were checked against a real IBM i (datastream level 24): the replies are the ones it sent, byte
// for byte where they matter to a client. The writing requests follow IBM's Toolbox for Java's
// layouts, not yet checked against a real IBM i.
// It logs each request and path, never a file's contents.

using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace SimI;

public sealed class FileServer : HostServer
{
    public const ushort ServerId = 0xE002;

    /// <summary>The datastream level it answers with, as the IBM i it was checked against does.</summary>
    public const int DatastreamLevel = 24;

    /// <summary>The CCSID of path names: UTF-16, big-endian.</summary>
    public const int PathCcsid = 1200;

    /// <summary>The most one read sends back.</summary>
    public const int MaxRead = 4 << 20;

    /// <summary>The most files one connection keeps open.</summary>
    public const int MaxOpen = 256;

    public const int FileNotFound = 2, PathNotFound = 3, NotAFile = 4, AlreadyThere = 4, InvalidHandle = 6, NotEmpty = 9,
        ResourceLimit = 11, AccessDenied = 13, NoMoreFiles = 18, NotSupported = 23;

    /// <summary>The longest request it reads: a write's data comes in one, and the exchange offers 1 GiB blocks.</summary>
    public const int MaxFrame = 64 << 20;

    private const ushort Exchange = 22, Open = 2, Read = 3, Write = 4, Close = 9, List = 10, DeleteFile = 12, CreateFolder = 13,
        DeleteFolder = 14, Rename = 15;
    private const ushort ReturnCodeReply = 0x8001, OpenReply = 0x8002, ReadReply = 0x8003, CloseReply = 0x8004, ListReply = 0x8005,
        ExchangeReply = 0x8009, WriteReply = 0x800B;

    private const ushort AccessRead = 1, AccessWrite = 2, AccessReadWrite = 3;

    /// <summary>Open options: create or open, create or replace, create but fail if there, open but fail if not, replace but fail if not.</summary>
    private const ushort CreateOpen = 1, CreateReplace = 2, CreateFail = 4, FailOpen = 8, FailReplace = 16;

    private FileServer(int port, Action<string>? log, IPAddress? address, Users? users, TlsOptions? tls, Ifs ifs)
        : base(port, log, address, tls)
    {
        Users = users ?? Users.Defaults();
        Ifs = ifs;
    }

    /// <summary>
    /// Listens on the loopback interface, or on <paramref name="address"/>. Port 0 picks a free
    /// port (see <see cref="HostServer.Port"/>). It serves <paramref name="ifs"/>, and signs on the
    /// other servers' <paramref name="users"/> (the default is <see cref="Users.Defaults"/>).
    /// </summary>
    public static Task<FileServer> StartAsync(Ifs ifs, int port = 0, Action<string>? log = null, IPAddress? address = null,
        Users? users = null, TlsOptions? tls = null)
    {
        var server = new FileServer(port, log, address, users, tls, ifs);
        server.Start();
        return Task.FromResult(server);
    }

    public Users Users { get; }

    /// <summary>The integrated file system it serves.</summary>
    public Ifs Ifs { get; }

    protected override string LogName => "file";

    protected override string JobName(int id) => $"{JobNumber(id)}/QUSER/QPWFSERVSO";

    protected override async Task ServeAsync(Stream stream, int id, bool tls)
    {
        var profile = await StartServerAsync(stream, id, ServerId, Users);
        if (profile is null) return;
        var open = new Dictionary<int, FileStream>();
        var next = 1;
        try
        {
            while (true)
            {
                var request = await ReadFrameAsync(stream, Stopping, MaxFrame);
                if (ServerIdOf(request) != ServerId) throw new InvalidDataException("not a file server request");
                var correlation = CorrelationOf(request);
                byte[] reply;
                string what;
                if (RequestIdOf(request) == List)
                {
                    var (replies, listed) = ListFolder(request, correlation);
                    foreach (var each in replies) await stream.WriteAsync(each, Stopping);
                    Log($"file #{id} {listed}");
                    continue;
                }
                switch (RequestIdOf(request))
                {
                    case Exchange:
                        what = "exchange attributes";
                        reply = ExchangeAttributes(correlation);
                        break;
                    case Open:
                        (reply, what) = OpenFile(request, correlation, open, ref next);
                        break;
                    case Read:
                        (reply, what) = ReadFile(request, correlation, open);
                        break;
                    case Close:
                        (reply, what) = CloseFile(request, correlation, open);
                        break;
                    case Write:
                        (reply, what) = WriteFile(request, correlation, open);
                        break;
                    case CreateFolder:
                    case DeleteFolder:
                    case DeleteFile:
                        (reply, what) = ByName(request, correlation, RequestIdOf(request));
                        break;
                    case Rename:
                        (reply, what) = RenameEntry(request, correlation);
                        break;
                    default:
                        what = $"request {RequestIdOf(request)}";
                        reply = ReturnCode(correlation, NotSupported);
                        break;
                }
                await stream.WriteAsync(reply, Stopping);
                Log($"file #{id} {what}: {(RequestIdOf(reply) == ReturnCodeReply ? $"rc {BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(22))}" : "ok")}");
            }
        }
        finally
        {
            foreach (var file in open.Values) await file.DisposeAsync();
        }
    }

    /// <summary>Datastream level 24, a 1 GiB data block, and path names in CCSID 1200 (code point 000A), as the IBM i answers.</summary>
    private static byte[] ExchangeAttributes(int correlation)
    {
        var template = new byte[10];
        BinaryPrimitives.WriteUInt16BigEndian(template.AsSpan(2), DatastreamLevel);
        BinaryPrimitives.WriteInt32BigEndian(template.AsSpan(6), 0x40000000);
        return Frame(ServerId, ExchangeReply, template, correlation, 0, (0x000A, new byte[] { PathCcsid >> 8, PathCcsid & 0xFF }));
    }

    private (byte[] Reply, string What) OpenFile(byte[] request, int correlation, Dictionary<int, FileStream> open, ref int next)
    {
        var templateLength = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(16));
        if (request.Length < 38) throw new InvalidDataException("short open");
        var name = CodePoints(request, 20 + templateLength).GetValueOrDefault(0x0002);
        if (name is null || name.Length % 2 != 0) throw new InvalidDataException("open without a name");
        var path = Ifs.Absolute(Encoding.BigEndianUnicode.GetString(name));
        var access = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(30));
        var option = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(36));
        var what = $"open {path}{(access == AccessRead ? "" : " for writing")}";
        if (access is not (AccessRead or AccessWrite or AccessReadWrite)) return (ReturnCode(correlation, NotSupported), what);
        // Reading an existing file, the one way the earlier clients open: anything else may write.
        var writes = access != AccessRead || option is not (FailOpen or 0);

        var (host, refused) = Resolve(path, writes);
        if (host is null) return (ReturnCode(correlation, refused), what);
        if (Directory.Exists(host)) return (ReturnCode(correlation, NotAFile), what);
        if (!Directory.Exists(Path.GetDirectoryName(host))) return (ReturnCode(correlation, PathNotFound), what);
        var exists = File.Exists(host);
        if (exists && option == CreateFail) return (ReturnCode(correlation, AlreadyThere), what);
        if (!exists && (option is FailOpen or FailReplace or 0)) return (ReturnCode(correlation, FileNotFound), what);
        if (open.Count >= MaxOpen) return (ReturnCode(correlation, ResourceLimit), what);

        var replace = exists && option is CreateReplace or FailReplace;
        var mode = !exists ? FileMode.CreateNew : replace ? FileMode.Truncate : FileMode.Open;
        var rights = access switch
        {
            AccessWrite => FileAccess.Write,
            AccessReadWrite => FileAccess.ReadWrite,
            _ => replace ? FileAccess.ReadWrite : FileAccess.Read,
        };
        FileStream file;
        try
        {
            file = new FileStream(host, mode, rights, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (UnauthorizedAccessException)
        {
            return (ReturnCode(correlation, AccessDenied), what);
        }
        catch (IOException) when (mode == FileMode.CreateNew)
        {
            return (ReturnCode(correlation, AlreadyThere), what);
        }
        var handle = next++;
        open[handle] = file;
        var info = new FileInfo(host);

        // The template as the IBM i sends it at level 16 and up (77 bytes): the handle at 22, an
        // object ID at 26, the creation, modification and access dates at 38, 46 and 54 (seconds and
        // microseconds since 1970), the archive attribute at 66 and the size, 64-bit, at 89.
        var template = new byte[77];
        var t = template.AsSpan();
        BinaryPrimitives.WriteInt32BigEndian(t[2..], handle);
        BinaryPrimitives.WriteInt32BigEndian(t[6..], StringComparer.OrdinalIgnoreCase.GetHashCode(path) & 0x7FFFFFFF);
        BinaryPrimitives.WriteInt32BigEndian(t[10..], 1);
        Date(t[18..], info.CreationTimeUtc);
        Date(t[26..], info.LastWriteTimeUtc);
        Date(t[34..], info.LastAccessTimeUtc);
        BinaryPrimitives.WriteInt32BigEndian(t[46..], 0x20);
        BinaryPrimitives.WriteInt64BigEndian(t[69..], info.Length);
        return (Frame(ServerId, OpenReply, template, correlation), what);
    }

    /// <summary>
    /// The host path of an IFS path, or the return code that refuses it: 13 for a path that leads
    /// out of the IFS, or (when it would change something) one in /QSYS.LIB, whose libraries are
    /// in the database, not files.
    /// </summary>
    private (string? Host, int Refused) Resolve(string path, bool writes)
    {
        if (writes && Ifs.InQsys(path)) return (null, AccessDenied);
        try
        {
            return (Ifs.HostPath(path), 0);
        }
        catch (UnauthorizedAccessException)
        {
            return (null, AccessDenied);
        }
    }

    /// <summary>A name sent as code point <paramref name="codePoint"/> after the template, as an absolute IFS path.</summary>
    private static string NameIn(byte[] request, ushort codePoint)
    {
        var templateLength = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(16));
        var name = CodePoints(request, 20 + templateLength).GetValueOrDefault(codePoint);
        if (name is null || name.Length % 2 != 0) throw new InvalidDataException($"no name (code point {codePoint})");
        return Ifs.Absolute(Encoding.BigEndianUnicode.GetString(name));
    }

    /// <summary>
    /// Write: the handle at 22, the offset 64-bit at 46 (a 34-byte template) or 32-bit at 30, the
    /// bytes as code point 0020 after the template. Reply 800B: return code at 22, not written at 28.
    /// </summary>
    private static (byte[] Reply, string What) WriteFile(byte[] request, int correlation, Dictionary<int, FileStream> open)
    {
        if (request.Length < 38) throw new InvalidDataException("short write");
        var handle = BinaryPrimitives.ReadInt32BigEndian(request.AsSpan(22));
        var what = $"write handle {handle}";
        if (!open.TryGetValue(handle, out var file)) return (ReturnCode(correlation, InvalidHandle), what);
        if (!file.CanWrite) return (ReturnCode(correlation, AccessDenied), what + ": opened for reading");
        var templateLength = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(16));
        var offset = templateLength >= 34 && request.Length >= 54
            ? BinaryPrimitives.ReadInt64BigEndian(request.AsSpan(46))
            : BinaryPrimitives.ReadUInt32BigEndian(request.AsSpan(30));
        var data = CodePoints(request, 20 + templateLength).GetValueOrDefault(0x0020) ?? [];
        if (offset < 0) throw new InvalidDataException("negative offset");
        file.Position = offset;
        file.Write(data);
        file.Flush();
        var template = new byte[12];
        return (Frame(ServerId, WriteReply, template, correlation), $"{what}: {data.Length} bytes at {offset}");
    }

    /// <summary>Create folder (13, name as code point 1), delete folder (14, 1) and delete file (12, 2): return code 0, or why not.</summary>
    private (byte[] Reply, string What) ByName(byte[] request, int correlation, ushort requestId)
    {
        var path = NameIn(request, requestId == DeleteFile ? (ushort)2 : (ushort)1);
        var what = requestId switch { CreateFolder => "create folder", DeleteFolder => "delete folder", _ => "delete file" } + $" {path}";
        var (host, refused) = Resolve(path, writes: true);
        if (host is null) return (ReturnCode(correlation, refused), what);
        var parent = Directory.Exists(Path.GetDirectoryName(host));
        try
        {
            switch (requestId)
            {
                case CreateFolder:
                    if (File.Exists(host) || Directory.Exists(host)) return (ReturnCode(correlation, AlreadyThere), what);
                    if (!parent) return (ReturnCode(correlation, PathNotFound), what);
                    Directory.CreateDirectory(host);
                    break;
                case DeleteFolder:
                    if (!Directory.Exists(host)) return (ReturnCode(correlation, File.Exists(host) ? NotAFile : PathNotFound), what);
                    if (Directory.EnumerateFileSystemEntries(host).Any()) return (ReturnCode(correlation, NotEmpty), what);
                    Directory.Delete(host);
                    break;
                default:
                    if (Directory.Exists(host)) return (ReturnCode(correlation, NotAFile), what);
                    if (!File.Exists(host)) return (ReturnCode(correlation, parent ? FileNotFound : PathNotFound), what);
                    File.Delete(host);
                    break;
            }
        }
        catch (UnauthorizedAccessException)
        {
            return (ReturnCode(correlation, AccessDenied), what);
        }
        return (ReturnCode(correlation, 0), what);
    }

    /// <summary>Rename (15): the old name as code point 3, the new as 4; a name in use is refused (4), never replaced.</summary>
    private (byte[] Reply, string What) RenameEntry(byte[] request, int correlation)
    {
        var from = NameIn(request, 3);
        var to = NameIn(request, 4);
        var what = $"rename {from} to {to}";
        var (source, refusedFrom) = Resolve(from, writes: true);
        if (source is null) return (ReturnCode(correlation, refusedFrom), what);
        var (target, refusedTo) = Resolve(to, writes: true);
        if (target is null) return (ReturnCode(correlation, refusedTo), what);
        var isFolder = Directory.Exists(source);
        if (!isFolder && !File.Exists(source))
            return (ReturnCode(correlation, Directory.Exists(Path.GetDirectoryName(source)) ? FileNotFound : PathNotFound), what);
        // A new name that differs only in case is the same entry, renamed.
        var sameEntry = string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase);
        if (!sameEntry && (File.Exists(target) || Directory.Exists(target))) return (ReturnCode(correlation, AlreadyThere), what);
        if (!Directory.Exists(Path.GetDirectoryName(target))) return (ReturnCode(correlation, PathNotFound), what);
        try
        {
            if (isFolder) Directory.Move(source, target);
            else File.Move(source, target);
        }
        catch (UnauthorizedAccessException)
        {
            return (ReturnCode(correlation, AccessDenied), what);
        }
        return (ReturnCode(correlation, 0), what);
    }

    private static (byte[] Reply, string What) ReadFile(byte[] request, int correlation, Dictionary<int, FileStream> open)
    {
        if (request.Length < 42) throw new InvalidDataException("short read");
        var handle = BinaryPrimitives.ReadInt32BigEndian(request.AsSpan(22));
        var what = $"read handle {handle}";
        if (!open.TryGetValue(handle, out var file)) return (ReturnCode(correlation, InvalidHandle), what);
        // Level 16 and up: a 38-byte template with a 64-bit offset at 50; below, 22 bytes with 32 bits at 30.
        var offset = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(16)) >= 38 && request.Length >= 58
            ? BinaryPrimitives.ReadInt64BigEndian(request.AsSpan(50))
            : BinaryPrimitives.ReadUInt32BigEndian(request.AsSpan(30));
        var length = Math.Clamp(BinaryPrimitives.ReadInt32BigEndian(request.AsSpan(34)), 0, MaxRead);
        var data = Array.Empty<byte>();
        if (offset >= 0 && offset < file.Length && length > 0)
        {
            data = new byte[(int)Math.Min(length, file.Length - offset)];
            file.Position = offset;
            file.ReadExactly(data);
        }
        // Not chained, and binary (CCSID 65535).
        return (Frame(ServerId, ReadReply, [0, 0, 0xFF, 0xFF], correlation, 0, (0x0005, data)), $"{what}: {data.Length} bytes at {offset}");
    }

    /// <summary>
    /// List attributes by name: the pattern is a folder and a name that may hold * and ?, found
    /// whatever its case (folder/* lists the folder). Each entry is a reply of its own, chained,
    /// then return code 18 ends the chain; the most entries at byte 34 is honoured (FFFF: no limit).
    /// </summary>
    private (List<byte[]> Replies, string What) ListFolder(byte[] request, int correlation)
    {
        if (request.Length < 46) throw new InvalidDataException("short list");
        var templateLength = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(16));
        var name = CodePoints(request, 20 + templateLength).GetValueOrDefault(0x0002);
        if (name is null || name.Length % 2 != 0) throw new InvalidDataException("list without a name");
        var pattern = Encoding.BigEndianUnicode.GetString(name);
        var most = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(34)) is var m and > 0 and < 0xFFFF ? m : int.MaxValue;
        var cut = pattern.LastIndexOf('/');
        var folder = Ifs.Absolute(cut <= 0 ? "/" : pattern[..cut]);
        var glob = pattern[(cut + 1)..] is { Length: > 0 } g ? g : "*";
        var what = $"list {folder}";

        string host;
        try
        {
            host = Ifs.HostPath(folder);
        }
        catch (UnauthorizedAccessException)
        {
            return ([ReturnCode(correlation, AccessDenied)], what + ": rc 13");
        }
        if (!Directory.Exists(host)) return ([ReturnCode(correlation, PathNotFound)], what + ": rc 3");

        var match = new System.Text.RegularExpressions.Regex(
            "^" + System.Text.RegularExpressions.Regex.Escape(glob).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        // Where an earlier page ended: a restart ID (code point 14, an entry's position in this
        // listing) or a restart name (7). The listing goes on after it.
        var points = CodePoints(request, 20 + templateLength);
        var after = points.TryGetValue(14, out var id) && id.Length == 4 ? BinaryPrimitives.ReadInt32BigEndian(id) : 0;
        var afterName = after == 0 && points.TryGetValue(7, out var restart) && restart.Length % 2 == 0
            ? Encoding.BigEndianUnicode.GetString(restart)
            : null;
        var replies = new List<byte[]>();
        var position = 0;
        foreach (var entry in new DirectoryInfo(host).EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            if (!match.IsMatch(entry.Name)) continue;
            position++;
            if (position <= after || (afterName is not null && string.CompareOrdinal(entry.Name, afterName) <= 0)) continue;
            if (replies.Count >= most) break;
            replies.Add(Entry(entry, correlation, position));
        }
        // After entries the chain ends with 18; with none (an empty folder, or nothing matched) the
        // IBM i answers 2 alone.
        var count = replies.Count;
        replies.Add(ReturnCode(correlation, count == 0 ? FileNotFound : NoMoreFiles));
        return (replies, $"{what}: {count} entries");
    }

    /// <summary>
    /// One entry, as the IBM i sends it (a 93-byte template): chained (1 at 20), the creation,
    /// modification and access dates at 22, 30 and 38, the restart ID at 77 (its position in the
    /// listing), the size at 46 (32-bit) and 81 (64-bit),
    /// the fixed attributes at 50 (10 a folder, 20 a file), the object type at 54 (2 a folder, 1 a
    /// file), then the name as code point 0002 (UTF-16).
    /// </summary>
    private static byte[] Entry(FileSystemInfo entry, int correlation, int position)
    {
        var folder = entry is DirectoryInfo;
        var size = entry is FileInfo file ? file.Length : 0;
        var template = new byte[93];
        var t = template.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(t, 1);
        Date(t[2..], entry.CreationTimeUtc);
        Date(t[10..], entry.LastWriteTimeUtc);
        Date(t[18..], entry.LastAccessTimeUtc);
        BinaryPrimitives.WriteUInt32BigEndian(t[26..], (uint)Math.Min(size, uint.MaxValue));
        BinaryPrimitives.WriteInt32BigEndian(t[30..], folder ? 0x10 : 0x20);
        BinaryPrimitives.WriteUInt16BigEndian(t[34..], folder ? (ushort)2 : (ushort)1);
        // The restart ID at 77: where a listing can go on from.
        BinaryPrimitives.WriteInt32BigEndian(t[57..], position);
        BinaryPrimitives.WriteInt64BigEndian(t[61..], size);
        return Frame(ServerId, ListReply, template, correlation, 0, (0x0002, Encoding.BigEndianUnicode.GetBytes(entry.Name)));
    }

    private static (byte[] Reply, string What) CloseFile(byte[] request, int correlation, Dictionary<int, FileStream> open)
    {
        if (request.Length < 26) throw new InvalidDataException("short close");
        var handle = BinaryPrimitives.ReadInt32BigEndian(request.AsSpan(22));
        var what = $"close handle {handle}";
        if (!open.Remove(handle, out var file)) return (ReturnCode(correlation, InvalidHandle), what);
        file.Dispose();
        return (Frame(ServerId, CloseReply, new byte[8], correlation), what);
    }

    /// <summary>A refusal: reply 8001, the return code at byte 22.</summary>
    private static byte[] ReturnCode(int correlation, int code) =>
        Frame(ServerId, ReturnCodeReply, [0, 0, (byte)(code >> 8), (byte)code], correlation);

    private static void Date(Span<byte> at, DateTime utc)
    {
        var since = utc - DateTime.UnixEpoch;
        var seconds = Math.Max(0, (long)since.TotalSeconds);
        BinaryPrimitives.WriteUInt32BigEndian(at, (uint)Math.Min(seconds, uint.MaxValue));
        BinaryPrimitives.WriteInt32BigEndian(at[4..], (int)(since.Ticks % TimeSpan.TicksPerSecond / 10));
    }
}
