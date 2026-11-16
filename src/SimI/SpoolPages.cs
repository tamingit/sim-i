// What ACS's printer output viewer asks for when a spooled file is opened: QGY/QGYRSPL, format
// GUIP0100, an API IBM doesn't document. Its layout here is as a real IBM i (V7R5) answers it: a
// 44-byte header, then for each page asked for a block of its printed lines, as text without SCS.
//
//   header   0 bytes returned, 4 bytes available, 8 pages returned, 12 and 16 the length of the first
//            page that didn't fit (0 when all fit), 20 pages in the file, 24 width, 28 48, 32 0,
//            36 00 00 F0 F0 F0 00 00 00
//   page     0 length (with these 20 bytes), 4 page number, 8 lines in it, 12 '100000', 2 bytes
//   line     0 length (a multiple of 4), 4 page number, 8 line number, 12 text length, 16 '000000',
//            22 the text (EBCDIC, trailing blanks cut), then filler to the length. Blank lines are left out.

using System.Buffers.Binary;

namespace SimI;

internal static class SpoolPages
{
    private const int HeaderLength = 44;

    /// <summary>A printed line: its number on the page (1 first) and its EBCDIC text, trailing blanks cut.</summary>
    public readonly record struct Line(int Number, byte[] Text);

    /// <summary>
    /// The receiver for pages <paramref name="first"/> on (<paramref name="count"/> of them, or the
    /// rest when 0 or less), as much as <paramref name="length"/> holds: whole pages only, and
    /// always the header (so the caller learns how much it needs). Null when the file isn't SCS or
    /// <paramref name="first"/> is past its last page (CPF34C8 on a real system).
    /// </summary>
    public static byte[]? Retrieve(SpooledFile file, int first, int count, int length)
    {
        if (file.DeviceType != "*SCS") return null;
        var pages = Pages(file.Data);
        if (first < 1 || first > pages.Count) return null;
        var last = count <= 0 ? pages.Count : Math.Min(pages.Count, first + count - 1);
        var blocks = Enumerable.Range(first, last - first + 1).Select(p => Block(p, pages[p - 1])).ToList();

        var available = HeaderLength + blocks.Sum(b => b.Length);
        var fitting = 0;
        for (var used = HeaderLength; fitting < blocks.Count && used + blocks[fitting].Length <= length; fitting++) used += blocks[fitting].Length;
        var body = blocks.Take(fitting).SelectMany(b => b).ToArray();
        var header = new byte[HeaderLength];
        BinaryPrimitives.WriteInt32BigEndian(header, HeaderLength + body.Length);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), available);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8), fitting);
        var next = fitting < blocks.Count ? blocks[fitting].Length : 0;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(12), next);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(16), next);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(20), pages.Count);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(24), Width(file.Data));
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(28), 48);
        header[38] = header[39] = header[40] = 0xF0;
        byte[] all = [.. header, .. body];
        return all.Length <= length ? all : all[..Math.Max(length, 0)];
    }

    private static byte[] Block(int page, List<Line> lines)
    {
        var block = new List<byte>(new byte[20]);
        BinaryPrimitives.WriteInt32BigEndian(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(block)[4..], page);
        BinaryPrimitives.WriteInt32BigEndian(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(block)[8..], lines.Count);
        for (var i = 0; i < 6; i++) block[12 + i] = i == 0 ? (byte)0xF1 : (byte)0xF0;
        block[18] = block[19] = 0x40;
        foreach (var line in lines)
        {
            var length = (22 + line.Text.Length + 3) / 4 * 4;
            var record = new byte[length];
            record.AsSpan().Fill(0x40);
            BinaryPrimitives.WriteInt32BigEndian(record, length);
            BinaryPrimitives.WriteInt32BigEndian(record.AsSpan(4), page);
            BinaryPrimitives.WriteInt32BigEndian(record.AsSpan(8), line.Number);
            BinaryPrimitives.WriteInt32BigEndian(record.AsSpan(12), line.Text.Length);
            record.AsSpan(16, 6).Fill(0xF0);
            line.Text.CopyTo(record, 22);
            block.AddRange(record);
        }
        var bytes = block.ToArray();
        BinaryPrimitives.WriteInt32BigEndian(bytes, bytes.Length);
        return bytes;
    }

    /// <summary>The width SCS's Set Horizontal Format (2B C1) gives, or 132.</summary>
    private static int Width(byte[] scs) =>
        scs.Length >= 4 && scs[0] == 0x2B && scs[1] == 0xC1 && scs[2] >= 2 ? scs[3] : 132;

    /// <summary>
    /// The pages of SCS print data and their non-blank lines. New line (15) and line feed (25)
    /// move down a line, carriage return (0D) back to the start of it, form feed (0C) ends the
    /// page; the 34 commands move to a column or line; other 2B commands and controls are skipped.
    /// A form feed at the very end doesn't start another page.
    /// </summary>
    public static List<List<Line>> Pages(byte[] scs)
    {
        var pages = new List<List<Line>>();
        var lines = new SortedDictionary<int, byte[]>();
        int line = 1, column = 0;
        var ended = false;

        void Put(byte b)
        {
            var text = lines.GetValueOrDefault(line) ?? [];
            if (text.Length <= column) Array.Resize(ref text, column + 1);
            for (var i = 0; i < text.Length; i++) if (text[i] == 0) text[i] = 0x40;
            text[column++] = b;
            lines[line] = text;
            ended = false;
        }

        void EndPage()
        {
            pages.Add([.. lines.Select(l => new Line(l.Key, l.Value.AsSpan().TrimEnd((byte)0x40).ToArray())).Where(l => l.Text.Length > 0)]);
            lines.Clear();
            line = 1;
            column = 0;
            ended = true;
        }

        for (var i = 0; i < scs.Length; i++)
        {
            var b = scs[i];
            switch (b)
            {
                case 0x15: line++; column = 0; ended = false; break;
                case 0x25: line++; ended = false; break;
                case 0x0D: column = 0; break;
                case 0x0C: EndPage(); break;
                case 0x2B when i + 2 < scs.Length: i += 1 + scs[i + 2]; break;
                case 0x34 when i + 2 < scs.Length:
                    var n = scs[i + 2];
                    switch (scs[i + 1])
                    {
                        case 0xC0: column = Math.Max(n - 1, 0); break;
                        case 0xC8: column += n; break;
                        case 0xC4: line = Math.Max((int)n, 1); break;
                        case 0x4C: line += n; break;
                    }
                    i += 2;
                    break;
                case < 0x40: break;
                default: Put(b); break;
            }
        }
        if (!ended || pages.Count == 0) EndPage();
        return pages;
    }
}
