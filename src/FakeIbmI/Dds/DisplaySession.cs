// A display file open in a running program: writes its record formats as 5250 screens, reads
// what the user does back into the program's fields and indicators, and keeps its subfiles.
// Screens are drawn into a ScreenImage first, so a window can go over what's under it: the
// file's own records, or (an ASSUME file's window) what the job last showed.

using FakeIbmI.Rpg;
using static FakeIbmI.Stream5250;

namespace FakeIbmI.Dds;

/// <summary>
/// What a screen shows, position by position: each byte (EBCDIC, or a display attribute
/// 0x20 to 0x3F) and whether anything was put there. Input fields are in it as their text.
/// </summary>
internal sealed class ScreenImage(int rows, int columns)
{
    private readonly byte[] _cells = new byte[rows * columns];
    private readonly bool[] _written = new bool[rows * columns];

    public int Rows { get; } = rows;
    public int Columns { get; } = columns;
    public bool Wide => Columns > 80;

    public ScreenImage Copy()
    {
        var copy = new ScreenImage(Rows, Columns);
        _cells.CopyTo(copy._cells, 0);
        _written.CopyTo(copy._written, 0);
        return copy;
    }

    private bool On(int row, int column) => row >= 1 && row <= Rows && column >= 1 && column <= Columns;

    public bool IsWritten(int row, int column) => On(row, column) && _written[(row - 1) * Columns + column - 1];

    public void Put(int row, int column, byte value)
    {
        if (!On(row, column)) return;
        _cells[(row - 1) * Columns + column - 1] = value;
        _written[(row - 1) * Columns + column - 1] = true;
    }

    /// <summary>Text from (row, column), up to <paramref name="last"/>. Bytes below 0x20 would be 5250 orders: they show as blanks.</summary>
    public void Put(int row, int column, byte[] text, int last)
    {
        for (var i = 0; i < text.Length && column + i <= Math.Min(last, Columns); i++) Put(row, column + i, text[i] < 0x20 ? (byte)0x40 : text[i]);
    }

    /// <summary>The image as 5250 orders: an SBA and the bytes for each run of written positions.</summary>
    public byte[] Orders()
    {
        var orders = new List<byte>();
        for (var row = 1; row <= Rows; row++)
        {
            var column = 1;
            while (column <= Columns)
            {
                if (!IsWritten(row, column))
                {
                    column++;
                    continue;
                }
                orders.AddRange(Sba(row, column));
                while (column <= Columns && IsWritten(row, column)) orders.Add(_cells[(row - 1) * Columns + column++ - 1]);
            }
        }
        return [.. orders];
    }
}

internal sealed class DisplaySession(
    DisplayFile file, FileDecl declaration, Func<string, Cell?> variable, Func<FileDecl, int, Cell> indicator, string user, JobState job,
    Func<string, string?, string?>? messages = null)
{
    /// <summary>A message's text from its message file (or the default ones); its id when there's no such message.</summary>
    private string MessageText(string id, string? messageFile) => messages?.Invoke(id, messageFile) ?? id;
    private sealed class Row(Dictionary<string, object> values)
    {
        public Dictionary<string, object> Values { get; } = values;
        public bool Changed { get; set; }
    }

    private sealed class Subfile
    {
        public SortedDictionary<int, Row> Rows { get; } = [];

        /// <summary>The index, among the records in order, of the first one on the page shown.</summary>
        public int Top { get; set; }
    }

    /// <summary>An input field on the screen: where it is, its record, and the subfile record it belongs to, if any.</summary>
    private sealed record Target(int Row, int Column, DisplayField Field, string? Subfile, int Number, string Record);

    /// <summary>A named field on the screen, for RTNCSRLOC: where it is, and its record.</summary>
    private sealed record Placed(int Row, int Column, int Width, string Record, string Field);

    private readonly Dictionary<string, Subfile> _subfiles = [];
    private readonly List<string> _shown = [];

    /// <summary>Records whose input fields a later record's PROTECT protected.</summary>
    private readonly HashSet<string> _protected = [];

    /// <summary>Where windows the program or the cursor places are, as last written.</summary>
    private readonly Dictionary<string, Window> _windows = [];

    /// <summary>Subfiles shown one line a record (SFLDROP until the key is pressed), by their control record.</summary>
    private readonly HashSet<string> _truncated = [];

    /// <summary>The field in error after a check of what was typed, and its message.</summary>
    private (Target Target, string Message)? _invalid;
    private readonly List<Target> _targets = [];
    private readonly List<Placed> _placed = [];
    private byte[]? _invited;

    public DisplayFile File { get; } = file;

    /// <summary>The AID byte of the key the user last pressed, for the INFDS.</summary>
    public int LastAid { get; private set; } = DisplayFile.Enter;

    /// <summary>Where the cursor was when the user last pressed a key.</summary>
    public (int Row, int Column) LastCursor { get; private set; }

    private bool On(int number) => Values.ToBool(indicator(declaration, number).Get());

    private Subfile SubfileOf(string record) => _subfiles.TryGetValue(record, out var s) ? s : _subfiles[record] = new Subfile();

    private Cell NumberField(string record) =>
        declaration.Subfiles.TryGetValue(record, out var name) && variable(name) is { } cell
            ? cell
            : throw new RpgError(1221, $"subfile {record} needs SFILE({record}:field) on the file, and the field declared");

    private Cell Variable(DisplayField field) => variable(field.Name!) ?? throw new RpgError(100, $"{field.Name} isn't defined");

    // ---- operations ----

    /// <summary>WRITE, or EXFMT with <paramref name="read"/>: shows the record, or adds a record to a subfile.</summary>
    public void Write(string name, IWorkstation workstation, bool read = false)
    {
        var format = File.Records[name];
        if (format.Subfile)
        {
            if (read) throw new RpgError(1221, $"EXFMT is for the subfile's control record, not {name}");
            var number = (int)Values.ToDecimal(NumberField(name).Get());
            var subfile = SubfileOf(name);
            if (number < 1 || subfile.Rows.ContainsKey(number)) throw new RpgError(1021, $"subfile {name} can't take record number {number}: it's below 1 or already there");
            subfile.Rows[number] = Snapshot(format);
            subfile.Rows[number].Changed = format.NextChange?.Holds(On) == true;
            return;
        }
        if (format.Controls is { } controlled)
        {
            var subfile = SubfileOf(controlled);
            if (format.Clear?.Holds(On) == true) subfile.Rows.Clear();
            // SFLINZ: SFLSIZ records with every field blank or zero.
            if (format.Initialize?.Holds(On) == true && File.Records.TryGetValue(controlled, out var initialized))
            {
                subfile.Rows.Clear();
                for (var n = 1; n <= Math.Max(format.SubfileSize, format.PageSize); n++)
                    subfile.Rows[n] = new Row(initialized.Fields.Where(f => f.Name is not null).ToDictionary(f => f.Name!, f => f.Numeric ? 0m : (object)""));
            }
            if (format.Clear?.Holds(On) == true || format.Initialize?.Holds(On) == true)
            {
                if (format.FoldKey is { Folded: false }) _truncated.Add(name);
                else _truncated.Remove(name);
            }
            subfile.Top = 0;
            // SFLRCDNBR: the page with that record on it.
            if (format.Fields.FirstOrDefault(f => f.RecordNumber) is { Name: not null } positioner && format.PageSize > 0)
            {
                var wanted = (int)Values.ToDecimal(Variable(positioner).Get());
                var index = subfile.Rows.Keys.ToList().FindIndex(k => k >= wanted);
                if (wanted > 0 && index > 0) subfile.Top = index - index % format.PageSize;
            }
        }
        // Writing it again with PUTOVR on: only what changed goes, so the screen isn't cleared.
        var again = _shown.Count > 0 && _shown[^1] == name && format.PutOverride?.Holds(On) == true && job.Screen?.Columns == File.Columns;
        if (format.Window is not null)
        {
            // A window goes over the screen, in place of the file's other windows.
            _shown.RemoveAll(n => File.Records[n].Window is not null);
        }
        else
        {
            if (!format.Overlay) _shown.Clear();
            else _shown.RemoveAll(n => File.Records[n].Window is not null);
        }
        _shown.Remove(name);
        _protected.Remove(name);
        // PROTECT: what's on the screen under the record takes no more input.
        if (format.Protect?.Holds(On) == true) _protected.UnionWith(_shown.Where(n => n != name));
        _shown.Add(name);
        if (format.WindowPlace is { } place) _windows[name] = PlaceWindow(format.Window!, place);
        var alarm = format.Alarm?.Holds(On) == true;
        _invited = null;
        if (read)
        {
            Await(Paint(true, null, alarm, again), format, screen => workstation.Exchange(screen));
        }
        else if (format.Invite)
        {
            // Sent when READ waits for the reply.
            _invited = Paint(true, null, alarm, again);
        }
        else
        {
            workstation.Send(Paint(false, null, alarm, again));
        }
    }

    /// <summary>
    /// Where a window the program places goes: at the line and position its fields hold (the
    /// border's top left), or for *DFT, under the cursor if it fits, else over it.
    /// </summary>
    private Window PlaceWindow(Window window, (string? Row, string? Column) place)
    {
        int row, column;
        if (place.Row is { } rowField && place.Column is { } columnField)
        {
            row = (int)Values.ToDecimal(variable(rowField)?.Get() ?? 1m);
            column = (int)Values.ToDecimal(variable(columnField)?.Get() ?? 1m);
        }
        else
        {
            var (cursorRow, cursorColumn) = LastCursor.Row > 0 ? LastCursor : (1, 1);
            row = cursorRow + window.Lines + 2 <= File.Rows - 1 ? cursorRow + 1 : Math.Max(1, cursorRow - window.Lines - 2);
            column = cursorColumn;
        }
        row = Math.Clamp(row, 1, Math.Max(1, File.Rows - window.Lines - 2));
        column = Math.Clamp(column, 1, Math.Max(1, File.Columns - window.Positions - 3));
        return window with { Row = row, Column = column };
    }

    private Window WindowOf(RecordFormat format) => _windows.TryGetValue(format.Name, out var placed) ? placed : format.Window!;

    /// <summary>READ of a record: waits for the user on what WRITE put there.</summary>
    public void Read(string name, IWorkstation workstation)
    {
        var format = File.Records[name];
        if (format.Subfile) throw new RpgError(1221, $"READ is for the subfile's control record, not {name}: use READC");
        if (!_shown.Contains(name)) throw new RpgError(1221, $"{name} has to be written before it's read");
        ReadFile(workstation, format);
    }

    /// <summary>
    /// READ of the file: the reply to the record last written. With WAITRCD on the file, when nobody
    /// presses a key in that many seconds it ends with status 1331.
    /// </summary>
    public void ReadFile(IWorkstation workstation, RecordFormat? format = null)
    {
        if (_shown.Count == 0) throw new RpgError(1221, $"nothing of {File.Name} has been written to read");
        format ??= File.Records[_shown[^1]];
        var screen = _invited ?? HostRecord(0x03, Join(WriteToDisplay(true), ReadMdtFields));
        _invited = null;
        Func<byte[], ReceivedInput?> exchange = File.WaitRecord > 0
            ? s => workstation.Exchange(s, TimeSpan.FromSeconds(File.WaitRecord))
            : s => workstation.Exchange(s);
        Await(screen, format, exchange);
    }

    /// <summary>READC: the next subfile record the user changed, into the program's fields. False when there's none.</summary>
    public bool ReadChanged(string name)
    {
        foreach (var (number, row) in SubfileOf(name).Rows)
        {
            if (!row.Changed) continue;
            row.Changed = false;
            Load(name, number, row);
            return true;
        }
        return false;
    }

    /// <summary>CHAIN: the subfile record of that number, into the program's fields. False when there's none.</summary>
    public bool Chain(string name, int number)
    {
        if (!File.Records[name].Subfile) throw new RpgError(1221, $"CHAIN is supported on subfile records, and {name} isn't one");
        if (!SubfileOf(name).Rows.TryGetValue(number, out var row)) return false;
        Load(name, number, row);
        return true;
    }

    /// <summary>UPDATE: the program's fields, back into the subfile record last read.</summary>
    public void Update(string name)
    {
        var format = File.Records[name];
        if (!format.Subfile) throw new RpgError(1221, $"UPDATE is supported on subfile records, and {name} isn't one");
        var number = (int)Values.ToDecimal(NumberField(name).Get());
        var rows = SubfileOf(name).Rows;
        if (!rows.ContainsKey(number)) throw new RpgError(1221, $"subfile {name} has no record {number} to update");
        rows[number] = Snapshot(format);
        rows[number].Changed = format.NextChange?.Holds(On) == true;
    }

    /// <summary>CLEAR of a record format: clears its fields. False when the file has no such record.</summary>
    public bool ClearRecord(string name)
    {
        if (!File.Records.TryGetValue(name, out var format)) return false;
        foreach (var field in format.Fields.Where(f => f.Name is not null)) variable(field.Name!)?.Clear();
        return true;
    }

    private Row Snapshot(RecordFormat format) =>
        new(format.Fields.Where(f => f.Name is not null).ToDictionary(f => f.Name!, f => Variable(f).Get()));

    private void Load(string name, int number, Row row)
    {
        foreach (var (field, value) in row.Values) variable(field)?.Set(value);
        NumberField(name).Set((decimal)number);
    }

    // ---- reading ----

    private void Await(byte[] screen, RecordFormat format, Func<byte[], ReceivedInput?> exchange)
    {
        var keys = format.Keys.Concat(File.Keys).ToList();
        while (true)
        {
            var input = exchange(screen) ?? throw new RpgError(1331, $"Wait time for {File.Name} ran out (WAITRCD {File.WaitRecord} seconds).");
            var key = keys.FirstOrDefault(k => k.Aid == input.Aid && k.When.Holds(On));
            var roll = input.Aid is DisplayFile.RollUp or DisplayFile.RollDown;
            // SFLDROP or SFLFOLD's key, when the program hasn't the key for itself: the subfile shows one line a record, or all of them.
            if (key is null && _shown.FirstOrDefault(n => File.Records[n].FoldKey?.Aid == input.Aid) is { } folding)
            {
                Apply(input);
                if (!_truncated.Remove(folding)) _truncated.Add(folding);
                screen = Paint(true, null, false, false);
                continue;
            }
            if (input.Aid != DisplayFile.Enter && key is null && !roll)
            {
                screen = HostRecord(0x03, Join(WriteToDisplay(true, Message("Function key not allowed.", job.Screen?.Rows ?? File.Rows)), ReadMdtFields));
                continue;
            }
            if (key is not { Attention: true })
            {
                Apply(input);
                // What was typed is checked (VALUES, RANGE, COMP, CHECK) before the program has it.
                if (Check(input) is { } invalid)
                {
                    _invalid = invalid;
                    screen = Paint(true, invalid.Message, false, false);
                    _invalid = null;
                    continue;
                }
                Changes(input);
            }
            if (roll)
            {
                // The subfile pages by itself while it has records that way; past them, it's the program's key, if it has one.
                var control = format.Controls is not null ? format : _shown.Select(n => File.Records[n]).FirstOrDefault(r => r.Controls is not null);
                if (control is not null && Page(control, input.Aid == DisplayFile.RollUp))
                {
                    screen = Paint(true, null, false, false);
                    continue;
                }
                if (key is null)
                {
                    screen = Paint(true, "Roll up or down past the first or last record in file.", false, false);
                    continue;
                }
            }
            foreach (var k in keys.Where(k => k.Indicator > 0)) indicator(declaration, k.Indicator).Set(false);
            if (key is { Indicator: > 0 }) indicator(declaration, key.Indicator).Set(true);
            LastAid = input.Aid;
            LastCursor = (input.CursorRow, input.CursorColumn);
            ReturnCursor(format, input);
            return;
        }
    }

    /// <summary>The first field whose typing fails its checks, and the message for it; null when all pass.</summary>
    private (Target Target, string Message)? Check(ReceivedInput input)
    {
        foreach (var target in _targets)
        {
            var field = target.Field;
            var text = input.FieldAt(target.Row, target.Column);
            string? problem = null;
            if (text is null)
            {
                if (field.MandatoryEnter) problem = "Data must be entered in this field.";
            }
            else
            {
                var typed = field.LowerCase ? text : text.ToUpperInvariant();
                if (field.MandatoryEnter && typed.Trim().Length == 0) problem = "Data must be entered in this field.";
                else if (field.MandatoryFill && typed.Trim().Length > 0 && typed.TrimEnd().Length < field.Length) problem = "Field must be filled completely.";
                else if (field.ValidName && typed.Trim().Length > 0 && !Machine.IsName(typed.Trim())) problem = "Name not valid.";
                else if (!Allowed(field, typed)) problem = "Value entered for field is not valid.";
            }
            if (problem is not null) return (target, field.CheckMessage is { } own ? MessageText(own.Id, own.File) : problem);
        }
        return null;
    }

    /// <summary>Whether what's typed is one of VALUES, in RANGE, and meets COMP.</summary>
    private static bool Allowed(DisplayField field, string typed)
    {
        if (field.AllowedValues.Count == 0 && field.Range is null && field.Compare is null) return true;
        int Order(string value)
        {
            if (field.Numeric)
            {
                var number = decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0m;
                return DisplayFile.ParseInput(typed, field).CompareTo(number);
            }
            return string.CompareOrdinal(typed.TrimEnd(), value.TrimEnd());
        }
        if (field.AllowedValues.Count > 0 && !field.AllowedValues.Any(v => Order(v) == 0)) return false;
        if (field.Range is var (low, high) && (Order(low) < 0 || Order(high) > 0)) return false;
        if (field.Compare is var (op, value))
        {
            var order = Order(value);
            var holds = op switch { "EQ" => order == 0, "NE" => order != 0, "LT" => order < 0, "NL" or "GE" => order >= 0, "GT" => order > 0, _ => order <= 0 };
            if (!holds) return false;
        }
        return true;
    }

    /// <summary>CHANGE(nn): on when the user changed the field (or, on a record, any of its fields); off when not.</summary>
    private void Changes(ReceivedInput input)
    {
        var changed = _targets.Where(t => input.FieldAt(t.Row, t.Column) is not null).ToList();
        foreach (var name in _shown)
        {
            var format = File.Records[name];
            if (format.ChangeIndicator > 0) indicator(declaration, format.ChangeIndicator).Set(changed.Any(t => t.Record == name || t.Subfile is not null && format.Controls == t.Subfile));
        }
        foreach (var field in _targets.Select(t => t.Field).Where(f => f.ChangeIndicator > 0).Distinct())
            indicator(declaration, field.ChangeIndicator).Set(changed.Any(t => t.Field == field));
    }

    /// <summary>RTNCSRLOC: the record and field the cursor was on, into the record's two fields.</summary>
    private void ReturnCursor(RecordFormat format, ReceivedInput input)
    {
        if (format.ReturnCursor is not var (recordField, fieldField)) return;
        var at = _placed.LastOrDefault(p => p.Row == input.CursorRow && input.CursorColumn >= p.Column && input.CursorColumn < p.Column + Math.Max(p.Width, 1));
        variable(recordField)?.Set(at?.Record ?? "");
        variable(fieldField)?.Set(at?.Field ?? "");
    }

    private bool Page(RecordFormat control, bool down)
    {
        var subfile = SubfileOf(control.Controls!);
        var size = Math.Max(control.PageSize, 1);
        if (control.Display?.Holds(On) != true) return false;
        if (down)
        {
            if (subfile.Top + size >= subfile.Rows.Count) return false;
            subfile.Top += size;
            return true;
        }
        if (subfile.Top == 0) return false;
        subfile.Top = Math.Max(0, subfile.Top - size);
        return true;
    }

    /// <summary>Moves what the user typed into the program's fields, or into the subfile records, marking those changed.</summary>
    private void Apply(ReceivedInput input)
    {
        foreach (var target in _targets)
        {
            var text = input.FieldAt(target.Row, target.Column);
            var field = target.Field;
            if (text is null)
            {
                // An input-only field nobody typed in comes back empty.
                // ...or, with DFT, its default.
                if (field.Usage == 'I' && target.Subfile is null)
                {
                    if (field.Default is { } shown) Variable(field).Set(field.Numeric ? DisplayFile.ParseInput(shown, field) : shown);
                    else Variable(field).Clear();
                }
                continue;
            }
            object value = field.Numeric ? DisplayFile.ParseInput(text, field) : field.LowerCase ? text : text.ToUpperInvariant();
            // CHECK(RB) or CHECK(RZ): right-adjusted, filled with blanks or zeros.
            if (!field.Numeric && field.RightAdjust is { } fill) value = ((string)value).Trim().PadLeft(field.Length, fill);
            if (target.Subfile is null)
            {
                Variable(field).Set(value);
            }
            else if (SubfileOf(target.Subfile).Rows.TryGetValue(target.Number, out var row))
            {
                row.Values[field.Name!] = value;
                row.Changed = true;
            }
        }
    }

    // ---- painting ----

    /// <summary>Where a record's positions are on the screen: added to its lines and positions, and how far it may go.</summary>
    private readonly record struct Area(int Row, int Column, int LastRow, int LastColumn);

    /// <summary>
    /// The whole screen: every record shown, in the order written, then the window on top. A window
    /// with none of the file's own records under it goes over what the job last showed.
    /// <paramref name="again"/> (PUTOVR) leaves the screen uncleared.
    /// </summary>
    private byte[] Paint(bool read, string? message, bool alarm, bool again)
    {
        _targets.Clear();
        _placed.Clear();
        var inputs = new List<byte[]>();
        (int Row, int Column)? cursor = null;
        (int Row, int Column)? firstInput = null;
        var windows = _shown.Where(n => File.Records[n].Window is not null).ToList();
        var full = _shown.Where(n => File.Records[n].Window is null).ToList();
        var image = windows.Count > 0 && full.Count == 0 && job.Screen is { } under ? under.Copy() : new ScreenImage(File.Rows, File.Columns);
        var screenArea = new Area(0, 0, image.Rows, image.Columns);
        foreach (var name in full) PaintRecord(File.Records[name], image, screenArea, windows.Count == 0, inputs, ref cursor, ref firstInput, ref message);
        for (var i = 0; i < windows.Count; i++)
        {
            var format = File.Records[windows[i]];
            var w = WindowOf(format);
            Frame(image, w);
            PaintRecord(format, image, new Area(w.Row, w.Column + 1, w.Row + w.Lines, w.Column + 1 + w.Positions), i == windows.Count - 1,
                inputs, ref cursor, ref firstInput, ref message);
        }
        // CSRLOC: the cursor where the program's fields say.
        foreach (var name in _shown)
        {
            if (File.Records[name].CursorLocation is var (when, rowField, columnField) && when.Holds(On)
                && variable(rowField)?.Get() is { } r && variable(columnField)?.Get() is { } col && Values.ToDecimal(r) > 0 && Values.ToDecimal(col) > 0)
                cursor = ((int)Values.ToDecimal(r), (int)Values.ToDecimal(col));
        }
        // The message line, cleared when the screen isn't.
        if (message is not null || again)
        {
            var text = (message ?? "").Length > image.Columns - 3 ? message![..(image.Columns - 3)] : message ?? "";
            image.Put(image.Rows, 1, message is null ? Normal : Highlight);
            image.Put(image.Rows, 2, Text(text.PadRight(image.Columns - 2)), image.Columns - 1);
            image.Put(image.Rows, image.Columns, Normal);
        }
        job.Screen = image.Copy();
        var at = cursor ?? firstInput;
        var control = (byte)((read ? 0x18 : 0x00) | (alarm ? 0x04 : 0x00));
        byte[] clear = again ? [] : image.Wide ? [Esc, 0x20, 0x00] : ClearUnit;
        return HostRecord(read ? 0x03 : 0x02, Join(clear,
            [Esc, 0x11, 0x00, control], Header(image.Rows), image.Orders(), Join([.. inputs]), at is { } c ? Ic(c.Row, c.Column) : [],
            read ? ReadMdtFields : []));
    }

    /// <summary>A window's border, with its title, and its inside cleared.</summary>
    private static void Frame(ScreenImage image, Window w)
    {
        var border = Attribute([.. w.BorderAttributes], w.BorderColor ?? "BLU", false);
        var chars = Text(w.BorderCharacters);
        var (top, left, right) = (w.Row, w.Column, w.Column + w.Positions + 3);
        for (var row = top; row <= top + w.Lines + 1; row++)
        {
            var edge = row == top || row == top + w.Lines + 1;
            var (first, middle, last) = row == top ? (chars[0], chars[1], chars[2]) : row == top + w.Lines + 1 ? (chars[5], chars[6], chars[7]) : (chars[3], (byte)0x40, chars[4]);
            image.Put(row, left, border);
            image.Put(row, left + 1, first);
            if (edge)
            {
                for (var c = left + 2; c < right; c++) image.Put(row, c, middle);
            }
            else
            {
                image.Put(row, left + 2, Normal);
                for (var c = left + 3; c < right - 1; c++) image.Put(row, c, 0x40);
                image.Put(row, right - 1, border);
            }
            image.Put(row, right, last);
            image.Put(row, right + 1, Normal);
        }
        if (w.Title is { Length: > 0 } title)
        {
            var text = title.Length > w.Positions - 2 ? title[..(w.Positions - 2)] : title;
            var row = w.TitleBottom ? top + w.Lines + 1 : top;
            var start = left + 2 + (w.Positions + 1 - text.Length) / 2;
            image.Put(row, start - 1, Attribute([], w.TitleColor ?? "WHT", false));
            image.Put(row, start, Text(text), right - 2);
            image.Put(row, start + text.Length, border);
        }
    }

    private void PaintRecord(RecordFormat format, ScreenImage image, Area area, bool active, List<byte[]> inputs,
        ref (int Row, int Column)? cursor, ref (int Row, int Column)? firstInput, ref string? message)
    {
        if (format.Controls is not { } controlled)
        {
            Fields(format, format.Name, area, 0, f => Variable(f).Get(), null, 0, image, active, inputs, ref cursor, ref firstInput, ref message);
            return;
        }
        if (format.DisplayControl?.Holds(On) == true)
            Fields(format, format.Name, area, 0, f => Variable(f).Get(), null, 0, image, active, inputs, ref cursor, ref firstInput, ref message);
        // SFLMSG and SFLMSGID: a message for the subfile, on the message line.
        foreach (var (when, id, messageFile, text) in format.Messages)
        {
            if (when.Holds(On)) message ??= text ?? MessageText(id!, messageFile);
        }
        if (!File.Records.TryGetValue(controlled, out var record)) return;
        // A message subfile (SFLMSGRCD): the messages sent to the program message queue SFLPGMQ names.
        if (record.MessageLine > 0)
        {
            if (format.Display?.Holds(On) != true) return;
            var queue = format.Fields.FirstOrDefault(f => f.MessageQueue && f.Length >= 10) is { Name: { } queueField } ? Values.ToText(variable(queueField)?.Get() ?? "").Trim() : "";
            var pending = job.ProgramMessages.Where(m => queue.Length == 0 || queue == "*" || m.Queue == "*" || m.Queue.Equals(queue, StringComparison.OrdinalIgnoreCase)).Select(m => m.Text).ToList();
            var line = area.Row + record.MessageLine;
            for (var i = 0; i < Math.Max(format.PageSize, 1) && i < pending.Count && line + i <= image.Rows; i++)
            {
                var text = pending[i].Length > image.Columns - 3 ? pending[i][..(image.Columns - 3)] : pending[i];
                image.Put(line + i, 1, Highlight);
                image.Put(line + i, 2, Text(text.PadRight(image.Columns - 3)), image.Columns - 1);
                image.Put(line + i, image.Columns, Normal);
            }
            return;
        }
        var subfile = SubfileOf(controlled);
        if (format.Display?.Holds(On) != true || subfile.Rows.Count == 0) return;
        var visible = record.Fields.Where(f => !f.Hidden).ToList();
        if (visible.Count == 0) return;
        var firstRow = visible.Min(f => f.Row);
        var height = visible.Max(f => f.Row) - firstRow + 1;
        var numbers = subfile.Rows.Keys.ToList();
        var size = Math.Max(format.PageSize, 1);
        // SFLDROP, truncated: each record shows its first line only, so a page has more of them.
        var truncated = _truncated.Contains(format.Name) && height > 1;
        if (truncated)
        {
            size *= height;
            height = 1;
            record = new RecordFormat(record.Name) { Subfile = true };
            record.Fields.AddRange(File.Records[controlled].Fields.Where(f => f.Hidden || f.Row == firstRow));
        }
        for (var i = 0; i < size && subfile.Top + i < numbers.Count; i++)
        {
            var number = numbers[subfile.Top + i];
            var row = subfile.Rows[number];
            Fields(record, controlled, area, i * height, f => row.Values.GetValueOrDefault(f.Name!, f.Numeric ? 0m : ""), controlled, number, image, active,
                inputs, ref cursor, ref firstInput, ref message);
        }
        if (format.End is { } end)
        {
            // More... or Bottom, at the right on the line after the subfile's page.
            var last = subfile.Top + size >= numbers.Count && end.Holds(On);
            var text = last ? "Bottom" : "More...";
            var line = area.Row + firstRow + size * height;
            var right = area.Column > 0 ? area.LastColumn : image.Columns - 1;
            if (line <= (area.Column > 0 ? area.LastRow : image.Rows))
            {
                var start = right - text.Length + 1;
                if (!image.IsWritten(line, start - 1)) image.Put(line, start - 1, Highlight);
                image.Put(line, start, Text(text), right);
                if (!image.IsWritten(line, right + 1)) image.Put(line, right + 1, Normal);
            }
        }
    }

    private void Fields(RecordFormat format, string recordName, Area area, int offset, Func<DisplayField, object> value, string? subfile, int number,
        ScreenImage image, bool active, List<byte[]> inputs, ref (int Row, int Column)? cursor, ref (int Row, int Column)? firstInput, ref string? message)
    {
        var lastRow = area.LastRow;
        var lastColumn = area.Column > 0 ? area.LastColumn : image.Columns;
        foreach (var field in format.Fields)
        {
            if (field.Hidden || !field.When.Holds(On)) continue;
            var (row, column) = (area.Row + field.Row + offset, area.Column + field.Column);
            if (row > lastRow || row > image.Rows || column > lastColumn) continue;
            var attributes = field.Attributes.Where(a => a.When.Holds(On)).Select(a => a.Attribute).ToHashSet();
            if (field.Errors.FirstOrDefault(e => e.When.Holds(On)) is { Text: { } error })
            {
                message ??= error;
                attributes.Add("RI");
                attributes.Add("PC");
            }
            else if (field.ErrorIds.FirstOrDefault(e => e.When.Holds(On)) is { Id: { } errorId } withId)
            {
                message ??= MessageText(errorId, withId.File);
                attributes.Add("RI");
                attributes.Add("PC");
            }
            // The field whose typing failed its checks.
            if (_invalid is var (bad, _) && bad.Field == field && bad.Subfile == subfile && bad.Number == number)
            {
                attributes.Add("RI");
                attributes.Add("PC");
            }
            // DSPATR(&field): the attribute a program-to-system field holds (x'20' to x'3F'; x'80' on as well protects it).
            byte? programAttribute = null;
            var programProtect = false;
            foreach (var (when, name) in field.AttributeFields)
            {
                if (!when.Holds(On) || format.Fields.FirstOrDefault(f => f.Name == name) is not { } holder) continue;
                var held = Values.ToText(value(holder));
                if (held.Length == 0) continue;
                var b = Ebcdic.ToByte(held[0]);
                if ((b & 0x60) != 0x20) continue;
                programAttribute = (byte)((b & 0x1f) | 0x20);
                programProtect = (b & 0x80) != 0;
            }
            var input = active && field.Input && !attributes.Contains("PR") && !programProtect && !_protected.Contains(recordName);
            var text = field.Text ?? SystemText(field) ?? MessageOf(field, value) ?? Format(field, value(field));
            if (column + text.Length - 1 > lastColumn) text = text[..Math.Max(0, lastColumn - column + 1)];
            var attribute = programAttribute ?? Attribute(attributes, field.Colors.LastOrDefault(c => c.When.Holds(On)).Color, field.Input && !attributes.Contains("PR"));
            // The attribute goes in the position before the field.
            var (attributeRow, attributeColumn) = column > 1 ? (row, column - 1) : (row - 1, image.Columns);
            if (field.Name is not null) _placed.Add(new Placed(row, column, text.Length, recordName, field.Name));
            if (attributeRow < 1) continue;
            image.Put(attributeRow, attributeColumn, attribute);
            if (input && field.Usage == 'I') text = (field.Default ?? "").PadRight(text.Length)[..text.Length];
            image.Put(row, column, Text(text), lastColumn);
            if (input)
            {
                var ffw = field.Numeric ? 0x4300 : field.LowerCase ? 0x4000 : 0x4020;
                inputs.Add(Join(Sba(attributeRow, attributeColumn), Sf(ffw, attribute, text.Length), field.Usage == 'B' ? Sanitized(Text(text)) : []));
                _targets.Add(new Target(row, column, field, subfile, number, recordName));
                if (attributes.Contains("PC")) cursor ??= (row, column);
                firstInput ??= (row, column);
            }
            else if (attribute != Normal && column + text.Length <= image.Columns && !image.IsWritten(row, column + text.Length))
            {
                image.Put(row, column + text.Length, Normal);
            }
        }
    }

    /// <summary>MSGID or MSGCON: the message's text, as long as the field; null for a field without.</summary>
    private string? MessageOf(DisplayField field, Func<DisplayField, object> value)
    {
        if (field.MessageText is not var (id, idField, messageFile)) return null;
        var messageId = id ?? Values.ToText(variable(idField!)?.Get() ?? "").Trim();
        if (messageId.Length == 0) return new string(' ', Math.Max(field.Length, 0));
        var text = MessageText(messageId, messageFile);
        var length = field.Length > 0 ? field.Length : text.Length;
        return text.Length >= length ? text[..length] : text.PadRight(length);
    }

    /// <summary>Bytes below 0x20 would be 5250 orders inside a field's data: blanks instead.</summary>
    private static byte[] Sanitized(byte[] text) => [.. text.Select(b => b < 0x20 ? (byte)0x40 : b)];

    /// <summary>A message on the last row, highlighted.</summary>
    private static byte[] Message(string message, int row) =>
        Join(Sba(row, 2), [Highlight], Text(message.Length > 77 ? message[..77] : message));

    private static byte Attribute(HashSet<string> attributes, string? color, bool input)
    {
        if (attributes.Contains("ND")) return 0x27;
        var value = color switch
        {
            "WHT" => 0x22,
            "RED" => 0x28,
            "TRQ" => 0x30,
            "YLW" => 0x32,
            "PNK" => 0x38,
            "BLU" => 0x3a,
            _ => 0x20,
        };
        if (color is null or "GRN" && (attributes.Contains("HI") || attributes.Contains("BL"))) value |= 0x02;
        if (color is "RED" && attributes.Contains("BL")) value |= 0x02;
        if (attributes.Contains("RI")) value |= 0x01;
        // Input fields are underlined unless the DDS says how they look.
        if (attributes.Contains("UL") || input && !attributes.Overlaps(["HI", "RI", "BL"]) && color is null) value |= 0x04;
        // Highlighted, reversed and underlined together would be nondisplay.
        return (byte)(value == 0x27 ? 0x26 : value);
    }

    private string? SystemText(DisplayField field) => field.System switch
    {
        null => null,
        "DATE" => field.EditCode is null
            ? DateTime.Now.ToString(field.FourDigitYear ? "MMddyyyy" : "MMddyy")
            : DateTime.Now.ToString(field.FourDigitYear ? "MM'/'dd'/'yyyy" : "MM'/'dd'/'yy"),
        "TIME" => DateTime.Now.ToString("HH':'mm':'ss"),
        "USER" => user.PadRight(10),
        _ => "FAKEIBMI",
    };

    internal static string Format(DisplayField field, object value)
    {
        if (!field.Numeric)
        {
            var text = Values.ToText(value);
            return text.Length >= field.Length ? text[..field.Length] : text.PadRight(field.Length);
        }
        if (field.EditWord is { } word) return Interpreter.EditWord(Values.ToDecimal(value), field.Decimals, word);
        var edited = Values.Edit(Values.ToDecimal(value), field.Length, field.Decimals, field.EditCode ?? 'X');
        if (field.EditCurrency is { } currency)
        {
            // The symbol just before the first character of the number.
            var first = edited.Length - edited.TrimStart().Length;
            edited = new string(' ', first) + currency + edited[first..];
        }
        if (field.EditFill is '*') edited = new string('*', edited.Length - edited.TrimStart().Length) + edited.TrimStart();
        return edited;
    }
}
