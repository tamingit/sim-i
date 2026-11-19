// A real IBM i's records, read as the system keeps them: each field at its place in the record
// buffer, as DSPFFD describes it. Characters are EBCDIC in the field's CCSID, numbers packed, zoned
// or binary, and dates, times and timestamps text in the field's format.

using System.Globalization;
using System.Text;

namespace SimI.Remote;

/// <summary>
/// A field of a file on the remote system, as DSPFFD describes it: its DDS data type (A, P, S, B,
/// F, L, T, Z, H, G ...), where it is in the record buffer (from 0) and how long, in bytes.
/// </summary>
internal sealed record RemoteField(string Name, char Type, int Offset, int Bytes, int Digits = 0, int Decimals = 0, bool Varying = false,
    bool Nullable = false, int Ccsid = 37, string Format = "", int Characters = 0)
{
    /// <summary>The column's long SQL name, when it has one that isn't its field name.</summary>
    public string Alias { get; init; } = "";
    public string Text { get; init; } = "";
    public string RecordFormat { get; init; } = "";
    public bool Identity { get; init; }

    /// <summary>In a logical file, the physical file's field this one is (its own name, unless it's renamed).</summary>
    public string Internal { get; init; } = "";

    /// <summary>In a logical file, the part of that field this one is (SST): where it starts, from 1, and how long; null when it's the whole field.</summary>
    public (int Start, int Length)? Substring { get; init; }

    /// <summary>The most characters a character field holds.</summary>
    public int Length => Type == 'G' ? (Characters > 0 ? Characters : (Bytes - (Varying ? 2 : 0)) / 2) : Bytes - (Varying ? 2 : 0);

    /// <summary>Whether the field's values can be received: not a LOB, a DATALINK, a ROWID or XML.</summary>
    public bool Supported => Type is 'A' or 'P' or 'S' or 'B' or 'F' or 'L' or 'T' or 'Z' or 'H' or 'G' or 'O' or 'J' or 'E' or '5' or '6';

    /// <summary>The field's type as SQL names it.</summary>
    public string SqlType => Type switch
    {
        'P' => $"DECIMAL({Digits}, {Decimals})",
        'S' => $"NUMERIC({Digits}, {Decimals})",
        'B' when Decimals > 0 => $"DECIMAL({Digits}, {Decimals})",
        'B' => Bytes <= 2 ? "SMALLINT" : Bytes <= 4 ? "INTEGER" : "BIGINT",
        'F' or '6' => "FLOAT",
        'L' => "DATE",
        'T' => "TIME",
        'Z' => "TIMESTAMP",
        _ => Varying ? $"VARCHAR({Math.Max(1, Length)})" : $"CHAR({Math.Max(1, Length)})",
    };

    /// <summary>The field as DDS has it: its data type, and its length and decimals (blank where DDS takes none).</summary>
    public (char Type, string Length, string Decimals) Dds => Type switch
    {
        'P' or 'S' => (Type, Digits.ToString(), Decimals.ToString()),
        'B' => ('B', Digits.ToString(), Decimals.ToString()),
        'F' or '6' => ('F', Type == '6' || Bytes > 4 ? "15" : "7", "0"),
        'L' or 'T' or 'Z' => (Type, "", ""),
        _ => ('A', Math.Max(1, Length).ToString(), ""),
    };
}

internal static class RemoteRecords
{
    static RemoteRecords() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static readonly Dictionary<int, Encoding?> Encodings = [];

    /// <summary>The encoding of a CCSID; null for 37, and for those not known (and 65535, no conversion), which are read as 37.</summary>
    private static Encoding? EncodingOf(int ccsid)
    {
        if (ccsid is 37 or 0 or 65535) return null;
        lock (Encodings)
        {
            if (Encodings.TryGetValue(ccsid, out var known)) return known;
            Encoding? found;
            try
            {
                found = ccsid switch
                {
                    1208 => Encoding.UTF8,
                    1200 or 13488 or 61952 => Encoding.BigEndianUnicode,
                    819 => Encoding.Latin1,
                    _ => Encoding.GetEncoding(ccsid),
                };
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException)
            {
                found = null;
            }
            return Encodings[ccsid] = found;
        }
    }

    /// <summary>Characters of a CCSID as text.</summary>
    public static string Characters(ReadOnlySpan<byte> bytes, int ccsid) =>
        EncodingOf(ccsid) is { } encoding ? encoding.GetString(bytes) : Ebcdic.Decode(bytes);

    /// <summary>
    /// A source record's text without its trailing blanks. Its control characters are blanks: the bytes
    /// X'00' to X'3F' are 5250 display attributes when SEU shows a line (a comment in white or red, say),
    /// and as text they'd be C1 controls an editor draws as boxes. One for one, so columns stay put.
    /// </summary>
    public static string SourceLine(ReadOnlySpan<byte> bytes, int ccsid)
    {
        var text = Characters(bytes, ccsid).ToCharArray();
        for (var i = 0; i < text.Length; i++)
            if (char.IsControl(text[i])) text[i] = ' ';
        return new string(text).TrimEnd();
    }

    /// <summary>
    /// A field's value in a record, as text PostgreSQL reads: characters without their trailing blanks,
    /// a number with a decimal point, a date as yyyy-mm-dd. A value that can't be read (a packed
    /// field that doesn't hold a number, say) is the type's zero, and <paramref name="bad"/> counts it.
    /// </summary>
    public static string Value(ReadOnlySpan<byte> record, RemoteField field, ref long bad)
    {
        var bytes = record.Slice(field.Offset, field.Bytes);
        switch (field.Type)
        {
            case 'P':
                return Packed(bytes, field.Decimals, ref bad);
            case 'S':
                return Zoned(bytes, field.Decimals, ref bad);
            case 'B':
            {
                long value = bytes.Length switch
                {
                    2 => System.Buffers.Binary.BinaryPrimitives.ReadInt16BigEndian(bytes),
                    4 => System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes),
                    8 => System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(bytes),
                    _ => 0,
                };
                return field.Decimals == 0 ? value.ToString(CultureInfo.InvariantCulture)
                    : (value / (decimal)Math.Pow(10, field.Decimals)).ToString(CultureInfo.InvariantCulture);
            }
            case 'F':
            {
                var value = bytes.Length == 4 ? System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(bytes)
                    : bytes.Length == 8 ? System.Buffers.Binary.BinaryPrimitives.ReadDoubleBigEndian(bytes) : 0;
                return double.IsFinite(value) ? value.ToString("R", CultureInfo.InvariantCulture) : "0";
            }
            case '6':
                // DECFLOAT: its decimal encoding isn't read.
                bad++;
                return "0";
            case 'L':
                return Date(Characters(bytes, field.Ccsid), field.Format, ref bad);
            case 'T':
                return Time(Characters(bytes, field.Ccsid), ref bad);
            case 'Z':
                return Timestamp(Characters(bytes, field.Ccsid), ref bad);
            default:
            {
                if (field.Varying)
                {
                    var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(bytes) * (field.Type == 'G' ? 2 : 1);
                    return Characters(bytes.Slice(2, Math.Min(length, bytes.Length - 2)), field.Ccsid).Replace('\0', ' ');
                }
                return Characters(bytes, field.Type == 'G' && EncodingOf(field.Ccsid) is null ? 1200 : field.Ccsid).Replace('\0', ' ').TrimEnd(' ');
            }
        }
    }

    private static string Number(Span<char> digits, int count, bool negative, int decimals)
    {
        // Leading zeros go, but for one before the decimal point.
        var whole = count - decimals;
        var first = 0;
        while (first < whole - 1 && digits[first] == '0') first++;
        var text = new StringBuilder(count + 2);
        if (negative) text.Append('-');
        if (whole <= 0) text.Append('0');
        else text.Append(digits[first..whole]);
        if (decimals > 0) text.Append('.').Append(digits[Math.Max(whole, 0)..count]);
        return text.ToString();
    }

    public static string Packed(ReadOnlySpan<byte> bytes, int decimals, ref long bad)
    {
        Span<char> digits = stackalloc char[bytes.Length * 2];
        var count = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            var high = bytes[i] >> 4;
            var low = bytes[i] & 0x0f;
            if (high > 9 || (i < bytes.Length - 1 && low > 9))
            {
                bad++;
                return "0";
            }
            digits[count++] = (char)('0' + high);
            if (i < bytes.Length - 1) digits[count++] = (char)('0' + low);
        }
        var sign = bytes[^1] & 0x0f;
        if (sign < 0x0a)
        {
            bad++;
            return "0";
        }
        return Number(digits, count, sign is 0x0d or 0x0b, Math.Min(decimals, count));
    }

    public static string Zoned(ReadOnlySpan<byte> bytes, int decimals, ref long bad)
    {
        Span<char> digits = stackalloc char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            var low = bytes[i] & 0x0f;
            if (low > 9)
            {
                bad++;
                return "0";
            }
            digits[i] = (char)('0' + low);
        }
        var zone = bytes[^1] >> 4;
        return Number(digits, bytes.Length, zone is 0x0d or 0x0b, Math.Min(decimals, bytes.Length));
    }

    /// <summary>The numbers in a date's or time's text, in order.</summary>
    private static List<int> Parts(string text)
    {
        var parts = new List<int>();
        var i = 0;
        while (i < text.Length)
        {
            if (!char.IsAsciiDigit(text[i]))
            {
                i++;
                continue;
            }
            var start = i;
            while (i < text.Length && char.IsAsciiDigit(text[i])) i++;
            parts.Add(int.Parse(text.AsSpan(start, Math.Min(i - start, 9)), CultureInfo.InvariantCulture));
        }
        return parts;
    }

    /// <summary>A two-digit year's century, as the system windows it: 40 to 99 are 19xx.</summary>
    private static int Year(int year, bool twoDigits) => !twoDigits ? year : year >= 40 ? 1900 + year : 2000 + year;

    public static string Date(string text, string format, ref long bad)
    {
        var p = Parts(text);
        (int Y, int M, int D)? date = format.Trim().TrimStart('*').ToUpperInvariant() switch
        {
            "USA" when p.Count == 3 => (p[2], p[0], p[1]),
            "EUR" when p.Count == 3 => (p[2], p[1], p[0]),
            "MDY" when p.Count == 3 => (Year(p[2], true), p[0], p[1]),
            "DMY" when p.Count == 3 => (Year(p[2], true), p[1], p[0]),
            "YMD" when p.Count == 3 => (Year(p[0], true), p[1], p[2]),
            "JUL" when p.Count == 2 => Julian(Year(p[0], true), p[1]),
            _ when p.Count == 3 => (p[0], p[1], p[2]),
            _ => null,
        };
        if (date is var (y, m, d) && y is >= 1 and <= 9999 && m is >= 1 and <= 12 && d >= 1 && d <= DateTime.DaysInMonth(y, m))
            return $"{y:0000}-{m:00}-{d:00}";
        bad++;
        return "0001-01-01";

        static (int, int, int)? Julian(int year, int day)
        {
            if (day < 1 || day > (DateTime.IsLeapYear(year) ? 366 : 365)) return null;
            var date = new DateTime(year, 1, 1).AddDays(day - 1);
            return (date.Year, date.Month, date.Day);
        }
    }

    public static string Time(string text, ref long bad)
    {
        var p = Parts(text);
        if (p.Count >= 2 && p[0] <= 24 && p[1] < 60 && (p.Count < 3 || p[2] < 60))
        {
            var hour = p[0];
            // *USA: 12 hours, with AM or PM.
            if (text.Contains("PM", StringComparison.OrdinalIgnoreCase) && hour < 12) hour += 12;
            else if (text.Contains("AM", StringComparison.OrdinalIgnoreCase) && hour == 12) hour = 0;
            if (hour == 24) hour = 0;
            return $"{hour:00}:{p[1]:00}:{(p.Count > 2 ? p[2] : 0):00}";
        }
        bad++;
        return "00:00:00";
    }

    public static string Timestamp(string text, ref long bad)
    {
        // yyyy-mm-dd-hh.mm.ss.ffffff
        text = text.Trim();
        if (text.Length >= 19 && Parts(text[..19]) is [var y, var m, var d, var h, var min, var s]
            && y is >= 1 and <= 9999 && m is >= 1 and <= 12 && d >= 1 && d <= DateTime.DaysInMonth(y, m) && h <= 24 && min < 60 && s < 60)
        {
            var fraction = text.Length > 20 ? new string([.. text[20..].Where(char.IsAsciiDigit).Take(6)]) : "";
            // 24:00:00 is the end of the day, which PostgreSQL keeps as the next day's start.
            return $"{y:0000}-{m:00}-{d:00} {h:00}:{min:00}:{s:00}{(fraction.Length > 0 ? "." + fraction : "")}";
        }
        bad++;
        return "0001-01-01 00:00:00";
    }

    /// <summary>A value in a line of COPY's text format.</summary>
    public static void AppendCopy(StringBuilder line, string value)
    {
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': line.Append(@"\\"); break;
                case '\t': line.Append(@"\t"); break;
                case '\n': line.Append(@"\n"); break;
                case '\r': line.Append(@"\r"); break;
                case '\0': line.Append(' '); break;
                default: line.Append(c); break;
            }
        }
    }
}
