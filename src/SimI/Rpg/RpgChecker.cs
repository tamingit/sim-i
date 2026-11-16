// Checks a parsed program as an IBM i's compiler does once it has read it: every name is defined
// once, types fit where they're used, calls pass what their prototypes ask for, LEAVE and ITER are in
// loops, RETURN fits its procedure, and the files and tables the declarations name are there. What
// fails is an error, so nothing is made from the member, as on an IBM i.

using SimI.Dds;

namespace SimI.Rpg;

/// <summary>
/// What checking a program needs from the system: a display (or, with true, printer) file's DDS,
/// or null when there's no such file; a table's columns (EXTNAME), empty when there's none; and
/// whether the member is SQLRPGLE, which has the SQL communication area's fields.
/// </summary>
internal sealed record RpgContext(Func<string, bool, DisplayFile?> File, Func<string, IReadOnlyList<(string Name, string Type)>> Columns, bool Sql,
    Func<string, DatabaseFile?>? Database = null);

internal sealed class RpgChecker
{
    private enum Cat { Unknown, Char, Num, Ind, Date, Time, Stamp, Ds, Duration }

    private enum Kind { Variable, Constant, Procedure, Format, File }

    private sealed class Sym(string name, Kind kind, int line)
    {
        public string Name { get; } = name;
        public Kind Kind { get; } = kind;
        public int Line { get; } = line;
        public Cat Cat { get; set; }
        public bool Array { get; set; }

        /// <summary>A CONST parameter: the procedure can't change it.</summary>
        public bool ReadOnly { get; set; }

        /// <summary>A field of a display or printer file.</summary>
        public bool FromFile { get; set; }

        /// <summary>Why Sim i can't use it, when it can't.</summary>
        public string? Unsupported { get; set; }

        public TypeSpec? Spec { get; set; }
        public Expr? Inz { get; set; }
        public Expr? Value { get; set; }
        public Scope? Home { get; set; }
        public bool Completed { get; set; }
        public bool Completing { get; set; }

        /// <summary>A data structure's subfields.</summary>
        public Dictionary<string, Sym>? Fields { get; set; }

        public Procedure? Procedure { get; set; }
        public ProtoDecl? Prototype { get; set; }
    }

    private sealed class Scope(Scope? outer, Procedure procedure)
    {
        public Dictionary<string, Sym> Names { get; } = [];
        public Procedure Procedure { get; } = procedure;
        public Sym? Find(string name) => Names.TryGetValue(name, out var found) ? found : outer?.Find(name);
    }

    private readonly RpgProgram _program;
    private readonly RpgContext _context;

    /// <summary>Names Sim i couldn't declare: what's done with them isn't checked.</summary>
    private bool Unchecked(string name) => _program.UnsupportedNames.Contains(name);
    private readonly Scope _global;
    private readonly Dictionary<VarDecl, Sym> _parameters = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<(int, string)> _reported = [];
    private Scope _scope;
    private Sym? _returns;
    private int _line;

    /// <summary>The record formats of the program's files, by name.</summary>
    private readonly HashSet<string> _formats = [];

    /// <summary>A file the program names isn't there: names its fields would have defined aren't reported.</summary>
    private bool _missingFile;

    private RpgChecker(RpgProgram program, RpgContext context)
    {
        _program = program;
        _context = context;
        _global = new Scope(null, program.Main);
        _scope = _global;
    }

    /// <summary>Checks the program, adding what's wrong to its diagnostics.</summary>
    public static void Check(RpgProgram program, RpgContext context) => new RpgChecker(program, context).Run();

    private void Error(string message, int? line = null)
    {
        var at = line ?? _line;
        if (_reported.Add((at, message))) _program.Diagnostics.Add(new Diagnostic(at, Severity.Error, message));
    }

    private void Unsupported(string message)
    {
        if (_reported.Add((_line, message))) _program.Diagnostics.Add(new Diagnostic(_line, Severity.Unsupported, message));
    }

    // ---- the program ----

    private void Run()
    {
        if (_context.Sql) DeclareSqlca();
        foreach (var declaration in _program.Main.Locals) Declare(declaration, _global);
        foreach (var parameter in _program.Main.Params) Declare(parameter, _global, parameter: true);
        foreach (var prototype in _program.Prototypes.Values)
        {
            _line = prototype.Line;
            Add(_global, new Sym(prototype.Name, Kind.Procedure, prototype.Line) { Prototype = prototype });
        }
        foreach (var procedure in _program.Procedures.Values)
        {
            _line = procedure.Line;
            Add(_global, new Sym(procedure.Name, Kind.Procedure, procedure.Line) { Procedure = procedure });
        }
        Files();
        foreach (var sym in _global.Names.Values.ToList()) Complete(sym);
        FileKeywords();

        Walk(_program.Main, _global, null);
        foreach (var procedure in _program.Procedures.Values)
        {
            var scope = new Scope(_global, procedure);
            foreach (var parameter in procedure.Params) Declare(parameter, scope, parameter: true);
            foreach (var declaration in procedure.Locals) Declare(declaration, scope);
            foreach (var sym in scope.Names.Values.ToList()) Complete(sym);
            Sym? returns = null;
            if (procedure.Returns is { } type)
            {
                returns = new Sym(procedure.Name, Kind.Variable, procedure.Line) { Spec = type, Home = scope };
                Complete(returns);
            }
            MatchPrototype(procedure);
            Walk(procedure, scope, returns);
        }
    }

    private void Walk(Procedure procedure, Scope scope, Sym? returns)
    {
        (_scope, _returns) = (scope, returns);
        Body(procedure.Body, loop: false, subroutine: false);
        if (procedure.OnExit is { } onExit) Body(onExit, loop: false, subroutine: false);
        foreach (var body in procedure.Subroutines.Values) Body(body, loop: false, subroutine: true);
        // A cursor's host variables are the ones where it's declared.
        foreach (var (cursor, (line, declaredIn)) in _program.CursorDeclarations)
        {
            if (declaredIn != procedure) continue;
            _line = line;
            HostVariables(_program.Cursors[cursor]);
        }
        _scope = _global;
    }

    /// <summary>The fields of the SQL communication area that a CRTSQLRPGI program has.</summary>
    private void DeclareSqlca()
    {
        void Field(string name, Cat cat, bool array = false, string? unsupported = null) =>
            _global.Names[name] = new Sym(name, Kind.Variable, 0) { Cat = cat, Array = array, Completed = true, Unsupported = unsupported };
        Field("SQLCODE", Cat.Num);
        Field("SQLCOD", Cat.Num);
        Field("SQLSTATE", Cat.Char);
        Field("SQLSTT", Cat.Char);
        Field("SQLERRD", Cat.Num, array: true);
        Field("SQLER3", Cat.Num);
        const string only = "only SQLCODE, SQLSTATE, SQLERRD and SQLER3 of the SQLCA are supported";
        foreach (var name in new[] { "SQLCAID", "SQLCABC", "SQLERRML", "SQLERRMC", "SQLERRP", "SQLER1", "SQLER2", "SQLER4", "SQLER5", "SQLER6", "SQLWARN", "SQLWN0", "SQLWN1", "SQLWN2", "SQLWN3", "SQLWN4", "SQLWN5", "SQLWN6", "SQLWN7", "SQLWN8", "SQLWN9", "SQLWNA", "SQLCA" })
            Field(name, name is "SQLERRML" or "SQLCABC" or "SQLER1" or "SQLER2" or "SQLER4" or "SQLER5" or "SQLER6" ? Cat.Num : Cat.Char, unsupported: only);
    }

    // ---- declarations ----

    private void Add(Scope scope, Sym sym)
    {
        if (!scope.Names.TryGetValue(sym.Name, out var existing))
        {
            scope.Names[sym.Name] = sym;
            return;
        }
        // A procedure and its prototype have the same name.
        if (existing.Kind == Kind.Procedure && sym.Kind == Kind.Procedure && (existing.Prototype is null) != (sym.Prototype is null))
        {
            existing.Prototype ??= sym.Prototype;
            existing.Procedure ??= sym.Procedure;
            return;
        }
        if (existing.FromFile && sym.Kind == Kind.Variable)
        {
            scope.Names[sym.Name] = sym;
            return;
        }
        Error($"{sym.Name} is defined more than once", sym.Line);
    }

    private void Declare(Decl declaration, Scope scope, bool parameter = false)
    {
        _line = declaration.Line;
        switch (declaration)
        {
            case ConstDecl constant:
                Add(scope, new Sym(constant.Name, Kind.Constant, constant.Line) { Value = constant.Value, Home = scope });
                break;
            case VarDecl variable:
            {
                // A calculation can define a field the program already has (with the same type and length).
                if (variable.Calculated && scope.Names.TryGetValue(variable.Name, out var already) && already.Kind == Kind.Variable) break;
                var sym = Variable(variable, scope);
                sym.ReadOnly = parameter && variable.Const;
                if (parameter) _parameters[variable] = sym;
                if (variable.Name.Length > 0) Add(scope, sym);
                // An unqualified data structure's subfields are names in their own right.
                if (sym.Fields is not null && !variable.Type.Qualified && variable.Type.LikeDs is null && variable.Type.Dim is null)
                {
                    foreach (var field in sym.Fields.Values)
                    {
                        // An externally described structure's field can be another's too (the same field of two files).
                        if (variable.Type.ExtName is not null && scope.Names.TryGetValue(field.Name, out var other) && other.Kind == Kind.Variable) continue;
                        Add(scope, field);
                    }
                }
                break;
            }
        }
    }

    /// <summary>A variable, subfield or parameter: a data structure's subfields are made now, its types when it's completed.</summary>
    private Sym Variable(VarDecl variable, Scope scope)
    {
        var sym = new Sym(variable.Name, Kind.Variable, variable.Line)
        {
            Spec = variable.Type,
            Inz = variable.Inz,
            Home = scope,
            Array = variable.Type.Dim is not null,
        };
        if (variable.Type is { Keyword: "DS", LikeDs: null })
        {
            sym.Fields = [];
            string Named(string name) => variable.Type.Prefix is { } prefix ? Interpreter.Prefixed(name, prefix, variable.Type.PrefixReplace) : name;
            if (variable.Type.LikeRec is { } record)
            {
                // LIKEREC: a record format of the program's files.
                var format = _program.Files.Where(f => !f.Disk).Select(f => _context.File(f.Name, f.Printer > 0)).OfType<DisplayFile>()
                    .SelectMany(d => d.Records.Values).FirstOrDefault(r => r.Name == record);
                // A database file's record format, by its name in the program; its fields have the file's prefix.
                (FileDecl File, DatabaseFile? Database) database = format is not null ? default : _program.Files.Where(f => f.Disk)
                    .SelectMany(f => _context.Database?.Invoke(f.ExtFile ?? f.Name)?.Formats.Select(d => (File: f, Database: (DatabaseFile?)d)) ?? [])
                    .FirstOrDefault(d => d.Database is not null && (d.File.Renames?.GetValueOrDefault(d.Database.Format) ?? d.Database.Format) == record);
                if (format is null && database.Database is null && !_missingFile) Error($"LIKEREC({record}): the program's files have no record format {record}");
                if (format is null && database.Database is null) sym.Cat = Cat.Unknown;
                foreach (var field in format?.Fields.Where(f => f.Name is not null) ?? [])
                    sym.Fields[Named(field.Name!)] = new Sym(Named(field.Name!), Kind.Variable, variable.Line) { Cat = field.Numeric ? Cat.Num : Cat.Char, Completed = true };
                foreach (var field in database.Database?.Fields ?? [])
                {
                    var name = Named(database.File.Prefix is { } filePrefix ? Interpreter.Prefixed(field.Name, filePrefix, database.File.PrefixReplace) : field.Name);
                    sym.Fields[name] = new Sym(name, Kind.Variable, variable.Line) { Cat = Category(field.Type.Kind), Completed = true };
                }
            }
            if (variable.Type.ExtName is { } table)
            {
                var columns = _context.Columns(table);
                if (columns.Count == 0) Error($"EXTNAME({table}): there's no such file or table");
                foreach (var (name, type) in columns)
                {
                    var named = Named(name.ToUpperInvariant());
                    sym.Fields[named] = new Sym(named, Kind.Variable, variable.Line)
                    {
                        Cat = Category(Interpreter.SqlType(type).Kind),
                        Completed = true,
                    };
                }
            }
            foreach (var field in variable.Type.Fields ?? [])
            {
                if (field.Name.Length == 0) continue;
                if (sym.Fields.ContainsKey(field.Name)) Error($"{field.Name} is defined more than once", field.Line);
                sym.Fields[field.Name] = Variable(field, scope);
            }
        }
        return sym;
    }

    private static Cat Category(RpgKind kind) => kind switch
    {
        RpgKind.Char or RpgKind.Varchar => Cat.Char,
        RpgKind.Decimal or RpgKind.Int or RpgKind.Float => Cat.Num,
        RpgKind.Ind => Cat.Ind,
        RpgKind.Date => Cat.Date,
        RpgKind.Time => Cat.Time,
        RpgKind.Timestamp => Cat.Stamp,
        RpgKind.Ds => Cat.Ds,
        _ => Cat.Unknown,
    };

    private static Cat Category(string? keyword) => keyword switch
    {
        "CHAR" or "VARCHAR" or "GRAPH" or "VARGRAPH" or "UCS2" or "VARUCS2" => Cat.Char,
        "PACKED" or "ZONED" or "BINDEC" or "INT" or "UNS" or "FLOAT" => Cat.Num,
        "IND" => Cat.Ind,
        "DATE" => Cat.Date,
        "TIME" => Cat.Time,
        "TIMESTAMP" => Cat.Stamp,
        "DS" => Cat.Ds,
        _ => Cat.Unknown,
    };

    /// <summary>Works out a name's type: LIKE and LIKEDS are looked up where it's declared, and its INZ checked.</summary>
    private void Complete(Sym sym)
    {
        if (sym.Completed) return;
        if (sym.Completing)
        {
            Error($"{sym.Name} is defined in terms of itself", sym.Line);
            return;
        }
        sym.Completing = true;
        var (scope, line) = (_scope, _line);
        _scope = sym.Home ?? _global;
        _line = sym.Line;
        try
        {
            if (sym.Kind == Kind.Constant)
            {
                sym.Cat = Type(sym.Value!);
                if (sym.Cat == Cat.Duration) Error($"the constant {sym.Name} has to be a literal");
            }
            else if (sym.Spec is { } spec)
            {
                foreach (var size in new[] { spec.Length, spec.Decimals, spec.Dim })
                {
                    if (size is not null && Type(size) is not (Cat.Num or Cat.Unknown)) Error($"the length, decimals or DIM of {sym.Name} has to be a number");
                }
                if (spec.Like is { } like)
                {
                    if (_scope.Find(like) is not { Kind: Kind.Variable or Kind.Constant } target)
                    {
                        if (!Unchecked(like) && (!_missingFile || _scope.Find(like) is not null)) Error($"LIKE({like}): {like} isn't defined");
                    }
                    else
                    {
                        Complete(target);
                        sym.Cat = target.Cat == Cat.Ds ? Cat.Char : target.Cat;
                    }
                }
                else if (spec.LikeDs is { } likeDs)
                {
                    sym.Cat = Cat.Ds;
                    var target = _scope.Find(likeDs);
                    if (target is null) { if (!Unchecked(likeDs)) Error($"LIKEDS({likeDs}): {likeDs} isn't defined"); }
                    else if (target.Fields is null && target.Spec?.LikeDs is null) Error($"LIKEDS({likeDs}): {likeDs} isn't a data structure");
                    else
                    {
                        Complete(target);
                        sym.Fields = target.Fields;
                    }
                }
                else
                {
                    sym.Cat = Category(spec.Keyword);
                    foreach (var field in sym.Fields?.Values ?? Enumerable.Empty<Sym>()) Complete(field);
                }
                if (sym.Inz is { } inz && Type(inz) is var value && !Assignable(sym.Cat, value, "="))
                    Error($"INZ: {sym.Name} is {Word(sym.Cat)}, and its INZ is {Word(value)}");
            }
        }
        finally
        {
            (_scope, _line) = (scope, line);
            sym.Completing = false;
            sym.Completed = true;
        }
    }

    /// <summary>The files: each one's record formats and fields are names of the program.</summary>
    private void Files()
    {
        foreach (var file in _program.Files)
        {
            _line = file.Line;
            Add(_global, new Sym(file.Name, Kind.File, file.Line) { Completed = true });
            if (file.Disk)
            {
                // A database file: its fields are the program's, and its record format a name it can use.
                if (_context.Database?.Invoke(file.ExtFile ?? file.Name) is not { } database)
                {
                    if (_context.Database is not null) Error($"the file {file.ExtFile ?? file.Name} isn't found: CRTPF, CRTLF or CREATE TABLE makes it");
                    _missingFile = true;
                    continue;
                }
                foreach (var format in database.Formats) _formats.Add(file.Renames?.GetValueOrDefault(format.Format) ?? format.Format);
                // I specs can give a field another name in the program.
                var renamed = _program.InputRecords.Where(r => database.Formats.Any(f => f.Format == r.Format || file.Renames?.GetValueOrDefault(f.Format) == r.Format))
                    .SelectMany(r => r.Fields).Where(f => f.Name != f.External).GroupBy(f => f.External).ToDictionary(g => g.Key, g => g.First().Name);
                foreach (var field in database.Formats.SelectMany(f => f.Fields))
                {
                    var name = renamed.TryGetValue(field.Name, out var inputName) ? inputName
                        : file.Prefix is { } prefix ? Interpreter.Prefixed(field.Name, prefix, file.PrefixReplace) : field.Name;
                    _global.Names.TryAdd(name, new Sym(name, Kind.Variable, file.Line) { Cat = Category(field.Type.Kind), FromFile = true, Completed = true });
                }
                continue;
            }
            DisplayFile? dds = null;
            if (file.Printer == 0)
            {
                dds = _context.File(file.Name, false);
                if (dds is null) Error($"the display file {file.Name} isn't found: CRTDSPF makes it");
            }
            else if (file.External)
            {
                dds = _context.File(file.Name, true);
                if (dds is null) Error($"the printer file {file.Name} isn't found: CRTPRTF makes it (or give PRINTER a line length, for a program-described file)");
            }
            if (dds is null)
            {
                _missingFile |= file.Printer == 0 || file.External;
                continue;
            }
            foreach (var record in dds.Records.Values)
            {
                // Record formats are kept apart: whether a procedure or variable can share a format's name isn't checked.
                _formats.Add(record.Name);
                foreach (var field in record.Fields.Where(f => f.Name is not null))
                {
                    _global.Names.TryAdd(field.Name!, new Sym(field.Name!, Kind.Variable, file.Line)
                    {
                        Cat = field.Numeric ? Cat.Num : Cat.Char,
                        FromFile = true,
                        Completed = true,
                    });
                }
            }
            foreach (var (record, _) in file.Subfiles)
            {
                if (!dds.Records.TryGetValue(record, out var format)) Error($"SFILE({record}): {file.Name} has no record format {record}");
                else if (!format.Subfile) Error($"SFILE({record}): {record} isn't a subfile record (SFL)");
            }
        }
    }

    /// <summary>The names a file's keywords give: SFILE's record number, INDDS, INFDS and OFLIND.</summary>
    private void FileKeywords()
    {
        foreach (var file in _program.Files)
        {
            _line = file.Line;
            foreach (var number in file.Subfiles.Values)
            {
                if (_global.Find(number) is not { Kind: Kind.Variable } sym) Error($"SFILE: the record number field {number} isn't defined");
                else if (sym.Cat is not (Cat.Num or Cat.Unknown)) Error($"SFILE: the record number field {number} has to be numeric");
            }
            foreach (var (keyword, name) in new[] { ("INDDS", file.IndDs), ("INFDS", file.InfDs) })
            {
                if (name is null) continue;
                if (_global.Find(name) is not { Cat: Cat.Ds or Cat.Unknown, Kind: Kind.Variable }) Error($"{keyword}({name}): {name} isn't a data structure of the program");
            }
            if (file.Overflow is { } overflow && !overflow.StartsWith("*IN") && _global.Find(overflow) is not { Cat: Cat.Ind or Cat.Unknown, Kind: Kind.Variable })
                Error($"OFLIND({overflow}): {overflow} isn't an indicator of the program");
        }
    }

    /// <summary>A procedure's interface has to match its prototype.</summary>
    private void MatchPrototype(Procedure procedure)
    {
        if (!_program.Prototypes.TryGetValue(procedure.Name, out var prototype)) return;
        _line = procedure.Line;
        if (prototype.Params.Count != procedure.Params.Count)
        {
            Error($"the prototype of {procedure.Name} has {Count(prototype.Params.Count, "parameter")}, and its procedure interface {procedure.Params.Count}");
            return;
        }
        for (var i = 0; i < prototype.Params.Count; i++)
        {
            var (declared, actual) = (Parameter(prototype.Params[i]), Parameter(procedure.Params[i]));
            if (declared.Cat != Cat.Unknown && actual.Cat != Cat.Unknown && declared.Cat != actual.Cat)
                Error($"parameter {i + 1} of {procedure.Name} is {Word(declared.Cat)} in its prototype, and {Word(actual.Cat)} in its procedure interface");
        }
        var (protoReturns, procReturns) = (prototype.Returns is null, procedure.Returns is null);
        if (protoReturns != procReturns) Error($"the prototype of {procedure.Name} and its procedure interface don't agree on whether it returns a value");
    }

    private static string Count(int n, string what) => $"{n} {what}{(n == 1 ? "" : "s")}";

    /// <summary>A parameter of a prototype or procedure interface, its type worked out where the program's globals are.</summary>
    private Sym Parameter(VarDecl parameter)
    {
        if (_parameters.TryGetValue(parameter, out var sym))
        {
            Complete(sym);
            return sym;
        }
        sym = Variable(parameter, _global);
        _parameters[parameter] = sym;
        Complete(sym);
        return sym;
    }

    // ---- statements ----

    private void Body(IReadOnlyList<Stmt> body, bool loop, bool subroutine)
    {
        foreach (var statement in body) Statement(statement, loop, subroutine);
    }

    private void Statement(Stmt statement, bool loop, bool subroutine)
    {
        _line = statement.Line;
        switch (statement)
        {
            case AssignStmt assign:
            {
                var target = Target(assign.Target);
                var value = Type(assign.Value);
                _line = statement.Line;
                if (!Assignable(target, value, assign.Op))
                {
                    Error(assign.Op == "="
                        ? $"{Describe(assign.Target)} is {Word(target)}, and can't be given {Word(value)}{Hint(target, value)}"
                        : $"{assign.Op} doesn't work on {Word(target)} and {Word(value)}");
                }
                break;
            }
            case IfStmt branches:
                foreach (var (condition, body) in branches.Branches)
                {
                    _line = statement.Line;
                    Condition(condition, "IF");
                    Body(body, loop, subroutine);
                }
                if (branches.Else is { } otherwise) Body(otherwise, loop, subroutine);
                break;
            case LoopStmt repeat:
                Condition(repeat.Condition, repeat.Until ? "DOU" : "DOW");
                Body(repeat.Body, loop: true, subroutine);
                break;
            case ForStmt count:
            {
                if (count.Variable is not UnsupportedExpr && Target(count.Variable) is not (Cat.Num or Cat.Unknown)) Error("FOR's index has to be numeric");
                foreach (var e in new[] { count.Start, count.Limit, count.By })
                {
                    if (e is not null && Type(e) is not (Cat.Num or Cat.Unknown)) Error("FOR's start, limit and BY have to be numeric");
                }
                Body(count.Body, loop: true, subroutine);
                break;
            }
            case ForEachStmt each:
                if (each.Variable is not UnsupportedExpr) Target(each.Variable);
                if (each.Array is not UnsupportedExpr) Type(each.Array);
                Body(each.Body, loop: true, subroutine);
                break;
            case SelectStmt select:
                foreach (var (condition, body) in select.Whens)
                {
                    _line = statement.Line;
                    Condition(condition, "WHEN");
                    Body(body, loop, subroutine);
                }
                if (select.Other is { } other) Body(other, loop, subroutine);
                break;
            case MonitorStmt monitor:
                Body(monitor.Body, loop, subroutine);
                foreach (var handler in monitor.Handlers) Body(handler.Body, loop, subroutine);
                break;
            case SortaStmt sorta:
                Type(sorta.Array);
                break;
            case EvalCorrStmt corr:
                if (Target(corr.Target) is not (Cat.Ds or Cat.Unknown) || Type(corr.Source) is not (Cat.Ds or Cat.Unknown))
                    Error("EVAL-CORR needs two data structures");
                break;
            case TestStmt test:
                Type(test.Value);
                break;
            case DataAreaStmt area when area.Name != "*DTAARA":
                Lookup(area.Name);
                break;
            case SendMessageStmt message:
                Type(message.Message);
                break;
            case MoveStmt move:
                Target(move.Target);
                Type(move.Source);
                break;
            case CallProgramStmt call:
                Type(call.Program);
                foreach (var (target, before, after) in call.Parameters)
                {
                    Target(target);
                    if (before is not null) Type(before);
                    if (after is not null) Target(after);
                }
                break;
            case TimeStmt time:
                Target(time.Target);
                break;
            case BitStmt bits:
                Target(bits.Target);
                break;
            case TotalStmt total:
                Body(total.Body, loop, subroutine);
                break;
            case GotoStmt jump when !HasTag(_scope.Procedure, jump.Label):
                Error($"there's no TAG {jump.Label}");
                break;
            case JumpStmt { Signal: Signal.Leave or Signal.Iter } jump when !loop:
                Error($"{(jump.Signal == Signal.Leave ? "LEAVE" : "ITER")} isn't in a loop (DOW, DOU, FOR or FOR-EACH)");
                break;
            case JumpStmt { Signal: Signal.LeaveSr } when !subroutine:
                Error("LEAVESR isn't in a subroutine");
                break;
            case JumpStmt { Signal: Signal.Return } jump:
                Return(jump.Value);
                break;
            case ExsrStmt exsr:
                if (!_scope.Procedure.Subroutines.ContainsKey(exsr.Name))
                    Error($"there's no subroutine {exsr.Name}{(_scope.Procedure == _program.Main ? "" : $" in procedure {_scope.Procedure.Name}")}");
                break;
            case CallStmt call:
                CallStatement(call.Call);
                break;
            case DsplyStmt dsply:
                if (dsply.Message is not null) Type(dsply.Message);
                if (dsply.Response is not null) Target(dsply.Response);
                break;
            case ClearStmt clear:
                if (clear.Target is NameExpr { Name: var name } && _scope.Find(name) is null && _formats.Contains(name)) break;
                Target(clear.Target);
                break;
            case SqlStmt sql:
                HostVariables(sql.Sql);
                break;
            case DisplayStmt display:
                Display(display);
                break;
            case PrintStmt print:
                Type(print.Data);
                break;
        }
    }

    private static bool HasTag(Procedure procedure, string label) =>
        Tags(procedure.Body).Contains(label) || procedure.Subroutines.Values.Any(b => Tags(b).Contains(label));

    private static IEnumerable<string> Tags(IReadOnlyList<Stmt> body) => body.SelectMany(s => s switch
    {
        TagStmt tag => [tag.Label],
        IfStmt i => i.Branches.SelectMany(b => Tags(b.Body)).Concat(i.Else is null ? [] : Tags(i.Else)),
        LoopStmt l => Tags(l.Body),
        ForStmt f => Tags(f.Body),
        SelectStmt s2 => s2.Whens.SelectMany(w => Tags(w.Body)).Concat(s2.Other is null ? [] : Tags(s2.Other)),
        TotalStmt t => Tags(t.Body),
        MonitorStmt m => Tags(m.Body),
        _ => [],
    });

    private void Condition(Expr condition, string operation)
    {
        if (condition is UnsupportedExpr) return;
        var cat = Type(condition);
        if (cat is not (Cat.Ind or Cat.Unknown))
            Error($"{operation}'s condition is {Word(cat)}, not an indicator: compare it, such as {Describe(condition)} {(cat == Cat.Num ? "<> 0" : "<> ''")}");
    }

    private void Return(Expr? value)
    {
        var procedure = _scope.Procedure;
        if (procedure == _program.Main)
        {
            if (value is not null) Error("the main procedure can't return a value: RETURN ends the program");
            return;
        }
        if (_returns is null)
        {
            if (value is not null) Error($"{procedure.Name} doesn't return a value: its RETURN can't have one");
            return;
        }
        if (value is null)
        {
            Error($"{procedure.Name} returns a value: its RETURN needs one");
            return;
        }
        var cat = Type(value);
        if (!Assignable(_returns.Cat, cat, "=")) Error($"{procedure.Name} returns {Word(_returns.Cat)}, not {Word(cat)}");
    }

    private void Display(DisplayStmt display)
    {
        foreach (var key in display.Keys ?? (display.Key is null ? [] : [display.Key])) Type(key);
        if (display.Data is not null) Target(display.Data);
        if (_formats.Contains(display.Record) || _global.Find(display.Record) is { Kind: Kind.File }) return;
        if (!_missingFile) Error($"{display.Op} {display.Record}: {display.Record} isn't a record format of the program's files");
    }

    /// <summary>The host variables of an SQL statement have to be the program's (SQL0312 on an IBM i).</summary>
    private void HostVariables(SqlTranslation sql)
    {
        foreach (var name in sql.Inputs.Concat(sql.Outputs.SelectMany(o => new[] { o.Name, o.Indicator })))
        {
            if (name is null || name.StartsWith('*')) continue;
            var path = name.ToUpperInvariant().Split('.');
            var sym = _scope.Find(path[0]);
            for (var i = 1; sym is not null && i < path.Length; i++)
            {
                Complete(sym);
                sym = sym.Fields?.GetValueOrDefault(path[i]);
            }
            if (path.Any(Unchecked)) continue;
            if (sym is null or { Kind: not Kind.Variable }) { if (!_missingFile) Error($"SQL: the host variable :{name} isn't defined"); }
            else if (sym.Unsupported is { } why) Unsupported($"{sym.Name}: {why}");
        }
    }

    // ---- calls ----

    private void CallStatement(Expr call)
    {
        var (name, args) = call switch
        {
            NameExpr n => (n.Name, (IReadOnlyList<Expr>)[]),
            CallExpr { Target: NameExpr n } c => (n.Name, c.Args),
            _ => (null, []),
        };
        if (name is null)
        {
            Type(call);
            return;
        }
        var sym = _scope.Find(name);
        if (sym is null)
        {
            if (!Unchecked(name)) Error($"{name} isn't defined: a procedure needs a prototype or DCL-PROC");
            foreach (var arg in args) Type(arg);
            return;
        }
        if (sym.Kind != Kind.Procedure)
        {
            Error($"{name} isn't a procedure: this isn't a statement");
            return;
        }
        Arguments(sym, args);
    }

    /// <summary>Checks a call's arguments against the parameters, and gives what the procedure returns.</summary>
    private Cat Arguments(Sym procedure, IReadOnlyList<Expr> args)
    {
        var parameters = procedure.Prototype?.Params ?? procedure.Procedure?.Params ?? [];
        // A procedure the program doesn't have, called by its name or its EXTPROC name: a service program's, unless Sim i has it.
        if (procedure is { Procedure: null, Prototype.Program: null } && procedure.Prototype.ExtProc is var external
            && !(external is not null && (Interpreter.NativeNames.Contains(external) || _program.Procedures.ContainsKey(external.ToUpperInvariant()))))
            _program.BoundCalls.Add((external ?? procedure.Name).ToUpperInvariant());
        if (Unchecked(procedure.Name))
        {
            foreach (var arg in args) Type(arg);
            return Cat.Unknown;
        }
        var required = parameters.TakeWhile(p => !p.NoPass).Count();
        var name = procedure.Name;
        if (args.Count > parameters.Count) Error($"{name} takes {Count(parameters.Count, "parameter")}, not {args.Count}");
        else if (args.Count < required) Error($"{name} needs {(required == parameters.Count ? "" : "at least ")}{Count(required, "parameter")}, not {args.Count}");
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is SpecialExpr { Name: "*OMIT" })
            {
                continue;
            }
            var cat = Type(arg);
            if (i >= parameters.Count) continue;
            var declared = parameters[i];
            var parameter = Parameter(declared);
            if (!declared.Const && !declared.ByValue)
            {
                if (!IsVariable(arg))
                {
                    Error($"parameter {i + 1} of {name} is passed by reference, so it has to be a variable, not {(arg is Literal ? "a literal" : "an expression")}: CONST or VALUE on the parameter would take it");
                    continue;
                }
                if (cat != Cat.Unknown && parameter.Cat != Cat.Unknown && cat != parameter.Cat && !(Charlike(cat) && Charlike(parameter.Cat) && cat != Cat.Ind && parameter.Cat != Cat.Ind))
                    Error($"parameter {i + 1} of {name} is {Word(parameter.Cat)}, and {Describe(arg)} is {Word(cat)}");
            }
            else if (!Assignable(parameter.Cat, cat, "="))
            {
                Error($"parameter {i + 1} of {name} is {Word(parameter.Cat)}, not {Word(cat)}");
            }
        }
        var returns = procedure.Prototype?.Returns ?? procedure.Procedure?.Returns;
        if (returns is null) return Cat.Unknown;
        var sym = new Sym(name, Kind.Variable, _line) { Spec = returns, Home = _global };
        Complete(sym);
        return sym.Cat;
    }

    private bool IsVariable(Expr e) => e switch
    {
        NameExpr n => _scope.Find(n.Name) is null or { Kind: Kind.Variable },
        MemberExpr => true,
        CallExpr { Target: NameExpr n } => _scope.Find(n.Name) is null or { Kind: Kind.Variable },
        CallExpr { Target: SpecialExpr or MemberExpr } => true,
        SpecialExpr s => s.Name == "*INLR" || IsIndicator(s.Name),
        BifExpr { Name: "%SUBST" } => true,
        _ => false,
    };

    private static bool IsIndicator(string name) =>
        name.Length == 5 && name.StartsWith("*IN") && (char.IsDigit(name[3]) && char.IsDigit(name[4]) || Interpreter.IsNamedIndicator(name));

    // ---- expressions ----

    /// <summary>What can be assigned: a variable, not a constant, procedure or CONST parameter.</summary>
    private Cat Target(Expr e)
    {
        switch (e)
        {
            case NameExpr { Name: var name }:
            {
                var sym = Lookup(name);
                if (sym is null) return Cat.Unknown;
                switch (sym.Kind)
                {
                    case Kind.Constant:
                        Error($"{name} is a constant: it can't be changed");
                        return Cat.Unknown;
                    case Kind.Procedure or Kind.Format or Kind.File:
                        Error($"{name} is a {What(sym)}, not a variable");
                        return Cat.Unknown;
                }
                if (sym.ReadOnly) Error($"{name} is a CONST parameter: the procedure can't change it");
                return sym.Cat;
            }
            case CallExpr { Target: NameExpr { Name: var name } } when _scope.Find(name) is { Kind: Kind.Procedure }:
                Error($"{name} is a procedure: its result can't be assigned to");
                return Cat.Unknown;
            case CallExpr { Target: NameExpr { Name: var name } } when _scope.Find(name) is { ReadOnly: true }:
                Error($"{name} is a CONST parameter: the procedure can't change it");
                return Type(e);
            case MemberExpr or CallExpr:
                return Type(e);
            case SpecialExpr s when s.Name == "*INLR" || IsIndicator(s.Name):
                return Cat.Ind;
            case BifExpr { Name: "%SUBST" or "%LEN" or "%SUBARR" or "%OCCUR" or "%ELEM" } bif:
                return Type(bif);
            case BifExpr { Name: "%STR" } str:
                foreach (var arg in str.Args) Type(arg);
                return Cat.Char;
            case UnsupportedExpr:
                return Cat.Unknown;
            default:
                Error($"{Describe(e)} isn't a variable, so it can't be assigned to");
                return Cat.Unknown;
        }
    }

    private Sym? Lookup(string name)
    {
        var sym = _scope.Find(name);
        if (sym is null)
        {
            if (_formats.Contains(name)) Error($"{name} is a record format, not a variable");
            else if (!_missingFile && !Unchecked(name)) Error($"{name} isn't defined");
            return null;
        }
        Complete(sym);
        if (sym.Unsupported is { } why) Unsupported($"{name}: {why}");
        return sym;
    }

    private static string What(Sym sym) => sym.Kind switch
    {
        Kind.Procedure => "procedure",
        Kind.Format => "record format",
        Kind.File => "file",
        Kind.Constant => "constant",
        _ => "variable",
    };

    /// <summary>The type of an expression; names that aren't defined, and types that don't fit, are reported.</summary>
    private Cat Type(Expr e)
    {
        switch (e)
        {
            case Literal { Value: var value }:
                return value switch
                {
                    string => Cat.Char,
                    decimal or double => Cat.Num,
                    DateOnly => Cat.Date,
                    TimeOnly => Cat.Time,
                    DateTime => Cat.Stamp,
                    _ => Cat.Unknown,
                };
            case NameExpr { Name: var name }:
            {
                var sym = Lookup(name);
                if (sym is null) return Cat.Unknown;
                switch (sym.Kind)
                {
                    case Kind.Procedure:
                        Error($"{name} is a procedure: in an expression, call it as {name}()");
                        return Cat.Unknown;
                    case Kind.Format or Kind.File:
                        Error($"{name} is a {What(sym)}, not a value");
                        return Cat.Unknown;
                }
                return sym.Cat;
            }
            case SpecialExpr { Name: var special }:
                return special is "*ON" or "*OFF" or "*INLR" || IsIndicator(special) ? Cat.Ind : Cat.Unknown;
            case MemberExpr member:
            {
                var sym = Member(member);
                return sym?.Cat ?? Cat.Unknown;
            }
            case CallExpr call:
                return Call(call);
            case BifExpr bif:
                return Builtin(bif);
            case UnaryExpr { Op: "NOT", Operand: var operand }:
            {
                var cat = Type(operand);
                if (cat is not (Cat.Ind or Cat.Unknown)) Error($"NOT needs an indicator, not {Word(cat)}");
                return Cat.Ind;
            }
            case UnaryExpr { Operand: var operand }:
            {
                var cat = Type(operand);
                if (cat is not (Cat.Num or Cat.Unknown)) Error($"a minus sign needs a number, not {Word(cat)}");
                return Cat.Num;
            }
            case BinaryExpr { Op: "IN" } member:
                Type(member.Left);
                Type(member.Right);
                return Cat.Ind;
            case BinaryExpr { Op: "AND" or "OR" } logical:
            {
                foreach (var side in new[] { logical.Left, logical.Right })
                {
                    var cat = Type(side);
                    if (cat is not (Cat.Ind or Cat.Unknown)) Error($"{logical.Op} needs indicators on both sides, not {Word(cat)}");
                }
                return Cat.Ind;
            }
            case BinaryExpr { Op: "=" or "<>" or "<" or ">" or "<=" or ">=" } comparison:
            {
                var (left, right) = (Type(comparison.Left), Type(comparison.Right));
                if (!Comparable(left, right)) Error($"{Word(left)} and {Word(right)} can't be compared{Hint(left, right)}");
                return Cat.Ind;
            }
            case BinaryExpr arithmetic:
                return Arithmetic(arithmetic.Op, Type(arithmetic.Left), Type(arithmetic.Right));
            default:
                return Cat.Unknown;
        }
    }

    /// <summary>The subfield a qualified name names: ds.field, ds(i).field.</summary>
    private Sym? Member(MemberExpr member)
    {
        var target = member.Target switch
        {
            NameExpr { Name: var name } => Lookup(name),
            CallExpr { Target: NameExpr { Name: var name } } call => Element(name, call),
            MemberExpr inner => Member(inner),
            CallExpr { Target: MemberExpr inner } call => ElementOf(Member(inner), call),
            _ => null,
        };
        if (target is null) return null;
        Complete(target);
        if (target.Fields is null)
        {
            if (target.Cat != Cat.Unknown) Error($"{Describe(member.Target)} isn't a data structure, so it has no subfield {member.Field}");
            return null;
        }
        if (target.Fields.TryGetValue(member.Field, out var field))
        {
            Complete(field);
            return field;
        }
        if (!Unchecked(member.Field) && !Unchecked(target.Name)) Error($"{Describe(member.Target)} has no subfield {member.Field}");
        return null;
    }

    private Sym? Element(string name, CallExpr call)
    {
        var sym = Lookup(name);
        return sym is null ? null : ElementOf(sym, call);
    }

    private Sym? ElementOf(Sym? sym, CallExpr call)
    {
        foreach (var arg in call.Args) Index(arg);
        if (sym is null) return null;
        if (!sym.Array) Error($"{Describe(call.Target)} isn't an array");
        else if (call.Args.Count != 1) Error($"{Describe(call.Target)} takes one index, not {call.Args.Count}");
        return sym;
    }

    private void Index(Expr e)
    {
        var cat = Type(e);
        if (cat is not (Cat.Num or Cat.Unknown)) Error($"an array index has to be a number, not {Word(cat)}");
    }

    private Cat Call(CallExpr call)
    {
        switch (call.Target)
        {
            case SpecialExpr { Name: "*IN" }:
                if (call.Args.Count != 1) Error($"*IN takes one index, not {call.Args.Count}");
                foreach (var arg in call.Args) Index(arg);
                return Cat.Ind;
            case NameExpr { Name: "@SIGN" }:
                foreach (var arg in call.Args) Type(arg);
                return Cat.Num;
            case NameExpr { Name: var name }:
            {
                var sym = _scope.Find(name);
                if (sym is { Kind: Kind.Procedure })
                {
                    var returns = Arguments(sym, call.Args);
                    if ((sym.Prototype?.Returns ?? sym.Procedure?.Returns) is null) Error($"{name} doesn't return a value, so it can't be in an expression");
                    return returns;
                }
                if (sym is { Kind: Kind.Format or Kind.File or Kind.Constant })
                {
                    Error($"{name} is a {What(sym)}: it can't take parameters or an index");
                    return Cat.Unknown;
                }
                if (sym is null && !_missingFile && !Unchecked(name))
                {
                    Error($"{name} isn't defined");
                    foreach (var arg in call.Args) Type(arg);
                    return Cat.Unknown;
                }
                return Element(name, call)?.Cat ?? Cat.Unknown;
            }
            case MemberExpr member:
                return ElementOf(Member(member), call)?.Cat ?? Cat.Unknown;
            default:
                return Cat.Unknown;
        }
    }

    // ---- built-in functions ----

    /// <summary>
    /// What a built-in function takes and gives: the fewest and most parameters, what each is (C
    /// character, N numeric, D a date, time or timestamp, * anything; the last repeats), and its result.
    /// </summary>
    private static readonly Dictionary<string, (int Min, int Max, string Takes, Cat Gives)> BifSpecs = new()
    {
        ["%TRIM"] = (1, 2, "CC", Cat.Char), ["%TRIMR"] = (1, 2, "CC", Cat.Char), ["%TRIML"] = (1, 2, "CC", Cat.Char),
        ["%LEN"] = (1, 2, "**", Cat.Num), ["%SIZE"] = (1, 2, "**", Cat.Num), ["%DECPOS"] = (1, 1, "N", Cat.Num),
        ["%SUBST"] = (2, 3, "CNN", Cat.Char), ["%LEFT"] = (2, 2, "CN", Cat.Char), ["%RIGHT"] = (2, 2, "CN", Cat.Char),
        ["%SCAN"] = (2, 4, "CCNN", Cat.Num), ["%SCANRPL"] = (3, 5, "CCCNN", Cat.Char), ["%REPLACE"] = (2, 4, "CCNN", Cat.Char),
        ["%XLATE"] = (3, 4, "CCCN", Cat.Char), ["%UPPER"] = (1, 3, "CNN", Cat.Char), ["%LOWER"] = (1, 3, "CNN", Cat.Char),
        ["%CONCAT"] = (2, 999, "*", Cat.Char), ["%CHECK"] = (2, 3, "CCN", Cat.Num), ["%CHECKR"] = (2, 3, "CCN", Cat.Num),
        ["%CHAR"] = (1, 2, "**", Cat.Char), ["%DEC"] = (1, 3, "*NN", Cat.Num), ["%DECH"] = (1, 3, "*NN", Cat.Num),
        ["%INT"] = (1, 1, "*", Cat.Num), ["%INTH"] = (1, 1, "*", Cat.Num), ["%UNS"] = (1, 1, "*", Cat.Num), ["%UNSH"] = (1, 1, "*", Cat.Num),
        ["%FLOAT"] = (1, 1, "*", Cat.Num), ["%EDITC"] = (2, 3, "NC*", Cat.Char), ["%ABS"] = (1, 1, "N", Cat.Num),
        ["%SQRT"] = (1, 1, "N", Cat.Num), ["%REM"] = (2, 2, "NN", Cat.Num), ["%DIV"] = (2, 2, "NN", Cat.Num),
        ["%MIN"] = (2, 999, "*", Cat.Unknown), ["%MAX"] = (2, 999, "*", Cat.Unknown),
        ["%ERROR"] = (0, 0, "", Cat.Ind), ["%PARMS"] = (0, 0, "", Cat.Num), ["%PASSED"] = (1, 1, "*", Cat.Ind), ["%OMITTED"] = (1, 1, "*", Cat.Ind),
        ["%ELEM"] = (1, 2, "**", Cat.Num), ["%XFOOT"] = (1, 1, "N", Cat.Num), ["%LOOKUP"] = (2, 4, "**NN", Cat.Num),
        ["%MAXARR"] = (1, 3, "*NN", Cat.Num), ["%MINARR"] = (1, 3, "*NN", Cat.Num),
        ["%DATE"] = (0, 2, "**", Cat.Date), ["%TIME"] = (0, 2, "**", Cat.Time), ["%TIMESTAMP"] = (0, 3, "**N", Cat.Stamp),
        ["%DAYS"] = (1, 1, "N", Cat.Duration), ["%MONTHS"] = (1, 1, "N", Cat.Duration), ["%YEARS"] = (1, 1, "N", Cat.Duration),
        ["%HOURS"] = (1, 1, "N", Cat.Duration), ["%MINUTES"] = (1, 1, "N", Cat.Duration), ["%SECONDS"] = (1, 1, "N", Cat.Duration),
        ["%DIFF"] = (3, 4, "DD*N", Cat.Num), ["%SUBDT"] = (2, 4, "D*NN", Cat.Num),
        ["%SCANR"] = (2, 4, "CCNN", Cat.Num), ["%EDITW"] = (2, 2, "NC", Cat.Char), ["%EDITFLT"] = (1, 1, "N", Cat.Char),
        ["%CHARCOUNT"] = (1, 2, "C*", Cat.Num), ["%SUBARR"] = (2, 3, "*NN", Cat.Unknown), ["%SPLIT"] = (1, 2, "CC", Cat.Unknown),
        ["%LOOKUPLT"] = (2, 4, "**NN", Cat.Num), ["%LOOKUPLE"] = (2, 4, "**NN", Cat.Num), ["%LOOKUPGT"] = (2, 4, "**NN", Cat.Num),
        ["%LOOKUPGE"] = (2, 4, "**NN", Cat.Num), ["%MSECONDS"] = (1, 1, "N", Cat.Duration), ["%PROC"] = (0, 0, "", Cat.Char),
        ["%SHTDN"] = (0, 0, "", Cat.Ind), ["%OPEN"] = (1, 1, "F", Cat.Ind), ["%STR"] = (1, 2, "*N", Cat.Char),
        ["%ALLOC"] = (1, 1, "N", Cat.Unknown), ["%REALLOC"] = (2, 2, "*N", Cat.Unknown), ["%ADDR"] = (1, 2, "**", Cat.Unknown),
        ["%MSG"] = (1, 3, "CC*", Cat.Unknown), ["%TARGET"] = (1, 2, "**", Cat.Unknown),
        ["%KDS"] = (1, 2, "*N", Cat.Unknown),
        ["%EOF"] = (0, 1, "F", Cat.Ind), ["%FOUND"] = (0, 1, "F", Cat.Ind), ["%EQUAL"] = (0, 1, "F", Cat.Ind), ["%STATUS"] = (0, 1, "F", Cat.Num),
    };

    private Cat Builtin(BifExpr bif)
    {
        if (!BifSpecs.TryGetValue(bif.Name, out var spec))
        {
            foreach (var arg in bif.Args) Type(arg);
            return Cat.Unknown;
        }
        var count = bif.Args.Count;
        if (count < spec.Min || count > spec.Max)
        {
            Error(spec.Min == spec.Max
                ? $"{bif.Name} takes {Count(spec.Min, "parameter")}, not {count}"
                : spec.Max == 999 ? $"{bif.Name} takes at least {Count(spec.Min, "parameter")}, not {count}"
                : $"{bif.Name} takes {spec.Min} to {Count(spec.Max, "parameter")}, not {count}");
        }
        var cats = new List<Cat>();
        for (var i = 0; i < count; i++)
        {
            var arg = bif.Args[i];
            var takes = spec.Takes.Length == 0 ? '*' : spec.Takes[Math.Min(i, spec.Takes.Length - 1)];
            if (takes == 'F')
            {
                if (arg is not NameExpr { Name: var file } || _scope.Find(file) is not { Kind: Kind.File } && !Unchecked(file)) Error($"{bif.Name} takes a file of the program, not {Describe(arg)}");
                cats.Add(Cat.Unknown);
                continue;
            }
            var cat = Type(arg);
            cats.Add(cat);
            var fits = takes switch
            {
                'C' => cat is Cat.Unknown || Charlike(cat),
                'N' => cat is Cat.Unknown or Cat.Num,
                'D' => cat is Cat.Unknown or Cat.Date or Cat.Time or Cat.Stamp,
                _ => cat != Cat.Duration || bif.Name is "%MIN" or "%MAX",
            };
            if (!fits) Error($"parameter {i + 1} of {bif.Name} has to be {Word(takes switch { 'C' => Cat.Char, 'N' => Cat.Num, _ => Cat.Date })}, not {Word(cat)}");
        }
        if (bif.Name is "%MIN" or "%MAX")
        {
            var known = cats.Where(c => c != Cat.Unknown).Distinct().ToList();
            if (known.Count > 1 && !(known.All(Charlike))) Error($"{bif.Name}'s parameters have to be of one type: {string.Join(", ", known.Select(Word))}");
            return known.FirstOrDefault();
        }
        if (bif.Name == "%DIFF" && cats is [var a, var b, ..] && a != Cat.Unknown && b != Cat.Unknown && a != b && !(a is Cat.Date or Cat.Stamp && b is Cat.Date or Cat.Stamp))
            Error($"%DIFF can't take {Word(a)} and {Word(b)}");
        return spec.Gives;
    }

    // ---- types ----

    private static bool Charlike(Cat cat) => cat is Cat.Char or Cat.Ind or Cat.Ds;

    private static bool Moment(Cat cat) => cat is Cat.Date or Cat.Time or Cat.Stamp;

    private static bool Assignable(Cat target, Cat value, string op)
    {
        if (target == Cat.Unknown || value == Cat.Unknown) return true;
        return op switch
        {
            "=" => target switch
            {
                Cat.Char or Cat.Ds => Charlike(value),
                Cat.Num => value == Cat.Num,
                Cat.Ind => value is Cat.Ind or Cat.Char,
                Cat.Date or Cat.Time or Cat.Stamp => value == target,
                _ => false,
            },
            "+=" => target == Cat.Num && value == Cat.Num || target is Cat.Char && value is Cat.Char or Cat.Ind || Moment(target) && value == Cat.Duration,
            "-=" => target == Cat.Num && value == Cat.Num || Moment(target) && value == Cat.Duration,
            _ => target == Cat.Num && value == Cat.Num,
        };
    }

    private static bool Comparable(Cat left, Cat right) =>
        left == Cat.Unknown || right == Cat.Unknown || left == right && left != Cat.Duration || Charlike(left) && Charlike(right);

    private Cat Arithmetic(string op, Cat left, Cat right)
    {
        if (left == Cat.Unknown || right == Cat.Unknown)
        {
            var known = left == Cat.Unknown ? right : left;
            if (op is "*" or "/" or "**" && known is not (Cat.Num or Cat.Unknown)) Error($"{op} needs numbers, not {Word(known)}");
            return op is "*" or "/" or "**" ? Cat.Num : known is Cat.Duration ? Cat.Unknown : op == "+" ? known : known is Cat.Num ? Cat.Num : Cat.Unknown;
        }
        switch (op)
        {
            case "+":
                if (left == Cat.Num && right == Cat.Num) return Cat.Num;
                if (Charlike(left) && Charlike(right)) return Cat.Char;
                if (Moment(left) && right == Cat.Duration) return left;
                if (left == Cat.Duration && Moment(right)) return right;
                break;
            case "-":
                if (left == Cat.Num && right == Cat.Num) return Cat.Num;
                if (Moment(left) && right == Cat.Duration) return left;
                if (Moment(left) && Moment(right))
                {
                    Error($"{Word(left)} minus {Word(right)} isn't RPG: %DIFF gives the time between them");
                    return Cat.Num;
                }
                break;
            default:
                if (left == Cat.Num && right == Cat.Num) return Cat.Num;
                break;
        }
        Error($"{op} doesn't work on {Word(left)} and {Word(right)}");
        return Cat.Unknown;
    }

    private static string Word(Cat cat) => cat switch
    {
        Cat.Char => "character",
        Cat.Num => "numeric",
        Cat.Ind => "an indicator",
        Cat.Date => "a date",
        Cat.Time => "a time",
        Cat.Stamp => "a timestamp",
        Cat.Ds => "a data structure",
        Cat.Duration => "a duration (such as %DAYS)",
        _ => "something else",
    };

    private static string Hint(Cat target, Cat value) => (target, value) switch
    {
        (Cat.Num, Cat.Char) => ": %DEC or %INT makes a number of it",
        (Cat.Char, Cat.Num) => ": %CHAR or %EDITC makes characters of it",
        (Cat.Date, Cat.Char) => ": %DATE makes a date of it",
        (Cat.Time, Cat.Char) => ": %TIME makes a time of it",
        (Cat.Stamp, Cat.Char) => ": %TIMESTAMP makes a timestamp of it",
        (Cat.Char, Cat.Date or Cat.Time or Cat.Stamp) => ": %CHAR makes characters of it",
        _ => "",
    };

    private static string Describe(Expr e) => e switch
    {
        NameExpr n => n.Name,
        MemberExpr m => $"{Describe(m.Target)}.{m.Field}",
        CallExpr c => $"{Describe(c.Target)}(...)",
        SpecialExpr s => s.Name,
        BifExpr b => $"{b.Name}(...)",
        Literal { Value: string s } => $"'{s}'",
        Literal { Value: var v } => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "the literal",
        _ => "the expression",
    };
}
