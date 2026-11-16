// A scripted fake of the IBM i Network Print server (as-netprt, server ID E003) for automated
// tests: the random seed exchange and Start Server, checking one placeholder user and password
// (the system's Users) with the DES (QPWDLVL 0-1) or SHA-1 (2-3) password substitute. It checks
// substitutes its own way (see Passwords), not with the server's code. It never logs a substitute.

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace SimI;

public sealed class NetworkPrintServer : HostServer
{
    public const string DefaultUser = Users.DefaultUser;
    public const string DefaultPassword = Users.DefaultPassword;

    private const ushort ServerId = 0xE003;

    private int _signOns;
    private int _refusals;

    private NetworkPrintServer(int port, Action<string>? log, IPAddress? address, Users? users, TlsOptions? tls, Machine? machine)
        : base(port, log, address, tls)
    {
        Users = users ?? Users.Defaults();
        Machine = machine;
    }

    /// <summary>The machine whose programs' printed output it lists after <see cref="Files"/>, or null.</summary>
    public Machine? Machine { get; }

    /// <summary>The placeholder files, then what programs have printed (each as an *SCS file).</summary>
    private List<SpooledFile> AllFiles() => [.. Files, .. Machine?.Store.Spools().Select(SpooledFile.From) ?? []];

    /// <summary>
    /// Listens on the loopback interface, or on <paramref name="address"/> (the demo container
    /// listens on every interface). Port 0 picks a free port (see <see cref="HostServer.Port"/>).
    /// Pass the other servers' <paramref name="users"/> to share them (the default is
    /// <see cref="Users.Defaults"/>). With
    /// <paramref name="tls"/>, it also listens for TLS.
    /// </summary>
    public static Task<NetworkPrintServer> StartAsync(int port = 0, Action<string>? log = null, IPAddress? address = null,
        Users? users = null, TlsOptions? tls = null, Machine? machine = null)
    {
        var server = new NetworkPrintServer(port, log, address, users, tls, machine);
        server.Start();
        return Task.FromResult(server);
    }

    /// <summary>The user profiles it signs on.</summary>
    public Users Users { get; }

    /// <summary>The main user's name (see <see cref="Users.Main"/>), upper case.</summary>
    public string User
    {
        get => Users.Main.User;
        set => Users.Main.User = value;
    }

    /// <summary>The main user's password.</summary>
    public string Password
    {
        get => Users.Main.Password;
        set => Users.Main.Password = value;
    }

    /// <summary>QPWDLVL: 0 or 1 for DES substitutes, 2 or 3 for SHA-1.</summary>
    public int PasswordLevel
    {
        get => Users.PasswordLevel;
        set => Users.PasswordLevel = value;
    }

    /// <summary>When set, Start Server answers with this return code whatever is sent.</summary>
    public int? Refuse { get; set; }

    /// <summary>Successful sign-ons so far.</summary>
    public int SignOns => Volatile.Read(ref _signOns);

    /// <summary>Refused sign-ons so far.</summary>
    public int Refusals => Volatile.Read(ref _refusals);

    protected override string LogName => "print";

    protected override string JobName(int id) => $"{JobNumber(id)}/QUSER/QNPSERVS";

    protected override async Task ServeAsync(Stream stream, int id, bool tls)
    {
        var signedOn = await SignOnAsync(stream, id);
        if (signedOn is null) return;
        var open = new Dictionary<int, OpenFile>();
        while (true)
        {
            var request = await ReadFrameAsync(stream, Stopping);
            int replies = 0, rc = 0;
            foreach (var reply in Answer(request, open, id, signedOn))
            {
                replies++;
                rc = BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(26));
                await stream.WriteAsync(reply, Stopping);
            }
            Log($"print #{id} {Describe(request)}: {(replies > 1 ? $"{replies} replies, last " : "")}rc {rc:X4}");
        }
    }

    /// <summary>A request in a log line: what it does to which object, and the attributes it asks for.</summary>
    private static string Describe(byte[] request)
    {
        var objectType = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(18));
        var action = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(20));
        var name = action switch
        {
            0x0002 => "open", 0x0003 => "read", 0x0005 => "close", 0x000A => "retrieve message", 0x000E => "set attributes",
            0x000F => "retrieve attributes", 0x0016 => "list", 0x0018 => "retrieve open attributes",
            0x0019 => "select page", 0x001A => "open by page", _ => $"action {action:X4}",
        };
        var target = objectType switch { SpooledFileObject => "spooled file", ServerObject => "server", _ => $"object {objectType:X4}" };
        var asked = CodePoints(request).GetValueOrDefault(0x0007);
        var count = asked is { Length: >= 2 } ? BinaryPrimitives.ReadUInt16BigEndian(asked) : 0;
        return $"{name} of {target}{(count > 0 ? $" ({count} attributes)" : "")}";
    }

    /// <summary>Signs the client on; returns its user, or null when refused.</summary>
    private async Task<string?> SignOnAsync(Stream stream, int id)
    {
        var profile = await StartServerAsync(stream, id, ServerId, Users, Refuse);
        Interlocked.Increment(ref profile is null ? ref _refusals : ref _signOns);
        return profile?.User;
    }

    // ---- Network Print requests, after sign-on ----

    private const ushort SpooledFileObject = 0x0001;
    private const ushort ServerObject = 0x0008;

    /// <summary>The replies to one request: a 12-byte template (action, flags, return code) and code points.</summary>
    private IEnumerable<byte[]> Answer(byte[] request, Dictionary<int, OpenFile> open, int id, string signedOn)
    {
        var objectType = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(18));
        var correlation = BinaryPrimitives.ReadInt32BigEndian(request.AsSpan(12));
        var action = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(20));
        var points = CodePoints(request);
        Interlocked.Increment(ref _requests);

        byte[] Reply(int rc, bool last = true, params (int Id, byte[] Data)[] codePoints) =>
            NpFrame((ushort)(objectType | 0x8000), correlation, action, rc, last ? 0u : MoreFlag, codePoints);
        byte[] Flagged(int rc, uint flags, params (int Id, byte[] Data)[] codePoints) =>
            NpFrame((ushort)(objectType | 0x8000), correlation, action, rc, flags, codePoints);

        switch (objectType, action)
        {
            // Retrieve the server's attributes: the ones asked for (code point 7) that it has, or its CCSID.
            case (ServerObject, 0x000F):
            {
                var job = JobName(id).Split('/');
                var server = new Dictionary<int, object>
                {
                    [0x008A] = 37, [0x003B] = job[2], [0x003C] = job[0], [0x003E] = job[1], [0x008D] = "V7R5M0",
                };
                yield return Reply(0, true, (0x0008, Attributes(Wanted(points, server, [0x008A]))));
                yield break;
            }

            // Set the server's attributes (JTOpen sends the client's language, NLV 2924): a real
            // system answers success and the attributes as sent.
            case (ServerObject, 0x000E):
                yield return Reply(0, true, (0x0008, points.GetValueOrDefault(0x0008) ?? Attributes()));
                yield break;

            case (SpooledFileObject, 0x0016):
            {
                // The selection: a user (*CURRENT, the one signed on; *ALL; or a name) and an output queue (*ALL or a name).
                var selection = ReadAttributes(points.GetValueOrDefault(0x0006) ?? []);
                var user = (selection.GetValueOrDefault(0x003E) as string ?? "*CURRENT").Trim();
                if (user == "*CURRENT") user = signedOn;
                var queue = (selection.GetValueOrDefault(0x004A) as string ?? "").Trim();
                var files = AllFiles().Where(f => user == "*ALL" || string.Equals(Owner(f), user, StringComparison.OrdinalIgnoreCase))
                    .Where(f => queue is "" or "*ALL" || string.Equals(f.Queue, queue, StringComparison.OrdinalIgnoreCase)).ToList();
                if (files.Count == 0)
                {
                    yield return Reply(0x0014);
                    yield break;
                }
                for (var i = 0; i < files.Count; i++)
                {
                    var f = files[i];
                    yield return Reply(0, i == files.Count - 1,
                        (0x0001, Attributes((0x003B, f.JobName), (0x003E, Owner(f)), (0x003C, f.JobNumber), (0x0068, f.Name), (0x0069, f.Number),
                            (0x00FB, SystemName), (0x0022, f.Date), (0x006E, f.Time))),
                        (0x0008, Attributes(Wanted(points, FileAttributes(f), [.. FileAttributes(f).Keys]))));
                }
                yield break;
            }

            // Open a spooled file to read it whole (0x0002) or a page at a time (0x001A). The handle
            // is 9 bytes, as a real system's: 02, the number, 4 zeros.
            case (SpooledFileObject, 0x0002):
            case (SpooledFileObject, 0x001A):
            {
                var file = Find(points);
                if (file is null)
                {
                    yield return Reply(0x001E);
                    yield break;
                }
                if (file.Unauthorized)
                {
                    yield return Reply(0x0009, true,
                        (0x0008, Attributes((0x0093, "CPF3303"), (0x0080, "Not authorized to spooled file (placeholder)."))));
                    yield break;
                }
                var handle = Interlocked.Increment(ref _nextHandle);
                open[handle] = new OpenFile(file, Pages(file.Data)) { Page = action == 0x001A ? 1 : 0 };
                yield return Reply(0, true, (0x000C, [0x02, .. BigEndian(handle), 0, 0, 0, 0]));
                yield break;
            }

            // Read up to the bytes asked for (attribute 7D). Whole: the chunk that ends the file is
            // flagged 40, and a read after it answers 13 (end of file). By page: the chunk that ends
            // the page is flagged 20, and a read after it answers 20 (end of page) with no data.
            case (SpooledFileObject, 0x0003):
            {
                if (OpenOf(points, open) is not { } o)
                {
                    yield return Reply(0x0003);
                    yield break;
                }
                var wanted = ReadAttributes(points.GetValueOrDefault(0x0008) ?? []).GetValueOrDefault(0x007D) as int? ?? ReadChunk;
                var (start, end) = o.Page == 0 ? (0, o.File.Data.Length) : o.Pages[o.Page - 1];
                var flag = o.Page == 0 ? EndOfFileFlag : EndOfPageFlag;
                if (o.Offset >= end)
                {
                    yield return o.Page == 0 ? Flagged(0x0013, flag) : Flagged(0x0020, flag, (0x0009, []));
                    yield break;
                }
                var chunk = o.File.Data.AsSpan(o.Offset, Math.Min(Math.Min(Math.Max(wanted, 1), ReadChunk), end - o.Offset)).ToArray();
                o.Offset += chunk.Length;
                yield return Flagged(0, o.Offset >= end ? flag : 0, (0x0009, chunk));
                yield break;
            }

            // An open file's attributes: its pages (6F), the bytes of the page being read, or of the
            // whole file (7D), and DA (*NO, as a real system answers).
            case (SpooledFileObject, 0x0018):
            {
                if (OpenOf(points, open) is not { } o)
                {
                    yield return Reply(0x0003);
                    yield break;
                }
                var (start, end) = o.Page == 0 ? (0, o.File.Data.Length) : o.Pages[o.Page - 1];
                var known = new Dictionary<int, object> { [0x006F] = o.Pages.Count, [0x007D] = end - start, [0x00DA] = "*NO" };
                yield return Reply(0, true, (0x0008, Attributes(Wanted(points, known, [.. known.Keys]))));
                yield break;
            }

            // Go to a page (attribute D7) of a file opened a page at a time; past the last one answers 21.
            case (SpooledFileObject, 0x0019):
            {
                if (OpenOf(points, open) is not { } o)
                {
                    yield return Reply(0x0003);
                    yield break;
                }
                var page = ReadAttributes(points.GetValueOrDefault(0x0008) ?? []).GetValueOrDefault(0x00D7) as int? ?? 0;
                if (page < 1 || page > o.Pages.Count)
                {
                    yield return Reply(0x0021);
                    yield break;
                }
                o.Page = page;
                o.Offset = o.Pages[page - 1].Start;
                yield return Reply(0);
                yield break;
            }

            // Retrieve one spooled file's attributes: the ones asked for (code point 7) that Sim i
            // has. JTOpen asks for 7D (its size) before reading, and refuses to read without it.
            case (SpooledFileObject, 0x000F):
            {
                var file = Find(points);
                if (file is null)
                {
                    yield return Reply(0x001E);
                    yield break;
                }
                var all = FileAttributes(file);
                yield return Reply(0, true, (0x0008, Attributes(Wanted(points, all, [.. all.Keys]))));
                yield break;
            }

            case (SpooledFileObject, 0x0005):
                if (points.TryGetValue(0x000C, out var closing)) open.Remove(Handle(closing));
                Interlocked.Increment(ref _closes);
                yield return Reply(0);
                yield break;

            default:
                yield return Reply(0x0003);
                yield break;
        }
    }

    private const string SystemName = "SIMI";

    // A reply template's flags: more replies follow; this chunk ends the file; this chunk ends the page.
    private const uint MoreFlag = 0x80000000, EndOfFileFlag = 0x40000000, EndOfPageFlag = 0x20000000;

    /// <summary>A spooled file open for reading: where the next read starts, and the page (0 when read whole).</summary>
    private sealed class OpenFile(SpooledFile file, List<(int Start, int End)> pages)
    {
        public SpooledFile File { get; } = file;
        public List<(int Start, int End)> Pages { get; } = pages;
        public int Page { get; set; }
        public int Offset { get; set; }
    }

    /// <summary>The file a request's code point 1 names (file name, job number, file number), or null.</summary>
    private SpooledFile? Find(Dictionary<int, byte[]> points)
    {
        var key = ReadAttributes(points.GetValueOrDefault(0x0001) ?? []);
        return AllFiles().FirstOrDefault(f => Equals(key.GetValueOrDefault(0x0068), f.Name)
                                             && Equals(key.GetValueOrDefault(0x003C), f.JobNumber)
                                             && Equals(key.GetValueOrDefault(0x0069), f.Number));
    }

    /// <summary>The number in a handle: 9 bytes (02, the number, 4 zeros), or the plain 4 older clients may send.</summary>
    private static int Handle(byte[] handle) =>
        handle.Length >= 5 && handle[0] == 0x02 ? BinaryPrimitives.ReadInt32BigEndian(handle.AsSpan(1)) : BinaryPrimitives.ReadInt32BigEndian(handle);

    private static OpenFile? OpenOf(Dictionary<int, byte[]> points, Dictionary<int, OpenFile> open) =>
        points.TryGetValue(0x000C, out var handle) && handle.Length >= 4 && open.TryGetValue(Handle(handle), out var o) ? o : null;

    /// <summary>Where each page is in the data: up to and including each form feed (0C), and whatever follows the last; one page when there are none.</summary>
    private static List<(int Start, int End)> Pages(byte[] data)
    {
        var pages = new List<(int Start, int End)>();
        var start = 0;
        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] != 0x0C) continue;
            pages.Add((start, i + 1));
            start = i + 1;
        }
        if (start < data.Length || pages.Count == 0) pages.Add((start, data.Length));
        return pages;
    }

    /// <summary>Who owns a file: the program's user, or the main user for the placeholder files.</summary>
    private string Owner(SpooledFile f) => f.User ?? User;

    /// <summary>The attributes Sim i knows for a spooled file, by Network Print attribute ID.</summary>
    /// <summary>
    /// A spooled file's attributes, as a real system (V7R5) answers them for a printer file's *SCS
    /// output: names and values padded as it pads them, measures in packed decimal (type 7), and the
    /// job's CCSID (14E), which ACS's viewer needs to show the file.
    /// </summary>
    private Dictionary<int, object> FileAttributes(SpooledFile f)
    {
        static string W(string s, int width = 10) => s.PadRight(width);
        var none = W("*NONE");
        var no = W("*NO");
        var devd = W("*DEVD");
        var blank = W("");
        return new()
        {
            [0x000A] = no, [0x000B] = no, [0x0011] = Packed.Of(0), [0x0012] = Packed.Of(-1), [0x0013] = blank,
            [0x0014] = W("*FRONTOVL"), [0x0015] = Packed.Of(0), [0x0016] = Packed.Of(0), [0x0017] = Packed.Of(10),
            [0x0018] = blank, [0x0019] = devd, [0x001A] = W("*FNTCHRSET"), [0x001C] = 1, [0x001D] = 1, [0x001E] = 0,
            [0x0022] = f.Date, [0x0024] = -1, [0x002B] = 0, [0x002C] = 0, [0x002D] = no, [0x002E] = W("*CPI"),
            [0x002F] = devd, [0x0030] = "*STD", [0x0031] = Packed.Of(0), [0x0032] = Packed.Of(-2), [0x0033] = blank,
            [0x0034] = none, [0x0035] = Packed.Of(0), [0x0036] = Packed.Of(0), [0x0037] = devd, [0x0038] = 0,
            [0x0039] = no, [0x003B] = f.JobName, [0x003C] = f.JobNumber, [0x003E] = Owner(f), [0x003F] = 0,
            [0x0040] = Packed.Of(6), [0x0042] = 0, [0x0048] = " 5", [0x0049] = "QUSRSYS", [0x004A] = f.Queue,
            [0x004C] = 60, [0x004E] = Packed.Of(66), [0x004F] = W("*ROWCOL"), [0x0051] = Packed.Of(132), [0x0052] = 1,
            [0x0053] = Packed.Of(0), [0x0054] = W("*CONTENT"), [0x0055] = no, [0x0056] = W("*STD"), [0x0058] = W("", 30),
            [0x0059] = blank, [0x005A] = f.DeviceType, [0x005B] = W("QSYS"), [0x005C] = W(f.Name), [0x005F] = -1,
            [0x0061] = no, [0x0062] = "*", [0x0063] = -1, [0x0064] = no, [0x0066] = 1, [0x0068] = f.Name,
            [0x0069] = f.Number, [0x006A] = "*READY", [0x006B] = W("*FILEEND"), [0x006C] = 0, [0x006E] = f.Time,
            [0x006F] = f.Pages, [0x0072] = W("*INCH"), [0x0073] = W("", 100), [0x0074] = f.UserData,
            [0x007D] = f.Data.Length,
            [0x0099] = no, [0x009A] = W("*YES"), [0x009B] = no, [0x009C] = -1, [0x009D] = W("*YES"),
            [0x00A2] = W("*NONE", 255), [0x00A3] = Bytes(W(""), 2), [0x00A4] = blank, [0x00A5] = none, [0x00A6] = blank,
            [0x00B6] = none, [0x00B7] = blank, [0x00BA] = "3", [0x00BC] = SystemName, [0x00BD] = W("SIMNET", 8),
            [0x00C0] = 0, [0x00C2] = W("*TEXT"), [0x00C3] = W("*CTLCHAR"), [0x00C4] = none, [0x00C6] = no,
            [0x00D9] = new Packed(f.Data.Length), [0x00DA] = no, [0x00E1] = 0x40404040, [0x00E2] = "0", [0x00E3] = "0",
            [0x00E4] = 0, [0x00E5] = W("", 41), [0x00E6] = W("", 41), [0x00E7] = W("", 41), [0x00E8] = W("", 41),
            [0x00E9] = W("", 41), [0x00EA] = W("", 7), [0x00EB] = W("", 7), [0x00EC] = W("", 6), [0x00ED] = W("", 6),
            [0x00EE] = none, [0x00EF] = Packed.Of(-1), [0x00F0] = 0, [0x00F2] = none, [0x00F3] = 0, [0x00F5] = blank,
            [0x00F6] = none, [0x00F8] = none, [0x00F9] = Bytes(W("*DEVD"), 2), [0x00FA] = W("", 41),
            [0x00FB] = SystemName, [0x00FC] = 1,
            [0x0109] = W("", 15), [0x010A] = W("*SYSBAS"), [0x010B] = devd, [0x010C] = W("*JOB"), [0x010D] = f.Date,
            [0x010E] = "0", [0x010F] = blank, [0x0110] = blank, [0x0111] = "N", [0x0112] = blank, [0x0113] = W("*SYSVAL"),
            [0x0114] = Packed.Of(0), [0x0115] = W("", 100), [0x0116] = "0", [0x0117] = blank, [0x0118] = blank,
            [0x0119] = Packed.Of(0), [0x011A] = "N", [0x011B] = "N", [0x011C] = "N", [0x011D] = "Y", [0x011E] = "N",
            [0x011F] = "N", [0x0120] = "N", [0x0121] = "N", [0x0122] = W("", 8), [0x0123] = "N", [0x0124] = "N",
            [0x0125] = "N", [0x0126] = "N", [0x0127] = "N", [0x0128] = "N", [0x0129] = "N", [0x012A] = "N",
            [0x012B] = "N", [0x012C] = "N", [0x012D] = "N", [0x012E] = "N", [0x012F] = "N", [0x0130] = "N",
            [0x0132] = Bytes(W("", 4), 2), [0x0133] = blank, [0x0134] = W("*FONT"), [0x0135] = "N",
            [0x0136] = Math.Max((f.Data.Length + 4095) / 4096, 1), [0x0137] = 4096, [0x0138] = Packed.Of(0),
            [0x0139] = -1, [0x013A] = W(Owner(f)), [0x013C] = "N", [0x0141] = W("", 7), [0x0142] = "", [0x0143] = "",
            [0x0144] = blank, [0x0145] = none, [0x0146] = "", [0x0147] = 0, [0x0148] = "", [0x0149] = "",
            [0x014A] = W("", 7), [0x014B] = "", [0x014C] = W("", 7), [0x014D] = "", [0x014E] = 37,
        };
    }

    /// <summary>A packed decimal attribute (type 7): 15 digits and a sign, as the integer the digits spell.</summary>
    private readonly record struct Packed(long Digits)
    {
        /// <summary>A measure: the digits carry 5 decimal places (10 CPI is 000000001000000F; -1, meaning *DEVD, ends D).</summary>
        public static Packed Of(decimal value) => new((long)(value * 100000));

        public byte[] Encode()
        {
            var digits = Math.Abs(Digits).ToString("D15", System.Globalization.CultureInfo.InvariantCulture);
            var bytes = new byte[8];
            for (var i = 0; i < 15; i++) bytes[i / 2] |= (byte)((digits[i] - '0') << (i % 2 == 0 ? 4 : 0));
            bytes[7] |= (byte)(Digits < 0 ? 0x0D : 0x0F);
            return bytes;
        }
    }

    /// <summary>An attribute a real system answers as bytes (type 9): the EBCDIC of the text, then zeros.</summary>
    private static byte[] Bytes(string text, int zeros) => [.. Ebcdic.Encode(text), .. new byte[zeros]];

    /// <summary>
    /// The attributes a request asks for in code point 7 (a count, the entry size 2, the IDs) that
    /// <paramref name="known"/> has, in the order asked; <paramref name="otherwise"/> when it asks for none.
    /// </summary>
    private static (int Id, object Value)[] Wanted(Dictionary<int, byte[]> points, Dictionary<int, object> known, int[] otherwise)
    {
        var ids = otherwise.ToList();
        if (points.TryGetValue(0x0007, out var list) && list.Length >= 4)
        {
            var count = BinaryPrimitives.ReadUInt16BigEndian(list);
            var size = Math.Max((int)BinaryPrimitives.ReadUInt16BigEndian(list.AsSpan(2)), 2);
            ids = [.. Enumerable.Range(0, count).Where(i => 4 + i * size + 2 <= list.Length)
                .Select(i => (int)BinaryPrimitives.ReadUInt16BigEndian(list.AsSpan(4 + i * size)))];
        }
        return [.. ids.Where(known.ContainsKey).Select(i => (i, known[i]))];
    }

    /// <summary>Read replies carry at most this much, so files take several reads.</summary>
    public int ReadChunk { get; set; } = 4096;

    /// <summary>The user's spooled files, in list order.</summary>
    public List<SpooledFile> Files { get; } = SpooledFile.Defaults();

    /// <summary>Network Print requests answered so far, after sign-on.</summary>
    public int Requests => Volatile.Read(ref _requests);

    /// <summary>Spooled files closed so far.</summary>
    public int Closes => Volatile.Read(ref _closes);

    private int _requests;
    private int _closes;
    private int _nextHandle;

    private static byte[] NpFrame(ushort replyId, int correlation, ushort action, int rc, uint flags, (int Id, byte[] Data)[] codePoints)
    {
        var length = 20 + 12 + codePoints.Sum(c => 6 + c.Data.Length);
        var frame = new byte[length];
        BinaryPrimitives.WriteInt32BigEndian(frame, length);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(6), ServerId);
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(12), correlation);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16), 12);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(18), replyId);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(20), action);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(22), flags);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(26), (ushort)rc);
        var at = 32;
        foreach (var (id, data) in codePoints)
        {
            BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(at), 6 + data.Length);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(at + 4), (ushort)id);
            data.CopyTo(frame, at + 6);
            at += 6 + data.Length;
        }
        return frame;
    }

    /// <summary>An attribute code point's data: count, entry size 12, entries, then values (offsets count the 6-byte prefix).</summary>
    private static byte[] Attributes(params (int Id, object Value)[] attributes)
    {
        var entries = new List<byte>();
        var values = new List<byte>();
        var valuesStart = 6 + 4 + 12 * attributes.Length;
        foreach (var (id, value) in attributes)
        {
            var (type, bytes) = value switch
            {
                int n => ((ushort)3, BigEndian(n)),
                Packed p => ((ushort)7, p.Encode()),
                byte[] raw => ((ushort)9, raw),
                _ => ((ushort)5, (byte[])[.. Ebcdic.Encode((string)value), 0]),
            };
            entries.AddRange([(byte)(id >> 8), (byte)id, (byte)(type >> 8), (byte)type]);
            entries.AddRange(BigEndian(bytes.Length));
            entries.AddRange(BigEndian(valuesStart + values.Count));
            values.AddRange(bytes);
        }
        return [(byte)(attributes.Length >> 8), (byte)attributes.Length, 0, 12, .. entries, .. values];
    }

    private static Dictionary<int, object> ReadAttributes(byte[] data)
    {
        var result = new Dictionary<int, object>();
        if (data.Length < 4) return result;
        var count = BinaryPrimitives.ReadUInt16BigEndian(data);
        for (var i = 0; i < count; i++)
        {
            var entry = data.AsSpan(4 + 12 * i, 12);
            var id = BinaryPrimitives.ReadUInt16BigEndian(entry);
            var type = BinaryPrimitives.ReadUInt16BigEndian(entry[2..]);
            var length = BinaryPrimitives.ReadInt32BigEndian(entry[4..]);
            var value = data.AsSpan(BinaryPrimitives.ReadInt32BigEndian(entry[8..]) - 6, length);
            result[id] = type == 3 ? BinaryPrimitives.ReadInt32BigEndian(value) : Ebcdic.Decode(value.TrimEnd((byte)0));
        }
        return result;
    }
}

/// <summary>One spooled file Sim i lists and serves. Placeholder text only.</summary>
public sealed record SpooledFile(
    string Name, int Number, string JobName, string JobNumber, string DeviceType, int Pages, string UserData, string Queue,
    string Date, string Time, byte[] Data)
{
    /// <summary>Opening it answers with a CPF message instead.</summary>
    public bool Unauthorized { get; init; }

    /// <summary>Who owns it: the user of the program that printed it. Placeholder files belong to the server's main user.</summary>
    public string? User { get; init; }

    /// <summary>
    /// A two-page *SCS report 132 columns wide, an *AFPDS file (bytes that aren't SCS) and a
    /// *USERASCII PDF.
    /// </summary>
    public static List<SpooledFile> Defaults() =>
    [
        new("QSYSPRT", 1, "QPADEV0001", "123456", "*SCS", 2, "PLACEHOLDR", "QPRINT", "1261012", "093000", WideReport()),
        new("QPRTAFP", 2, "QPADEV0001", "123456", "*AFPDS", 1, "", "QPRINT", "1261012", "093500",
            [0x5A, 0x00, 0x10, 0xD3, 0xA8, 0xA8, 0x00, 0x00, 0x00, 0xC1, 0xC2, 0xC3, 0xC4, 0xC5, 0xC6, 0xC7, 0xC8]),
        new("QPDFOUT", 3, "BATCHJOB", "123457", "*USERASCII", 1, "PDFDATA", "QPRINT", "1261012", "100000",
            "%PDF-1.4\n% placeholder spooled PDF\n%%EOF\n"u8.ToArray()),
    ];

    /// <summary>What a program printed, as an *SCS file: lines of EBCDIC, 60 to a page (or as its form feed lines say), a form feed ending each.</summary>
    public static SpooledFile From(SpoolRecord r)
    {
        List<byte> scs = [0x2B, 0xC1, 0x02, (byte)Math.Min(r.Width, 255), 0x2B, 0xC2, 0x02, 66];
        var lines = r.Text.Length == 0 ? [] : r.Text.Split('\n');
        // A printer file's DDS makes its own pages: a form feed line between them. Otherwise 60 lines to a page.
        var paged = lines.Contains("\f");
        for (var i = 0; i < lines.Length; i++)
        {
            if (paged && lines[i] == "\f")
            {
                scs.Add(0x0C);
                continue;
            }
            if (!paged && i > 0 && i % 60 == 0) scs.Add(0x0C);
            scs.AddRange(Ebcdic.Encode(lines[i]));
            scs.Add(0x15);
        }
        scs.Add(0x0C);
        var c = r.Created;
        return new SpooledFile(r.Name, (int)r.Id, "SIMI", ((int)(r.Id % 900000) + 100000).ToString(), "*SCS", r.Pages, "", "QPRINT",
            $"{(c.Year - 1900) / 100}{c.Year % 100:00}{c.Month:00}{c.Day:00}", c.ToString("HHmmss"), [.. scs]) { User = r.User };
    }

    /// <summary>SCS: forms 132 by 66, a heading and lines on page one, a form feed, one line on page two.</summary>
    public static byte[] WideReport()
    {
        List<byte> scs = [0x2B, 0xC1, 0x02, 132, 0x2B, 0xC2, 0x02, 66];
        void Line(string text)
        {
            scs.AddRange(Ebcdic.Encode(text));
            scs.Add(0x15);
        }
        Line("PLACEHOLDER REPORT".PadLeft(75) + "PAGE 1".PadLeft(57));
        for (var i = 1; i <= 40; i++) Line($"ITEM {i:D4}  {new string('X', 20)}  {i * 3,10}");
        scs.Add(0x0C);
        Line("PLACEHOLDER REPORT".PadLeft(75) + "PAGE 2".PadLeft(57));
        Line("END OF REPORT");
        scs.Add(0x0C);
        return [.. scs];
    }
}
