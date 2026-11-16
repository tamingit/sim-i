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
            Assert.Equal(23, ReturnCode(await client.SendAsync(Open("/tmp/note.txt", access: 2))));
            Assert.Equal(23, ReturnCode(await client.SendAsync(Frame(18, new byte[14]))));
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

    private static byte[] Open(string path, ushort access = 1)
    {
        var name = Encoding.BigEndianUnicode.GetBytes(path);
        var template = new byte[44];
        BinaryPrimitives.WriteUInt16BigEndian(template.AsSpan(2), 1200);
        BinaryPrimitives.WriteInt32BigEndian(template.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16BigEndian(template.AsSpan(10), access);
        BinaryPrimitives.WriteUInt16BigEndian(template.AsSpan(16), 8);
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

        public async Task<byte[]> SendAsync(byte[] frame)
        {
            if (Int(frame, 12) == 0) BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(12), Interlocked.Increment(ref _correlation) + 100);
            await _stream.WriteAsync(frame);
            var header = new byte[20];
            await _stream.ReadExactlyAsync(header);
            var reply = new byte[Int(header, 0)];
            header.CopyTo(reply, 0);
            await _stream.ReadExactlyAsync(reply.AsMemory(20));
            return reply;
        }

        public async ValueTask DisposeAsync()
        {
            await _stream.DisposeAsync();
            _tcp.Dispose();
        }
    }
}
