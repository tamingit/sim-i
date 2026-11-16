// The RPG cycle, and what fixed-form RPG has that free form doesn't. A program with a mainline runs
// it as RPG does: heading and detail output, then (with a primary file) its next record read, with
// control breaks (L1 to L9) and total-time calculations and output before the record's fields come
// in, then the detail calculations; again and again until LR is on. Without a primary file, the
// detail calculations run until LR is on or a RETURN. O specs print to program-described printer
// files (heading, detail, total and exception lines, spaced, skipped and edited), or write a
// record format; MOVE, CALL with PARM, TIME and the bit operations are here too.

using System.Globalization;

namespace SimI.Rpg;

internal sealed partial class Interpreter
{
    // ---- named indicators: L0 to L9, 1P, OF and OA to OG, OV, MR, RT, H1 to H9, U1 to U8, KA to KY ----

    private readonly Dictionary<string, Cell> _named = [];

    internal static bool IsNamedIndicator(string name) =>
        name.Length == 5 && name.StartsWith("*IN") && name != "*INLR" && name[3..] is var n
        && (n[0] is 'L' && char.IsDigit(n[1]) || n is "1P" or "OF" or "OV" or "MR" or "RT"
            || n[0] == 'O' && n[1] is >= 'A' and <= 'G' || n[0] == 'H' && char.IsDigit(n[1]) || n[0] == 'U' && n[1] is >= '1' and <= '8'
            || n[0] == 'K' && char.IsLetter(n[1]));

    private Cell NamedIndicator(string name) =>
        _named.TryGetValue(name, out var cell) ? cell : _named[name] = new Cell(RpgType.Indicator);

    private bool On(string name) => name == "LR" ? Values.ToBool(_lastRecord.Get()) : _named.TryGetValue(name, out var cell) && Values.ToBool(cell.Get());

    // ---- the cycle ----

    /// <summary>Whether the program runs the RPG cycle: it has a mainline (not MAIN or NOMAIN).</summary>
    private bool Cycles => _program.MainProcedure is null && !_program.NoMain;

    /// <summary>
    /// Runs the mainline in the RPG cycle until LR is on (or a RETURN): with a primary file, once for each
    /// of its records (then its secondary files'), with control breaks and total time between them.
    /// </summary>
    private void Cycle(Frame entry)
    {
        var inputs = _program.Files.Where(f => f.Disk && f.Primary is 'P' or 'S').OrderBy(f => f.Primary == 'P' ? 0 : 1).ToList();
        var totals = entry.Procedure.Body.OfType<TotalStmt>().ToList();
        object[]? controls = null;
        var first = true;
        NamedIndicator("1P").Set(true);
        while (true)
        {
            // Heading and detail output; then the first-page indicator and record indicators go off.
            Output('H', null, entry);
            Output('D', null, entry);
            if (_named.TryGetValue("OF", out var overflow)) overflow.Set(false);
            foreach (var flag in _overflowFlags.Values) flag.Set(false);
            NamedIndicator("1P").Set(false);
            foreach (var input in _program.InputRecords.Where(r => r.Indicator is not null)) Indicator(input.Indicator!).Set(false);
            for (var level = 1; level <= 9; level++) NamedIndicator($"L{level}").Set(false);

            (DiskState Disk, object[] Row)? next = null;
            if (inputs.Count > 0)
            {
                foreach (var file in inputs)
                {
                    var disk = _disks[file.Name];
                    if (!disk.Open) continue;
                    var row = Fetch(disk, null, [], forward: true, fromPosition: true);
                    if (row is not null)
                    {
                        next = (disk, row);
                        break;
                    }
                    disk.Eof = true;
                    disk.AtEnd = true;
                }
                if (next is null)
                {
                    // The last record has been read: LR, and every control level.
                    _lastRecord.Set(true);
                    for (var level = 1; level <= 9; level++) NamedIndicator($"L{level}").Set(true);
                }
                else
                {
                    // A control break: L1 to Ln when a control field of level n changed (not on the first record).
                    var values = ControlValues(next.Value.Disk, next.Value.Row);
                    if (controls is not null && !first)
                    {
                        var broken = 0;
                        for (var level = 9; level >= 1; level--)
                        {
                            if (Values.Compare(controls[level] ?? "", values[level] ?? "") != 0)
                            {
                                broken = level;
                                break;
                            }
                        }
                        for (var level = 1; level <= broken; level++) NamedIndicator($"L{level}").Set(true);
                    }
                    controls = values;
                }
            }
            // Total time: L0 calculations always, the others when their level is on; then total output.
            if (!first || On("LR"))
            {
                foreach (var total in totals) Execute(total.Body, entry);
                Output('T', null, entry);
            }
            if (On("LR")) return;
            if (next is { } record)
            {
                Take(record.Disk, record.Row, new DisplayStmt(0, "READ", record.Disk.Declaration.Name, null, false), entry);
                RecordIndicators(record.Disk);
            }
            first = false;
            // Detail time.
            var signal = Execute(entry.Procedure.Body, entry);
            if (signal == Signal.Goto) throw new RpgError(100, $"there's no TAG {_gotoLabel}");
            if (signal == Signal.Return) return;
            Tick();
        }
    }

    /// <summary>The control fields' values (L1 to L9) of a record, by level.</summary>
    private object[] ControlValues(DiskState disk, object[] row)
    {
        var values = new object[10];
        var inputs = _program.InputRecords.Where(r => r.Format == disk.Format || r.Format == disk.File.Format);
        foreach (var field in inputs.SelectMany(r => r.Fields).Where(f => f.ControlLevel is ['L', >= '1' and <= '9']))
        {
            var at = disk.File.Fields.ToList().FindIndex(f => f.Name == field.External);
            if (at < 0) continue;
            var level = field.ControlLevel![1] - '0';
            values[level] = (values[level] is null ? "" : Values.ToText(values[level])) + Values.ToText(row[at + 1] is DBNull ? "" : row[at + 1]);
        }
        return values;
    }

    /// <summary>The record identifying indicator of the record read, and its fields' indicators (plus, minus, zero or blank).</summary>
    private void RecordIndicators(DiskState disk)
    {
        foreach (var record in _program.InputRecords.Where(r => r.Format == disk.Format || r.Format == disk.File.Format))
        {
            if (record.Indicator is { } indicator) Indicator(indicator).Set(true);
            foreach (var field in record.Fields)
            {
                if (_globals.GetValueOrDefault(field.Name) is not { } cell) continue;
                var value = cell.Get();
                var sign = Values.IsNumber(value) ? Math.Sign(Values.ToDecimal(value)) : Values.ToText(value).Trim().Length == 0 ? 0 : 1;
                if (field.Plus is { } plus) Indicator(plus).Set(sign > 0);
                if (field.Minus is { } minus) Indicator(minus).Set(sign < 0);
                if (field.Zero is { } zero) Indicator(zero).Set(sign == 0);
            }
        }
    }

    /// <summary>An indicator by its fixed-form name: 01 to 99, LR, L1, OF and the like.</summary>
    private Cell Indicator(string name) =>
        int.TryParse(name, out var n) && n is >= 1 and <= 99 ? _indicators[n] : name == "LR" ? _lastRecord : NamedIndicator(name);

    // ---- O specs ----

    /// <summary>Overflow indicators of program-described printer files, by file.</summary>
    private readonly Dictionary<string, Cell> _overflowFlags = [];

    private sealed class Page
    {
        public List<string> Lines { get; } = [];
        public int Line { get; set; }
    }

    private readonly Dictionary<string, Page> _pages = [];

    /// <summary>Writes the O spec records of a type (H, D, T, or E with the EXCEPT name) whose conditions hold.</summary>
    private void Output(char type, string? exceptName, Frame frame)
    {
        foreach (var record in _program.OutputRecords)
        {
            if (record.Type != type) continue;
            if (type == 'E' && !string.Equals(record.ExceptName, exceptName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!Holds(record.Condition, frame)) continue;
            var file = _program.Files.FirstOrDefault(f => f.Name == record.File);
            // A record format of a database or externally described printer file.
            if (file is null || file.Disk || file.External)
            {
                WriteFormat(record, frame);
                continue;
            }
            if (file.Printer == 0) throw new RpgError(1299, $"O specs for {record.File}: only printer files and record formats are supported");
            PrintLine(file, record, frame);
        }
    }

    private bool Holds(string? condition, Frame frame) =>
        condition is null || _program.OutputConditions.TryGetValue(condition, out var expr) && Values.ToBool(Evaluate(expr, frame));

    private void PrintLine(FileDecl file, OutputRecord record, Frame frame)
    {
        // A record that starts a page (skips before) counts it in PAGE (PAGE1 to PAGE7) when the program has it as a field.
        if (record.SkipBefore > 0)
        {
            foreach (var name in record.Fields.Select(f => f.Name).Where(n => n is "PAGE" or "PAGE1" or "PAGE2" or "PAGE3" or "PAGE4" or "PAGE5" or "PAGE6" or "PAGE7").Distinct())
            {
                if (Find(name!, frame) is { } counter) counter.Set(Values.ToDecimal(counter.Get()) + 1);
            }
        }
        var text = new char[file.Printer];
        Array.Fill(text, ' ');
        var position = 0;
        foreach (var field in record.Fields)
        {
            if (!Holds(field.Condition, frame)) continue;
            string piece;
            if (field.Name is "PAGE" or "PAGE1" or "PAGE2" or "PAGE3" or "PAGE4" or "PAGE5" or "PAGE6" or "PAGE7" && Find(field.Name, frame) is null)
                piece = Values.Edit(_pages.TryGetValue(file.Name, out var counting) ? counting.Lines.Count(l => l == "\f") + 1 : 1, 4, 0, field.EditCode ?? 'Z');
            else if (field.Name is "UDATE" or "*DATE")
                piece = field.EditCode is { } c ? Values.Edit(decimal.Parse(DateTime.Now.ToString(field.Name == "UDATE" ? "MMddyy" : "MMddyyyy", CultureInfo.InvariantCulture)), field.Name == "UDATE" ? 6 : 8, 0, c) : DateTime.Now.ToString("MMddyy", CultureInfo.InvariantCulture);
            else if (field.Name is { } name)
            {
                var cell = Locate(new NameExpr(name), frame);
                var value = cell.Get();
                piece = field.EditWord is { } word ? EditWord(Values.ToDecimal(value), cell.Type.Decimals, word)
                    : field.EditCode is { } code && Values.IsNumber(value) ? Values.Edit(Values.ToDecimal(value), cell.Type.Length, cell.Type.Decimals, code)
                    : cell.Type.IsNumeric ? Zoned(value, cell.Type)
                    : Values.ToText(value);
                if (field.BlankAfter) cell.Clear();
            }
            else piece = field.Constant ?? "";
            var end = field.Relative ? position + field.End : field.End > 0 ? field.End : position + piece.Length;
            var start = end - piece.Length;
            for (var k = 0; k < piece.Length; k++)
            {
                if (start + k >= 0 && start + k < text.Length) text[start + k] = piece[k];
            }
            position = end;
        }
        Advance(file, record.SkipBefore, record.SpaceBefore, before: true);
        var page = PageOf(file);
        page.Lines.Add(new string(text).TrimEnd());
        page.Line++;
        Advance(file, record.SkipAfter, record.SpaceAfter == 0 && record.SkipAfter == 0 && record.SpaceBefore == 0 && record.SkipBefore == 0 ? 1 : record.SpaceAfter, before: false);
    }

    private Page PageOf(FileDecl file)
    {
        if (!_pages.TryGetValue(file.Name, out var page)) _pages[file.Name] = page = new Page();
        return page;
    }

    /// <summary>Spaces lines or skips to one (a new page when it's above where the page is), setting the overflow indicator.</summary>
    private void Advance(FileDecl file, int skip, int space, bool before)
    {
        var page = PageOf(file);
        if (skip > 0)
        {
            if (skip <= page.Line)
            {
                page.Lines.Add("\f");
                page.Line = 0;
            }
            while (page.Line < skip - 1)
            {
                page.Lines.Add("");
                page.Line++;
            }
        }
        // A line printed moves down one; spacing after more than one leaves blank lines.
        for (var k = before ? 0 : 1; k < space; k++)
        {
            page.Lines.Add("");
            page.Line++;
        }
        if (page.Line >= 60)
        {
            var flag = file.Overflow is { } name ? Indicator(name.StartsWith("*IN") ? name[3..] : name) : NamedIndicator("OF");
            flag.Set(true);
            _overflowFlags[file.Name] = flag;
        }
        if (page.Line >= 66)
        {
            page.Lines.Add("\f");
            page.Line = 0;
        }
    }

    /// <summary>An O spec record that's a record format: written (ADD), deleted (DEL), updated, or a printer file's format printed.</summary>
    private void WriteFormat(OutputRecord record, Frame frame)
    {
        if (DiskFor(record.File) is { } disk)
        {
            var op = record.Operation switch { "ADD" => "WRITE", "DEL" => "DELETE", _ => "UPDATE" };
            DiskOperation(new DisplayStmt(record.Line, op, record.File, null, false), disk, frame);
            return;
        }
        if (_printers.Values.FirstOrDefault(p => p.File.Records.ContainsKey(record.File)) is { } printer)
        {
            printer.Write(record.File);
            return;
        }
        throw new RpgError(1299, $"O specs: {record.File} isn't a printer file or record format of the program");
    }

    /// <summary>A number's digits as a zoned field shows them: the sign in the last digit's zone.</summary>
    private static string Zoned(object value, RpgType type)
    {
        var d = Values.ToDecimal(value);
        var digits = decimal.Truncate(Math.Abs(d) * Values.Pow10(type.Decimals)).ToString(CultureInfo.InvariantCulture);
        var length = Math.Max(type.Length, 1);
        digits = digits.Length > length ? digits[^length..] : digits.PadLeft(length, '0');
        if (d < 0) digits = digits[..^1] + Ebcdic.ToChar((byte)(0xd0 | (digits[^1] - '0')));
        return digits;
    }

    /// <summary>A zoned image's number: each character's digit, the sign from the last one's zone.</summary>
    private static decimal FromZoned(string image, int decimals)
    {
        decimal value = 0;
        foreach (var c in image)
        {
            var digit = Ebcdic.ToByte(c) & 0x0f;
            value = value * 10 + Math.Min(digit, 9);
        }
        if (image.Length > 0 && Ebcdic.ToByte(image[^1]) >> 4 is 0xd or 0xb) value = -value;
        return value / Values.Pow10(decimals);
    }

    // ---- MOVE, MOVEL and MOVEA ----

    /// <summary>A value as MOVE sees it: characters, a number's zoned digits, or a date in a format.</summary>
    private static string Image(object value, RpgType? type, string? format) => value switch
    {
        DateOnly d => Values.FormatDate(d, format ?? "*ISO"),
        TimeOnly t => Values.FormatTime(t, format ?? "*ISO"),
        DateTime t => Values.ToText(t),
        decimal or double when type is { IsNumeric: true } => Zoned(value, type),
        decimal d => Zoned(d, RpgType.Number(Math.Max(1, decimal.Truncate(Math.Abs(d)).ToString(CultureInfo.InvariantCulture).Length + d.Scale), d.Scale)),
        _ => Values.ToText(value),
    };

    private void Move(MoveStmt move, Frame frame)
    {
        var target = Locate(move.Target, frame);
        var sourceValue = Evaluate(move.Source, frame);
        if (move.Kind == "MOVEA")
        {
            MoveArray(target, sourceValue, move.Pad);
            return;
        }
        var sourceType = IsVariable(move.Source) && Find(Root(move.Source), frame) is not null ? Locate(move.Source, frame).Type : null;
        if (target.Type.Kind is RpgKind.Date or RpgKind.Time or RpgKind.Timestamp)
        {
            var text = Image(sourceValue, sourceType, move.Format);
            target.Set(target.Type.Kind switch
            {
                RpgKind.Date => Values.ParseDate(text, move.Format ?? "*ISO"),
                RpgKind.Time => Values.ParseTime(text),
                _ => (object)Values.ParseTimestamp(text),
            });
            return;
        }
        var source = Image(sourceValue, sourceType, move.Format);
        var length = target.Type.IsNumeric ? target.Type.Length : target.Type.Kind == RpgKind.Varchar ? target.Type.Length : Values.ByteLength(target.Type);
        var current = target.Type.IsNumeric ? Zoned(target.Get(), target.Type) : Values.ToText(target.Get()).PadRight(length);
        if (move.Pad) current = new string(target.Type.IsNumeric ? '0' : ' ', length);
        string result;
        if (move.Kind == "MOVE")
        {
            // Right-justified: the source's rightmost characters over the target's rightmost.
            result = source.Length >= length ? source[^length..] : current[..(length - source.Length)] + source;
        }
        else
        {
            result = source.Length >= length ? source[..length] : source + current[source.Length..];
            // MOVEL of a shorter number keeps the target's sign.
            if (target.Type.IsNumeric && source.Length < length && Ebcdic.ToByte(current[^1]) >> 4 is 0xd)
                result = result[..^1] + Ebcdic.ToChar((byte)(0xd0 | (Ebcdic.ToByte(result[^1]) & 0x0f)));
        }
        if (target.Type.IsNumeric) target.Set(FromZoned(result, target.Type.Decimals));
        else target.Set(target.Type.Kind == RpgKind.Varchar ? result.TrimEnd() : result);
    }

    /// <summary>MOVEA: arrays (or fields) as the characters they make, moved left-justified, element by element.</summary>
    private static void MoveArray(Cell target, object source, bool pad)
    {
        var text = source is Cell { Elements: { } elements } ? string.Concat(elements.Select(e => Image(e.Get(), e.Type, null))) : Image(source, null, null);
        if (target.Elements is { } into)
        {
            var size = into.Length == 0 ? 0 : into[0].Type.IsNumeric ? into[0].Type.Length : Values.ByteLength(into[0].Type);
            for (var i = 0; i < into.Length; i++)
            {
                var at = i * size;
                if (at >= text.Length && !pad) break;
                var piece = at < text.Length ? text.Substring(at, Math.Min(size, text.Length - at)) : "";
                var element = into[i];
                if (element.Type.IsNumeric)
                {
                    var current = Zoned(element.Get(), element.Type);
                    element.Set(FromZoned(piece.Length >= size || pad ? piece.PadRight(size, '0') : piece + current[piece.Length..], element.Type.Decimals));
                }
                else
                {
                    var current = Values.ToText(element.Get()).PadRight(size);
                    element.Set(piece.Length >= size || pad ? piece : piece + current[piece.Length..]);
                }
            }
            return;
        }
        var length = Values.ByteLength(target.Type);
        var existing = Values.ToText(target.Get()).PadRight(length);
        target.Set(text.Length >= length ? text[..length] : text + (pad ? new string(' ', length - text.Length) : existing[text.Length..]));
    }

    // ---- CALL with PARM, TIME and the bit operations ----

    private void CallProgram(CallProgramStmt call, Frame frame)
    {
        _error = false;
        var program = Values.ToText(Evaluate(call.Program, frame)).Trim();
        var cells = new List<Cell>();
        foreach (var (target, before, _) in call.Parameters)
        {
            var cell = Locate(target, frame);
            if (before is not null) cell.Set(Evaluate(before, frame));
            cells.Add(cell);
        }
        try
        {
            _machine.Call(program, cells, _host, _depth + 1);
        }
        catch (RpgHalt e)
        {
            if (!call.ErrorExtender) throw new RpgError(202, $"The call to {program} ended in error: {e.Message}", e.MessageId ?? "CPF9999");
            _error = true;
            _status = 202;
        }
        foreach (var (target, _, after) in call.Parameters)
        {
            if (after is not null) Locate(after, frame).Set(Locate(target, frame).Get());
        }
    }

    private static void SetTime(Cell target)
    {
        var now = DateTime.Now;
        switch (target.Type.Kind)
        {
            case RpgKind.Time:
                target.Set(TimeOnly.FromDateTime(now));
                return;
            case RpgKind.Date:
                target.Set(DateOnly.FromDateTime(now));
                return;
            case RpgKind.Timestamp:
                target.Set(now);
                return;
        }
        // 6 digits: HHMMSS; 12: and MMDDYY; 14: and MMDDYYYY.
        var text = now.ToString(target.Type.Length switch { 12 => "HHmmssMMddyy", 14 => "HHmmssMMddyyyy", _ => "HHmmss" }, CultureInfo.InvariantCulture);
        target.Set(decimal.Parse(text, CultureInfo.InvariantCulture));
    }

    private void Bits(BitStmt bits, Frame frame)
    {
        var target = Locate(bits.Target, frame);
        void Set(int i, bool value)
        {
            if (i < bits.Indicators.Count && bits.Indicators[i] is { } indicator) Locate(indicator, frame).Set(value);
        }
        if (bits.Op == "TESTN")
        {
            var text = Values.ToText(target.Get());
            var digits = text.All(char.IsAsciiDigit) && text.Length > 0;
            var leading = !digits && text.TrimStart().Length > 0 && text.TrimStart().All(char.IsAsciiDigit) && text.StartsWith(' ');
            Set(0, digits);
            Set(1, leading);
            Set(2, text.Trim().Length == 0);
            return;
        }
        // The bits: '0' to '7' each a bit (0 the leftmost), or a one-character field whose bits are on.
        var mask = 0;
        var given = Values.ToText(Evaluate(bits.Bits!, frame));
        if (bits.Bits is Literal && given.All(c => c is >= '0' and <= '7')) foreach (var c in given) mask |= 0x80 >> (c - '0');
        else if (given.Length > 0) mask = Ebcdic.ToByte(given[0]);
        var current = Values.ToText(target.Get());
        var b = current.Length > 0 ? Ebcdic.ToByte(current[0]) : (byte)0x40;
        switch (bits.Op)
        {
            case "BITON":
                b = (byte)(b | mask);
                break;
            case "BITOFF":
                b = (byte)(b & ~mask);
                break;
            default:
                Set(0, (b & mask) == 0);
                Set(1, (b & mask) != 0 && (b & mask) != mask);
                Set(2, (b & mask) == mask);
                return;
        }
        target.Set(Ebcdic.ToChar(b) + (current.Length > 1 ? current[1..] : ""));
    }
}
