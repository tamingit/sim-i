using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FakeIbmI.Tests;

/// <summary>
/// The remote command server, driven with requests laid out as JTOpen 21.0.7 builds them. JTOpen's
/// CommandCall and ProgramCall were also run against the image by hand. The start-server substitute
/// uses the fake's own password code, which the sign-on server tests check against JTOpen's.
/// </summary>
public class RemoteCommandServerTests
{
    private static readonly byte[] ClientSeed = Convert.FromHexString("0102030405060708");

    [Fact]
    public async Task TheAttributeReplyIsLaidOutAsJTOpenReadsIt()
    {
        await using var server = await RemoteCommandServer.StartAsync();
        await using var client = await Client.SignOnAsync(server.Port, "PRTUSER", "Secret12");

        var reply = await client.RequestAsync(0x1001, [0, 0, 0x04, 0xB0, 0xF2, 0xF9, 0xF2, 0xF4, 0, 0, 0, 1]);

        Assert.Equal(0x8001, RequestId(reply));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(20)));
        Assert.Equal(37, BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(22)));
        Assert.Equal(10, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(34)));
    }

    [Fact]
    public async Task ACommandSucceedsAndOnlyItsNameIsLogged()
    {
        var lines = new List<string>();
        await using var server = await RemoteCommandServer.StartAsync(log: line => { lock (lines) lines.Add(line); });
        await using var client = await Client.SignOnAsync(server.Port, "PRTUSER", "Secret12");
        await client.RequestAsync(0x1001, [0, 0, 0x04, 0xB0, 0xF2, 0xF9, 0xF2, 0xF4, 0, 0, 0, 1]);

        // Data stream level 10: the command in UTF-16, after its CCSID.
        var text = Encoding.BigEndianUnicode.GetBytes("QSYS/CHGUSRPRF USRPRF(X) PASSWORD(Hush1234)");
        var reply = await client.RequestAsync(0x1002, [5], (0x1104, [0, 0, 0x04, 0xB0, .. text]));

        Assert.Equal(0x8002, RequestId(reply));
        Assert.Equal([0, 0, 0, 0], reply.AsSpan(20, 4).ToArray());
        Assert.Equal(["CMD CHGUSRPRF"], server.Calls);
        lock (lines)
        {
            Assert.Contains(lines, l => l.Contains("command CHGUSRPRF"));
            Assert.DoesNotContain(lines, l => l.Contains("Hush1234"));
        }
    }

    [Fact]
    public async Task AProgramCallZeroFillsItsOutputAndReturnsItsInOutData()
    {
        await using var server = await RemoteCommandServer.StartAsync();
        await using var client = await Client.SignOnAsync(server.Port, "PRTUSER", "Secret12");

        // In (11), out (22), in-out (33), in-out compressed (23: 0x33 0x40 repeated 512 times), null (255).
        byte[] compressed = [0x1B, 0x33, 0x40, 0x02, 0x00];
        var reply = await client.CallAsync("QLZAREQ", "QSYS",
            (4, 11, [0, 0, 0, 7]),
            (100, 22, []),
            (8, 33, [0x11, 0x22]),
            (1024, 23, compressed),
            (0, 255, []));

        Assert.Equal(0x8003, RequestId(reply));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(20)));
        var outputs = Parameters(reply);
        Assert.Equal(3, outputs.Count);
        Assert.Equal(100, outputs[0].Length);
        Assert.Empty(outputs[0].Data); // the client zero-fills up to the length
        Assert.Equal(8, outputs[1].Length);
        Assert.Equal([0x11, 0x22, 0, 0, 0, 0, 0, 0], outputs[1].Data);
        Assert.Equal(1024, outputs[2].Length);
        Assert.Equal(Enumerable.Repeat(new byte[] { 0x33, 0x40 }, 512).SelectMany(b => b), outputs[2].Data);
        Assert.Equal(["PGM QSYS/QLZAREQ"], server.Calls);
    }

    /// <summary>A machine with program ADDER: two packed inputs, their sum out, and a name it greets in place.</summary>
    private static Machine MachineWithAdder()
    {
        var machine = Machine.Open();
        var member = new Member("QGPL", "QRPGLESRC", "ADDER", "RPGLE", """
            **FREE
            dcl-pi *n;
              a packed(5:0) const;
              b packed(7:2) const;
              sum packed(9:2);
              name char(12);
              count int(10);
            end-pi;
            sum = a + b;
            name = 'Hi ' + %trim(name);
            count += 1;
            if a = 999;
              sum = sum / (a - 999);
            endif;
            dsply 'added';
            return;
            """);
        machine.Save(member);
        Assert.True(machine.Compile(member).Ok);
        return machine;
    }

    [Fact]
    public async Task AProgramTheMachineHasReallyRunsAndReturnsItsParameters()
    {
        using var machine = MachineWithAdder();
        var lines = new List<string>();
        await using var server = await RemoteCommandServer.StartAsync(machine: machine, log: line => { lock (lines) lines.Add(line); });
        await using var client = await Client.SignOnAsync(server.Port, "PRTUSER", "Secret12");

        // Packed 5,0 = 40; packed 7,2 = -2.50; the sum out; a name in and out; a 4-byte integer in and out.
        var reply = await client.CallAsync("ADDER", "QGPL",
            (3, 11, [0x00, 0x04, 0x0F]), (4, 11, [0x00, 0x00, 0x25, 0x0D]), (5, 22, []),
            (12, 33, Ebcdic.Encode("Ada")), (4, 33, [0, 0, 0, 41]));

        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(20)));
        var outputs = Parameters(reply);
        Assert.Equal(3, outputs.Count);
        Assert.Equal([0x00, 0x00, 0x03, 0x75, 0x0F], outputs[0].Data); // 37.50
        Assert.Equal("Hi Ada      ", Ebcdic.Decode(outputs[1].Data));
        Assert.Equal([0, 0, 0, 42], outputs[2].Data);
        lock (lines) Assert.Contains(lines, l => l.Contains("call QGPL/ADDER (5 parameters): ran"));
    }

    [Fact]
    public async Task AProgramThatFailsAnswersAnErrorAndTheLogSaysWhy()
    {
        using var machine = MachineWithAdder();
        var lines = new List<string>();
        await using var server = await RemoteCommandServer.StartAsync(machine: machine, log: line => { lock (lines) lines.Add(line); });
        await using var client = await Client.SignOnAsync(server.Port, "PRTUSER", "Secret12");

        var reply = await client.CallAsync("ADDER", "*LIBL",
            (3, 11, [0x00, 0x99, 0x9F]), (4, 11, [0x00, 0x00, 0x00, 0x0F]), (5, 22, []), (12, 33, []), (4, 33, []));

        Assert.Equal(0x0500, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(20)));
        Assert.Empty(Parameters(reply));
        lock (lines) Assert.Contains(lines, l => l.Contains("failed: ADDER ended in error at line 13: Attempt to divide by zero. (status 00102)"));
    }

    [Fact]
    public async Task TheMachinesCommandsRunAndOthersStillSucceedUnrun()
    {
        using var machine = MachineWithAdder();
        var lines = new List<string>();
        await using var server = await RemoteCommandServer.StartAsync(machine: machine, log: line => { lock (lines) lines.Add(line); });
        await using var client = await Client.SignOnAsync(server.Port, "PRTUSER", "Secret12");
        async Task<int> RunAsync(string command)
        {
            var reply = await client.RequestAsync(0x1002, [5], (0x1104, [0, 0, 0x04, 0xB0, .. Encoding.BigEndianUnicode.GetBytes(command)]));
            return BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(20));
        }

        Assert.Equal(0x0400, await RunAsync("RUNSQL SQL('create table mylib/t (a int)') COMMIT(*NONE)"));
        Assert.Equal(0, await RunAsync("CRTLIB LIB(MYLIB)"));
        Assert.Equal(0x0400, await RunAsync("CRTLIB LIB(MYLIB)"));
        Assert.Equal(0, await RunAsync("RUNSQL SQL('create table mylib/t (a int)') COMMIT(*NONE)"));
        Assert.Equal(0x0400, await RunAsync("RUNSQL SQL('create table mylib/t (a int)') COMMIT(*NONE)"));
        Assert.Equal(0, await RunAsync("CALL PGM(QGPL/ADDER) PARM(1 2 0 'Ada' 0)"));
        Assert.Equal(0x0400, await RunAsync("CALL PGM(ADDER) PARM(1 2 3 4 5 6)"));
        Assert.Equal(0x0400, await RunAsync("CRTBNDRPG PGM(QGPL/NOSRC)"));
        // Not the machine's: an IBM program, and a command it doesn't have.
        Assert.Equal(0, await RunAsync("CALL PGM(QSYS/QCMDEXC) PARM('x' 1)"));
        Assert.Equal(0, await RunAsync("STRTCPSVR SERVER(*HTTP)"));

        Assert.Single(machine.Store.Columns("T"));
        lock (lines)
        {
            Assert.Contains(lines, l => l.Contains("command CALL: ran (DSPLY showed 1 message)"));
            Assert.Contains(lines, l => l.Contains("command CALL: failed: ADDER takes 5 parameters, not 6"));
            Assert.Contains(lines, l => l.Contains("command RUNSQL: failed") && !l.Contains("already exists"));
            Assert.Contains(lines, l => l.Contains("command STRTCPSVR: ran nothing, answered success"));
        }
    }

    [Fact]
    public async Task StartServerNamesTheServerJob()
    {
        await using var server = await RemoteCommandServer.StartAsync();
        await using var client = await Client.ConnectAsync(server.Port);

        var start = await client.StartReplyAsync("PRTUSER", "Secret12");

        // 0x111F after the return code: CCSID 37, then number/user/name, as JTOpen splits it.
        Assert.Equal(0x111F, BinaryPrimitives.ReadUInt16BigEndian(start.AsSpan(28)));
        Assert.Equal(37, BinaryPrimitives.ReadInt32BigEndian(start.AsSpan(30)));
        Assert.Matches("^[0-9]{6}/QUSER/QZRCSRVS$", Ebcdic.Decode(start.AsSpan(34)));
    }

    [Fact]
    public async Task RetrieveCurrentAttributesAnswersTheJobName()
    {
        await using var server = await RemoteCommandServer.StartAsync();
        await using var client = await Client.SignOnAsync(server.Port, "PRTUSER", "Secret12");

        // As JTOpen's getJobInfo calls it: receiver (46), its length, RTVC0100, 1 attribute, key 1009, error code.
        var reply = await client.CallAsync("QWCRTVCA", "QSYS",
            (46, 22, []), (4, 11, [0, 0, 0, 46]), (8, 11, Ebcdic.Encode("RTVC0100")), (4, 11, [0, 0, 0, 1]),
            (4, 11, [0, 0, 0x03, 0xF1]), (8, 11, []));

        var receiver = Assert.Single(Parameters(reply)).Data;
        Assert.Equal(46, receiver.Length);
        Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(receiver));
        Assert.Equal(1009, BinaryPrimitives.ReadInt32BigEndian(receiver.AsSpan(8)));
        Assert.Matches("^QZRCSRVS  QUSER     [0-9]{6}$", Ebcdic.Decode(receiver.AsSpan(20, 26)));
    }

    [Fact]
    public async Task CheckUserFunctionUsageSaysAllowedAndLogsTheFunction()
    {
        var lines = new List<string>();
        await using var server = await RemoteCommandServer.StartAsync(log: line => { lock (lines) lines.Add(line); });
        await using var client = await Client.SignOnAsync(server.Port, "PRTUSER", "Secret12");

        var reply = await client.CallAsync("QSYCKUFU", "QSYS",
            (1, 22, []), (30, 11, Ebcdic.Encode("QIBM_XE1_EXAMPLE".PadRight(30))), (10, 11, Ebcdic.Encode("PRTUSER   ")), (4, 11, []));

        Assert.Equal([0xF2], Assert.Single(Parameters(reply)).Data); // EBCDIC '2': allowed ('1' is not allowed)
        lock (lines) Assert.Contains(lines, l => l.Contains("QSYS/QSYCKUFU") && l.Contains("QIBM_XE1_EXAMPLE allowed for PRTUSER"));
    }

    [Fact]
    public async Task TheOpenListOfSpooledFilesListsThemForAUserAndClosesTheList()
    {
        await using var server = await RemoteCommandServer.StartAsync();
        await using var client = await Client.SignOnAsync(server.Port, "PRTUSER", "Secret12");

        // As JTOpen's SpooledFileOpenList sends QGYOLSPL for one user: OSPF0100 filter (92 bytes), job, format OSPL0300.
        async Task<byte[]> OpenAsync(string user)
        {
            var filter = new byte[92];
            Array.Fill(filter, (byte)0x40);
            BinaryPrimitives.WriteInt32BigEndian(filter, 1);
            Ebcdic.Encode(user.PadRight(10)).CopyTo(filter, 4);
            var reply = await client.CallAsync("QGYOLSPL", "QSYS",
                (0, 22, []), (4, 11, [0, 0, 0, 0]), (80, 22, []), (4, 11, [0xFF, 0xFF, 0xFF, 0xFF]), (4, 11, [0, 0, 0, 0]),
                (92, 11, filter), (26, 11, Ebcdic.Encode("".PadRight(26))), (8, 11, Ebcdic.Encode("OSPL0300")), (8, 11, []));
            return Parameters(reply).Single(p => p.Length == 80).Data;
        }

        var information = await OpenAsync("PRTUSER");
        Assert.Equal(3, BinaryPrimitives.ReadInt32BigEndian(information));
        Assert.Equal(136, BinaryPrimitives.ReadInt32BigEndian(information.AsSpan(12)));
        Assert.Equal(0xF2, information[30]); // ready
        var handle = information[8..12];

        // QGYGTLE: the first two, as the receiver, its length, the handle, list information, number, starting record.
        var got = await client.CallAsync("QGYGTLE", "QSYS",
            (272, 22, []), (4, 11, [0, 0, 0x01, 0x10]), (4, 11, handle), (80, 22, []), (4, 11, [0, 0, 0, 2]), (4, 11, [0, 0, 0, 1]), (8, 11, []));
        var outputs = Parameters(got);
        var receiver = outputs[0].Data;
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(outputs[1].Data.AsSpan(4)));
        Assert.Equal("QSYSPRT   ", Ebcdic.Decode(receiver.AsSpan(26, 10)));
        Assert.Equal("QPADEV0001PRTUSER   123456", Ebcdic.Decode(receiver.AsSpan(0, 26)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(receiver.AsSpan(120)));
        Assert.Equal("QPRTAFP   ", Ebcdic.Decode(receiver.AsSpan(136 + 26, 10)));

        await client.CallAsync("QGYCLST", "QSYS", (4, 11, handle), (8, 11, []));
        Assert.Equal(0, BinaryPrimitives.ReadInt32BigEndian(await OpenAsync("NOBODY")));
        // ACS's printer output asks for *CURRENT: the user signed on to this server.
        Assert.Equal(3, BinaryPrimitives.ReadInt32BigEndian(await OpenAsync("*CURRENT")));
    }

    [Theory]
    [InlineData("OSPL0100", 196)]
    [InlineData("OSPL0200", 236)]
    [InlineData("OSPL0400", 136)]
    public async Task TheOpenListOfSpooledFilesAnswersEachFormatAsARealSystemLaysItOut(string format, int length)
    {
        await using var server = await RemoteCommandServer.StartAsync();
        await using var client = await Client.SignOnAsync(server.Port, "PRTUSER", "Secret12");

        var filter = new byte[92];
        Array.Fill(filter, (byte)0x40);
        BinaryPrimitives.WriteInt32BigEndian(filter, 1);
        Ebcdic.Encode("PRTUSER".PadRight(10)).CopyTo(filter, 4);
        var opened = await client.CallAsync("QGYOLSPL", "QSYS",
            (0, 22, []), (4, 11, [0, 0, 0, 0]), (80, 22, []), (4, 11, [0xFF, 0xFF, 0xFF, 0xFF]), (4, 11, [0, 0, 0, 0]),
            (92, 11, filter), (26, 11, Ebcdic.Encode("".PadRight(26))), (8, 11, Ebcdic.Encode(format)), (8, 11, []));
        var information = Parameters(opened).Single(p => p.Length == 80).Data;
        Assert.Equal(3, BinaryPrimitives.ReadInt32BigEndian(information));
        Assert.Equal(length, BinaryPrimitives.ReadInt32BigEndian(information.AsSpan(12)));

        var got = await client.CallAsync("QGYGTLE", "QSYS",
            (length * 3, 22, []), (4, 11, BigEndian(length * 3)), (4, 11, information[8..12]), (80, 22, []), (4, 11, [0, 0, 0, 3]),
            (4, 11, [0, 0, 0, 1]), (8, 11, []));
        var outputs = Parameters(got);
        var receiver = outputs[0].Data;
        Assert.Equal(3, BinaryPrimitives.ReadInt32BigEndian(outputs[1].Data.AsSpan(4)));
        var second = receiver.AsSpan(length, length);

        var local = new DateTime(2026, 10, 12, 9, 35, 0, DateTimeKind.Local).ToUniversalTime();
        var (utcDate, utcTime) = ($"1{local:yyMMdd}", local.ToString("HHmmss"));
        if (format == "OSPL0400")
        {
            Assert.Equal("QPADEV0001PRTUSER   123456QPRTAFP   ", Ebcdic.Decode(second[..36]));
            Assert.Equal(utcDate + utcTime, Ebcdic.Decode(second.Slice(44, 13)));
            Assert.Equal(4096, BinaryPrimitives.ReadInt32BigEndian(second[116..]));
            return;
        }
        Assert.Equal("QPRTAFP   QPADEV0001PRTUSER   123456", Ebcdic.Decode(second[..36]));
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(second[36..]));
        Assert.Equal("QPRINT    QUSRSYS   ", Ebcdic.Decode(second.Slice(52, 20)));
        Assert.Equal("*READY    *STD       5", Ebcdic.Decode(second.Slice(82, 22)));
        Assert.Equal("PRINTER   ", Ebcdic.Decode(second.Slice(136, 10)));
        var extension = BinaryPrimitives.ReadInt32BigEndian(second[148..]);
        Assert.Equal(format == "OSPL0100" ? 160 : 200, extension);
        Assert.Equal(36, BinaryPrimitives.ReadInt32BigEndian(second[152..]));
        Assert.Equal("FAKEIBMI1261012093500" + utcDate + utcTime, Ebcdic.Decode(second.Slice(extension, 34)));
        if (format == "OSPL0200")
            Assert.Equal("1261012093500" + "3" + "".PadRight(10) + utcDate + utcTime, Ebcdic.Decode(second.Slice(160, 37)));
    }

    [Fact]
    public async Task ACSsViewerGetsASpooledFilesPagesAsTextLines()
    {
        await using var server = await RemoteCommandServer.StartAsync();
        await using var client = await Client.SignOnAsync(server.Port, "PRTUSER", "Secret12");

        // QGY/QGYRSPL as ACS calls it for the placeholder QSYSPRT #1 (two pages): option, a handle,
        // format GUIP0100, file and number, job, '1', first page and count, error code, system, date, time.
        async Task<byte[]> CallAsync(string option, int length, int first, int count)
        {
            byte[] file = [.. Ebcdic.Encode("QSYSPRT   "), .. BigEndian(1)];
            return await client.CallAsync("QGYRSPL", "QGY",
                (length, 22, []), (4, 11, BigEndian(length)), (10, 11, Ebcdic.Encode(option.PadRight(10))), (10, 11, new byte[10]),
                (8, 11, Ebcdic.Encode("GUIP0100")), (14, 11, file), (26, 11, Ebcdic.Encode("QPADEV0001PRTUSER   123456")),
                (1, 11, Ebcdic.Encode("1")), (8, 11, [.. BigEndian(first), .. BigEndian(count)]), (4, 11, new byte[4]),
                (8, 11, Ebcdic.Encode("FAKEIBMI")), (7, 11, Ebcdic.Encode("1261012")), (6, 11, Ebcdic.Encode("093000")));
        }

        // Page 1: the header (all of it returned, one page of two, 132 wide), then its block of lines.
        var page = Parameters(await CallAsync("*OPEN", 65536, 1, 1))[0].Data;
        var returned = BinaryPrimitives.ReadInt32BigEndian(page);
        Assert.Equal(returned, BinaryPrimitives.ReadInt32BigEndian(page.AsSpan(4)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(page.AsSpan(8)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(page.AsSpan(20)));
        Assert.Equal(132, BinaryPrimitives.ReadInt32BigEndian(page.AsSpan(24)));
        Assert.Equal(returned - 44, BinaryPrimitives.ReadInt32BigEndian(page.AsSpan(44)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(page.AsSpan(48)));
        Assert.Equal(41, BinaryPrimitives.ReadInt32BigEndian(page.AsSpan(52)));
        Assert.Equal("100000", Ebcdic.Decode(page.AsSpan(56, 6)));
        // The first line: its length (a multiple of 4), page, line number, text length, '000000', the text without trailing blanks.
        var first = page.AsSpan(64);
        var text = "PLACEHOLDER REPORT".PadLeft(75) + "PAGE 1".PadLeft(57);
        Assert.Equal((22 + text.Length + 3) / 4 * 4, BinaryPrimitives.ReadInt32BigEndian(first));
        Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(first[4..]));
        Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(first[8..]));
        Assert.Equal(text.Length, BinaryPrimitives.ReadInt32BigEndian(first[12..]));
        Assert.Equal("000000" + text, Ebcdic.Decode(first.Slice(16, 6 + text.Length)));
        var second = first[BinaryPrimitives.ReadInt32BigEndian(first)..];
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(second[8..]));
        Assert.StartsWith("ITEM 0001", Ebcdic.Decode(second.Slice(22, 9)));

        // Too small a receiver: only the header, which says how much is needed.
        var small = Parameters(await CallAsync("*OPEN", 44, 1, 1))[0].Data;
        Assert.Equal(44, BinaryPrimitives.ReadInt32BigEndian(small));
        Assert.Equal(returned, BinaryPrimitives.ReadInt32BigEndian(small.AsSpan(4)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32BigEndian(small.AsSpan(8)));

        // Page 2 has the heading and one line; there is no page 3 (the call fails, as CPF34C8 on a real system).
        var two = Parameters(await CallAsync("*MORE", 65536, 2, 1))[0].Data;
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(two.AsSpan(48)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(two.AsSpan(52)));
        Assert.Equal(0x0500, BinaryPrimitives.ReadUInt16BigEndian((await CallAsync("*OPEN", 65536, 3, 1)).AsSpan(20)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian((await CallAsync("*CLOSE", 44, 1, 1)).AsSpan(20)));
    }

    private static byte[] BigEndian(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }

    [Fact]
    public async Task ADisabledUserIsRefusedAtStartServer()
    {
        await using var server = await RemoteCommandServer.StartAsync();

        var rc = await Client.StartServerAsync(server.Port, "LOCKUSER", "Locked12");

        Assert.Equal(0x00020002, rc);
        Assert.Equal(1, server.Refusals);
    }

    [Fact]
    public async Task AnUnknownRequestGetsInvalidRequestId()
    {
        await using var server = await RemoteCommandServer.StartAsync();
        await using var client = await Client.SignOnAsync(server.Port, "PRTUSER", "Secret12");

        var reply = await client.RequestAsync(0x1009, []);

        Assert.Equal(0x9009, RequestId(reply));
        Assert.Equal(0x0205, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(20)));
    }

    [Fact]
    public void RunLengthCompressionDecompressesAsJTOpenWritesIt()
    {
        byte[] source = [0x01, 0x1B, 0x1B, 0x1B, 0x00, 0x00, 0x00, 0x03, 0x02];

        Assert.Equal([0x01, 0x1B, 0, 0, 0, 0, 0, 0, 0x02, 0], Rle.Decompress(source, 10));
    }

    private static int RequestId(byte[] reply) => BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(18));

    /// <summary>The reply's parameters after the return code and message count: (length, data) each.</summary>
    private static List<(int Length, byte[] Data)> Parameters(byte[] reply)
    {
        var list = new List<(int, byte[])>();
        for (var at = 24; at + 12 <= reply.Length;)
        {
            var ll = BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(at));
            Assert.Equal(0x1103, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(at + 4)));
            list.Add((BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(at + 6)), reply.AsSpan(at + 12, ll - 12).ToArray()));
            at += ll;
        }
        return list;
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

        /// <summary>Exchange random seeds and start server, as JTOpen opens the server; returns the start server return code.</summary>
        public static async Task<int> StartServerAsync(int port, string user, string password)
        {
            await using var client = await ConnectAsync(port);
            return await client.StartAsync(user, password);
        }

        public static async Task<Client> SignOnAsync(int port, string user, string password)
        {
            var client = await ConnectAsync(port);
            Assert.Equal(0, await client.StartAsync(user, password));
            return client;
        }

        public static async Task<Client> ConnectAsync(int port)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            return new Client(tcp);
        }

        private async Task<int> StartAsync(string user, string password) =>
            BinaryPrimitives.ReadInt32BigEndian((await StartReplyAsync(user, password)).AsSpan(20));

        public async Task<byte[]> StartReplyAsync(string user, string password)
        {
            var seeds = await RequestAsync(0x7001, ClientSeed);
            var level = seeds[5];
            var serverSeed = seeds.AsSpan(24, 8).ToArray();
            var substitute = Passwords.Substitute(level, Passwords.Token(level, user, password), user, ClientSeed, serverSeed, 1);
            return await RequestAsync(0x7002, [level < 2 ? (byte)1 : (byte)3, 1, 0],
                (0x1105, substitute), (0x1104, Ebcdic.Encode(user.PadRight(10))));
        }

        /// <summary>RCCallProgramRequestDataStream: program, library, message option, count, then each parameter.</summary>
        public Task<byte[]> CallAsync(string program, string library, params (int MaxLength, int Usage, byte[] Data)[] parameters)
        {
            byte[] template = [.. Ebcdic.Encode(program.PadRight(10)), .. Ebcdic.Encode(library.PadRight(10)), 5,
                (byte)(parameters.Length >> 8), (byte)parameters.Length];
            var points = parameters.Select(p =>
            {
                var data = new byte[6 + p.Data.Length];
                BinaryPrimitives.WriteInt32BigEndian(data, p.MaxLength);
                BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(4), (ushort)p.Usage);
                p.Data.CopyTo(data, 6);
                return (0x1103, data);
            }).ToArray();
            return RequestAsync(0x1003, template, points);
        }

        public async Task<byte[]> RequestAsync(ushort requestId, byte[] template, params (int Id, byte[] Data)[] codePoints)
        {
            var frame = new byte[20 + template.Length + codePoints.Sum(c => 6 + c.Data.Length)];
            BinaryPrimitives.WriteInt32BigEndian(frame, frame.Length);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(6), 0xE008);
            BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(12), 3);
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
            await _stream.WriteAsync(frame);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var header = new byte[20];
            await _stream.ReadExactlyAsync(header, timeout.Token);
            var reply = new byte[BinaryPrimitives.ReadInt32BigEndian(header)];
            header.CopyTo(reply, 0);
            await _stream.ReadExactlyAsync(reply.AsMemory(20), timeout.Token);
            Assert.Equal(3, BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(12)));
            return reply;
        }

        public ValueTask DisposeAsync()
        {
            _tcp.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
