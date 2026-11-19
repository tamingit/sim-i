// A parsed RPG program: declarations, procedures and statements.

namespace SimI.Rpg;

// ---- expressions ----

internal abstract record Expr;

/// <summary>A literal: a string or a decimal.</summary>
internal sealed record Literal(object Value) : Expr;

internal sealed record NameExpr(string Name) : Expr;

/// <summary>A special word, such as *ON, *BLANKS or *INLR. <see cref="All"/> is the literal of *ALL'x'.</summary>
internal sealed record SpecialExpr(string Name, string? All = null) : Expr;

/// <summary>name(args): a procedure call or an array element, told apart when it runs.</summary>
internal sealed record CallExpr(Expr Target, IReadOnlyList<Expr> Args) : Expr;

/// <summary>A subfield: target.field.</summary>
internal sealed record MemberExpr(Expr Target, string Field) : Expr;

internal sealed record BifExpr(string Name, IReadOnlyList<Expr> Args) : Expr;

internal sealed record UnaryExpr(string Op, Expr Operand) : Expr;

internal sealed record BinaryExpr(string Op, Expr Left, Expr Right) : Expr;

/// <summary>An expression Sim i can't evaluate. Reaching it ends the program with this message.</summary>
internal sealed record UnsupportedExpr(string Message) : Expr;

// ---- statements ----

internal abstract record Stmt(int Line);

/// <param name="Truncate">Fixed form's arithmetic (ADD, Z-ADD ...): a result too long for its field loses its high-order digits, with no error.</param>
internal sealed record AssignStmt(int Line, Expr Target, string Op, Expr Value, bool HalfAdjust, bool Right, bool Truncate = false) : Stmt(Line);

/// <summary>MOVE, MOVEL and MOVEA: characters (or digits) moved right- or left-justified, padded with P; a date's format in <see cref="Format"/>.</summary>
internal sealed record MoveStmt(int Line, string Kind, Expr Target, Expr Source, bool Pad, string? Format) : Stmt(Line);

/// <summary>GOTO a TAG, in the same body or one round it.</summary>
internal sealed record GotoStmt(int Line, string Label) : Stmt(Line);

internal sealed record TagStmt(int Line, string Label) : Stmt(Line);

/// <summary>CALL with PARM: each parameter's field, the value it's given first (factor 2), and the field it's copied to after (factor 1).</summary>
internal sealed record CallProgramStmt(int Line, Expr Program, IReadOnlyList<(Expr Target, Expr? Before, Expr? After)> Parameters, bool ErrorExtender) : Stmt(Line);

/// <summary>TIME: the time (and date) into a numeric field, or a time, date or timestamp.</summary>
internal sealed record TimeStmt(int Line, Expr Target) : Stmt(Line);

/// <summary>TESTN, TESTB, BITON and BITOFF: bits of a character field, or whether it's digits; the indicators they set (null for none).</summary>
internal sealed record BitStmt(int Line, string Op, Expr Target, Expr? Bits, IReadOnlyList<Expr?> Indicators) : Stmt(Line);

/// <summary>EXCEPT: the O specs' exception records of this name (or with none).</summary>
internal sealed record ExceptStmt(int Line, string? Name) : Stmt(Line);

/// <summary>Total-time calculations (L1 to L9 and LR): run by the RPG cycle, not with the detail calculations.</summary>
internal sealed record TotalStmt(int Line, IReadOnlyList<Stmt> Body) : Stmt(Line);

internal sealed record IfStmt(int Line, IReadOnlyList<(Expr Condition, IReadOnlyList<Stmt> Body)> Branches, IReadOnlyList<Stmt>? Else) : Stmt(Line);

/// <summary>DOW, or DOU when <see cref="Until"/>.</summary>
internal sealed record LoopStmt(int Line, Expr Condition, bool Until, IReadOnlyList<Stmt> Body) : Stmt(Line);

internal sealed record ForStmt(int Line, Expr Variable, Expr? Start, Expr? Limit, Expr? By, bool Down, IReadOnlyList<Stmt> Body) : Stmt(Line);

internal sealed record ForEachStmt(int Line, Expr Variable, Expr Array, IReadOnlyList<Stmt> Body) : Stmt(Line);

internal sealed record SelectStmt(int Line, IReadOnlyList<(Expr Condition, IReadOnlyList<Stmt> Body)> Whens, IReadOnlyList<Stmt>? Other) : Stmt(Line);

/// <summary>LEAVE, ITER, LEAVESR, or RETURN with its value.</summary>
internal sealed record JumpStmt(int Line, Signal Signal, Expr? Value = null) : Stmt(Line);

internal sealed record ExsrStmt(int Line, string Name) : Stmt(Line);

internal sealed record CallStmt(int Line, Expr Call) : Stmt(Line);

internal sealed record DsplyStmt(int Line, Expr? Message, Expr? Response) : Stmt(Line);

internal sealed record ClearStmt(int Line, Expr Target) : Stmt(Line);

internal sealed record MonitorStmt(int Line, IReadOnlyList<Stmt> Body, IReadOnlyList<MonitorHandler> Handlers) : Stmt(Line);

/// <summary>ON-ERROR (statuses, or <see cref="Any"/> for *PROGRAM, *FILE, *ALL or none) or ON-EXCP (message ids), and what it runs.</summary>
internal sealed record MonitorHandler(IReadOnlyList<int> Statuses, IReadOnlyList<string> MessageIds, bool Any, IReadOnlyList<Stmt> Body)
{
    public bool Catches(RpgError e) => MessageIds.Count > 0 ? MessageIds.Contains(e.MessageId) : Any || Statuses.Contains(e.Status);
}

/// <summary>SORTA: an array (or %SUBARR of one, or a structure array's subfield, ds(*).key) sorted, descending with (D).</summary>
internal sealed record SortaStmt(int Line, Expr Array, bool Descending) : Stmt(Line);

/// <summary>EVAL-CORR: the subfields of the same name, from one structure to the other.</summary>
internal sealed record EvalCorrStmt(int Line, Expr Target, Expr Source) : Stmt(Line);

/// <summary>TEST(D), (T), (Z) or (N): whether the value is a valid date, time, timestamp or zoned number (%ERROR), in the format given.</summary>
internal sealed record TestStmt(int Line, string Extender, string? Format, Expr Value) : Stmt(Line);

/// <summary>IN, OUT or UNLOCK of a data area: the variable that has DTAARA, or *DTAARA for all of them.</summary>
internal sealed record DataAreaStmt(int Line, string Op, string Name) : Stmt(Line);

/// <summary>SND-MSG: a message of a type (*INFO, *ESCAPE ...), with %MSG's id or text.</summary>
internal sealed record SendMessageStmt(int Line, string Type, Expr Message) : Stmt(Line);

internal sealed record SqlStmt(int Line, SqlTranslation Sql) : Stmt(Line);

/// <summary>
/// An operation on a file's records: EXFMT, WRITE, READ, READC, CHAIN or UPDATE on a display file's
/// record format, or on a database file CHAIN, SETLL, SETGT, READ, READE, READP, READPE, WRITE,
/// UPDATE, DELETE, UNLOCK and FEOD. <see cref="Keys"/> is the search argument (a list, or %KDS);
/// <see cref="Data"/> a result data structure; <see cref="Fields"/> UPDATE's %FIELDS.
/// </summary>
internal sealed record DisplayStmt(int Line, string Op, string Record, Expr? Key, bool ErrorExtender, IReadOnlyList<Expr>? Keys = null,
    Expr? Data = null, IReadOnlyList<string>? Fields = null, bool NoLock = false) : Stmt(Line);

/// <summary>OPEN or CLOSE of a file (CLOSE *ALL: File is *ALL).</summary>
internal sealed record FileStmt(int Line, string Op, string File, bool ErrorExtender) : Stmt(Line);

/// <summary>WRITE to a printer file: one line of output.</summary>
internal sealed record PrintStmt(int Line, string File, Expr Data) : Stmt(Line);

/// <summary>A statement Sim i can't run. Reaching it ends the program with this message.</summary>
internal sealed record UnsupportedStmt(int Line, string Message) : Stmt(Line);

internal enum Signal
{
    None,
    Leave,
    Iter,
    LeaveSr,
    Return,

    /// <summary>GOTO: to the TAG the interpreter's label names, in this body or one round it.</summary>
    Goto,
}

// ---- declarations ----

/// <summary>
/// A type as written: a keyword (CHAR, PACKED ...) with its length and decimals, LIKE or LIKEDS, or
/// a data structure's subfields. Lengths are expressions, as they can name constants.
/// </summary>
internal sealed class TypeSpec
{
    public string? Keyword { get; set; }
    public Expr? Length { get; set; }
    public Expr? Decimals { get; set; }
    public string? Like { get; set; }
    public string? LikeDs { get; set; }
    public string? ExtName { get; set; }

    /// <summary>EXTFLD(name): a subfield that is the external field of that name, renamed; "" for EXTFLD alone (the field of its own name).</summary>
    public string? ExtFld { get; set; }
    public Expr? Dim { get; set; }
    public bool Qualified { get; set; }
    public List<VarDecl>? Fields { get; set; }

    /// <summary>PSDS: the program status data structure, filled in by the program.</summary>
    public bool Psds { get; set; }

    /// <summary>OVERLAY(name): the subfield it lies over (or the structure's name), and where in it: a position, *NEXT, or null for its start.</summary>
    public string? Overlay { get; set; }
    public Expr? OverlayAt { get; set; }

    /// <summary>BASED(pointer): the pointer variable that says where the variable is.</summary>
    public string? Based { get; set; }

    /// <summary>LIKE(name : +n): how much longer (or shorter) than what it's like.</summary>
    public Expr? LikeAdjust { get; set; }

    /// <summary>POS with a named constant: worked out when the program starts.</summary>
    public Expr? PositionExpr { get; set; }

    /// <summary>PREFIX(prefix : n): the external fields' names, their first n characters replaced by the prefix.</summary>
    public string? Prefix { get; set; }
    public int PrefixReplace { get; set; }

    /// <summary>LIKEREC(format : *KEY ...) and EXTNAME(file : *KEY ...): the record format, and which fields.</summary>
    public string? LikeRec { get; set; }
    public string? FieldSet { get; set; }

    /// <summary>DIM(*VAR : n) or DIM(*AUTO : n): an array whose number of elements changes (V or A); null for a fixed one.</summary>
    public char? VaryingDim { get; set; }

    /// <summary>OCCURS(n): a multiple-occurrence data structure.</summary>
    public Expr? Occurs { get; set; }

    /// <summary>CTDATA: the compile-time data's records; PERRCD, how many elements each has.</summary>
    public IReadOnlyList<string>? CompileTimeData { get; set; }
    public Expr? PerRecord { get; set; }
    public bool CtData { get; set; }

    /// <summary>DTAARA: the data area the variable is (IN and OUT), *LDA, or with <see cref="DtaAraAuto"/> read at the start and written at the end.</summary>
    public string? DtaAra { get; set; }
    public bool DtaAraAuto { get; set; }
}

internal abstract record Decl(int Line, string Name);

/// <summary>A variable, subfield or parameter. <see cref="Position"/> is POS(n) of an indicator subfield, or 0.
/// <see cref="Options"/> are a parameter's OPTIONS, such as *NOPASS.</summary>
internal sealed record VarDecl(int Line, string Name, TypeSpec Type, Expr? Inz = null, int Position = 0,
    bool Const = false, bool ByValue = false, IReadOnlyList<string>? Options = null) : Decl(Line, Name)
{
    public bool NoPass => Options?.Contains("*NOPASS") == true;

    /// <summary>A field a fixed-form calculation defines (a result field's length, *LIKE DEFINE), which can define a name again.</summary>
    public bool Calculated { get; init; }
}

internal sealed record ConstDecl(int Line, string Name, Expr Value) : Decl(Line, Name);

/// <summary>A workstation file. <see cref="Subfiles"/> maps a subfile record to its relative record number field.</summary>
/// <param name="Printer">The line width of a printer file, or 0 when it's a display file.</param>
/// <param name="External">A printer file with no length: its records are a printer file's DDS (CRTPRTF), if there is one.</param>
/// <param name="UserOpen">USROPN: opened by OPEN, not when the program starts.</param>
/// <param name="InfDs">INFDS: the file information data structure.</param>
/// <param name="Overflow">OFLIND: the indicator a printer file sets when it reaches its overflow line.</param>
/// <param name="Disk">A database file: its records read and written by key (<paramref name="Keyed"/>) or in arrival order.</param>
/// <param name="ExtFile">EXTFILE or EXTDESC: the database file it is, when that isn't its name.</param>
/// <param name="Renames">RENAME(external : internal): record formats by another name.</param>
/// <param name="Prefix">PREFIX(prefix : n): its fields' names, their first n characters replaced by the prefix.</param>
/// <param name="Primary">USAGE(*INPUT) with the RPG cycle: the primary (or secondary) file it reads.</param>
internal sealed record FileDecl(int Line, string Name, string? IndDs, IReadOnlyDictionary<string, string> Subfiles, int Printer = 0,
    bool External = false, bool UserOpen = false, string? InfDs = null, string? Overflow = null, bool Disk = false, bool Keyed = false,
    string? ExtFile = null, IReadOnlyDictionary<string, string>? Renames = null, string? Prefix = null, int PrefixReplace = 0, string? Usage = null,
    char Primary = ' ');

/// <summary>A prototype. <see cref="Program"/> is the program EXTPGM names, or null for a procedure; <see cref="Returns"/> the type it returns.</summary>
/// <param name="ExtProc">EXTPROC: the procedure's external name (a C function, an API, or another module's procedure), or null.</param>
internal sealed record ProtoDecl(string Name, string? Program, IReadOnlyList<VarDecl> Params, TypeSpec? Returns = null, int Line = 0, string? ExtProc = null);

internal sealed class Procedure(string name)
{
    public string Name { get; } = name;

    /// <summary>The line of its DCL-PROC.</summary>
    public int Line { get; init; }

    public List<VarDecl> Params { get; set; } = [];
    public TypeSpec? Returns { get; set; }
    public List<Decl> Locals { get; } = [];
    public List<Stmt> Body { get; set; } = [];
    public Dictionary<string, IReadOnlyList<Stmt>> Subroutines { get; } = [];

    /// <summary>EXPORT: other programs can call it, from its service program.</summary>
    public bool Exported { get; set; }

    /// <summary>ON-EXIT: what runs when the procedure ends, however it ends; the indicator set when it ends in error.</summary>
    public List<Stmt>? OnExit { get; set; }
    public string? OnExitIndicator { get; set; }
}

internal sealed class RpgProgram
{
    /// <summary>The main procedure: the global declarations, the program's parameters and its mainline.</summary>
    public Procedure Main { get; } = new("*MAIN");

    /// <summary>CTL-OPT MAIN(name): the procedure that runs instead of a mainline.</summary>
    public string? MainProcedure { get; set; }

    /// <summary>CTL-OPT NOMAIN: a module of procedures, with no mainline (a service program's).</summary>
    public bool NoMain { get; set; }

    /// <summary>CTL-OPT DATFMT and TIMFMT: the formats dates and times have as characters, when none is given.</summary>
    public string DateFormat { get; set; } = "*ISO";
    public string TimeFormat { get; set; } = "*ISO";

    /// <summary>The compile-time data after the source (**CTDATA name, or ** in the order of the CTDATA arrays).</summary>
    public List<(string? Name, List<string> Records)> CompileTimeData { get; } = [];

    /// <summary>CTL-OPT BNDDIR: the binding directories its prototyped procedures are found through.</summary>
    public List<string> BindingDirectories { get; } = [];

    public List<FileDecl> Files { get; } = [];
    public Dictionary<string, Procedure> Procedures { get; } = [];
    public Dictionary<string, ProtoDecl> Prototypes { get; } = [];

    /// <summary>Every DECLARE CURSOR, by cursor name: declared before the program runs, wherever it's written.</summary>
    public Dictionary<string, SqlTranslation> Cursors { get; } = [];

    /// <summary>Where each cursor is declared: its line, and the procedure (or the main procedure) it's in.</summary>
    public Dictionary<string, (int Line, Procedure Procedure)> CursorDeclarations { get; } = [];

    /// <summary>
    /// Names whose declarations Sim i doesn't support (a POINTER, a DISK file, a prototype with
    /// EXTPROC, ...), and procedures with parameters it doesn't: checking takes them as they're used.
    /// </summary>
    public HashSet<string> UnsupportedNames { get; } = [];

    /// <summary>Procedures called through a prototype (not EXTPGM) that the program doesn't have: a service program's (filled in by checking).</summary>
    public HashSet<string> BoundCalls { get; } = [];

    /// <summary>Fixed form's *ENTRY PLIST: the fields that are the program's parameters.</summary>
    public List<string> EntryParameters { get; } = [];

    /// <summary>Fixed form's I and O specs, for the RPG cycle.</summary>
    public List<InputRecord> InputRecords { get; } = [];
    public List<OutputRecord> OutputRecords { get; } = [];

    /// <summary>The O specs' conditions, parsed.</summary>
    public Dictionary<string, Expr> OutputConditions { get; } = [];

    /// <summary>The line of the first EXEC SQL, or 0 when there's none.</summary>
    public int FirstSqlLine { get; set; }

    public List<Diagnostic> Diagnostics { get; } = [];
}
