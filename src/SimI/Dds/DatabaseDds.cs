// DDS for database files: a physical file (PF) is a record format's fields and its key (K lines);
// a logical file (LF) is a view of a physical file (PFILE), with its own fields, key and select/omit
// rules (S and O lines). CRTPF makes a table of the physical file; CRTLF a view of the logical file,
// and RPG reads both by their key.

using System.Globalization;
using System.Text;
using SimI.Rpg;

namespace SimI.Dds;

/// <summary>A field of a database file: its name, its RPG type, and its type as the table has it.</summary>
internal sealed record DbField(string Name, RpgType Type, string SqlType, string? Column = null, string? Expression = null)
{
    /// <summary>
    /// The field in SQL: an expression for a logical file's derived field (SST, CONCAT), else its column,
    /// quoted: its own (a long SQL name) or the field's name.
    /// </summary>
    public string SqlColumn => Expression ?? Store.ColumnName(Column ?? Name);
}

/// <summary>A logical file's derived field: SST(field start length) or CONCAT(field field ...), and its own length if given.</summary>
internal sealed record DerivedField(string Keyword, string[] Arguments, int? Length);

/// <summary>A select/omit rule: S or O, and its conditions on fields (all of which have to hold).</summary>
internal sealed record SelectOmit(bool Select, List<(string Field, string Condition)> Conditions, bool All = false);

internal sealed class DatabaseDds
{
    public string Name { get; init; } = "";
    public bool Logical { get; init; }
    public string Format { get; set; } = "";

    /// <summary>The fields, in order; for a logical file, those it names (none: all of its physical file's).</summary>
    public List<DbField> Fields { get; } = [];

    /// <summary>For a logical file, its fields' names (their types are its physical file's).</summary>
    public List<string> FieldNames { get; } = [];

    public List<(string Field, bool Descending)> Keys { get; } = [];
    public bool Unique { get; set; }

    /// <summary>A logical file's physical file (PFILE).</summary>
    public string? BasedOn { get; set; }

    public List<SelectOmit> Rules { get; } = [];

    /// <summary>A logical file's fields that are its physical file's under another name (RENAME), by their own names.</summary>
    public Dictionary<string, string> Renames { get; } = [];

    /// <summary>A logical file's derived fields (SST, CONCAT), by name.</summary>
    public Dictionary<string, DerivedField> Derived { get; } = [];

    /// <summary>A logical file's record formats after its first, each with its own physical file, fields and key.</summary>
    public List<DatabaseDds> Others { get; } = [];

    /// <summary>A join logical file (JFILE), which Sim i doesn't make.</summary>
    public bool Join { get; set; }
    public List<Diagnostic> Diagnostics { get; } = [];

    private static string Column(string line, int from, int to) =>
        line.Length < from ? "" : line.Substring(from - 1, Math.Min(to, line.Length) - from + 1);

    /// <summary>
    /// Finds a field another file defines, for a reference field (R in column 29): the file (as
    /// written, LIB/FILE or FILE) and the field's name. Null when the file or the field isn't there.
    /// </summary>
    public delegate DbField? FieldReference(string file, string field);

    /// <summary>The prefix of the error a reference to a file that isn't there gives, so a build can make that file first.</summary>
    public const string MissingReference = "the reference file ";

    public static DatabaseDds Parse(string name, string source, bool logical, FieldReference? reference = null)
    {
        var primary = new DatabaseDds { Name = name.ToUpperInvariant(), Logical = logical };
        // A logical file can have more than one record format: each after the first is one of Others.
        var dds = primary;
        string? referenceFile = null;
        // A field waits for its keyword lines (REFFLD, VARLEN can be on the next lines).
        (string Name, string Length, char Type, string Decimals, bool Referenced, StringBuilder Words, int Line)? pending = null;
        void Finish()
        {
            if (pending is not { } f) return;
            pending = null;
            AddField(dds, f.Name, f.Length, f.Type, f.Decimals, f.Referenced, f.Words.ToString(), f.Line, referenceFile, reference, logical,
                (line, message, severity) => primary.Diagnostics.Add(new Diagnostic(line, severity, message)));
        }
        var lines = source.Replace("\r\n", "\n").Split('\n');
        SelectOmit? rule = null;
        var keywords = new StringBuilder();
        void Problem(int line, string message, Severity severity = Severity.Error) => primary.Diagnostics.Add(new Diagnostic(line, severity, message));
        for (var n = 0; n < lines.Length; n++)
        {
            var line = lines[n].TrimEnd();
            if (line.Length < 7 || line.Length >= 7 && line[6] == '*') continue;
            // Column 6 is the form type: A, or blank (the compiler takes either).
            if (char.ToUpperInvariant(line[5]) is not ('A' or ' '))
            {
                Problem(n + 1, "this isn't a DDS line: column 6 has to be A or blank");
                continue;
            }
            var kind = char.ToUpperInvariant(Column(line, 17, 17).FirstOrDefault(' '));
            var fieldName = Column(line, 19, 28).Trim().ToUpperInvariant();
            var referenced = char.ToUpperInvariant(Column(line, 29, 29).FirstOrDefault(' ')) == 'R';
            if (pending is not null && kind == ' ' && fieldName.Length == 0)
            {
                pending.Value.Words.Append(' ').Append(Column(line, 45, 80).Trim());
                continue;
            }
            Finish();
            var lengthText = Column(line, 30, 34).Trim();
            var type = char.ToUpperInvariant(Column(line, 35, 35).FirstOrDefault(' '));
            var decimalsText = Column(line, 36, 37).Trim();
            var words = Column(line, 45, 80).Trim();
            switch (kind)
            {
                case 'R':
                {
                    if (dds.Format.Length > 0)
                    {
                        if (!logical)
                        {
                            Problem(n + 1, "a physical file has one record format");
                            continue;
                        }
                        dds = new DatabaseDds { Name = primary.Name, Logical = true };
                        primary.Others.Add(dds);
                    }
                    dds.Format = fieldName;
                    rule = null;
                    // PFILE( and JFILE( can go on over the lines after, each ending in +.
                    // The record's keywords go on over the keyword lines after it (a line ending in + continues a keyword).
                    var record = new StringBuilder(words);
                    while (n + 1 < lines.Length && lines[n + 1].TrimEnd() is var next && next.Length > 44
                           && next[6] != '*' && Column(next, 7, 44).Trim().Length == 0)
                    {
                        if (record.ToString().TrimEnd().EndsWith('+')) record.Length = record.ToString().TrimEnd().Length - 1;
                        record.Append(' ').Append(Column(next, 45, 80).Trim());
                        n++;
                    }
                    var all = record.ToString();
                    if (Keyword(all, "PFILE") is { } pfile) dds.BasedOn = pfile.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].ToUpperInvariant().Split('/')[^1];
                    if (Keyword(all, "JFILE") is not null)
                    {
                        dds.Join = true;
                        Problem(n + 1, "join logical files (JFILE) aren't supported", Severity.Unsupported);
                    }
                    continue;
                }
                case 'K':
                    dds.Keys.Add((fieldName, words.Contains("DESCEND", StringComparison.OrdinalIgnoreCase)));
                    continue;
                case 'S' or 'O':
                    rule = new SelectOmit(kind == 'S', [], words.Equals("ALL", StringComparison.OrdinalIgnoreCase));
                    dds.Rules.Add(rule);
                    if (fieldName.Length > 0) AddCondition(rule, fieldName, words, n + 1, Problem);
                    continue;
                case ' ' when fieldName.Length > 0 && rule is not null:
                    // A named line after a select/omit line is another condition of the same rule: both have to hold.
                    AddCondition(rule, fieldName, words, n + 1, Problem);
                    continue;
                case ' ' when fieldName.Length > 0:
                {
                    if (dds.Format.Length == 0)
                    {
                        Problem(n + 1, "a field comes before the record format (R)");
                        continue;
                    }
                    pending = (fieldName, lengthText, type, decimalsText, referenced, new StringBuilder(words), n + 1);
                    continue;
                }
                default:
                    // A line of keywords: the file's (before the record), or a continuation.
                    if (rule is not null && fieldName.Length == 0 && words.Length > 0 && rule.Conditions.Count > 0)
                    {
                        AddCondition(rule, rule.Conditions[^1].Field, words, n + 1, Problem);
                    }
                    if (dds.Format.Length == 0 && words.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)) dds.Unique = true;
                    if (dds.Format.Length == 0 && Keyword(words, "REF") is { } file) referenceFile = file.Trim();
                    keywords.Append(' ').Append(words);
                    continue;
            }
        }
        Finish();
        if (primary.Format.Length == 0) Problem(0, "the file has no record format (R)");
        foreach (var format in primary.Others.Prepend(primary))
        {
            if (logical && format.BasedOn is null && !format.Join && format.Format.Length > 0) Problem(0, $"a logical file's record format needs PFILE ({format.Format})");
        }
        return primary;
    }

    /// <summary>
    /// A field line, with its keyword lines: its own length, type and decimals, or (R in column 29)
    /// those of the field it refers to (REFFLD, or the same name in the REF file), which its own
    /// length and decimals replace or, with + or -, change.
    /// </summary>
    private static void AddField(DatabaseDds dds, string fieldName, string lengthText, char type, string decimalsText, bool referenced, string words, int line,
        string? referenceFile, FieldReference? reference, bool logical, Action<int, string, Severity> problem)
    {
        // A logical file's field with no length, type or decimals of its own is its physical file's (R or not).
        // RENAME(field): the logical file's field is the physical file's field of that name.
        if (logical && Keyword(words, "RENAME") is { } renamed) dds.Renames[fieldName] = renamed.Trim().ToUpperInvariant();
        // SST and CONCAT: a field made of the physical file's, for input only.
        if (logical && (Keyword(words, "SST") is { } sst ? ("SST", sst) : Keyword(words, "CONCAT") is { } concat ? ("CONCAT", concat) : default) is (string kind, string value))
        {
            dds.Derived[fieldName] = new DerivedField(kind, value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(a => a.ToUpperInvariant()).ToArray(),
                int.TryParse(lengthText, out var own) ? own : null);
            dds.FieldNames.Add(fieldName);
            return;
        }
        if (logical && lengthText.Length == 0 && decimalsText.Length == 0 && type == ' ' && Keyword(words, "REFFLD") is null)
        {
            dds.FieldNames.Add(fieldName);
            return;
        }
        int length;
        var decimals = 0;
        var varying = words.Contains("VARLEN", StringComparison.OrdinalIgnoreCase);
        if (referenced)
        {
            // REFFLD(field), REFFLD(format/field), REFFLD(field file) or REFFLD(field *SRC).
            var target = fieldName;
            var file = referenceFile;
            if (Keyword(words, "REFFLD") is { } refFld)
            {
                var parts = refFld.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0) target = parts[0].Split('/')[^1].ToUpperInvariant();
                if (parts.Length > 1) file = parts[1].Equals("*SRC", StringComparison.OrdinalIgnoreCase) ? null : parts[1];
                else if (referenceFile is null) file = null;
            }
            DbField? found;
            if (file is null)
            {
                found = dds.Fields.FirstOrDefault(f => f.Name == target);
                if (found is null)
                {
                    problem(line, $"field {target} that {fieldName} refers to isn't defined before it, and there's no REF file", Severity.Error);
                    return;
                }
            }
            else
            {
                if (reference is null)
                {
                    // Nothing to look in (a check of the source alone): the field is there, its type unknown.
                    if (logical) dds.FieldNames.Add(fieldName);
                    return;
                }
                found = reference(file, target);
                if (found is null)
                {
                    problem(line, $"{MissingReference}{file.ToUpperInvariant()} or its field {target} isn't found", Severity.Error);
                    return;
                }
            }
            var (baseType, baseLength, baseDecimals) = Attributes(found);
            length = Adjust(lengthText, baseLength);
            decimals = Adjust(decimalsText, baseDecimals);
            if (type == ' ') type = decimalsText.Length > 0 && baseType == 'A' ? 'P' : baseType;
            varying |= found.Type.Kind == RpgKind.Varchar;
        }
        else
        {
            if (!int.TryParse(lengthText, out length) && type is not ('L' or 'T' or 'Z' or 'F'))
            {
                problem(line, $"field {fieldName} needs a length", Severity.Error);
                return;
            }
            decimals = int.TryParse(decimalsText, out var d) ? d : 0;
            if (type == ' ') type = decimalsText.Length == 0 ? 'A' : 'P';
        }
        var field = Field(fieldName, type, length, decimals, varying);
        if (field is null) problem(line, $"the data type {type} isn't supported", Severity.Unsupported);
        else dds.Fields.Add(field);
        if (logical) dds.FieldNames.Add(fieldName);

        static int Adjust(string text, int base_) =>
            text.Length == 0 ? base_
            : text[0] is '+' or '-' && int.TryParse(text[1..], out var by) ? base_ + (text[0] == '+' ? by : -by)
            : int.TryParse(text, out var value) ? value : base_;
    }

    /// <summary>A field's DDS data type, length and decimals, from the field it was made of.</summary>
    private static (char Type, int Length, int Decimals) Attributes(DbField field)
    {
        var t = field.Type;
        return t.Kind switch
        {
            RpgKind.Char => ('A', t.Length, 0),
            RpgKind.Varchar => ('A', t.Length, 0),
            RpgKind.Int => ('B', t.Length switch { 5 => 4, 10 => 9, _ => 18 }, 0),
            RpgKind.Float => ('F', 8, 0),
            RpgKind.Date => ('L', 0, 0),
            RpgKind.Time => ('T', 0, 0),
            RpgKind.Timestamp => ('Z', 0, 0),
            _ => (t.Form is 'S' or 'B' ? t.Form : 'P', t.Length, t.Decimals),
        };
    }

    /// <summary>The files a DDS source's reference fields name (REF, and REFFLD's file), without their libraries.</summary>
    public static IEnumerable<string> ReferencedFiles(string source)
    {
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(source,
            @"\b(?:REF\(\s*([^\s)]+)|REFFLD\(\s*[^\s)]+\s+([^\s)]+))", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            var file = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Split('/')[^1].ToUpperInvariant();
            if (file != "*SRC") yield return file;
        }
    }

    /// <summary>Where a select/omit condition's field goes, until <see cref="Where"/> knows its column.</summary>
    private const string FieldMark = "\u0001";

    /// <summary>A keyword's value: PFILE(CUSTPF) gives CUSTPF; null when it isn't there.</summary>
    private static string? Keyword(string words, string keyword)
    {
        var at = words.IndexOf(keyword + "(", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        var end = words.IndexOf(')', at);
        return end < 0 ? null : words[(at + keyword.Length + 1)..end];
    }

    private static void AddCondition(SelectOmit rule, string field, string words, int line, Action<int, string, Severity> problem)
    {
        // CMP is COMP's older name.
        if ((Keyword(words, "COMP") ?? Keyword(words, "CMP")) is { } comp)
        {
            var parts = comp.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var op = parts[0].ToUpperInvariant() switch { "EQ" => "=", "NE" => "<>", "LT" => "<", "NL" => ">=", "GT" => ">", "NG" => "<=", "LE" => "<=", "GE" => ">=", _ => null };
            if (op is null || parts.Length < 2) problem(line, $"COMP({comp}) isn't a comparison", Severity.Error);
            else rule.Conditions.Add((field, $"{FieldMark} {op} {Literal(parts[1])}"));
        }
        else if (Keyword(words, "VALUES") is { } values)
        {
            rule.Conditions.Add((field, $"{FieldMark} IN ({string.Join(", ", Values(values).Select(Literal))})"));
        }
        else if (Keyword(words, "RANGE") is { } range && Values(range) is [var low, var high])
        {
            rule.Conditions.Add((field, $"{FieldMark} BETWEEN {Literal(low)} AND {Literal(high)}"));
        }
        else if (words.Length > 0)
        {
            problem(line, $"the select/omit keyword in {words} isn't supported", Severity.Unsupported);
        }
    }

    private static List<string> Values(string text)
    {
        var values = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }
            var start = i;
            if (text[i] == '\'')
            {
                i++;
                while (i < text.Length && !(text[i] == '\'' && (i + 1 >= text.Length || text[i + 1] != '\''))) i += text[i] == '\'' ? 2 : 1;
                i++;
            }
            else while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
            values.Add(text[start..Math.Min(i, text.Length)]);
        }
        return values;
    }

    /// <summary>A DDS value as SQL: a quoted string as it is, a number as a number.</summary>
    private static string Literal(string value) =>
        value.StartsWith('\'') ? value : decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _) ? value : $"'{value.Replace("'", "''")}'";

    /// <summary>A field's RPG and SQL types from its DDS data type, length and decimals.</summary>
    public static DbField? Field(string name, char type, int length, int decimals, bool varying = false) => type switch
    {
        'A' => varying ? new DbField(name, new RpgType(RpgKind.Varchar, length), $"VARCHAR({length})") : new DbField(name, RpgType.Char(length), $"CHAR({length})"),
        'P' => new DbField(name, RpgType.Number(length, decimals), $"DECIMAL({length}, {decimals})"),
        'S' => new DbField(name, RpgType.Number(length, decimals, 'S'), $"NUMERIC({length}, {decimals})"),
        'B' when decimals == 0 => new DbField(name, new RpgType(RpgKind.Int, length <= 4 ? 5 : length <= 9 ? 10 : 20), length <= 4 ? "SMALLINT" : length <= 9 ? "INTEGER" : "BIGINT"),
        'B' => new DbField(name, RpgType.Number(length, decimals, 'B'), $"DECIMAL({length}, {decimals})"),
        'F' => new DbField(name, new RpgType(RpgKind.Float, 8), "DOUBLE"),
        'L' => new DbField(name, new RpgType(RpgKind.Date), "DATE"),
        'T' => new DbField(name, new RpgType(RpgKind.Time), "TIME"),
        'Z' => new DbField(name, new RpgType(RpgKind.Timestamp), "TIMESTAMP"),
        _ => null,
    };

    /// <summary>The select/omit rules as an SQL condition: the first rule a row meets says whether it's in; one it meets none of is in when the last rule omits.</summary>
    public string? Where(Func<string, string>? column = null)
    {
        if (Rules.Count == 0) return null;
        column ??= Store.ColumnName;
        var cases = new StringBuilder("CASE");
        foreach (var rule in Rules)
        {
            var condition = rule.All || rule.Conditions.Count == 0 ? "1 = 1"
                : string.Join(" AND ", rule.Conditions.Select(c => $"({c.Condition.Replace(FieldMark, column(c.Field))})"));
            cases.Append($" WHEN {condition} THEN {(rule.Select ? 1 : 0)}");
        }
        cases.Append($" ELSE {(Rules[^1].Select ? 0 : 1)} END = 1");
        return cases.ToString();
    }
}
