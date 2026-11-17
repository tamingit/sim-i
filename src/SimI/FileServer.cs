// The IBM i file server (as-file, server ID E002, port 8473; 9473 for TLS): the integrated file
// system by path, as JTOpen's IFSFile streams and ACS reach it. It signs a client on as the other
// host servers do (random seeds, then start server), then answers:
//
//   22 exchange attributes -> 8009: datastream level 24, path names in CCSID 1200 (UTF-16)
//    2 open                -> 8002: a handle, the file's dates and its size (64-bit at byte 89)
//    3 read                -> 8003: the data as code point 0005; none at the end of the file
//    9 close               -> 8004
//   10 list attributes     -> a chain of 8005, one per entry of a folder that matches the
//                             pattern (folder/*), then return code 18 (no more files); return
//                             code 2 alone when nothing matches (an empty folder)
//
// Anything it refuses is reply 8001 with a return code at byte 22: 2 no such file, 3 no such
// folder, 4 a folder opened as a file, 6 no such handle, 13 not authorised (a path out of the IFS),
// 23 a request it doesn't support. Each layout was checked against a real IBM i (datastream level
// 24): the replies are the ones it sent, byte for byte where they matter to a client. Files open
// for reading only; writing and creating aren't supported yet (23).
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

    public const int FileNotFound = 2, PathNotFound = 3, NotAFile = 4, InvalidHandle = 6, ResourceLimit = 11,
        AccessDenied = 13, NoMoreFiles = 18, NotSupported = 23;

    private const ushort Exchange = 22, Open = 2, Read = 3, Close = 9, List = 10;
    private const ushort ReturnCodeReply = 0x8001, OpenReply = 0x8002, ReadReply = 0x8003, CloseReply = 0x8004, ListReply = 0x8005,
        ExchangeReply = 0x8009;

    private const ushort AccessRead = 1;

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
                var request = await ReadFrameAsync(stream, Stopping);
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
        if (request.Length < 32) throw new InvalidDataException("short open");
        var name = CodePoints(request, 20 + templateLength).GetValueOrDefault(0x0002);
        if (name is null || name.Length % 2 != 0) throw new InvalidDataException("open without a name");
        var path = Ifs.Absolute(Encoding.BigEndianUnicode.GetString(name));
        var access = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(30));
        var what = $"open {path}";
        if (access != AccessRead) return (ReturnCode(correlation, NotSupported), what + " for writing");

        string host;
        try
        {
            host = Ifs.HostPath(path);
        }
        catch (UnauthorizedAccessException)
        {
            return (ReturnCode(correlation, AccessDenied), what);
        }
        if (Directory.Exists(host)) return (ReturnCode(correlation, NotAFile), what);
        if (!File.Exists(host))
            return (ReturnCode(correlation, Directory.Exists(Path.GetDirectoryName(host)) ? FileNotFound : PathNotFound), what);
        if (open.Count >= MaxOpen) return (ReturnCode(correlation, ResourceLimit), what);

        FileStream file;
        try
        {
            file = new FileStream(host, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (UnauthorizedAccessException)
        {
            return (ReturnCode(correlation, AccessDenied), what);
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
        var replies = new List<byte[]>();
        foreach (var entry in new DirectoryInfo(host).EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            if (replies.Count >= most) break;
            if (!match.IsMatch(entry.Name)) continue;
            replies.Add(Entry(entry, correlation));
        }
        // After entries the chain ends with 18; with none (an empty folder, or nothing matched) the
        // IBM i answers 2 alone.
        var count = replies.Count;
        replies.Add(ReturnCode(correlation, count == 0 ? FileNotFound : NoMoreFiles));
        return (replies, $"{what}: {count} entries");
    }

    /// <summary>
    /// One entry, as the IBM i sends it (a 93-byte template): chained (1 at 20), the creation,
    /// modification and access dates at 22, 30 and 38, the size at 46 (32-bit) and 81 (64-bit),
    /// the fixed attributes at 50 (10 a folder, 20 a file), the object type at 54 (2 a folder, 1 a
    /// file), then the name as code point 0002 (UTF-16).
    /// </summary>
    private static byte[] Entry(FileSystemInfo entry, int correlation)
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
