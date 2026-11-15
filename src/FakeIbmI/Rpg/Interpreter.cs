// Runs a parsed RPG program by walking its statements. One Interpreter is one call of one
// program: nothing is kept between calls, as if every program ended with *INLR on.

namespace FakeIbmI.Rpg;

/// <summary>What a running program needs from the job it runs in.</summary>
internal interface IJobHost
{
    /// <summary>The job's user profile.</summary>
    string User { get; }

    /// <summary>Cancelled when the job ends, such as when its 5250 session closes.</summary>
    CancellationToken Cancelled { get; }

    /// <summary>The job's display, or null in a job with none.</summary>
    IWorkstation? Workstation { get; }

    /// <summary>What lasts for the job between programs: its library list, overrides, service programs and display.</summary>
    JobState State { get; }

    /// <summary>
    /// Shows DSPLY's message. When <paramref name="replyLength"/> is above 0 it waits for a reply of up to that
    /// many characters, and returns it; a job with nobody to answer returns null.
    /// </summary>
    string? Dsply(string message, int replyLength);
}

/// <summary>A 5250 display: send a record, or send one and wait for what the user does.</summary>
internal interface IWorkstation
{
    void Send(byte[] record);

    ReceivedInput Exchange(byte[] record);

    /// <summary>Sends the record and waits that long for the user: null when nobody did anything in time.</summary>
    ReceivedInput? Exchange(byte[] record, TimeSpan timeout);

    /// <summary>Whether the display can show 27 x 132.</summary>
    bool Wide { get; }
}

/// <summary>The program ended in error, or reached code the fake doesn't support. The message is for whoever called it.</summary>
internal sealed class RpgHalt(string message, string? messageId = null) : Exception(message)
{
    /// <summary>The escape message it ended with, such as CPF9898 from SNDPGMMSG, when there's one.</summary>
    public string? MessageId { get; } = messageId;
}

internal sealed partial class Interpreter
{
    private const long StatementLimit = 200_000_000;
    private const int DepthLimit = 400;

    private sealed class Frame(Procedure procedure, Dictionary<string, Cell> variables)
    {
        public Procedure Procedure { get; } = procedure;
        public Dictionary<string, Cell> Variables { get; } = variables;
        public int Parameters { get; set; }

        /// <summary>The parameters passed as *OMIT, by position (0 first).</summary>
        public HashSet<int> Omitted { get; set; } = [];
        public object? Result { get; set; }
    }

    private readonly RpgProgram _program;
    private readonly string _name;
    private readonly Machine _machine;
    private IJobHost _host;
    private int _depth;
    private readonly Dictionary<string, Cell> _globals = [];
    private readonly Cell[] _indicators = [.. Enumerable.Range(0, 100).Select(_ => new Cell(RpgType.Indicator))];
    private readonly Cell _lastRecord = new(RpgType.Indicator);
    private readonly Frame _main;
    private readonly List<Dds.DisplaySession> _displays = [];
    private readonly Dictionary<string, Dds.PrinterSession> _printers = [];
    private readonly Dictionary<string, Dds.DisplayFile> _definitions = [];
    private readonly HashSet<string> _openPrinters = [];
    private string _library = "";
    private string _exception = "";
    private bool _activated;
    private long _statements;
    private int _line;
    private int _calls;
    private bool _eof;
    private bool _found;
    private bool _error;
    private int _status;

    public Interpreter(RpgProgram program, string name, Machine machine, IJobHost host, int depth = 0, string library = "")
    {
        _program = program;
        _name = name;
        _machine = machine;
        _host = host;
        _depth = depth;
        _library = library;
        _main = new Frame(program.Main, _globals);
    }

    /// <summary>
    /// Makes the program's storage: its SQL variables, the fields of its files, its global
    /// variables and the program status data structure. Files that aren't USROPN are opened.
    /// </summary>
    internal void Activate()
    {
        if (_activated) return;
        _activated = true;
        DeclareSql();
        foreach (var file in _program.Files) DefineFields(file);
        foreach (var file in _program.Files.Where(f => !f.UserOpen)) Open(file);
        foreach (var declaration in _program.Main.Locals) Declare(declaration, _main);
        FillPsds();
        // DTAARA(*AUTO): read when the program starts. *INZSR: run before the mainline.
        foreach (var declaration in _program.Main.Locals.OfType<VarDecl>().Where(v => v.Type.DtaAraAuto)) DataArea(new DataAreaStmt(declaration.Line, "IN", declaration.Name), _main);
        if (_program.Main.Subroutines.TryGetValue("*INZSR", out var initialization)) Execute(initialization, _main);
    }

    /// <summary>Closes the files and cursors the program left open: what it printed becomes spooled files.</summary>
    private void Deactivate()
    {
        CloseSql();
        foreach (var file in _program.Files.Where(f => f.Printer > 0 && IsOpen(f)).ToList()) Close(file);
        FinishSpool();
    }

    /// <summary>
    /// Calls the program. Each argument is copied into its parameter and, unless the parameter is
    /// CONST or by value, copied back when the program returns. Throws <see cref="RpgHalt"/> when it ends in error.
    /// </summary>
    public void Run(IReadOnlyList<Cell> arguments)
    {
        if (_depth > 50) throw new RpgHalt($"{_name}: programs are calling each other more than 50 deep");
        try
        {
            Activate();
            if (_program.NoMain) throw new RpgHalt($"{_name} is a module of procedures (NOMAIN), with no mainline to call");
            var entry = _main;
            if (_program.MainProcedure is { } mainName)
            {
                if (!_program.Procedures.TryGetValue(mainName, out var procedure)) throw new RpgHalt($"{_name}: the main procedure {mainName} isn't in the program");
                entry = new Frame(procedure, []);
            }
            var parameters = entry.Procedure.Params;
            // Fixed form's *ENTRY PLIST: the parameters are fields of the program.
            var entryFields = entry == _main && parameters.Count == 0 ? _program.EntryParameters : [];
            if (arguments.Count > Math.Max(parameters.Count, entryFields.Count))
                throw new RpgHalt($"{_name} takes {Math.Max(parameters.Count, entryFields.Count)} parameter{(parameters.Count == 1 ? "" : "s")}, not {arguments.Count}");
            for (var i = 0; i < Math.Min(arguments.Count, entryFields.Count); i++)
                (Find(entryFields[i], _main) ?? throw new RpgHalt($"{_name}: the *ENTRY parameter {entryFields[i]} isn't defined")).Set(arguments[i].Get());
            var cells = new List<Cell>();
            for (var i = 0; i < Math.Min(arguments.Count, parameters.Count); i++)
            {
                var cell = new Cell(Resolve(parameters[i].Type, entry));
                cell.Set(arguments[i].Get());
                if (parameters[i].Name.Length > 0) entry.Variables[parameters[i].Name] = cell;
                cells.Add(cell);
            }
            entry.Parameters = arguments.Count;
            if (entry != _main) foreach (var declaration in entry.Procedure.Locals) Declare(declaration, entry);
            try
            {
                if (entry == _main && Cycles) Cycle(entry);
                else if (Execute(entry.Procedure.Body, entry) == Signal.Goto) throw new RpgError(100, $"there's no TAG {_gotoLabel}");
            }
            catch (RpgError e) when (entry.Procedure.Subroutines.ContainsKey("*PSSR") && !_inPssr)
            {
                // *PSSR: the program's own error handling, after which it ends.
                _status = e.Status;
                _exception = e.MessageId;
                FillPsds();
                _inPssr = true;
                Execute(entry.Procedure.Subroutines["*PSSR"], entry);
            }
            foreach (var declaration in _program.Main.Locals.OfType<VarDecl>().Where(v => v.Type.DtaAraAuto)) DataArea(new DataAreaStmt(declaration.Line, "OUT", declaration.Name), _main);
            for (var i = 0; i < Math.Min(arguments.Count, parameters.Count); i++)
            {
                if (!parameters[i].Const && !parameters[i].ByValue) arguments[i].Set(cells[i].Get());
            }
            for (var i = 0; i < Math.Min(arguments.Count, entryFields.Count); i++) arguments[i].Set(Find(entryFields[i], _main)!.Get());
        }
        catch (RpgError e)
        {
            throw new RpgHalt($"{_name} ended in error at line {_line}: {e.Message}{(e.Status > 0 ? $" (status {e.Status:00000})" : "")}", e.MessageId);
        }
        catch (OverflowException)
        {
            throw new RpgHalt($"{_name} ended in error at line {_line}: a number is too large");
        }
        catch (InvalidCastException)
        {
            throw new RpgHalt($"{_name} ended in error at line {_line}: a value isn't of the type the operation needs");
        }
        finally
        {
            Deactivate();
        }
    }


    /// <summary>Whatever the program printed becomes one spooled file for each printer file, named for the file (or as OVRPRTF says).</summary>
    private void FinishSpool()
    {
        foreach (var file in _pages.Keys.ToList()) FinishSpool(file);
    }

    private void FinishSpool(string file)
    {
        if (!_pages.Remove(file, out var page) || page.Lines.Count == 0) return;
        var name = _host.State.Overrides.GetValueOrDefault(file)?.SpoolName ?? file;
        var lines = page.Lines;
        while (lines.Count > 0 && lines[^1] == "\f") lines.RemoveAt(lines.Count - 1);
        _machine.Store.AddSpool(name, _host.User, _program.Files.First(f => f.Name == file).Printer, lines,
            lines.Contains("\f") ? lines.Count(l => l == "\f") + 1 : null);
    }

    private void Print(PrintStmt print, Frame frame)
    {
        var file = _program.Files.FirstOrDefault(f => f.Name == print.File && f.Printer > 0)
                   ?? throw new RpgError(1211, $"{print.File} isn't a printer file in this program");
        var text = Values.ToText(Evaluate(print.Data, frame)).TrimEnd();
        var page = PageOf(file);
        page.Lines.Add(text.Length > file.Printer ? text[..file.Printer] : text);
        page.Line++;
    }

    /// <summary>The types of the program's parameters, for a caller that has to build them from bytes.</summary>
    public IReadOnlyList<RpgType> ParameterTypes()
    {
        try
        {
            var frame = new Frame(_program.Main, new Dictionary<string, Cell>(_globals));
            foreach (var declaration in _program.Main.Locals.OfType<ConstDecl>()) Declare(declaration, frame);
            var procedure = _program.MainProcedure is { } main && _program.Procedures.TryGetValue(main, out var p) ? p : _program.Main;
            return [.. procedure.Params.Select(parameter => Resolve(parameter.Type, frame))];
        }
        catch (RpgError e)
        {
            throw new RpgHalt($"{_name}: {e.Message}");
        }
    }

    // ---- declarations ----

    private Cell? Find(string name, Frame frame) =>
        frame.Variables.TryGetValue(name, out var cell) || _globals.TryGetValue(name, out cell) ? cell : null;

    private void Declare(Decl declaration, Frame frame)
    {
        _line = declaration.Line;
        switch (declaration)
        {
            case ConstDecl constant:
            {
                var value = Evaluate(constant.Value, frame);
                var type = value switch
                {
                    string s => new RpgType(RpgKind.Varchar, Math.Max(s.Length, 1)),
                    decimal d => RpgType.Number(28, Math.Min((int)d.Scale, 27)),
                    DateOnly => new RpgType(RpgKind.Date),
                    TimeOnly => new RpgType(RpgKind.Time),
                    DateTime => new RpgType(RpgKind.Timestamp),
                    _ => throw new RpgError(100, $"the constant {constant.Name} has to be a number or a literal"),
                };
                var cell = new Cell(type);
                cell.Set(value);
                frame.Variables[constant.Name] = cell;
                break;
            }
            case VarDecl variable when variable.Type.Based is { } based:
            {
                // BASED(pointer): the variable is wherever the pointer points, each time it's used.
                var type = Resolve(variable.Type, frame);
                Pointer At(int offset)
                {
                    var pointer = Find(based, frame)?.Value as Pointer ?? throw new RpgError(100, $"{based}, the pointer {variable.Name} is based on, isn't a pointer");
                    return pointer.IsNull ? pointer : pointer with { Offset = pointer.Offset + offset };
                }
                var cell = new Cell(type, () => At(0));
                if (variable.Name.Length > 0) frame.Variables[variable.Name] = cell;
                if (type is { Kind: RpgKind.Ds, Dim: 0, Ds.Qualified: false })
                {
                    foreach (var field in type.Ds!.Fields.Where(f => f.Name.Length > 0))
                        frame.Variables[field.Name] = new Cell(field.Type, () => At(field.Position - 1));
                }
                break;
            }
            case VarDecl variable:
            {
                // A calculation's definition of a field the program already has is the same field.
                if (variable.Calculated && Find(variable.Name, frame) is not null) return;
                var cell = new Cell(Resolve(variable.Type, frame));
                Initialize(cell, variable.Inz, frame);
                if (variable.Type.CtData) LoadCompileTimeData(cell, variable, frame);
                if (variable.Type.Occurs is { } occurs && cell.Type.Kind == RpgKind.Ds)
                    _occurrences[cell] = ([.. Enumerable.Repeat(cell.Ds.Text, Math.Max(1, Number(occurs, frame)))], 1);
                if (variable.Name.Length > 0) frame.Variables[variable.Name] = cell;
                // An unqualified data structure's subfields are variables in their own right.
                if (cell.Type is { Kind: RpgKind.Ds, Dim: 0, Ds.Qualified: false })
                {
                    for (var i = 0; i < cell.Ds.Cells.Count; i++)
                    {
                        if (cell.Ds.Layout.Fields[i].Name.Length > 0) frame.Variables[cell.Ds.Layout.Fields[i].Name] = cell.Ds.Cells[i];
                    }
                }
                break;
            }
        }
    }

    /// <summary>Multiple-occurrence data structures (OCCURS): each occurrence's bytes, and which is current.</summary>
    private readonly Dictionary<Cell, (string[] Slots, int Current)> _occurrences = new(ReferenceEqualityComparer.Instance);

    /// <summary>A table's current element (from %TLOOKUP): what its name alone stands for.</summary>
    private readonly Dictionary<Cell, int> _tableCurrent = new(ReferenceEqualityComparer.Instance);

    /// <summary>%OCCUR(ds) = n: keeps the current occurrence's bytes and brings in occurrence n's.</summary>
    private void Occur(Cell cell, int occurrence)
    {
        if (!_occurrences.TryGetValue(cell, out var state)) throw new RpgError(100, "%OCCUR needs a multiple-occurrence data structure (OCCURS)");
        if (occurrence < 1 || occurrence > state.Slots.Length) throw new RpgError(122, "OCCUR value is out of range.");
        state.Slots[state.Current - 1] = cell.Ds.Text;
        cell.Ds.Fill(state.Slots[occurrence - 1]);
        _occurrences[cell] = (state.Slots, occurrence);
    }

    private int _compileTimeSections;

    /// <summary>A CTDATA array's elements, from its compile-time data: PERRCD elements a record, each its length.</summary>
    private void LoadCompileTimeData(Cell cell, VarDecl variable, Frame frame)
    {
        var sections = _program.CompileTimeData;
        var section = sections.FirstOrDefault(s => s.Name == variable.Name).Records;
        if (section is null)
        {
            var unnamed = sections.Where(s => s.Name is null).ToList();
            section = _compileTimeSections < unnamed.Count ? unnamed[_compileTimeSections++].Records : [];
        }
        if (cell.Elements is not { } elements) return;
        var perRecord = variable.Type.PerRecord is { } per ? Math.Max(1, Number(per, frame)) : 1;
        var width = cell.Type.Element.Kind is RpgKind.Char or RpgKind.Varchar ? cell.Type.Length : cell.Type.Length;
        var next = 0;
        foreach (var record in section)
        {
            for (var k = 0; k < perRecord && next < elements.Length; k++, next++)
            {
                var piece = k * width < record.Length ? record.Substring(k * width, Math.Min(width, record.Length - k * width)) : "";
                if (cell.Type.IsNumeric) elements[next].Set(Values.ToDecimal(piece.Trim().Length == 0 ? "0" : piece) / Values.Pow10(cell.Type.Decimals));
                else elements[next].Set(piece);
            }
        }
    }

    private void Initialize(Cell cell, Expr? inz, Frame frame)
    {
        if (cell.Elements is not null)
        {
            foreach (var element in cell.Elements) Initialize(element, inz, frame);
            return;
        }
        if (cell.Type.Kind == RpgKind.Ds)
        {
            for (var i = 0; i < cell.Ds.Cells.Count; i++) Initialize(cell.Ds.Cells[i], cell.Ds.Layout.Fields[i].Inz, frame);
            return;
        }
        if (inz is not null) cell.Set(Evaluate(inz, frame));
    }

    private int Number(Expr? e, Frame frame, int fallback = 0) => e is null ? fallback : (int)Values.ToDecimal(Evaluate(e, frame));

    private RpgType Resolve(TypeSpec spec, Frame frame)
    {
        var resolved = ResolveType(spec, frame);
        return spec.VaryingDim is { } varying && resolved.Dim > 0 ? resolved with { Varying = varying } : resolved;
    }

    private RpgType ResolveType(TypeSpec spec, Frame frame)
    {
        var dim = Number(spec.Dim, frame);
        if (spec.Dim is not null && dim is < 1 or > 1_000_000) throw new RpgError(100, "DIM has to be between 1 and 1000000");
        if ((spec.Like ?? spec.LikeDs) is { } like)
        {
            var source = Find(like, frame) ?? throw new RpgError(100, $"{like} isn't defined, for LIKE or LIKEDS");
            var type = source.Type with { Dim = dim, Varying = spec.VaryingDim };
            if (spec.LikeAdjust is { } adjust) type = type with { Length = type.Length + Number(adjust, frame) };
            return spec.LikeDs is not null && type.Ds is { } layout ? type with { Ds = layout with { Qualified = true } } : type;
        }
        switch (spec.Keyword)
        {
            case "CHAR":
                return new RpgType(RpgKind.Char, Number(spec.Length, frame), Dim: dim);
            case "VARCHAR":
                return new RpgType(RpgKind.Varchar, Number(spec.Length, frame), Dim: dim);
            case "PACKED" or "ZONED" or "BINDEC":
            {
                var (digits, decimals) = (Number(spec.Length, frame), Number(spec.Decimals, frame));
                if (digits is < 1 or > 63 || decimals < 0 || decimals > digits) throw new RpgError(100, $"{spec.Keyword}({digits}:{decimals}) isn't a valid type");
                if (digits > 28) throw new RpgError(100, "numbers of more than 28 digits aren't supported");
                return new RpgType(RpgKind.Decimal, digits, decimals, Dim: dim, Form: spec.Keyword switch { "ZONED" => 'S', "BINDEC" => 'B', _ => 'P' });
            }
            case "INT" or "UNS":
                return new RpgType(RpgKind.Int, Number(spec.Length, frame), Unsigned: spec.Keyword == "UNS", Dim: dim);
            case "FLOAT":
                return new RpgType(RpgKind.Float, Number(spec.Length, frame), Dim: dim);
            case "IND":
                return RpgType.Indicator with { Dim = dim };
            case "DATE":
                return new RpgType(RpgKind.Date, Dim: dim);
            case "TIME":
                return new RpgType(RpgKind.Time, Dim: dim);
            case "TIMESTAMP":
                return new RpgType(RpgKind.Timestamp, Dim: dim);
            case "POINTER":
                return RpgType.PointerType with { Dim = dim };
            case "DS":
            {
                var fields = new List<DsField>();
                if (spec.LikeRec is { } record)
                {
                    // LIKEREC: the fields of a record format of the program's files.
                    if (_disks.Values.FirstOrDefault(d => d.Format == record) is { } disk)
                    {
                        // A database file's record: its fields, with the file's prefix.
                        var keys = disk.File.Keys.Select(k => k.Field).ToList();
                        fields.AddRange(disk.File.Fields.Where(f => spec.FieldSet != "*KEY" || keys.Contains(f.Name))
                            .OrderBy(f => spec.FieldSet == "*KEY" ? keys.IndexOf(f.Name) : 0)
                            .Select(f => new DsField(disk.Name(f), f.Type, null, 0)));
                    }
                    else
                    {
                        if (_definitions.Values.SelectMany(d => d.Records.Values).FirstOrDefault(r => r.Name == record) is not { } format)
                            throw new RpgError(100, $"LIKEREC({record}): the program has no record format {record}");
                        var usage = spec.FieldSet switch { "*INPUT" => "IB", "*OUTPUT" => "OB", _ => "IOBH" };
                        fields.AddRange(format.Fields.Where(f => f.Name is not null && usage.Contains(f.Usage))
                            .Select(f => new DsField(f.Name!, f.Numeric ? RpgType.Number(f.Length, f.Decimals, 'S') : RpgType.Char(f.Length), null, 0)));
                    }
                }
                if (spec.ExtName is { } table)
                {
                    // EXTNAME('LIB/TABLE') or ('LIB.TABLE') names the library too.
                    var columns = _machine.ExternalFields(table, _library.Length > 0 ? _library : null);
                    if (columns.Count == 0) throw new RpgError(100, $"EXTNAME({table}): there's no such table");
                    fields.AddRange(columns.Select(c => new DsField(c.Name.ToUpperInvariant(), SqlType(c.Type), null, 0)));
                }
                if (spec.Prefix is { } prefix)
                {
                    for (var k = 0; k < fields.Count; k++) fields[k] = fields[k] with { Name = Prefixed(fields[k].Name, prefix, spec.PrefixReplace) };
                }
                // Each subfield's place: after the one before, at POS, or OVERLAY'd on another (at a position in it, or *NEXT after the last one there).
                var at = fields.Sum(f => Values.ByteLength(f.Type));
                for (var k = 0; k < fields.Count; k++) fields[k] = fields[k] with { Position = 1 + fields.Take(k).Sum(f => Values.ByteLength(f.Type)) };
                var next = new Dictionary<string, int>();
                foreach (var field in spec.Fields ?? [])
                {
                    var type = Resolve(field.Type, frame);
                    var length = Values.ByteLength(type) * Math.Max(1, type.Dim);
                    int start;
                    if (field.Type.Overlay is { } target)
                    {
                        var over = fields.FirstOrDefault(f => f.Name == target);
                        var origin = over is null ? 0 : over.Position - 1;
                        start = field.Type.OverlayAt switch
                        {
                            null => origin,
                            SpecialExpr { Name: "*NEXT" } => next.GetValueOrDefault(target, origin),
                            var position => origin + Number(position, frame) - 1,
                        };
                        next[target] = start + length;
                    }
                    else
                    {
                        start = field.Position > 0 ? field.Position - 1 : field.Type.PositionExpr is { } position ? Number(position, frame) - 1 : at;
                        at = Math.Max(at, start + length);
                    }
                    fields.Add(new DsField(field.Name, type, field.Inz, start + 1));
                }
                var declared = spec.Length is null ? 0 : Number(spec.Length, frame);
                var end = fields.Count == 0 ? 0 : fields.Max(f => f.Position - 1 + Values.ByteLength(f.Type) * Math.Max(1, f.Type.Dim));
                return new RpgType(RpgKind.Ds, Ds: new DsLayout(fields, spec.Qualified, Math.Max(declared, end)), Dim: dim);
            }
            default:
                throw new RpgError(100, "a declaration has no type");
        }
    }

    /// <summary>A field's name with PREFIX(prefix : n): its first n characters replaced by the prefix.</summary>
    internal static string Prefixed(string name, string prefix, int replace) => prefix + name[Math.Min(replace, name.Length)..];

    /// <summary>The RPG type for a column, from the type its table was created with.</summary>
    internal static RpgType SqlType(string declared)
    {
        var text = declared.ToUpperInvariant();
        var open = text.IndexOf('(');
        var name = (open < 0 ? text : text[..open]).Trim();
        var numbers = open < 0 ? [] : text[(open + 1)..].TrimEnd(')', ' ').Split(',').Select(n => int.TryParse(n.Trim(), out var v) ? v : 0).ToArray();
        int Arg(int i, int fallback) => i < numbers.Length && numbers[i] > 0 ? numbers[i] : fallback;
        return name switch
        {
            "CHAR" or "CHARACTER" or "NCHAR" or "GRAPHIC" => RpgType.Char(Arg(0, 1)),
            "VARCHAR" or "NVARCHAR" or "VARGRAPHIC" or "CLOB" or "TEXT" => new RpgType(RpgKind.Varchar, Arg(0, 256)),
            "DECIMAL" or "DEC" => RpgType.Number(Math.Min(Arg(0, 5), 28), numbers.Length > 1 ? numbers[1] : 0),
            "NUMERIC" or "NUM" => RpgType.Number(Math.Min(Arg(0, 5), 28), numbers.Length > 1 ? numbers[1] : 0, 'S'),
            "SMALLINT" => new RpgType(RpgKind.Int, 5),
            "INTEGER" or "INT" => new RpgType(RpgKind.Int, 10),
            "BIGINT" => new RpgType(RpgKind.Int, 20),
            "REAL" or "DOUBLE" or "FLOAT" => new RpgType(RpgKind.Float, 8),
            "DATE" => new RpgType(RpgKind.Date),
            "TIME" => new RpgType(RpgKind.Time),
            "TIMESTAMP" => new RpgType(RpgKind.Timestamp),
            _ => new RpgType(RpgKind.Varchar, 256),
        };
    }

    // ---- statements ----

    private Signal Execute(IReadOnlyList<Stmt> body, Frame frame)
    {
        for (var i = 0; i < body.Count; i++)
        {
            var signal = Execute(body[i], frame);
            if (signal == Signal.Goto && body.Select((s, k) => (s, k)).FirstOrDefault(t => t.s is TagStmt tag && tag.Label == _gotoLabel) is ({ } , var at))
            {
                // The TAG is in this body: carry on from it.
                i = at;
                Tick();
                continue;
            }
            if (signal != Signal.None) return signal;
        }
        return Signal.None;
    }

    /// <summary>The TAG a GOTO is going to.</summary>
    private string _gotoLabel = "";

    private Signal Execute(Stmt statement, Frame frame)
    {
        _line = statement.Line;
        if ((++_statements & 0x3ff) == 0)
        {
            _host.Cancelled.ThrowIfCancellationRequested();
            if (_statements > StatementLimit) throw new RpgHalt($"{_name} was stopped at line {_line}: it ran {StatementLimit:N0} statements without ending");
        }
        switch (statement)
        {
            case AssignStmt assign:
                Assign(assign, frame);
                return Signal.None;
            case IfStmt branch:
                foreach (var (condition, body) in branch.Branches)
                {
                    if (Values.ToBool(Evaluate(condition, frame))) return Execute(body, frame);
                }
                return branch.Else is null ? Signal.None : Execute(branch.Else, frame);
            case LoopStmt loop:
                while (loop.Until || Values.ToBool(Evaluate(loop.Condition, frame)))
                {
                    var signal = Execute(loop.Body, frame);
                    if (signal == Signal.Leave) break;
                    if (signal is Signal.Return or Signal.LeaveSr or Signal.Goto) return signal;
                    if (loop.Until && Values.ToBool(Evaluate(loop.Condition, frame))) break;
                    _line = loop.Line;
                    Tick();
                }
                return Signal.None;
            case ForStmt loop:
            {
                var variable = Locate(loop.Variable, frame);
                if (loop.Start is not null) variable.Set(Evaluate(loop.Start, frame));
                var by = loop.By is null ? 1m : Values.ToDecimal(Evaluate(loop.By, frame));
                while (true)
                {
                    if (loop.Limit is not null)
                    {
                        var compared = Values.Compare(variable.Get(), Evaluate(loop.Limit, frame));
                        if (loop.Down ? compared < 0 : compared > 0) break;
                    }
                    var signal = Execute(loop.Body, frame);
                    if (signal == Signal.Leave) break;
                    if (signal is Signal.Return or Signal.LeaveSr or Signal.Goto) return signal;
                    variable.Set(Values.ToDecimal(variable.Get()) + (loop.Down ? -by : by));
                    _line = loop.Line;
                    Tick();
                }
                return Signal.None;
            }
            case ForEachStmt loop:
            {
                var variable = Locate(loop.Variable, frame);
                if (Evaluate(loop.Array, frame) is not Cell { Elements: { } elements }) throw new RpgError(100, "FOR-EACH needs an array");
                foreach (var element in elements)
                {
                    variable.Set(element.Get());
                    var signal = Execute(loop.Body, frame);
                    if (signal == Signal.Leave) break;
                    if (signal is Signal.Return or Signal.LeaveSr or Signal.Goto) return signal;
                }
                return Signal.None;
            }
            case SelectStmt select:
                foreach (var (condition, body) in select.Whens)
                {
                    if (Values.ToBool(Evaluate(condition, frame))) return Execute(body, frame);
                }
                return select.Other is null ? Signal.None : Execute(select.Other, frame);
            case JumpStmt jump:
                if (jump.Value is not null) frame.Result = Evaluate(jump.Value, frame);
                return jump.Signal;
            case ExsrStmt exsr:
            {
                if (!frame.Procedure.Subroutines.TryGetValue(exsr.Name, out var body) && !_program.Main.Subroutines.TryGetValue(exsr.Name, out body))
                    throw new RpgError(100, $"the subroutine {exsr.Name} isn't defined");
                var signal = Execute(body, frame);
                return signal == Signal.Return ? signal : Signal.None;
            }
            case CallStmt call:
                Call((CallExpr)call.Call, frame, statement: true);
                return Signal.None;
            case DsplyStmt dsply:
                Dsply(dsply, frame);
                return Signal.None;
            case ClearStmt clear:
                if (clear.Target is NameExpr record && Find(record.Name, frame) is null && _displays.Any(d => d.ClearRecord(record.Name))) return Signal.None;
                Locate(clear.Target, frame).Clear();
                return Signal.None;
            case MonitorStmt monitor:
                try
                {
                    return Execute(monitor.Body, frame);
                }
                catch (RpgError e) when (monitor.Handlers.Any(h => h.Catches(e)))
                {
                    _status = e.Status;
                    _exception = e.MessageId;
                    FillPsds();
                    return Execute(monitor.Handlers.First(h => h.Catches(e)).Body, frame);
                }
            case SortaStmt sorta:
                Sort(sorta, frame);
                return Signal.None;
            case EvalCorrStmt corr:
            {
                var (target, source) = (Locate(corr.Target, frame), Locate(corr.Source, frame));
                if (target.Type.Kind != RpgKind.Ds || source.Type.Kind != RpgKind.Ds) throw new RpgError(100, "EVAL-CORR needs two data structures");
                foreach (var field in target.Ds.Layout.Fields)
                {
                    if (field.Name.Length > 0 && source.Ds.Field(field.Name) is { } from) target.Ds.Field(field.Name)!.Set(from.Get());
                }
                return Signal.None;
            }
            case TestStmt test:
                Test(test, frame);
                return Signal.None;
            case DataAreaStmt area:
                DataArea(area, frame);
                return Signal.None;
            case SendMessageStmt message:
            {
                var (id, text) = Evaluate(message.Message, frame) switch
                {
                    MessageValue m => (m.Id, m.Text),
                    var other => ("CPF9898", Values.ToText(other).TrimEnd()),
                };
                if (message.Type == "*ESCAPE") throw new RpgError(9999, text, id);
                if (message.Type != "*STATUS") _host.State.Messages.Add(text);
                return Signal.None;
            }
            case SqlStmt sql:
                ExecuteSql(sql.Sql, frame);
                return Signal.None;
            case PrintStmt print:
                Print(print, frame);
                return Signal.None;
            case DisplayStmt display:
                Display(display, frame);
                return Signal.None;
            case FileStmt file:
                FileOperation(file);
                return Signal.None;
            case GotoStmt jump:
                _gotoLabel = jump.Label;
                return Signal.Goto;
            case TagStmt or TotalStmt:
                return Signal.None;
            case MoveStmt move:
                Move(move, frame);
                return Signal.None;
            case CallProgramStmt call:
                CallProgram(call, frame);
                return Signal.None;
            case TimeStmt time:
                SetTime(Locate(time.Target, frame));
                return Signal.None;
            case BitStmt bits:
                Bits(bits, frame);
                return Signal.None;
            case ExceptStmt except:
                Output('E', except.Name, frame);
                return Signal.None;
            case UnsupportedStmt unsupported:
                throw new RpgHalt($"{_name} reached line {unsupported.Line}, which isn't supported: {unsupported.Message}");
            default:
                throw new InvalidOperationException(statement.GetType().Name);
        }
    }

    private bool _inPssr;

    /// <summary>SORTA: the array's elements in order (a structure array by the subfield named, ds(*).key), stable.</summary>
    private void Sort(SortaStmt sorta, Frame frame)
    {
        Cell[] elements;
        Func<Cell, object> key = c => c.Get();
        if (sorta.Array is MemberExpr { Target: CallExpr { Target: var structures, Args: [SpecialExpr { Name: "*" }] }, Field: var field })
        {
            elements = Locate(structures, frame).Elements ?? throw new RpgError(100, "SORTA needs an array of data structures");
            key = c => c.Ds.Field(field)?.Get() ?? throw new RpgError(100, $"the data structures have no subfield {field}");
        }
        else
        {
            elements = Evaluate(sorta.Array, frame) is Cell { Elements: { } e } ? e : throw new RpgError(100, "SORTA needs an array");
        }
        var order = elements.Select((c, i) => (Key: key(c), Value: c.Type.Kind == RpgKind.Ds ? (object)c.Ds.Text : c.Get(), Index: i)).ToList();
        order.Sort((a, b) => Values.Compare(a.Key, b.Key) is var c && c != 0 ? (sorta.Descending ? -c : c) : a.Index.CompareTo(b.Index));
        for (var i = 0; i < elements.Length; i++)
        {
            if (elements[i].Type.Kind == RpgKind.Ds) elements[i].Ds.Fill((string)order[i].Value);
            else elements[i].Set(order[i].Value);
        }
    }

    /// <summary>TEST(D), (T), (Z) and (N): sets %ERROR when the value isn't valid; without (E), an error that isn't is a run-time error.</summary>
    private void Test(TestStmt test, Frame frame)
    {
        var value = Evaluate(test.Value, frame);
        var valid = true;
        if (value is string text)
        {
            try
            {
                if (test.Extender.Contains('D')) Values.ParseDate(text, test.Format ?? "*ISO");
                else if (test.Extender.Contains('T')) Values.ParseTime(text);
                else if (test.Extender.Contains('Z')) Values.ParseTimestamp(text);
                else valid = text.Trim().Length > 0 && text.TrimStart().All(c => char.IsAsciiDigit(c) || c == ' ') ;
            }
            catch (RpgError)
            {
                valid = false;
            }
        }
        _error = !valid;
        if (!valid && !test.Extender.Contains('E')) throw new RpgError(112, "Date, Time or Timestamp value is not valid.");
    }

    /// <summary>IN, OUT and UNLOCK of a data area (an object of type *DTAARA, or the job's *LDA), into or from its variable.</summary>
    private void DataArea(DataAreaStmt area, Frame frame)
    {
        var declarations = _program.Main.Locals.Concat(frame.Procedure.Locals).OfType<VarDecl>().Where(v => v.Type.DtaAra is not null);
        foreach (var declaration in area.Name == "*DTAARA" ? declarations : declarations.Where(v => v.Name == area.Name))
        {
            if (area.Op == "UNLOCK") continue;
            var cell = Find(declaration.Name, frame) ?? throw new RpgError(100, $"{declaration.Name} isn't defined");
            var name = declaration.Type.DtaAra is { Length: > 0 } named ? named : declaration.Name;
            if (area.Op == "IN") cell.Set(_machine.ReadDataArea(name, _host) ?? throw new RpgError(401, $"Data area {name} not found.", "CPF1015"));
            else if (!_machine.WriteDataArea(name, Values.ToText(cell.Get()), _host)) throw new RpgError(401, $"Data area {name} not found.", "CPF1015");
        }
    }

    private void Tick()
    {
        if ((++_statements & 0x3ff) != 0) return;
        _host.Cancelled.ThrowIfCancellationRequested();
        if (_statements > StatementLimit) throw new RpgHalt($"{_name} was stopped at line {_line}: it ran {StatementLimit:N0} statements without ending");
    }

    private void Assign(AssignStmt assign, Frame frame)
    {
        var value = Evaluate(assign.Value, frame);
        if (assign.Target is BifExpr { Name: "%SUBST" } subst)
        {
            if (subst.Args.Count < 2 || assign.Op != "=") throw new RpgError(100, "%SUBST on the left needs a variable and a start");
            var target = Locate(subst.Args[0], frame);
            var current = Values.ToText(target.Get());
            var start = (int)Values.ToDecimal(Evaluate(subst.Args[1], frame));
            var length = subst.Args.Count > 2 ? (int)Values.ToDecimal(Evaluate(subst.Args[2], frame)) : current.Length - start + 1;
            if (start < 1 || length < 0 || start + length - 1 > current.Length) throw new RpgError(100, "Value out of range for string operation.");
            var piece = Values.ToText(value);
            piece = piece.Length >= length ? piece[..length] : piece.PadRight(length);
            target.Set(current[..(start - 1)] + piece + current[(start - 1 + length)..]);
            return;
        }
        if (assign.Target is BifExpr { Name: "%OCCUR", Args: [var occurring] })
        {
            Occur(Locate(occurring, frame), (int)Values.ToDecimal(value));
            return;
        }
        if (assign.Target is BifExpr { Name: "%ELEM", Args: [var varying] })
        {
            Locate(varying, frame).Count = (int)Values.ToDecimal(value);
            return;
        }
        if (assign.Target is BifExpr { Name: "%STR" } str)
        {
            // %STR(pointer : length) = value: the characters and a null, as many as fit in the length.
            var pointer = Evaluate(str.Args[0], frame) as Pointer ?? throw new RpgError(222, "Pointer or parameter error.");
            var max = str.Args.Count > 1 ? (int)Values.ToDecimal(Evaluate(str.Args[1], frame)) : int.MaxValue;
            var text = Values.ToText(value);
            if (text.Length > max - 1) text = text[..Math.Max(0, max - 1)];
            pointer.Target!.Put(pointer.Offset, text + '\0');
            return;
        }
        var cell = Locate(assign.Target, frame);
        if (assign.Truncate && cell.Type is { Kind: RpgKind.Decimal or RpgKind.Int } numeric && Values.IsNumber(value))
        {
            // Fixed form's arithmetic: the high-order digits that don't fit are lost.
            var rounded = decimal.Round(Values.ToDecimal(value), numeric.Decimals, assign.HalfAdjust ? MidpointRounding.AwayFromZero : MidpointRounding.ToZero);
            var limit = Values.Pow10(numeric.Length - numeric.Decimals);
            value = decimal.Truncate(rounded) % limit + (rounded - decimal.Truncate(rounded));
        }
        cell.Set(assign.Op == "=" ? value : Arithmetic(assign.Op[..^1], cell.Get(), value), assign.HalfAdjust, assign.Right);
    }

    private void Dsply(DsplyStmt dsply, Frame frame)
    {
        var message = dsply.Message is null ? "" : Evaluate(dsply.Message, frame);
        var text = Values.ToText(message).TrimEnd();
        if (text.Length > 52) text = text[..52];
        var response = dsply.Response is null ? null : Locate(dsply.Response, frame);
        var reply = _host.Dsply(text, response is null ? 0 : Math.Clamp(Values.ByteLength(response.Type), 1, 52));
        if (response is null || reply is null) return;
        response.Set(response.Type.IsNumeric ? Values.ParseNumber(reply) : reply);
    }

    // ---- variables ----

    /// <summary>The variable, subfield or array element an expression names.</summary>
    private Cell Locate(Expr e, Frame frame)
    {
        switch (e)
        {
            case NameExpr name:
                return Find(name.Name, frame) ?? throw new RpgError(100, $"{name.Name} isn't defined");
            case SpecialExpr { Name: "*INLR" }:
                return _lastRecord;
            case SpecialExpr named when IsNamedIndicator(named.Name):
                return NamedIndicator(named.Name[3..]);
            case SpecialExpr special when special.Name.StartsWith("*IN") && special.Name.Length == 5 && int.TryParse(special.Name[3..], out var n) && n > 0:
                return _indicators[n];
            case MemberExpr member:
            {
                var target = Locate(member.Target, frame);
                if (target.Type.Kind != RpgKind.Ds || target.Elements is not null) throw new RpgError(100, $"{Describe(member.Target)} isn't a data structure");
                return target.Ds.Field(member.Field) ?? throw new RpgError(100, $"{Describe(member.Target)} has no subfield {member.Field}");
            }
            case CallExpr call:
            {
                var array = call.Target is SpecialExpr { Name: "*IN" } ? null : Locate(call.Target, frame);
                if (call.Args.Count != 1) throw new RpgError(100, $"{Describe(call.Target)} takes one index");
                var index = (int)Values.ToDecimal(Evaluate(call.Args[0], frame));
                if (array is not null) return array.Elements is null ? throw new RpgError(100, $"{Describe(call.Target)} isn't an array") : array.Element(index);
                var elements = _indicators[1..];
                if (index < 1 || index > elements.Length) throw new RpgError(121, "An array index is out of range.");
                return elements[index - 1];
            }
            default:
                throw new RpgError(100, "this isn't a variable");
        }
    }

    private static string Describe(Expr e) => e switch
    {
        NameExpr n => n.Name,
        MemberExpr m => $"{Describe(m.Target)}.{m.Field}",
        CallExpr c => Describe(c.Target),
        SpecialExpr s => s.Name,
        _ => "the expression",
    };

    // ---- expressions ----

    private object Evaluate(Expr e, Frame frame)
    {
        switch (e)
        {
            case Literal literal:
                return literal.Value;
            case NameExpr name:
                if (Find(name.Name, frame) is { } cell)
                {
                    // A table's name alone (TABxxx) is its current element.
                    if (cell.Elements is { } table && name.Name.StartsWith("TAB") && _tableCurrent.TryGetValue(cell, out var current)) return table[current].Get();
                    return cell.Get();
                }
                if (_program.Procedures.ContainsKey(name.Name) || _program.Prototypes.ContainsKey(name.Name))
                    return Call(new CallExpr(name, []), frame, statement: false)!;
                throw new RpgError(100, $"{name.Name} isn't defined");
            case SpecialExpr special:
                if (special.Name == "*INLR" || special.Name.StartsWith("*IN") && special.Name.Length == 5 && char.IsDigit(special.Name[3]) || IsNamedIndicator(special.Name)) return Locate(e, frame).Get();
                if (special.Name == "*NULL") return Pointer.Null;
                return new SpecialValue(special.Name, special.All);
            case MemberExpr:
                return Locate(e, frame).Get();
            case CallExpr call:
                if (call.Target is NameExpr target && Find(target.Name, frame) is null) return Call(call, frame, statement: false)!;
                return Locate(e, frame).Get();
            case BifExpr bif:
                return Builtin(bif, frame);
            case UnaryExpr { Op: "NOT" } not:
                return !Values.ToBool(Evaluate(not.Operand, frame));
            case UnaryExpr negate:
            {
                var operand = Evaluate(negate.Operand, frame);
                return operand is double d ? -d : -Values.ToDecimal(operand);
            }
            case BinaryExpr { Op: "AND" } and:
                return Values.ToBool(Evaluate(and.Left, frame)) && Values.ToBool(Evaluate(and.Right, frame));
            case BinaryExpr { Op: "OR" } or:
                return Values.ToBool(Evaluate(or.Left, frame)) || Values.ToBool(Evaluate(or.Right, frame));
            case BinaryExpr { Op: "IN" } member:
            {
                var value = Evaluate(member.Left, frame);
                return Evaluate(member.Right, frame) switch
                {
                    RangeValue range => Values.Compare(value, range.Low) >= 0 && Values.Compare(value, range.High) <= 0,
                    Cell { Elements: { } elements } => elements.Any(e => Values.Compare(e.Get(), value) == 0),
                    _ => throw new RpgError(100, "IN needs %LIST, %RANGE or an array"),
                };
            }
            case BinaryExpr binary:
            {
                var left = Evaluate(binary.Left, frame);
                var right = Evaluate(binary.Right, frame);
                return binary.Op switch
                {
                    "=" => Values.Compare(left, right) == 0,
                    "<>" => Values.Compare(left, right) != 0,
                    "<" => Values.Compare(left, right) < 0,
                    ">" => Values.Compare(left, right) > 0,
                    "<=" => Values.Compare(left, right) <= 0,
                    ">=" => Values.Compare(left, right) >= 0,
                    _ => Arithmetic(binary.Op, left, right),
                };
            }
            case UnsupportedExpr unsupported:
                throw new RpgHalt($"{_name} reached line {_line}, which isn't supported: {unsupported.Message}");
            default:
                throw new InvalidOperationException(e.GetType().Name);
        }
    }

    private static object Arithmetic(string op, object left, object right)
    {
        // Pointer arithmetic: a pointer moved by a number of bytes, or the bytes between two pointers.
        if (left is Pointer p)
        {
            if (right is Pointer q && op == "-") return (decimal)(p.Offset - q.Offset);
            if (op is "+" or "-" && Values.IsNumber(right))
                return p.IsNull ? throw new RpgError(222, "Pointer or parameter error.") : p with { Offset = p.Offset + (op == "+" ? 1 : -1) * (int)Values.ToDecimal(right) };
            throw new RpgError(100, "a pointer can only have a number added or taken away");
        }
        if (right is Pointer r && op == "+" && Values.IsNumber(left)) return r with { Offset = r.Offset + (int)Values.ToDecimal(left) };
        if (op == "+" && left is string or bool && right is string or bool) return Values.ToText(left) + Values.ToText(right);
        if (left is DateOnly or TimeOnly or DateTime && right is Duration duration)
        {
            if (op is not ("+" or "-")) throw new RpgError(100, "a duration can only be added or subtracted");
            return AddDuration(left, duration, op == "-" ? -1 : 1);
        }
        if (right is DateOnly or TimeOnly or DateTime && left is Duration added && op == "+") return AddDuration(right, added, 1);
        if (left is string or bool || right is string or bool || !(Values.IsNumber(left) && Values.IsNumber(right)))
            throw new RpgError(100, $"{op} needs two numbers{(op == "+" ? " or two character values" : "")}");
        try
        {
            if (left is double || right is double)
            {
                var (a, b) = (System.Convert.ToDouble(left), System.Convert.ToDouble(right));
                return op switch
                {
                    "+" => a + b,
                    "-" => a - b,
                    "*" => a * b,
                    "/" => b == 0 ? throw new RpgError(102, "Attempt to divide by zero.") : a / b,
                    _ => Math.Pow(a, b),
                };
            }
            var (x, y) = ((decimal)left, (decimal)right);
            return op switch
            {
                "+" => x + y,
                "-" => x - y,
                "*" => x * y,
                "/" => y == 0 ? throw new RpgError(102, "Attempt to divide by zero.") : x / y,
                "**" => y == decimal.Truncate(y) && Math.Abs(y) <= 64 ? Power(x, (int)y) : (decimal)Math.Pow((double)x, (double)y),
                _ => throw new InvalidOperationException(op),
            };
        }
        catch (OverflowException)
        {
            throw new RpgError(103, "The target for a numeric operation is too small to hold the result.");
        }
    }

    private static decimal Power(decimal x, int y)
    {
        var result = 1m;
        for (var i = 0; i < Math.Abs(y); i++) result *= x;
        if (y >= 0) return result;
        return result == 0 ? throw new RpgError(102, "Attempt to divide by zero.") : 1 / result;
    }

    private static object AddDuration(object moment, Duration duration, int sign)
    {
        var count = (double)(duration.Count * sign);
        var whole = (int)(duration.Count * sign);
        try
        {
            var stamp = moment switch
            {
                DateOnly d => d.ToDateTime(TimeOnly.MinValue),
                TimeOnly t => new DateTime(2000, 1, 1).Add(t.ToTimeSpan()),
                _ => (DateTime)moment,
            };
            stamp = duration.Unit switch
            {
                "*YEARS" => stamp.AddYears(whole),
                "*MONTHS" => stamp.AddMonths(whole),
                "*DAYS" => stamp.AddDays(whole),
                "*HOURS" => stamp.AddHours(count),
                "*MINUTES" => stamp.AddMinutes(count),
                "*SECONDS" => stamp.AddSeconds(count),
                _ => stamp.AddTicks((long)(count * 10)),
            };
            return moment switch
            {
                DateOnly => DateOnly.FromDateTime(stamp),
                TimeOnly => TimeOnly.FromDateTime(stamp),
                _ => stamp,
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new RpgError(113, "Date overflow or underflow.");
        }
    }

    // ---- calls ----

    private object? Call(CallExpr call, Frame frame, bool statement)
    {
        if (call.Target is NameExpr { Name: "@SIGN" } && call.Args is [var signed])
        {
            // A MOVE's result: positive, negative, or zero (or blank).
            var value = Evaluate(signed, frame);
            return (decimal)(Values.IsNumber(value) ? Math.Sign(Values.ToDecimal(value)) : Values.ToText(value).Trim().Length == 0 ? 0 : 1);
        }
        if (call.Target is not NameExpr { Name: var name })
        {
            if (statement) throw new RpgError(100, "this isn't a statement");
            return Locate(call, frame).Get();
        }
        // EXTPROC: a procedure by its external name, in this module, in the fake (a C or system API), or bound from a service program.
        if (_program.Prototypes.TryGetValue(name, out var external) && external.ExtProc is { } extProc
            && !(_program.Procedures.ContainsKey(name) && extProc.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            var upper = extProc.ToUpperInvariant();
            if (_program.Procedures.TryGetValue(upper, out var local)) return CallProcedure(local, call.Args, frame);
            if (NativeNames.Contains(extProc)) return CallNative(extProc, external, call.Args, frame);
            if (Bound(upper) is { } exporter) return CallBound(exporter, upper, call.Args, frame);
            throw new RpgError(100, $"{name}: there's no procedure {extProc} (EXTPROC) in the program, the fake or a service program");
        }
        if (_program.Procedures.TryGetValue(name, out var procedure)) return CallProcedure(procedure, call.Args, frame);
        if (_program.Prototypes.TryGetValue(name, out var prototype) && prototype.Program is { } program)
        {
            var arguments = new List<Cell>();
            var copyBack = new List<(Cell Target, Cell Passed)>();
            for (var i = 0; i < call.Args.Count; i++)
            {
                var parameter = i < prototype.Params.Count ? prototype.Params[i] : null;
                var value = Evaluate(call.Args[i], frame);
                var passed = new Cell(parameter is null ? TypeOf(value) : Resolve(parameter.Type, frame));
                passed.Set(value);
                arguments.Add(passed);
                if (parameter is { Const: false, ByValue: false } && IsVariable(call.Args[i])) copyBack.Add((Locate(call.Args[i], frame), passed));
            }
            try
            {
                _machine.Call(program, arguments, _host, _depth + 1);
            }
            catch (RpgHalt e)
            {
                throw new RpgError(202, $"The call to {program} ended in error: {e.Message}", e.MessageId ?? "CPF9999");
            }
            foreach (var (target, passed) in copyBack) target.Set(passed.Get());
            return null;
        }
        if (statement && Find(name, frame) is not null) throw new RpgError(100, $"{name} is a variable, not a procedure");
        if (prototype is not null && Bound(name) is { } bound) return CallBound(bound, name, call.Args, frame);
        throw new RpgError(100, prototype is not null
            ? $"{name} has a prototype, but no service program{(_program.BindingDirectories.Count > 0 ? $" in binding directory {string.Join(", ", _program.BindingDirectories)}" : "")} exports it"
            : $"{name} isn't defined");
    }

    // ---- service programs ----

    private readonly Dictionary<string, Interpreter?> _bound = [];

    /// <summary>The service program, activated in this job, that exports the procedure: through CTL-OPT BNDDIR, or any that has it.</summary>
    private Interpreter? Bound(string procedure)
    {
        if (_bound.TryGetValue(procedure, out var known)) return known;
        var found = _machine.FindExport(procedure, _program.BindingDirectories, _host, _library);
        _bound[procedure] = found;
        return found;
    }

    internal bool Exports(string procedure) => _program.Procedures.TryGetValue(procedure, out var p) && (p.Exported || _program.NoMain);

    /// <summary>A bound call: the arguments are evaluated here, the procedure runs in its service program, with that program's globals.</summary>
    private object? CallBound(Interpreter target, string name, IReadOnlyList<Expr> arguments, Frame caller)
    {
        var procedure = target._program.Procedures[name];
        var (cells, copyBack) = Bind(procedure, arguments, caller, spec => target.Resolve(spec, target._main));
        target._omitting.UnionWith(_omitting);
        _omitting.Clear();
        var (host, depth) = (target._host, target._depth);
        (target._host, target._depth) = (_host, _depth);
        try
        {
            return target.Invoke(procedure, cells, arguments.Count, copyBack);
        }
        finally
        {
            (target._host, target._depth) = (host, depth);
        }
    }

    /// <summary>
    /// CL's CALLPRC: a procedure this service program exports, with the caller's variables as its
    /// parameters (each copied in its parameter's type, and back when the call ends); what it returns.
    /// </summary>
    internal object? CallExported(string name, IReadOnlyList<Cell> arguments, IJobHost host, int depth)
    {
        var procedure = _program.Procedures[name];
        if (arguments.Count > procedure.Params.Count)
            throw new RpgError(100, $"{procedure.Name} takes {procedure.Params.Count} parameter{(procedure.Params.Count == 1 ? "" : "s")}, not {arguments.Count}");
        var cells = new List<Cell>();
        var copyBack = new List<(Cell Target, Cell Passed)>();
        for (var i = 0; i < arguments.Count; i++)
        {
            var cell = new Cell(Resolve(procedure.Params[i].Type, _main));
            var value = arguments[i].Get();
            cell.Set(cell.Type.IsNumeric == arguments[i].Type.IsNumeric ? value : cell.Type.IsNumeric ? Values.ParseNumber(Values.ToText(value)) : Values.ToText(value));
            cells.Add(cell);
            copyBack.Add((arguments[i], cell));
        }
        var (saved, savedDepth) = (_host, _depth);
        (_host, _depth) = (host, depth);
        try
        {
            var result = Invoke(procedure, cells, arguments.Count, []);
            foreach (var (target, passed) in copyBack)
                target.Set(target.Type.IsNumeric == passed.Type.IsNumeric ? passed.Get() : target.Type.IsNumeric ? Values.ParseNumber(Values.ToText(passed.Get())) : Values.ToText(passed.Get()));
            return result;
        }
        finally
        {
            (_host, _depth) = (saved, savedDepth);
        }
    }

    private static bool IsVariable(Expr e) => e is NameExpr or MemberExpr or CallExpr or SpecialExpr { Name: "*INLR" };

    private static RpgType TypeOf(object value) => value switch
    {
        string s => RpgType.Char(Math.Max(s.Length, 1)),
        decimal => RpgType.Number(15, 5),
        bool => RpgType.Indicator,
        DateOnly => new RpgType(RpgKind.Date),
        TimeOnly => new RpgType(RpgKind.Time),
        DateTime => new RpgType(RpgKind.Timestamp),
        double => new RpgType(RpgKind.Float, 8),
        _ => throw new RpgError(100, "this value can't be passed as a parameter"),
    };

    private object? CallProcedure(Procedure procedure, IReadOnlyList<Expr> arguments, Frame caller)
    {
        var (cells, copyBack) = Bind(procedure, arguments, caller, null);
        return Invoke(procedure, cells, arguments.Count, copyBack);
    }

    /// <summary>
    /// The cells a procedure's parameters are: by reference, the caller's own variable when it has
    /// the parameter's type, otherwise a copy written back on return (<paramref name="copyBack"/>).
    /// </summary>
    private (List<Cell> Cells, List<(Cell Target, Cell Passed)> CopyBack) Bind(Procedure procedure, IReadOnlyList<Expr> arguments, Frame caller,
        Func<TypeSpec, RpgType>? resolve)
    {
        if (arguments.Count > procedure.Params.Count)
            throw new RpgError(100, $"{procedure.Name} takes {procedure.Params.Count} parameter{(procedure.Params.Count == 1 ? "" : "s")}, not {arguments.Count}");
        var cells = new List<Cell>();
        var copyBack = new List<(Cell Target, Cell Passed)>();
        for (var i = 0; i < arguments.Count; i++)
        {
            var parameter = procedure.Params[i];
            var type = resolve?.Invoke(parameter.Type) ?? Resolve(parameter.Type, _main);
            Cell passed;
            if (arguments[i] is SpecialExpr { Name: "*OMIT" })
            {
                passed = new Cell(type);
                _omitting.Add(i);
            }
            else if (!parameter.Const && !parameter.ByValue && IsVariable(arguments[i]) && Find(Root(arguments[i]), caller) is not null)
            {
                var source = Locate(arguments[i], caller);
                if (source.Type == type)
                {
                    passed = source;
                }
                else
                {
                    passed = new Cell(type);
                    passed.Set(source.Get());
                    copyBack.Add((source, passed));
                }
            }
            else
            {
                passed = new Cell(type);
                passed.Set(Evaluate(arguments[i], caller));
            }
            cells.Add(passed);
        }
        return (cells, copyBack);
    }

    /// <summary>The arguments of the call being bound that are *OMIT, for the procedure it calls.</summary>
    private readonly HashSet<int> _omitting = [];

    private object? Invoke(Procedure procedure, List<Cell> cells, int count, List<(Cell Target, Cell Passed)> copyBack)
    {
        if (++_calls > DepthLimit)
        {
            _calls--;
            throw new RpgError(100, $"procedures are calling each other more than {DepthLimit} deep");
        }
        try
        {
            var frame = new Frame(procedure, []) { Parameters = count, Omitted = [.. _omitting] };
            _omitting.Clear();
            for (var i = 0; i < cells.Count; i++)
            {
                if (procedure.Params[i].Name.Length > 0) frame.Variables[procedure.Params[i].Name] = cells[i];
            }
            foreach (var declaration in procedure.Locals) Declare(declaration, frame);
            var line = _line;
            var ended = false;
            try
            {
                Execute(procedure.Body, frame);
                ended = true;
            }
            catch (RpgError e) when (procedure.Subroutines.ContainsKey("*PSSR") && !_inPssr)
            {
                _status = e.Status;
                _exception = e.MessageId;
                _inPssr = true;
                try
                {
                    Execute(procedure.Subroutines["*PSSR"], frame);
                }
                finally
                {
                    _inPssr = false;
                }
                ended = true;
            }
            finally
            {
                // ON-EXIT runs however the procedure ends; its indicator says whether it ended in error.
                if (procedure.OnExit is { } onExit)
                {
                    if (procedure.OnExitIndicator is { } flag && Find(flag, frame) is { } indicator) indicator.Set(!ended);
                    Execute(onExit, frame);
                }
            }
            _line = line;
            foreach (var (target, passed) in copyBack) target.Set(passed.Get());
            if (procedure.Returns is null) return null;
            var result = new Cell(Resolve(procedure.Returns, frame));
            if (frame.Result is not null) result.Set(frame.Result);
            return result.Get();
        }
        finally
        {
            _calls--;
        }
    }

    /// <summary>The variable name an expression starts from, or "" when it doesn't start from one.</summary>
    private static string Root(Expr e) => e switch
    {
        NameExpr n => n.Name,
        MemberExpr m => Root(m.Target),
        CallExpr c => Root(c.Target),
        _ => "",
    };

    // ---- files ----

    /// <summary>A file's record formats: each named field is a variable of the program, unless the program declares it.</summary>
    private void DefineFields(FileDecl file)
    {
        _line = file.Line;
        if (file.Disk)
        {
            DefineDiskFields(file);
            return;
        }
        Dds.DisplayFile? definition;
        if (file.Printer > 0)
        {
            // A printer file with no DDS of its own is program-described: WRITE prints lines.
            if (!file.External || _machine.LoadPrinterFile(file.Name, _host.User) is not { } printer) return;
            definition = printer;
        }
        else
        {
            definition = _machine.LoadDisplayFile(file.Name, _host.User)
                ?? throw new RpgError(1211, $"display file {file.Name} doesn't exist: create it first with CRTDSPF");
        }
        _definitions[file.Name] = definition;
        foreach (var field in definition.Records.Values.SelectMany(r => r.Fields).Where(f => f.Name is not null))
        {
            _globals.TryAdd(field.Name!, new Cell(field.Numeric ? RpgType.Number(field.Length, field.Decimals, 'S') : RpgType.Char(field.Length)));
        }
    }

    private bool IsOpen(FileDecl file) =>
        file.Disk ? _disks.TryGetValue(file.Name, out var disk) && disk.Open
        : file.Printer > 0 ? _openPrinters.Contains(file.Name) : _displays.Any(d => d.File.Name == file.Name);

    private void Open(FileDecl file)
    {
        if (IsOpen(file)) throw new RpgError(1215, $"{file.Name} is already open");
        if (file.Disk)
        {
            foreach (var disk in _disks.Values.Where(d => d.Declaration.Name == file.Name))
            {
                disk.Reset();
                disk.Open = true;
            }
            return;
        }
        if (file.Printer > 0)
        {
            _openPrinters.Add(file.Name);
            if (_definitions.TryGetValue(file.Name, out var printer))
            {
                var flag = file.Overflow is { } name ? Find(name, _main) ?? (name.StartsWith("*IN") && int.TryParse(name[3..], out var n) ? _indicators[n] : null) : null;
                _printers[file.Name] = new Dds.PrinterSession(printer, name => _globals.GetValueOrDefault(name), n => Values.ToBool(_indicators[n].Get()), flag);
            }
            return;
        }
        var definition = _definitions[file.Name];
        if (definition.Wide && _host.Workstation is { Wide: false })
            throw new RpgError(1217, $"Display file {file.Name} is 27 x 132, and this display shows only 24 x 80.", "CPF4131");
        _displays.Add(new Dds.DisplaySession(definition, file,
            name => _globals.GetValueOrDefault(name),
            Indicator,
            _host.User,
            _host.State,
            (id, messageFile) => _machine.MessageText(id, messageFile, _library.Length > 0 ? _library : null)));
    }

    private void Close(FileDecl file)
    {
        if (!IsOpen(file)) throw new RpgError(1216, $"{file.Name} isn't open");
        if (file.Disk)
        {
            foreach (var disk in _disks.Values.Where(d => d.Declaration.Name == file.Name)) disk.Open = false;
            return;
        }
        if (file.Printer > 0)
        {
            _openPrinters.Remove(file.Name);
            if (_printers.Remove(file.Name, out var session))
            {
                var (lines, pages) = session.Output();
                var name = _host.State.Overrides.GetValueOrDefault(file.Name)?.SpoolName ?? file.Name;
                if (lines.Count > 0) _machine.Store.AddSpool(name, _host.User, session.File.PageWidth, lines, pages);
            }
            FinishSpool(file.Name);
            return;
        }
        _displays.RemoveAll(d => d.File.Name == file.Name);
    }

    private void FileOperation(FileStmt statement)
    {
        _error = false;
        try
        {
            var files = statement.File == "*ALL" ? _program.Files.Where(IsOpen).ToList() : [_program.Files.First(f => f.Name == statement.File)];
            foreach (var file in files)
            {
                if (statement.Op == "OPEN") Open(file);
                else Close(file);
            }
        }
        catch (RpgError e) when (statement.ErrorExtender)
        {
            _error = true;
            _status = e.Status;
            _exception = e.MessageId;
            FillPsds();
        }
    }

    /// <summary>The program status data structure: the program's name, the last exception's message, the job and the user.</summary>
    private void FillPsds()
    {
        if (_program.Main.Locals.OfType<VarDecl>().FirstOrDefault(v => v.Type.Psds) is not { } psds || Find(psds.Name, _main) is not { Type.Kind: RpgKind.Ds } cell) return;
        var image = new char[429];
        Array.Fill(image, ' ');
        void Put(int position, string text) => text.CopyTo(0, image, position - 1, Math.Min(text.Length, image.Length - position + 1));
        Put(1, _name.PadRight(10));
        Put(11, _status.ToString("00000"));
        Put(16, "00000");
        Put(21, "00000000");
        Put(29, "*DETC".PadRight(8));
        Put(37, Math.Clamp(_main.Parameters, 0, 999).ToString("000"));
        Put(40, _exception.PadRight(7));
        Put(81, _library.PadRight(10));
        Put(244, _host.State.Name.PadRight(10));
        Put(254, _host.User.PadRight(10));
        Put(264, _host.State.Number);
        Put(358, _host.User.PadRight(10));
        cell.Ds.Fill(new string(image));
    }

    /// <summary>The file information data structure, after an operation on a display file: the file, its status and the key pressed (position 369).</summary>
    private void FillInfds(FileDecl file, Dds.DisplaySession session)
    {
        if (file.InfDs is null || Find(file.InfDs, _main) is not { Type.Kind: RpgKind.Ds, Elements: null } cell) return;
        var image = new char[528];
        Array.Fill(image, ' ');
        void Put(int position, string text) => text.CopyTo(0, image, position - 1, Math.Min(text.Length, image.Length - position + 1));
        Put(1, file.Name.PadRight(8));
        Put(11, _status.ToString("00000"));
        Put(369, Ebcdic.ToChar((byte)session.LastAid).ToString());
        cell.Ds.Fill(new string(image));
    }

    /// <summary>A display file's indicator: the INDDS subfield at that position, or *IN.</summary>
    private Cell Indicator(FileDecl file, int number)
    {
        if (file.IndDs is null) return _indicators[number];
        if (_globals.GetValueOrDefault(file.IndDs) is not { Type.Kind: RpgKind.Ds, Elements: null } ds) throw new RpgError(100, $"the INDDS data structure {file.IndDs} isn't defined");
        for (var i = 0; i < ds.Ds.Layout.Fields.Count; i++)
        {
            if (ds.Ds.Layout.Fields[i].Position == number && ds.Ds.Cells[i].Type.Kind == RpgKind.Ind) return ds.Ds.Cells[i];
        }
        // An indicator the structure doesn't name still has to be somewhere.
        return _spare.TryGetValue((file.Name, number), out var cell) ? cell : _spare[(file.Name, number)] = new Cell(RpgType.Indicator);
    }

    private readonly Dictionary<(string File, int Number), Cell> _spare = [];

    private void Display(DisplayStmt statement, Frame frame)
    {
        _error = false;
        try
        {
            // A database file's record.
            if (DiskFor(statement.Record) is { } disk && !(statement.Op == "READ" && _displays.Any(d => d.File.Name == statement.Record)))
            {
                DiskOperation(statement, disk, frame);
                return;
            }
            // A printer file's record.
            if (statement.Op == "WRITE" && _printers.Values.FirstOrDefault(p => p.File.Records.ContainsKey(statement.Record)) is { } printer)
            {
                printer.Write(statement.Record);
                return;
            }
            if (statement.Op == "WRITE" && _program.Files.Any(f => f.Printer > 0 && _definitions.TryGetValue(f.Name, out var d) && d.Records.ContainsKey(statement.Record)))
                throw new RpgError(1211, $"the printer file of {statement.Record} isn't open");
            var session = _displays.FirstOrDefault(d => d.File.Records.ContainsKey(statement.Record));
            // READ of the file itself: the reply to the record last written.
            var wholeFile = session is null && statement.Op == "READ" ? _displays.FirstOrDefault(d => d.File.Name == statement.Record) : null;
            session ??= wholeFile;
            if (session is null)
            {
                throw new RpgError(1211, _program.Files.Any(f => f.Name == statement.Record || _definitions.TryGetValue(f.Name, out var d) && d.Records.ContainsKey(statement.Record))
                    ? $"the file of {statement.Record} isn't open"
                    : $"{statement.Record} isn't a record format of a display file in this program");
            }
            var workstation = _host.Workstation ?? throw new RpgError(1255, $"{statement.Op} {statement.Record} needs a display, and this job has none");
            var declaration = _program.Files.First(f => f.Name == session.File.Name);
            switch (statement.Op)
            {
                case "WRITE":
                    session.Write(statement.Record, workstation);
                    break;
                case "EXFMT":
                    session.Write(statement.Record, workstation, read: true);
                    FillInfds(declaration, session);
                    break;
                case "READ":
                    try
                    {
                        if (wholeFile is not null) session.ReadFile(workstation);
                        else session.Read(statement.Record, workstation);
                    }
                    finally
                    {
                        FillInfds(declaration, session);
                    }
                    _eof = false;
                    break;
                case "READC":
                    _eof = !session.ReadChanged(statement.Record);
                    break;
                case "CHAIN":
                    _found = session.Chain(statement.Record, (int)Values.ToDecimal(Evaluate(statement.Key!, frame)));
                    break;
                case "UPDATE":
                    session.Update(statement.Record);
                    break;
            }
        }
        catch (RpgError e) when (statement.ErrorExtender)
        {
            _error = true;
            _status = e.Status;
            _exception = e.MessageId;
            FillPsds();
        }
    }
}
