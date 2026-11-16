// Display file DDS, parsed from its fixed columns into record formats and fields. What isn't
// supported becomes a diagnostic; the rest of the file still works.

using System.Globalization;

namespace SimI.Dds;

/// <summary>
/// Option indicators: sets of them, of which one has to hold all its indicators (03 is on, N03 is
/// off). A set goes on over lines with A in column 7; O starts another. None always holds.
/// </summary>
internal sealed record Condition(IReadOnlyList<IReadOnlyList<(int Indicator, bool Not)>> Groups)
{
    public Condition(IReadOnlyList<(int Indicator, bool Not)> terms) : this(terms.Count == 0 ? [] : new[] { terms }) { }

    public static readonly Condition Always = new(Array.Empty<IReadOnlyList<(int Indicator, bool Not)>>());

    public bool Holds(Func<int, bool> indicator) => Groups.Count == 0 || Groups.Any(g => g.All(t => indicator(t.Indicator) != t.Not));
}

/// <summary>A key the user may press: its AID byte, the response indicator it sets (0: none), and whether it's a CA key, which returns no data.</summary>
internal sealed record FunctionKey(int Aid, int Indicator, bool Attention, Condition When);

internal sealed class DisplayField
{
    /// <summary>The field's name, or null for a constant.</summary>
    public string? Name { get; init; }

    /// <summary>A constant's text.</summary>
    public string? Text { get; set; }

    /// <summary>DATE, TIME, USER or SYSNAME, for a constant the system fills in.</summary>
    public string? System { get; set; }

    public int Length { get; set; }
    public int Decimals { get; set; }
    public bool Numeric { get; set; }

    /// <summary>O (output), I (input), B (both), H (hidden) or P (program to system: a display attribute).</summary>
    public char Usage { get; init; } = 'O';

    /// <summary>Not on the screen: hidden, or a program-to-system field.</summary>
    public bool Hidden => Usage is 'H' or 'P';

    /// <summary>DSPATR(&field): program-to-system fields whose value is the field's display attribute.</summary>
    public List<(Condition When, string Field)> AttributeFields { get; } = [];

    /// <summary>EDTCDE's fill character (*) and floating currency symbol.</summary>
    public char? EditFill { get; set; }
    public char? EditCurrency { get; set; }

    /// <summary>DFT on a named input field: what it shows before anything's typed.</summary>
    public string? Default { get; set; }

    /// <summary>EDTWRD: the edit word, in place of an edit code.</summary>
    public string? EditWord { get; set; }

    /// <summary>CHANGE(nn): the indicator set on when the user changes the field.</summary>
    public int ChangeIndicator { get; set; }

    /// <summary>ERRMSGID: messages from a message file, shown as ERRMSG's are.</summary>
    public List<(Condition When, string Id, string? File)> ErrorIds { get; } = [];

    /// <summary>MSGID: the field shows a message's text (its id from the keyword or, with &field, from a field).</summary>
    public (string? Id, string? IdField, string? File)? MessageText { get; set; }

    /// <summary>The checks on what's typed: VALUES, RANGE, COMP or CMP, and CHECK's ME, MF, VN.</summary>
    public List<string> AllowedValues { get; } = [];
    public (string Low, string High)? Range { get; set; }
    public (string Op, string Value)? Compare { get; set; }
    public bool MandatoryEnter { get; set; }
    public bool MandatoryFill { get; set; }
    public bool ValidName { get; set; }

    /// <summary>CHECK(RB) or CHECK(RZ): what's typed is right-adjusted, filled with blanks or zeros.</summary>
    public char? RightAdjust { get; set; }

    /// <summary>CHKMSGID: the message shown when a check fails, in place of the system's.</summary>
    public (string Id, string? File)? CheckMessage { get; set; }

    /// <summary>SFLPGMQ or SFLMSGKEY: a field of a message subfile, which isn't on the screen.</summary>
    public bool MessageQueue { get; set; }

    public int Row { get; init; }
    public int Column { get; init; }
    public Condition When { get; init; } = Condition.Always;
    public List<(Condition When, string Attribute)> Attributes { get; } = [];
    public List<(Condition When, string Color)> Colors { get; } = [];
    public List<(Condition When, string Text)> Errors { get; } = [];
    public char? EditCode { get; set; }
    public bool LowerCase { get; set; }

    /// <summary>SFLRCDNBR: the field holds the subfile record whose page is shown.</summary>
    public bool RecordNumber { get; set; }

    /// <summary>DATE(*YY): the year with four digits.</summary>
    public bool FourDigitYear { get; set; }

    // Printer files: lines spaced or skipped to before and after the field (0: none).
    public int SpaceBefore { get; set; }
    public int SpaceAfter { get; set; }
    public int SkipBefore { get; set; }
    public int SkipAfter { get; set; }

    public bool Input => Usage is 'I' or 'B';

    /// <summary>How many columns the field takes on the screen.</summary>
    public int Width => Text is not null ? Text.Length
        : System is not null ? SystemWidth
        : Numeric && EditWord is { } word ? word.Length
        : Numeric && EditCode is { } code ? Rpg.Values.Edit(0, Length, Decimals, code).Length + (EditCurrency is null ? 0 : 1)
        : Length;

    private int SystemWidth => System switch
    {
        "DATE" => EditCode is null ? (FourDigitYear ? 8 : 6) : FourDigitYear ? 10 : 8,
        "TIME" => 8,
        "USER" => 10,
        "PAGNBR" => EditCode is null ? 4 : Rpg.Values.Edit(0, 4, 0, EditCode.Value).Length,
        _ => 8,
    };
}

/// <summary>
/// WINDOW: the window's top border is on <see cref="Row"/>, its left border at <see cref="Column"/>;
/// inside it are <see cref="Lines"/> lines of <see cref="Positions"/> positions, where its fields are.
/// </summary>
internal sealed record Window(int Row, int Column, int Lines, int Positions)
{
    /// <summary>WDWBORDER: the border's color and display attributes, and its eight characters (corners and sides).</summary>
    public string? BorderColor { get; init; }
    public IReadOnlyList<string> BorderAttributes { get; init; } = [];
    public string BorderCharacters { get; init; } = "...::.:.";

    /// <summary>WDWTITLE: the title in the top border, and its color.</summary>
    public string? Title { get; init; }
    public string? TitleColor { get; init; }
    public bool TitleBottom { get; init; }
}

internal sealed class RecordFormat(string name)
{
    public string Name { get; } = name;
    public List<DisplayField> Fields { get; } = [];
    public List<FunctionKey> Keys { get; } = [];

    /// <summary>OVERLAY: what's on the screen stays when this record is written.</summary>
    public bool Overlay { get; set; }

    /// <summary>SFL: a subfile record.</summary>
    public bool Subfile { get; set; }

    /// <summary>SFLCTL(name): the subfile this record controls.</summary>
    public string? Controls { get; set; }

    public int PageSize { get; set; }
    public Condition? Display { get; set; }
    public Condition? DisplayControl { get; set; }
    public Condition? Clear { get; set; }
    public Condition? End { get; set; }

    /// <summary>WINDOW: the record is a window over what's on the screen.</summary>
    public Window? Window { get; set; }

    /// <summary>PUTOVR: when it holds, writing the record again changes only its fields, not the whole screen.</summary>
    public Condition? PutOverride { get; set; }

    /// <summary>ALARM: the display beeps when the record is written, when this holds.</summary>
    public Condition? Alarm { get; set; }

    /// <summary>INVITE: written, the record waits for the user without the program waiting; READ of the file takes the reply.</summary>
    public bool Invite { get; set; }

    /// <summary>ASSUME: the record is assumed to be on the screen, so opening the file leaves the screen as it is.</summary>
    public bool Assume { get; set; }

    /// <summary>RTNCSRLOC: the fields that get the record and field the cursor was on.</summary>
    public (string Record, string Field)? ReturnCursor { get; set; }

    /// <summary>CHANGE(nn) on the record: the indicator set on when the user changes any of its fields.</summary>
    public int ChangeIndicator { get; set; }

    /// <summary>SFLNXTCHG: a subfile record written or updated with this holding is read by READC as changed.</summary>
    public Condition? NextChange { get; set; }

    /// <summary>SFLINZ: writing the control record fills the subfile with SFLSIZ empty records.</summary>
    public Condition? Initialize { get; set; }
    public int SubfileSize { get; set; }

    /// <summary>SFLDROP or SFLFOLD: the key that switches the subfile between one line and all its lines a record, and which it starts in.</summary>
    public (int Aid, bool Folded)? FoldKey { get; set; }

    /// <summary>PROTECT: writing the record protects the input fields of what's on the screen under it.</summary>
    public Condition? Protect { get; set; }

    /// <summary>CSRLOC(&line &position): where the cursor goes, from two fields.</summary>
    public (Condition When, string Row, string Column)? CursorLocation { get; set; }

    /// <summary>WINDOW(name): the window record whose window this record is in.</summary>
    public string? WindowOf { get; set; }

    /// <summary>WINDOW(&line &position ...) or WINDOW(*DFT ...): a window the program (or the cursor) places.</summary>
    public (string? Row, string? Column)? WindowPlace { get; set; }

    /// <summary>SFLMSGID or SFLMSG: a message for the subfile's control record.</summary>
    public List<(Condition When, string? Id, string? File, string? Text)> Messages { get; } = [];

    /// <summary>SFLMSGRCD(line): a message subfile, on that line, of the program message queue SFLPGMQ names.</summary>
    public int MessageLine { get; set; }

    // Printer files: lines spaced or skipped to before and after the record (0: none).
    public int SpaceBefore { get; set; }
    public int SpaceAfter { get; set; }
    public int SkipBefore { get; set; }
    public int SkipAfter { get; set; }
}

internal sealed class DisplayFile(string name)
{
    public string Name { get; } = name;

    /// <summary>DSPSIZ(27 132 *DS4): a 27 x 132 screen.</summary>
    public bool Wide { get; private set; }

    public int Rows => Wide ? 27 : 24;
    public int Columns => Wide ? 132 : 80;

    /// <summary>A printer file's DDS (CRTPRTF), not a display file's.</summary>
    public bool Printer { get; private set; }

    /// <summary>CRTDSPF WAITRCD: how many seconds READ of the file waits for the user; 0 for no limit.</summary>
    public int WaitRecord { get; set; }

    /// <summary>CRTPRTF PAGESIZE and OVRFLW: the lines on a page, and the line the overflow indicator comes on at.</summary>
    public int PageLength { get; set; } = 66;
    public int PageWidth { get; set; } = 132;
    public int OverflowLine { get; set; } = 60;
    public Dictionary<string, RecordFormat> Records { get; } = [];

    /// <summary>Keys allowed on every record.</summary>
    public List<FunctionKey> Keys { get; } = [];

    public List<Diagnostic> Diagnostics { get; } = [];

    // AID bytes.
    public const int Enter = 0xf1;
    public const int RollDown = 0xf4;
    public const int RollUp = 0xf5;

    /// <summary>The AID byte of function key 1 to 24.</summary>
    public static int Aid(int key) => key <= 12 ? 0x30 + key : 0xb0 + key - 12;

    private static readonly HashSet<string> Ignored =
    [
        "INDARA", "PRINT", "HELP", "ALTHELP", "MSGLOC", "CHGINPDFT", "ERRSFL", "OPENPRT", "OVRDTA", "OVRATR", "KEEP",
        "BLINK", "TEXT", "RTNDTA", "ALIAS", "DFTVAL", "BLKFOLD", "AUTO", "INDTXT", "FLDCSRPRG", "LOCK", "FRCDTA", "ERASEINP",
        "INZRCD", "UNLOCK", "MDTOFF", "LOGINP", "LOGOUT", "HLPRTN", "HLPTITLE", "HLPCLR", "SFLCSRRRN", "SFLMODE", "SFLLIN", "CHRID", "NOCCSID",
        "ERASE", "HOME", "CLEAR", "SETOF", "SETOFF", "ALWROL", "USRDSPMGT", "ALTPAGEUP", "ALTPAGEDWN", "RETKEY", "RETCMDKEY",
        "RMVWDW", "USRRSTDSP", "WDWRSTDSP", "SFLMSGKEY", "SFLPGMQ", "TRNSPY", "CPI", "LPI", "FONT", "UNDERLINE", "HIGHLIGHT",
        "REF", "REFFLD", "INDTXT", "CHRSIZ", "DFNCHR", "DRAWER", "PAGRTT", "PRTQLT", "TXTRTT", "BOX", "LINE", "POSITION", "FLTFIXDEC", "TRANSFORM",
        // Help, taken with no help to show.
        "HLPARA", "HLPPNLGRP", "HLPRCD", "HLPDOC", "HLPSCHIDX", "HLPID", "HLPSEQ", "HLPEXCLD", "HLPFULL", "HLPBDY", "HLPSHELF", "HLPCMDKEY", "HLPARA",
        // What a 5250 emulator does for itself, or makes no difference to what's shown.
        "ENTFLDATR", "MSGALARM", "PUTRETAIN", "MOUBTN", "VLDCMDKEY", "DLTEDT", "EXTIND", "DUP", "DATFMT", "DATSEP", "TIMFMT", "TIMSEP", "SFLRNA",
        "BARCODE", "MAPVAL", "SFLSCROLL", "SFLRTNSEL", "CSRINPONLY", "MNUCNL", "CHGINPDFT", "BLANKS",
    ];

    /// <summary>The values of VALUES, RANGE and COMP: quoted ones unquoted, numbers as they are.</summary>
    private static List<string> Values(string argument)
    {
        var values = new List<string>();
        var i = 0;
        while (i < argument.Length)
        {
            if (char.IsWhiteSpace(argument[i]))
            {
                i++;
                continue;
            }
            if (argument[i] == '\'')
            {
                values.Add(Quoted(argument, ref i));
                continue;
            }
            var start = i;
            while (i < argument.Length && !char.IsWhiteSpace(argument[i])) i++;
            values.Add(argument[start..i]);
        }
        return values;
    }

    /// <summary>A keyword's value on a line: REF(FRF) gives FRF; null when it isn't there.</summary>
    private static string? Keyword(string words, string keyword)
    {
        var at = System.Text.RegularExpressions.Regex.Match(words, $@"\b{keyword}\(", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!at.Success) return null;
        var end = words.IndexOf(')', at.Index);
        return end < 0 ? null : words[(at.Index + at.Length)..end];
    }

    private static int Adjusted(string text, int value) =>
        text.Length == 0 ? value
        : text[0] is '+' or '-' && int.TryParse(text[1..], out var by) ? value + (text[0] == '+' ? by : -by)
        : int.TryParse(text, out var own) ? own : value;

    /// <summary>Parses a display file's DDS, or with <paramref name="printer"/> a printer file's, whose fields have a position but no line.</summary>
    public static DisplayFile Parse(string name, string source, bool printer = false, DatabaseDds.FieldReference? reference = null)
    {
        var file = new DisplayFile(name) { Printer = printer };
        string? referenceFile = null;
        RecordFormat? record = null;
        DisplayField? field = null;
        var pending = "";
        var pendingLine = 0;
        var pendingWhen = Condition.Always;
        var lastEnd = (Row: 1, Column: 1);
        var lines = source.Replace("\r\n", "\n").Split('\n');
        void Report(Severity severity, string message, int line) => file.Diagnostics.Add(new Diagnostic(line, severity, message));
        List<List<(int Indicator, bool Not)>>? carried = null;
        var help = false;
        var helpNoted = false;
        (DisplayField Field, string Name, string Length, string Decimals, char Type, int Line)? unresolved = null;
        void NoteHelp()
        {
            if (helpNoted) return;
            helpNoted = true;
            Report(Severity.Info, "help (H specs and the HLP keywords) is taken: the Help key shows no help", 0);
        }

        // R in column 29: the field's length, type and decimals are a database file's field's (REFFLD,
        // or the same name in the REF file); its own replace them or, with + or -, change them.
        void Resolve(DisplayField target, string fieldName, string lengthText, string decimalsText, char dataType, int line, string words)
        {
            var name = fieldName;
            var from = referenceFile;
            if (Keyword(words, "REFFLD") is { } refFld && refFld.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } parts)
            {
                name = parts[0].Split('/')[^1].ToUpperInvariant();
                if (parts.Length > 1) from = parts[1];
            }
            var found = from is null || reference is null ? null : reference(from, name);
            if (found is null)
            {
                Report(Severity.Unsupported, from is null || reference is null
                    ? $"{fieldName} refers to a field in another file, which isn't found here: give its length here"
                    : $"{fieldName} refers to field {name} of {from.ToUpperInvariant()}, which isn't found", line);
                return;
            }
            var (baseLength, baseDecimals, numeric) = found.Type.Kind switch
            {
                Rpg.RpgKind.Char or Rpg.RpgKind.Varchar => (found.Type.Length, 0, false),
                Rpg.RpgKind.Decimal => (found.Type.Length, found.Type.Decimals, true),
                Rpg.RpgKind.Int => (found.Type.Length, 0, true),
                Rpg.RpgKind.Date => (10, 0, false),
                Rpg.RpgKind.Time => (8, 0, false),
                Rpg.RpgKind.Timestamp => (26, 0, false),
                _ => (found.Type.Length, found.Type.Decimals, found.Type.IsNumeric),
            };
            target.Length = Adjusted(lengthText, baseLength);
            target.Decimals = numeric ? Adjusted(decimalsText, baseDecimals) : int.TryParse(decimalsText, out var own) ? own : 0;
            target.Numeric = dataType is 'S' or 'Y' or 'D' or 'P' or 'B' || dataType == ' ' && (numeric || decimalsText.Length > 0);
            if (target.Numeric && (target.Length > 28 || target.Decimals > target.Length))
                Report(Severity.Unsupported, $"{fieldName}: numbers of more than 28 digits aren't supported", line);
        }

        for (var n = 0; n < lines.Length; n++)
        {
            var number = n + 1;
            var line = lines[n].TrimEnd().PadRight(80);
            if (line.Trim().Length == 0 || line[6] == '*') continue;
            if (line[5] is not ('A' or 'a' or ' '))
            {
                Report(Severity.Unsupported, "this isn't a DDS line: column 6 has to be A or blank", number);
                continue;
            }
            string Columns(int from, int to) => line[(from - 1)..to].Trim();

            // A display size condition in columns 9 to 13 (*DS3, *DS4): the line is for that size of screen only.
            var sizeCondition = Columns(9, 16).ToUpperInvariant();
            if (sizeCondition is "*DS3" or "*DS4")
            {
                if (sizeCondition == "*DS4" != file.Wide) continue;
                line = line[..7] + new string(' ', 9) + line[16..];
            }
            // Option indicators, columns 8 to 16: up to three of N and a number.
            var terms = new List<(int Indicator, bool Not)>();
            var conditionProblem = false;
            for (var c = 7; c < 16; c += 3)
            {
                var digits = line.Substring(c + 1, 2).Trim();
                if (digits.Length == 0) continue;
                if (int.TryParse(digits, out var indicator) && indicator is >= 1 and <= 99) terms.Add((indicator, line[c] is 'N' or 'n'));
                else conditionProblem = true;
            }
            if (conditionProblem) Report(Severity.Unsupported, "an option indicator is N and a number from 01 to 99", number);

            var type = char.ToUpperInvariant(line[16]);
            var fieldName = Columns(19, 28).ToUpperInvariant();
            var position = Columns(39, 41);
            var keywords = line[44..Math.Min(line.Length, 80)].TrimEnd();

            // A in column 7 adds the line's indicators to the line before's (AND); O starts another set (OR).
            var andOr = char.ToUpperInvariant(line[6]);
            List<List<(int Indicator, bool Not)>> groups;
            if (andOr is 'A' or 'O' && carried is not null)
            {
                groups = carried;
                if (andOr == 'A') groups[^1].AddRange(terms);
                else groups.Add(terms);
            }
            else groups = terms.Count == 0 ? [] : [terms];
            // A line of indicators alone conditions the line after it.
            if (terms.Count > 0 && type == ' ' && fieldName.Length == 0 && position.Length == 0 && keywords.Trim().Length == 0 && Columns(42, 44).Length == 0)
            {
                carried = groups;
                continue;
            }
            carried = null;
            var when = groups.Count == 0 ? Condition.Always : new Condition([.. groups.Select(g => (IReadOnlyList<(int Indicator, bool Not)>)g.ToList())]);

            // Help (H specs and their keyword lines): taken, and the Help key shows no help.
            if (type == 'H')
            {
                help = true;
                NoteHelp();
                continue;
            }
            if (type == 'R' || fieldName.Length > 0 || position.Length > 0) help = false;
            if (help) continue;

            if (record is null && Keyword(keywords, "REF") is { } refFile) referenceFile = refFile.Trim().Split(' ')[0];
            if (type == 'R')
            {
                record = new RecordFormat(fieldName);
                file.Records[fieldName] = record;
                field = null;
            }
            else if (type is 'K' or 'S' or 'J')
            {
                Report(Severity.Unsupported, $"type {type} specifications aren't for display files", number);
                continue;
            }
            else if (fieldName.Length > 0 || position.Length > 0 || printer && Columns(42, 44).Length > 0)
            {
                if (record is null)
                {
                    Report(Severity.Unsupported, "a field has to follow a record format (R in column 17)", number);
                    continue;
                }
                var referenced = line[28] is 'R' or 'r';
                var usage = char.ToUpperInvariant(line[37]);
                if (usage == ' ') usage = 'O';
                // A message subfile's fields (SFLPGMQ, SFLMSGKEY) have their own lengths, and no place on the screen.
                var messageQueue = System.Text.RegularExpressions.Regex.IsMatch(keywords, @"\bSFL(PGMQ|MSGKEY)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (messageQueue) usage = 'H';
                if (usage == 'M')
                {
                    Report(Severity.Unsupported, $"usage M (message) fields aren't supported ({fieldName})", number);
                    usage = 'H';
                }
                var dataType = char.ToUpperInvariant(line[34]);
                var lengthText = Columns(30, 34);
                var decimalsText = Columns(36, 37);
                // Dates, times and timestamps are shown as their text: as long as their format makes them.
                if (dataType is 'L' or 'T' or 'Z' && lengthText.Length == 0)
                {
                    var format = (Keyword(keywords, "DATFMT") ?? Keyword(keywords, "TIMFMT") ?? "").Trim().ToUpperInvariant();
                    lengthText = dataType switch
                    {
                        'T' => "8",
                        'Z' => "26",
                        _ => format switch { "*MDY" or "*DMY" or "*YMD" => "8", "*JUL" => "6", _ => "10" },
                    };
                }
                if (dataType is 'F' or 'J' or 'E' or 'O' or 'G')
                    Report(Severity.Unsupported, $"data type {dataType} isn't supported ({fieldName}): use A, S or Y", number);
                int.TryParse(lengthText, out var length);
                int.TryParse(decimalsText, out var decimals);
                if (messageQueue && length == 0) length = keywords.Contains("SFLPGMQ", StringComparison.OrdinalIgnoreCase) ? 10 : 4;
                if (fieldName.Length > 0 && length == 0 && !referenced) Report(Severity.Unsupported, $"{fieldName} needs a length", number);
                var (row, column) = (0, 0);
                if (printer)
                {
                    var columnText = Columns(42, 44);
                    if (columnText.StartsWith('+'))
                    {
                        int.TryParse(columnText.TrimStart('+'), out var gap);
                        column = lastEnd.Column + gap;
                    }
                    else if (columnText.Length > 0 && (!int.TryParse(columnText, out column) || column is < 1 or > 378))
                    {
                        Report(Severity.Unsupported, $"position {columnText} isn't on the page", number);
                        column = 1;
                    }
                    else if (columnText.Length == 0) column = lastEnd.Column;
                }
                else if (usage is not ('H' or 'P'))
                {
                    var columnText = Columns(42, 44);
                    if (position.StartsWith('+') || columnText.StartsWith('+'))
                    {
                        // +n: n columns after the previous field.
                        int.TryParse(columnText.TrimStart('+'), out var gap);
                        (row, column) = (lastEnd.Row, lastEnd.Column + gap);
                    }
                    else if (!int.TryParse(position, out row) | !int.TryParse(columnText, out column) || row < 1 || column < 1
                             || (record.Window is { } w ? row > w.Lines || column > w.Positions : row > file.Rows || column > file.Columns))
                    {
                        Report(Severity.Unsupported, record.Window is { } window
                            ? $"line {position} position {columnText} isn't in the window of {window.Lines} by {window.Positions}"
                            : $"line {position} position {columnText} isn't on a {file.Rows} by {file.Columns} screen", number);
                        (row, column) = (1, 1);
                    }
                }
                field = new DisplayField
                {
                    Name = fieldName.Length > 0 ? fieldName : null,
                    Length = length,
                    Decimals = decimals,
                    Numeric = dataType is 'S' or 'Y' or 'D' or 'P' or 'B' || dataType == ' ' && decimalsText.Length > 0,
                    Usage = usage,
                    Row = row,
                    Column = column,
                    When = when,
                    MessageQueue = messageQueue,
                };
                if (!referenced && field.Numeric && (length > 28 || decimals > length)) Report(Severity.Unsupported, $"{fieldName}: numbers of more than 28 digits aren't supported", number);
                record.Fields.Add(field);
                // A reference field's definition waits for its keywords (REFFLD can go on to the next line).
                if (referenced) unresolved = (field, fieldName, lengthText, decimalsText, dataType, number);
            }

            // Keywords, which may continue on the next line after + or -.
            var continues = keywords.EndsWith('+') || keywords.EndsWith('-');
            if (pending.Length == 0)
            {
                pendingLine = number;
                pendingWhen = when;
            }
            pending += continues ? keywords[..^1] + (keywords.EndsWith('-') ? "" : "") : keywords;
            if (continues)
            {
                if (keywords.EndsWith('+') && n + 1 < lines.Length && lines[n + 1].Length > 44)
                    lines[n + 1] = lines[n + 1][..44] + lines[n + 1][44..].TrimStart();
                continue;
            }
            if (unresolved is { } u)
            {
                Resolve(u.Field, u.Name, u.Length, u.Decimals, u.Type, u.Line, pending);
                unresolved = null;
            }
            foreach (var (keyword, argument) in Keywords(pending))
            {
                try
                {
                    file.Apply(keyword, argument, record, field, pendingWhen);
                }
                catch (FormatException e)
                {
                    Report(Severity.Unsupported, e.Message, pendingLine);
                }
            }
            pending = "";
            if (field is { Hidden: false } && (fieldName.Length > 0 || position.Length > 0 || printer && Columns(42, 44).Length > 0 || field.Text is not null || field.System is not null))
                lastEnd = (field.Row, field.Column + field.Width);
        }

        foreach (var format in file.Records.Values)
        {
            if (format.WindowOf is { } windowOf)
            {
                if (file.Records.TryGetValue(windowOf, out var defining) && defining.Window is { } window && defining.WindowOf is null)
                    (format.Window, format.WindowPlace) = (window, defining.WindowPlace);
                else Report(Severity.Unsupported, $"WINDOW({windowOf}): there's no window record {windowOf}", 0);
            }
            if (format.Controls is { } controlled)
            {
                if (!file.Records.TryGetValue(controlled, out var subfile) || !subfile.Subfile)
                    Report(Severity.Unsupported, $"SFLCTL({controlled}): there's no subfile record of that name before {format.Name}", 0);
                if (format.PageSize < 1) Report(Severity.Unsupported, $"{format.Name} needs SFLPAG", 0);
            }
        }
        if (file.Records.Count == 0) Report(Severity.Unsupported, "the display file has no record format", 0);
        return file;
    }

    /// <summary>Splits a line's functions: NAME, NAME(arguments) or a quoted constant, which comes back as keyword "'".</summary>
    private static IEnumerable<(string Keyword, string Argument)> Keywords(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }
            if (text[i] == '\'')
            {
                yield return ("'", Quoted(text, ref i));
                continue;
            }
            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '(') i++;
            var keyword = text[start..i].ToUpperInvariant();
            var argument = "";
            if (i < text.Length && text[i] == '(')
            {
                var from = ++i;
                var depth = 1;
                while (i < text.Length && depth > 0)
                {
                    if (text[i] == '\'')
                    {
                        Quoted(text, ref i);
                        continue;
                    }
                    if (text[i] == '(') depth++;
                    else if (text[i] == ')') depth--;
                    i++;
                }
                argument = text[from..Math.Max(from, i - 1)];
            }
            yield return (keyword, argument);
        }
    }

    /// <summary>The groups of WDWBORDER and WDWTITLE: (*COLOR BLU) (*TEXT 'x') ..., each as its word and the rest, unquoted.</summary>
    private static IEnumerable<(string What, string Value)> Groups(string argument)
    {
        foreach (var (keyword, inner) in Keywords(argument))
        {
            // Keywords() takes "(*COLOR BLU)" as an empty keyword with an argument.
            var text = keyword.Length == 0 ? inner : keyword;
            var space = text.IndexOf(' ');
            var what = (space < 0 ? text : text[..space]).ToUpperInvariant();
            var rest = space < 0 ? "" : text[(space + 1)..].Trim();
            var quote = rest.IndexOf('\'');
            yield return (what, quote >= 0 ? Quoted(rest, ref quote) : rest);
        }
    }

    private static string Quoted(string text, ref int i)
    {
        var value = new System.Text.StringBuilder();
        i++;
        while (i < text.Length)
        {
            if (text[i] == '\'')
            {
                if (i + 1 < text.Length && text[i + 1] == '\'')
                {
                    value.Append('\'');
                    i += 2;
                    continue;
                }
                i++;
                break;
            }
            value.Append(text[i++]);
        }
        return value.ToString();
    }

    /// <summary>Applies one keyword to the file, record or field it's written on. Throws FormatException for what isn't supported.</summary>
    private void Apply(string keyword, string argument, RecordFormat? record, DisplayField? field, Condition when)
    {
        string[] Words() => argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int Indicator() => Words().FirstOrDefault() is { } word && int.TryParse(word, out var n) && n is >= 1 and <= 99 ? n : 0;
        FormatException No(string what) => new($"{what} isn't supported");
        var keys = record?.Keys ?? Keys;

        if (keyword == "'")
        {
            if (field is null || field.Name is not null) throw No("a constant with no line and position");
            field.Text = argument;
            return;
        }
        if (keyword.Length == 4 && keyword[..2] is "CA" or "CF" && int.TryParse(keyword[2..], out var key) && key is >= 1 and <= 24)
        {
            keys.Add(new FunctionKey(Aid(key), Indicator(), keyword[1] == 'A', when));
            return;
        }
        if (Ignored.Contains(keyword)) return;
        switch (keyword)
        {
            case "DSPSIZ":
            {
                // The first size is the one used: 24 by 80 (*DS3) or 27 by 132 (*DS4).
                var words = Words().Select(w => w.ToUpperInvariant()).ToList();
                var first = words.FirstOrDefault() ?? "";
                if (first is "27" or "*DS4") Wide = true;
                else if (first is not ("24" or "*DS3")) throw No($"DSPSIZ({argument}): only 24 by 80 (*DS3) and 27 by 132 (*DS4)");
                return;
            }
            case "ALARM" or "INVITE" when record is null:
                return;
            case "FORMLEN" when record is null && Printer:
                PageLength = int.TryParse(argument.Trim(), out var formLength) && formLength > 0 ? formLength : throw No($"FORMLEN({argument})");
                return;
            case "FORMOFL" when record is null && Printer:
                OverflowLine = int.TryParse(argument.Trim(), out var formOverflow) && formOverflow > 0 ? formOverflow : throw No($"FORMOFL({argument})");
                return;
            case "ROLLUP" or "PAGEDOWN":
                keys.Add(new FunctionKey(RollUp, Indicator(), false, when));
                return;
            case "ROLLDOWN" or "PAGEUP":
                keys.Add(new FunctionKey(RollDown, Indicator(), false, when));
                return;
        }
        if (record is null) throw No($"the file keyword {keyword}");
        if (field is null)
        {
            switch (keyword)
            {
                case "OVERLAY" or "CLRL":
                    record.Overlay = true;
                    return;
                case "SFL":
                    record.Subfile = true;
                    return;
                case "SFLCTL":
                    record.Controls = argument.Trim().ToUpperInvariant();
                    return;
                case "SFLSIZ":
                    record.SubfileSize = int.TryParse(argument.Trim(), out var size) ? size : 0;
                    return;
                case "SFLPAG":
                    record.PageSize = int.TryParse(argument, out var page) && page >= 1 && page < Rows ? page : throw No($"SFLPAG({argument})");
                    return;
                case "WINDOW":
                {
                    // *NOMSGLIN, *MSGLIN, *RSTCSR, *NORSTCSR: where the window's messages go, and the cursor: taken.
                    var words = Words().Where(w => !w.StartsWith('*') || w.Equals("*DFT", StringComparison.OrdinalIgnoreCase)).ToArray();
                    var numbers = words.Select(w => int.TryParse(w, out var n) ? n : -1).ToList();
                    if (numbers is [var top, var left, var lines, var positions] && numbers.All(n => n > 0))
                    {
                        if (top + lines + 1 > Rows || left + positions + 3 > Columns) throw No($"WINDOW({argument}): the window doesn't fit on the screen, so it");
                        record.Window = new Window(top, left, lines, positions);
                        return;
                    }
                    // WINDOW(&line &position lines positions): the program says where; WINDOW(*DFT lines positions): by the cursor.
                    if (words.Length >= 3 && words[0].Equals("*DFT", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(words[1], out var defaultLines) && int.TryParse(words[2], out var defaultPositions))
                    {
                        record.Window = new Window(1, 1, defaultLines, defaultPositions);
                        record.WindowPlace = (null, null);
                        return;
                    }
                    if (words.Length >= 4 && words[0].StartsWith('&') && words[1].StartsWith('&')
                        && int.TryParse(words[2], out var placedLines) && int.TryParse(words[3], out var placedPositions))
                    {
                        record.Window = new Window(1, 1, placedLines, placedPositions);
                        record.WindowPlace = (words[0][1..].ToUpperInvariant(), words[1][1..].ToUpperInvariant());
                        return;
                    }
                    if (words is [var other] && Records.TryGetValue(other.ToUpperInvariant(), out var defined) && defined.Window is { } shared)
                    {
                        record.Window = shared;
                        record.WindowPlace = defined.WindowPlace;
                        return;
                    }
                    // A window record defined further on: its window, when the file's read.
                    if (words is [var later] && !int.TryParse(later, out _))
                    {
                        record.WindowOf = later.ToUpperInvariant();
                        record.Window = new Window(1, 1, Rows - 2, Columns - 4);
                        return;
                    }
                    throw No($"WINDOW({argument})");
                }
                case "CHANGE":
                    record.ChangeIndicator = Indicator();
                    return;
                case "SFLNXTCHG":
                    record.NextChange = when;
                    return;
                case "SFLINZ":
                    record.Initialize = when;
                    return;
                case "SFLDROP" or "SFLFOLD":
                {
                    var keyName = Words().FirstOrDefault()?.ToUpperInvariant() ?? "";
                    if (keyName.Length != 4 || keyName[..2] is not ("CA" or "CF") || !int.TryParse(keyName[2..], out var foldKey) || foldKey is < 1 or > 24)
                        throw No($"{keyword}({argument})");
                    record.FoldKey = (Aid(foldKey), keyword == "SFLFOLD");
                    return;
                }
                case "PROTECT":
                    record.Protect = when;
                    return;
                case "CSRLOC":
                {
                    // CSRLOC(line-field position-field).
                    if (Words() is not [var rowName, var columnName]) throw No($"CSRLOC({argument})");
                    record.CursorLocation = (when, rowName.TrimStart('&').ToUpperInvariant(), columnName.TrimStart('&').ToUpperInvariant());
                    return;
                }
                case "SFLMSGID":
                {
                    var words = Words();
                    if (words.Length < 2) throw No($"SFLMSGID({argument})");
                    record.Messages.Add((when, words[0].ToUpperInvariant(), words[1].ToUpperInvariant(), null));
                    return;
                }
                case "SFLMSG":
                {
                    var i = argument.IndexOf('\'');
                    if (i < 0) throw No("SFLMSG without a message");
                    record.Messages.Add((when, null, null, Quoted(argument, ref i)));
                    return;
                }
                case "SFLMSGRCD":
                    record.MessageLine = int.TryParse(argument.Trim(), out var messageLine) && messageLine >= 1 ? messageLine : throw No($"SFLMSGRCD({argument})");
                    return;
                case "WDWBORDER":
                {
                    var window = record.Window ?? throw No("WDWBORDER before WINDOW");
                    foreach (var (what, value) in Groups(argument))
                    {
                        if (what == "*COLOR") window = window with { BorderColor = value.ToUpperInvariant() };
                        else if (what == "*DSPATR") window = window with { BorderAttributes = value.ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries) };
                        else if (what == "*CHAR" && value.Length >= 8) window = window with { BorderCharacters = value[..8] };
                    }
                    record.Window = window;
                    return;
                }
                case "WDWTITLE":
                {
                    var window = record.Window ?? throw No("WDWTITLE before WINDOW");
                    foreach (var (what, value) in Groups(argument))
                    {
                        if (what == "*TEXT") window = window with { Title = value };
                        else if (what == "*COLOR") window = window with { TitleColor = value.ToUpperInvariant() };
                        else if (what == "*BOTTOM") window = window with { TitleBottom = true };
                    }
                    record.Window = window;
                    return;
                }
                case "RTNCSRLOC":
                {
                    var names = Words().Where(w => w.StartsWith('&')).Select(w => w[1..].ToUpperInvariant()).ToList();
                    if (names.Count < 2) throw No($"RTNCSRLOC({argument})");
                    record.ReturnCursor = (names[0], names[1]);
                    return;
                }
                case "PUTOVR":
                    record.PutOverride = when;
                    return;
                case "ALARM":
                    record.Alarm = when;
                    return;
                case "INVITE":
                    record.Invite = true;
                    return;
                case "ASSUME":
                    record.Assume = true;
                    return;
                case "SPACEB" or "SPACEA" or "SKIPB" or "SKIPA" when Printer:
                {
                    var n = int.TryParse(argument.Trim(), out var lines) ? lines : 1;
                    if (keyword == "SPACEB") record.SpaceBefore = n;
                    else if (keyword == "SPACEA") record.SpaceAfter = n;
                    else if (keyword == "SKIPB") record.SkipBefore = n;
                    else record.SkipAfter = n;
                    return;
                }
                case "SFLDSP":
                    record.Display = when;
                    return;
                case "SFLDSPCTL":
                    record.DisplayControl = when;
                    return;
                case "SFLCLR" or "SFLDLT":
                    record.Clear = when;
                    return;
                case "SFLEND":
                    record.End = when;
                    return;
                default:
                    throw No($"the record keyword {keyword}");
            }
        }
        switch (keyword)
        {
            case "DSPATR":
                foreach (var attribute in Words().Select(w => w.ToUpperInvariant()))
                {
                    // &field: a program-to-system field holds the attribute.
                    if (attribute.StartsWith('&'))
                    {
                        field.AttributeFields.Add((when, attribute[1..]));
                        continue;
                    }
                    if (attribute is not ("HI" or "RI" or "UL" or "BL" or "ND" or "PC" or "PR" or "CS" or "MDT" or "OID" or "SP"))
                        throw No($"DSPATR({attribute})");
                    field.Attributes.Add((when, attribute));
                }
                return;
            case "EDTWRD":
            {
                var i = argument.IndexOf('\'');
                if (i < 0) throw No("EDTWRD without an edit word");
                field.EditWord = Quoted(argument, ref i);
                return;
            }
            case "CHANGE":
                field.ChangeIndicator = Indicator();
                return;
            case "MSGID" or "MSGCON":
            {
                var words = Words();
                // MSGCON(length id file): a constant; MSGID(id file) or MSGID(&field file): a field showing the message.
                if (keyword == "MSGCON" && words.Length >= 3) words = words[1..];
                if (words.Length == 0) throw No($"{keyword}({argument})");
                var file = words.Length > 1 ? words[^1].ToUpperInvariant() : null;
                field.MessageText = words[0].StartsWith('&') ? (null, words[0][1..].ToUpperInvariant(), file) : (words[0].ToUpperInvariant(), null, file);
                return;
            }
            case "ERRMSGID":
            {
                var words = Words();
                if (words.Length < 2) throw No($"ERRMSGID({argument})");
                field.ErrorIds.Add((when, words[0].ToUpperInvariant(), words[1].ToUpperInvariant()));
                return;
            }
            case "CHKMSGID":
            {
                var words = Words();
                if (words.Length < 1) throw No($"CHKMSGID({argument})");
                field.CheckMessage = (words[0].ToUpperInvariant(), words.Length > 1 ? words[1].ToUpperInvariant() : null);
                return;
            }
            case "COLOR":
                if (argument.Trim().ToUpperInvariant() is not ("GRN" or "WHT" or "RED" or "TRQ" or "YLW" or "PNK" or "BLU")) throw No($"COLOR({argument})");
                field.Colors.Add((when, argument.Trim().ToUpperInvariant()));
                return;
            case "EDTCDE":
            {
                var code = Words().FirstOrDefault() ?? "";
                if (code.Length != 1 || !"1234ABCDJKLMNOPQXYZ".Contains(char.ToUpperInvariant(code[0]))) throw No($"EDTCDE({argument})");
                field.EditCode = char.ToUpperInvariant(code[0]);
                // EDTCDE(J *): asterisks in place of leading blanks; EDTCDE(J $): the currency symbol floats before the number.
                if (Words() is [_, var extra, ..])
                {
                    if (extra == "*") field.EditFill = '*';
                    else if (extra.Trim('\'').Length == 1) field.EditCurrency = extra.Trim('\'')[0];
                    else throw No($"EDTCDE({argument})");
                }
                return;
            }
            case "CHECK" or "CHK":
                foreach (var code in Words().Select(w => w.ToUpperInvariant()))
                {
                    switch (code)
                    {
                        case "LC": field.LowerCase = true; break;
                        case "ME": field.MandatoryEnter = true; break;
                        case "MF": field.MandatoryFill = true; break;
                        case "VN" or "VNE": field.ValidName = true; break;
                        case "RB": field.RightAdjust = ' '; break;
                        case "RZ": field.RightAdjust = '0'; break;
                        // Field exit, auto-enter, allow blanks, modulus checks: what the emulator does, or taken.
                        case "FE" or "ER" or "AB" or "M10" or "M11" or "M10F" or "M11F" or "RL": break;
                        default: throw No($"CHECK({code})");
                    }
                }
                return;
            case "LOWER":
                field.LowerCase = true;
                return;
            case "ERRMSG":
            {
                var i = argument.IndexOf('\'');
                if (i < 0) throw No("ERRMSG without a message");
                field.Errors.Add((when, Quoted(argument, ref i)));
                return;
            }
            case "DFT":
            {
                var i = argument.IndexOf('\'');
                if (i < 0) throw No("DFT without a value");
                // On a constant, its text; on an input-only field, what it shows before anything's typed.
                if (field.Name is null) field.Text = Quoted(argument, ref i);
                else field.Default = Quoted(argument, ref i);
                return;
            }
            case "DATE" or "TIME" or "USER" or "SYSNAME" or "PAGNBR":
                if (field.Name is not null) throw No($"{keyword} on a named field");
                field.System = keyword;
                if (Words().Any(w => w.Equals("*YY", StringComparison.OrdinalIgnoreCase))) field.FourDigitYear = true;
                return;
            case "SPACEB" or "SPACEA" or "SKIPB" or "SKIPA" when Printer:
            {
                var n = int.TryParse(argument.Trim(), out var lines) ? lines : 1;
                if (keyword == "SPACEB") field.SpaceBefore = n;
                else if (keyword == "SPACEA") field.SpaceAfter = n;
                else if (keyword == "SKIPB") field.SkipBefore = n;
                else field.SkipAfter = n;
                return;
            }
            case "SFLRCDNBR":
                field.RecordNumber = true;
                return;
            case "VALUES":
                field.AllowedValues.AddRange(Values(argument));
                return;
            case "RANGE":
                if (Values(argument) is not [var low, var high]) throw No($"RANGE({argument})");
                field.Range = (low, high);
                return;
            case "COMP" or "CMP":
            {
                var parts = argument.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || parts[0].ToUpperInvariant() is not ("EQ" or "NE" or "LT" or "NL" or "GT" or "NG" or "LE" or "GE")) throw No($"{keyword}({argument})");
                field.Compare = (parts[0].ToUpperInvariant(), Values(parts[1]).FirstOrDefault() ?? "");
                return;
            }
            default:
                throw No($"the field keyword {keyword}");
        }
    }

    /// <summary>The number typed into a numeric field: without a decimal point, the last digits are the decimals, as on a real display.</summary>
    public static decimal ParseInput(string text, DisplayField field)
    {
        var negative = text.Contains('-') || text.TrimEnd().EndsWith("CR", StringComparison.OrdinalIgnoreCase);
        var digits = new string(text.Where(c => char.IsDigit(c) || c == '.').ToArray());
        if (digits.Length == 0 || !decimal.TryParse(digits, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) return 0;
        if (!digits.Contains('.')) value /= Rpg.Values.Pow10(field.Decimals);
        value = decimal.Round(value, field.Decimals, MidpointRounding.ToZero) % Rpg.Values.Pow10(field.Length - field.Decimals);
        return negative ? -value : value;
    }
}
