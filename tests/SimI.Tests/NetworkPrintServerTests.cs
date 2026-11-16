using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using SimI;

namespace SimI.Tests;

/// <summary>The Network Print requests JTOpen's SpooledFileList sends, answered as a real IBM i (V7R5) answers them.</summary>
public class NetworkPrintServerTests
{
    private static readonly byte[] ClientSeed = Convert.FromHexString("0102030405060708");
    private const ushort Server = 0x0008, SpooledFile = 0x0001;

    [Fact]
    public async Task TheServerAnswersTheAttributesAskedForAndAcceptsTheClientsLanguage()
    {
        await using var server = await NetworkPrintServer.StartAsync();
        await using var client = await Client.SignOnAsync(server.Port);

        var ccsid = Single(await client.RequestAsync(Server, 0x000F, (0x0007, Ids(0x008A)), (0x0008, Attributes((0x008A, 13488)))));
        Assert.Equal(0, ReturnCode(ccsid));
        Assert.Equal(37, Read(ccsid)[0x008A]);

        // JTOpen's NPConversation gives up with "an error occurred on the system" unless this succeeds.
        var language = Single(await client.RequestAsync(Server, 0x000E, (0x0007, Ids(0x00B4)), (0x0008, Attributes((0x00B4, "2924")))));
        Assert.Equal(0, ReturnCode(language));
        Assert.Equal("2924", Read(language)[0x00B4]);

        var job = Single(await client.RequestAsync(Server, 0x000F, (0x0007, Ids(0x003B, 0x003C, 0x003E, 0x008D))));
        var values = Read(job);
        Assert.Equal("QNPSERVS", values[0x003B]);
        Assert.Equal("QUSER", values[0x003E]);
        Assert.Equal("V7R5M0", values[0x008D]);
        Assert.Matches("^[0-9]{6}$", (string)values[0x003C]);
    }

    [Fact]
    public async Task TheListSelectsANamedUsersFilesAndEachFilesAttributesCanBeRetrieved()
    {
        await using var server = await NetworkPrintServer.StartAsync();
        await using var client = await Client.SignOnAsync(server.Port);

        // As SpooledFileList with setUserFilter("PRTUSER") and every output queue sends it.
        var replies = await client.RequestAsync(SpooledFile, 0x0016,
            (0x0006, Attributes((0x004A, "*ALL"), (0x0049, ""), (0x003E, "PRTUSER"))), (0x0007, Ids(0x0068, 0x006F)));
        Assert.Equal(3, replies.Count);
        Assert.All(replies, r => Assert.Equal(0, ReturnCode(r)));
        var id = Read(replies[1], 0x0001);
        Assert.Equal("QPRTAFP", id[0x0068]);
        Assert.Equal("SIMI", id[0x00FB]);
        Assert.Equal("1261012", id[0x0022]);
        Assert.Equal("093500", id[0x006E]);

        var attributes = Single(await client.RequestAsync(SpooledFile, 0x000F,
            (0x0001, Attributes((0x003B, "QPADEV0001"), (0x003E, "PRTUSER"), (0x003C, "123456"), (0x0068, "QPRTAFP"), (0x0069, 2))),
            (0x0007, Ids(0x006F, 0x004A, 0x006A, 0x0022, 0x006E, 0x9999))));
        Assert.Equal(0, ReturnCode(attributes));
        var values = Read(attributes);
        Assert.Equal(1, values[0x006F]);
        Assert.Equal("QPRINT", values[0x004A]);
        Assert.Equal("*READY", values[0x006A]);
        Assert.Equal("093500", values[0x006E]);
        Assert.False(values.ContainsKey(0x9999));

        var current = await client.RequestAsync(SpooledFile, 0x0016, (0x0006, Attributes((0x004A, "*ALL"), (0x003E, "*CURRENT"))));
        Assert.Equal(3, current.Count);
        Assert.All(current, r => Assert.Equal("PRTUSER", Read(r, 0x0001)[0x003E]));

        var nobody = Single(await client.RequestAsync(SpooledFile, 0x0016, (0x0006, Attributes((0x004A, "*ALL"), (0x003E, "NOBODY")))));
        Assert.Equal(0x0014, ReturnCode(nobody));
    }

    [Fact]
    public async Task AFileReadsWholeAsJTOpenDownloadsIt()
    {
        await using var server = await NetworkPrintServer.StartAsync();
        server.ReadChunk = 1000;
        await using var client = await Client.SignOnAsync(server.Port);
        var data = server.Files[0].Data;
        var key = (0x0001, Attributes((0x0022, "1261012"), (0x006E, "093000"), (0x0069, 1), (0x0068, "QSYSPRT"), (0x003E, "PRTUSER"),
            (0x003C, "123456"), (0x003B, "QPADEV0001"), (0x00FB, "SIMI")));

        var opened = Single(await client.RequestAsync(SpooledFile, 0x0002, key, (0x0006, Attributes((0x00B8, "*YES")))));
        Assert.Equal(0, ReturnCode(opened));
        var handle = CodePoint(opened, 0x000C);
        Assert.Equal([0x02, 0, 0, 0], handle[..4]);
        Assert.Equal(9, handle.Length);

        // JTOpen won't read a file whose size (7D) it can't retrieve: "Correct system level is required".
        var size = Single(await client.RequestAsync(SpooledFile, 0x000F, key, (0x0007, Ids(0x007D))));
        Assert.Equal(data.Length, Read(size)[0x007D]);

        var read = new List<byte>();
        while (true)
        {
            var reply = Single(await client.RequestAsync(SpooledFile, 0x0003, (0x0008, Attributes((0x007D, 4096))), (0x000C, handle)));
            if (ReturnCode(reply) == 0x0013)
            {
                Assert.Equal(0x40000000u, Flags(reply)); // end of file
                break;
            }
            Assert.Equal(0, ReturnCode(reply));
            read.AddRange(CodePoint(reply, 0x0009));
            Assert.Equal(read.Count == data.Length ? 0x40000000u : 0u, Flags(reply)); // the chunk that ends the file
        }
        Assert.Equal(data, read.ToArray());
        Assert.Equal(0, ReturnCode(Single(await client.RequestAsync(SpooledFile, 0x0005, (0x000C, handle)))));
        Assert.Equal(1, server.Closes);
    }

    [Fact]
    public async Task AFileReadsAPageAtATime()
    {
        await using var server = await NetworkPrintServer.StartAsync();
        await using var client = await Client.SignOnAsync(server.Port);
        var data = server.Files[0].Data;
        var firstFormFeed = Array.IndexOf(data, (byte)0x0C) + 1;
        var key = (0x0001, Attributes((0x0069, 1), (0x0068, "QSYSPRT"), (0x003C, "123456")));

        var handle = CodePoint(Single(await client.RequestAsync(SpooledFile, 0x001A, key, (0x0006, Attributes((0x00D6, "*YES"))))), 0x000C);
        var attributes = Read(Single(await client.RequestAsync(SpooledFile, 0x0018, (0x0007, Ids(0x006F, 0x007D, 0x00DA)), (0x000C, handle))));
        Assert.Equal(2, attributes[0x006F]);
        Assert.Equal(firstFormFeed, attributes[0x007D]);
        Assert.Equal("*NO", attributes[0x00DA]);

        async Task<byte[]> PageAsync()
        {
            var page = Single(await client.RequestAsync(SpooledFile, 0x0003, (0x0008, Attributes((0x007D, 4096))), (0x000C, handle)));
            Assert.Equal(0x20000000u, Flags(page)); // the chunk that ends the page
            var end = Single(await client.RequestAsync(SpooledFile, 0x0003, (0x0008, Attributes((0x007D, 4096))), (0x000C, handle)));
            Assert.Equal(0x0020, ReturnCode(end)); // end of page
            return CodePoint(page, 0x0009);
        }

        Assert.Equal(data[..firstFormFeed], await PageAsync());
        Assert.Equal(0, ReturnCode(Single(await client.RequestAsync(SpooledFile, 0x0019, (0x0008, Attributes((0x00D7, 2))), (0x000C, handle)))));
        Assert.Equal(data[firstFormFeed..], await PageAsync());
        Assert.Equal(0x0021, ReturnCode(Single(await client.RequestAsync(SpooledFile, 0x0019, (0x0008, Attributes((0x00D7, 3))), (0x000C, handle)))));
    }

    private static uint Flags(byte[] reply) => BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(22));

    private static byte[] CodePoint(byte[] reply, int codePoint)
    {
        for (var at = 32; at + 6 <= reply.Length;)
        {
            var ll = BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(at));
            if (BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(at + 4)) == codePoint) return reply.AsSpan(at + 6, ll - 6).ToArray();
            at += ll;
        }
        throw new InvalidOperationException($"no code point {codePoint:X4}");
    }

    private static byte[] Single(List<byte[]> replies) => Assert.Single(replies);

    private static int ReturnCode(byte[] reply) => BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(26));

    private static byte[] Ids(params int[] ids) =>
        [(byte)(ids.Length >> 8), (byte)ids.Length, 0, 2, .. ids.SelectMany(i => new[] { (byte)(i >> 8), (byte)i })];

    /// <summary>An attribute code point's data: count, entry size 12, entries (ID, type, length, offset), values.</summary>
    private static byte[] Attributes(params (int Id, object Value)[] attributes)
    {
        var entries = new List<byte>();
        var values = new List<byte>();
        var start = 6 + 4 + 12 * attributes.Length;
        foreach (var (id, value) in attributes)
        {
            var bytes = value is int n ? Be(n) : [.. Ebcdic.Encode((string)value), 0];
            entries.AddRange([(byte)(id >> 8), (byte)id, 0, (byte)(value is int ? 3 : 5), .. Be(bytes.Length), .. Be(start + values.Count)]);
            values.AddRange(bytes);
        }
        return [(byte)(attributes.Length >> 8), (byte)attributes.Length, 0, 12, .. entries, .. values];
    }

    /// <summary>The attributes in a reply's code point (8 unless asked otherwise).</summary>
    private static Dictionary<int, object> Read(byte[] reply, int codePoint = 0x0008)
    {
        var result = new Dictionary<int, object>();
        for (var at = 32; at + 6 <= reply.Length;)
        {
            var ll = BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(at));
            if (BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(at + 4)) == codePoint)
            {
                var data = reply.AsSpan(at + 6, ll - 6);
                for (var i = 0; i < BinaryPrimitives.ReadUInt16BigEndian(data); i++)
                {
                    var entry = data.Slice(4 + 12 * i, 12);
                    var value = data.Slice(BinaryPrimitives.ReadInt32BigEndian(entry[8..]) - 6, BinaryPrimitives.ReadInt32BigEndian(entry[4..]));
                    result[BinaryPrimitives.ReadUInt16BigEndian(entry)] = BinaryPrimitives.ReadUInt16BigEndian(entry[2..]) == 3
                        ? BinaryPrimitives.ReadInt32BigEndian(value)
                        : Ebcdic.Decode(value.TrimEnd((byte)0)).TrimEnd();
                }
            }
            at += ll;
        }
        return result;
    }

    private static byte[] Be(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }

    private sealed class Client : IAsyncDisposable
    {
        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;
        private int _correlation;

        private Client(TcpClient tcp)
        {
            _tcp = tcp;
            _stream = tcp.GetStream();
        }

        /// <summary>Exchange random seeds and start server as the default user, as JTOpen opens the server.</summary>
        public static async Task<Client> SignOnAsync(int port)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            var client = new Client(tcp);
            const string user = Users.DefaultUser, password = Users.DefaultPassword;
            var seeds = await client.SendAsync(Frame(0x7001, ClientSeed, []));
            var level = seeds[5];
            var substitute = Passwords.Substitute(level, Passwords.Token(level, user, password), user, ClientSeed, seeds.AsSpan(24, 8).ToArray(), 1);
            var start = await client.SendAsync(Frame(0x7002, [level < 2 ? (byte)1 : (byte)3, 1, 0],
                [(0x1105, substitute), (0x1104, Ebcdic.Encode(user.PadRight(10)))]));
            Assert.Equal(0, BinaryPrimitives.ReadInt32BigEndian(start.AsSpan(20)));
            return client;
        }

        /// <summary>A Network Print request: object type, then a 12-byte template (action, flags, return code). Returns every reply frame.</summary>
        public async Task<List<byte[]>> RequestAsync(ushort objectType, ushort action, params (int Id, byte[] Data)[] codePoints)
        {
            var frame = Frame(objectType, [(byte)(action >> 8), (byte)action, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], codePoints);
            BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(12), ++_correlation);
            var replies = new List<byte[]> { await SendAsync(frame) };
            while ((BinaryPrimitives.ReadUInt32BigEndian(replies[^1].AsSpan(22)) & 0x80000000) != 0) replies.Add(await ReadAsync());
            return replies;
        }

        private static byte[] Frame(ushort requestId, byte[] template, (int Id, byte[] Data)[] codePoints)
        {
            var frame = new byte[20 + template.Length + codePoints.Sum(c => 6 + c.Data.Length)];
            BinaryPrimitives.WriteInt32BigEndian(frame, frame.Length);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(6), 0xE003);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16), (ushort)template.Length);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(18), requestId);
            template.CopyTo(frame, 20);
            var at = 20 + template.Length;
            foreach (var (id, data) in codePoints)
            {
                BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(at), 6 + data.Length);
                BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(at + 4), (ushort)id);
                data.CopyTo(frame, at + 6);
                at += 6 + data.Length;
            }
            return frame;
        }

        private async Task<byte[]> SendAsync(byte[] frame)
        {
            await _stream.WriteAsync(frame);
            return await ReadAsync();
        }

        private async Task<byte[]> ReadAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var header = new byte[20];
            await _stream.ReadExactlyAsync(header, timeout.Token);
            var reply = new byte[BinaryPrimitives.ReadInt32BigEndian(header)];
            header.CopyTo(reply, 0);
            await _stream.ReadExactlyAsync(reply.AsMemory(20), timeout.Token);
            return reply;
        }

        public ValueTask DisposeAsync()
        {
            _tcp.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
