// CL: programs (CLLE) and command definitions (CMD). A CL program is parsed into blocks of
// commands, and run by walking them: its own commands (DCL, CHGVAR, IF, SELECT, DO, CALL,
// CALLSUBR, MONMSG, SNDPGMMSG and the like) here, the rest by the machine, with the
// program's variables put in where & names them.

using System.Globalization;
using System.Text;
using SimI.Rpg;

namespace SimI.Cl;

/// <summary>An escape message that ended a CL program, or a command it ran that failed with no MONMSG for it.</summary>
internal sealed class ClEscape(string messageId, string message) : Exception(message)
{
    public string MessageId { get; } = messageId;
}

internal static class ClSource
{
    /// <summary>
    /// The commands of CL source, each with its first line: comments taken out, and continued lines
    /// joined (after +, from the next line's first non-blank; after -, from its first column).
    /// </summary>
    public static List<(int Line, string Text)> Commands(string source)
    {
        var commands = new List<(int, string)>();
        var text = new StringBuilder();
        var start = 0;
        var inComment = false;
        var skipBlanks = false;
        var lines = source.Replace("\r\n", "\n").Split('\n');
        for (var n = 0; n < lines.Length; n++)
        {
            var line = new StringBuilder();
            var raw = lines[n];
            var quoted = text.ToString().Count(c => c == '\'') % 2 == 1;
            for (var i = 0; i < raw.Length; i++)
            {
                var c = raw[i];
                if (inComment)
                {
                    if (c == '*' && i + 1 < raw.Length && raw[i + 1] == '/')
                    {
                        inComment = false;
                        i++;
                    }
                    continue;
                }
                // A comment starts with /* except right after a name's character: LIB/*ALL is a name.
                if (!quoted && c == '/' && i + 1 < raw.Length && raw[i + 1] == '*' && (i == 0 || !(char.IsLetterOrDigit(raw[i - 1]) || raw[i - 1] is '#' or '@' or '$' or '_')))
                {
                    inComment = true;
                    i++;
                    continue;
                }
                if (c == '\'') quoted = !quoted;
                line.Append(c);
            }
            var piece = line.ToString().TrimEnd();
            if (skipBlanks) piece = piece.TrimStart();
            skipBlanks = false;
            if (piece.Trim().Length == 0 && text.Length == 0) continue;
            if (start == 0) start = n + 1;
            if (piece.EndsWith('+') || piece.EndsWith('-'))
            {
                skipBlanks = piece.EndsWith('+');
                text.Append(piece[..^1]);
                continue;
            }
            text.Append(piece);
            if (text.ToString().Trim().Length > 0) commands.Add((start, text.ToString().Trim()));
            text.Clear();
            start = 0;
        }
        if (text.ToString().Trim().Length > 0) commands.Add((start, text.ToString().Trim()));
        return commands;
    }
}

/// <summary>A command definition (CMD source): its parameters, as CRTCMD makes them.</summary>
internal sealed record CommandParameter(string Keyword, string Type, int Length, int Decimals, string? Default, bool Restricted,
    IReadOnlyList<string> Values, IReadOnlyList<string> Special, bool Required);

internal sealed record CommandDefinition(IReadOnlyList<CommandParameter> Parameters, IReadOnlyList<Diagnostic> Diagnostics)
{
    public static CommandDefinition Parse(string source)
    {
        var parameters = new List<CommandParameter>();
        var diagnostics = new List<Diagnostic>();
        foreach (var (line, text) in ClSource.Commands(source))
        {
            ClCommand command;
            try
            {
                command = ClCommand.Parse(text);
            }
            catch (FormatException e)
            {
                diagnostics.Add(new Diagnostic(line, Severity.Unsupported, e.Message));
                continue;
            }
            switch (command.Name)
            {
                case "CMD":
                    break;
                case "PARM":
                {
                    var keyword = command.Value("KWD", 0)?.ToUpperInvariant() ?? "";
                    var type = command.Value("TYPE", 1)?.ToUpperInvariant() ?? "*CHAR";
                    var lengths = (command.Value("LEN", 2) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var length = lengths.Length > 0 && int.TryParse(lengths[0], out var l) ? l : type switch { "*DEC" => 15, "*INT2" => 5, "*INT4" => 10, "*LGL" => 1, _ => 32 };
                    var decimals = lengths.Length > 1 && int.TryParse(lengths[1], out var d) ? d : type == "*DEC" ? 5 : 0;
                    var values = ClCommand.Values(command.Value("VALUES", -1) ?? "").Select(v => v.Quoted ? v.Text : v.Text.ToUpperInvariant()).ToList();
                    var special = ClCommand.Values(command.Value("SPCVAL", -1) ?? "").Select(v => v.Text.Trim('(', ')').Split(' ')[0].ToUpperInvariant()).ToList();
                    if (type is "*CMD" or "*X" or "*ZEROELEM" || type.StartsWith("ELEM", StringComparison.Ordinal) || type.StartsWith("QUAL", StringComparison.Ordinal)
                        || !type.StartsWith('*'))
                        diagnostics.Add(new Diagnostic(line, Severity.Unsupported, $"parameter {keyword}: TYPE({type}) isn't supported"));
                    var dft = command.Value("DFT", -1) is { } dv ? ClCommand.Values(dv) is [var (t, q)] ? (q ? t : t.ToUpperInvariant()) : dv : null;
                    parameters.Add(new CommandParameter(keyword, type, length, decimals, dft,
                        command.Value("RSTD", -1)?.ToUpperInvariant() == "*YES", values, special,
                        int.TryParse(command.Value("MIN", -1), out var min) && min > 0));
                    break;
                }
                case "ELEM" or "QUAL" or "DEP" or "PMTCTL":
                    diagnostics.Add(new Diagnostic(line, Severity.Unsupported, $"{command.Name} isn't supported in a command definition"));
                    break;
                default:
                    diagnostics.Add(new Diagnostic(line, Severity.Unsupported, $"{command.Name} isn't a command definition statement"));
                    break;
            }
        }
        return new CommandDefinition(parameters, diagnostics);
    }

    /// <summary>The values for the command processing program, one cell for each parameter, from the command as typed. Throws ClEscape for a value that isn't allowed.</summary>
    public List<Cell> Arguments(ClCommand command)
    {
        var cells = new List<Cell>();
        var known = Parameters.Select(p => p.Keyword).ToHashSet();
        foreach (var keyword in command.Keyed.Keys.Where(k => !known.Contains(k)))
            throw new ClEscape("CPD0043", $"Keyword {keyword} not valid for this command.");
        if (command.Positional.Count > Parameters.Count)
            throw new ClEscape("CPD0071", $"Number of positional parameters exceeds limit of {Parameters.Count}.");
        for (var i = 0; i < Parameters.Count; i++)
        {
            var parameter = Parameters[i];
            var given = command.Value(parameter.Keyword, i);
            string? text = null;
            if (given is not null)
            {
                var values = ClCommand.Values(given);
                text = values is [var (t, quoted)] ? (quoted ? t : t.ToUpperInvariant()) : given;
            }
            if (text is null && parameter.Required) throw new ClEscape("CPD0072", $"Parameter {parameter.Keyword} required.");
            text ??= parameter.Default ?? "";
            if (parameter.Restricted && text.Length > 0 && !parameter.Values.Contains(text) && !parameter.Special.Contains(text) && text != parameter.Default)
                throw new ClEscape("CPD0084", $"Value '{text}' for parameter {parameter.Keyword} not a valid value.");
            var cell = parameter.Type switch
            {
                "*DEC" => new Cell(RpgType.Number(parameter.Length, parameter.Decimals)),
                "*INT2" => new Cell(new RpgType(RpgKind.Int, 5)),
                "*INT4" => new Cell(new RpgType(RpgKind.Int, 10)),
                "*UINT2" => new Cell(new RpgType(RpgKind.Int, 5, Unsigned: true)),
                "*UINT4" => new Cell(new RpgType(RpgKind.Int, 10, Unsigned: true)),
                "*LGL" => new Cell(RpgType.Char(1)),
                _ => new Cell(RpgType.Char(parameter.Length)),
            };
            try
            {
                cell.Set(cell.Type.IsNumeric ? Values.ParseNumber(text) : parameter.Type == "*LGL" ? (text is "*YES" or "1" ? "1" : "0") : text);
            }
            catch (RpgError)
            {
                throw new ClEscape("CPD0078", $"Value '{text}' for parameter {parameter.Keyword} not a valid number.");
            }
            cells.Add(cell);
        }
        return cells;
    }
}

/// <summary>A CL program, parsed: its parameters, variables, commands and subroutines.</summary>
internal sealed partial class ClProgram
{
    private abstract record Node(int Line);

    /// <summary>A command, with the MONMSGs that follow it.</summary>
    private sealed record Command(int Line, string Text, ClCommand Parsed, List<Monitor> Monitors) : Node(Line);

    private sealed record Monitor(IReadOnlyList<string> Ids, Node? Exec);

    private sealed record Block(int Line, List<Node> Body) : Node(Line);

    /// <summary>A label (LABEL: before a command), where GOTO goes.</summary>
    private sealed record Label(int Line, string Name) : Node(Line);

    private sealed record If(int Line, string Condition, Node? Then, Node? Else) : Node(Line);

    private sealed record Select(int Line, List<(string Condition, Node? Then)> Whens, Node? Otherwise) : Node(Line);

    /// <summary>DOWHILE (<see cref="Until"/> false) or DOUNTIL.</summary>
    private sealed record Loop(int Line, string Condition, bool Until, Block Body) : Node(Line);

    private sealed record For(int Line, string Variable, string From, string To, string By, Block Body) : Node(Line);

    private sealed record Declare(string Name, string Type, int Length, int Decimals, string? Value);

    private readonly List<Declare> _declares = [];

    /// <summary>DCLPRCOPT BNDDIR: where CALLPRC looks for its procedures first.</summary>
    private readonly List<string> _bindingDirectories = [];
    private readonly List<string> _parameters = [];
    private readonly Dictionary<string, Block> _subroutines = [];
    private readonly List<Monitor> _global = [];
    private List<Node> _body = [];
    private List<(int Line, string Text)> _source = [];
    private int _next;

    public List<Diagnostic> Diagnostics { get; } = [];

    public static ClProgram Parse(string source)
    {
        var program = new ClProgram { _source = ClSource.Commands(source) };
        program._body = program.Statements(out _, "ENDPGM");
        return program;
    }

    private static readonly HashSet<string> Own =
    [
        "PGM", "ENDPGM", "DCL", "DCLF", "DCLPRCOPT", "COPYRIGHT", "IF", "ELSE", "DO", "ENDDO", "SELECT", "WHEN", "OTHERWISE", "ENDSELECT", "DOWHILE", "DOUNTIL",
        "DOFOR", "LEAVE", "ITERATE", "SUBR", "ENDSUBR", "RTNSUBR", "CALLSUBR", "MONMSG", "CHGVAR", "CALL", "CALLPRC", "SNDPGMMSG", "RETURN", "GOTO",
        "RCVF", "SNDF", "SNDRCVF", "RCVMSG", "RTVJOBA", "RTVSYSVAL", "RTVDTAARA", "CHGDTAARA",
    ];

    /// <summary>The labels before the command last taken (a label can be on a line of its own).</summary>
    private readonly List<(int Line, string Name)> _labels = [];

    private (int Line, string Text, ClCommand Command)? Take()
    {
        while (_next < _source.Count)
        {
            var (line, text) = _source[_next++];
            // A label: LABEL: COMMAND.
            var label = System.Text.RegularExpressions.Regex.Match(text, @"^([A-Za-z#$@][A-Za-z0-9#$@_]{0,9}):\s*");
            if (label.Success)
            {
                _labels.Add((line, label.Groups[1].Value.ToUpperInvariant()));
                text = text[label.Length..];
            }
            // ?COMMAND, and ?? or ?* before a keyword: prompting, which Sim i runs without.
            text = System.Text.RegularExpressions.Regex.Replace(text.TrimStart('?').TrimStart(), @"(?<=\s)\?[?*-]?(?=[A-Za-z])", "");
            if (text.Length == 0) continue;
            try
            {
                return (line, text, ClCommand.Parse(text));
            }
            catch (FormatException e)
            {
                Diagnostics.Add(new Diagnostic(line, Severity.Unsupported, e.Message));
            }
        }
        return null;
    }

    private (int Line, string Text, ClCommand Command)? Peek()
    {
        var at = _next;
        var labels = _labels.Count;
        var taken = Take();
        _next = at;
        _labels.RemoveRange(labels, _labels.Count - labels);
        return taken;
    }

    /// <summary>The labels the command just taken had, as nodes before it.</summary>
    private void Labels(List<Node> body)
    {
        foreach (var (line, name) in _labels) body.Add(new Label(line, name));
        _labels.Clear();
    }

    /// <summary>Commands up to one of <paramref name="ends"/>, which is taken.</summary>
    private List<Node> Statements(out string end, params string[] ends)
    {
        var body = new List<Node>();
        end = "";
        while (Take() is var (line, text, command))
        {
            Labels(body);
            if (ends.Contains(command.Name))
            {
                end = command.Name;
                return body;
            }
            switch (command.Name)
            {
                case "PGM":
                    _parameters.AddRange(ClCommand.Values(command.Value("PARM", 0) ?? "").Select(v => v.Text.ToUpperInvariant()));
                    continue;
                case "DCL":
                {
                    var name = command.Value("VAR", 0)?.ToUpperInvariant() ?? "";
                    var type = command.Value("TYPE", 1)?.ToUpperInvariant() ?? "*CHAR";
                    var lengths = (command.Value("LEN", 2) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    int.TryParse(lengths.FirstOrDefault(), out var length);
                    var decimals = lengths.Length > 1 && int.TryParse(lengths[1], out var d) ? d : 0;
                    if (type is not ("*CHAR" or "*DEC" or "*INT" or "*UINT" or "*LGL"))
                        Diagnostics.Add(new Diagnostic(line, Severity.Unsupported, $"{name}: TYPE({type}) isn't supported"));
                    _declares.Add(new Declare(name, type, length, decimals, command.Value("VALUE", 3)));
                    continue;
                }
                case "DCLF":
                    _files.Add(new DeclaredFile(line, (command.Value("FILE", 0) ?? "").Trim().ToUpperInvariant(), command.Value("OPNID", -1)?.Trim().ToUpperInvariant()));
                    continue;
                case "DCLPRCOPT":
                    if (command.Value("BNDDIR", -1) is { } directories)
                        _bindingDirectories.AddRange(ClCommand.Values(directories).Select(v => v.Text.ToUpperInvariant()));
                    continue;
                case "COPYRIGHT":
                    continue;
                case "MONMSG":
                {
                    var monitor = new Monitor(ClCommand.Values(command.Value("MSGID", 0) ?? "").Select(v => v.Text.ToUpperInvariant()).ToList(),
                        command.Value("EXEC", 2) is { } exec ? Inline(line, exec) : null);
                    if (body.LastOrDefault() is Command previous) previous.Monitors.Add(monitor);
                    else if (body.Count == 0 && _subroutines.Count == 0) _global.Add(monitor);
                    else Diagnostics.Add(new Diagnostic(line, Severity.Warning, "MONMSG doesn't follow a command it can monitor: it's ignored"));
                    continue;
                }
                case "SUBR":
                {
                    var name = command.Value("SUBR", 0)?.ToUpperInvariant() ?? "";
                    _subroutines[name] = new Block(line, Statements(out _, "ENDSUBR"));
                    continue;
                }
            }
            if (Make(line, text, command) is { } node) body.Add(node);
        }
        if (ends.Length > 0 && ends[0] != "ENDPGM") Diagnostics.Add(new Diagnostic(0, Severity.Unsupported, $"{ends[0]} is missing"));
        return body;
    }

    private Node? Make(int line, string text, ClCommand command)
    {
        switch (command.Name)
        {
            case "IF":
            {
                var then = command.Value("THEN", 1) is { } t ? Inline(line, t) : null;
                Node? otherwise = null;
                if (Peek() is { Command.Name: "ELSE" } next)
                {
                    Take();
                    otherwise = next.Command.Value("CMD", 0) is { } e ? Inline(next.Line, e) : null;
                }
                return new If(line, command.Value("COND", 0) ?? "", then, otherwise);
            }
            case "ELSE":
                Diagnostics.Add(new Diagnostic(line, Severity.Unsupported, "ELSE doesn't follow an IF"));
                return null;
            case "DO":
                return new Block(line, Statements(out _, "ENDDO"));
            case "DOWHILE" or "DOUNTIL":
                return new Loop(line, command.Value("COND", 0) ?? "", command.Name == "DOUNTIL", new Block(line, Statements(out _, "ENDDO")));
            case "DOFOR":
                return new For(line, command.Value("VAR", 0) ?? "", command.Value("FROM", 1) ?? "1", command.Value("TO", 2) ?? "1", command.Value("BY", 3) ?? "1",
                    new Block(line, Statements(out _, "ENDDO")));
            case "SELECT":
            {
                var whens = new List<(string, Node?)>();
                Node? otherwise = null;
                while (Take() is var (l, x, c))
                {
                    if (c.Name == "ENDSELECT") break;
                    if (c.Name == "WHEN") whens.Add((c.Value("COND", 0) ?? "", c.Value("THEN", 1) is { } t ? Inline(l, t) : null));
                    else if (c.Name == "OTHERWISE") otherwise = c.Value("CMD", 0) is { } o ? Inline(l, o) : null;
                    else Diagnostics.Add(new Diagnostic(l, Severity.Unsupported, $"{c.Name} isn't expected between SELECT and ENDSELECT"));
                }
                return new Select(line, whens, otherwise);
            }
            default:
                return new Command(line, text, command, []);
        }
    }

    /// <summary>The command in THEN(...), EXEC(...), CMD(...): DO takes the block that follows.</summary>
    private Node? Inline(int line, string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return null;
        try
        {
            var command = ClCommand.Parse(trimmed);
            return Make(line, trimmed, command);
        }
        catch (FormatException e)
        {
            Diagnostics.Add(new Diagnostic(line, Severity.Unsupported, e.Message));
            return null;
        }
    }

    // ---- running ----

    private enum Flow
    {
        Next,
        Leave,
        Iterate,
        ReturnFromSubroutine,
        Return,

        /// <summary>GOTO: to the label in <see cref="Run.Target"/>.</summary>
        Goto,
    }

    private sealed class Run(Machine machine, IJobHost host, int depth, string name)
    {
        public Machine Machine { get; } = machine;
        public IJobHost Host { get; } = host;
        public int Depth { get; } = depth;
        public string Name { get; } = name;
        public Dictionary<string, Cell> Variables { get; } = [];
        public int Line { get; set; }

        /// <summary>The label a GOTO goes to.</summary>
        public string? Target { get; set; }

        /// <summary>The escape messages MONMSG caught, the last last: what RCVMSG receives.</summary>
        public List<(string Id, string Text)> Caught { get; } = [];

        /// <summary>The files DCLF declares, open: by OPNID, or "" for the one with none.</summary>
        public Dictionary<string, OpenFile> Files { get; } = [];
    }

    /// <summary>Runs the program with these arguments, by reference. Throws RpgHalt when it ends with an escape message.</summary>
    public void Call(string name, IReadOnlyList<Cell> arguments, Machine machine, IJobHost host, int depth)
    {
        var run = new Run(machine, host, depth, name);
        try
        {
            foreach (var d in _declares)
            {
                var type = d.Type switch
                {
                    "*DEC" => RpgType.Number(d.Length > 0 ? d.Length : 15, d.Length > 0 ? d.Decimals : 5),
                    "*INT" => new RpgType(RpgKind.Int, d.Length switch { 2 => 5, 8 => 20, _ => 10 }),
                    "*UINT" => new RpgType(RpgKind.Int, d.Length switch { 2 => 5, 8 => 20, _ => 10 }, Unsigned: true),
                    "*LGL" => RpgType.Char(1),
                    _ => RpgType.Char(d.Length > 0 ? d.Length : 32),
                };
                var cell = new Cell(type);
                if (d.Type == "*LGL") cell.Set("0");
                if (d.Value is { } value) cell.Set(Literal(value, type));
                run.Variables[d.Name] = cell;
            }
            OpenFiles(run);
            if (arguments.Count > _parameters.Count) throw new ClEscape("CPD0172", $"Parameters passed on CALL do not match those required: {name} takes {_parameters.Count}.");
            var copyBack = new List<(Cell Argument, Cell Variable)>();
            for (var i = 0; i < arguments.Count; i++)
            {
                if (!run.Variables.TryGetValue(_parameters[i], out var variable)) throw new ClEscape("CPF0001", $"Parameter {_parameters[i]} isn't declared.");
                variable.Set(arguments[i].Type.IsNumeric == variable.Type.IsNumeric ? arguments[i].Get() : Values.ToText(arguments[i].Get()));
                copyBack.Add((arguments[i], variable));
            }
            try
            {
                if (Execute(_body, run) == Flow.Goto) throw new ClEscape("CPF0001", $"Label {run.Target} isn't in the program.");
            }
            finally
            {
                foreach (var (argument, variable) in copyBack) argument.Set(variable.Get());
            }
        }
        catch (ClEscape e)
        {
            throw new RpgHalt(e.Message, e.MessageId);
        }
        catch (RpgError e)
        {
            throw new RpgHalt($"{name} ended in error at line {run.Line}: {e.Message}", "CPF0001");
        }
    }

    private static object Literal(string text, RpgType type)
    {
        var values = ClCommand.Values(text);
        var value = values is [var (t, quoted)] ? (quoted ? t : t) : text;
        return type.IsNumeric ? Values.ParseNumber(value) : value;
    }

    /// <summary>
    /// Runs a list of commands, from its start or (with <paramref name="enter"/>) from a label in it
    /// or in one of its groups. A GOTO to a label here (or in a group here) goes there; one elsewhere goes out.
    /// </summary>
    private Flow Execute(List<Node> body, Run run, string? enter = null)
    {
        var i = 0;
        var target = enter;
        while (true)
        {
            Flow flow;
            if (target is not null)
            {
                var at = IndexOf(body, target);
                if (at < 0) return Flow.Goto;
                flow = body[at] is Label ? Flow.Next : ExecuteFrom(body[at], target, run);
                i = at + 1;
                target = null;
            }
            else
            {
                if (i >= body.Count) return Flow.Next;
                flow = Execute(body[i], run);
                i++;
            }
            if (flow == Flow.Goto)
            {
                if (IndexOf(body, run.Target!) < 0) return Flow.Goto;
                target = run.Target;
                continue;
            }
            if (flow != Flow.Next) return flow;
        }
    }

    /// <summary>Where a label is in a list: the label itself, or the group it's in; -1 when it isn't.</summary>
    private static int IndexOf(List<Node> body, string label) => body.FindIndex(n => Contains(n, label));

    private static bool Contains(Node? node, string label) => node switch
    {
        Label l => l.Name == label,
        Block b => b.Body.Any(n => Contains(n, label)),
        If i => Contains(i.Then, label) || Contains(i.Else, label),
        Select s => s.Whens.Any(w => Contains(w.Then, label)) || Contains(s.Otherwise, label),
        Loop l => Contains(l.Body, label),
        For f => Contains(f.Body, label),
        _ => false,
    };

    /// <summary>A group entered at a label inside it (by GOTO), then on as it would go.</summary>
    private Flow ExecuteFrom(Node node, string label, Run run)
    {
        switch (node)
        {
            case Block block:
                return Execute(block.Body, run, label);
            case If branch:
                return Contains(branch.Then, label) ? ExecuteFrom(branch.Then!, label, run) : ExecuteFrom(branch.Else!, label, run);
            case Select select:
            {
                var when = select.Whens.FirstOrDefault(w => Contains(w.Then, label));
                return ExecuteFrom(when.Then ?? select.Otherwise!, label, run);
            }
            case Loop loop:
            {
                var flow = Execute(loop.Body.Body, run, label);
                if (flow == Flow.Leave) return Flow.Next;
                if (flow is Flow.Return or Flow.ReturnFromSubroutine or Flow.Goto) return flow;
                if (loop.Until && Values.ToBool(Truth(Evaluate(loop.Condition, run)))) return Flow.Next;
                return Execute(loop, run);
            }
            case For loop:
            {
                var variable = Variable(loop.Variable, run);
                var flow = Execute(loop.Body.Body, run, label);
                if (flow == Flow.Leave) return Flow.Next;
                if (flow is Flow.Return or Flow.ReturnFromSubroutine or Flow.Goto) return flow;
                variable.Set(Values.ToDecimal(variable.Get()) + Values.ToDecimal(Evaluate(loop.By, run)));
                return ContinueFor(loop, variable, run);
            }
            default:
                return Flow.Next;
        }
    }

    private Flow Execute(Node? node, Run run)
    {
        if (node is null) return Flow.Next;
        run.Line = node.Line;
        if (node is not Block && node.Line > 0) run.Host.State.Profiler?.Line(node.Line);
        run.Host.Cancelled.ThrowIfCancellationRequested();
        switch (node)
        {
            case Block block:
                return Execute(block.Body, run);
            case If branch:
                return Values.ToBool(Truth(Evaluate(branch.Condition, run))) ? Execute(branch.Then, run) : Execute(branch.Else, run);
            case Select select:
                foreach (var (condition, then) in select.Whens)
                {
                    if (Values.ToBool(Truth(Evaluate(condition, run)))) return Execute(then, run);
                }
                return Execute(select.Otherwise, run);
            case Loop loop:
                while (true)
                {
                    if (!loop.Until && !Values.ToBool(Truth(Evaluate(loop.Condition, run)))) break;
                    var flow = Execute(loop.Body, run);
                    if (flow == Flow.Leave) break;
                    if (flow is Flow.Return or Flow.ReturnFromSubroutine or Flow.Goto) return flow;
                    if (loop.Until && Values.ToBool(Truth(Evaluate(loop.Condition, run)))) break;
                }
                return Flow.Next;
            case For loop:
            {
                var variable = Variable(loop.Variable, run);
                variable.Set(Evaluate(loop.From, run));
                return ContinueFor(loop, variable, run);
            }
            case Command command:
            {
                // The command is a bar of the profile; a MONITOR's EXEC runs after it, on its own line.
                var profiler = run.Host.State.Profiler;
                var mark = profiler?.Enter("command", command.Parsed.Name) ?? 0;
                try
                {
                    return RunCommand(command, run);
                }
                catch (ClEscape e)
                {
                    profiler?.Leave(mark);
                    var monitor = command.Monitors.Concat(_global).FirstOrDefault(m => m.Ids.Any(id => Matches(id, e.MessageId)));
                    if (monitor is null) throw;
                    run.Caught.Add((e.MessageId, e.Message));
                    return Execute(monitor.Exec, run);
                }
                finally
                {
                    profiler?.Leave(mark);
                }
            }
            default:
                return Flow.Next;
        }
    }

    private Flow ContinueFor(For loop, Cell variable, Run run)
    {
        var by = Values.ToDecimal(Evaluate(loop.By, run));
        while (by >= 0 ? Values.ToDecimal(variable.Get()) <= Values.ToDecimal(Evaluate(loop.To, run)) : Values.ToDecimal(variable.Get()) >= Values.ToDecimal(Evaluate(loop.To, run)))
        {
            var flow = Execute(loop.Body, run);
            if (flow == Flow.Leave) break;
            if (flow is Flow.Return or Flow.ReturnFromSubroutine or Flow.Goto) return flow;
            variable.Set(Values.ToDecimal(variable.Get()) + by);
        }
        return Flow.Next;
    }

    /// <summary>CALL's and CALLPRC's parameters: the variables themselves, and literals as CL passes them.</summary>
    private static List<Cell> Arguments(string parameters, Run run)
    {
        var arguments = new List<Cell>();
        foreach (var (text, quoted) in ClCommand.Values(parameters))
        {
            // CALLPRC's (&X *BYVAL) and the like: the variable.
            var value = !quoted && text.StartsWith('(') ? text.Trim('(', ')').Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "" : text;
            if (!quoted && value.StartsWith('&'))
            {
                arguments.Add(Variable(value, run));
                continue;
            }
            // A literal: characters at least 32 long, numbers packed (15 5), as CL passes them.
            var n = 0m;
            var number = !quoted && decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out n);
            var cell = new Cell(number ? RpgType.Number(15, 5) : RpgType.Char(Math.Max(value.Length, 32)));
            cell.Set(number ? n : quoted ? value : value.ToUpperInvariant());
            arguments.Add(cell);
        }
        return arguments;
    }

    /// <summary>Whether MONMSG's id catches the message: CPF0000 any CPF message, CPF2100 any CPF21xx, otherwise the id itself.</summary>
    private static bool Matches(string monitored, string id)
    {
        if (monitored.Length != 7 || id.Length != 7) return monitored == id;
        if (monitored[3..] == "0000") return id[..3] == monitored[..3];
        if (monitored[5..] == "00") return id[..5] == monitored[..5];
        return id == monitored;
    }

    private Flow RunCommand(Command command, Run run)
    {
        var c = command.Parsed;
        switch (c.Name)
        {
            case "CHGVAR":
            {
                var variable = Variable(c.Value("VAR", 0) ?? "", run);
                var value = Evaluate(c.Value("VALUE", 1) ?? "", run);
                variable.Set(variable.Type.IsNumeric ? value : value is bool b ? (b ? "1" : "0") : Values.ToText(value));
                return Flow.Next;
            }
            case "RCVF" or "SNDF" or "SNDRCVF" or "ENDRCV" or "WAIT":
                FileOperation(c, run);
                return Flow.Next;
            case "CLOSE":
            {
                var id = c.Value("OPNID", 0)?.Trim().ToUpperInvariant() ?? "";
                if (!run.Files.TryGetValue(id, out var open)) throw new ClEscape("CPF4520", $"No file open with identifier {id}.");
                open.Close();
                return Flow.Next;
            }
            case "RCVMSG":
            {
                // The last message caught (MSGTYPE *EXCP, *LAST or *ANY), or the last sent (*INFO and the like); blanks when there's none.
                var type = c.Value("MSGTYPE", -1)?.Trim().ToUpperInvariant() ?? "*ANY";
                (string Id, string Text) message = ("", "");
                if (type is "*EXCP" or "*LAST" or "*ANY" or "*NEXT" or "*FIRST" && run.Caught.Count > 0) message = run.Caught[^1];
                else if (type is "*INFO" or "*COMP" or "*DIAG" or "*ANY" or "*LAST" && run.Host.State.Messages.Count > 0) message = ("", run.Host.State.Messages[^1]);
                if (c.Value("RMV", -1)?.Trim().ToUpperInvariant() != "*NO" && run.Caught.Count > 0 && message == run.Caught[^1]) run.Caught.RemoveAt(run.Caught.Count - 1);
                void Put(string keyword, string value)
                {
                    if (c.Value(keyword, -1) is { } name && name.TrimStart().StartsWith('&')) Variable(name, run).Set(value);
                }
                Put("MSGID", message.Id);
                Put("MSG", message.Text);
                Put("MSGDTA", message.Text);
                Put("SECLVL", "");
                Put("MSGF", message.Id.Length > 0 ? "QCPFMSG" : "");
                Put("MSGFLIB", message.Id.Length > 0 ? "QSYS" : "");
                Put("SENDER", "");
                Put("KEYVAR", "");
                if (c.Value("SEV", -1) is { } sev && sev.TrimStart().StartsWith('&')) Variable(sev, run).Set(message.Id.Length > 0 ? 40m : 0m);
                if (c.Value("RTNTYPE", -1) is { } rtn && rtn.TrimStart().StartsWith('&')) Variable(rtn, run).Set(message.Id.Length > 0 ? "15" : "  ");
                return Flow.Next;
            }
            case "RMVMSG":
                // CLEAR(*ALL) and the like: the program messages a message subfile would show.
                run.Host.State.ProgramMessages.Clear();
                return Flow.Next;
            case "GOTO":
                run.Target = (c.Value("CMDLBL", 0) ?? throw new ClEscape("CPD0072", "Parameter CMDLBL required.")).Trim().ToUpperInvariant();
                return Flow.Goto;
            case "RTVDTAARA":
            {
                // DTAARA(name) or DTAARA(name (start length)) into RTNVAR.
                var target = c.Value("DTAARA", 0) ?? throw new ClEscape("CPD0072", "Parameter DTAARA required.");
                var parts = target.Replace("(", " ").Replace(")", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var area = parts[0].StartsWith('&') ? Values.ToText(Variable(parts[0], run).Get()).Trim() : parts[0];
                var value = run.Machine.ReadDataArea(area, run.Host) ?? throw new ClEscape("CPF1015", $"Data area {area.ToUpperInvariant()} not found.");
                var variable = Variable(c.Value("RTNVAR", 1) ?? throw new ClEscape("CPD0072", "Parameter RTNVAR required."), run);
                if (variable.Type.IsNumeric)
                {
                    variable.Set(value is decimal number ? number : Values.ParseNumber(Values.ToText(value)));
                    return Flow.Next;
                }
                var text = Values.ToText(value);
                if (parts.Length > 2 && int.TryParse(parts[1], out var start) && int.TryParse(parts[2], out var length) && start >= 1)
                    text = start - 1 >= text.Length ? "" : text.Substring(start - 1, Math.Min(length, text.Length - start + 1));
                variable.Set(text);
                return Flow.Next;
            }
            case "RTVJOBA":
                RetrieveJob(c, run);
                return Flow.Next;
            case "CVTDAT":
                ConvertDate(c, run);
                return Flow.Next;
            case "RTVMBRD":
                RetrieveMember(c, run);
                return Flow.Next;
            case "SNDUSRMSG":
            {
                // An inquiry: the user's reply (or with nobody to answer, as in batch, the default).
                var message = c.Value("MSG", 0) is { } m ? Values.ToText(Evaluate(m, run)).TrimEnd() : c.Value("MSGID", -1) ?? "";
                var allowed = c.Value("VALUES", -1) is { } v ? ClCommand.Values(v).Select(x => x.Text.ToUpperInvariant()).ToList() : [];
                var fallback = c.Value("DFT", -1) is { } d ? ClCommand.Values(d) is [var (dft, _)] ? dft : d : allowed.FirstOrDefault() ?? "";
                if (c.Value("MSGRPY", -1) is not { } reply)
                {
                    run.Host.State.Messages.Add(message);
                    return Flow.Next;
                }
                var target = Variable(reply, run);
                string? answer = null;
                while (true)
                {
                    answer = run.Host.Dsply(message, target.Type.Length > 0 ? target.Type.Length : 1)?.Trim().ToUpperInvariant();
                    if (answer is null || answer.Length == 0) answer = fallback;
                    if (allowed.Count == 0 || allowed.Contains(answer.ToUpperInvariant()) || run.Host.Workstation is null) break;
                }
                target.Set(target.Type.IsNumeric ? Values.ParseNumber(answer.Length > 0 ? answer : "0") : answer);
                return Flow.Next;
            }
            case "RTVSYSVAL":
            {
                var name = (c.Value("SYSVAL", 0) ?? "").Trim().ToUpperInvariant();
                var variable = Variable(c.Value("RTNVAR", 1) ?? throw new ClEscape("CPD0072", "Parameter RTNVAR required."), run);
                var now = DateTime.Now;
                object value = name switch
                {
                    "QDATE" => now.ToString("MMddyy", CultureInfo.InvariantCulture),
                    "QDAY" => now.ToString("dd", CultureInfo.InvariantCulture),
                    "QMONTH" => now.ToString("MM", CultureInfo.InvariantCulture),
                    "QYEAR" => now.ToString("yy", CultureInfo.InvariantCulture),
                    "QCENTURY" => now.Year >= 2000 ? "1" : "0",
                    "QTIME" => now.ToString("HHmmss", CultureInfo.InvariantCulture),
                    "QHOUR" => now.ToString("HH", CultureInfo.InvariantCulture),
                    "QMINUTE" => now.ToString("mm", CultureInfo.InvariantCulture),
                    "QSECOND" => now.ToString("ss", CultureInfo.InvariantCulture),
                    "QDATFMT" => "MDY",
                    "QDATSEP" => "/",
                    "QTIMSEP" => ":",
                    "QDECFMT" => " ",
                    "QDAYOFWEEK" => "*" + now.DayOfWeek.ToString().ToUpperInvariant()[..3],
                    "QSRLNBR" => "SIMI0001",
                    "QMODEL" => "SIM",
                    "QSYSNAME" or "QSYSLIBL" or "QUSRLIBL" => name == "QSYSNAME" ? "SIMI" : name == "QSYSLIBL" ? "QSYS QSYS2 QHLPSYS QUSRSYS" : "QGPL QTEMP",
                    _ => throw new ClEscape("CPF1028", $"System value {name} not found."),
                };
                variable.Set(variable.Type.IsNumeric ? Values.ParseNumber(Values.ToText(value)) : value);
                return Flow.Next;
            }
            case "LEAVE":
                return Flow.Leave;
            case "ITERATE":
                return Flow.Iterate;
            case "RETURN" or "ENDPGM":
                return Flow.Return;
            case "RTNSUBR":
                return Flow.ReturnFromSubroutine;
            case "CALLSUBR":
            {
                var name = c.Value("SUBR", 0)?.ToUpperInvariant() ?? "";
                if (!_subroutines.TryGetValue(name, out var subroutine)) throw new ClEscape("CPF0001", $"Subroutine {name} isn't in the program.");
                var flow = Execute(subroutine, run);
                return flow == Flow.Return ? Flow.Return : Flow.Next;
            }
            case "CALLPRC":
            {
                // A procedure a service program exports: the program's binding directories (DCLPRCOPT BNDDIR), then any.
                var procedure = Values.ToText(Literal(c.Value("PRC", 0) ?? throw new ClEscape("CPD0072", "Parameter PRC required."), RpgType.Char(256))).Trim();
                var arguments = Arguments(c.Value("PARM", 1) ?? "", run);
                var service = run.Machine.FindExport(procedure, _bindingDirectories, run.Host, "")
                    ?? throw new ClEscape("CPD0170", $"Procedure {procedure} not found: no service program exports it.");
                try
                {
                    var result = service.CallExported(procedure.ToUpperInvariant(), arguments, run.Host, run.Depth + 1);
                    if (c.Value("RTNVAL", 2) is { } returned && returned.Trim().StartsWith('&') && result is not null)
                    {
                        var target = Variable(returned, run);
                        target.Set(target.Type.IsNumeric ? Values.ToDecimal(result is bool flag ? (flag ? 1m : 0m) : result) : result is bool b ? (b ? "1" : "0") : Values.ToText(result));
                    }
                }
                catch (RpgHalt e)
                {
                    throw new ClEscape(e.MessageId ?? "CPF9999", e.Message);
                }
                catch (RpgError e)
                {
                    throw new ClEscape(e.MessageId ?? "CPF9999", e.Message);
                }
                return Flow.Next;
            }
            case "CALL":
            {
                var program = c.Value("PGM", 0) ?? throw new ClEscape("CPD0072", "Parameter PGM required.");
                var arguments = Arguments(c.Value("PARM", 1) ?? "", run);
                try
                {
                    run.Machine.Call(program, arguments, run.Host, run.Depth + 1);
                }
                catch (RpgHalt e)
                {
                    throw new ClEscape(e.MessageId ?? "CPF9999", e.Message);
                }
                return Flow.Next;
            }
            case "SNDPGMMSG":
            {
                var id = c.Value("MSGID", -1)?.ToUpperInvariant();
                var data = c.Value("MSGDTA", -1) is { } dta ? Values.ToText(Evaluate(dta, run)).TrimEnd() : "";
                // A message from a message file: its text, with the message data for &1.
                var described = id is not null && c.Value("MSGF", -1) is { } msgf
                    ? run.Machine.MessageTemplateText(id, Values.ToText(Substitute(msgf, run)).Trim(), data) : null;
                var text = c.Value("MSG", 0) is { } msg ? Values.ToText(Evaluate(msg, run)).TrimEnd()
                    : described is not null ? described
                    : id is "CPF9898" or "CPF9897" ? data + (data.EndsWith('.') ? "" : ".")
                    : data.Length > 0 ? data : id ?? "";
                var type = c.Value("MSGTYPE", -1)?.ToUpperInvariant() ?? "*INFO";
                // To the program's own message queue (TOPGMQ(*SAME)): what its display file's message subfile shows.
                if (c.Value("TOPGMQ", -1) is { } queue && (queue.Contains("*SAME", StringComparison.OrdinalIgnoreCase) || queue.Contains(run.Name, StringComparison.OrdinalIgnoreCase))
                    && type is not "*ESCAPE")
                {
                    run.Host.State.ProgramMessages.Add(("*", text));
                    return Flow.Next;
                }
                switch (type)
                {
                    case "*ESCAPE":
                        throw new ClEscape(id ?? "CPF9898", text);
                    case "*STATUS":
                        if (run.Host.Workstation is { } workstation)
                        {
                            workstation.Send(Stream5250.HostRecord(0x02, Stream5250.Join(Stream5250.WriteToDisplay(false,
                                Stream5250.Sba(24, 2), [Stream5250.Highlight], Stream5250.Text(text.Length > 77 ? text[..77] : text.PadRight(77))))));
                        }
                        return Flow.Next;
                    default:
                        run.Host.State.Messages.Add(text);
                        return Flow.Next;
                }
            }
            default:
            {
                var text = Substitute(command.Text, run);
                var result = run.Machine.Run(text, run.Host, run.Depth + 1);
                if (!result.Ok) throw new ClEscape(result.MessageId ?? (result.Known ? "CPF0001" : "CPD0030"), result.Message);
                return Flow.Next;
            }
        }
    }

    private static readonly Dictionary<string, string> DateFormats = new()
    {
        ["*MDY"] = "MMddyy", ["*DMY"] = "ddMMyy", ["*YMD"] = "yyMMdd", ["*JUL"] = "yyddd", ["*MDYY"] = "MMddyyyy", ["*DMYY"] = "ddMMyyyy",
        ["*YYMD"] = "yyyyMMdd", ["*CYMD"] = "cyyMMdd", ["*ISO"] = "yyyy-MM-dd", ["*USA"] = "MM/dd/yyyy", ["*EUR"] = "dd.MM.yyyy",
        ["*JIS"] = "yyyy-MM-dd", ["*LONGJUL"] = "yyyyddd",
    };

    /// <summary>
    /// CVTDAT: a date from one format to another (*MDY, *DMY, *YMD, *JUL, *MDYY, *DMYY, *YYMD, *CYMD,
    /// *ISO, *USA, *EUR, *JIS, *LONGJUL; *JOB and *SYSVAL are *MDY), with TOSEP's separator.
    /// </summary>
    private static void ConvertDate(ClCommand c, Run run)
    {
        string Format(string keyword, int position)
        {
            var format = c.Value(keyword, position)?.Trim().ToUpperInvariant() ?? "*JOB";
            return format is "*JOB" or "*SYSVAL" ? "*MDY" : format;
        }
        var text = Values.ToText(Evaluate(c.Value("DATE", 0) ?? throw new ClEscape("CPD0072", "Parameter DATE required."), run)).Trim();
        var from = Format("FROMFMT", 2);
        var to = Format("TOFMT", 3);
        if (!DateFormats.TryGetValue(from, out var fromPattern) || !DateFormats.TryGetValue(to, out var toPattern))
            throw new ClEscape("CPF0555", $"Date format {from} or {to} not valid.");
        // The date's digits, as the format has them (its separators, whatever they are, left out).
        var digits = new string(text.Where(char.IsDigit).ToArray());
        DateOnly date;
        try
        {
            var pattern = new string(fromPattern.Where(char.IsLetter).ToArray());
            if (pattern.StartsWith('c'))
            {
                digits = digits.PadLeft(7, '0');
                date = DateOnly.ParseExact((1900 + (digits[0] - '0') * 100 + int.Parse(digits[1..3], CultureInfo.InvariantCulture)).ToString("0000") + digits[3..], "yyyyMMdd", CultureInfo.InvariantCulture);
            }
            else if (pattern.Contains("ddd"))
            {
                var yearLength = pattern.StartsWith("yyyy") ? 4 : 2;
                digits = digits.PadLeft(yearLength + 3, '0');
                var year = int.Parse(digits[..yearLength], CultureInfo.InvariantCulture);
                if (yearLength == 2) year += year >= 40 ? 1900 : 2000;
                date = new DateOnly(year, 1, 1).AddDays(int.Parse(digits[yearLength..], CultureInfo.InvariantCulture) - 1);
            }
            else
            {
                var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
                culture.Calendar.TwoDigitYearMax = 2039;
                date = DateOnly.ParseExact(digits.PadLeft(pattern.Length, '0'), pattern, culture);
            }
        }
        catch (Exception e) when (e is FormatException or ArgumentOutOfRangeException)
        {
            throw new ClEscape("CPF0555", $"Date {text} not in specified format or date not valid.");
        }
        var separator = c.Value("TOSEP", 4)?.Trim().ToUpperInvariant() switch
        {
            null or "*JOB" or "*SYSVAL" => to is "*ISO" or "*USA" or "*EUR" or "*JIS" ? null : "/",
            "*NONE" => "",
            "*BLANK" => " ",
            var other => other.Trim('\''),
        };
        string result;
        if (toPattern.StartsWith('c')) result = ((date.Year - 1900) / 100).ToString(CultureInfo.InvariantCulture) + date.ToString("yyMMdd", CultureInfo.InvariantCulture);
        else if (toPattern.Contains("ddd")) result = (toPattern.StartsWith("yyyy") ? date.Year.ToString("0000") : (date.Year % 100).ToString("00")) + date.DayOfYear.ToString("000");
        else result = date.ToString(toPattern, CultureInfo.InvariantCulture);
        // Separators between the parts, for the formats that have them (TOSEP).
        if (separator is { } sep && to is not ("*ISO" or "*USA" or "*EUR" or "*JIS" or "*CYMD" or "*LONGJUL"))
        {
            var parts = to switch
            {
                "*JUL" => new[] { result[..2], result[2..] },
                "*MDYY" or "*DMYY" => [result[..2], result[2..4], result[4..]],
                "*YYMD" => [result[..4], result[4..6], result[6..]],
                _ => [result[..2], result[2..4], result[4..]],
            };
            result = string.Join(sep, parts);
        }
        var target = Variable(c.Value("TOVAR", 1) ?? throw new ClEscape("CPD0072", "Parameter TOVAR required."), run);
        target.Set(target.Type.IsNumeric ? Values.ParseNumber(new string(result.Where(char.IsDigit).ToArray())) : result);
    }

    /// <summary>RTVMBRD: a database file's member: its number of records (NBRCURRCD), file, library, and the like.</summary>
    private static void RetrieveMember(ClCommand c, Run run)
    {
        var name = c.Value("FILE", 0) ?? throw new ClEscape("CPD0072", "Parameter FILE required.");
        var file = run.Machine.FindDatabaseFile(Values.ToText(Substitute(name, run)).Trim(), run.Machine.DefaultLibrary(run.Host.User))
            ?? throw new ClEscape("CPF9812", $"File {name.ToUpperInvariant()} not found.");
        foreach (var (keyword, given) in c.Keyed)
        {
            if (!given.TrimStart().StartsWith('&')) continue;
            object value = keyword switch
            {
                "RTNLIB" => file.Library,
                "RTNSYSNAME" => "SIMI",
                "RTNMBR" => file.Name,
                "FILEATR" => "PF",
                "SRCTYPE" or "TEXT" or "SRCF" => "",
                "NBRCURRCD" => (decimal)run.Machine.CountRecords(file),
                "NBRDLTRCD" => 0m,
                "CRTDATE" or "CHGDATE" or "SAVDATE" or "RSTDATE" or "USEDATE" => "1" + DateTime.Now.ToString("yyMMddHHmmss", CultureInfo.InvariantCulture),
                "DTASPCSIZ" => 0m,
                _ => "",
            };
            var variable = Variable(given, run);
            variable.Set(variable.Type.IsNumeric ? Values.ToDecimal(value is decimal d ? d : Values.ParseNumber(Values.ToText(value) is { Length: > 0 } t ? t : "0")) : Values.ToText(value));
        }
    }

    /// <summary>RTVJOBA: the job's attributes into the variables its keywords name.</summary>
    private static void RetrieveJob(ClCommand c, Run run)
    {
        var state = run.Host.State;
        var now = DateTime.Now;
        var interactive = run.Host.Workstation is not null;
        foreach (var (keyword, given) in c.Keyed)
        {
            if (!given.TrimStart().StartsWith('&')) continue;
            object value = keyword switch
            {
                "JOB" => state.Name,
                "USER" or "CURUSER" => run.Host.User,
                "NBR" => state.Number,
                "TYPE" => interactive ? "1" : "0",
                "SUBTYPE" => " ",
                "CURLIB" => run.Machine.DefaultLibrary(run.Host.User),
                "USRLIBL" => string.Join(" ", state.LibraryList.Append("QGPL").Append("QTEMP")),
                "SYSLIBL" => "QSYS QSYS2 QHLPSYS QUSRSYS",
                "DATE" => now.ToString("MMddyy", CultureInfo.InvariantCulture),
                "CYMDDATE" => ((now.Year - 1900) / 100).ToString(CultureInfo.InvariantCulture) + now.ToString("yyMMdd", CultureInfo.InvariantCulture),
                "DATFMT" => "*MDY",
                "DATSEP" => "/",
                "TIMSEP" => ":",
                "OUTQ" or "PRTDEV" => "PRT01",
                "OUTQLIB" => "QGPL",
                "JOBD" => "QDFTJOBD",
                "JOBDLIB" => "QGPL",
                "LOGLVL" => "4",
                "INQMSGRPY" => "*RQD",
                "ENDSTS" => "0",
                "SWS" => "00000000",
                "PRTTXT" or "ACGCDE" => "",
                "JOBQ" => "QBATCH",
                "JOBQLIB" => "QGPL",
                "CCSID" => 37m,
                "LANGID" => "ENU",
                "CNTRYID" => "US",
                "CURLIBL" => run.Machine.DefaultLibrary(run.Host.User),
                _ => "",
            };
            var variable = Variable(given, run);
            variable.Set(variable.Type.IsNumeric ? Values.ParseNumber(Values.ToText(value) is { Length: > 0 } t ? t : "0") : Values.ToText(value));
        }
    }

    /// <summary>The command with each &amp;VARIABLE outside a quoted string replaced by its value.</summary>
    private static string Substitute(string text, Run run)
    {
        var result = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '\'') quoted = !quoted;
            if (quoted || ch != '&' || i + 1 >= text.Length || !(char.IsLetter(text[i + 1]) || text[i + 1] is '#' or '$' or '@'))
            {
                result.Append(ch);
                continue;
            }
            var start = i;
            i++;
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '#' or '$' or '@' or '_')) i++;
            var name = text[start..i].ToUpperInvariant();
            i--;
            if (!run.Variables.TryGetValue(name, out var cell)) throw new ClEscape("CPF0001", $"Variable {name} isn't declared.");
            var value = cell.Type.IsNumeric ? Values.NumberText(Values.ToDecimal(cell.Get())) : Values.ToText(cell.Get()).TrimEnd();
            if (value.Length == 0 || value.Any(c => c is ' ' or '\'' or '(' or ')')) value = "'" + value.Replace("'", "''") + "'";
            result.Append(value);
        }
        return result.ToString();
    }

    private static Cell Variable(string name, Run run)
    {
        var key = name.Trim().ToUpperInvariant();
        return run.Variables.TryGetValue(key, out var cell) ? cell : throw new ClEscape("CPF0001", $"Variable {key} isn't declared.");
    }

    private static object Truth(object value) => value is bool ? value : Values.ToText(value).Trim() == "1";

    // ---- expressions ----

    private sealed class Expression(string text, Run run)
    {
        private readonly List<string> _tokens = Tokens(text);
        private int _p;

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
                if (c == '\'')
                {
                    i++;
                    while (i < text.Length)
                    {
                        if (text[i] == '\'' && i + 1 < text.Length && text[i + 1] == '\'') i += 2;
                        else if (text[i] == '\'') break;
                        else i++;
                    }
                    tokens.Add(text[start..Math.Min(i + 1, text.Length)]);
                    i++;
                    continue;
                }
                if (c is '&' or '*' or '%' && i + 1 < text.Length && (char.IsLetter(text[i + 1]) || text[i + 1] is '#' or '$' or '@'))
                {
                    i++;
                    while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '#' or '$' or '@' or '_')) i++;
                    tokens.Add(text[start..i].ToUpperInvariant());
                    continue;
                }
                if (char.IsDigit(c) || c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1]))
                {
                    while (i < text.Length && (char.IsDigit(text[i]) || text[i] is '.' or ',')) i++;
                    tokens.Add(text[start..i]);
                    continue;
                }
                var two = i + 1 < text.Length ? text.Substring(i, 2) : "";
                if (two is "<>" or ">=" or "<=" or "¬=" or "¬<" or "¬>" or "||" or "|>" or "|<" or "*=")
                {
                    tokens.Add(two);
                    i += 2;
                    continue;
                }
                if (char.IsLetter(c))
                {
                    while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '#' or '$' or '@' or '/')) i++;
                    tokens.Add(text[start..i]);
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

        public object Parse()
        {
            var value = Or();
            if (Peek is { } extra) throw new ClEscape("CPF0001", $"{extra} isn't expected in the expression {text}.");
            return value;
        }

        private object Or()
        {
            var left = And();
            while (Take("*OR", "|")) left = Bool(left) | Bool(And());
            return left;
        }

        private object And()
        {
            var left = Not();
            while (Take("*AND", "&")) left = Bool(left) & Bool(Not());
            return left;
        }

        private object Not() => Take("*NOT", "¬") ? !Bool(Not()) : Relation();

        private object Relation()
        {
            var left = Concatenation();
            var op = Peek;
            int? compared = null;
            switch (op)
            {
                case "*EQ" or "=" or "*NE" or "<>" or "¬=" or "*GT" or ">" or "*LT" or "<" or "*GE" or ">=" or "*LE" or "<=" or "*NG" or "¬>" or "*NL" or "¬<":
                    _p++;
                    var right = Concatenation();
                    compared = Values.Compare(left is bool lb ? (lb ? "1" : "0") : left, right is bool rb ? (rb ? "1" : "0") : right);
                    break;
            }
            return compared is not { } c ? left : op switch
            {
                "*EQ" or "=" => c == 0,
                "*NE" or "<>" or "¬=" => c != 0,
                "*GT" or ">" => c > 0,
                "*LT" or "<" => c < 0,
                "*GE" or ">=" or "*NL" or "¬<" => c >= 0,
                _ => c <= 0,
            };
        }

        private object Concatenation()
        {
            var left = Additive();
            while (Peek is "*CAT" or "||" or "*BCAT" or "|>" or "*TCAT" or "|<")
            {
                var op = _tokens[_p++];
                var (a, b) = (Values.ToText(left), Values.ToText(Additive()));
                left = op switch
                {
                    "*CAT" or "||" => a + b,
                    "*TCAT" or "|<" => a.TrimEnd() + b,
                    _ => a.TrimEnd().Length == 0 ? b.TrimStart() : a.TrimEnd() + " " + b.TrimStart(),
                };
            }
            return left;
        }

        private object Additive()
        {
            var left = Multiplicative();
            while (Peek is "+" or "-")
            {
                var op = _tokens[_p++];
                var right = Values.ToDecimal(Multiplicative());
                left = op == "+" ? Values.ToDecimal(left) + right : Values.ToDecimal(left) - right;
            }
            return left;
        }

        private object Multiplicative()
        {
            var left = Unary();
            while (Peek is "*" or "/")
            {
                var op = _tokens[_p++];
                var right = Values.ToDecimal(Unary());
                if (op == "/" && right == 0) throw new ClEscape("MCH1211", "Attempt made to divide by zero.");
                left = op == "*" ? Values.ToDecimal(left) * right : Values.ToDecimal(left) / right;
            }
            return left;
        }

        private object Unary()
        {
            if (Take("-")) return -Values.ToDecimal(Unary());
            if (Take("+")) return Values.ToDecimal(Unary());
            return Primary();
        }

        private object Primary()
        {
            var t = Peek ?? throw new ClEscape("CPF0001", $"The expression {text} ends too soon.");
            _p++;
            if (t == "(")
            {
                var value = Or();
                Take(")");
                return value;
            }
            if (t.StartsWith('\'')) return t.Length >= 2 ? t[1..^1].Replace("''", "'") : "";
            if (t.StartsWith('&')) return Variable(t, run).Get() is var v && v is decimal or string ? v : Values.ToText(v);
            if (t.Length > 0 && (char.IsDigit(t[0]) || t[0] == '.')) return decimal.Parse(t.Replace(',', '.'), CultureInfo.InvariantCulture);
            if (t.StartsWith('%'))
            {
                var arguments = new List<object>();
                if (Take("("))
                {
                    while (Peek is not null && Peek != ")")
                    {
                        arguments.Add(Concatenation());
                        Take(",", ":");
                    }
                    Take(")");
                }
                string Arg(int i) => i < arguments.Count ? Values.ToText(arguments[i]) : "";
                switch (t)
                {
                    case "%SST" or "%SUBSTRING":
                    {
                        var s = Arg(0);
                        var start = (int)Values.ToDecimal(arguments[1]);
                        var length = arguments.Count > 2 ? (int)Values.ToDecimal(arguments[2]) : s.Length - start + 1;
                        if (start < 1 || length < 0 || start + length - 1 > s.Length) throw new ClEscape("CPF0001", "%SST is out of range.");
                        return s.Substring(start - 1, length);
                    }
                    case "%TRIM":
                        return Arg(0).Trim();
                    case "%TRIML":
                        return Arg(0).TrimStart();
                    case "%TRIMR":
                        return Arg(0).TrimEnd();
                    case "%UPPER":
                        return Arg(0).ToUpperInvariant();
                    case "%LOWER":
                        return Arg(0).ToLowerInvariant();
                    case "%LEN":
                        return (decimal)Arg(0).Length;
                    case "%CHAR":
                        return arguments.Count > 0 && arguments[0] is decimal d ? Values.NumberText(d) : Arg(0);
                    case "%DEC" or "%INT" or "%UINT":
                        return Values.ParseNumber(Arg(0));
                    case "%SCAN":
                        return (decimal)(Arg(1).IndexOf(Arg(0), StringComparison.Ordinal) + 1);
                    default:
                        throw new ClEscape("CPF0001", $"{t} isn't supported.");
                }
            }
            // A special value such as *YES, or a name: as characters.
            return t;
        }

        private static bool Bool(object v) => v is bool b ? b : Values.ToText(v).Trim() == "1";
    }

    private static object Evaluate(string text, Run run) => new Expression(text, run).Parse();
}
