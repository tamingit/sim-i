// OPNQRYF: an open query over database files, which the job's next open of the file (shared, as
// OVRDBF SHARE(*YES) has it) reads. Its selection (QRYSLT), mapped fields (MAPFLD), key (KEYFLD),
// join (JFLD, JDFTVAL) and grouping (GRPFLD, GRPSLT) become an SQL view in QTEMP, whose records
// come in the key's order. CPYFRMQRYF copies them to a file, CLOF closes the query.

using System.Text;
using FakeIbmI.Dds;
using FakeIbmI.Rpg;

namespace FakeIbmI;

public sealed partial class Machine
{
    /// <summary>The query a program opening this file gets (OPNQRYF), or null.</summary>
    internal static DatabaseFile? QueryFile(string name, JobState state) =>
        state.QueryFiles.TryGetValue(Qualified(name).Name, out var query) ? query : null;

    /// <summary>A database file a program opens: the job's open query of it, or the file OVRDBF names, or the file.</summary>
    internal DatabaseFile? OpenedFile(string name, JobState state, string? first)
    {
        var overridden = Overridden(name, state);
        return QueryFile(overridden, state) ?? QueryFile(name, state) ?? FindDatabaseFile(overridden, first);
    }

    private CommandResult OpenQuery(ClCommand command, IJobHost host)
    {
        CommandResult Fail(string message, string id) => new(false, message, MessageId: id);
        var first = DefaultLibrary(host.User);
        // FILE((LIB/FILE MBR FORMAT) (...)): the files, in join order.
        var files = new List<DatabaseFile>();
        foreach (var group in Groups(command.Value("FILE", 0) ?? ""))
        {
            var name = group.FirstOrDefault() ?? "";
            if (FindDatabaseFile(Overridden(name, host.State), first) is not { } file) return Fail($"File {name.ToUpperInvariant()} not found.", "CPF4101");
            files.Add(file);
        }
        if (files.Count == 0) return Fail("Parameter FILE required.", "CPD0072");
        var openId = command.Value("OPNID", -1)?.Trim().ToUpperInvariant() is { Length: > 0 } id && id != "*FILE" ? id : files[0].Name;
        var formatName = command.Value("FORMAT", -1)?.Trim() is { Length: > 0 } f && !f.StartsWith("*FILE", StringComparison.OrdinalIgnoreCase) ? Groups(f).First()[0] : null;
        var format = formatName is null ? files[0] : FindDatabaseFile(formatName, first);
        if (format is null) return Fail($"Format file {formatName!.ToUpperInvariant()} not found.", "CPF4101");

        try
        {
            var query = new QueryTranslator(files);
            // MAPFLD((NAME 'expression' [type]) ...): fields made of others, in order.
            foreach (var mapped in Groups(command.Value("MAPFLD", -1) ?? ""))
            {
                if (mapped.Count < 2) continue;
                query.Map(mapped[0].ToUpperInvariant(), Unquote(mapped[1]));
            }
            var columns = new List<string>();
            foreach (var field in format.Fields)
            {
                var expression = query.FieldExpression(field.Name) ?? throw new FormatException($"field {field.Name} of the format isn't a field of the files or a mapped field");
                columns.Add($"CAST({expression} AS {field.SqlType}) AS {Store.ColumnName(field.Name)}");
            }
            var from = new StringBuilder($"{files[0].Table} AS f1");
            var outer = command.Value("JDFTVAL", -1)?.Trim().ToUpperInvariant() is "*YES" or "*ONLYDFT";
            var joins = Groups(command.Value("JFLD", -1) ?? "").ToList();
            for (var i = 1; i < files.Count; i++)
            {
                // A join field pair (F1/A F2/B [op]) belongs to the last file it names.
                var conditions = joins.Where(j => j.Count >= 2 && query.FileIndex(j[0], j[1]) == i)
                    .Select(j => $"{query.Operand(j[0])} {QueryTranslator.Comparison(j.Count > 2 ? j[2] : "*EQ")} {query.Operand(j[1])}").ToList();
                from.Append(outer ? " LEFT JOIN " : " JOIN ").Append($"{files[i].Table} AS f{i + 1}")
                    .Append(" ON ").Append(conditions.Count > 0 ? string.Join(" AND ", conditions) : "TRUE");
            }
            var where = new List<string>();
            for (var i = 0; i < files.Count; i++)
            {
                if (files[i].Where is { } selected) where.Add($"({query.Qualify(selected, i)})");
            }
            if (command.Value("QRYSLT", -1) is { } slt && Unquote(slt).Trim() is { Length: > 0 } selection && selection != "*ALL")
                where.Add($"({query.Condition(selection)})");
            var groups = Groups(command.Value("GRPFLD", -1) ?? "").Select(g => query.Operand(g[0])).ToList();
            var having = command.Value("GRPSLT", -1) is { } grp && Unquote(grp).Trim() is { Length: > 0 } groupSelection && groupSelection != "*ALL"
                ? query.Condition(groupSelection) : null;
            // KEYFLD((FIELD [*DESCEND]) ...), or *FILE: the first file's key.
            var keyText = command.Value("KEYFLD", -1)?.Trim() ?? "*NONE";
            var keys = keyText.StartsWith("*FILE", StringComparison.OrdinalIgnoreCase)
                ? files[0].Keys.ToList()
                : keyText.StartsWith('*') ? [] : Groups(keyText).Select(k => (Field: k[0].Split('/')[^1].ToUpperInvariant(), Descending: k.Skip(1).Any(w => w.Equals("*DESCEND", StringComparison.OrdinalIgnoreCase)))).ToList();
            var order = keys.Select(k => (format.Fields.Any(f => f.Name == k.Field) ? Store.ColumnName(k.Field) : query.Operand(k.Field)) + (k.Descending ? " DESC" : "")).ToList();
            var select = $"SELECT {string.Join(", ", columns)} FROM {from}"
                + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
                + (groups.Count > 0 ? " GROUP BY " + string.Join(", ", groups) : "")
                + (having is not null ? " HAVING " + having : "");
            var view = $"pg_temp.\"qfake_q_{openId.ToLowerInvariant()}\"";
            var ordered = order.Count > 0 ? string.Join(", ", order) : "1";
            var sql = $"CREATE OR REPLACE TEMPORARY VIEW \"qfake_q_{openId.ToLowerInvariant()}\" AS SELECT q.*, row_number() OVER (ORDER BY {ordered}) AS {Store.RowNumber} FROM ({select}) AS q";
            if (Sql($"DROP VIEW IF EXISTS {view}") is { } dropped) return Fail($"OPNQRYF of {files[0].Name} failed: {dropped}", "CPF9899");
            if (Sql(sql) is { } failed) return Fail($"OPNQRYF of {files[0].Name} failed: {failed}", "CPF9899");
            var opened = new DatabaseFile("QTEMP", openId, format.Format, view,
                [.. format.Fields.Select(fld => fld with { Column = fld.Name, Expression = null })], keys, false, null, Store.RowNumber);
            host.State.QueryFiles[openId] = opened;
            return new CommandResult(true, "");
        }
        catch (FormatException e)
        {
            return Fail($"OPNQRYF of {files[0].Name}: {e.Message}.", "CPF9899");
        }
    }

    /// <summary>CLOF: closes an open query (or a file a CL program opened); CPF4520 when none is open by that id.</summary>
    private CommandResult CloseFile(ClCommand command, IJobHost host)
    {
        var id = (command.Value("OPNID", 0) ?? "").Trim().ToUpperInvariant();
        if (!host.State.QueryFiles.Remove(id, out var query)) return new CommandResult(false, $"No file open with identifier {id}.", MessageId: "CPF4520");
        Sql($"DROP VIEW IF EXISTS {query.Table}");
        return new CommandResult(true, "");
    }

    /// <summary>
    /// The groups of a list parameter's value: (A B) (C) as [A, B], [C]; a value with no parentheses
    /// is one group. Quoted strings are kept whole.
    /// </summary>
    private static List<List<string>> Groups(string text)
    {
        var groups = new List<List<string>>();
        var t = text.Trim();
        if (t.Length == 0) return groups;
        if (!t.StartsWith('(')) return [Words(t)];
        var i = 0;
        while (i < t.Length)
        {
            if (t[i] != '(')
            {
                i++;
                continue;
            }
            var start = ++i;
            var depth = 1;
            while (i < t.Length && depth > 0)
            {
                if (t[i] == '\'')
                {
                    i++;
                    while (i < t.Length && !(t[i] == '\'' && (i + 1 >= t.Length || t[i + 1] != '\''))) i += t[i] == '\'' ? 2 : 1;
                }
                else if (t[i] == '(') depth++;
                else if (t[i] == ')') depth--;
                i++;
            }
            var inner = t[start..Math.Max(start, i - 1)].Trim();
            // ((A B) (C)): the groups are one level in.
            if (inner.StartsWith('(')) groups.AddRange(Groups(inner));
            else groups.Add(Words(inner));
        }
        return groups;
    }

    /// <summary>Words, with quoted strings kept whole (and quoted).</summary>
    private static List<string> Words(string text)
    {
        var words = new List<string>();
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
            else
            {
                var depth = 0;
                while (i < text.Length && (depth > 0 || !char.IsWhiteSpace(text[i])))
                {
                    if (text[i] == '(') depth++;
                    if (text[i] == ')') depth--;
                    i++;
                }
            }
            words.Add(text[start..Math.Min(i, text.Length)]);
        }
        return words;
    }

    private static string Unquote(string text)
    {
        var t = text.Trim();
        return t.Length >= 2 && t[0] == '\'' && t[^1] == '\'' ? t[1..^1].Replace("''", "'") : t;
    }
}

/// <summary>
/// OPNQRYF's expressions (QRYSLT, MAPFLD, GRPSLT) as SQL over the query's files (f1, f2 ...): fields
/// by name or FILE/FIELD (or 1/FIELD), "..." and '...' strings, *EQ and the rest, *AND, *OR, *NOT,
/// *CAT, %RANGE, %VALUES, %WLDCRD, *CT, %SST, %DIGITS, %SUM and the other functions.
/// </summary>
internal sealed class QueryTranslator(List<DatabaseFile> files)
{
    private readonly Dictionary<string, string> _mapped = new(StringComparer.OrdinalIgnoreCase);

    public void Map(string name, string expression)
    {
        _mapped[name] = $"({Expression(expression)})";
    }

    /// <summary>A format field: mapped, or a field of the files of that name; null when it's neither.</summary>
    public string? FieldExpression(string name)
    {
        if (_mapped.TryGetValue(name, out var mapped)) return mapped;
        for (var i = 0; i < files.Count; i++)
        {
            if (files[i].Fields.FirstOrDefault(f => f.Name == name) is { } field) return Column(field, i);
        }
        return null;
    }

    private static string Column(DbField field, int file) =>
        field.Expression is { } expression ? Qualify(expression, file, field) : $"f{file + 1}.{field.SqlColumn}";

    private static string Qualify(string expression, int file, DbField _) => expression.Replace("\"", $"f{file + 1}.\"");

    /// <summary>A file's select/omit condition (columns quoted), on that file of the query.</summary>
    public string Qualify(string condition, int file) =>
        System.Text.RegularExpressions.Regex.Replace(condition, "(\"[^\"]+\")", $"f{file + 1}.$1");

    /// <summary>Which file of the query a join pair joins: the later of the two it names.</summary>
    public int FileIndex(string a, string b) => Math.Max(FileOf(a), FileOf(b));

    private int FileOf(string operand)
    {
        var parts = operand.Split('/');
        if (parts.Length == 2)
        {
            if (int.TryParse(parts[0], out var n)) return n - 1;
            var index = files.FindIndex(f => f.Name.Equals(parts[0], StringComparison.OrdinalIgnoreCase));
            if (index >= 0) return index;
        }
        var name = parts[^1].ToUpperInvariant();
        var found = files.FindIndex(f => f.Fields.Any(fl => fl.Name == name));
        return Math.Max(found, 0);
    }

    /// <summary>A field (FIELD, FILE/FIELD or n/FIELD) as SQL.</summary>
    public string Operand(string operand)
    {
        var parts = operand.Trim().Split('/');
        var name = parts[^1].ToUpperInvariant();
        if (parts.Length == 1 && _mapped.TryGetValue(name, out var mapped)) return mapped;
        var file = parts.Length == 2 ? FileOf(operand) : files.FindIndex(f => f.Fields.Any(fl => fl.Name == name));
        if (file < 0 || files[file].Fields.FirstOrDefault(f => f.Name == name) is not { } field) throw new FormatException($"{operand.Trim().ToUpperInvariant()} isn't a field of the files");
        return Column(field, file);
    }

    private DbField? FieldOf(string operand)
    {
        var parts = operand.Trim().Split('/');
        var name = parts[^1].ToUpperInvariant();
        var file = parts.Length == 2 ? FileOf(operand) : files.FindIndex(f => f.Fields.Any(fl => fl.Name == name));
        return file < 0 ? null : files[file].Fields.FirstOrDefault(f => f.Name == name);
    }

    public static string Comparison(string op) => op.Trim().ToUpperInvariant() switch
    {
        "*EQ" or "=" => "=",
        "*NE" or "<>" or "¬=" => "<>",
        "*GT" or ">" => ">",
        "*LT" or "<" => "<",
        "*GE" or ">=" or "*NL" or "¬<" => ">=",
        "*LE" or "<=" or "*NG" or "¬>" => "<=",
        var other => throw new FormatException($"{other} isn't a comparison"),
    };

    // ---- the expression ----

    private List<string> _tokens = [];
    private int _p;

    public string Condition(string text) => Expression(text);

    private string Expression(string text)
    {
        var (tokens, p) = (_tokens, _p);
        (_tokens, _p) = (Tokens(text), 0);
        try
        {
            var sql = Or();
            if (_p < _tokens.Count) throw new FormatException($"{_tokens[_p]} isn't expected in {text.Trim()}");
            return sql;
        }
        finally
        {
            (_tokens, _p) = (tokens, p);
        }
    }

    private static List<string> Tokens(string text)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            var start = i;
            if (c is '"' or '\'')
            {
                i++;
                while (i < text.Length && !(text[i] == c && (i + 1 >= text.Length || text[i + 1] != c))) i += text[i] == c ? 2 : 1;
                i++;
                tokens.Add("'" + text[(start + 1)..Math.Max(start + 1, Math.Min(i - 1, text.Length))].Replace(c.ToString() + c, c.ToString()).Replace("'", "''") + "'");
                continue;
            }
            if (char.IsDigit(c) || c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1]))
            {
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.')) i++;
                tokens.Add(text[start..i]);
                continue;
            }
            if (c is '*' or '%' && i + 1 < text.Length && char.IsLetter(text[i + 1]) || char.IsLetter(c) || c is '#' or '@' or '$')
            {
                i++;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '#' or '@' or '$' or '/')) i++;
                tokens.Add(text[start..i].ToUpperInvariant());
                continue;
            }
            var two = i + 1 < text.Length ? text.Substring(i, 2) : "";
            if (two is "<>" or ">=" or "<=" or "¬=" or "¬<" or "¬>" or "||")
            {
                tokens.Add(two);
                i += 2;
                continue;
            }
            tokens.Add(c.ToString());
            i++;
        }
        return tokens;
    }

    private string? Peek => _p < _tokens.Count ? _tokens[_p] : null;

    private bool Take(params string[] what)
    {
        if (Peek is { } t && what.Contains(t))
        {
            _p++;
            return true;
        }
        return false;
    }

    private string Or()
    {
        var left = And();
        while (Take("*OR", "|")) left = $"{left} OR {And()}";
        return left;
    }

    private string And()
    {
        var left = Not();
        while (Take("*AND", "&")) left = $"{left} AND {Not()}";
        return left;
    }

    private string Not() => Take("*NOT", "¬") ? $"NOT ({Not()})" : Relation();

    private string Relation()
    {
        var at = _p;
        var left = Concatenation();
        var op = Peek;
        if (op is "*CT")
        {
            _p++;
            return $"{left} LIKE '%' || {Concatenation()} || '%'";
        }
        if (op is null || !IsComparison(op)) return left;
        _p++;
        var sqlOp = Comparison(op);
        // field *EQ %RANGE(low high), %VALUES(a b c), %WLDCRD('pattern' 'wildcards').
        if (Peek is "%RANGE" or "%VALUES" or "%WLDCRD")
        {
            var function = _tokens[_p++];
            var arguments = Arguments();
            var negated = sqlOp == "<>";
            var leftField = _tokens.Count > at ? FieldOf(_tokens[at]) : null;
            return function switch
            {
                "%RANGE" => $"{left} {(negated ? "NOT " : "")}BETWEEN {Padded(arguments[0], leftField)} AND {Padded(arguments[1], leftField)}",
                "%VALUES" => $"{left} {(negated ? "NOT " : "")}IN ({string.Join(", ", arguments.Select(a => Padded(a, leftField)))})",
                _ => $"rtrim({left}) {(negated ? "NOT " : "")}LIKE {Wildcard(arguments)}",
            };
        }
        return $"{left} {sqlOp} {Concatenation()}";
    }

    /// <summary>A string compared with a character field: as long as the field, as Db2 compares.</summary>
    private static string Padded(string value, DbField? field) =>
        field is { Type.Kind: RpgKind.Char } && value.StartsWith('\'') ? $"CAST({value} AS CHAR({field.Type.Length}))" : value;

    /// <summary>%WLDCRD's pattern as LIKE's: by default _ is one character and * any.</summary>
    private static string Wildcard(List<string> arguments)
    {
        var pattern = arguments[0].Trim('\'');
        var wild = arguments.Count > 1 ? arguments[1].Trim('\'') : "_*";
        var one = wild.Length > 0 ? wild[0] : '_';
        var many = wild.Length > 1 ? wild[1] : '*';
        var like = new StringBuilder();
        foreach (var ch in pattern)
            like.Append(ch == one ? "_" : ch == many ? "%" : ch is '%' or '_' ? "\\" + ch : ch.ToString());
        return $"'{like.ToString().TrimEnd()}'";
    }

    private static bool IsComparison(string op) =>
        op is "*EQ" or "*NE" or "*GT" or "*LT" or "*GE" or "*LE" or "*NG" or "*NL" or "=" or "<>" or "¬=" or ">" or "<" or ">=" or "<=" or "¬<" or "¬>";

    private string Concatenation()
    {
        var left = Additive();
        while (Peek is "*CAT" or "||" or "*BCAT" or "*TCAT")
        {
            var op = _tokens[_p++];
            var right = Additive();
            left = op switch
            {
                "*TCAT" => $"(rtrim({left}) || {right})",
                "*BCAT" => $"(rtrim({left}) || ' ' || ltrim({right}))",
                _ => $"({left}::text || {right}::text)",
            };
        }
        return left;
    }

    private string Additive()
    {
        var left = Multiplicative();
        while (Peek is "+" or "-") left = $"{left} {_tokens[_p++]} {Multiplicative()}";
        return left;
    }

    private string Multiplicative()
    {
        var left = Unary();
        while (Peek is "*" or "/" or "//")
        {
            var op = _tokens[_p++];
            left = op == "/" ? $"{left} / NULLIF({Unary()}, 0)" : $"{left} {op} {Unary()}";
        }
        return left;
    }

    private string Unary()
    {
        if (Take("-")) return $"-{Unary()}";
        if (Take("+")) return Unary();
        return Primary();
    }

    private List<string> Arguments()
    {
        var arguments = new List<string>();
        if (!Take("(")) return arguments;
        while (Peek is not null && Peek != ")")
        {
            arguments.Add(Concatenation());
            Take(",", ":");
        }
        Take(")");
        return arguments;
    }

    private string Primary()
    {
        var t = Peek ?? throw new FormatException("the expression ends too soon");
        _p++;
        if (t == "(")
        {
            var inner = Or();
            Take(")");
            return $"({inner})";
        }
        if (t.StartsWith('\'')) return t;
        if (char.IsDigit(t[0]) || t[0] == '.') return t;
        if (t.StartsWith('%'))
        {
            var start = _p;
            var arguments = Arguments();
            string Arg(int i) => i < arguments.Count ? arguments[i] : throw new FormatException($"{t} needs {i + 1} arguments");
            return t switch
            {
                "%SST" or "%SUBSTRING" => arguments.Count > 2 ? $"substr({Arg(0)}::text, {Arg(1)}, {Arg(2)})" : $"substr({Arg(0)}::text, {Arg(1)})",
                "%DIGITS" => Digits(start, Arg(0)),
                "%SUM" => $"SUM({Arg(0)})",
                "%COUNT" => "COUNT(*)",
                "%MAX" => $"MAX({Arg(0)})",
                "%MIN" => $"MIN({Arg(0)})",
                "%AVG" => $"AVG({Arg(0)})",
                "%CHAR" => $"({Arg(0)})::text",
                "%STRIP" => $"trim({Arg(0)})",
                "%UPPER" or "%XLATE" => $"upper({Arg(0)})",
                "%LOWER" => $"lower({Arg(0)})",
                "%ABSVAL" => $"abs({Arg(0)})",
                "%LEN" => $"length({Arg(0)})",
                "%DATE" => $"({Arg(0)})::date",
                "%YEAR" => $"extract(year from {Arg(0)})",
                "%MONTH" => $"extract(month from {Arg(0)})",
                "%DAY" => $"extract(day from {Arg(0)})",
                _ => throw new FormatException($"{t} isn't supported"),
            };
        }
        if (t.StartsWith('*'))
        {
            return t switch
            {
                "*BLANK" or "*BLANKS" => "' '",
                "*ZERO" or "*ZEROS" => "0",
                "*NULL" => "NULL",
                _ => throw new FormatException($"{t} isn't supported"),
            };
        }
        return Operand(t);
    }

    /// <summary>%DIGITS: a number's digits, as many as the field has, with no sign or point.</summary>
    private string Digits(int start, string argument)
    {
        var digits = start + 1 < _tokens.Count && FieldOf(_tokens[start + 1]) is { } field && field.Type.IsNumeric ? field.Type.Length : 15;
        var scale = start + 1 < _tokens.Count && FieldOf(_tokens[start + 1]) is { } f2 ? f2.Type.Decimals : 0;
        return $"lpad(trunc(abs({argument}) * {Pow10(scale)})::bigint::text, {digits}, '0')";
    }

    private static string Pow10(int scale) => scale == 0 ? "1" : "1" + new string('0', scale);
}
