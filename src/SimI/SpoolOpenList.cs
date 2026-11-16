// The "open list" APIs JTOpen's SpooledFileOpenList calls on the remote command server, which is how
// ACS lists printer output: QGYOLSPL builds the list and answers its handle, QGYGTLE returns entries
// from it and QGYCLST drops it. Entries are in format OSPL0100, OSPL0200, OSPL0300 or OSPL0400, laid
// out as a real IBM i (V7R5) answers them; the list is a snapshot of the spooled files taken when it opens.

using System.Buffers.Binary;
using System.Globalization;

namespace SimI;

internal sealed class SpoolOpenList(Func<IReadOnlyList<SpooledFile>> files, Func<string> defaultUser)
{
    private const int MaxOpen = 50;
    private const string SystemName = "SIMI";

    private readonly Lock _gate = new();
    private readonly Dictionary<int, (List<SpooledFile> Files, string Format)> _lists = [];
    private int _next;

    /// <summary>The length of one entry in each format, or 0 for a format Sim i doesn't answer.</summary>
    public static int RecordLength(string format) => format switch
    {
        "OSPL0100" => 196,
        "OSPL0200" => 236,
        "OSPL0300" or "OSPL0400" => 136,
        _ => 0,
    };

    /// <summary>
    /// QGYOLSPL: the filter parameter (format OSPF0100 or OSPF0200, only its users are used), the
    /// entry format name and who is signed on (whom *CURRENT means). Returns the 80-byte list
    /// information, or null for a format it doesn't know.
    /// </summary>
    public byte[]? Open(byte[] filter, byte[] format, string signedOn)
    {
        var name = Ebcdic.Decode(format).TrimEnd();
        if (RecordLength(name) == 0) return null;
        var users = Users(filter)?.Select(u => u == "*CURRENT" ? signedOn : u).ToList();
        var selected = files().Where(f => users is null || users.Contains(f.User ?? defaultUser(), StringComparer.OrdinalIgnoreCase)).ToList();
        int handle;
        lock (_gate)
        {
            // A client that never closes its lists doesn't fill the memory: the oldest go.
            if (_lists.Count >= MaxOpen) _lists.Remove(_lists.Keys.Min());
            handle = ++_next;
            _lists[handle] = (selected, name);
        }
        return Information(selected.Count, 0, handle, RecordLength(name), 0);
    }

    /// <summary>QGYGTLE: up to <paramref name="number"/> entries from the record <paramref name="start"/> (1-based), as many as fit.</summary>
    public (byte[] Receiver, byte[] Information)? Get(byte[] handle, int receiverLength, int number, int start)
    {
        (List<SpooledFile> Files, string Format) list;
        var key = BinaryPrimitives.ReadInt32BigEndian(handle);
        lock (_gate)
            if (!_lists.TryGetValue(key, out list)) return null;

        var length = RecordLength(list.Format);
        var first = Math.Max(start, 1) - 1;
        var count = Math.Clamp(Math.Min(number, receiverLength / length), 0, Math.Max(list.Files.Count - first, 0));
        var receiver = new byte[count * length];
        for (var i = 0; i < count; i++)
        {
            var f = list.Files[first + i];
            var record = receiver.AsSpan(i * length, length);
            var user = f.User ?? defaultUser();
            switch (list.Format)
            {
                case "OSPL0100": Entry0100(f, user, record, extension: 160); break;
                case "OSPL0200": Entry0200(f, user, record); break;
                default: Entry0300(f, user, record, utc: list.Format == "OSPL0400"); break;
            }
        }
        return (receiver, Information(list.Files.Count, count, key, length, receiver.Length, first + 1));
    }

    /// <summary>QGYCLST.</summary>
    public void Close(byte[] handle)
    {
        lock (_gate) _lists.Remove(BinaryPrimitives.ReadInt32BigEndian(handle));
    }

    /// <summary>
    /// The 80-byte list information: total records, records returned, handle, record length, 'C'
    /// (complete), when it was built, status '2' (ready), the length of what came back, the first record in it.
    /// </summary>
    private static byte[] Information(int total, int returned, int handle, int recordLength, int length, int first = 0)
    {
        var info = new byte[80];
        BinaryPrimitives.WriteInt32BigEndian(info, total);
        BinaryPrimitives.WriteInt32BigEndian(info.AsSpan(4), returned);
        BinaryPrimitives.WriteInt32BigEndian(info.AsSpan(8), handle);
        BinaryPrimitives.WriteInt32BigEndian(info.AsSpan(12), recordLength);
        info[16] = 0xC3;
        Ebcdic.Encode(Cyymmdd(DateTime.Now) + DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture)).CopyTo(info, 17);
        info[30] = 0xF2;
        BinaryPrimitives.WriteInt32BigEndian(info.AsSpan(32), length);
        BinaryPrimitives.WriteInt32BigEndian(info.AsSpan(36), first);
        return info;
    }

    /// <summary>
    /// OSPL0100: file, job, user, number, file number, pages, current page, copies left, queue and
    /// library, user data, status, form type, priority, the internal job and file identifiers, device
    /// type, then (from <paramref name="extension"/>, 36 bytes) the system and when it was opened, local and UTC.
    /// </summary>
    private static void Entry0100(SpooledFile f, string user, Span<byte> r, int extension)
    {
        r.Clear();
        Text(r, 0, 10, f.Name);
        Text(r, 10, 10, f.JobName);
        Text(r, 20, 10, user);
        Text(r, 30, 6, f.JobNumber);
        Number(r, 36, f.Number);
        Number(r, 40, f.Pages);
        Number(r, 44, 0);
        Number(r, 48, 1);
        Text(r, 52, 10, f.Queue);
        Text(r, 62, 10, "QUSRSYS");
        Text(r, 72, 10, f.UserData);
        Text(r, 82, 10, "*READY");
        Text(r, 92, 10, "*STD");
        Text(r, 102, 2, " 5");
        InternalIdentifiers(f, r[104..136]);
        Text(r, 136, 10, "PRINTER");
        Number(r, 148, extension);
        Number(r, 152, 36);
        var (date, time) = Utc(f);
        Text(r, extension, 8, SystemName);
        Text(r, extension + 8, 7, f.Date);
        Text(r, extension + 15, 6, f.Time);
        Text(r, extension + 21, 7, date);
        Text(r, extension + 28, 6, time);
    }

    /// <summary>OSPL0200: OSPL0100's first 160 bytes, then when it was opened, the printer (none assigned), UTC, and the extension at 200.</summary>
    private static void Entry0200(SpooledFile f, string user, Span<byte> r)
    {
        Entry0100(f, user, r, extension: 200);
        var (date, time) = Utc(f);
        Text(r, 160, 7, f.Date);
        Text(r, 167, 6, f.Time);
        Text(r, 173, 1, "3");
        Text(r, 174, 10, "");
        Text(r, 184, 7, date);
        Text(r, 191, 6, time);
    }

    /// <summary>
    /// OSPL0300 (OSPL0400 has the same layout, with the date and time in UTC): job, user, number, file
    /// name, number, status (1 = *READY), opened, schedule, system, user data, form, queue, ASP, size
    /// in units of 4096 bytes, pages, copies left, priority, 3 reserved, IPP job ID.
    /// </summary>
    private static void Entry0300(SpooledFile f, string user, Span<byte> r, bool utc)
    {
        r.Fill(0x40);
        var (date, time) = utc ? Utc(f) : (f.Date, f.Time);
        Text(r, 0, 10, f.JobName);
        Text(r, 10, 10, user);
        Text(r, 20, 6, f.JobNumber);
        Text(r, 26, 10, f.Name);
        Number(r, 36, f.Number);
        Number(r, 40, 1);
        Text(r, 44, 7, date);
        Text(r, 51, 6, time);
        Text(r, 57, 1, "2");
        Text(r, 58, 10, SystemName);
        Text(r, 68, 10, f.UserData);
        Text(r, 78, 10, "*STD");
        Text(r, 88, 10, f.Queue);
        Text(r, 98, 10, "QUSRSYS");
        Number(r, 108, 1);
        Number(r, 112, Math.Max((f.Data.Length + 4095) / 4096, 1));
        Number(r, 116, 4096);
        Number(r, 120, f.Pages);
        Number(r, 124, 1);
        Text(r, 128, 1, "5");
        r.Slice(129, 3).Clear();
        Number(r, 132, 0);
    }

    private static void Text(Span<byte> r, int at, int length, string value) =>
        Ebcdic.Encode(value.Length > length ? value[..length] : value.PadRight(length)).CopyTo(r[at..]);

    private static void Number(Span<byte> r, int at, int value) => BinaryPrimitives.WriteInt32BigEndian(r[at..], value);

    /// <summary>Stand-ins for the 16-byte internal job and spooled file identifiers: the job number and the file number, in binary.</summary>
    private static void InternalIdentifiers(SpooledFile f, Span<byte> ids)
    {
        ids.Clear();
        if (int.TryParse(f.JobNumber, NumberStyles.None, CultureInfo.InvariantCulture, out var job))
        {
            BinaryPrimitives.WriteInt32BigEndian(ids, job);
            BinaryPrimitives.WriteInt32BigEndian(ids[16..], job);
        }
        BinaryPrimitives.WriteInt32BigEndian(ids[28..], f.Number);
    }

    /// <summary>When the file was opened (CYYMMDD, HHMMSS on this machine's clock), in UTC.</summary>
    private static (string Date, string Time) Utc(SpooledFile f)
    {
        if (f.Date.Length != 7 || f.Time.Length != 6
            || !DateTime.TryParseExact($"{19 + f.Date[0] - '0'}{f.Date[1..]}{f.Time}", "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeLocal, out var local))
            return (f.Date, f.Time);
        var utc = local.ToUniversalTime();
        return (Cyymmdd(utc), utc.ToString("HHmmss", CultureInfo.InvariantCulture));
    }

    private static string Cyymmdd(DateTime d) => $"{(d.Year - 1900) / 100}{d.Year % 100:00}{d:MMdd}";

    /// <summary>The users the filter asks for, or null for all of them (*ALL).</summary>
    private static List<string>? Users(byte[] filter)
    {
        var users = new List<string>();
        if (filter.Length < 16) return null;
        // OSPF0200 starts with the length of its fixed part (106); OSPF0100 with the number of users.
        var extended = BinaryPrimitives.ReadInt32BigEndian(filter) == 106;
        var (at, count, step) = extended
            ? (BinaryPrimitives.ReadInt32BigEndian(filter.AsSpan(4)), BinaryPrimitives.ReadInt32BigEndian(filter.AsSpan(8)), 10)
            : (4, BinaryPrimitives.ReadInt32BigEndian(filter), 12);
        for (var i = 0; i < count && at + 10 <= filter.Length; i++, at += step) users.Add(Ebcdic.Decode(filter.AsSpan(at, 10)).TrimEnd());
        return users.Count == 0 || users.Contains("*ALL") ? null : users;
    }
}
