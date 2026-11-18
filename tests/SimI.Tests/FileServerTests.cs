using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SimI;

namespace SimI.Tests;

/// <summary>
/// The file server (as-file), driven the way JTOpen's IFSFileInputStream reads a file, with the
/// layouts checked against a real IBM i at datastream level 24.
/// </summary>
public sealed class FileServerTests : IDisposable
{
    private static readonly byte[] ClientSeed = Convert.FromHexString("0102030405060708");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sim-i-file-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private Ifs NewIfs() => new(_root);

    private static int Int(byte[] b, int at) => BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(at));
    private static ushort Short(byte[] b, int at) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(at));
    private static int ReturnCode(byte[] reply) => Short(reply, 18) == 0x8001 ? Short(reply, 22) : 0;

    [Fact]
    public async Task ExchangeAnswersAsTheIbmIDid()
    {
        await using var server = await FileServer.StartAsync(NewIfs());
        await using var client = await Client.SignOnAsync(server.Port);
        var reply = await client.SendAsync(Exchange());
        // What a real IBM i (datastream level 24) answered to the same request, correlation 1.
        Assert.Equal("000000260000E0020000000000000001000A80090000001800004000000000000008000A04B0", Convert.ToHexString(reply));
    }

    [Fact]
    public async Task AFileReadsBackWholeThenTheEndAndClose()
    {
        var ifs = NewIfs();
        var data = new byte[150_000];
        new Random(5).NextBytes(data);
        Directory.CreateDirectory(ifs.HostPath("/home/prtuser"));
        await File.WriteAllBytesAsync(ifs.HostPath("/home/prtuser/data.bin"), data);
        await using var server = await FileServer.StartAsync(ifs);
        await using var client = await Client.SignOnAsync(server.Port);
        await client.SendAsync(Exchange());

        // Names are found whatever their case, as in the IFS's root file system.
        var opened = await client.SendAsync(Open("/HOME/PrtUser/Data.BIN"));
        Assert.Equal(0x8002, Short(opened, 18));
        Assert.Equal(97, opened.Length);
        Assert.Equal(77, Short(opened, 16));
        var handle = Int(opened, 22);
        Assert.Equal(data.Length, BinaryPrimitives.ReadInt64BigEndian(opened.AsSpan(89)));
        Assert.True(Int(opened, 46) > 0, "the modification date is set");

        var copy = new MemoryStream();
        while (true)
        {
            var read = await client.SendAsync(Read(handle, copy.Length, 65536));
            Assert.Equal(0x8003, Short(read, 18));
            Assert.Equal(0, Short(read, 20));
            Assert.Equal(0xFFFF, Short(read, 22));
            Assert.Equal(5, Short(read, 28));
            var length = Int(read, 24) - 6;
            if (length == 0) break;
            copy.Write(read, 30, length);
        }
        Assert.Equal(data, copy.ToArray());

        var closed = await client.SendAsync(Close(handle));
        // What a real IBM i answered to a close, correlation 5.
        Assert.Equal("0000001C0000E0020000000000000005000880040000000000000000", Convert.ToHexString(closed));
        Assert.Equal(6, ReturnCode(await client.SendAsync(Read(handle, 0, 16))));
        Assert.Equal(6, ReturnCode(await client.SendAsync(Close(handle))));
    }

    [Fact]
    public async Task RefusalsHaveTheIbmIsReturnCodes()
    {
        var ifs = NewIfs();
        Directory.CreateDirectory(ifs.HostPath("/tmp/folder"));
        await File.WriteAllTextAsync(ifs.HostPath("/tmp/note.txt"), "placeholder");
        var outside = Path.Combine(Path.GetTempPath(), "sim-i-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "placeholder");
        Directory.CreateSymbolicLink(ifs.HostPath("/escape"), outside);
        try
        {
            await using var server = await FileServer.StartAsync(ifs);
            await using var client = await Client.SignOnAsync(server.Port);
            await client.SendAsync(Exchange());

            Assert.Equal(3, ReturnCode(await client.SendAsync(Open("/no-such-folder/x.txt"))));
            Assert.Equal(2, ReturnCode(await client.SendAsync(Open("/tmp/no-such-file.txt"))));
            Assert.Equal(4, ReturnCode(await client.SendAsync(Open("/tmp/folder"))));
            Assert.Equal(13, ReturnCode(await client.SendAsync(Open("/escape/secret.txt"))));
            // Writing: a file already there isn't created again; /QSYS.LIB's libraries aren't files.
            Assert.Equal(4, ReturnCode(await client.SendAsync(Open("/tmp/note.txt", access: 2, option: 4))));
            Assert.Equal(13, ReturnCode(await client.SendAsync(Open("/QSYS.LIB/x.txt", access: 2, option: 4))));
            Assert.Equal(23, ReturnCode(await client.SendAsync(Frame(18, new byte[14]))));
            Assert.Equal(13, ReturnCode(Assert.Single(await client.ListAsync("/escape/*"))));
            Assert.Equal(6, ReturnCode(await client.SendAsync(Read(99, 0, 16))));

            // The connection still works after refusals.
            Assert.Equal(0x8002, Short(await client.SendAsync(Open("/tmp/note.txt")), 18));
        }
        finally
        {
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public async Task AFolderListsAsAChainOfEntriesThenNoMoreFiles()
    {
        var ifs = NewIfs();
        Directory.CreateDirectory(ifs.HostPath("/home/prtuser/Reports"));
        Directory.CreateDirectory(ifs.HostPath("/home/prtuser/empty"));
        await File.WriteAllBytesAsync(ifs.HostPath("/home/prtuser/data.bin"), new byte[1234]);
        await File.WriteAllTextAsync(ifs.HostPath("/home/prtuser/Note.TXT"), "placeholder");
        await using var server = await FileServer.StartAsync(ifs);
        await using var client = await Client.SignOnAsync(server.Port);
        await client.SendAsync(Exchange());

        var replies = await client.ListAsync("/home/prtuser/*");
        var entries = replies[..^1];
        Assert.Equal(4, entries.Count);
        Assert.All(entries, e =>
        {
            Assert.Equal(0x8005, Short(e, 18));
            Assert.Equal(93, Short(e, 16));
            Assert.Equal(1, Short(e, 20));
            Assert.Equal(2, Short(e, 117));
        });
        Assert.Equal(18, ReturnCode(replies[^1]));
        Assert.Equal(0, Short(replies[^1], 20));
        var byName = entries.ToDictionary(Name);
        Assert.Equal(["Note.TXT", "Reports", "data.bin", "empty"], byName.Keys.Order(StringComparer.Ordinal));
        Assert.Equal((2, 0x10), (Short(byName["Reports"], 54), Int(byName["Reports"], 50)));
        Assert.Equal((1, 0x20), (Short(byName["data.bin"], 54), Int(byName["data.bin"], 50)));
        Assert.Equal(1234, BinaryPrimitives.ReadInt64BigEndian(byName["data.bin"].AsSpan(81)));
        Assert.Equal(1234, Int(byName["data.bin"], 46));
        Assert.True(BinaryPrimitives.ReadUInt32BigEndian(byName["data.bin"].AsSpan(30)) > 1_600_000_000);

        // A pattern, whatever its case; the most entries; an empty folder; none there.
        Assert.Equal(["Note.TXT"], (await client.ListAsync("/HOME/prtuser/*.txt"))[..^1].Select(Name));
        Assert.Equal(2, (await client.ListAsync("/home/prtuser/*", most: 2)).Count - 1);
        // An empty folder is return code 2 alone, as the IBM i answers.
        Assert.Equal(2, ReturnCode(Assert.Single(await client.ListAsync("/home/prtuser/empty/*"))));
        Assert.Equal(2, ReturnCode(Assert.Single(await client.ListAsync("/home/prtuser/*.none"))));
        Assert.Equal(3, ReturnCode(Assert.Single(await client.ListAsync("/no-such-folder/*"))));
        Assert.Equal(0x8002, Short(await client.SendAsync(Open("/home/prtuser/data.bin")), 18));
    }

    private static string Name(byte[] entry) => Encoding.BigEndianUnicode.GetString(entry, 119, Int(entry, 113) - 6);

    [Fact]
    public async Task AFileIsCreatedWrittenReadBackAndReplaced()
    {
        var ifs = NewIfs();
        Directory.CreateDirectory(ifs.HostPath("/tmp"));
        await using var server = await FileServer.StartAsync(ifs);
        await using var client = await Client.SignOnAsync(server.Port);
        await client.SendAsync(Exchange());

        // Create, but fail if it's there (option 4); two writes, the second past 1 MiB in one request.
        var data = new byte[1_500_000];
        new Random(3).NextBytes(data);
        var created = await client.SendAsync(Open("/tmp/new.bin", access: 2, option: 4));
        Assert.Equal(0x8002, Short(created, 18));
        var handle = Int(created, 22);
        var first = await client.SendAsync(Write(handle, 0, data[..1000]));
        Assert.Equal((0x800B, 0, 0), (Short(first, 18), Short(first, 22), Int(first, 28)));
        Assert.Equal(0x800B, Short(await client.SendAsync(Write(handle, 1000, data[1000..])), 18));
        Assert.Equal(0x8004, Short(await client.SendAsync(Close(handle)), 18));
        Assert.Equal(data, await File.ReadAllBytesAsync(ifs.HostPath("/tmp/new.bin")));

        Assert.Equal(4, ReturnCode(await client.SendAsync(Open("/tmp/new.bin", access: 2, option: 4))));
        // Create or replace (2) empties it first.
        var replaced = await client.SendAsync(Open("/tmp/new.bin", access: 2, option: 2));
        Assert.Equal(0, BinaryPrimitives.ReadInt64BigEndian(replaced.AsSpan(89)));
        await client.SendAsync(Write(Int(replaced, 22), 0, [1, 2, 3]));
        await client.SendAsync(Close(Int(replaced, 22)));
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(ifs.HostPath("/tmp/new.bin")));

        // Not through a handle opened for reading; not into a folder that isn't there.
        var reading = await client.SendAsync(Open("/tmp/new.bin"));
        Assert.Equal(13, ReturnCode(await client.SendAsync(Write(Int(reading, 22), 0, [9]))));
        Assert.Equal(3, ReturnCode(await client.SendAsync(Open("/tmp/none/x.bin", access: 2, option: 4))));
        Assert.Equal(2, ReturnCode(await client.SendAsync(Open("/tmp/none.bin", access: 2, option: 8))));
    }

    [Fact]
    public async Task TextInAnotherCcsidIsKeptAsUtf8AndEverythingElseAsSent()
    {
        var ifs = NewIfs();
        Directory.CreateDirectory(ifs.HostPath("/tmp"));
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        await using var server = await FileServer.StartAsync(ifs);
        await using var client = await Client.SignOnAsync(server.Port);
        await client.SendAsync(Exchange());

        async Task<byte[]> SendAsync(string path, ushort ccsid, byte[] data, ushort option = 4, bool close = true)
        {
            var opened = await client.SendAsync(Open(path, access: 2, option: option, ccsid: ccsid));
            Assert.Equal(0x8002, Short(opened, 18));
            await client.SendAsync(Write(Int(opened, 22), 0, data));
            if (close) await client.SendAsync(Close(Int(opened, 22)));
            return close ? await File.ReadAllBytesAsync(ifs.HostPath(path)) : [];
        }
        var utf8 = new UTF8Encoding(false);
        const string accents = "Café, naïve, 5 €";
        Assert.Equal(utf8.GetBytes(accents), await SendAsync("/tmp/w1252.csv", 1252, Encoding.GetEncoding(1252).GetBytes(accents)));
        Assert.Equal(utf8.GetBytes("HELLO, SIM I"), await SendAsync("/tmp/ebcdic.txt", 37, Ebcdic.Encode("HELLO, SIM I")));
        Assert.Equal(utf8.GetBytes(accents), await SendAsync("/tmp/wide.txt", 1200, [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(accents)]));
        // Latin-1 has no euro sign.
        Assert.Equal(utf8.GetBytes("Café, naïve, £5"), await SendAsync("/tmp/latin1.txt", 819, Encoding.Latin1.GetBytes("Café, naïve, £5")));

        // Kept as sent: UTF-8 already, binary, a CCSID it doesn't know, and a file added to rather than made.
        Assert.Equal(utf8.GetBytes(accents), await SendAsync("/tmp/utf8.txt", 1208, utf8.GetBytes(accents)));
        byte[] binary = [0x50, 0x4B, 0x03, 0x04, 0x00, 0xE9, 0x80];
        Assert.Equal(binary, await SendAsync("/tmp/archive.zip", 1252, binary));
        Assert.Equal(binary, await SendAsync("/tmp/odd.bin", 65535, binary));
        byte[] latin = Encoding.Latin1.GetBytes("é");
        Assert.Equal(latin, await SendAsync("/tmp/other.txt", 500, latin));
        await File.WriteAllBytesAsync(ifs.HostPath("/tmp/existing.txt"), latin);
        Assert.Equal(latin, await SendAsync("/tmp/existing.txt", 1252, latin, option: 1));

        // A file still open when the client goes is converted too.
        await SendAsync("/tmp/left-open.txt", 1252, Encoding.GetEncoding(1252).GetBytes(accents), close: false);
        await client.DisposeAsync();
        await WaitForAsync(async () => (await File.ReadAllBytesAsync(ifs.HostPath("/tmp/left-open.txt"))).SequenceEqual(utf8.GetBytes(accents)));
    }

    private static async Task WaitForAsync(Func<Task<bool>> done)
    {
        for (var i = 0; i < 100 && !await done(); i++) await Task.Delay(50);
        Assert.True(await done());
    }

    [Fact]
    public async Task FoldersAreMadeAndDeletedAndNamesChange()
    {
        var ifs = NewIfs();
        Directory.CreateDirectory(ifs.HostPath("/tmp"));
        await File.WriteAllTextAsync(ifs.HostPath("/tmp/a.txt"), "placeholder");
        await using var server = await FileServer.StartAsync(ifs);
        await using var client = await Client.SignOnAsync(server.Port);
        await client.SendAsync(Exchange());

        Assert.Equal(0, ReturnCode(await client.SendAsync(Named(13, 1, "/tmp/made"))));
        Assert.True(Directory.Exists(ifs.HostPath("/tmp/made")));
        Assert.Equal(4, ReturnCode(await client.SendAsync(Named(13, 1, "/tmp/made"))));
        Assert.Equal(3, ReturnCode(await client.SendAsync(Named(13, 1, "/tmp/no/made"))));
        Assert.Equal(13, ReturnCode(await client.SendAsync(Named(13, 1, "/QSYS.LIB/MYLIB.LIB"))));

        Assert.Equal(0, ReturnCode(await client.SendAsync(RenameRequest("/tmp/a.txt", "/tmp/made/b.txt"))));
        Assert.True(File.Exists(ifs.HostPath("/tmp/made/b.txt")));
        await File.WriteAllTextAsync(ifs.HostPath("/tmp/c.txt"), "placeholder");
        Assert.Equal(4, ReturnCode(await client.SendAsync(RenameRequest("/tmp/c.txt", "/tmp/made/b.txt"))));
        Assert.Equal(2, ReturnCode(await client.SendAsync(RenameRequest("/tmp/none.txt", "/tmp/x.txt"))));
        Assert.Equal(0, ReturnCode(await client.SendAsync(RenameRequest("/tmp/c.txt", "/tmp/C.TXT"))));

        Assert.Equal(9, ReturnCode(await client.SendAsync(Named(14, 1, "/tmp/made"))));
        Assert.Equal(4, ReturnCode(await client.SendAsync(Named(12, 2, "/tmp/made"))));
        Assert.Equal(0, ReturnCode(await client.SendAsync(Named(12, 2, "/tmp/made/b.txt"))));
        Assert.Equal(2, ReturnCode(await client.SendAsync(Named(12, 2, "/tmp/made/b.txt"))));
        Assert.Equal(0, ReturnCode(await client.SendAsync(Named(14, 1, "/tmp/made"))));
        Assert.False(Directory.Exists(ifs.HostPath("/tmp/made")));
        Assert.Equal(3, ReturnCode(await client.SendAsync(Named(14, 1, "/tmp/made"))));
    }

    [Fact]
    public async Task AListingGoesOnFromARestartIdOrName()
    {
        var ifs = NewIfs();
        Directory.CreateDirectory(ifs.HostPath("/big"));
        for (var i = 0; i < 25; i++) await File.WriteAllTextAsync(ifs.HostPath($"/big/f{i:00}.txt"), "x");
        await using var server = await FileServer.StartAsync(ifs);
        await using var client = await Client.SignOnAsync(server.Port);
        await client.SendAsync(Exchange());

        var names = new List<string>();
        byte[]? restart = null;
        while (true)
        {
            var replies = await client.ListAsync("/big/*", most: 10, restartId: restart);
            var entries = replies.Where(r => Short(r, 18) == 0x8005).ToList();
            names.AddRange(entries.Select(Name));
            if (entries.Count < 10) break;
            restart = entries[^1].AsSpan(77, 4).ToArray();
        }
        Assert.Equal(Enumerable.Range(0, 25).Select(i => $"f{i:00}.txt"), names);

        var afterName = (await client.ListAsync("/big/*", restartName: "f22.txt")).Where(r => Short(r, 18) == 0x8005).Select(Name);
        Assert.Equal(["f23.txt", "f24.txt"], afterName);
        Assert.Equal(2, ReturnCode(Assert.Single(await client.ListAsync("/big/*", restartName: "f24.txt"))));
    }

    [Fact]
    public async Task AWrongPasswordIsRefusedAtStartServer()
    {
        await using var server = await FileServer.StartAsync(NewIfs());
        var e = await Assert.ThrowsAnyAsync<Exception>(() => Client.SignOnAsync(server.Port, password: "not-the-password"));
        Assert.Contains("3000B", e.Message);
    }

    [Fact]
    public void TheFilePortsAreOptions()
    {
        var options = Options.Parse(["--file-port", "8473", "--file-tls-port", "9473"]);
        Assert.Equal((8473, 9473), (options.FilePort, options.FileTlsPort));
        Assert.True(options.AnyTls);
        Assert.Equal(18473, Options.Parse([], new Dictionary<string, string> { ["SIM_I_FILE_PORT"] = "18473" }).FilePort);
        Assert.Throws<ArgumentException>(() => Options.Parse(["--file-tls-port", "9473"]));
    }

    // ---- requests, as JTOpen sends them at level 16 and up ----

    private static int _correlation;

    private static byte[] Frame(ushort request, byte[] template, byte[]? tail = null)
    {
        tail ??= [];
        var frame = new byte[20 + template.Length + tail.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, frame.Length);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(6), FileServer.ServerId);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16), (ushort)template.Length);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(18), request);
        template.CopyTo(frame, 20);
        tail.CopyTo(frame, 20 + template.Length);
        return frame;
    }

    /// <summary>Exchange attributes: level 0x60100's low half, the CCSIDs 13488 and F200, correlation 1.</summary>
    private static byte[] Exchange()
    {
        var template = new byte[16];
        BinaryPrimitives.WriteUInt16BigEndian(template.AsSpan(2), 0x0100);
        BinaryPrimitives.WriteInt32BigEndian(template.AsSpan(6), -1);
        BinaryPrimitives.WriteInt32BigEndian(template.AsSpan(10), 10);
        BinaryPrimitives.WriteUInt16BigEndian(template.AsSpan(14), 10);
        var frame = Frame(22, template[..10], [.. template[10..], 0x34, 0xB0, 0xF2, 0x00]);
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(12), 1);
        return frame;
    }

    private static byte[] Open(string path, ushort access = 1, ushort option = 8, ushort ccsid = 0)
    {
        var name = Encoding.BigEndianUnicode.GetBytes(path);
        var template = new byte[44];
        BinaryPrimitives.WriteUInt16BigEndian(template.AsSpan(2), 1200);
        BinaryPrimitives.WriteInt32BigEndian(template.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16BigEndian(template.AsSpan(10), access);
        BinaryPrimitives.WriteUInt16BigEndian(template.AsSpan(16), option);
        BinaryPrimitives.WriteUInt16BigEndian(template.AsSpan(8), ccsid);
        BinaryPrimitives.WriteUInt16BigEndian(template.AsSpan(26), 1);
        var tail = new byte[6 + name.Length];
        BinaryPrimitives.WriteInt32BigEndian(tail, tail.Length);
        BinaryPrimitives.WriteUInt16BigEndian(tail.AsSpan(4), 2);
        name.CopyTo(tail, 6);
        return Frame(2, template, tail);
    }

    private static byte[] Read(int handle, long offset, int length)
    {
        var template = new byte[38];
        BinaryPrimitives.WriteInt32BigEndian(template.AsSpan(2), handle);
        BinaryPrimitives.WriteInt32BigEndian(template.AsSpan(14), length);
        BinaryPrimitives.WriteInt64BigEndian(template.AsSpan(30), offset);
        return Frame(3, template);
    }

    private static byte[] Close(int handle)
    {
        var template = new byte[21];
        BinaryPrimitives.WriteInt32BigEndian(template.AsSpan(2), handle);
        BinaryPrimitives.WriteUInt16BigEndian(template.AsSpan(6), 2);
        BinaryPrimitives.WriteUInt16BigEndian(template.AsSpan(8), 0xFFFF);
        BinaryPrimitives.WriteUInt16BigEndian(template.AsSpan(10), 100);
        var frame = Frame(9, template);
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(12), 5);
        return frame;
    }

    /// <summary>Write: a 34-byte template (handle at 22, 64-bit offset at 46), the data as code point 0020.</summary>
    private static byte[] Write(int handle, long offset, byte[] data)
    {
        var template = new byte[34];
        BinaryPrimitives.WriteInt32BigEndian(template.AsSpan(2), handle);
        BinaryPrimitives.WriteInt64BigEndian(template.AsSpan(26), offset);
        BinaryPrimitives.WriteUInt16BigEndian(template.AsSpan(14), 2);
        BinaryPrimitives.WriteUInt16BigEndian(template.AsSpan(16), 0xFFFF);
        return Frame(4, template, CodePoint(0x0020, data));
    }

    /// <summary>Create folder (13) and delete folder (14), name as code point 1; delete file (12), as 2.</summary>
    private static byte[] Named(ushort request, ushort codePoint, string path)
    {
        var template = new byte[request == 14 ? 10 : 8];
        BinaryPrimitives.WriteUInt16BigEndian(template.AsSpan(2), 1200);
        BinaryPrimitives.WriteInt32BigEndian(template.AsSpan(4), 1);
        return Frame(request, template, CodePoint(codePoint, Encoding.BigEndianUnicode.GetBytes(path)));
    }

    /// <summary>Rename: a 16-byte template, the old name as code point 3, the new as 4.</summary>
    private static byte[] RenameRequest(string from, string to)
    {
        var template = new byte[16];
        BinaryPrimitives.WriteUInt16BigEndian(template.AsSpan(2), 1200);
        BinaryPrimitives.WriteUInt16BigEndian(template.AsSpan(4), 1200);
        BinaryPrimitives.WriteInt32BigEndian(template.AsSpan(6), 1);
        BinaryPrimitives.WriteInt32BigEndian(template.AsSpan(10), 1);
        return Frame(15, template, [.. CodePoint(3, Encoding.BigEndianUnicode.GetBytes(from)), .. CodePoint(4, Encoding.BigEndianUnicode.GetBytes(to))]);
    }

    private static byte[] CodePoint(ushort id, byte[] data)
    {
        var point = new byte[6 + data.Length];
        BinaryPrimitives.WriteInt32BigEndian(point, point.Length);
        BinaryPrimitives.WriteUInt16BigEndian(point.AsSpan(4), id);
        data.CopyTo(point, 6);
        return point;
    }

    /// <summary>List attributes by name: the pattern, the most entries (FFFF: no limit), as JTOpen sends it.</summary>
    private static byte[] List(string pattern, int most, byte[]? restartId = null, string? restartName = null)
    {
        var name = Encoding.BigEndianUnicode.GetBytes(pattern);
        var template = new byte[20];
        BinaryPrimitives.WriteUInt16BigEndian(template.AsSpan(6), 1200);
        BinaryPrimitives.WriteInt32BigEndian(template.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(template.AsSpan(14), (ushort)most);
        BinaryPrimitives.WriteUInt16BigEndian(template.AsSpan(16), 1);
        var tail = new byte[6 + name.Length];
        BinaryPrimitives.WriteInt32BigEndian(tail, tail.Length);
        BinaryPrimitives.WriteUInt16BigEndian(tail.AsSpan(4), 2);
        name.CopyTo(tail, 6);
        byte[] restart = restartId is not null ? CodePoint(14, restartId)
            : restartName is not null ? CodePoint(7, Encoding.BigEndianUnicode.GetBytes(restartName))
            : [];
        return Frame(10, template, [.. tail, .. restart]);
    }

    private sealed class Client : IAsyncDisposable
    {
        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;

        private Client(TcpClient tcp)
        {
            _tcp = tcp;
            _stream = tcp.GetStream();
        }

        /// <summary>Exchange random seeds and start server, as JTOpen opens a host server.</summary>
        public static async Task<Client> SignOnAsync(int port, string user = Users.DefaultUser, string password = Users.DefaultPassword)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            var client = new Client(tcp);
            var seeds = await client.SendAsync(Frame(0x7001, ClientSeed));
            var level = seeds[5];
            var substitute = Passwords.Substitute(level, Passwords.Token(level, user, password), user, ClientSeed, seeds.AsSpan(24, 8).ToArray(), 1);
            byte[] points = [.. CodePoint(0x1105, substitute), .. CodePoint(0x1104, Ebcdic.Encode(user.PadRight(10)))];
            var start = await client.SendAsync(Frame(0x7002, [level < 2 ? (byte)1 : (byte)3, 1, 0], points));
            var rc = Int(start, 20);
            if (rc != 0)
            {
                await client.DisposeAsync();
                throw new InvalidOperationException($"start server refused: {rc:X8}");
            }
            return client;
        }

        private static byte[] CodePoint(ushort id, byte[] data)
        {
            var point = new byte[6 + data.Length];
            BinaryPrimitives.WriteInt32BigEndian(point, point.Length);
            BinaryPrimitives.WriteUInt16BigEndian(point.AsSpan(4), id);
            data.CopyTo(point, 6);
            return point;
        }

        /// <summary>Sends a list and reads every reply, to the one that ends the chain.</summary>
        public async Task<List<byte[]>> ListAsync(string pattern, int most = 0xFFFF, byte[]? restartId = null, string? restartName = null)
        {
            var replies = new List<byte[]> { await SendAsync(List(pattern, most, restartId, restartName)) };
            while (Short(replies[^1], 18) != 0x8001 && Short(replies[^1], 20) != 0) replies.Add(await ReadAsync());
            return replies;
        }

        private async Task<byte[]> ReadAsync()
        {
            var header = new byte[20];
            await _stream.ReadExactlyAsync(header);
            var reply = new byte[Int(header, 0)];
            header.CopyTo(reply, 0);
            await _stream.ReadExactlyAsync(reply.AsMemory(20));
            return reply;
        }

        public async Task<byte[]> SendAsync(byte[] frame)
        {
            if (Int(frame, 12) == 0) BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(12), Interlocked.Increment(ref _correlation) + 100);
            await _stream.WriteAsync(frame);
            return await ReadAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _stream.DisposeAsync();
            _tcp.Dispose();
        }
    }
}
