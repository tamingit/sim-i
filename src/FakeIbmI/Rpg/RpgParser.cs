// Parses free-form RPG. What isn't RPG an IBM i's compiler takes is an error, so nothing is made from
// the member. What the fake can't run becomes a diagnostic and (where it's a statement) an
// UnsupportedStmt, so the rest of the program still runs.

namespace FakeIbmI.Rpg;

internal sealed class RpgParser
{
    private readonly List<SourceStatement> _statements;
    private readonly RpgProgram _program = new();
    private readonly Func<string, string?>? _prepareProblem;
    private readonly HashSet<string> _otherFiles = [];
    private readonly HashSet<string> _printers = [];
    private int _next;
    private List<Token> _t = [];
    private int _p;
    private int _line;
    private Procedure _scope;

    private readonly RpgIncludes? _include;

    private RpgParser(string source, Func<string, string?>? prepareProblem, RpgIncludes? include)
    {
        _include = include;
        _statements = Preprocess(RpgSource.Statements(source), [], 0, null, include?.Directory);
        _statements = FixedForm.Translate(_statements, _program, (line, severity, message) => _program.Diagnostics.Add(new Diagnostic(line, severity, message)));
        _program.CompileTimeData.AddRange(RpgSource.CompileTimeData(source));
        foreach (var condition in _program.OutputRecords.SelectMany(r => r.Fields.Select(f => f.Condition).Prepend(r.Condition)).OfType<string>().Distinct())
        {
            try
            {
                (_t, _p) = (RpgLexer.Tokens(condition), 0);
                _program.OutputConditions[condition] = Expression();
            }
            catch (RpgUnsupported e)
            {
                _program.Diagnostics.Add(new Diagnostic(0, e.Severity, $"O spec condition {condition}: {e.Message}"));
            }
        }
        _prepareProblem = prepareProblem;
        _scope = _program.Main;
    }

    /// <summary>An expression on its own, as a debugger's watch or a breakpoint's condition is: a name, a subfield, a comparison, a built-in function.</summary>
    public static Expr ParseExpression(string text)
    {
        var parser = new RpgParser("", null, null);
        (parser._t, parser._p) = (RpgLexer.Tokens(text), 0);
        if (parser.AtEnd) throw new FormatException("there's no expression");
        var expression = parser.Expression();
        if (!parser.AtEnd) throw new FormatException($"{text} isn't one expression");
        return expression;
    }

    /// <summary>
    /// Parses a member. <paramref name="prepareProblem"/> says why the database won't take a translated
    /// SQL statement (null when it will); without it, SQL is translated but not tried.
    /// <paramref name="include"/> finds the member /COPY and /INCLUDE name (library, file and member, the first two
    /// null when not given): its source, or null when there's no such member.
    /// </summary>
    public static RpgProgram Parse(string source, Func<string, string?>? prepareProblem = null, RpgIncludes? include = null)
    {
        RpgParser? parser = null;
        try
        {
            parser = new RpgParser(source, prepareProblem, include);
            parser._program.Main.Body = parser.Block().Body;
            return parser._program;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Something the fake didn't expect: the member can't be read, but the rest still can.
            var program = parser?._program ?? new RpgProgram();
            program.Diagnostics.Add(new Diagnostic(parser?._line ?? 0, Severity.Unsupported, $"the fake couldn't read this member: {e.GetType().Name}: {e.Message}"));
            return program;
        }
    }

    // ---- compiler directives ----

    /// <summary>
    /// The statements with the compiler directives done: /COPY and /INCLUDE bring in their member
    /// (its statements numbered as the directive's line), /DEFINE, /UNDEFINE, /IF DEFINED, /ELSEIF,
    /// /ELSE and /ENDIF keep or drop what's between them, and /EOF ends the member.
    /// </summary>
    private List<SourceStatement> Preprocess(List<SourceStatement> statements, HashSet<string> defines, int depth, int? lineAs, string? directory)
    {
        var result = new List<SourceStatement>();
        // Each /IF: whether the code around it is kept, whether its own branch is, and whether a branch was.
        var conditions = new Stack<(bool Outer, bool Active, bool Taken)>();
        bool Active() => conditions.Count == 0 || conditions.Peek().Active;
        void Problem(string message, int line) => _program.Diagnostics.Add(new Diagnostic(line, Severity.Error, message));
        foreach (var statement in statements)
        {
            var line = lineAs ?? statement.Line;
            if (statement.Kind != StatementKind.Directive)
            {
                if (Active()) result.Add(lineAs is null ? statement : statement with { Line = line });
                continue;
            }
            var parts = statement.Text.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            var name = parts[0].ToUpperInvariant();
            var argument = parts.Length > 1 ? parts[1].Trim() : "";
            switch (name)
            {
                case "/IF":
                {
                    var outer = Active();
                    var holds = Condition(argument, defines, line, Problem);
                    conditions.Push((outer, outer && holds, holds));
                    continue;
                }
                case "/ELSEIF":
                {
                    if (conditions.Count == 0)
                    {
                        Problem("/ELSEIF has no /IF", line);
                        continue;
                    }
                    var (outer, _, taken) = conditions.Pop();
                    var holds = !taken && Condition(argument, defines, line, Problem);
                    conditions.Push((outer, outer && holds, taken || holds));
                    continue;
                }
                case "/ELSE":
                {
                    if (conditions.Count == 0)
                    {
                        Problem("/ELSE has no /IF", line);
                        continue;
                    }
                    var (outer, _, taken) = conditions.Pop();
                    conditions.Push((outer, outer && !taken, true));
                    continue;
                }
                case "/ENDIF":
                    if (conditions.Count == 0) Problem("/ENDIF has no /IF", line);
                    else conditions.Pop();
                    continue;
            }
            if (!Active()) continue;
            switch (name)
            {
                case "/DEFINE":
                    defines.Add(argument.ToUpperInvariant());
                    continue;
                case "/UNDEFINE":
                    defines.Remove(argument.ToUpperInvariant());
                    continue;
                case "/EOF":
                    return result;
                case "/COPY" or "/INCLUDE":
                {
                    var (library, file, member) = IncludeName(argument);
                    if (depth >= 16)
                    {
                        Problem($"{name} goes more than 16 members deep", line);
                        continue;
                    }
                    if (StreamFile(argument))
                    {
                        var path = argument.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0].Trim('\'', '"');
                        if (_include?.Stream is not { } stream)
                        {
                            _program.Diagnostics.Add(new Diagnostic(line, Severity.Unsupported, $"{name} of a stream file ({path}) isn't supported here"));
                            continue;
                        }
                        if (stream(path, directory) is not { } found)
                        {
                            Problem($"{name} {path}: there's no such stream file", line);
                            continue;
                        }
                        result.AddRange(Preprocess(RpgSource.Statements(found.Text), defines, depth + 1, line, found.Path[..Math.Max(1, found.Path.LastIndexOf('/'))]));
                        continue;
                    }
                    if (member.Length == 0)
                    {
                        Problem($"{name} needs a member to copy", line);
                        continue;
                    }
                    var text = _include?.Member(library, file, member);
                    if (text is null)
                    {
                        if (_include is null) _program.Diagnostics.Add(new Diagnostic(line, Severity.Unsupported, $"{name} isn't supported here"));
                        else Problem($"{name} {argument}: there's no such member", line);
                        continue;
                    }
                    result.AddRange(Preprocess(RpgSource.Statements(text), defines, depth + 1, line, directory));
                    continue;
                }
                default:
                    result.Add(lineAs is null ? statement : statement with { Line = line });
                    continue;
            }
        }
        if (conditions.Count > 0) Problem("/ENDIF is missing", lineAs ?? statements.LastOrDefault()?.Line ?? 0);
        return result;
    }

    /// <summary>DEFINED(name) or NOT DEFINED(name).</summary>
    private bool Condition(string text, HashSet<string> defines, int line, Action<string, int> problem)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text, @"^(NOT\s+)?DEFINED\s*\(\s*([^)\s]+)\s*\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success)
        {
            // An IBM i's /IF has only DEFINED and NOT DEFINED, and (since 7.5) comparisons of *CRTBNDRPG and the like.
            if (text.TrimStart().StartsWith('*')) _program.Diagnostics.Add(new Diagnostic(line, Severity.Unsupported, $"/IF {text}: only DEFINED(name) and NOT DEFINED(name) are supported"));
            else problem($"/IF {text}: the condition has to be DEFINED(name) or NOT DEFINED(name)", line);
            return false;
        }
        return defines.Contains(m.Groups[2].Value.ToUpperInvariant()) != m.Groups[1].Success;
    }

    /// <summary>
    /// Whether a /COPY names a stream file in the IFS rather than a member: a quoted name, a path
    /// (starting with / or .), or a name with an extension and no comma.
    /// </summary>
    private static bool StreamFile(string text)
    {
        var name = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return name.StartsWith('\'') || name.StartsWith('"') || name.StartsWith('/') || name.StartsWith('.')
            || name.Contains('.') && !name.Contains(',');
    }

    /// <summary>The member a /COPY names: LIB/FILE,MEMBER, FILE,MEMBER or MEMBER (file QRPGLESRC).</summary>
    private static (string? Library, string? File, string Member) IncludeName(string text)
    {
        var name = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToUpperInvariant() ?? "";
        var comma = name.IndexOf(',');
        if (comma < 0) return (null, "QRPGLESRC", name);
        var (library, file) = Machine.Qualified(name[..comma]);
        return (library, file.Length == 0 ? "QRPGLESRC" : file, name[(comma + 1)..]);
    }

    // ---- built-in functions and operation codes ----

    internal static readonly HashSet<string> Bifs =
    [
        "%TRIM", "%TRIMR", "%TRIML", "%LEN", "%SUBST", "%SCAN", "%SCANRPL", "%REPLACE", "%CHAR", "%DEC", "%DECH", "%INT", "%INTH",
        "%UNS", "%UNSH", "%FLOAT", "%EDITC", "%XLATE", "%UPPER", "%LOWER", "%ABS", "%REM", "%DIV", "%SQRT", "%EOF", "%FOUND", "%ERROR", "%STATUS",
        "%ELEM", "%DATE", "%TIME", "%TIMESTAMP", "%DAYS", "%MONTHS", "%YEARS", "%HOURS", "%MINUTES", "%SECONDS", "%DIFF", "%SUBDT",
        "%PARMS", "%SIZE", "%LOOKUP", "%XFOOT", "%MIN", "%MAX", "%CHECK", "%CHECKR", "%CONCAT", "%LEFT", "%RIGHT", "%PASSED", "%OMITTED",
        "%EQUAL", "%DECPOS", "%MAXARR", "%MINARR",
        "%ADDR", "%ALLOC", "%REALLOC", "%STR", "%SCANR", "%EDITW", "%EDITFLT", "%CHARCOUNT", "%GRAPH", "%UCS2", "%CONCATARR",
        "%BITAND", "%BITOR", "%BITXOR", "%BITNOT", "%SUBARR", "%SPLIT", "%LIST", "%RANGE", "%LOOKUPLT", "%LOOKUPLE", "%LOOKUPGT",
        "%LOOKUPGE", "%HIVAL", "%LOVAL", "%PROC", "%PARMNUM", "%OPEN", "%SHTDN", "%MSECONDS", "%MSG", "%TARGET", "%OCCUR", "%TLOOKUP",
        "%TLOOKUPLT", "%TLOOKUPLE", "%TLOOKUPGT", "%TLOOKUPGE", "%KDS",
    ];

    /// <summary>Free-form operation codes the fake doesn't run, and what they are.</summary>
    private static readonly Dictionary<string, string> UnsupportedOps = new()
    {
        ["COMMIT"] = "commitment control", ["ROLBK"] = "commitment control",
        ["XML-INTO"] = "XML-INTO", ["XML-SAX"] = "XML-SAX", ["DATA-INTO"] = "DATA-INTO",
        ["DATA-GEN"] = "DATA-GEN",
        ["FORCE"] = "FORCE", ["POST"] = "POST", ["ACQ"] = "ACQ", ["REL"] = "REL", ["NEXT"] = "NEXT",
        ["DCL-ENUM"] = "DCL-ENUM",
    };

    private static readonly HashSet<string> DisplayOps =
        ["EXFMT", "WRITE", "READ", "READC", "CHAIN", "UPDATE", "SETLL", "SETGT", "READE", "READP", "READPE", "DELETE", "FEOD"];

    // ---- statements ----

    private void Report(Severity severity, string message, int? line = null) =>
        _program.Diagnostics.Add(new Diagnostic(line ?? _line, severity, message));

    /// <summary>Moves to the next statement and tokenizes it. False at the end of the member.</summary>
    private SourceStatement? _statement;

    private bool Advance(out SourceStatement statement)
    {
        statement = null!;
        if (_next >= _statements.Count) return false;
        statement = _statements[_next++];
        _statement = statement;
        _line = statement.Line;
        _p = 0;
        _t = [];
        if (statement.Unended) Report(Severity.Error, "the statement has no semicolon at its end");
        if (statement.Kind != StatementKind.Code) return true;
        try
        {
            _t = RpgLexer.Tokens(statement.Text);
            // Not RPG, and an IBM i's compiler rejects them, so the member doesn't compile. They're read as
            // the operation meant, so the rest of the member is checked as it would be with the fix.
            if (_t is [{ Kind: TokenKind.Name, Text: "ENDSEL" }, ..])
            {
                Report(Severity.Error, "ENDSEL isn't an RPG operation code: a SELECT ends with ENDSL");
                _t[0] = new Token(TokenKind.Name, "ENDSL");
            }
            if (_t is [{ Kind: TokenKind.Name, Text: "ITERATE" }])
            {
                Report(Severity.Error, "ITERATE isn't an RPG operation code (it's CL's): RPG's is ITER");
                _t[0] = new Token(TokenKind.Name, "ITER");
            }
        }
        catch (RpgUnsupported e)
        {
            Report(e.Severity, e.Message);
            _t = [new Token(TokenKind.Name, "*BAD", e.Message)];
        }
        return true;
    }

    /// <summary>
    /// Statements up to one that starts with one of <paramref name="ends"/>, which is left as the
    /// current statement, after its first word.
    /// </summary>
    private (List<Stmt> Body, string End) Block(params string[] ends)
    {
        var body = new List<Stmt>();
        var startLine = _line;
        while (Advance(out var statement))
        {
            if (statement.Kind == StatementKind.Code && _t.Count > 0 && _t[0].Kind == TokenKind.Name && ends.Contains(_t[0].Text))
            {
                _p = 1;
                return (body, _t[0].Text);
            }
            Stmt? parsed;
            try
            {
                parsed = statement.Kind switch
                {
                    StatementKind.Sql => Sql(statement),
                    StatementKind.Fixed => Fixed(statement),
                    StatementKind.Directive => Directive(statement),
                    _ => Statement(),
                };
            }
            catch (RpgUnsupported e)
            {
                Report(e.Severity, e.Message, statement.Line);
                parsed = new UnsupportedStmt(statement.Line, e.Message);
                // A declaration the fake can't make: its name is still the program's.
                if (e.Severity == Severity.Unsupported && _t is [{ Text: "DCL-S" or "DCL-C" or "DCL-DS" or "DCL-PR" or "DCL-F" }, { Kind: TokenKind.Name } declared, ..])
                    _program.UnsupportedNames.Add(declared.Text);
            }
            if (parsed is not null) body.Add(parsed);
        }
        if (ends.Length > 0) Report(Severity.Error, $"{ends[^1]} is missing for the block that starts here", startLine);
        return (body, "");
    }

    private Stmt? Fixed(SourceStatement statement)
    {
        const string message = "fixed-form specifications aren't supported: only free-form RPG";
        Report(Severity.Unsupported, message);
        return char.ToUpperInvariant(statement.Text[5]) == 'C' ? new UnsupportedStmt(statement.Line, message) : null;
    }

    private Stmt? Directive(SourceStatement statement)
    {
        var name = statement.Text.Split(' ', 2)[0].ToUpperInvariant();
        if (name is "/FREE" or "/END-FREE" or "/TITLE" or "/EJECT" or "/SPACE") return null;
        if (name.StartsWith("**")) return null;
        else if (RpgLanguage.Directives.Contains(name)) Report(Severity.Unsupported, $"the directive {name} isn't supported");
        else Report(Severity.Error, $"{name} isn't a compiler directive");
        return null;
    }

    private Stmt? Sql(SourceStatement statement)
    {
        if (_program.FirstSqlLine == 0) _program.FirstSqlLine = statement.Line;
        var translation = SqlTranslator.Translate(statement.Text);
        foreach (var note in translation.Notes) Report(Severity.Info, $"SQL: {note}");
        if (translation.Kind == SqlKind.Unsupported)
        {
            Report(Severity.Unsupported, $"SQL: {translation.Problem}");
        }
        else if (translation.Sql is { } sql && translation.Kind is not (SqlKind.Prepare or SqlKind.ExecuteImmediate) && _prepareProblem?.Invoke(sql) is { } problem)
        {
            if (problem.Contains("no such table") || problem.Contains("no such column"))
            {
                Report(Severity.Warning, $"SQL: {problem} (yet)");
            }
            else if (!problem.Contains("already exists") && !problem.Contains("values were supplied"))
            {
                Report(Severity.Unsupported, $"SQL: the database doesn't take this statement, even translated: {problem}");
                translation = new SqlTranslation(SqlKind.Unsupported, Problem: problem);
            }
        }
        if (translation.Kind == SqlKind.Select) Report(Severity.Warning, "SQL: a SELECT with no INTO returns nothing to the program");
        if (translation.Kind != SqlKind.DeclareCursor) return new SqlStmt(statement.Line, translation);
        _program.Cursors[translation.Cursor!] = translation;
        _program.CursorDeclarations[translation.Cursor!] = (statement.Line, _scope);
        return null;
    }

    // ---- token helpers ----

    private Token? Peek => _p < _t.Count ? _t[_p] : null;
    private bool AtEnd => _p >= _t.Count;
    private bool PeekOp(string op) => Peek is { } t && t.IsOp(op);
    private bool PeekName(string name) => Peek is { } t && t.IsName(name);

    private bool TakeOp(string op)
    {
        if (!PeekOp(op)) return false;
        _p++;
        return true;
    }

    private bool TakeName(string name)
    {
        if (!PeekName(name)) return false;
        _p++;
        return true;
    }

    private void ExpectOp(string op)
    {
        if (!TakeOp(op)) throw new RpgSyntaxError($"{op} is expected{(Peek is { } t ? $" before {Show(t)}" : " at the end of the statement")}");
    }

    private string ExpectName(string what)
    {
        if (Peek is { Kind: TokenKind.Name } t)
        {
            _p++;
            return t.Text;
        }
        throw new RpgSyntaxError($"{what} is expected{(Peek is { } other ? $", not {Show(other)}" : "")}");
    }

    private void ExpectEnd()
    {
        if (!AtEnd) throw new RpgSyntaxError($"{Show(Peek!)} isn't expected here");
    }

    private static string Show(Token t) => t.Kind == TokenKind.String ? $"'{t.Value}'" : t.Text;

    /// <summary>An operation extender, such as (E) or (H): its letters, or "" when there's none.</summary>
    private string Extender()
    {
        if (_p + 2 < _t.Count && _t[_p].IsOp("(") && _t[_p + 1].Kind == TokenKind.Name && _t[_p + 2].IsOp(")")
            && _t[_p + 1].Text.All(c => "HENRTMADPZ".Contains(c)) && _t[_p + 1].Text.Length <= 3)
        {
            var letters = _t[_p + 1].Text;
            _p += 3;
            return letters;
        }
        return "";
    }

    /// <summary>A condition or other expression of a block's first statement: when it can't be parsed, the block still is.</summary>
    private Expr Header(Func<Expr> parse)
    {
        try
        {
            var e = parse();
            ExpectEnd();
            return e;
        }
        catch (RpgUnsupported e)
        {
            Report(e.Severity, e.Message);
            return new UnsupportedExpr(e.Message);
        }
    }

    // ---- one statement ----

    private Stmt? Statement()
    {
        var line = _line;
        var first = _t[0];
        if (first.Kind != TokenKind.Name) return AssignOrCall(line, false, false);
        if (first.Text == "*BAD") return new UnsupportedStmt(line, (string)first.Value!);
        // No operation code starts with @: in the member's own code, @TIME = ... is a name like any other.
        if (first.Text.StartsWith('@') && _statement?.Translated != true) return AssignOrCall(line, false, false);
        _p = 1;
        switch (first.Text)
        {
            case "CTL-OPT":
                ControlOptions();
                return null;
            case "DCL-S":
            {
                var name = ExpectName("a name");
                var k = Keywords();
                if (k.Type.Keyword is null && k.Type.Like is null) throw new RpgSyntaxError($"{name} needs a type");
                _scope.Locals.Add(new VarDecl(line, name, k.Type, k.Inz) { Calculated = _statement?.Calculated == true });
                return null;
            }
            case "DCL-C":
            {
                var name = ExpectName("a name");
                var value = TakeName("CONST") ? Parenthesized() : Expression();
                ExpectEnd();
                _scope.Locals.Add(new ConstDecl(line, name, value));
                return null;
            }
            case "DCL-DS":
                DataStructure(line);
                return null;
            case "DCL-F":
                File(line);
                return null;
            case "DCL-PR":
                Prototype();
                return null;
            case "DCL-PI":
                Interface();
                return null;
            case "DCL-PROC":
            {
                var name = ExpectName("a procedure name");
                var outer = _scope;
                _scope = new Procedure(name) { Exported = TakeName("EXPORT"), Line = line };
                _program.Procedures[name] = _scope;
                var (procedureBody, procedureEnd) = Block("ON-EXIT", "END-PROC");
                _scope.Body = procedureBody;
                if (procedureEnd == "ON-EXIT")
                {
                    _scope.OnExitIndicator = Peek is { Kind: TokenKind.Name } indicator ? _t[_p++].Text : null;
                    _scope.OnExit = Block("END-PROC").Body;
                }
                _scope = outer;
                return null;
            }
            case "BEGSR":
            {
                var name = SubroutineName();
                _scope.Subroutines[name] = Block("ENDSR").Body;
                return null;
            }
            case "IF":
            {
                var branches = new List<(Expr, IReadOnlyList<Stmt>)>();
                List<Stmt>? otherwise = null;
                var condition = Header(Expression);
                while (true)
                {
                    var (body, end) = Block("ELSEIF", "ELSE", "ENDIF");
                    branches.Add((condition, body));
                    if (end == "ELSEIF")
                    {
                        condition = Header(Expression);
                        continue;
                    }
                    if (end == "ELSE") otherwise = Block("ENDIF").Body;
                    break;
                }
                return new IfStmt(line, branches, otherwise);
            }
            case "DOW" or "DOU":
            {
                var condition = Header(Expression);
                return new LoopStmt(line, condition, first.Text == "DOU", Block("ENDDO").Body);
            }
            case "FOR":
            {
                Expr variable = new UnsupportedExpr("");
                Expr? start = null, limit = null, by = null;
                var down = false;
                Header(() =>
                {
                    variable = Postfix();
                    if (TakeOp("=")) start = Expression();
                    while (!AtEnd)
                    {
                        if (TakeName("BY")) by = Expression();
                        else if (TakeName("TO")) limit = Expression();
                        else if (TakeName("DOWNTO"))
                        {
                            limit = Expression();
                            down = true;
                        }
                        else break;
                    }
                    return variable;
                });
                return new ForStmt(line, variable, start, limit, by, down, Block("ENDFOR").Body);
            }
            case "FOR-EACH":
            {
                Expr variable = new UnsupportedExpr(""), array = new UnsupportedExpr("");
                Header(() =>
                {
                    variable = Postfix();
                    if (!TakeName("IN")) throw new RpgSyntaxError("FOR-EACH needs IN and an array");
                    return array = Expression();
                });
                return new ForEachStmt(line, variable, array, Block("ENDFOR").Body);
            }
            case "SELECT" when !AtEnd:
            {
                // SELECT operand: WHEN-IS value and WHEN-IN list compare it.
                var operand = Header(Expression);
                var whens = new List<(Expr, IReadOnlyList<Stmt>)>();
                List<Stmt>? other = null;
                var end = Block("WHEN-IS", "WHEN-IN", "OTHER", "ENDSL").End;
                while (end is "WHEN-IS" or "WHEN-IN")
                {
                    var op = end == "WHEN-IS" ? "=" : "IN";
                    var value = Header(Expression);
                    (var body, end) = Block("WHEN-IS", "WHEN-IN", "OTHER", "ENDSL");
                    whens.Add((new BinaryExpr(op, operand, value), body));
                }
                if (end == "OTHER") other = Block("ENDSL").Body;
                return new SelectStmt(line, whens, other);
            }
            case "SELECT":
            {
                var whens = new List<(Expr, IReadOnlyList<Stmt>)>();
                List<Stmt>? other = null;
                var end = Block("WHEN", "OTHER", "ENDSL").End;
                while (end == "WHEN")
                {
                    var condition = Header(Expression);
                    (var body, end) = Block("WHEN", "OTHER", "ENDSL");
                    whens.Add((condition, body));
                }
                if (end == "OTHER") other = Block("ENDSL").Body;
                return new SelectStmt(line, whens, other);
            }
            case "MONITOR":
            {
                var handlers = new List<MonitorHandler>();
                var (body, end) = Block("ON-ERROR", "ON-EXCP", "ENDMON");
                while (end is "ON-ERROR" or "ON-EXCP")
                {
                    var statuses = new List<int>();
                    var ids = new List<string>();
                    var any = false;
                    var kind = end;
                    while (!AtEnd)
                    {
                        var t = _t[_p++];
                        if (kind == "ON-EXCP" && t.Kind == TokenKind.String) ids.Add(((string)t.Value!).Trim().ToUpperInvariant());
                        else if (kind == "ON-ERROR" && t.Kind == TokenKind.Number && (decimal)t.Value! is >= 0 and <= 99999 and var status && status == decimal.Truncate(status)) statuses.Add((int)status);
                        // *PROGRAM, *FILE and *ALL: any status.
                        else if (kind == "ON-ERROR" && t.Kind == TokenKind.Special && t.Text is "*PROGRAM" or "*FILE" or "*ALL") any = true;
                        else
                        {
                            Report(Severity.Error, kind == "ON-EXCP"
                                ? $"ON-EXCP takes message ids in quotes, separated by colons, not {Show(t)}"
                                : $"ON-ERROR takes status codes, *PROGRAM, *FILE or *ALL, separated by colons, not {Show(t)}");
                            break;
                        }
                        if (!AtEnd && !TakeOp(":"))
                        {
                            Report(Severity.Error, $"{kind}'s values are separated by colons, not {Show(Peek!)}");
                            break;
                        }
                    }
                    if (kind == "ON-EXCP" && ids.Count == 0) Report(Severity.Error, "ON-EXCP needs a message id");
                    (var handler, end) = Block("ON-ERROR", "ON-EXCP", "ENDMON");
                    handlers.Add(new MonitorHandler(statuses, ids, kind == "ON-ERROR" && (any || statuses.Count == 0), handler));
                }
                return new MonitorStmt(line, body, handlers);
            }
            case "SORTA":
            {
                var extender = Extender();
                var array = Postfix();
                ExpectEnd();
                return new SortaStmt(line, array, extender.Contains('D'));
            }
            case "EVAL-CORR":
            {
                Extender();
                var target = Postfix();
                ExpectOp("=");
                var source = Postfix();
                ExpectEnd();
                return new EvalCorrStmt(line, target, source);
            }
            case "TEST" when !PeekOp("=") && !PeekOp("."):
            {
                var extender = Extender();
                var format = Peek is { Kind: TokenKind.Special } f && _p + 1 < _t.Count ? _t[_p++].Text : null;
                var value = Postfix();
                ExpectEnd();
                if (!extender.Any(c => c is 'D' or 'T' or 'Z' or 'N')) throw new RpgUnsupported("TEST is supported with (D), (T), (Z) or (N)");
                return new TestStmt(line, extender, format, value);
            }
            case "IN" or "OUT" or "UNLOCK" when !PeekOp("=") && !PeekOp(".") && !PeekOp("(") && first.Text != "UNLOCK" || first.Text == "UNLOCK" && Peek is { Kind: TokenKind.Name or TokenKind.Special }:
            {
                Extender();
                if (Peek is { Kind: TokenKind.Special, Text: "*LOCK" }) _p++;
                var name = Peek is { Kind: TokenKind.Special, Text: "*DTAARA" } ? _t[_p++].Text : ExpectName("a data area variable");
                ExpectEnd();
                if (first.Text == "UNLOCK" && _program.Files.Any(f => f.Disk && f.Name == name)) return new DisplayStmt(line, "UNLOCK", name, null, false);
                return new DataAreaStmt(line, first.Text, name);
            }
            case "SND-MSG":
            {
                var type = Peek is { Kind: TokenKind.Special } t && t.Text is "*INFO" or "*ESCAPE" or "*COMP" or "*DIAG" or "*NOTIFY" or "*STATUS" ? _t[_p++].Text : "*INFO";
                var message = Expression();
                // %TARGET(*CALLER ...) and the like: the fake has one message queue a job.
                if (!AtEnd) Expression();
                ExpectEnd();
                return new SendMessageStmt(line, type, message);
            }
            case "LEAVE":
                ExpectEnd();
                return new JumpStmt(line, Signal.Leave);
            case "ITER":
                ExpectEnd();
                return new JumpStmt(line, Signal.Iter);
            case "LEAVESR":
                ExpectEnd();
                return new JumpStmt(line, Signal.LeaveSr);
            case "RETURN":
            {
                Extender();
                var value = AtEnd ? null : Expression();
                ExpectEnd();
                return new JumpStmt(line, Signal.Return, value);
            }
            case "EXSR":
            {
                var name = SubroutineName();
                ExpectEnd();
                return new ExsrStmt(line, name);
            }
            case "CALLP":
            {
                Extender();
                var call = Postfix();
                ExpectEnd();
                return new CallStmt(line, call is NameExpr ? new CallExpr(call, []) : call);
            }
            case "EVAL" or "EVALR":
            {
                var extender = Extender();
                return AssignOrCall(line, extender.Contains('H'), first.Text == "EVALR" || extender.Contains('R'));
            }
            case "DSPLY":
            {
                Extender();
                var message = AtEnd ? null : Expression();
                Expr? response = null;
                if (!AtEnd)
                {
                    // The second operand is a message queue, which there isn't; the third takes the reply.
                    var second = Expression();
                    response = AtEnd ? null : Postfix();
                    if (response is null && second is not Literal) response = second;
                }
                ExpectEnd();
                return new DsplyStmt(line, message, response);
            }
            case "CLEAR" or "RESET":
            {
                // CLEAR *NOKEY *ALL name; *IN65 and the like are what's cleared.
                while (Peek is { Kind: TokenKind.Special, Text: "*NOKEY" or "*ALL" }) _p++;
                var target = Postfix();
                ExpectEnd();
                return new ClearStmt(line, target);
            }
            case "DUMP":
                return null;
            // ---- what fixed-form operations become ----
            case "@ZADD":
            {
                var halfAdjust = Extender().Contains('H');
                var target = Postfix();
                ExpectOp("=");
                var value = Expression();
                ExpectEnd();
                return new AssignStmt(line, target, "=", value, halfAdjust, false, Truncate: true);
            }
            case "@MOVE" or "@MOVEL" or "@MOVEA":
            {
                var pad = Extender().Contains('P');
                var target = Postfix();
                ExpectOp("=");
                var source = Expression();
                string? format = null;
                if (TakeOp(":")) format = Peek is { Kind: TokenKind.Special } f ? _t[_p++].Text : Expression() is Literal { Value: string s } ? s : null;
                ExpectEnd();
                return new MoveStmt(line, first.Text[1..], target, source, pad, format);
            }
            case "@GOTO":
                return new GotoStmt(line, ExpectName("a TAG's name"));
            case "@TAG":
                return new TagStmt(line, ExpectName("a TAG's name"));
            case "@CALL":
            {
                var error = Extender().Contains('E');
                var program = Primary();
                var parameters = new List<(Expr, Expr?, Expr?)>();
                while (!AtEnd)
                {
                    var args = Arguments();
                    parameters.Add((args[0], args[1] is SpecialExpr { Name: "*OMIT" } ? null : args[1], args[2] is SpecialExpr { Name: "*OMIT" } ? null : args[2]));
                }
                return new CallProgramStmt(line, program, parameters, error);
            }
            case "@ENTRY":
                while (!AtEnd) _program.EntryParameters.Add(ExpectName("a parameter's field"));
                return null;
            case "@TIME":
                return new TimeStmt(line, Postfix());
            case "@TESTN" or "@TESTB" or "@BITON" or "@BITOFF":
            {
                var target = Postfix();
                var parts = new List<Expr?>();
                while (TakeOp(":")) parts.Add(Expression() is var e && e is SpecialExpr { Name: "*OMIT" } ? null : e);
                ExpectEnd();
                var bits = first.Text == "@TESTN" ? null : parts.FirstOrDefault();
                return new BitStmt(line, first.Text[1..], target, bits, first.Text == "@TESTN" ? parts : first.Text == "@TESTB" ? parts.Skip(1).ToList() : []);
            }
            case "@EXCEPT" or "EXCEPT" when !PeekOp("=") && !PeekOp("(") && !PeekOp("."):
            {
                var name = AtEnd ? null : ExpectName("an EXCEPT name");
                ExpectEnd();
                return new ExceptStmt(line, name);
            }
            case "@TOTAL":
                return new TotalStmt(line, Block("@ENDTOTAL").Body);
            case "DEALLOC":
            {
                // DEALLOC(N) sets the pointer to *NULL; the storage is the garbage collector's.
                var extender = Extender();
                var pointer = Postfix();
                ExpectEnd();
                return extender.Contains('N') ? new AssignStmt(line, pointer, "=", new SpecialExpr("*NULL"), false, false) : null;
            }
            case "ENDIF" or "ENDDO" or "ENDFOR" or "ENDSL" or "ENDMON" or "ENDSR" or "END-PROC" or "ELSE" or "ELSEIF" or "WHEN" or "OTHER"
                or "ON-ERROR" or "ON-EXCP" or "ON-EXIT" or "WHEN-IS" or "WHEN-IN" or "END-DS" or "END-PI" or "END-PR":
                throw new RpgSyntaxError($"{first.Text} has no block to end");
        }
        if (first.Text is "OPEN" or "CLOSE" && !PeekOp("=") && !PeekOp("."))
        {
            var extender = Extender();
            var file = Peek is { Kind: TokenKind.Special, Text: "*ALL" } ? "*ALL" : null;
            if (file is not null) _p++;
            file ??= ExpectName("a file name");
            ExpectEnd();
            if (file != "*ALL" && !_program.Files.Any(f => f.Name == file))
                throw _otherFiles.Contains(file) ? new RpgUnsupported($"{first.Text} {file}: SEQ and SPECIAL files aren't supported") : new RpgSyntaxError($"{file} isn't a file of this program");
            return new FileStmt(line, first.Text, file, extender.Contains('E'));
        }
        if (DisplayOps.Contains(first.Text) && !PeekOp("=") && !PeekOp("."))
        {
            var extender = Extender();
            // WRITE to a printer file: the line to print follows the file's name.
            if (first.Text == "WRITE" && Peek is { Kind: TokenKind.Name } printer && _printers.Contains(printer.Text))
            {
                _p++;
                var printed = AtEnd ? new Literal("") : Expression();
                ExpectEnd();
                return new PrintStmt(line, printer.Text, printed);
            }
            // The search argument: a value, a list of key values (k1 : k2), %KDS(ds), or *LOVAL and the like.
            List<Expr>? keys = null;
            var searches = first.Text is "CHAIN" or "SETLL" or "SETGT" or "READE" or "READPE";
            if (searches && !(first.Text is "READE" or "READPE" && Operands() < 2) || first.Text == "DELETE" && Operands() >= 2)
                keys = SearchArgument();
            var record = ExpectName("a file or record format name");
            Expr? data = null;
            List<string>? fields = null;
            if (!AtEnd)
            {
                if (first.Text == "UPDATE" && Peek is { Kind: TokenKind.Bif, Text: "%FIELDS" })
                {
                    _p++;
                    fields = [.. Arguments().Select(a => NameOf(a, "%FIELDS"))];
                }
                else data = Postfix();
            }
            ExpectEnd();
            if (_otherFiles.Contains(record)) throw new RpgUnsupported($"{first.Text} {record}: SEQ and SPECIAL files aren't supported");
            if (_program.Files.Count == 0) throw new RpgSyntaxError($"{first.Text} {record}: the program has no files");
            return new DisplayStmt(line, first.Text, record, keys?.FirstOrDefault(), extender.Contains('E'), keys, data, fields, extender.Contains('N'));
        }
        // A name that is an operation code, not a variable being assigned or a procedure called.
        var operation = !PeekOp("=") && !PeekOp("(") && !PeekOp(".") && !PeekOp("+=") && !PeekOp("-=") && !PeekOp("*=") && !PeekOp("/=");
        if (operation && UnsupportedOps.TryGetValue(first.Text, out var what))
            throw new RpgUnsupported($"{what} isn't supported ({first.Text})");
        if (operation && !AtEnd && RpgLanguage.FixedFormOnly.TryGetValue(first.Text, out var instead))
            throw new RpgSyntaxError($"{first.Text} is a fixed-form operation code, not free-form{(instead.Length > 0 ? $": {instead}" : "")}");
        _p = 0;
        return AssignOrCall(line, false, false);
    }

    /// <summary>How many operands are left in the statement: names, values or parenthesized groups side by side.</summary>
    private int Operands()
    {
        var count = 0;
        var depth = 0;
        Token? previous = null;
        for (var i = _p; i < _t.Count; i++)
        {
            var t = _t[i];
            var startsOperand = depth == 0 && t.Kind is TokenKind.Name or TokenKind.Bif or TokenKind.Special or TokenKind.Number or TokenKind.String || depth == 0 && t.IsOp("(") && previous is not { Kind: TokenKind.Name or TokenKind.Bif };
            if (startsOperand && !(previous is { Kind: TokenKind.Op } op && op.Text is not ")" && depth == 0)) count++;
            if (t.IsOp("(")) depth++;
            if (t.IsOp(")")) depth--;
            previous = t;
        }
        return count;
    }

    /// <summary>A search argument: (k1 : k2), %KDS(ds : n), or one value.</summary>
    private List<Expr> SearchArgument()
    {
        if (PeekOp("("))
        {
            // A list of key values, or one value in parentheses.
            var depth = 0;
            var list = false;
            for (var i = _p; i < _t.Count; i++)
            {
                if (_t[i].IsOp("(")) depth++;
                else if (_t[i].IsOp(")") && --depth == 0) break;
                else if (depth == 1 && _t[i].IsOp(":")) list = true;
            }
            if (list) return Arguments();
        }
        return [Expression()];
    }

    private Stmt AssignOrCall(int line, bool halfAdjust, bool right)
    {
        var target = Postfix();
        if (AtEnd)
        {
            return target switch
            {
                NameExpr => new CallStmt(line, new CallExpr(target, [])),
                CallExpr => new CallStmt(line, target),
                _ => throw new RpgSyntaxError("this isn't a statement"),
            };
        }
        var op = _t[_p];
        if (op.Kind == TokenKind.Op && op.Text is "=" or "+=" or "-=" or "*=" or "/=" or "**=")
        {
            _p++;
            var value = Expression();
            ExpectEnd();
            return new AssignStmt(line, target, op.Text, value, halfAdjust, right);
        }
        if (target is NameExpr n && _p == 1)
        {
            throw RpgLanguage.FreeFormOperations.Contains(n.Name)
                ? new RpgUnsupported($"the operation {n.Name} isn't supported")
                : new RpgSyntaxError($"{n.Name} isn't an operation code{(RpgLanguage.FixedFormOnly.ContainsKey(n.Name) ? " in free-form RPG" : "")}");
        }
        throw new RpgSyntaxError($"{Show(op)} isn't expected here");
    }

    // ---- declarations ----

    private void ControlOptions()
    {
        while (!AtEnd)
        {
            var keyword = ExpectName("a keyword");
            var args = PeekOp("(") ? Arguments() : [];
            if (!RpgLanguage.ControlKeywords.Contains(keyword)) Report(Severity.Error, $"{keyword} isn't a CTL-OPT keyword");
            if (keyword == "MAIN" && args is [NameExpr main]) _program.MainProcedure = main.Name;
            if (keyword == "NOMAIN") _program.NoMain = true;
            if (keyword == "BNDDIR") _program.BindingDirectories.AddRange(args.OfType<Literal>().Select(a => Values.ToText(a.Value).Trim().ToUpperInvariant()));
            if (keyword == "DATFMT" && args is [SpecialExpr datfmt]) _program.DateFormat = datfmt.Name;
            if (keyword == "TIMFMT" && args is [SpecialExpr timfmt]) _program.TimeFormat = timfmt.Name;
        }
    }

    private sealed class KeywordSet
    {
        public TypeSpec Type { get; } = new();
        public Expr? Inz { get; set; }
        public int Position { get; set; }
        public bool Const { get; set; }
        public bool ByValue { get; set; }
        public bool HasProgram { get; set; }
        public string? Program { get; set; }
        public bool Closed { get; set; }

        /// <summary>EXTPROC: the external procedure's name.</summary>
        public string? ExtProc { get; set; }

        /// <summary>OPTIONS: *NOPASS, *OMIT, *VARSIZE and the like.</summary>
        public List<string> Options { get; } = [];
    }

    private List<Expr> Arguments()
    {
        ExpectOp("(");
        var args = new List<Expr>();
        if (TakeOp(")")) return args;
        // name(*): every element, as in SORTA ds(*).key.
        if (PeekOp("*") && _p + 1 < _t.Count && _t[_p + 1].IsOp(")"))
        {
            _p += 2;
            return [new SpecialExpr("*")];
        }
        do
        {
            args.Add(Expression());
        }
        while (TakeOp(":"));
        ExpectOp(")");
        return args;
    }

    private Expr Parenthesized()
    {
        ExpectOp("(");
        var e = Expression();
        ExpectOp(")");
        return e;
    }

    private static string NameOf(Expr e, string keyword) => e switch
    {
        NameExpr n => n.Name,
        Literal { Value: string s } => s.Trim().ToUpperInvariant(),
        _ => throw new RpgSyntaxError($"{keyword} needs a name"),
    };

    /// <summary>The type and other keywords of a declaration, to the end of the statement (or END-DS, END-PI, END-PR).</summary>
    private KeywordSet Keywords()
    {
        var k = new KeywordSet();
        while (!AtEnd)
        {
            var keyword = ExpectName("a keyword");
            if (keyword is "END-DS" or "END-PI" or "END-PR")
            {
                k.Closed = true;
                if (Peek is { Kind: TokenKind.Name }) _p++;
                ExpectEnd();
                break;
            }
            var args = PeekOp("(") ? Arguments() : null;
            switch (keyword)
            {
                case "CHAR" or "VARCHAR" or "PACKED" or "ZONED" or "BINDEC" or "INT" or "UNS" or "FLOAT":
                    if (args is null or []) throw new RpgSyntaxError($"{keyword} needs a length");
                    k.Type.Keyword = keyword;
                    k.Type.Length = args[0];
                    k.Type.Decimals = args.Count > 1 && keyword is "PACKED" or "ZONED" or "BINDEC" ? args[1] : null;
                    break;
                case "IND" or "DATE" or "TIME" or "TIMESTAMP":
                    k.Type.Keyword = keyword;
                    break;
                case "POINTER":
                    if (args is [SpecialExpr { Name: "*PROC" }]) throw new RpgUnsupported("procedure pointers (POINTER(*PROC)) aren't supported");
                    k.Type.Keyword = keyword;
                    break;
                case "OVERLAY":
                    if (args is not [NameExpr over, ..] || args.Count > 2) throw new RpgSyntaxError("OVERLAY needs a subfield name, and a position or *NEXT");
                    k.Type.Overlay = over.Name;
                    k.Type.OverlayAt = args.Count > 1 ? args[1] : null;
                    break;
                case "DTAARA":
                    // DTAARA, DTAARA(name), DTAARA(*LDA), DTAARA(*AUTO : name) and the like.
                    foreach (var arg in args ?? [])
                    {
                        if (arg is SpecialExpr { Name: "*AUTO" }) k.Type.DtaAraAuto = true;
                        else if (arg is SpecialExpr { Name: "*USRCTL" }) { }
                        else if (arg is SpecialExpr { Name: "*LDA" or "*PDA" or "*GDA" } area) k.Type.DtaAra = area.Name;
                        else k.Type.DtaAra = NameOf(arg, keyword);
                    }
                    k.Type.DtaAra ??= "";
                    break;
                case "BASED":
                    k.Type.Based = args is [NameExpr pointer] ? pointer.Name : throw new RpgSyntaxError("BASED needs a pointer's name");
                    break;
                case "UCS2" or "VARUCS2" or "GRAPH" or "VARGRAPH":
                    // Unicode and DBCS characters: kept as characters, a character a position.
                    if (args is null or []) throw new RpgSyntaxError($"{keyword} needs a length");
                    k.Type.Keyword = keyword.StartsWith("VAR") ? "VARCHAR" : "CHAR";
                    k.Type.Length = args[0];
                    break;
                case "OBJECT":
                    throw new RpgUnsupported($"the type {keyword} isn't supported");
                case "LIKE":
                    if (args is not [NameExpr like, ..] || args.Count > 2) throw new RpgSyntaxError("LIKE needs the name of what it's like");
                    k.Type.Like = like.Name;
                    k.Type.LikeAdjust = args.Count > 1 ? args[1] : null;
                    break;
                case "LIKEREC":
                    if (args is not [var record, ..]) throw new RpgSyntaxError("LIKEREC needs a record format's name");
                    k.Type.LikeRec = NameOf(record, keyword);
                    k.Type.FieldSet = args.Count > 1 && args[1] is SpecialExpr set ? set.Name : null;
                    k.Type.Keyword = "DS";
                    k.Type.Qualified = true;
                    break;
                case "PREFIX":
                    if (args is not [var prefix, ..]) throw new RpgSyntaxError("PREFIX needs a prefix");
                    k.Type.Prefix = prefix is Literal { Value: string p } ? p.Trim().ToUpperInvariant() : NameOf(prefix, keyword);
                    k.Type.PrefixReplace = args.Count > 1 && args[1] is Literal { Value: decimal n } ? (int)n : 0;
                    break;
                case "OCCURS":
                    k.Type.Occurs = args is [var occurs] ? occurs : throw new RpgSyntaxError("OCCURS needs a number");
                    break;
                case "CTDATA":
                    k.Type.CtData = true;
                    break;
                case "PERRCD":
                    k.Type.PerRecord = args is [var perRecord] ? perRecord : throw new RpgSyntaxError("PERRCD needs a number");
                    break;
                case "LIKEDS":
                    k.Type.LikeDs = NameOf(args is [var ds] ? ds : throw new RpgSyntaxError("LIKEDS needs a data structure name"), keyword);
                    k.Type.Keyword = "DS";
                    break;
                case "EXTNAME":
                    if (args is null or []) throw new RpgSyntaxError("EXTNAME needs a file name");
                    if (args is not [var table, ..] || args.Count > 3) throw new RpgSyntaxError("EXTNAME takes a file, a record format and *ALL, *INPUT, *OUTPUT or *KEY");
                    k.Type.FieldSet = args.Skip(1).OfType<SpecialExpr>().FirstOrDefault()?.Name;
                    k.Type.ExtName = NameOf(table, keyword);
                    k.Type.ExtName = k.Type.ExtName[(k.Type.ExtName.LastIndexOf('/') + 1)..];
                    break;
                case "DIM":
                    if (args is [SpecialExpr { Name: "*VAR" or "*AUTO" } varying, var maximum])
                    {
                        k.Type.Dim = maximum;
                        k.Type.VaryingDim = varying.Name == "*VAR" ? 'V' : 'A';
                    }
                    else k.Type.Dim = args is [var dim] ? dim : throw new RpgSyntaxError("DIM needs one number");
                    break;
                case "QUALIFIED":
                    k.Type.Qualified = true;
                    break;
                case "LEN":
                    k.Type.Length = args is [var length] ? length : throw new RpgSyntaxError("LEN needs one number");
                    break;
                case "PSDS":
                    k.Type.Psds = true;
                    break;
                case "VARYING":
                    k.Type.Keyword = "VARCHAR";
                    break;
                case "INZ":
                    k.Inz = args is [var inz and not SpecialExpr { Name: "*LIKEDS" }] ? inz : null;
                    break;
                case "POS":
                    if (args is [Literal { Value: decimal position }]) k.Position = (int)position;
                    else if (args is [var named]) k.Type.PositionExpr = named;
                    else throw new RpgSyntaxError("POS needs a number");
                    break;
                case "CONST":
                    k.Const = true;
                    break;
                case "VALUE":
                    k.ByValue = true;
                    break;
                case "EXTPGM":
                    k.HasProgram = true;
                    k.Program = args is [var program] ? NameOf(program, keyword) : null;
                    break;
                case "EXTPROC":
                    // EXTPROC('name'), EXTPROC(*DCLCASE), EXTPROC(*CL : 'name') and the like.
                    k.ExtProc = args switch
                    {
                        [Literal { Value: string name }] => name.Trim(),
                        [NameExpr name] => name.Name,
                        [SpecialExpr { Name: "*DCLCASE" }] => "*DCLCASE",
                        [SpecialExpr { Name: "*CL" or "*CWIDEN" or "*CNOWIDEN" }, Literal { Value: string name }] => name.Trim(),
                        [SpecialExpr { Name: "*JAVA" }, ..] => throw new RpgUnsupported("EXTPROC(*JAVA ...) isn't supported"),
                        _ => throw new RpgSyntaxError("EXTPROC needs a procedure's name"),
                    };
                    break;
                case "OPTIONS":
                    foreach (var option in args ?? []) k.Options.Add(option is SpecialExpr special ? special.Name : "");
                    break;
                case "STATIC" or "TEMPLATE" or "EXPORT" or "IMPORT" or "CCSID" or "ASCEND" or "DESCEND" or "OPDESC" or "RTNPARM" or "NOOPT"
                    or "ALIGN" or "DATFMT" or "TIMFMT" or "REQPROTO" or "DCL-PARM" or "DCL-SUBF":
                    break;
                default:
                    throw RpgLanguage.Types.Contains(keyword) || RpgLanguage.DefinitionKeywords.Contains(keyword)
                        ? new RpgUnsupported($"the keyword {keyword} isn't supported")
                        : new RpgSyntaxError($"{keyword} isn't a type or a definition keyword");
            }
        }
        return k;
    }

    /// <summary>The subfields or parameters that follow, up to <paramref name="end"/>.</summary>
    private static readonly Dictionary<string, string> PsdsSubfields = new()
    {
        ["*PROC"] = "CHAR(10) POS(1)", ["*STATUS"] = "ZONED(5 : 0) POS(11)", ["*ROUTINE"] = "CHAR(8) POS(29)", ["*PARMS"] = "ZONED(3 : 0) POS(37)",
    };

    private static readonly Dictionary<string, string> InfdsSubfields = new()
    {
        ["*FILE"] = "CHAR(8) POS(1)", ["*STATUS"] = "ZONED(5 : 0) POS(11)", ["*OPCODE"] = "CHAR(6) POS(16)", ["*ROUTINE"] = "CHAR(8) POS(22)",
        ["*RECORD"] = "CHAR(8) POS(38)", ["*SIZE"] = "ZONED(4 : 0) POS(67)", ["*INP"] = "ZONED(2 : 0) POS(71)", ["*OUT"] = "ZONED(2 : 0) POS(73)",
        ["*MODE"] = "ZONED(2 : 0) POS(75)",
    };

    private List<VarDecl> Entries(string end, bool psds = false)
    {
        var entries = new List<VarDecl>();
        while (true)
        {
            if (!Advance(out var statement))
            {
                Report(Severity.Error, $"{end} is missing");
                break;
            }
            if (statement.Kind != StatementKind.Code)
            {
                Report(statement.Kind == StatementKind.Fixed ? Severity.Unsupported : Severity.Error, $"only subfields or parameters are expected before {end}");
                continue;
            }
            if (_t[0].IsName(end)) break;
            var name = "";
            try
            {
                if (_t[0].IsName("DCL-PARM") || _t[0].IsName("DCL-SUBF")) _p = 1;
                name = Peek is { Kind: TokenKind.Special, Text: "*N" } ? "" : ExpectName("a name");
                if (name.Length == 0) _p++;
                // A status data structure's subfield named by its keyword (*PROC, *STATUS ...): its type and place.
                if (Peek is { Kind: TokenKind.Special } special && (psds ? PsdsSubfields : InfdsSubfields).TryGetValue(special.Text, out var meant))
                {
                    _t.RemoveAt(_p);
                    _t.InsertRange(_p, RpgLexer.Tokens(meant));
                }
                var line = _line;
                var k = Keywords();
                if (k.Type.Keyword is null && k.Type.Like is null) throw new RpgSyntaxError($"{(name.Length == 0 ? "*N" : name)} needs a type");
                entries.Add(new VarDecl(line, name, k.Type, k.Inz, k.Position, k.Const, k.ByValue, k.Options));
            }
            catch (RpgUnsupported e)
            {
                Report(e.Severity, e.Message);
                if (e.Severity != Severity.Unsupported) continue;
                _incomplete = true;
                if (name.Length > 0) _program.UnsupportedNames.Add(name);
            }
        }
        return entries;
    }

    /// <summary>The name after BEGSR or EXSR: a name, *INZSR or *PSSR.</summary>
    private string SubroutineName()
    {
        if (Peek is { Kind: TokenKind.Special, Text: "*INZSR" or "*PSSR" } special)
        {
            _p++;
            return special.Text;
        }
        return ExpectName("a subroutine name");
    }

    /// <summary>The name after DCL-DS, DCL-PI or DCL-PR: a name, or *N for none.</summary>
    private string DeclaredName()
    {
        if (Peek is { Kind: TokenKind.Special, Text: "*N" })
        {
            _p++;
            return "";
        }
        return ExpectName("a name");
    }

    /// <summary>The last subfields or parameters read had one the fake doesn't support.</summary>
    private bool _incomplete;

    /// <summary>
    /// The keywords of a DCL-DS, DCL-PR or DCL-PI. When one is wrong or not supported, it's reported and
    /// the declaration's subfields or parameters are still read (so they aren't taken for statements),
    /// their names kept as ones the fake couldn't declare; then it's null.
    /// </summary>
    private KeywordSet? DeclarationKeywords(string end)
    {
        try
        {
            return Keywords();
        }
        catch (RpgUnsupported e)
        {
            Report(e.Severity, e.Message);
            if (!_t.Any(t => t.IsName(end)))
            {
                foreach (var entry in Entries(end).Where(entry => entry.Name.Length > 0)) _program.UnsupportedNames.Add(entry.Name);
            }
            return null;
        }
    }

    private void DataStructure(int line)
    {
        _incomplete = false;
        var name = DeclaredName();
        if (DeclarationKeywords("END-DS") is not { } k)
        {
            if (name.Length > 0) _program.UnsupportedNames.Add(name);
            return;
        }
        k.Type.Keyword = "DS";
        // LIKEDS and LIKEREC structures have no subfields of their own, and no END-DS.
        if (k.Type.LikeDs is null && k.Type.LikeRec is null && !k.Closed) k.Type.Fields = Entries("END-DS", k.Type.Psds);
        if (k.Type.LikeDs is null && k.Type.LikeRec is null && k.Type.ExtName is null && k.Type.Fields is null or []) Report(Severity.Warning, $"data structure {name} has no subfields", line);
        _scope.Locals.Add(new VarDecl(line, name, k.Type, k.Inz));
        if (_incomplete && name.Length > 0) _program.UnsupportedNames.Add(name);
    }

    private void Interface()
    {
        _incomplete = false;
        DeclaredName();
        if (DeclarationKeywords("END-PI") is not { } k)
        {
            _program.UnsupportedNames.Add(_scope.Name);
            return;
        }
        if (k.Type.Keyword is not null || k.Type.Like is not null) _scope.Returns = k.Type;
        _scope.Params = k.Closed ? [] : Entries("END-PI");
        if (_incomplete) _program.UnsupportedNames.Add(_scope.Name);
    }

    private void Prototype()
    {
        _incomplete = false;
        var name = DeclaredName();
        if (DeclarationKeywords("END-PR") is not { } k)
        {
            _program.UnsupportedNames.Add(name);
            return;
        }
        var parameters = k.Closed ? [] : Entries("END-PR");
        if (_incomplete) _program.UnsupportedNames.Add(name);
        var returns = k.Type.Keyword is not null || k.Type.Like is not null ? k.Type : null;
        _program.Prototypes[name] = new ProtoDecl(name, k.HasProgram ? k.Program ?? name : null, parameters, returns, _line,
            k.ExtProc == "*DCLCASE" ? name : k.ExtProc);
    }

    private void File(int line)
    {
        var name = ExpectName("a file name");
        var workstation = false;
        var printerWidth = 0;
        var external = false;
        var userOpen = false;
        string? infds = null;
        string? overflow = null;
        string? indicators = null;
        var subfiles = new Dictionary<string, string>();
        var disk = false;
        var keyed = false;
        string? extFile = null;
        string? usage = null;
        string? prefix = null;
        var prefixReplace = 0;
        var renames = new Dictionary<string, string>();
        var primary = ' ';
        while (!AtEnd)
        {
            var keyword = ExpectName("a keyword");
            var args = PeekOp("(") ? Arguments() : [];
            switch (keyword)
            {
                case "WORKSTN":
                    workstation = true;
                    break;
                case "INDDS":
                    indicators = NameOf(args is [var ds] ? ds : throw new RpgSyntaxError("INDDS needs a data structure name"), keyword);
                    break;
                case "SFILE":
                    if (args is not [NameExpr record, NameExpr number]) throw new RpgSyntaxError("SFILE needs a record format and a field");
                    subfiles[record.Name] = number.Name;
                    break;
                case "PRINTER":
                    printerWidth = args is [Literal { Value: decimal width }] && width is >= 1 and <= 378 ? (int)width : 132;
                    // With no length, the file's records are a printer file's DDS; WRITE names them.
                    external = args.Count == 0;
                    _printers.Add(name);
                    break;
                case "USROPN":
                    userOpen = true;
                    break;
                case "INFDS":
                    infds = NameOf(args is [var info] ? info : throw new RpgSyntaxError("INFDS needs a data structure name"), keyword);
                    break;
                case "OFLIND":
                    overflow = args is [NameExpr flag] ? flag.Name : args is [SpecialExpr special] ? special.Name : throw new RpgSyntaxError("OFLIND needs an indicator");
                    break;
                case "DISK":
                    if (args is [Literal]) throw new RpgUnsupported($"program-described disk files (DISK(length)) aren't supported (file {name})");
                    disk = true;
                    break;
                case "SEQ" or "SPECIAL":
                    _otherFiles.Add(name);
                    throw new RpgUnsupported($"{keyword} files aren't supported (file {name})");
                case "KEYED":
                    keyed = true;
                    break;
                case "USAGE":
                    usage = string.Join(' ', args.OfType<SpecialExpr>().Select(a => a.Name));
                    break;
                case "EXTFILE" or "EXTDESC":
                    if (args is [SpecialExpr { Name: "*EXTDESC" }]) break;
                    if (args is [Literal { Value: string ext }]) extFile = ext.Trim().ToUpperInvariant();
                    else if (args is [NameExpr variable]) Report(Severity.Unsupported, $"{keyword} with a variable ({variable.Name}) isn't supported: name the file", line);
                    break;
                case "RENAME":
                    if (args is not [NameExpr externalName, NameExpr internalName]) throw new RpgSyntaxError("RENAME needs the record format and its new name");
                    renames[externalName.Name] = internalName.Name;
                    break;
                case "PREFIX":
                    if (args is not [var prefixed, ..]) throw new RpgSyntaxError("PREFIX needs a prefix");
                    prefix = prefixed is Literal { Value: string p } ? p.Trim().ToUpperInvariant() : NameOf(prefixed, keyword);
                    prefixReplace = args.Count > 1 && args[1] is Literal { Value: decimal r } ? (int)r : 0;
                    break;
                case "ALIAS" or "INFSR" or "MAXDEV" or "IGNORE" or "INCLUDE" or "COMMIT" or "BLOCK" or "STATIC" or "EXTMBR":
                    break;
                case "QUALIFIED" or "HANDLER":
                    Report(Severity.Unsupported, $"the file keyword {keyword} isn't supported", line);
                    break;
                case "@PRIMARY" or "@SECONDARY":
                    primary = keyword == "@PRIMARY" ? 'P' : 'S';
                    break;
                default:
                    if (RpgLanguage.FileKeywords.Contains(keyword)) Report(Severity.Unsupported, $"the file keyword {keyword} isn't supported", line);
                    else Report(Severity.Error, $"{keyword} isn't a file keyword", line);
                    break;
            }
        }
        // With no device, a file is a database file.
        if (!workstation && printerWidth == 0) disk = true;
        _program.Files.Add(new FileDecl(line, name, indicators, subfiles, printerWidth, external, userOpen, infds, overflow, disk, keyed,
            extFile, renames, prefix, prefixReplace, usage, primary));
    }

    // ---- expressions ----

    private Expr Expression() => Or();

    private Expr Or()
    {
        var left = And();
        while (TakeName("OR")) left = new BinaryExpr("OR", left, And());
        return left;
    }

    private Expr And()
    {
        var left = Not();
        while (TakeName("AND")) left = new BinaryExpr("AND", left, Not());
        return left;
    }

    private Expr Not() => TakeName("NOT") ? new UnaryExpr("NOT", Not()) : Comparison();

    private Expr Comparison()
    {
        var left = Additive();
        if (Peek is { Kind: TokenKind.Op } op && op.Text is "=" or "<>" or "<" or ">" or "<=" or ">=")
        {
            _p++;
            return new BinaryExpr(op.Text, left, Additive());
        }
        if (TakeName("IN")) return new BinaryExpr("IN", left, Additive());
        return left;
    }

    private Expr Additive()
    {
        var left = Multiplicative();
        while (Peek is { Kind: TokenKind.Op } op && op.Text is "+" or "-")
        {
            _p++;
            left = new BinaryExpr(op.Text, left, Multiplicative());
        }
        return left;
    }

    private Expr Multiplicative()
    {
        var left = Unary();
        while (Peek is { Kind: TokenKind.Op } op && op.Text is "*" or "/")
        {
            _p++;
            left = new BinaryExpr(op.Text, left, Unary());
        }
        return left;
    }

    private Expr Unary()
    {
        if (TakeOp("-")) return new UnaryExpr("-", Unary());
        if (TakeOp("+")) return Unary();
        var operand = Postfix();
        return TakeOp("**") ? new BinaryExpr("**", operand, Unary()) : operand;
    }

    private Expr Postfix()
    {
        var e = Primary();
        while (true)
        {
            if (PeekOp("(") && e is NameExpr or MemberExpr or SpecialExpr { Name: "*IN" })
            {
                e = new CallExpr(e, Arguments());
            }
            else if (PeekOp(".") && e is NameExpr or MemberExpr or CallExpr)
            {
                _p++;
                e = new MemberExpr(e, ExpectName("a subfield name"));
            }
            else
            {
                return e;
            }
        }
    }

    private Expr Primary()
    {
        if (Peek is not { } t) throw new RpgSyntaxError("the statement ends where a value is expected");
        _p++;
        switch (t.Kind)
        {
            case TokenKind.Number or TokenKind.String:
                return new Literal(t.Value!);
            case TokenKind.Name:
                return new NameExpr(t.Text);
            case TokenKind.Special:
                if (t.Text == "*ALL" && Peek is { Kind: TokenKind.String } all)
                {
                    _p++;
                    return new SpecialExpr("*ALL", (string)all.Value!);
                }
                return new SpecialExpr(t.Text);
            case TokenKind.Bif:
                if (!Bifs.Contains(t.Text))
                {
                    throw RpgLanguage.Bifs.Contains(t.Text)
                        ? new RpgUnsupported($"the built-in function {t.Text} isn't supported")
                        : new RpgSyntaxError($"{t.Text} isn't a built-in function");
                }
                return new BifExpr(t.Text, PeekOp("(") ? Arguments() : []);
            default:
                if (t.IsOp("("))
                {
                    var e = Expression();
                    ExpectOp(")");
                    return e;
                }
                throw new RpgSyntaxError($"{Show(t)} isn't expected here");
        }
    }
}
