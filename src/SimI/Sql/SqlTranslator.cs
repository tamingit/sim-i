// Turns a Db2 for i SQL statement into one PostgreSQL runs, where the difference is simple: library
// qualifiers (each library is a schema), host variables, INTO, isolation clauses, special registers,
// date arithmetic with labeled durations, function-style casts, and the column options of CREATE
// TABLE. What it can't translate it says so, by name. Scalar functions PostgreSQL lacks (UCASE,
// DIGITS, YEAR and so on) are the store's own, in the schema qsim, so they need no rewriting.

using System.Text;

namespace SimI;

public enum SqlKind
{
    /// <summary>A statement that returns one row into host variables: SELECT INTO, VALUES INTO, SET :v =.</summary>
    Query,

    /// <summary>INSERT, UPDATE, DELETE and data definition.</summary>
    Execute,

    /// <summary>A SELECT with no INTO, as a script or RUNSQL runs it.</summary>
    Select,
    DeclareCursor,
    Open,
    Fetch,
    Close,
    Prepare,
    ExecuteImmediate,
    ExecutePrepared,

    /// <summary>GET DIAGNOSTICS :v = ROW_COUNT.</summary>
    RowCount,

    /// <summary>CALL of a program (or QSYS2.QCMDEXC): its name in <see cref="SqlTranslation.Statement"/>, its arguments in <see cref="SqlTranslation.Arguments"/>.</summary>
    Call,

    /// <summary>A statement with nothing to do here, such as SET OPTION or COMMIT.</summary>
    NoOp,
    Unsupported,
}

/// <summary>
/// A translated statement. <see cref="Sql"/> is PostgreSQL's text, with a parameter @h0, @h1 ... for
/// each of <see cref="Inputs"/> (host variable names; *USER is the job's user). <see cref="Outputs"/>
/// are the INTO variables, each with its indicator variable or null. <see cref="Notes"/> say what
/// was changed or dropped; <see cref="Problem"/> why the statement isn't supported.
/// </summary>
public sealed record SqlTranslation(
    SqlKind Kind,
    string? Sql = null,
    IReadOnlyList<string>? InputNames = null,
    IReadOnlyList<(string Name, string? Indicator)>? OutputNames = null,
    string? Cursor = null,
    string? Statement = null,
    IReadOnlyList<string>? NoteList = null,
    string? Problem = null,
    string? Orientation = null,
    string? Amount = null,
    string? Rows = null,
    IReadOnlyList<string>? Arguments = null)
{
    public IReadOnlyList<string> Inputs => InputNames ?? [];
    public IReadOnlyList<(string Name, string? Indicator)> Outputs => OutputNames ?? [];
    public IReadOnlyList<string> Notes => NoteList ?? [];
}

public static class SqlTranslator
{
    private enum T
    {
        Word,
        Number,
        String,
        Quoted,
        Host,
        Marker,
        Symbol,
    }

    /// <param name="Spaced">Whether white space came before it, which the translation keeps.</param>
    private sealed record Token(T Type, string Text, bool Spaced = true)
    {
        public bool Is(string word) => Type == T.Word && Text.Equals(word, StringComparison.OrdinalIgnoreCase);
        public bool IsSymbol(string symbol) => Type == T.Symbol && Text == symbol;
        public bool IsAny(params string[] words) => Type == T.Word && words.Contains(Text, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class Unsupported(string message) : Exception(message);

    /// <summary>Splits a script into statements at each semicolon outside a string or comment, with each one's first line.</summary>
    public static List<(string Text, int Line)> SplitScript(string script)
    {
        var statements = new List<(string, int)>();
        var text = new StringBuilder();
        var line = 1;
        var startLine = 0;
        for (var i = 0; i < script.Length; i++)
        {
            var c = script[i];
            if (c == '-' && i + 1 < script.Length && script[i + 1] == '-')
            {
                while (i < script.Length && script[i] != '\n') i++;
                i--;
                continue;
            }
            if (c == '/' && i + 1 < script.Length && script[i + 1] == '*')
            {
                var end = script.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? script.Length : end + 2;
                line += script[i..end].Count(ch => ch == '\n');
                i = end - 1;
                continue;
            }
            if (c is '\'' or '"')
            {
                var end = i + 1;
                while (end < script.Length && script[end] != c) end++;
                var quoted = script[i..Math.Min(end + 1, script.Length)];
                if (startLine == 0) startLine = line;
                text.Append(quoted);
                line += quoted.Count(ch => ch == '\n');
                i = end;
                continue;
            }
            if (c == ';')
            {
                if (text.ToString().Trim().Length > 0) statements.Add((text.ToString().Trim(), startLine));
                text.Clear();
                startLine = 0;
                continue;
            }
            if (c == '\n') line++;
            if (startLine == 0 && !char.IsWhiteSpace(c)) startLine = line;
            text.Append(c);
        }
        if (text.ToString().Trim().Length > 0) statements.Add((text.ToString().Trim(), startLine));
        return statements;
    }

    /// <summary>
    /// Translates one statement. <paramref name="schema"/> is the library SET SCHEMA named earlier in
    /// a script: tables named without a library are that library's.
    /// </summary>
    public static SqlTranslation Translate(string db2, string? schema = null)
    {
        var notes = new List<string>();
        try
        {
            var tokens = Tokenize(db2);
            if (tokens.Count == 0) return new SqlTranslation(SqlKind.NoOp);
            return Statement(tokens, notes, schema?.ToUpperInvariant()) with { NoteList = notes };
        }
        catch (Unsupported e)
        {
            return new SqlTranslation(SqlKind.Unsupported, Problem: e.Message, NoteList: notes);
        }
    }

    // ---- tokens ----

    private static List<Token> Tokenize(string sql)
    {
        var tokens = new List<Token>();
        var i = 0;
        var spaced = false;
        var counted = 0;
        while (i < sql.Length)
        {
            // Each token remembers whether white space or a comment came before it.
            if (tokens.Count > counted)
            {
                tokens[^1] = tokens[^1] with { Spaced = spaced };
                counted = tokens.Count;
                spaced = false;
            }
            var c = sql[i];
            if (char.IsWhiteSpace(c))
            {
                spaced = true;
                i++;
            }
            else if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                spaced = true;
                while (i < sql.Length && sql[i] != '\n') i++;
            }
            else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                spaced = true;
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + 2;
            }
            else if (c is '\'' or '"')
            {
                var start = i++;
                while (i < sql.Length)
                {
                    if (sql[i] == c)
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == c) i++;
                        else break;
                    }
                    i++;
                }
                if (i >= sql.Length) throw new Unsupported("a string or quoted name isn't closed");
                i++;
                tokens.Add(new Token(c == '\'' ? T.String : T.Quoted, sql[start..i]));
            }
            else if (c == ':' && HostAfter(sql, i) is > 0 and var nameAt)
            {
                // The precompiler takes blanks between the colon and the name.
                var start = i = nameAt;
                while (i < sql.Length && (IsWordChar(sql[i]) || sql[i] == '.' && i + 1 < sql.Length && IsWordChar(sql[i + 1]))) i++;
                tokens.Add(new Token(T.Host, sql[start..i]));
            }
            else if (char.IsDigit(c) || c == '.' && i + 1 < sql.Length && char.IsDigit(sql[i + 1]))
            {
                var start = i;
                while (i < sql.Length && (char.IsDigit(sql[i]) || sql[i] == '.')) i++;
                if (i < sql.Length && sql[i] is 'e' or 'E' && i + 1 < sql.Length && (char.IsDigit(sql[i + 1]) || sql[i + 1] is '+' or '-'))
                {
                    i += 2;
                    while (i < sql.Length && char.IsDigit(sql[i])) i++;
                }
                tokens.Add(new Token(T.Number, sql[start..i]));
            }
            else if (IsWordChar(c))
            {
                var start = i;
                while (i < sql.Length && IsWordChar(sql[i])) i++;
                var word = sql[start..i];
                // Db2 takes # @ $ in an ordinary name; PostgreSQL needs it quoted (as Db2 has it, in capitals).
                tokens.Add(word.IndexOfAny(['#', '@', '$']) >= 0 ? new Token(T.Quoted, $"\"{word.ToUpperInvariant()}\"") : new Token(T.Word, word));
            }
            else if (c == '?')
            {
                tokens.Add(new Token(T.Marker, "?"));
                i++;
            }
            else
            {
                var two = i + 1 < sql.Length ? sql.Substring(i, 2) : "";
                if (two is "<>" or "<=" or ">=" or "!=" or "||" or "¬=")
                {
                    tokens.Add(new Token(T.Symbol, two == "¬=" ? "<>" : two));
                    i += 2;
                }
                else if (two == "!!")
                {
                    tokens.Add(new Token(T.Symbol, "||"));
                    i += 2;
                }
                else
                {
                    tokens.Add(new Token(T.Symbol, c.ToString()));
                    i++;
                }
            }
        }
        if (tokens.Count > counted) tokens[^1] = tokens[^1] with { Spaced = spaced };
        // An indicator variable follows its host variable, with or without INDICATOR: one token, "name\tindicator".
        for (var k = 0; k < tokens.Count - 1; k++)
        {
            if (tokens[k].Type != T.Host) continue;
            var next = tokens[k + 1].Is("INDICATOR") && k + 2 < tokens.Count ? k + 2 : k + 1;
            if (tokens[next].Type != T.Host) continue;
            tokens[k] = tokens[k] with { Text = $"{tokens[k].Text}\t{tokens[next].Text}" };
            tokens.RemoveRange(k + 1, next - k);
        }
        return tokens;
    }

    /// <summary>Where a host variable's name starts after the colon at <paramref name="colon"/>, or 0.</summary>
    private static int HostAfter(string sql, int colon)
    {
        var k = colon + 1;
        while (k < sql.Length && sql[k] is ' ' or '\t' or '\n' or '\r') k++;
        return k < sql.Length && (char.IsLetter(sql[k]) || sql[k] is '_' or '#' or '@' or '$') ? k : 0;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '#' or '@' or '$';

    /// <summary>The tokens as text, spaced as the statement was, with any second statement on its own line.</summary>
    private static string Join(IEnumerable<Token> tokens)
    {
        var text = new StringBuilder();
        Token? previous = null;
        foreach (var t in tokens)
        {
            if (previous is not null && previous.IsSymbol(";")) text.Append('\n');
            else if (previous is not null && t.Spaced && !(t.Type == T.Symbol && t.Text is "," or ")" or ";") && !previous.IsSymbol("(")) text.Append(' ');
            text.Append(t.Text);
            previous = t;
        }
        return text.ToString();
    }

    // ---- statements ----

    /// <summary>The library SET SCHEMA (or SET CURRENT SCHEMA) names, or null when the statement isn't one.</summary>
    public static string? SetSchema(string db2)
    {
        var m = System.Text.RegularExpressions.Regex.Match(db2, @"^\s*SET\s+(?:CURRENT\s+)?SCHEMA\s*=?\s*""?([A-Za-z#$@][A-Za-z0-9#$@_]*)""?\s*;?\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
    }

    private static SqlTranslation Statement(List<Token> t, List<string> notes, string? schema)
    {
        bool Starts(params string[] words) => t.Count >= words.Length && words.Select((w, i) => t[i].Is(w)).All(b => b);
        string Rest(int from) => string.Join(' ', t.Skip(from).Select(x => x.Text));

        if (Starts("SET", "OPTION")) return NoOp(notes, "SET OPTION is ignored");
        if (Starts("SET", "SCHEMA") || Starts("SET", "CURRENT", "SCHEMA"))
            return NoOp(notes, "SET SCHEMA names the library of the tables named without one that follow it in a script; in a program it's ignored");
        if (Starts("SET", "CURRENT") || Starts("SET", "PATH"))
            return NoOp(notes, $"SET {t[1].Text.ToUpperInvariant()} is ignored: name the library in each table, as LIB.TABLE");
        if (Starts("SET", "TRANSACTION")) return NoOp(notes, "SET TRANSACTION is ignored: SQL runs without commitment control");
        if (Starts("COMMIT") || Starts("ROLLBACK"))
            return NoOp(notes, $"{t[0].Text.ToUpperInvariant()} is ignored: SQL runs without commitment control");
        if (Starts("INCLUDE")) return NoOp(notes, "INCLUDE is ignored: SQLCODE and SQLSTATE are always there");
        if (Starts("LABEL", "ON") || Starts("COMMENT", "ON") || Starts("GRANT") || Starts("REVOKE"))
            return NoOp(notes, $"{t[0].Text.ToUpperInvariant()} is ignored");
        if (Starts("CREATE", "SCHEMA") || Starts("CREATE", "COLLECTION"))
            return NoOp(notes, "CREATE SCHEMA makes the library in a script (or use CRTLIB); it does nothing in a program");
        if (Starts("WHENEVER")) throw new Unsupported("WHENEVER isn't supported: test SQLCODE or SQLSTATE after each statement");

        if (Starts("SET") && t.Count > 3 && t[1].Type == T.Host && t[2].IsSymbol("="))
        {
            if (t.Skip(3).Any(x => x.Type == T.Host && TopLevelAssignment(t, x))) throw new Unsupported("SET with more than one host variable isn't supported");
            var body = Rewrite([new Token(T.Word, "SELECT"), .. t.Skip(3)], notes, schema, out var inputs, out _);
            return new SqlTranslation(SqlKind.Query, body, inputs, [Output(t[1])]);
        }

        if (Starts("DECLARE"))
        {
            var at = t.FindIndex(x => x.Is("CURSOR"));
            if (at < 0) throw new Unsupported($"DECLARE {Rest(1).Split(' ').FirstOrDefault()?.ToUpperInvariant()} isn't supported: only cursors can be declared");
            var forAt = t.FindIndex(at, x => x.Is("FOR"));
            if (forAt < 0 || forAt + 1 >= t.Count) throw new Unsupported("DECLARE CURSOR needs FOR and a SELECT or statement name");
            if (t.Skip(at).Take(forAt - at).Any(x => x.Is("RETURN") && !t[t.IndexOf(x) - 1].Is("WITHOUT")))
                throw new Unsupported("a cursor WITH RETURN isn't supported");
            if (t.Take(at).Any(x => x.Is("SCROLL") && !t[Math.Max(0, t.IndexOf(x) - 1)].Is("NO")))
                notes.Add("SCROLL is ignored: every cursor scrolls");
            var name = t[1].Text.ToUpperInvariant();
            if (forAt + 2 == t.Count && t[forAt + 1].Type == T.Word)
                return new SqlTranslation(SqlKind.DeclareCursor, Cursor: name, Statement: t[forAt + 1].Text.ToUpperInvariant());
            var select = Rewrite(t.Skip(forAt + 1).ToList(), notes, schema, out var inputs, out var outputs);
            if (outputs.Count > 0) throw new Unsupported("a cursor's SELECT can't have INTO");
            return new SqlTranslation(SqlKind.DeclareCursor, select, inputs, Cursor: name);
        }

        if (Starts("OPEN") && t.Count >= 2)
        {
            var inputs = new List<string>();
            if (t.Count > 2)
            {
                if (!t[2].Is("USING") || t.Skip(3).Any(x => x.Type != T.Host && !x.IsSymbol(",")))
                    throw new Unsupported("OPEN takes only USING and host variables");
                inputs.AddRange(t.Skip(3).Where(x => x.Type == T.Host).Select(x => Output(x).Name));
            }
            return new SqlTranslation(SqlKind.Open, InputNames: inputs, Cursor: t[1].Text.ToUpperInvariant());
        }

        if (Starts("FETCH"))
        {
            // FETCH [orientation] [FROM] cursor [FOR n ROWS] [INTO ...]. The orientation's number, and
            // the row count, are a literal or a host variable (the first input when there is one).
            var i = 1;
            string orientation = "NEXT";
            string? amount = null, rows = null;
            var inputs = new List<string>();
            string Number(string what)
            {
                if (i >= t.Count) throw new Unsupported($"{what} needs a number");
                var token = t[i++];
                if (token.Type == T.Host)
                {
                    inputs.Add(Output(token).Name);
                    return ":" + Output(token).Name;
                }
                var sign = "";
                if (token.IsSymbol("-") || token.IsSymbol("+"))
                {
                    sign = token.Text == "-" ? "-" : "";
                    if (i >= t.Count) throw new Unsupported($"{what} needs a number");
                    token = t[i++];
                }
                if (!long.TryParse(token.Text, out _)) throw new Unsupported($"{what} needs a whole number or a host variable");
                return sign + token.Text;
            }
            if (i < t.Count && t[i].IsAny("NEXT", "PRIOR", "FIRST", "LAST", "BEFORE", "AFTER", "CURRENT", "RELATIVE", "ABSOLUTE"))
            {
                orientation = t[i++].Text.ToUpperInvariant();
                if (orientation is "RELATIVE" or "ABSOLUTE") amount = Number($"FETCH {orientation}");
            }
            if (i < t.Count && t[i].Is("FROM")) i++;
            if (i >= t.Count) throw new Unsupported("FETCH needs a cursor name");
            var cursor = t[i++].Text.ToUpperInvariant();
            if (i < t.Count && t[i].Is("FOR"))
            {
                i++;
                rows = Number("FOR n ROWS");
                if (i >= t.Count || !t[i].IsAny("ROWS", "ROW")) throw new Unsupported("FOR n needs ROWS");
                i++;
            }
            if (i >= t.Count)
            {
                // Without INTO the cursor only moves.
                return new SqlTranslation(SqlKind.Fetch, InputNames: inputs, OutputNames: [], Cursor: cursor, Orientation: orientation, Amount: amount, Rows: rows);
            }
            if (t[i].Is("USING")) throw new Unsupported("FETCH USING DESCRIPTOR isn't supported");
            if (!t[i].Is("INTO")) throw new Unsupported("FETCH needs INTO and host variables");
            var outputs = t.Skip(i + 1).Where(x => x.Type == T.Host).Select(Output).ToList();
            if (outputs.Count == 0 || t.Skip(i + 1).Any(x => x.Type != T.Host && !x.IsSymbol(",")))
                throw new Unsupported("FETCH INTO takes host variables only");
            if (rows is not null && outputs.Count != 1) throw new Unsupported("a multiple-row FETCH goes into one data structure array");
            return new SqlTranslation(SqlKind.Fetch, InputNames: inputs, OutputNames: outputs, Cursor: cursor, Orientation: orientation, Amount: amount, Rows: rows);
        }

        if (Starts("CLOSE") && t.Count == 2) return new SqlTranslation(SqlKind.Close, Cursor: t[1].Text.ToUpperInvariant());

        if (Starts("PREPARE") && t.Count == 4 && t[2].Is("FROM") && t[3].Type is T.Host or T.String)
        {
            return t[3].Type == T.Host
                ? new SqlTranslation(SqlKind.Prepare, InputNames: [Output(t[3]).Name], Statement: t[1].Text.ToUpperInvariant())
                : new SqlTranslation(SqlKind.Prepare, Sql: Unquote(t[3].Text), Statement: t[1].Text.ToUpperInvariant());
        }
        if (Starts("EXECUTE", "IMMEDIATE") && t.Count == 3 && t[2].Type is T.Host or T.String)
        {
            return t[2].Type == T.Host
                ? new SqlTranslation(SqlKind.ExecuteImmediate, InputNames: [Output(t[2]).Name])
                : new SqlTranslation(SqlKind.ExecuteImmediate, Sql: Unquote(t[2].Text));
        }
        if (Starts("EXECUTE") && t.Count >= 2 && t[1].Type == T.Word && (t.Count == 2 || t[2].Is("USING")))
        {
            if (t.Skip(3).Any(x => x.Type != T.Host && !x.IsSymbol(","))) throw new Unsupported("EXECUTE USING takes host variables only");
            return new SqlTranslation(SqlKind.ExecutePrepared,
                InputNames: t.Skip(3).Where(x => x.Type == T.Host).Select(x => Output(x).Name).ToList(), Statement: t[1].Text.ToUpperInvariant());
        }

        if (Starts("CALL") && t.Count >= 2 && t[1].Type is T.Word or T.Quoted)
        {
            // CALL [library.]name [(argument, ...)]: an argument is a host variable (passed by
            // reference, so the program can change it), a literal or NULL.
            var i = 1;
            static string Bare(Token x) => (x.Type == T.Quoted ? x.Text[1..^1] : x.Text).ToUpperInvariant();
            var name = Bare(t[i++]);
            if (i + 1 < t.Count && (t[i].IsSymbol(".") || t[i].IsSymbol("/")) && t[i + 1].Type is T.Word or T.Quoted)
            {
                // QSYS2.QCMDEXC is the procedure over QSYS/QCMDEXC.
                name = name == "QSYS2" && Bare(t[i + 1]) == "QCMDEXC" ? "QCMDEXC" : name + "/" + Bare(t[i + 1]);
                i += 2;
            }
            var arguments = new List<string>();
            var inputs = new List<string>();
            if (i < t.Count)
            {
                if (!t[i].IsSymbol("(") || !t[^1].IsSymbol(")")) throw new Unsupported("CALL takes its arguments in parentheses");
                var parts = new List<List<Token>> { new() };
                var depth = 0;
                foreach (var token in t.Skip(i + 1).Take(t.Count - i - 2))
                {
                    if (token.IsSymbol("(")) depth++;
                    if (token.IsSymbol(")")) depth--;
                    if (token.IsSymbol(",") && depth == 0) parts.Add([]);
                    else parts[^1].Add(token);
                }
                if (parts is [{ Count: 0 }]) parts.Clear();
                foreach (var part in parts)
                {
                    switch (part)
                    {
                        case [{ Type: T.Host }, ..] when part.Count <= 2 && part.All(x => x.Type == T.Host):
                            arguments.Add(":" + Output(part[0]).Name);
                            inputs.Add(Output(part[0]).Name);
                            break;
                        case [{ Type: T.String or T.Number } literal]:
                            arguments.Add(literal.Text);
                            break;
                        case [{ Type: T.Symbol, Text: "-" }, { Type: T.Number } negative]:
                            arguments.Add("-" + negative.Text);
                            break;
                        case [var word] when word.Is("NULL"):
                            arguments.Add("NULL");
                            break;
                        default:
                            throw new Unsupported("a CALL argument that's an expression isn't supported: use a host variable");
                    }
                }
            }
            return new SqlTranslation(SqlKind.Call, InputNames: inputs, Statement: name, Arguments: arguments);
        }

        if (Starts("RENAME"))
        {
            // RENAME [TABLE | INDEX] name TO new [FOR SYSTEM NAME short]: the table gets its new (long)
            // name; its short system name, which RPG and CL use, is a view of it.
            var i = t[1].IsAny("TABLE", "INDEX") ? 2 : 1;
            var to = t.FindIndex(i, x => x.Is("TO"));
            if (to < 0 || to + 1 >= t.Count) throw new Unsupported("RENAME needs TO and the new name");
            var isIndex = t[1].Is("INDEX");
            // RENAME TABLE long TO SYSTEM NAME short: the table keeps its name; the short one is a view of it.
            if (!isIndex && to + 3 < t.Count && t[to + 1].Is("SYSTEM") && t[to + 2].Is("NAME"))
            {
                var table = Rewrite([new Token(T.Word, "TABLE"), .. t.Skip(i).Take(to - i)], notes, schema, out _, out _).Trim()[5..].Trim();
                var prefix = table.Contains('.') ? table[..(table.LastIndexOf('.') + 1)] : "";
                notes.Add($"the system name {Identifier(t[to + 3]).Text.Trim('"').ToUpperInvariant()} is a view of the table");
                return new SqlTranslation(SqlKind.Execute, $"CREATE VIEW {prefix}{Identifier(t[to + 3]).Text} AS SELECT * FROM {table}", NoteList: notes);
            }
            var newName = Identifier(t[to + 1]).Text;
            var systemAt = t.FindIndex(to, x => x.Is("SYSTEM"));
            var system = systemAt > 0 && systemAt + 2 < t.Count && t[systemAt + 1].Is("NAME") ? Identifier(t[systemAt + 2]).Text : null;
            // As TABLE name, the name gets its library (or the default one) as any table's does.
            var body = Rewrite([new Token(T.Word, "TABLE"), .. t.Skip(i).Take(to - i)], notes, schema, out _, out _).Trim()[5..].Trim();
            var renamed = $"ALTER {(isIndex ? "INDEX" : "TABLE")} {body} RENAME TO {newName}";
            if (!isIndex && system is not null && !system.Trim('"').Equals(newName.Trim('"'), StringComparison.OrdinalIgnoreCase))
            {
                var qualifier = body.Contains('.') ? body[..(body.LastIndexOf('.') + 1)] : "";
                renamed += $";\nCREATE VIEW {qualifier}{system} AS SELECT * FROM {qualifier}{newName}";
                notes.Add($"the system name {system.Trim('"').ToUpperInvariant()} is a view of the table");
            }
            return new SqlTranslation(SqlKind.Execute, renamed, NoteList: notes);
        }

        if (Starts("GET", "DIAGNOSTICS"))
        {
            if (t.Count == 5 && t[2].Type == T.Host && t[3].IsSymbol("=") && t[4].Is("ROW_COUNT"))
                return new SqlTranslation(SqlKind.RowCount, OutputNames: [Output(t[2])]);
            throw new Unsupported("GET DIAGNOSTICS is supported only as GET DIAGNOSTICS :variable = ROW_COUNT");
        }

        if (!t[0].IsAny("SELECT", "WITH", "VALUES", "INSERT", "UPDATE", "DELETE", "MERGE", "CREATE", "DROP", "ALTER"))
            throw new Unsupported($"the SQL statement {t[0].Text.ToUpperInvariant()} isn't supported");
        if (t[0].Is("CREATE"))
        {
            var what = t.Skip(1).FirstOrDefault(x => !x.IsAny("OR", "REPLACE", "UNIQUE"));
            if (what is null || !what.IsAny("TABLE", "INDEX", "VIEW"))
                throw new Unsupported($"CREATE {what?.Text.ToUpperInvariant()} isn't supported: only tables, indexes and views");
        }

        var sql = Rewrite(t, notes, schema, out var hostInputs, out var hostOutputs);
        var query = t[0].IsAny("SELECT", "WITH", "VALUES");
        if (hostOutputs.Count > 0 && !query) throw new Unsupported("INTO with host variables is supported only on SELECT and VALUES");
        var kind = !query ? SqlKind.Execute : hostOutputs.Count > 0 ? SqlKind.Query : SqlKind.Select;
        return new SqlTranslation(kind, sql, hostInputs, hostOutputs);
    }

    private static bool TopLevelAssignment(List<Token> t, Token host)
    {
        var i = t.IndexOf(host);
        return i + 1 < t.Count && t[i + 1].IsSymbol("=") && i > 0 && t[i - 1].IsSymbol(",");
    }

    private static SqlTranslation NoOp(List<string> notes, string note)
    {
        notes.Add(note);
        return new SqlTranslation(SqlKind.NoOp);
    }

    private static (string Name, string? Indicator) Output(Token host)
    {
        var parts = host.Text.Split('\t');
        return (parts[0], parts.Length > 1 ? parts[1] : null);
    }

    private static string Unquote(string literal) => literal[1..^1].Replace("''", "'");

    // ---- the rewrite ----

    private static readonly string[] Isolation = ["UR", "CS", "RS", "RR", "NC", "CHG", "ALL", "NONE"];
    private static readonly string[] DurationUnits =
        ["DAY", "DAYS", "MONTH", "MONTHS", "YEAR", "YEARS", "HOUR", "HOURS", "MINUTE", "MINUTES", "SECOND", "SECONDS", "MICROSECOND", "MICROSECONDS"];
    private static readonly string[] SystemSchemas = ["QSYS2", "SYSIBM", "SYSCAT", "SYSTOOLS", "SYSPROC", "QSYS", "SYSIBMADM"];

    /// <summary>
    /// Rewrites a data or definition statement, token by token. Host variables become @h parameters
    /// (<paramref name="inputs"/>), and a SELECT's or VALUES' INTO comes out as <paramref name="outputs"/>.
    /// </summary>
    private static string Rewrite(List<Token> source, List<string> notes, string? schema, out List<string> inputs, out List<(string, string?)> outputs)
    {
        var t = source.ToList();
        var cteNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (t[0].IsAny("SELECT", "WITH", "VALUES", "INSERT", "UPDATE", "DELETE")) t = HoistDerivedTables(t, cteNames, notes);
        RowsOfValues(t);
        DateArithmetic(t, notes);
        outputs = [];
        inputs = [];
        void Note(string note)
        {
            if (!notes.Contains(note)) notes.Add(note);
        }
        Token Word(string text) => new(T.Word, text);
        // A library is a schema; QTEMP, the job's temporary library, is PostgreSQL's pg_temp.
        Token Schema(Token library) => Word(SchemaName(library.Text.Trim('"')));
        Token Symbol(string text, bool spaced = true) => new(T.Symbol, text, spaced);
        bool At(int i, params string[] words) => i >= 0 && i + words.Length <= t.Count && words.Select((w, k) => t[i + k].Is(w)).All(b => b);
        var query = t[0].IsAny("SELECT", "WITH", "VALUES");
        var definition = t[0].IsAny("CREATE", "ALTER", "DROP");

        // INTO :a, :b of a SELECT or VALUES.
        if (query)
        {
            var depth = 0;
            for (var i = 0; i < t.Count; i++)
            {
                if (t[i].IsSymbol("(")) depth++;
                else if (t[i].IsSymbol(")")) depth--;
                else if (depth == 0 && t[i].Is("INTO") && i + 1 < t.Count && t[i + 1].Type == T.Host)
                {
                    var end = i + 1;
                    while (end < t.Count && (t[end].Type == T.Host || t[end].IsSymbol(",") && end + 1 < t.Count && t[end + 1].Type == T.Host)) end++;
                    outputs.AddRange(t.Skip(i + 1).Take(end - i - 1).Where(x => x.Type == T.Host).Select(Output));
                    t.RemoveRange(i, end - i);
                    break;
                }
            }
            // VALUES (a, b) INTO :x, :y is SELECT a, b.
            if (t[0].Is("VALUES") && outputs.Count > 0)
            {
                t[0] = Word("SELECT");
                if (t.Count > 2 && t[1].IsSymbol("(") && MatchingParen(t, 1) == t.Count - 1)
                {
                    t.RemoveAt(t.Count - 1);
                    t.RemoveAt(1);
                    t[1] = t[1] with { Spaced = true };
                }
            }
        }

        // CREATE OR REPLACE: drop it first.
        var prefix = new List<Token>();
        if (At(0, "CREATE", "OR", "REPLACE") && t.Count > 4 && t[3].IsAny("TABLE", "VIEW"))
        {
            var qualified = t.Count > 6 && t[5].Type == T.Symbol && t[5].Text is "." or "/";
            Token[] name = qualified ? [Schema(t[4]) with { Spaced = true }, Symbol(".", false), t[6] with { Spaced = false }]
                : [Word(Machine.DefaultSchemaMarker) with { Spaced = true }, Symbol(".", false), t[4] with { Spaced = false }];
            prefix = [new Token(T.Word, "DROP", false), Word(t[3].Text.ToUpperInvariant()), Word("IF"), Word("EXISTS"), .. name, Symbol(";", false)];
            if (t[3].Is("TABLE")) Note("CREATE OR REPLACE TABLE drops the table and its rows first");
            t.RemoveRange(1, 2);
        }

        var result = new List<Token>();
        var after = new List<string>();
        var changeTimestamps = new List<string>();
        var systemNames = new List<(string Column, string System)>();
        string? recordFormat = null;
        var indexTable = false;
        var expectTable = false;
        var fromDepths = new HashSet<int>();
        var parenDepth = 0;
        for (var i = 0; i < t.Count; i++)
        {
            var token = t[i];
            var previous = result.Count > 0 ? result[^1] : null;
            var next = i + 1 < t.Count ? t[i + 1] : null;

            if (token.IsSymbol("(")) parenDepth++;
            if (token.IsSymbol(")")) fromDepths.Remove(parenDepth--);

            // ---- a table name, with its library dropped ----
            if (expectTable && token.Type is T.Word or T.Quoted && !token.IsAny("IF", "NOT", "EXISTS", "ONLY"))
            {
                expectTable = false;
                if (token.IsAny("TABLE", "LATERAL", "FINAL", "NEW", "OLD", "UNNEST", "XMLTABLE", "JSON_TABLE") && next is not null && (next.IsSymbol("(") || next.Is("TABLE")))
                    throw new Unsupported($"{token.Text.ToUpperInvariant()} in FROM isn't supported");
                if (next is not null && next.Type == T.Symbol && next.Text is "." or "/" && i + 2 < t.Count && t[i + 2].Type is T.Word or T.Quoted)
                {
                    var isSystem = SystemSchemas.Contains(token.Text, StringComparer.OrdinalIgnoreCase);
                    if (isSystem && SystemViews.TryGetValue(t[i + 2].Text, out var view))
                    {
                        result.Add(Word(view) with { Spaced = token.Spaced });
                        i += 2;
                        continue;
                    }
                    if (!indexTable && At(0, "CREATE") && t.Any(x => x.Is("INDEX")) && previous is not null && previous.IsAny("INDEX", "EXISTS"))
                    {
                        // CREATE INDEX LIB.X ON LIB.T: the index goes in its table's schema, and its name has no library.
                        result.Add(Identifier(t[i + 2]) with { Spaced = token.Spaced });
                        i += 2;
                        continue;
                    }
                    if (isSystem && !t[i + 2].Is("SYSDUMMY1"))
                        throw new Unsupported($"{token.Text.ToUpperInvariant()}.{t[i + 2].Text.ToUpperInvariant()} isn't supported: Sim i has no system catalog or services");
                    // SYSIBM.SYSDUMMY1 is the one system table there is: in QGPL, found without a library.
                    if (isSystem)
                        result.Add(t[i + 2] with { Spaced = token.Spaced });
                    else
                        result.AddRange([Schema(token) with { Spaced = token.Spaced }, Symbol(".", false), Identifier(t[i + 2]) with { Spaced = false }]);
                    i += 2;
                    continue;
                }
                var creating = At(0, "CREATE") && previous is not null && previous.IsAny("TABLE", "VIEW", "INDEX", "EXISTS")
                    && (!t.Any(x => x.Is("INDEX")) || previous.Is("INDEX") || previous.Is("EXISTS"));
                if (cteNames.Contains(token.Text.Trim('"')))
                {
                    result.Add(token);
                }
                else if (creating && t.Any(x => x.Is("INDEX")) && !indexTable)
                {
                    // An index's name: it goes in its table's schema.
                    result.Add(Identifier(token));
                }
                else if (schema is not null)
                {
                    result.AddRange([Word(SchemaName(schema)) with { Spaced = token.Spaced }, Symbol(".", false), Identifier(token) with { Spaced = false }]);
                }
                // A table made with no library goes in the job's default library, which the machine fills in when it runs.
                else if (creating)
                    result.AddRange([Word(Machine.DefaultSchemaMarker) with { Spaced = token.Spaced }, Symbol(".", false), Identifier(token) with { Spaced = false }]);
                else
                    result.Add(Identifier(token));
                indexTable = false;
                continue;
            }
            if (token.Type != T.Word || !token.IsAny("IF", "NOT", "EXISTS", "ONLY")) expectTable = false;

            if (token.Type == T.Host)
            {
                inputs.Add(Output(token).Name);
                result.Add(Word($"@h{inputs.Count - 1}"));
                continue;
            }
            if (token.Type == T.Marker)
            {
                // A parameter marker takes its value from USING, in order.
                inputs.Add("?");
                result.Add(Word($"@h{inputs.Count - 1}"));
                continue;
            }
            if (token.Type == T.Quoted)
            {
                result.Add(Identifier(token));
                continue;
            }
            if (token.Type != T.Word)
            {
                if (token.IsSymbol(",") && fromDepths.Contains(parenDepth)) expectTable = true;
                // Db2 compares characters as if the shorter were padded with blanks; Sim i keeps them without trailing blanks.
                if (token.Type == T.String && token.Text.EndsWith(" '", StringComparison.Ordinal)
                    && (previous is not null && IsComparison(previous) || next is not null && IsComparison(next)))
                    token = token with { Text = "'" + token.Text[1..^1].TrimEnd(' ') + "'" };
                result.Add(token);
                continue;
            }

            // ---- where a table name comes next ----
            if (token.Is("FROM"))
            {
                // Not the FROM of TRIM(x FROM y), SUBSTRING(x FROM n) or EXTRACT.
                if (!(previous is not null && previous.IsAny("BOTH", "LEADING", "TRAILING")) && !InsideFunctionFrom(t, i))
                {
                    fromDepths.Add(parenDepth);
                    expectTable = true;
                }
                result.Add(token);
                // DELETE FROM t x: AS before the correlation name, as standard SQL has it.
                if (At(0, "DELETE") && i == 1 && AliasAfterTable(t, i + 1) is { } aliasAt) t.Insert(aliasAt, Word("AS"));
                continue;
            }
            if (token.IsAny("WHERE", "GROUP", "ORDER", "HAVING", "UNION", "EXCEPT", "INTERSECT", "SET", "ON", "USING")) fromDepths.Remove(parenDepth);
            if (token.IsAny("JOIN", "TABLE", "INDEX", "VIEW", "REFERENCES") || token.Is("INTO") && next?.Type != T.Host
                || token.Is("USING") && At(0, "MERGE") && next is not null && !next.IsSymbol("(")
                || token.Is("UPDATE") && i == 0 || token.Is("ON") && At(0, "CREATE") && t.Any(x => x.Is("INDEX")) && parenDepth == 0)
            {
                expectTable = true;
                indexTable = token.Is("ON");
                result.Add(token);
                if (token.Is("UPDATE") && AliasAfterTable(t, i + 1) is { } aliasAt) t.Insert(aliasAt, Word("AS"));
                continue;
            }

            // ---- clauses with nothing to do here ----
            if (token.Is("WITH") && next is not null && next.IsAny(Isolation) && (i + 2 >= t.Count || !t[i + 2].IsSymbol("(") && !t[i + 2].Is("AS")))
            {
                Note($"WITH {next.Text.ToUpperInvariant()} is dropped: SQL runs without commitment control");
                i++;
                continue;
            }
            if (token.Is("FOR") && (At(i + 1, "READ", "ONLY") || At(i + 1, "FETCH", "ONLY")))
            {
                i += 2;
                continue;
            }
            if (token.Is("FOR") && At(i + 1, "UPDATE"))
            {
                Note("FOR UPDATE is dropped");
                i++;
                if (At(i + 1, "OF"))
                {
                    i += 2;
                    while (i + 1 < t.Count && t[i + 1].IsSymbol(",")) i += 2;
                }
                continue;
            }
            if (token.Is("OPTIMIZE") && At(i + 1, "FOR"))
            {
                while (i < t.Count && !t[i].IsAny("ROW", "ROWS")) i++;
                continue;
            }
            if (token.Is("WHERE") && At(i + 1, "CURRENT", "OF")) throw new Unsupported("WHERE CURRENT OF a cursor isn't supported: use the row's key");

            // ---- operators and special registers ----
            if (token.Is("CONCAT") && next is not null && !next.IsSymbol("(") && previous is not null)
            {
                result.Add(Symbol("||"));
                continue;
            }
            if (token.Is("CURRENT") && next is not null && next.IsAny("DATE", "TIME", "TIMESTAMP", "USER", "SCHEMA", "SERVER", "TIMEZONE", "PATH"))
            {
                token = Word($"CURRENT_{next.Text.ToUpperInvariant()}");
                i++;
                next = i + 1 < t.Count ? t[i + 1] : null;
            }
            if (token.IsAny("CURRENT_DATE", "CURRENT_TIME", "CURRENT_TIMESTAMP"))
            {
                // Local time, as Db2's are, with no time zone.
                result.Add(Word(token.Text.ToUpperInvariant() switch
                {
                    "CURRENT_DATE" => "CURRENT_DATE",
                    "CURRENT_TIME" => "LOCALTIME(0)",
                    _ => "LOCALTIMESTAMP",
                }) with { Spaced = token.Spaced });
                continue;
            }
            if (token.Is("IDENTITY_VAL_LOCAL") && next is not null && next.IsSymbol("(") && i + 2 < t.Count && t[i + 2].IsSymbol(")"))
            {
                inputs.Add("*IDENTITY");
                result.Add(Word($"@h{inputs.Count - 1}") with { Spaced = token.Spaced });
                i += 2;
                continue;
            }
            if (token.IsAny("CURRENT_USER", "SESSION_USER"))
            {
                inputs.Add("*USER");
                result.Add(Word($"@h{inputs.Count - 1}"));
                continue;
            }
            if (token.IsAny("CURRENT_SCHEMA", "CURRENT_SERVER", "CURRENT_TIMEZONE", "CURRENT_PATH"))
                throw new Unsupported($"{token.Text.ToUpperInvariant().Replace('_', ' ')} isn't supported");
            if (token.IsAny(DurationUnits) && previous is not null && (previous.Type is T.Number || previous.IsSymbol(")") || previous.Text.StartsWith("@h"))
                && !(next?.IsSymbol("(") ?? false) && result.Count >= 2 && result[^2].Type == T.Symbol && result[^2].Text is "+" or "-")
                throw new Unsupported($"date arithmetic with {token.Text.ToUpperInvariant()} isn't supported here");
            if (token.Is("RRN") && next is not null && next.IsSymbol("(") && i + 3 < t.Count && t[i + 3].IsSymbol(")"))
            {
                // The relative record number: the hidden column each table has.
                result.AddRange([Identifier(t[i + 2]) with { Spaced = token.Spaced }, Symbol(".", false), new Token(T.Word, Store.RowNumber, false)]);
                i += 3;
                continue;
            }
            if (token.Is("VALUE") && next is not null && next.IsSymbol("("))
            {
                result.Add(Word("COALESCE") with { Spaced = token.Spaced });
                continue;
            }
            // Function-style casts: INT(x), DEC(x, 7, 2), CHAR(x), DOUBLE(x) ... but not a column's type.
            if (next is not null && next.IsSymbol("(") && FunctionCast(token) is { } cast && (previous is null || previous.Type == T.Symbol && !previous.IsSymbol(".") || previous.IsAny(CastFollows)))
            {
                var close = MatchingParen(t, i + 1);
                var args = Arguments(t, i + 1, close);
                if (args.Count > 0)
                {
                    // Host variables keep their colons (and indicators), as the expression is read again.
                    string Text(List<Token> tokens) => string.Join(" ", tokens.Select(x => x.Type == T.Host ? ":" + x.Text.Replace("\t", " :") : x.Text));
                    var value = Text(args[0]);
                    var text = cast switch
                    {
                        "INTEGER" => $"CAST(TRUNC(CAST(({value}) AS NUMERIC)) AS BIGINT)",
                        "DECIMAL" when args.Count >= 2 => $"CAST(TRUNC(CAST(({value}) AS NUMERIC), {(args.Count > 2 ? Text(args[2]) : "0")}) AS NUMERIC({Text(args[1])}, {(args.Count > 2 ? Text(args[2]) : "0")}))",
                        "DECIMAL" => $"CAST(({value}) AS NUMERIC)",
                        "DOUBLE" => $"CAST(({value}) AS DOUBLE PRECISION)",
                        "DATE" or "TIME" or "TIMESTAMP" => $"CAST(({value}) AS {cast})",
                        _ when args.Count >= 2 && args[1] is [{ Type: T.Number } length] => $"RPAD(CAST(({value}) AS TEXT), {length.Text})",
                        _ => $"CAST(({value}) AS TEXT)",
                    };
                    // The rest of the statement still needs translating: the expression's tokens go back in place.
                    var replacement = Tokenize(text);
                    replacement[0] = replacement[0] with { Spaced = token.Spaced };
                    t.RemoveRange(i, close - i + 1);
                    t.InsertRange(i, replacement);
                    i--;
                    continue;
                }
            }

            // ---- column options of CREATE TABLE and ALTER TABLE ----
            if (definition)
            {
                if (token.Is("FOR") && At(i + 1, "COLUMN"))
                {
                    // The column's system name, which DDS and record I/O know it by: kept as the column's comment.
                    if (i + 2 < t.Count && result.Count > 0)
                        systemNames.Add((result[ColumnStart(result)].Text, (t[i + 2].Type == T.Quoted ? t[i + 2].Text[1..^1] : t[i + 2].Text).ToUpperInvariant()));
                    i += 2;
                    continue;
                }
                if (token.Is("FOR") && At(i + 1, "SYSTEM", "NAME"))
                {
                    i += 3;
                    continue;
                }
                if (token.IsAny("CCSID", "RCDFMT"))
                {
                    // The record format's name, which RPG's record I/O uses: kept as the table's comment.
                    if (token.Is("RCDFMT") && next is not null) recordFormat = (next.Type == T.Quoted ? next.Text[1..^1] : next.Text).ToUpperInvariant();
                    i++;
                    continue;
                }
                if (token.Is("ALLOCATE") && next is not null && next.IsSymbol("("))
                {
                    i = MatchingParen(t, i + 1);
                    continue;
                }
                if (At(i, "IMPLICITLY", "HIDDEN"))
                {
                    i++;
                    continue;
                }
                if (token.Is("WITH") && At(i + 1, "DEFAULT"))
                {
                    // WITH DEFAULT and no value: the type's default.
                    var hasValue = i + 2 < t.Count && !t[i + 2].IsSymbol(",") && !t[i + 2].IsSymbol(")") && t[i + 2].Type is T.Number or T.String or T.Word;
                    result.Add(Word("DEFAULT"));
                    if (!hasValue) result.Add(Word(DefaultFor(ColumnType(result))));
                    i++;
                    continue;
                }
                if (token.Is("DEFAULT") && (next is null || next.IsSymbol(",") || next.IsSymbol(")")
                    || next.IsAny("NOT", "CONSTRAINT", "PRIMARY", "UNIQUE", "CHECK", "REFERENCES", "IMPLICITLY", "CCSID")))
                {
                    // DEFAULT and no value: the type's default, as WITH DEFAULT.
                    result.Add(Word("DEFAULT"));
                    result.Add(Word(DefaultFor(ColumnType(result))));
                    continue;
                }
                if (token.Is("GENERATED") && next is not null && next.IsAny("ALWAYS", "BY"))
                {
                    var j = i + (next.Is("BY") ? 3 : 2);
                    if (At(j, "FOR", "EACH", "ROW", "ON", "UPDATE", "AS", "ROW", "CHANGE", "TIMESTAMP"))
                    {
                        // A row change timestamp: the time when the row is added, and a trigger for each update.
                        changeTimestamps.Add(result[ColumnStart(result)].Text);
                        result.AddRange([Word("DEFAULT"), Word("LOCALTIMESTAMP")]);
                        Note($"row change timestamp {result[ColumnStart(result)].Text.ToUpperInvariant()} is set by a trigger");
                        i = j + 8;
                        continue;
                    }
                    if (!At(j, "AS", "IDENTITY")) throw new Unsupported("GENERATED is supported only as an IDENTITY column or a ROW CHANGE TIMESTAMP");
                    // An identity column, as Db2 has it, without the options PostgreSQL doesn't take (ORDER, NO CACHE).
                    result.AddRange(t.Skip(i).Take(j + 2 - i));
                    j += 2;
                    if (j < t.Count && t[j].IsSymbol("("))
                    {
                        var close = MatchingParen(t, j);
                        var options = t.GetRange(j, close - j + 1);
                        for (var k = 0; k < options.Count; k++)
                        {
                            if (options[k].Is("ORDER") || options[k].Is("NO") && k + 1 < options.Count && options[k + 1].IsAny("ORDER", "CACHE"))
                            {
                                options.RemoveRange(k, options[k].Is("ORDER") ? 1 : 2);
                                k--;
                            }
                        }
                        if (options.Count > 2) result.AddRange(options);
                        j = close + 1;
                    }
                    i = j - 1;
                    continue;
                }
            }

            result.Add(token);
        }

        if (At(0, "CREATE") && result.FindIndex(x => x.Is("TABLE")) is var tableAt and >= 0)
        {
            var n = tableAt + 1;
            while (n < result.Count && result[n].IsAny("IF", "NOT", "EXISTS")) n++;
            var qualified = n + 2 < result.Count && result[n + 1].IsSymbol(".");
            var name = string.Join("", result.Skip(n).Take(qualified ? 3 : 1).Select(x => x.Text));
            var table = (qualified ? result[n + 2] : result[n]).Text.Trim('"');
            // The hidden relative record number (RRN), last, when the table has a list of columns.
            var list = n + (qualified ? 3 : 1);
            if (list < result.Count && result[list].IsSymbol("("))
            {
                var close = MatchingParen(result, list);
                result.InsertRange(close, [Symbol(",", false), Word(Store.RowNumber), Word("BIGINT"), Word("GENERATED"), Word("BY"), Word("DEFAULT"), Word("AS"), Word("IDENTITY")]);
            }
            if (recordFormat is not null) after.Add($"COMMENT ON TABLE {name} IS '{Store.RecordFormatNote}{recordFormat.Replace("'", "''")}'");
            foreach (var (column, system) in systemNames)
                after.Add($"COMMENT ON COLUMN {name}.{column} IS '{Store.SystemNameNote}{system.Replace("'", "''")}'");
            foreach (var column in changeTimestamps)
                after.Add($"CREATE TRIGGER \"{table}_{column.Trim('"').ToLowerInvariant()}_rct\" BEFORE UPDATE ON {name} FOR EACH ROW EXECUTE FUNCTION qsim.row_change('{column.Trim('"').ToLowerInvariant()}')");
        }
        foreach (var statement in after) result.AddRange([Symbol(";", false), Word(statement)]);
        return Join([.. prefix, .. result]);
    }

    private static bool IsComparison(Token t) => t.Type == T.Symbol && t.Text is "=" or "<>" or "<" or ">" or "<=" or ">=" or "!=";

    /// <summary>A library's schema: its name in lower case, quoted; QTEMP is pg_temp.</summary>
    private static string SchemaName(string library) =>
        library.Equals("QTEMP", StringComparison.OrdinalIgnoreCase) ? "pg_temp" : Store.SchemaName(library);

    /// <summary>A name as PostgreSQL takes it: a quoted name in capitals is the name unquoted, as in Db2.</summary>
    private static Token Identifier(Token name) =>
        name.Type == T.Quoted && name.Text[1..^1] is var inner && inner == inner.ToUpperInvariant() ? name with { Text = $"\"{inner.ToLowerInvariant()}\"" } : name;

    /// <summary>Words a function-style cast can follow (otherwise, after a name, it's a column's type).</summary>
    private static readonly string[] CastFollows =
        ["SELECT", "WHERE", "AND", "OR", "NOT", "ON", "WHEN", "THEN", "ELSE", "SET", "VALUES", "BY", "HAVING", "IN", "IS", "RETURN", "DISTINCT", "ALL", "CASE", "BETWEEN", "LIKE"];

    /// <summary>The cast a Db2 function-style cast is: INT(x) is INTEGER, DEC(x, p, s) DECIMAL and so on; null for any other word.</summary>
    private static string? FunctionCast(Token word) => word.Type != T.Word ? null : word.Text.ToUpperInvariant() switch
    {
        "INT" or "INTEGER" or "BIGINT" or "SMALLINT" => "INTEGER",
        "DEC" or "DECIMAL" or "NUMERIC" or "ZONED" => "DECIMAL",
        "DOUBLE" or "FLOAT" or "REAL" => "DOUBLE",
        "CHAR" or "VARCHAR" or "CHARACTER" => "CHAR",
        "TIMESTAMP" => "TIMESTAMP",
        "TIME" => "TIME",
        _ => null,
    };

    /// <summary>A parenthesized list's arguments (between <paramref name="open"/> and <paramref name="close"/>), split at its commas.</summary>
    private static List<List<Token>> Arguments(List<Token> t, int open, int close)
    {
        var args = new List<List<Token>>();
        var current = new List<Token>();
        var depth = 0;
        for (var k = open + 1; k < close; k++)
        {
            if (t[k].IsSymbol("(")) depth++;
            else if (t[k].IsSymbol(")")) depth--;
            if (depth == 0 && t[k].IsSymbol(","))
            {
                args.Add(current);
                current = [];
                continue;
            }
            current.Add(t[k]);
        }
        if (current.Count > 0) args.Add(current);
        return args;
    }

    /// <summary>System catalog views Sim i has, made from its own tables.</summary>
    private static readonly Dictionary<string, string> SystemViews = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USER_INFO_BASIC"] = "(SELECT USERNAME AS AUTHORIZATION_NAME, TEXT AS TEXT_DESCRIPTION, STATUS, PWDEXP AS PASSWORD_EXPIRED, DFTLIB AS CURRENT_LIBRARY_NAME FROM qgpl.qsim_user)",
        ["USER_INFO"] = "(SELECT USERNAME AS AUTHORIZATION_NAME, TEXT AS TEXT_DESCRIPTION, STATUS, PWDEXP AS PASSWORD_EXPIRED, DFTLIB AS CURRENT_LIBRARY_NAME FROM qgpl.qsim_user)",
        ["SYSTABLES"] = "(SELECT upper(table_name) AS TABLE_NAME, upper(table_schema) AS TABLE_SCHEMA, CASE table_type WHEN 'VIEW' THEN 'V' ELSE 'T' END AS TABLE_TYPE, "
            + "upper(table_name) AS SYSTEM_TABLE_NAME, upper(table_schema) AS SYSTEM_TABLE_SCHEMA FROM information_schema.tables "
            + "WHERE table_schema NOT IN ('pg_catalog', 'information_schema', 'qsim') AND table_name NOT LIKE 'qsim!_%' ESCAPE '!')",
        ["SYSCOLUMNS"] = "(SELECT upper(column_name) AS COLUMN_NAME, upper(table_name) AS TABLE_NAME, upper(table_schema) AS TABLE_SCHEMA, "
            + "ordinal_position AS ORDINAL_POSITION, upper(data_type) AS DATA_TYPE, coalesce(character_maximum_length, numeric_precision) AS LENGTH, "
            + "numeric_scale AS NUMERIC_SCALE, CASE is_nullable WHEN 'YES' THEN 'Y' ELSE 'N' END AS IS_NULLABLE FROM information_schema.columns "
            + "WHERE table_schema NOT IN ('pg_catalog', 'information_schema', 'qsim') AND column_name <> 'qsim_rrn')",
    };

    /// <summary>
    /// A derived table with column names, (SELECT ...) AS X (A, B), becomes
    /// a common table expression: WITH X (A, B) AS (SELECT ...) before the statement.
    /// </summary>
    private static List<Token> HoistDerivedTables(List<Token> t, HashSet<string> names, List<string> notes)
    {
        var ctes = new List<List<Token>>();
        var body = Hoist(t, ctes, names);
        // Names the statement's own WITH gives.
        if (body.Count > 0 && body[0].Is("WITH"))
        {
            var depth = 0;
            for (var i = 1; i < body.Count; i++)
            {
                if (body[i].IsSymbol("(")) depth++;
                else if (body[i].IsSymbol(")")) depth--;
                else if (depth == 0 && body[i].Type == T.Word && (body[i - 1].Is("WITH") || body[i - 1].IsSymbol(",")) && !body[i].Is("RECURSIVE")) names.Add(body[i].Text);
                else if (depth == 0 && body[i].IsAny("SELECT", "INSERT", "UPDATE", "DELETE", "VALUES")) break;
            }
        }
        if (ctes.Count == 0) return body;
        notes.Add("a derived table's column names become a common table expression");
        var list = new List<Token>();
        foreach (var cte in ctes)
        {
            if (list.Count > 0) list.Add(new Token(T.Symbol, ",", false));
            list.AddRange(cte);
        }
        if (body[0].Is("WITH")) return [body[0], .. list, new Token(T.Symbol, ",", false), .. body.Skip(1)];
        return [new Token(T.Word, "WITH", false), .. list, body[0] with { Spaced = true }, .. body.Skip(1)];
    }

    private static List<Token> Hoist(List<Token> source, List<List<Token>> ctes, HashSet<string> names)
    {
        var t = source.ToList();
        for (var i = 0; i < t.Count; i++)
        {
            if (!t[i].IsSymbol("(") || i + 1 >= t.Count || !t[i + 1].IsAny("SELECT", "VALUES", "WITH")) continue;
            var close = MatchingParen(t, i);
            var j = close + 1;
            if (j < t.Count && t[j].Is("AS")) j++;
            if (j + 1 >= t.Count || t[j].Type != T.Word || !t[j + 1].IsSymbol("(") || t[j].IsAny("ON", "WHERE", "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "ORDER", "GROUP", "UNION", "EXCEPT", "INTERSECT"))
                continue;
            var columnsClose = MatchingParen(t, j + 1);
            var columns = t.GetRange(j + 2, columnsClose - j - 2);
            if (columns.Count == 0 || columns.Any(c => c.Type != T.Word && !c.IsSymbol(","))) continue;
            var inner = Hoist(t.GetRange(i + 1, close - i - 1), ctes, names);
            RowsOfValues(inner);
            var name = t[j];
            names.Add(name.Text);
            ctes.Add([name with { Spaced = true }, new Token(T.Symbol, "(", true), .. columns, new Token(T.Symbol, ")", false), new Token(T.Word, "AS"),
                new Token(T.Symbol, "(", true), .. inner, new Token(T.Symbol, ")", false)]);
            var spaced = t[i].Spaced;
            t.RemoveRange(i, columnsClose - i + 1);
            t.Insert(i, name with { Spaced = spaced });
        }
        return t;
    }

    /// <summary>Db2's VALUES 1, 2, 3 is three rows: VALUES (1), (2), (3).</summary>
    private static void RowsOfValues(List<Token> t)
    {
        for (var v = 0; v < t.Count; v++)
        {
            if (!t[v].Is("VALUES")) continue;
            var i = v + 1;
            while (i < t.Count)
            {
                var start = i;
                var depth = 0;
                while (i < t.Count)
                {
                    if (t[i].IsSymbol("(")) depth++;
                    else if (t[i].IsSymbol(")"))
                    {
                        if (depth == 0) break;
                        depth--;
                    }
                    else if (depth == 0 && (t[i].IsSymbol(",") || t[i].IsSymbol(";") || t[i].IsAny("INTO", "ORDER", "FETCH", "LIMIT", "UNION", "EXCEPT", "INTERSECT", "OFFSET")))
                        break;
                    i++;
                }
                if (i == start) break;
                var single = t[start].IsSymbol("(") && MatchingParen(t, start) == i - 1;
                if (!single)
                {
                    t.Insert(i, new Token(T.Symbol, ")", false));
                    t.Insert(start, new Token(T.Symbol, "(", t[start].Spaced));
                    t[start + 1] = t[start + 1] with { Spaced = false };
                    i += 2;
                }
                if (i < t.Count && t[i].IsSymbol(",")) i++;
                else break;
            }
            v = i;
        }
    }

    /// <summary>
    /// Date arithmetic with labeled durations, such as CURRENT DATE - 30 DAYS or DUEDATE + (n) DAYS,
    /// becomes qsim.add_duration(value, count, unit), which keeps a date a date.
    /// </summary>
    private static void DateArithmetic(List<Token> t, List<string> notes)
    {
        string? Unit(Token x) => x.Type != T.Word ? null : x.Text.ToUpperInvariant().TrimEnd('S') switch
        {
            "DAY" => "days",
            "MONTH" => "months",
            "YEAR" => "years",
            "HOUR" => "hours",
            "MINUTE" => "minutes",
            "SECOND" => "seconds",
            "MICROSECOND" => "microseconds",
            _ => null,
        };
        int OpenOf(int close)
        {
            var depth = 0;
            for (var k = close; k >= 0; k--)
            {
                if (t[k].IsSymbol(")")) depth++;
                else if (t[k].IsSymbol("(") && --depth == 0) return k;
            }
            return -1;
        }
        for (var u = 2; u < t.Count; u++)
        {
            if (Unit(t[u]) is not { } unit || u + 1 < t.Count && t[u + 1].IsSymbol("(")) continue;
            // The number of units: a number, host variable, column or parenthesized expression.
            var a = t[u - 1].IsSymbol(")") ? OpenOf(u - 1) : t[u - 1].Type is T.Number or T.Host or T.Word ? u - 1 : -1;
            if (a < 0) continue;
            if (t[u - 1].Type == T.Word && a >= 2 && t[a - 1].IsSymbol(".")) a -= 2;
            // A function's value: MOD(n, 40) DAYS.
            if (t[u - 1].IsSymbol(")") && a > 0 && t[a - 1].Type == T.Word && !t[a - 1].IsAny("AND", "OR", "NOT", "WHEN", "THEN", "ELSE", "SELECT", "WHERE", "ON", "IN")) a--;
            var s = a - 1;
            if (s < 1 || !(t[s].IsSymbol("+") || t[s].IsSymbol("-"))) continue;
            // What it's added to.
            var end = s - 1;
            int l;
            if (t[end].IsSymbol(")"))
            {
                l = OpenOf(end);
                if (l > 0 && t[l - 1].Type == T.Word && !t[l - 1].IsAny("AND", "OR", "NOT", "WHEN", "THEN", "ELSE", "SELECT", "WHERE", "ON", "IN")) l--;
            }
            else if (t[end].IsAny("DATE", "TIMESTAMP", "TIME") && end > 0 && t[end - 1].Is("CURRENT")) l = end - 1;
            else if (t[end].Type is T.Word or T.Host or T.String)
            {
                l = end;
                if (t[end].Type == T.Word && end >= 2 && t[end - 1].IsSymbol(".")) l = end - 2;
            }
            else continue;
            if (l < 0) continue;
            var left = t.GetRange(l, end - l + 1);
            var count = t.GetRange(a, u - a);
            List<Token> replacement =
            [
                new Token(T.Word, "qsim.add_duration", t[l].Spaced), new Token(T.Symbol, "(", false),
                left[0] with { Spaced = false }, .. left.Skip(1), new Token(T.Symbol, ",", false),
                .. (t[s].IsSymbol("-") ? new[] { new Token(T.Symbol, "-", true) } : []),
                new Token(T.Symbol, "(", !t[s].IsSymbol("-")), count[0] with { Spaced = false }, .. count.Skip(1), new Token(T.Symbol, ")", false),
                new Token(T.Symbol, ",", false), new Token(T.String, $"'{unit}'", true), new Token(T.Symbol, ")", false),
            ];
            t.RemoveRange(l, u - l + 1);
            t.InsertRange(l, replacement);
            if (!notes.Contains("date arithmetic with durations uses qsim.add_duration")) notes.Add("date arithmetic with durations uses qsim.add_duration");
            u = l;
        }
    }

    /// <summary>Whether the FROM at <paramref name="i"/> is inside TRIM(... FROM ...), SUBSTRING or EXTRACT, not a table list.</summary>
    private static bool InsideFunctionFrom(List<Token> t, int i)
    {
        var depth = 0;
        for (var k = i - 1; k > 0; k--)
        {
            if (t[k].IsSymbol(")")) depth++;
            else if (t[k].IsSymbol("("))
            {
                if (depth == 0) return t[k - 1].IsAny("TRIM", "SUBSTRING", "EXTRACT", "LTRIM", "RTRIM", "POSITION");
                depth--;
            }
            else if (depth == 0 && t[k].Is("SELECT")) return false;
        }
        return false;
    }

    /// <summary>Where a correlation name with no AS follows the table name at <paramref name="i"/>, if one does.</summary>
    private static int? AliasAfterTable(List<Token> t, int i)
    {
        if (i + 2 < t.Count && t[i + 1].Type == T.Symbol && t[i + 1].Text is "." or "/") i += 2;
        return i + 1 < t.Count && t[i + 1].Type == T.Word && !t[i + 1].IsAny("SET", "WHERE", "AS") ? i + 1 : null;
    }

    private static int MatchingParen(List<Token> t, int open)
    {
        var depth = 0;
        for (var i = open; i < t.Count; i++)
        {
            if (t[i].IsSymbol("(")) depth++;
            else if (t[i].IsSymbol(")") && --depth == 0) return i;
        }
        throw new Unsupported("a parenthesis isn't closed");
    }

    /// <summary>Where the column definition being written starts: after the last comma or opening parenthesis at the table's level.</summary>
    private static int ColumnStart(List<Token> result)
    {
        var depth = 0;
        for (var i = result.Count - 1; i >= 0; i--)
        {
            if (result[i].IsSymbol(")")) depth++;
            else if (result[i].IsSymbol("("))
            {
                if (depth == 0) return i + 1;
                depth--;
            }
            else if (result[i].IsSymbol(",") && depth == 0) return i + 1;
        }
        return 0;
    }

    /// <summary>The type of the column definition being written: the word after its name (after ADD COLUMN, in ALTER TABLE).</summary>
    private static string ColumnType(List<Token> result)
    {
        var start = ColumnStart(result);
        var column = result.FindIndex(start, x => x.Is("COLUMN"));
        if (column >= 0) start = column + 1;
        return start + 1 < result.Count ? result[start + 1].Text.ToUpperInvariant() : "";
    }

    private static string DefaultFor(string type) => type switch
    {
        "CHAR" or "CHARACTER" or "VARCHAR" or "CLOB" or "GRAPHIC" or "VARGRAPHIC" or "NCHAR" or "NVARCHAR" => "''",
        "DATE" => "CURRENT_DATE",
        "TIME" => "LOCALTIME(0)",
        "TIMESTAMP" => "LOCALTIMESTAMP",
        _ => "0",
    };
}
