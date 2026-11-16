// Reads free-form RPG source into statements, and a statement into tokens.

using System.Globalization;
using System.Text;

namespace SimI.Rpg;

internal enum StatementKind
{
    Code,

    /// <summary>EXEC SQL: <see cref="SourceStatement.Text"/> is the SQL after those words.</summary>
    Sql,

    /// <summary>A compiler directive, such as /COPY.</summary>
    Directive,

    /// <summary>A fixed-form specification, which isn't supported.</summary>
    Fixed,
}

/// <param name="Unended">The member ends before the statement's semicolon.</param>
/// <summary>
/// A statement of a member: its kind, text and first line. <see cref="Translated"/> is true for one
/// the fixed-form translation made, which can use Sim i's own operations (@MOVE, @CALL ...).
/// </summary>
internal sealed record SourceStatement(StatementKind Kind, string Text, int Line, bool Unended = false, bool Translated = false)
{
    /// <summary>A definition a fixed-form calculation makes (see <see cref="VarDecl.Calculated"/>).</summary>
    public bool Calculated { get; init; }
}

/// <summary>
/// What /COPY and /INCLUDE bring in. <see cref="Member"/> gives a member's source by library, file and
/// member (the first two null when not given), or null when there's no such member. <see cref="Stream"/>
/// gives a stream file's text and absolute path from its path, relative to the folder given (the folder of
/// the file that includes it), or null when there's no such file. <see cref="Directory"/> is the folder
/// of the source being compiled, when it's a stream file.
/// </summary>
internal sealed record RpgIncludes(
    Func<string?, string?, string, string?> Member,
    Func<string, string?, (string Text, string Path)?>? Stream = null,
    string? Directory = null);

/// <summary>Something Sim i's RPG doesn't support. The message says what.</summary>
internal class RpgUnsupported(string message, Severity severity = Severity.Unsupported) : Exception(message)
{
    public Severity Severity { get; } = severity;
}

/// <summary>Source that isn't RPG an IBM i's compiler takes. The message says what's wrong.</summary>
internal sealed class RpgSyntaxError(string message) : RpgUnsupported(message, Severity.Error);

internal static partial class RpgSource
{
    /// <summary>
    /// The statements of a member. With **FREE on its first line, a line is all code. Without it, code
    /// is in columns 8 to 80, and a line with a specification letter in column 6 is fixed-form.
    /// </summary>
    public static List<SourceStatement> Statements(string source)
    {
        var lines = source.Replace("\r\n", "\n").Split('\n');
        var fullyFree = lines.Length > 0 && lines[0].TrimEnd().Equals("**FREE", StringComparison.OrdinalIgnoreCase);
        var statements = new List<SourceStatement>();
        var text = new StringBuilder();
        var startLine = 0;
        var inString = false;
        for (var n = fullyFree ? 1 : 0; n < lines.Length; n++)
        {
            var line = lines[n];
            // Compile-time data (** in columns 1 and 2) ends the code.
            if (!fullyFree && !inString && line.StartsWith("**") && text.ToString().Trim().Length == 0)
            {
                statements.Add(new SourceStatement(StatementKind.Directive, line.Trim(), n + 1));
                break;
            }
            if (!fullyFree)
            {
                if (line.Length >= 7 && line[6] == '*' && !inString) continue;
                // A // comment starting in column 7 is a comment line too.
                if (line.Length >= 8 && line[6] == '/' && line[7] == '/' && !inString) continue;
                // Fixed-form SQL: C/EXEC SQL, its C+ lines, and C/END-EXEC.
                if (line.Length >= 15 && line[6] == '/' && !inString && text.ToString().Trim().Length == 0
                    && line[7..].TrimStart().StartsWith("EXEC SQL", StringComparison.OrdinalIgnoreCase))
                {
                    var sql = new StringBuilder(line[7..Math.Min(line.Length, 80)].Trim()[8..].Trim());
                    var start = n + 1;
                    while (n + 1 < lines.Length)
                    {
                        var next = lines[n + 1];
                        if (next.Length > 6 && next[6] == '*')
                        {
                            n++;
                            continue;
                        }
                        if (next.Length > 6 && next[6] == '+')
                        {
                            sql.Append(' ').Append(next.Length > 7 ? next[7..Math.Min(next.Length, 80)].Trim() : "");
                            n++;
                            continue;
                        }
                        if (next.Length > 7 && next[6] == '/' && next[7..].TrimStart().StartsWith("END-EXEC", StringComparison.OrdinalIgnoreCase)) n++;
                        break;
                    }
                    statements.Add(new SourceStatement(StatementKind.Sql, sql.ToString().Trim(), start));
                    continue;
                }
                // A directive starts in column 7: /COPY, /FREE, /EXEC SQL and the like, whatever is in column 6.
                if (line.Length > 7 && line[6] == '/' && char.IsLetter(line[7]) && !inString && text.ToString().Trim().Length == 0)
                {
                    statements.Add(new SourceStatement(StatementKind.Directive, line[6..Math.Min(line.Length, 80)].Trim(), n + 1));
                    continue;
                }
                // Columns 1 to 5 are the sequence area, which the compiler ignores; column 6 is the spec letter.
                if (line.Length >= 6 && "HFDCIOPhfdciop".Contains(line[5]) && !inString)
                {
                    // A line with only the spec letter is empty.
                    if (line.Length > 6 && line[6..Math.Min(line.Length, 80)].Trim().Length > 0)
                        statements.Add(new SourceStatement(StatementKind.Fixed, line.TrimEnd(), n + 1));
                    continue;
                }
                // A // comment starting in column 80 has only its first / in the code columns.
                var code = line.Length > 7 ? line[7..Math.Min(line.Length, 80)] : "";
                if (line.Length > 80 && line[79] == '/' && line[80] == '/' && !code[..^1].Contains("//")) code = code[..^1];
                line = code;
            }
            var trimmed = line.Trim();
            if (!inString && text.ToString().Trim().Length == 0)
            {
                // Compile-time data ends the code.
                if (line.StartsWith("**"))
                {
                    statements.Add(new SourceStatement(StatementKind.Directive, trimmed, n + 1));
                    break;
                }
                if (trimmed.Length > 1 && trimmed[0] == '/' && char.IsLetter(trimmed[1]))
                {
                    statements.Add(new SourceStatement(StatementKind.Directive, trimmed, n + 1));
                    continue;
                }
            }
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (inString)
                {
                    text.Append(c);
                    if (c != '\'') continue;
                    if (i + 1 < line.Length && line[i + 1] == '\'') text.Append(line[++i]);
                    else inString = false;
                    continue;
                }
                if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') break;
                if (c == ';')
                {
                    Add(statements, text, startLine);
                    startLine = 0;
                    continue;
                }
                if (startLine == 0 && !char.IsWhiteSpace(c)) startLine = n + 1;
                if (c == '\'') inString = true;
                text.Append(c);
            }
            if (inString)
            {
                // A literal continues on the next line after + (from its first non-blank) or - (from its start).
                var soFar = text.ToString().TrimEnd();
                if (soFar.EndsWith('+') || soFar.EndsWith('-'))
                {
                    var plus = soFar.EndsWith('+');
                    text.Clear().Append(soFar[..^1]);
                    if (plus && n + 1 < lines.Length)
                    {
                        var next = lines[n + 1];
                        var body = fullyFree ? next : next.Length > 7 ? next[7..] : "";
                        lines[n + 1] = (fullyFree ? "" : new string(' ', 7)) + body.TrimStart();
                    }
                }
                else
                {
                    inString = false;
                }
            }
            else
            {
                text.Append('\n');
            }
        }
        if (text.ToString().Trim().Length > 0) Add(statements, text, startLine, unended: true);
        return statements;
    }

    private static void Add(List<SourceStatement> statements, StringBuilder text, int line, bool unended = false)
    {
        var statement = text.ToString().Trim();
        text.Clear();
        if (statement.Length == 0) return;
        var words = statement.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 2 && words[0].Equals("EXEC", StringComparison.OrdinalIgnoreCase) && words[1].Equals("SQL", StringComparison.OrdinalIgnoreCase))
            statements.Add(new SourceStatement(StatementKind.Sql, words.Length > 2 ? words[2] : "", line, unended));
        else statements.Add(new SourceStatement(StatementKind.Code, statement, line, unended));
    }
}

internal static partial class RpgSource
{
    /// <summary>
    /// The compile-time data after the code: each section's name (from **CTDATA name, or null for a
    /// plain **) and its records. Sections are the arrays with CTDATA, in order when they have no name.
    /// </summary>
    public static List<(string? Name, List<string> Records)> CompileTimeData(string source)
    {
        var sections = new List<(string?, List<string>)>();
        var lines = source.Replace("\r\n", "\n").Split('\n');
        var fullyFree = lines.Length > 0 && lines[0].TrimEnd().Equals("**FREE", StringComparison.OrdinalIgnoreCase);
        List<string>? current = null;
        for (var n = fullyFree ? 1 : 0; n < lines.Length; n++)
        {
            var line = lines[n];
            if (line.StartsWith("**") && !(n == 0 && fullyFree))
            {
                var words = line[2..].Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length > 0 && words[0].Equals("FREE", StringComparison.OrdinalIgnoreCase) && current is null) continue;
                var name = words.Length >= 2 && words[0].ToUpperInvariant() is "CTDATA" ? words[1].ToUpperInvariant() : null;
                current = [];
                sections.Add((name, current));
                continue;
            }
            current?.Add(line);
        }
        // A trailing empty line is the file's end, not a record.
        foreach (var (_, records) in sections)
        {
            while (records.Count > 0 && records[^1].Length == 0) records.RemoveAt(records.Count - 1);
        }
        return sections;
    }
}

internal enum TokenKind
{
    /// <summary>A name or operation code, upper case. Hyphenated ones (DCL-S, END-PROC) are one token.</summary>
    Name,
    Number,
    String,

    /// <summary>A special word, such as *ON or *INLR, upper case with its asterisk.</summary>
    Special,

    /// <summary>A built-in function, such as %TRIM, upper case with its percent sign.</summary>
    Bif,
    Op,
}

internal sealed record Token(TokenKind Kind, string Text, object? Value = null)
{
    public bool IsOp(string op) => Kind == TokenKind.Op && Text == op;
    public bool IsName(string name) => Kind == TokenKind.Name && Text == name;
}

internal static class RpgLexer
{
    private static readonly HashSet<string> Hyphenated = ["DCL", "END", "CTL", "ON", "EVAL", "XML", "DATA", "SND", "FOR", "WHEN"];
    private static readonly HashSet<string> BeforeSpecial = ["AND", "OR", "NOT", "TO", "DOWNTO", "BY"];

    public static List<Token> Tokens(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            var previous = tokens.Count > 0 ? tokens[^1] : null;
            if (c == '\'')
            {
                tokens.Add(new Token(TokenKind.String, "", ReadString(text, ref i)));
            }
            else if ((c is 'd' or 'D' or 't' or 'T' or 'z' or 'Z') && i + 1 < text.Length && text[i + 1] == '\''
                     && (i == 0 || !IsNameChar(text[i - 1])))
            {
                // Typed literals: d'2024-01-31', t'12.00.00', z'2024-01-31-12.00.00.000000'.
                i++;
                var literal = ReadString(text, ref i);
                try
                {
                    object value = char.ToUpperInvariant(c) switch
                    {
                        'D' => Values.ParseDate(literal, "*ISO"),
                        'T' => Values.ParseTime(literal),
                        _ => Values.ParseTimestamp(literal),
                    };
                    tokens.Add(new Token(TokenKind.String, "", value));
                }
                catch (RpgError)
                {
                    throw new RpgSyntaxError($"{c}'{literal}' isn't a valid date, time or timestamp");
                }
            }
            else if ((c is 'x' or 'X') && i + 1 < text.Length && text[i + 1] == '\'')
            {
                i++;
                var hex = ReadString(text, ref i);
                try
                {
                    tokens.Add(new Token(TokenKind.String, "", Ebcdic.Decode(Convert.FromHexString(hex))));
                }
                catch (FormatException)
                {
                    throw new RpgSyntaxError($"x'{hex}' isn't a hexadecimal literal");
                }
            }
            else if (char.IsDigit(c) || c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1]))
            {
                var start = i;
                var point = false;
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.' && !point && i + 1 < text.Length && char.IsDigit(text[i + 1])))
                {
                    if (text[i] == '.') point = true;
                    i++;
                }
                if (i < text.Length && text[i] == '.' && !point) i++;
                var number = text[start..i];
                if (i + 1 < text.Length && text[i] is 'e' or 'E' && (char.IsDigit(text[i + 1]) || text[i + 1] is '+' or '-'))
                {
                    // A floating-point literal: 1.5E3.
                    var exponent = ++i;
                    if (text[i] is '+' or '-') i++;
                    while (i < text.Length && char.IsDigit(text[i])) i++;
                    var literal = number + "E" + text[exponent..i];
                    tokens.Add(new Token(TokenKind.Number, literal, double.Parse(literal, CultureInfo.InvariantCulture)));
                    continue;
                }
                if (i < text.Length && (char.IsLetter(text[i]) || text[i] == '_')) throw new RpgSyntaxError($"{number}{text[i]}... isn't a number or a name");
                tokens.Add(new Token(TokenKind.Number, number, decimal.Parse(number.TrimEnd('.'), CultureInfo.InvariantCulture)));
            }
            else if (IsNameStart(c))
            {
                var start = i;
                while (i < text.Length && IsNameChar(text[i])) i++;
                var name = text[start..i].ToUpperInvariant();
                // DCL-S, END-DS, ON-ERROR and the like, at the start of a statement (or END-DS and END-PI at the end of one).
                while (i + 1 < text.Length && text[i] == '-' && char.IsLetter(text[i + 1]) && Hyphenated.Contains(name.Split('-')[0])
                       && (previous is null || name is "END"))
                {
                    var from = ++i;
                    while (i < text.Length && IsNameChar(text[i])) i++;
                    name += "-" + text[from..i].ToUpperInvariant();
                }
                tokens.Add(new Token(TokenKind.Name, name));
            }
            else if (c == '%' && i + 1 < text.Length && char.IsLetter(text[i + 1]))
            {
                var start = i++;
                while (i < text.Length && IsNameChar(text[i])) i++;
                tokens.Add(new Token(TokenKind.Bif, text[start..i].ToUpperInvariant()));
            }
            else if (c == '*' && i + 1 < text.Length && char.IsLetter(text[i + 1]) && SpecialFollows(tokens))
            {
                var start = i++;
                while (i < text.Length && IsNameChar(text[i])) i++;
                // A date or time format can end with its separator: *USA/, *ISO-, *DMY. or *HMS& (a blank).
                if (i < text.Length && text[i] is '/' or '-' or '.' or ',' or '&' && DateTimeFormats.Contains(text[start..i].ToUpperInvariant())) i++;
                // *HMS: (a colon, which is otherwise the separator of arguments) only where nothing follows it: TIMFMT(*HMS:).
                else if (i + 1 < text.Length && text[i] == ':' && text[i + 1] == ')' && text[start..i].ToUpperInvariant() is "*HMS" or "*JIS" or "*ISO" or "*EUR") i++;
                tokens.Add(new Token(TokenKind.Special, text[start..i].ToUpperInvariant()));
            }
            else
            {
                var two = i + 1 < text.Length ? text.Substring(i, 2) : "";
                if (i + 2 < text.Length && text.Substring(i, 3) == "**=")
                {
                    tokens.Add(new Token(TokenKind.Op, "**="));
                    i += 3;
                }
                else if (two is "<>" or "<=" or ">=" or "+=" or "-=" or "*=" or "/=" or "**")
                {
                    tokens.Add(new Token(TokenKind.Op, two));
                    i += 2;
                }
                else if ("+-*/=<>():,.".Contains(c))
                {
                    tokens.Add(new Token(TokenKind.Op, c.ToString()));
                    i++;
                }
                else
                {
                    throw new RpgSyntaxError($"the character {c} isn't expected here");
                }
            }
        }
        return tokens;
    }

    /// <summary>Whether an asterisk here starts a special word (*ON) rather than multiplying.</summary>
    private static bool SpecialFollows(List<Token> tokens)
    {
        if (tokens.Count == 0) return true;
        // After an operation code's extender: TEST(DE) *ISO.
        if (tokens is [{ Kind: TokenKind.Name }, { Text: "(" }, { Kind: TokenKind.Name }, { Text: ")" }]) return true;
        var previous = tokens[^1];
        return previous.Kind switch
        {
            TokenKind.Op => previous.Text != ")",
            TokenKind.Name => tokens.Count == 1 || BeforeSpecial.Contains(previous.Text),
            // IN *LOCK *DTAARA, CLEAR *NOKEY *ALL: a special word after the operation's own.
            TokenKind.Special => tokens.Count == 2 && tokens[0].Kind == TokenKind.Name,
            _ => false,
        };
    }

    private static readonly HashSet<string> DateTimeFormats =
        ["*MDY", "*DMY", "*YMD", "*JUL", "*ISO", "*USA", "*EUR", "*JIS", "*CYMD", "*CMDY", "*CDMY", "*LONGJUL", "*HMS"];

    private static bool IsNameStart(char c) => char.IsLetter(c) || c is '_' or '#' or '@' or '$';

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '#' or '@' or '$';

    private static string ReadString(string text, ref int i)
    {
        var value = new StringBuilder();
        i++;
        while (true)
        {
            if (i >= text.Length) throw new RpgSyntaxError("a literal isn't closed");
            if (text[i] == '\'')
            {
                if (i + 1 < text.Length && text[i + 1] == '\'')
                {
                    value.Append('\'');
                    i += 2;
                    continue;
                }
                i++;
                return value.ToString();
            }
            value.Append(text[i++]);
        }
    }
}
