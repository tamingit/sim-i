// The fake's system beyond its servers: source members, the programs and display files made
// from them, the CL commands that work on them, and calling a program. Everything is in the Store.

using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using FakeIbmI.Dds;
using FakeIbmI.Rpg;
using Npgsql;

namespace FakeIbmI;

/// <summary>
/// A database file as RPG sees it: see <see cref="Machine.FindDatabaseFile"/>. <see cref="RowId"/> is the
/// column that's its relative record number (the hidden qfake_rrn, or PostgreSQL's ctid for a table without one).
/// </summary>
internal sealed record DatabaseFile(string Library, string Name, string Format, string Table, IReadOnlyList<DbField> Fields,
    IReadOnlyList<(string Field, bool Descending)> Keys, bool Unique, string? Where, string RowId = Store.RowNumber)
{
    /// <summary>A multiple-format logical file's other record formats, each over its own physical file.</summary>
    public IReadOnlyList<DatabaseFile> Others { get; init; } = [];

    /// <summary>The file's record formats, its first first.</summary>
    public IEnumerable<DatabaseFile> Formats => Others.Prepend(this);
}

/// <summary>
/// How a CL command went. <see cref="Known"/> is false for a command the machine doesn't run at all.
/// <see cref="MessageId"/> is the escape message of one that failed, as MONMSG sees it (such as CPF9801).
/// </summary>
public sealed record CommandResult(bool Ok, string Message, bool Known = true, string? MessageId = null);

public sealed partial class Machine : IDisposable
{
    private readonly ConcurrentDictionary<(string Library, string Name), (long Version, RpgProgram Program)> _programs = new();
    private readonly ConcurrentDictionary<(string Library, string Name), (long Version, DisplayFile File)> _displayFiles = new();
    private readonly ConcurrentDictionary<(string Library, string Name), (long Version, Cl.ClProgram Program)> _clPrograms = new();

    private readonly string? _temporaryIfs;

    /// <summary>A machine on this store, with <paramref name="ifsDir"/> as the IFS's root (null: a temporary folder, gone when the machine is).</summary>
    public Machine(Store store, string? ifsDir = null)
    {
        Store = store;
        if (ifsDir is null)
        {
            _temporaryIfs = Path.Combine(Path.GetTempPath(), "fake-ibmi-ifs-" + Guid.NewGuid().ToString("N"));
            ifsDir = _temporaryIfs;
        }
        Ifs = new Ifs(ifsDir);
    }

    /// <summary>
    /// A machine on a PostgreSQL database (a connection string), or (null) on a database of its own on the
    /// scratch server (see <see cref="Store.ScratchServer"/>), gone when the machine is.
    /// </summary>
    public static Machine Open(string? database = null, string? ifsDir = null) =>
        new(database is null ? Store.Open(Store.ScratchServer, temporary: true) : Store.Open(database), ifsDir);

    public Store Store { get; }

    /// <summary>What stops a running program for a debugger (see <see cref="Debugging.DapServer"/>).</summary>
    internal Debugging.DebugEngine Debugger { get; } = new();

    /// <summary>The integrated file system: a folder on the host.</summary>
    public Ifs Ifs { get; }

    /// <summary>The library members and objects go to when none is named.</summary>
    public string CurrentLibrary => Store.Current;

    /// <summary>
    /// The library a user's names are looked for in first, and new source goes to: the one their profile names
    /// (DFTLIB), when there is such a library, else QGPL.
    /// </summary>
    public string DefaultLibrary(string user) =>
        Store.FindUser(user.ToUpperInvariant())?.DefaultLibrary.ToUpperInvariant() is { } library && Store.HasLibrary(library) ? library : CurrentLibrary;

    /// <summary>What the translator writes where SQL names no library for a table it creates: the job's default library.</summary>
    internal const string DefaultSchemaMarker = "QFAKE_DFTLIB";

    /// <summary>The SQL with the job's default library where the translator left its marker.</summary>
    internal string Resolve(string sql, string user)
    {
        if (!sql.Contains(DefaultSchemaMarker, StringComparison.Ordinal)) return sql;
        var library = DefaultLibrary(user);
        return sql.Replace(DefaultSchemaMarker, Store.SchemaName(library), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        Store.Dispose();
        if (_temporaryIfs is not null && Directory.Exists(_temporaryIfs))
        {
            try
            {
                Directory.Delete(_temporaryIfs, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [GeneratedRegex(@"CREATE\s+(?:TABLE|VIEW)\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:""?[\w#$@]+""?\.)?""?([\w#$@]+)""?", RegexOptions.IgnoreCase)]
    private static partial Regex CreatedName();

    [GeneratedRegex(@"^\s*CREATE\s+(?:SCHEMA|COLLECTION)\s+(\w+)\s*;?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex CreatedLibrary();

    [GeneratedRegex("^[A-Z#$@][A-Z0-9#$@_.]{0,9}$")]
    private static partial Regex ObjectName();

    /// <summary>Whether this is a name an object, member or library can have.</summary>
    public static bool IsName(string name) => ObjectName().IsMatch(name);

    // ---- source types ----

    /// <summary>
    /// The source type for a file extension (.rpgle, .sqlrpgle, .rpgleinc, .dspf, .prtf, .sql, .clle, .cmd),
    /// or null for one the fake doesn't read.
    /// </summary>
    public static string? TypeForExtension(string extension) => extension.TrimStart('.').ToUpperInvariant() switch
    {
        "RPGLE" => "RPGLE",
        "SQLRPGLE" => "SQLRPGLE",
        "RPGLEINC" => "RPGLEINC",
        "DSPF" => "DSPF",
        "PRTF" => "PRTF",
        "PF" => "PF",
        "LF" => "LF",
        "SQL" => "SQL",
        // Db2 DDL, as ACS's Generate SQL and ARCAD keep it.
        "TABLE" or "VIEW" or "INDEX" => "SQL",
        "CPY" => "RPGLEINC",
        // Text members: often copy members of old.
        "TXT" => "TXT",
        "CLLE" => "CLLE",
        "CLP" => "CLLE",
        "CMD" => "CMD",
        // A message file's source: the CL that makes it.
        "MSGF" => "MSGF",
        _ => null,
    };

    /// <summary>The source file a member of this type goes in when none is named.</summary>
    public static string DefaultFile(string type) => type switch
    {
        "RPGLE" or "SQLRPGLE" or "RPGLEINC" => "QRPGLESRC",
        "DSPF" or "PRTF" or "PF" or "LF" => "QDDSSRC",
        "SQL" => "QSQLSRC",
        "CLLE" => "QCLLESRC",
        "CMD" => "QCMDSRC",
        "MSGF" => "QMSGFSRC",
        _ => "QTXTSRC",
    };

    /// <summary>
    /// What /COPY and /INCLUDE bring into a member: the source of the member they name, looked for from
    /// <paramref name="first"/>, or a stream file in the IFS (a relative path from <paramref name="directory"/>,
    /// the folder of a stream file being compiled, or else the root).
    /// </summary>
    internal RpgIncludes Includes(string? first, string? directory = null) => new(
        (library, file, name) => Store.FindMember(library, file, name, first)?.Source,
        (path, from) => ReadStream(path, from ?? directory ?? "/"),
        directory);

    /// <summary>A stream file's text and absolute path, or a /QSYS.LIB member's; null when there's none (or the path leads out of the IFS).</summary>
    internal (string Text, string Path)? ReadStream(string path, string current)
    {
        var absolute = Ifs.Absolute(path, current);
        if (Ifs.QsysMember(absolute) is var (library, file, member))
            return Store.FindMember(library, file, member)?.Source is { } source ? (source, absolute) : null;
        try
        {
            return Ifs.ReadText(absolute) is { } text ? (text, absolute) : null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The folder of an IFS path: /home/me for /home/me/prog.rpgle.</summary>
    internal static string FolderOf(string path) => path.LastIndexOf('/') is var at and > 0 ? path[..at] : "/";

    // ---- checking and saving ----

    /// <summary>What the fake can't run in this source, and what it translated or ignored, in line order.</summary>
    public IReadOnlyList<Diagnostic> Check(string type, string name, string source, string? library = null, string? path = null)
    {
        var directory = path is null ? null : FolderOf(path);
        IEnumerable<Diagnostic> found = type switch
        {
            "RPGLE" or "SQLRPGLE" => CheckedRpg(type, source, library, directory).Diagnostics,
            "RPGLEINC" => RpgParser.Parse(source, Store.PrepareProblem, Includes(library, directory)).Diagnostics,
            "DSPF" => DisplayFile.Parse(name, source, reference: References(library)).Diagnostics,
            "PRTF" => DisplayFile.Parse(name, source, printer: true, References(library)).Diagnostics,
            "PF" or "LF" => DatabaseDds.Parse(name, source, type == "LF", References(library)).Diagnostics,
            "SQL" => CheckScript(source),
            "CLLE" => Cl.ClProgram.Parse(source).Diagnostics,
            "CMD" => Cl.CommandDefinition.Parse(source).Diagnostics,
            "TXT" => [],
            "MSGF" => [.. Commands(source).Select(c => (c.Line, Command: TryParse(c.Text))).Where(c => c.Command is null)
                .Select(c => new Diagnostic(c.Line, Severity.Error, "this isn't a CL command"))],
            _ => [new Diagnostic(0, Severity.Unsupported, $"source type {type} isn't supported: only RPGLE, SQLRPGLE, RPGLEINC, DSPF, PRTF, PF, LF, SQL, CLLE and CMD")],
        };
        return [.. found.OrderBy(d => d.Line)];
    }

    /// <summary>
    /// An RPG member, checked as CRTBNDRPG or CRTSQLRPGI would: its syntax, then its names, types and
    /// calls, with the files and tables it names as they are now.
    /// </summary>
    private RpgProgram CheckedRpg(string type, string source, string? library, string? directory = null)
    {
        var program = RpgParser.Parse(source, Store.PrepareProblem, Includes(library, directory));
        var context = new Rpg.RpgContext(
            (name, printer) => LoadFile(name, null, printer, library)
                ?? (Store.FindMemberOfType(name, [printer ? "PRTF" : "DSPF"], library) is { } member ? DisplayFile.Parse(member.Name, member.Source, printer, References(member.Library)) : null),
            table => ExternalFields(table, library),
            type == "SQLRPGLE",
            name => FindDatabaseFile(name, library));
        Rpg.RpgChecker.Check(program, context);
        if (type == "RPGLE" && program.FirstSqlLine > 0)
            program.Diagnostics.Add(new Diagnostic(program.FirstSqlLine, Severity.Error, "EXEC SQL needs SQLRPGLE source, which CRTSQLRPGI compiles: CRTBNDRPG doesn't take it"));
        return program;
    }

    /// <summary>
    /// Binding a program: the procedures it calls through a prototype that it doesn't have itself,
    /// and that no service program exports (through its binding directories, or any service program,
    /// as calls find them). On an IBM i the program isn't created (CPF5D04).
    /// </summary>
    private CommandResult? Unresolved(RpgProgram program, string name, string library)
    {
        var missing = program.BoundCalls.Where(procedure => !ServicePrograms(program.BindingDirectories, library)
            .Any(o => Parsed(o) is var service && service.Procedures.TryGetValue(procedure, out var p) && (p.Exported || service.NoMain))).ToList();
        if (missing.Count == 0) return null;
        return new CommandResult(false,
            $"Program {name} not created: no service program exports {string.Join(", ", missing)}{(program.BindingDirectories.Count > 0 ? $" (binding directory {string.Join(", ", program.BindingDirectories)})" : "")}.",
            MessageId: "CPF5D04");
    }

    private List<Diagnostic> CheckScript(string source)
    {
        var found = new List<Diagnostic>();
        var created = new List<string>();
        string? schema = null;
        foreach (var (text, line) in SqlTranslator.SplitScript(source))
        {
            schema = SqlTranslator.SetSchema(text) ?? schema;
            var translation = SqlTranslator.Translate(text, schema);
            if (translation.Sql is { } made && CreatedName().Match(made) is { Success: true } match) created.Add(match.Groups[1].Value);
            found.AddRange(translation.Notes.Select(note => new Diagnostic(line, Severity.Info, note)));
            if (translation.Kind == SqlKind.Unsupported)
            {
                found.Add(new Diagnostic(line, Severity.Unsupported, translation.Problem!));
            }
            else if (translation.Kind is not (SqlKind.Execute or SqlKind.Select or SqlKind.NoOp) || translation.Inputs.Any(i => i != "*USER"))
            {
                found.Add(new Diagnostic(line, Severity.Unsupported, "a script can't use host variables, cursors or prepared statements"));
            }
            else if (translation.Sql is { } sql && Store.PrepareProblem(sql) is { } problem && !problem.Contains("already exists"))
            {
                // A script often creates the table its next statement uses, so a missing one isn't an error yet.
                var missing = problem.Contains("no such table") || problem.Contains("no such column");
                if (created.Any(name => problem.EndsWith($"no such table: {name}", StringComparison.OrdinalIgnoreCase)
                    || problem.EndsWith($".{name}", StringComparison.OrdinalIgnoreCase) && problem.Contains("no such table"))) continue;
                found.Add(new Diagnostic(line, missing ? Severity.Warning : Severity.Unsupported,
                    missing ? $"{problem} (yet)" : $"the database doesn't take this statement, even translated: {problem}"));
            }
        }
        return found;
    }

    /// <summary>Saves the member and what checking it found, which it returns.</summary>
    public IReadOnlyList<Diagnostic> Save(Member member)
    {
        var diagnostics = Check(member.Type, member.Name, member.Source, member.Library);
        Store.SaveMember(member, diagnostics);
        return diagnostics;
    }

    // ---- compiling ----

    /// <summary>
    /// Makes the member's object: a program from RPGLE or SQLRPGLE, a display file from DSPF; an
    /// SQL member is run. A program is made even with lines the fake can't run, which end it if reached.
    /// </summary>
    public CommandResult Compile(Member member, string? library = null, string? name = null, string user = "QUSER", string? objectType = null, string attributes = "")
    {
        library ??= member.Library;
        name ??= member.Name;
        if (!Store.HasLibrary(library)) return new CommandResult(false, $"Library {library} not found.", MessageId: "CPF9810");
        var diagnostics = Check(member.Type, member.Name, member.Source, member.Library, member.Path);
        if (member.Path is not null) attributes = $"{attributes} SRCSTMF({member.Path})".Trim();
        // Source an IBM i's compiler rejects makes nothing; the compile command gives the message id.
        if (diagnostics.Count(d => d.Severity == Severity.Error) is > 0 and var errors)
            return new CommandResult(false, $"{member} not compiled: {errors} error{(errors == 1 ? "" : "s")} in the source: see the member's diagnostics.");
        var unsupported = diagnostics.Count(d => d.Severity == Severity.Unsupported);
        var caveat = unsupported == 0 ? "" : $" {unsupported} line{(unsupported == 1 ? "" : "s")} can't be run: see the member's diagnostics.";
        switch (member.Type)
        {
            case "RPGLE" or "SQLRPGLE":
            {
                // A module of procedures (NOMAIN) is made a service program unless something else is asked for.
                var program = CheckedRpg(member.Type, member.Source, member.Library, member.Path is null ? null : FolderOf(member.Path));
                var noMain = program.NoMain;
                var type = objectType ?? (noMain ? "*SRVPGM" : "*PGM");
                if (type == "*PGM" && noMain) return new CommandResult(false, $"Program {name} not created: {member} has CTL-OPT NOMAIN, so it's a module.", MessageId: "RNS9310");
                if (type == "*PGM" && Unresolved(program, name, library) is { } unbound) return unbound;
                Store.SaveObject(library, name, type, $"{member.Type} {attributes}".Trim(), member.Source);
                var what = type switch { "*MODULE" => "Module", "*SRVPGM" => "Service program", _ => "Program" };
                return new CommandResult(true, $"{what} {name} created in library {library}.{caveat}");
            }
            case "PF" or "LF":
                return CreateDatabaseFile(member, library, name);
            case "DSPF":
                Store.SaveObject(library, name, "*FILE", ("DSPF " + attributes).Trim(), member.Source);
                return new CommandResult(true, $"File {name} created in library {library}.{caveat}");
            case "PRTF":
                Store.SaveObject(library, name, "*FILE", ("PRTF " + attributes).Trim(), member.Source);
                return new CommandResult(true, $"File {name} created in library {library}.{caveat}");
            case "CLLE":
                Store.SaveObject(library, name, "*PGM", $"CLLE {attributes}".Trim(), member.Source);
                return new CommandResult(true, $"Program {name} created in library {library}.{caveat}");
            case "CMD":
                if (attributes.Length == 0) return new CommandResult(true, $"Command {name} not created: CRTCMD makes it, naming its program.");
                Store.SaveObject(library, name, "*CMD", attributes, member.Source);
                return new CommandResult(true, $"Command {name} created in library {library}.{caveat}");
            case "RPGLEINC":
                return new CommandResult(true, $"Member {member} is a copy member: there's nothing to create.");
            case "MSGF":
                return BuildMessageFile(member, library, name, user);
            case "SQL":
            {
                // Its unqualified names are in the member's library, as a build's RUNSQLSTM with DFTRDBCOL has them.
                var (ran, failures) = RunScript(member.Source, user, member.Library);
                return new CommandResult(failures.Count == 0,
                    failures.Count == 0 ? $"{ran} SQL statement{(ran == 1 ? "" : "s")} run from {member}." : $"{failures.Count} of {ran + failures.Count} SQL statements failed. First: {failures[0]}");
            }
            default:
                return new CommandResult(false, $"Member {member.Name} has source type {member.Type}, which isn't supported.");
        }
    }

    /// <summary>Runs each statement of a script, carrying on after one fails. Returns how many ran and what failed.</summary>
    public (int Ran, IReadOnlyList<string> Failures) RunScript(string source, string user, string? schema = null)
    {
        var ran = 0;
        var failures = new List<string>();
        var current = schema is not null && Store.HasLibrary(schema) ? schema.ToUpperInvariant() : null;
        foreach (var (text, line) in SqlTranslator.SplitScript(source))
        {
            if (SqlTranslator.SetSchema(text) is { } set)
            {
                if (!Store.HasLibrary(set) && set != "QTEMP") failures.Add($"line {line}: Library {set} not found.");
                else current = set;
                ran++;
                continue;
            }
            // CREATE SCHEMA (or COLLECTION) makes a library.
            if (CreatedLibrary().Match(text) is { Success: true } created)
            {
                try
                {
                    Store.CreateLibrary(created.Groups[1].Value);
                    ran++;
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException)
                {
                    failures.Add($"line {line}: {e.Message}");
                }
                continue;
            }
            var translation = SqlTranslator.Translate(text, current);
            if (translation.Kind == SqlKind.NoOp)
            {
                ran++;
                continue;
            }
            if (translation.Kind is not (SqlKind.Execute or SqlKind.Select) || translation.Inputs.Any(i => i != "*USER"))
            {
                failures.Add($"line {line}: {translation.Problem ?? "a script can't use host variables, cursors or prepared statements"}");
                continue;
            }
            try
            {
                lock (Store.Gate)
                {
                    using var command = Store.Connection.CreateCommand();
                    command.CommandText = Resolve(translation.Sql!, user);
                    for (var i = 0; i < translation.Inputs.Count; i++) command.Parameters.AddWithValue($"h{i}", user);
                    command.ExecuteNonQuery();
                }
                ran++;
            }
            catch (PostgresException e)
            {
                failures.Add($"line {line}: {Store.Clean(e)}");
            }
        }
        return (ran, failures);
    }

    // ---- objects ----

    /// <summary>The library and name in LIB/NAME. No library, *LIBL or *CURLIB is null: any library.</summary>
    internal static (string? Library, string Name) Qualified(string text)
    {
        var parts = text.Trim().ToUpperInvariant().Split('/');
        if (parts.Length == 1) return (null, parts[0]);
        return (parts[0] is "*LIBL" or "*CURLIB" or "" ? null : parts[0], parts[1]);
    }

    /// <summary>Whether a program of this name has been created.</summary>
    public bool HasProgram(string? library, string name, string? user = null) =>
        Store.FindObject(library, name, "*PGM", user is null ? null : DefaultLibrary(user)) is not null;

    internal RpgProgram? LoadProgram(string? library, string name, string? user = null) =>
        Store.FindObject(library, name, "*PGM", user is null ? null : DefaultLibrary(user)) is { Kind: not "CLLE" } o ? Parsed(o) : null;

    /// <summary>The program an RPG object was made from, parsed, kept until the object changes.</summary>
    private RpgProgram Parsed(StoredObject o)
    {
        var cached = _programs.GetValueOrDefault((o.Library, o.Name + o.Type));
        if (cached.Version == o.Version) return cached.Program;
        var program = RpgParser.Parse(o.Source, null, Includes(o.Library, o.StreamFile is { } stream ? FolderOf(stream) : null));
        _programs[(o.Library, o.Name + o.Type)] = (o.Version, program);
        return program;
    }

    /// <summary>A keyword's value in an object's attributes, as CRTDSPF and CRTPRTF keep them: "DSPF WAITRCD(2)".</summary>
    private static string? AttributeValue(string attributes, string keyword) =>
        Regex.Match(attributes, $@"\b{keyword}\(([^)]*)\)", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value.Trim() : null;

    internal DisplayFile? LoadDisplayFile(string name, string? user = null) => LoadFile(name, user, printer: false);

    /// <summary>A printer file made with CRTPRTF from DDS, or null when there's none of that name.</summary>
    internal DisplayFile? LoadPrinterFile(string name, string? user = null) => LoadFile(name, user, printer: true);

    private DisplayFile? LoadFile(string name, string? user, bool printer, string? first = null)
    {
        if (Store.FindObject(null, name, "*FILE", first ?? (user is null ? null : DefaultLibrary(user))) is not { } o) return null;
        if (o.Attribute.StartsWith("PRTF", StringComparison.Ordinal) != printer) return null;
        var cached = _displayFiles.GetValueOrDefault((o.Library, o.Name));
        if (cached.Version == o.Version) return cached.File;
        var file = DisplayFile.Parse(o.Name, o.Source, printer, References(o.Library));
        if (AttributeValue(o.Attribute, "WAITRCD") is { } wait && int.TryParse(wait, out var seconds)) file.WaitRecord = seconds;
        if (AttributeValue(o.Attribute, "PAGESIZE")?.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var lines, var width, ..]
            && int.TryParse(lines, out var l) && int.TryParse(width, out var w))
            (file.PageLength, file.PageWidth) = (l, w);
        if (AttributeValue(o.Attribute, "OVRFLW") is { } overflow && int.TryParse(overflow, out var line)) file.OverflowLine = line;
        _displayFiles[(o.Library, o.Name)] = (o.Version, file);
        return file;
    }

    /// <summary>Calls a program with these arguments, which take back what it leaves in its parameters. Throws RpgHalt when it fails.</summary>
    internal void Call(string program, IReadOnlyList<Cell> arguments, IJobHost host, int depth = 0)
    {
        var (library, name) = Qualified(program);
        if (depth > 50) throw new RpgHalt($"{name}: programs are calling each other more than 50 deep");
        if (name is "QCMDEXC" or "QCAPCMD" && library is null or "QSYS")
        {
            // Runs the command in the first parameter, as long as the second says.
            var text = arguments.Count > 0 ? Values.ToText(arguments[0].Get()) : "";
            if (arguments.Count > 1 && arguments[1].Type.IsNumeric) text = text[..Math.Clamp((int)Values.ToDecimal(arguments[1].Get()), 0, text.Length)];
            var result = Run(text.Trim(), host, depth);
            if (!result.Ok) throw new RpgHalt(result.Message, result.MessageId ?? (result.Known ? "CPF0001" : "CPD0030"));
            return;
        }
        if (name is "QMHSNDPM" or "QMHRMVPM" && library is null or "QSYS")
        {
            ProgramMessage(name, arguments, host);
            return;
        }
        var o = Store.FindObject(library, name, "*PGM", DefaultLibrary(host.User))
            ?? throw new RpgHalt($"Program {name} in library {library ?? "*LIBL"} not found.", "CPF0001");
        if (o.Kind == "CLLE")
        {
            var cached = _clPrograms.GetValueOrDefault((o.Library, o.Name));
            if (cached.Version != o.Version) _clPrograms[(o.Library, o.Name)] = cached = (o.Version, Cl.ClProgram.Parse(o.Source));
            cached.Program.Call(name, arguments, this, host, depth);
            return;
        }
        new Interpreter(Parsed(o), name, this, host, depth, o.Library).Run(arguments);
    }

    /// <summary>
    /// The service program, activated in the job, that exports the procedure: one in the binding
    /// directories named, then any service program at all. Null when none does.
    /// </summary>
    internal Interpreter? FindExport(string procedure, IReadOnlyList<string> bindingDirectories, IJobHost host, string library)
    {
        foreach (var service in ServicePrograms(bindingDirectories, library.Length > 0 ? library : DefaultLibrary(host.User)))
        {
            var activated = Activate(service, host);
            if (activated.Exports(procedure.ToUpperInvariant())) return activated;
        }
        return null;
    }

    /// <summary>The service programs a call looks in: those of the binding directories, then every other one.</summary>
    private List<StoredObject> ServicePrograms(IReadOnlyList<string> bindingDirectories, string first)
    {
        var candidates = new List<StoredObject>();
        foreach (var directory in bindingDirectories)
        {
            var (l, n) = Qualified(directory);
            if (Store.FindObject(l, n, "*BNDDIR", first) is not { } bnddir) continue;
            foreach (var entry in bnddir.Source.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = entry.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts is not [var qualified, "*SRVPGM", ..]) continue;
                var (el, en) = Qualified(qualified);
                if (Store.FindObject(el, en, "*SRVPGM", first) is { } service) candidates.Add(service);
            }
        }
        candidates.AddRange(Store.Objects("*SRVPGM"));
        return [.. candidates.DistinctBy(o => (o.Library, o.Name))];
    }

    /// <summary>A service program's activation in the job: its globals made once, and kept while the job lasts and the program doesn't change.</summary>
    private Interpreter Activate(StoredObject service, IJobHost host)
    {
        var key = (service.Library, service.Name);
        if (host.State.ServicePrograms.TryGetValue(key, out var known) && known.Version == service.Version) return known.Program;
        var interpreter = new Interpreter(Parsed(service), service.Name, this, host, 0, service.Library);
        host.State.ServicePrograms[key] = (service.Version, interpreter);
        try
        {
            interpreter.Activate();
        }
        catch (RpgError e)
        {
            throw new RpgError(e.Status, $"Service program {service.Name} couldn't be activated: {e.Message}", e.MessageId);
        }
        return interpreter;
    }

    /// <summary>The types of a program's parameters, or null when there's no such program.</summary>
    internal IReadOnlyList<RpgType>? ParameterTypes(string? library, string name, IJobHost host) =>
        LoadProgram(library, name, host.User) is { } program ? new Interpreter(program, name, this, host).ParameterTypes() : null;

    // ---- CL commands ----

    /// <summary>
    /// Runs a CL command: CALL, CRTBNDRPG, CRTSQLRPGI, CRTDSPF, RUNSQLSTM, RUNSQL, CRTLIB, DLTLIB, DLTPGM, DLTF, SAVRSTLIB and so on.
    /// Any other comes back as not <see cref="CommandResult.Known"/>.
    /// </summary>
    internal CommandResult Run(string text, IJobHost host, int depth = 0)
    {
        ClCommand command;
        try
        {
            command = ClCommand.Parse(text);
        }
        catch (FormatException e)
        {
            return new CommandResult(false, e.Message, MessageId: "CPD0013");
        }
        if (RunBuild(command, host) is { } built) return built;
        switch (command.Name)
        {
            case "CALL":
            {
                if (command.Value("PGM", 0) is not { } program) return new CommandResult(false, "Parameter PGM required.");
                var arguments = new List<Cell>();
                foreach (var (value, quoted) in ClCommand.Values(command.Value("PARM", 1) ?? ""))
                {
                    // As CL passes literals: characters as they are, numbers as packed (15 5).
                    var number = !quoted && decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var n);
                    var cell = new Cell(number ? RpgType.Number(15, 5) : RpgType.Char(Math.Max(value.Length, 1)));
                    cell.Set(number ? decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : quoted ? value : value.ToUpperInvariant());
                    arguments.Add(cell);
                }
                try
                {
                    Call(program, arguments, host, depth);
                    return new CommandResult(true, "");
                }
                catch (RpgHalt e)
                {
                    return new CommandResult(false, e.Message, MessageId: e.MessageId ?? "CPF9999");
                }
            }
            case "CRTBNDRPG" or "CRTSQLRPGI" or "CRTDSPF" or "CRTRPGMOD" or "CRTPRTF" or "CRTBNDCL" or "CRTCLPGM" or "CRTPF" or "CRTLF":
            {
                var keyword = command.Name switch { "CRTBNDRPG" or "CRTBNDCL" or "CRTCLPGM" => "PGM", "CRTSQLRPGI" => "OBJ", "CRTRPGMOD" => "MODULE", _ => "FILE" };
                var kind = command.Name switch { "CRTDSPF" => "DSPF", "CRTPRTF" => "PRTF", "CRTPF" => "PF", "CRTLF" => "LF", "CRTBNDCL" or "CRTCLPGM" => "CLLE", _ => "RPG" };
                if (command.Value(keyword, 0) is not { } target) return new CommandResult(false, $"Parameter {keyword} required.", MessageId: "CPD0072");
                var (library, name) = Qualified(target);
                var defaultFile = kind switch { "DSPF" or "PRTF" or "PF" or "LF" => "QDDSSRC", "CLLE" => "QCLLESRC", _ => "QRPGLESRC" };
                var failed = kind switch { "DSPF" or "PRTF" or "PF" or "LF" => "CPF7302", "CLLE" => "CPF0C33", _ => command.Name == "CRTSQLRPGI" ? "SQL9001" : "RNS9310" };
                Member? member;
                if (command.Value("SRCSTMF", -1) is { } stmf)
                {
                    // Source in a stream file: compiled as if it were a member, its type from the command.
                    if (kind is "DSPF" or "PRTF" or "PF" or "LF") return new CommandResult(false, $"{command.Name} doesn't take SRCSTMF: DDS has to be in a member.", MessageId: failed);
                    var path = Ifs.Absolute(Unquoted(stmf), host.State.CurrentDirectory);
                    if (ReadStream(path, "/") is not { } stream) return new CommandResult(false, $"Stream file {path} not found.", MessageId: failed);
                    var streamType = kind == "CLLE" ? "CLLE" : command.Name == "CRTSQLRPGI" ? "SQLRPGLE" : "RPGLE";
                    member = new Member(library ?? DefaultLibrary(host.User), "*STMF", name, streamType, stream.Text, stream.Path);
                }
                else if ((member = FindSource(command, name, defaultFile, host)) is null)
                    return new CommandResult(false, $"Member {SourceMember(command, name)} not found in file {command.Value("SRCFILE", 1) ?? defaultFile}.", MessageId: failed);
                var fits = kind switch
                {
                    "DSPF" or "PRTF" or "CLLE" or "PF" or "LF" => member.Type == kind,
                    _ => member.Type is "RPGLE" or "SQLRPGLE",
                };
                if (!fits) return new CommandResult(false, $"Member {member} has source type {member.Type}, which {command.Name} doesn't compile.", MessageId: failed);
                var objectType = command.Name switch
                {
                    "CRTRPGMOD" => "*MODULE",
                    "CRTSQLRPGI" => command.Value("OBJTYPE", 3)?.ToUpperInvariant() is { } t && t is "*MODULE" or "*SRVPGM" ? t : "*PGM",
                    _ => kind == "RPG" ? "*PGM" : null,
                };
                var attributes = kind switch
                {
                    "DSPF" => command.Value("WAITRCD", -1) is { } wait ? $"WAITRCD({wait})" : "",
                    "PRTF" => string.Join(' ', new[] { "PAGESIZE", "OVRFLW" }.Where(k => command.Value(k, -1) is not null).Select(k => $"{k}({command.Value(k, -1)})")),
                    _ => "",
                };
                var compiled = Compile(member, library ?? member.Library, name, host.User, objectType, attributes);
                return compiled.Ok ? compiled : compiled with { MessageId = compiled.MessageId ?? failed };
            }
            case "RUNSQLSTM":
            {
                string script;
                if (command.Value("SRCSTMF", -1) is { } stmf)
                {
                    var path = Ifs.Absolute(Unquoted(stmf), host.State.CurrentDirectory);
                    if (ReadStream(path, "/") is not { } stream) return new CommandResult(false, $"Stream file {path} not found.", MessageId: "SQL9010");
                    script = stream.Text;
                }
                else
                {
                    if (command.Value("SRCMBR", 1) is not { } memberName) return new CommandResult(false, "Parameter SRCMBR required.", MessageId: "CPD0072");
                    var (library, file) = command.Value("SRCFILE", 0) is { } f ? Qualified(f) : (null, null);
                    var member = Store.FindMember(library, file, memberName, DefaultLibrary(host.User));
                    if (member is null) return new CommandResult(false, $"Member {memberName.ToUpperInvariant()} not found in file {file ?? "*LIBL"}.", MessageId: "SQL9010");
                    script = member.Source;
                }
                var defaultSchema = command.Value("DFTRDBCOL", -1) is { } collection && !collection.StartsWith('*') ? collection.ToUpperInvariant() : null;
                var (ran, failures) = RunScript(script, host.User, defaultSchema);
                // ERRLVL(30) and above: statements on tables that aren't there (severity 30 on Db2) don't fail the command.
                var level = int.TryParse(command.Value("ERRLVL", -1), out var errlvl) ? errlvl : 10;
                var serious = failures.Where(f => !(level >= 30 && f.Contains("no such table"))).ToList();
                return new CommandResult(serious.Count == 0,
                    failures.Count == 0 ? $"{ran} SQL statement{(ran == 1 ? "" : "s")} run."
                    : serious.Count == 0 ? $"{ran} SQL statement{(ran == 1 ? "" : "s")} run; {failures.Count} failed below ERRLVL({level})."
                    : $"{failures.Count} of {ran + failures.Count} SQL statements failed. First: {serious[0]}", MessageId: serious.Count == 0 ? null : "SQL9010");
            }
            case "RUNSQL":
            {
                if (command.Value("SQL", 0) is not { } raw) return new CommandResult(false, "Parameter SQL required.");
                var sql = ClCommand.Values(raw) is [(var quoted, true)] ? quoted : raw;
                var (ran, failures) = RunScript(sql, host.User);
                return new CommandResult(failures.Count == 0, failures.Count == 0 ? (ran == 0 ? "No SQL statement to run." : "SQL statement run.") : failures[0][(failures[0].IndexOf(':') + 2)..]);
            }
            case "OVRDBF" or "CLRPFM" or "CPYF" or "CPYFRMQRYF" or "OPNQRYF" or "CRTDUPOBJ" or "CPYTOIMPF" or "SBMJOB" or "ADDPFM" or "RMVM" or "CHGPFM" or "INZPFM" or "RGZPFM"
                or "CLOF" or "OPNDBF" or "POSDBF" or "CHGAUT" or "GRTOBJAUT" or "RVKOBJAUT" or "CHGOBJOWN" or "SNDMSG" or "SNDBRKMSG" or "STRJRNPF" or "ENDJRNPF"
                or "ALCOBJ" or "DLCOBJ" or "CHGJOB" or "DLYJOB" or "RCLRSC" or "RCLACTGRP" or "STRCMTCTL" or "ENDCMTCTL" or "CHGSPLFA" or "SNDNETSPLF"
                or "CPYFRMIMPF" or "DLTSPLF" or "CPYSPLF" or "CRTDTAQ" or "DLTDTAQ" or "CLRDTAQ" or "DSPFD" or "DSPFFD" or "STRSBS" or "ENDSBS" or "VRYCFG"
                or "HLDJOBQ" or "RLSJOBQ" or "HLDOUTQ" or "RLSOUTQ" or "CHGPRTF" or "CHGDSPF" or "ADDWSE" or "RMVWSE" or "SIGNOFF" or "CHGMSGQ" or "CLRMSGQ"
                or "CHGOUTQ" or "CLROUTQ" or "STRPRTWTR" or "ENDWTR" or "CHGWTR":
                return RunObjectCommand(command, host);
            case "SAVRSTLIB" or "SAVRSTOBJ":
                return RunSaveRestore(command, host);
            case "CRTMSGF" or "ADDMSGD" or "CHGMSGD" or "RMVMSGD" or "DLTMSGF":
                return RunMessageFile(command, host);
            case "CRTDTAARA" or "CHGDTAARA" or "DSPDTAARA" or "DLTDTAARA":
                return RunDataArea(command, host);
            case "MKDIR" or "MD" or "CRTDIR" or "RMDIR" or "RD" or "RMVLNK" or "DEL" or "ERASE" or "CHGCURDIR" or "CD" or "CHDIR"
                or "CPYTOSTMF" or "CPYFRMSTMF" or "CPY" or "COPY":
                return RunIfs(command, host);
            case "CRTLIB":
            {
                if (command.Value("LIB", 0)?.ToUpperInvariant() is not { } library) return new CommandResult(false, "Parameter LIB required.");
                try
                {
                    return Store.CreateLibrary(library)
                        ? new CommandResult(true, $"Library {library} created.")
                        : new CommandResult(false, $"Library {library} already exists.");
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException)
                {
                    return new CommandResult(false, e.Message);
                }
            }
            case "DLTLIB":
            {
                if (command.Value("LIB", 0)?.ToUpperInvariant() is not { } library) return new CommandResult(false, "Parameter LIB required.");
                try
                {
                    return Store.DeleteLibrary(library)
                        ? new CommandResult(true, $"Library {library} deleted.")
                        : new CommandResult(false, $"Library {library} not found.");
                }
                catch (ArgumentException e)
                {
                    return new CommandResult(false, e.Message);
                }
            }
            case "DLTPGM" or "DLTF":
            {
                var program = command.Name == "DLTPGM";
                if (command.Value(program ? "PGM" : "FILE", 0) is not { } target) return new CommandResult(false, $"Parameter {(program ? "PGM" : "FILE")} required.");
                var (library, name) = Qualified(target);
                if (Store.FindObject(library, name, program ? "*PGM" : "*FILE", DefaultLibrary(host.User)) is { } o)
                {
                    Store.DeleteObject(o);
                    return new CommandResult(true, $"Object {name} in {o.Library} type {o.Type} deleted.");
                }
                if (!program && IsName(name) && Store.Columns(name, library).Count > 0)
                {
                    RunScript($"DROP TABLE {(library is null ? "" : $"{library}.")}{name}", host.User);
                    return new CommandResult(true, $"File {name} deleted.");
                }
                return new CommandResult(false, $"Object {name} in library {library ?? "*LIBL"} not found.");
            }
            default:
            {
                // A command made with CRTCMD runs its command processing program.
                if (Store.FindObject(command.Library, command.Name, "*CMD", DefaultLibrary(host.User)) is { } made) return RunMade(made, command, host, depth);
                return new CommandResult(false, $"Command {command.Name} in library {command.Library ?? "*LIBL"} not found.", Known: false, MessageId: "CPD0030");
            }
        }
    }

    private static string SourceMember(ClCommand command, string name) =>
        command.Value("SRCMBR", 2)?.ToUpperInvariant() is { } m && !m.StartsWith('*') ? m : name;

    /// <summary>The source member a compile command names: SRCFILE and SRCMBR, or failing those the command's default file, then any file.</summary>
    private Member? FindSource(ClCommand command, string name, string defaultFile, IJobHost host)
    {
        var (sourceLibrary, sourceFile) = command.Value("SRCFILE", 1) is { } file ? Qualified(file) : (null, null);
        var memberName = SourceMember(command, name);
        var first = DefaultLibrary(host.User);
        var member = Store.FindMember(sourceLibrary, sourceFile, memberName, first);
        if (member is null && sourceFile is null) member = Store.FindMember(null, defaultFile, memberName, first) ?? Store.FindMember(null, null, memberName, first);
        return member;
    }

    // ---- database files ----

    /// <summary>
    /// A database file as RPG sees it, by its name (a physical or logical file, an SQL table or view,
    /// or an SQL index): the table its records are in, its record format and fields, its key and,
    /// for a logical file, the condition its records meet. Null when there's no such file.
    /// </summary>
    internal DatabaseFile? FindDatabaseFile(string name, string? first)
    {
        return _fileCache is { } cache
            ? cache.GetOrAdd((name.ToUpperInvariant(), first?.ToUpperInvariant()), _ => LookUpDatabaseFile(name, first))
            : LookUpDatabaseFile(name, first);
    }

    /// <summary>
    /// While a build checks programs, the database files they name, looked up once: a program's
    /// files (and their reference files) don't change until a file or script is built.
    /// </summary>
    private System.Collections.Concurrent.ConcurrentDictionary<(string Name, string? First), DatabaseFile?>? _fileCache;

    private DatabaseFile? LookUpDatabaseFile(string name, string? first)
    {
        var (library, file) = Qualified(name);
        if (Store.FindObject(library, file, "*FILE", first) is { Kind: "PF" or "LF" } o)
        {
            // A file's fields are what they were when it was created: its DDS is read once per version.
            var key = (o.Library, o.Name, o.Version);
            if (_createdFiles.TryGetValue(key, out var known)) return known;
            var made = DescribeFile(o);
            if (made is not null) _createdFiles[key] = made;
            return made;
        }
        if (Store.TableLibrary(file, library, first) is { } tableLibrary)
        {
            // A column with a system name (FOR COLUMN) is a field by that name.
            var columns = Store.SqlColumns(file, tableLibrary);
            var fields = columns.Select(c => new DbField(c.Name.ToUpperInvariant(), Interpreter.SqlType(c.Type), c.Type, c.Column)).ToList();
            return new DatabaseFile(tableLibrary, file, Store.RecordFormat(file, tableLibrary) ?? file, Store.SqlName(tableLibrary, file), fields,
                [.. Store.PrimaryKey(tableLibrary, file).Select(k => (FieldOf(columns, k), false))], true, null, Store.HasRowNumber(tableLibrary, file) ? Store.RowNumber : "ctid");
        }
        if (Store.IndexLibrary(file, library, first) is { } indexLibrary && Store.Index(indexLibrary, file) is var (table, keys, unique))
        {
            var columns = Store.SqlColumns(table, indexLibrary);
            var fields = columns.Select(c => new DbField(c.Name.ToUpperInvariant(), Interpreter.SqlType(c.Type), c.Type, c.Column)).ToList();
            return new DatabaseFile(indexLibrary, file, table, Store.SqlName(indexLibrary, table), fields, [.. keys.Select(k => (FieldOf(columns, k), false))], unique, null,
                Store.HasRowNumber(indexLibrary, table) ? Store.RowNumber : "ctid");
        }
        return null;
    }

    /// <summary>
    /// The fields EXTNAME gives a data structure: the file's (a physical or logical file, which needn't
    /// have a table) or the table's, LIB/NAME or LIB.NAME.
    /// </summary>
    internal IReadOnlyList<(string Name, string Type)> ExternalFields(string table, string? library)
    {
        var named = table.ToUpperInvariant().Split('/', '.');
        if (FindDatabaseFile(string.Join('/', named), library) is { } file) return [.. file.Fields.Select(f => (f.Name, f.SqlType))];
        return named is [var lib, var name] ? Store.Columns(name, lib) : Store.Columns(table);
    }

    /// <summary>The field name of a column (its system name, when it has one).</summary>
    private static string FieldOf(IReadOnlyList<(string Name, string Type, string Column)> columns, string column) =>
        columns.FirstOrDefault(c => c.Column.Equals(column, StringComparison.OrdinalIgnoreCase)).Name?.ToUpperInvariant() ?? column.ToUpperInvariant();

    // Jobs run at once, so the cache is shared safely.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Library, string Name, long Version), DatabaseFile> _createdFiles = [];

    /// <summary>A physical or logical file as its DDS describes it.</summary>
    private DatabaseFile? DescribeFile(StoredObject o)
    {
        var dds = DatabaseDds.Parse(o.Name, o.Source, o.Kind == "LF", References(o.Library));
        if (!dds.Logical)
            return new DatabaseFile(o.Library, o.Name, dds.Format, Store.SqlName(o.Library, o.Name), dds.Fields, dds.Keys, dds.Unique, null,
                Store.HasRowNumber(o.Library, o.Name) ? Store.RowNumber : "ctid");
        var formats = new List<DatabaseFile>();
        foreach (var format in dds.Others.Prepend(dds))
        {
            if (format.BasedOn is null || FindDatabaseFile(format.BasedOn, o.Library) is not { } physical) return null;
            var fields = LogicalFields(format, physical);
            formats.Add(new DatabaseFile(o.Library, o.Name, format.Format, physical.Table, fields, format.Keys, dds.Unique,
                format.Where(name => fields.FirstOrDefault(f => f.Name == name)?.SqlColumn ?? Store.ColumnName(name)), physical.RowId));
        }
        return formats[0] with { Others = formats.Skip(1).ToList() };
    }

    /// <summary>A logical file's fields: its physical file's, under their own names (RENAME) and in their columns.</summary>
    private static List<DbField> LogicalFields(DatabaseDds dds, DatabaseFile physical)
    {
        if (dds.FieldNames.Count == 0) return [.. physical.Fields];
        DbField? Physical(string name) => physical.Fields.FirstOrDefault(f => f.Name == name);
        return [.. dds.FieldNames.Select(n => dds.Derived.TryGetValue(n, out var derived) ? DerivedOf(n, derived, Physical)
            : Physical(dds.Renames.GetValueOrDefault(n, n)) is { } f ? f with { Name = n, Column = f.Column ?? f.Name } : null).OfType<DbField>()];
    }

    /// <summary>A derived field as an SQL expression over the physical file's columns; null when a field it names isn't there.</summary>
    private static DbField? DerivedOf(string name, DerivedField derived, Func<string, DbField?> physical)
    {
        if (derived.Keyword == "SST")
        {
            if (derived.Arguments is not [var of, var startText, ..] || physical(of) is not { } source || !int.TryParse(startText, out var start)) return null;
            var length = derived.Length ?? (derived.Arguments.Length > 2 && int.TryParse(derived.Arguments[2], out var l) ? l : Math.Max(1, source.Type.Length - start + 1));
            return new DbField(name, RpgType.Char(length), $"CHAR({length})", Expression: $"CAST(substr(rpad({source.SqlColumn}::text, {source.Type.Length}), {start}, {length}) AS CHAR({length}))");
        }
        var parts = derived.Arguments.Select(physical).ToList();
        if (parts.Any(p => p is null)) return null;
        var total = derived.Length ?? parts.Sum(p => p!.Type.Length);
        return new DbField(name, RpgType.Char(total), $"CHAR({total})",
            Expression: $"CAST(({string.Join(" || ", parts.Select(p => $"rpad({p!.SqlColumn}::text, {p.Type.Length})"))}) AS CHAR({total}))");
    }

    /// <summary>Where a DDS reference field (REF, REFFLD) finds its field: in the file, looked for from this library.</summary>
    private DatabaseDds.FieldReference References(string? library)
    {
        // A file's many reference fields usually name one reference file: it's looked up once.
        var files = new Dictionary<string, DatabaseFile?>(StringComparer.OrdinalIgnoreCase);
        return (file, field) =>
        {
            if (!files.TryGetValue(file, out var found)) files[file] = found = FindDatabaseFile(file, library) ?? DisplayFields(file, library);
            return found?.Fields.FirstOrDefault(f => f.Name.Equals(field, StringComparison.OrdinalIgnoreCase));
        };
    }

    /// <summary>A display file's named fields, as a file a reference field (REF, REFFLD) can name.</summary>
    private DatabaseFile? DisplayFields(string file, string? library)
    {
        var (lib, name) = Qualified(file);
        // The display file, or (when checking, before it's made) its source member.
        var (objectLibrary, objectName, source) = Store.FindObject(lib, name, "*FILE", library) is { Kind: "DSPF" } o ? (o.Library, o.Name, o.Source)
            : Store.FindMemberOfType(name, ["DSPF"], lib ?? library) is { } member ? (member.Library, member.Name, member.Source)
            : default;
        if (source is null) return null;
        var fields = new List<DbField>();
        foreach (var field in DisplayFile.Parse(objectName, source).Records.Values.SelectMany(r => r.Fields).Where(f => f.Name is not null && f.Length > 0))
        {
            if (fields.Any(f => f.Name == field.Name)) continue;
            var type = field.Numeric ? RpgType.Number(field.Length, field.Decimals, 'S') : RpgType.Char(field.Length);
            fields.Add(new DbField(field.Name!, type, field.Numeric ? $"NUMERIC({field.Length}, {field.Decimals})" : $"CHAR({field.Length})"));
        }
        return new DatabaseFile(objectLibrary, objectName, objectName, "", fields, [], false, null);
    }

    /// <summary>The most columns a PostgreSQL table has.</summary>
    private const int MaxColumns = 1600;

    /// <summary>CRTPF and CRTLF: the table (or view) of a physical (or logical) file's DDS, and the *FILE object.</summary>
    private CommandResult CreateDatabaseFile(Member member, string library, string name)
    {
        var dds = DatabaseDds.Parse(name, member.Source, member.Type == "LF", References(library));
        var failed = member.Type == "LF" ? "CPF7302" : "CPF7302";
        if (dds.Diagnostics.Any(d => d.Severity == Severity.Error))
            return new CommandResult(false, $"File {name} not created in library {library}: {dds.Diagnostics.First(d => d.Severity == Severity.Error).Message}.", MessageId: failed);
        if (Store.FindObject(library, name, "*FILE") is not null || Store.TableLibrary(name, library) is not null)
            return new CommandResult(false, $"File {name} already exists in library {library}.", MessageId: "CPF5813");
        var sql = new System.Text.StringBuilder();
        if (!dds.Logical && dds.Fields.Count > MaxColumns)
        {
            // IBM i takes up to 8000 fields; PostgreSQL a table of 1600 columns. A file that big is
            // a field reference file, there to be referred to, so it's made without a table.
            Store.SaveObject(library, name, "*FILE", member.Type, member.Source);
            return new CommandResult(true, $"File {name} created in library {library}, without a table: its {dds.Fields.Count} fields are more than "
                + $"PostgreSQL's {MaxColumns} columns, so they can be referred to (REF, REFFLD, EXTNAME, LIKEREC) but the file can't hold records.");
        }
        if (!dds.Logical)
        {
            if (dds.Fields.Count == 0) return new CommandResult(false, $"File {name} not created: its record format has no fields.", MessageId: failed);
            sql.Append($"CREATE TABLE {Store.SchemaName(library)}.{Store.ColumnName(name)} ({string.Join(", ", dds.Fields.Select(f => $"{Store.ColumnName(f.Name)} {(f.SqlType == "DOUBLE" ? "DOUBLE PRECISION" : f.SqlType)} NOT NULL DEFAULT {Default(f)}"))}, "
                + $"{Store.RowNumber} BIGINT GENERATED BY DEFAULT AS IDENTITY)");
            if (dds.Keys.Count > 0)
                sql.Append($";\nCREATE {(dds.Unique ? "UNIQUE " : "")}INDEX {Store.ColumnName(name + "_qk")} ON {Store.SchemaName(library)}.{Store.ColumnName(name)} ({string.Join(", ", dds.Keys.Select(k => Store.ColumnName(k.Field)))})");
        }
        else
        {
            if (dds.Join) return new CommandResult(false, $"File {name} not created: join logical files (JFILE) aren't supported by the fake.", MessageId: failed);
            // Another record format's physical file has to be there too (only the first format is a view, as SQL sees the file).
            foreach (var other in dds.Others)
            {
                if (other.BasedOn is null || FindDatabaseFile(other.BasedOn, library) is not { } otherPhysical)
                    return new CommandResult(false, $"File {name} not created: the physical file {other.BasedOn} of record format {other.Format} isn't there.", MessageId: failed);
                if (LogicalFields(other, otherPhysical).Count < (other.FieldNames.Count == 0 ? otherPhysical.Fields.Count : other.FieldNames.Count))
                    return new CommandResult(false, $"File {name} not created: record format {other.Format} names a field {otherPhysical.Name} hasn't.", MessageId: failed);
            }
            if (dds.BasedOn is null || FindDatabaseFile(dds.BasedOn, library) is not { } physical)
                return new CommandResult(false, $"File {name} not created: its physical file {dds.BasedOn} isn't there.", MessageId: failed);
            var names = dds.FieldNames.Count == 0 ? physical.Fields.Select(f => f.Name) : dds.FieldNames;
            var missing = names.FirstOrDefault(f => !dds.Derived.ContainsKey(f) && physical.Fields.All(p => p.Name != dds.Renames.GetValueOrDefault(f, f)));
            if (missing is not null) return new CommandResult(false, $"File {name} not created: {dds.Renames.GetValueOrDefault(missing, missing)} isn't a field of {physical.Name}.", MessageId: failed);
            var fields = LogicalFields(dds, physical);
            if (fields.Count < names.Count())
                return new CommandResult(false, $"File {name} not created: a derived field (SST, CONCAT) names a field {physical.Name} hasn't.", MessageId: failed);
            var where = dds.Where(n => fields.FirstOrDefault(f => f.Name == n)?.SqlColumn ?? Store.ColumnName(n));
            sql.Append($"CREATE VIEW {Store.SchemaName(library)}.{Store.ColumnName(name)} AS SELECT {string.Join(", ", fields.Select(f => $"{f.SqlColumn} AS {Store.ColumnName(f.Name)}"))} "
                + $"FROM {physical.Table}{(where is not null ? $" WHERE {where}" : "")}");
        }
        try
        {
            lock (Store.Gate)
            {
                using var command = Store.Connection.CreateCommand();
                command.CommandText = sql.ToString();
                command.ExecuteNonQuery();
            }
        }
        catch (PostgresException e)
        {
            return new CommandResult(false, $"File {name} not created in library {library}: {Store.Clean(e)}", MessageId: failed);
        }
        Store.SaveObject(library, name, "*FILE", member.Type, member.Source);
        return new CommandResult(true, $"File {name} created in library {library}.");

        static string Default(DbField f) => f.Type.Kind switch
        {
            Rpg.RpgKind.Char or Rpg.RpgKind.Varchar => "''",
            Rpg.RpgKind.Date => "'0001-01-01'",
            Rpg.RpgKind.Time => "'00:00:00'",
            Rpg.RpgKind.Timestamp => "'0001-01-01 00:00:00.000000'",
            _ => "0",
        };
    }

    // ---- data areas ----

    /// <summary>
    /// A data area's value, as a program's IN gets it: characters (padded to its length), a number,
    /// or an indicator's "1" or "0". *LDA is the job's local data area. Null when there's no such data area.
    /// </summary>
    internal object? ReadDataArea(string name, IJobHost host)
    {
        if (name.Equals("*LDA", StringComparison.OrdinalIgnoreCase)) return host.State.LocalDataArea;
        var (library, area) = Qualified(name);
        if (Store.FindObject(library, area, "*DTAARA", DefaultLibrary(host.User)) is not { } o) return null;
        var parts = o.Attribute.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts[0] switch
        {
            "*DEC" => decimal.TryParse(o.Source, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0m,
            "*LGL" => o.Source == "1" ? "1" : "0",
            _ => o.Source.PadRight(parts.Length > 1 && int.TryParse(parts[1], out var length) ? length : o.Source.Length),
        };
    }

    /// <summary>Writes a data area's value (OUT, CHGDTAARA): cut to its length. False when there's no such data area.</summary>
    internal bool WriteDataArea(string name, string value, IJobHost host, int start = 0, int? length = null)
    {
        if (length is { } part) value = value.Length >= part ? value[..part] : value.PadRight(part);
        if (name.Equals("*LDA", StringComparison.OrdinalIgnoreCase))
        {
            var lda = host.State.LocalDataArea.ToCharArray();
            for (var i = 0; i < value.Length && start + i < lda.Length; i++) lda[start + i] = value[i];
            host.State.LocalDataArea = new string(lda);
            return true;
        }
        var (library, area) = Qualified(name);
        if (Store.FindObject(library, area, "*DTAARA", DefaultLibrary(host.User)) is not { } o) return false;
        var parts = o.Attribute.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var size = parts.Length > 1 && int.TryParse(parts[1], out var l) ? l : 2000;
        string text;
        if (parts[0] == "*CHAR")
        {
            // A part (CHGDTAARA DTAARA(name (start length))) changes only those characters; otherwise the value is the whole.
            var current = (length is null ? "" : o.Source).PadRight(size).ToCharArray();
            for (var i = 0; i < value.Length && start + i < size; i++) current[start + i] = value[i];
            text = new string(current).TrimEnd();
        }
        else text = value.Trim();
        Store.SaveObject(o.Library, o.Name, "*DTAARA", o.Attribute, text);
        return true;
    }

    /// <summary>The commands on data areas: CRTDTAARA, CHGDTAARA, DSPDTAARA and DLTDTAARA.</summary>
    private CommandResult RunDataArea(ClCommand command, IJobHost host)
    {
        CommandResult Fail(string message, string id) => new(false, message, MessageId: id);
        var target = command.Value("DTAARA", 0);
        if (target is null) return Fail("Parameter DTAARA required.", "CPD0072");
        // CHGDTAARA DTAARA(name (start length)): a part of it.
        var parts = target.Replace("(", " ").Replace(")", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var name = parts[0].ToUpperInvariant();
        var start = parts.Length > 1 && int.TryParse(parts[1], out var s) ? s - 1 : 0;
        int? partLength = parts.Length > 2 && int.TryParse(parts[2], out var pl) ? pl : null;
        var (library, area) = Qualified(name);
        switch (command.Name)
        {
            case "CRTDTAARA":
            {
                var lib = library ?? DefaultLibrary(host.User);
                if (!Store.HasLibrary(lib)) return Fail($"Library {lib} not found.", "CPF9810");
                if (Store.FindObject(lib, area, "*DTAARA") is not null) return Fail($"Data area {area} exists in library {lib}.", "CPF1023");
                var type = command.Value("TYPE", 1)?.ToUpperInvariant() ?? "*CHAR";
                var lengthParts = (command.Value("LEN", 2) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var length = lengthParts.Length > 0 && int.TryParse(lengthParts[0], out var n) ? n : type switch { "*DEC" => 15, "*LGL" => 1, _ => 32 };
                var decimals = lengthParts.Length > 1 && int.TryParse(lengthParts[1], out var d) ? d : type == "*DEC" ? 5 : 0;
                var initial = command.Value("VALUE", 3) is { } v ? Unquoted(v) : type == "*DEC" ? "0" : type == "*LGL" ? "0" : "";
                Store.SaveObject(lib, area, "*DTAARA", type == "*DEC" ? $"*DEC {length} {decimals}" : $"{type} {length}", initial);
                return new CommandResult(true, $"Data area {area} created in library {lib}.");
            }
            case "CHGDTAARA":
                return WriteDataArea(name, command.Value("VALUE", 1) is { } value ? Unquoted(value) : "", host, start, partLength)
                    ? new CommandResult(true, $"Data area {name} changed.")
                    : Fail($"Data area {name} not found.", "CPF1015");
            case "DSPDTAARA":
                return ReadDataArea(name, host) is { } shown
                    ? new CommandResult(true, $"{name}: {Rpg.Values.ToText(shown).TrimEnd()}")
                    : Fail($"Data area {name} not found.", "CPF1015");
            default: // DLTDTAARA
                return Store.FindObject(library, area, "*DTAARA", DefaultLibrary(host.User)) is { } o && Store.DeleteObject(o)
                    ? new CommandResult(true, $"Data area {area} deleted.")
                    : Fail($"Data area {name} not found.", "CPF1015");
        }
    }

    /// <summary>A value as typed, without the quotes round it.</summary>
    internal static string Unquoted(string raw) => ClCommand.Values(raw) is [(var text, true)] ? text : raw.Trim();

    /// <summary>The commands on the integrated file system's directories and stream files.</summary>
    private CommandResult RunIfs(ClCommand command, IJobHost host)
    {
        var current = host.State.CurrentDirectory;
        string? PathOf(string keyword, int position) => command.Value(keyword, position) is { } raw ? Ifs.Absolute(Unquoted(raw), current) : null;
        CommandResult Fail(string message, string id) => new(false, message, MessageId: id);
        try
        {
            switch (command.Name)
            {
                case "MKDIR" or "MD" or "CRTDIR":
                {
                    if (PathOf("DIR", 0) is not { } path) return Fail("Parameter DIR required.", "CPD0072");
                    if (Ifs.InQsys(path)) return Fail($"Directory {path} can't be made in /QSYS.LIB: CRTLIB makes libraries.", "CPFA0A9");
                    var local = Ifs.HostPath(path);
                    if (Path.Exists(local)) return Fail($"Object {path} already exists.", "CPFA0A0");
                    if (!Directory.Exists(Path.GetDirectoryName(local))) return Fail($"Object {FolderOf(path)} not found.", "CPFA0A9");
                    Directory.CreateDirectory(local);
                    return new CommandResult(true, $"Directory {path} created.");
                }
                case "RMDIR" or "RD":
                {
                    if (PathOf("DIR", 0) is not { } path) return Fail("Parameter DIR required.", "CPD0072");
                    var local = Ifs.HostPath(path);
                    if (path == "/" || !Directory.Exists(local)) return Fail($"Object {path} not found.", "CPFA0A9");
                    var all = command.Value("SUBTREE", 1)?.ToUpperInvariant() == "*ALL";
                    if (!all && Directory.EnumerateFileSystemEntries(local).Any()) return Fail($"Directory {path} isn't empty.", "CPFA0AC");
                    Directory.Delete(local, recursive: all);
                    return new CommandResult(true, $"Directory {path} removed.");
                }
                case "RMVLNK" or "DEL" or "ERASE":
                {
                    if (PathOf("OBJLNK", 0) is not { } path) return Fail("Parameter OBJLNK required.", "CPD0072");
                    var local = Ifs.HostPath(path);
                    if (!File.Exists(local)) return Fail($"Object {path} not found.", "CPFA0A9");
                    File.Delete(local);
                    return new CommandResult(true, $"Object {path} removed.");
                }
                case "CHGCURDIR" or "CD" or "CHDIR":
                {
                    if (PathOf("DIR", 0) is not { } path) return Fail("Parameter DIR required.", "CPD0072");
                    if (!Ifs.DirectoryExists(path)) return Fail($"Object {path} not found.", "CPFA0A9");
                    host.State.CurrentDirectory = path;
                    return new CommandResult(true, $"Current directory changed to {path}.");
                }
                case "CPYTOSTMF":
                {
                    if (PathOf("FROMMBR", 0) is not { } from || PathOf("TOSTMF", 1) is not { } to) return Fail("Parameters FROMMBR and TOSTMF required.", "CPD0072");
                    if (Ifs.QsysMember(from) is not var (library, file, name) || Store.FindMember(library, file, name) is not { } member)
                        return Fail($"Member {from} not found.", "CPFA0A9");
                    var replace = command.Value("STMFOPT", 2)?.ToUpperInvariant() == "*REPLACE";
                    if (Ifs.FileExists(to) && !replace) return Fail($"Stream file {to} already exists: STMFOPT(*REPLACE) replaces it.", "CPFA0A0");
                    Ifs.WriteText(to, member.Source);
                    return new CommandResult(true, $"Member {member} copied to stream file {to}.");
                }
                case "CPYFRMSTMF":
                {
                    if (PathOf("FROMSTMF", 0) is not { } from || PathOf("TOMBR", 1) is not { } to) return Fail("Parameters FROMSTMF and TOMBR required.", "CPD0072");
                    if (Ifs.ReadText(from) is not { } text) return Fail($"Stream file {from} not found.", "CPFA0A9");
                    if (Ifs.QsysMember(to) is not var (library, file, name)) return Fail($"{to} isn't a member's path, such as /QSYS.LIB/MYLIB.LIB/QRPGLESRC.FILE/PROG.MBR.", "CPFA0A9");
                    if (!Store.HasLibrary(library)) return Fail($"Library {library} not found.", "CPF9810");
                    var existing = Store.FindMember(library, file, name);
                    if (existing is not null && command.Value("MBROPT", 2)?.ToUpperInvariant() != "*REPLACE")
                        return Fail($"Member {existing} already exists: MBROPT(*REPLACE) replaces it.", "CPFA0A0");
                    var type = existing?.Type ?? TypeForExtension(Path.GetExtension(from)) ?? "TXT";
                    Save(new Member(library, file, name, type, text));
                    return new CommandResult(true, $"Stream file {from} copied to member {library}/{file}({name}).");
                }
                default: // CPY, COPY
                {
                    if (PathOf("OBJ", 0) is not { } from || PathOf("TOOBJ", 1) is not { } to) return Fail("Parameters OBJ and TOOBJ required.", "CPD0072");
                    var source = Ifs.HostPath(from);
                    if (!File.Exists(source)) return Fail($"Object {from} not found.", "CPFA0A9");
                    var target = Ifs.HostPath(to);
                    if (Directory.Exists(target)) target = Path.Combine(target, Path.GetFileName(source));
                    if (File.Exists(target) && command.Value("REPLACE", -1)?.ToUpperInvariant() != "*YES") return Fail($"Object {to} already exists.", "CPFA0A0");
                    File.Copy(source, target, overwrite: true);
                    return new CommandResult(true, $"Object {from} copied to {to}.");
                }
            }
        }
        catch (UnauthorizedAccessException e)
        {
            return Fail(e.Message, "CPFA09C");
        }
        catch (IOException e)
        {
            return Fail(e.Message, "CPFA0A9");
        }
    }

    /// <summary>A command CRTCMD made: its parameters, checked against its definition, passed to its program.</summary>
    private CommandResult RunMade(StoredObject made, ClCommand command, IJobHost host, int depth)
    {
        var definition = Cl.CommandDefinition.Parse(made.Source);
        var program = AttributeValue(made.Attribute, "PGM") ?? made.Name;
        try
        {
            var arguments = definition.Arguments(command);
            var before = host.State.Messages.Count;
            Call(program, arguments, host, depth + 1);
            // What the program sent as a completion message is the command's.
            var message = host.State.Messages.Count > before ? host.State.Messages[^1] : "";
            return new CommandResult(true, message);
        }
        catch (Cl.ClEscape e)
        {
            return new CommandResult(false, $"{e.Message} Error found on {command.Name} command.", MessageId: "CPF0001");
        }
        catch (RpgHalt e)
        {
            return new CommandResult(false, e.Message, MessageId: e.MessageId ?? "CPF9999");
        }
    }

    /// <summary>The output queues there are, for CHKOBJ and OVRPRTF.</summary>
    private static readonly HashSet<string> OutputQueues = ["QPRINT", "QPRINTS", "QPRINT2", "PRT01"];

    /// <summary>The commands that build and run an application: service programs, binding directories, commands, overrides, the library list. Null for any other.</summary>
    private CommandResult? RunBuild(ClCommand command, IJobHost host)
    {
        var first = DefaultLibrary(host.User);
        CommandResult Fail(string message, string id) => new(false, message, MessageId: id);
        (string Library, string Name)? Target(string keyword, int position)
        {
            if (command.Value(keyword, position) is not { } text) return null;
            var (library, name) = Qualified(text);
            return (library ?? first, name);
        }
        switch (command.Name)
        {
            case "CRTSRVPGM" or "CRTPGM":
            {
                var keyword = command.Name == "CRTSRVPGM" ? "SRVPGM" : "PGM";
                if (Target(keyword, 0) is not var (library, name)) return Fail($"Parameter {keyword} required.", "CPD0072");
                if (!Store.HasLibrary(library)) return Fail($"Library {library} not found.", "CPF9810");
                var modules = ClCommand.Values(command.Value("MODULE", 1) ?? name).Select(v => Qualified(v.Text)).ToList();
                var found = modules.Select(m => Store.FindObject(m.Library, m.Name == "*SRVPGM" || m.Name == "*PGM" ? name : m.Name, "*MODULE", library)).ToList();
                if (found.FirstOrDefault(m => m is null) is null && found.Count > 0 && found.All(m => m is not null))
                {
                    if (found.Count > 1) return Fail($"{command.Name} with more than one module isn't supported.", "CPF5D04");
                    var module = found[0]!;
                    if (command.Name == "CRTPGM" && module.Kind is "RPGLE" or "SQLRPGLE"
                        && Unresolved(CheckedRpg(module.Kind, module.Source, module.Library, module.StreamFile is { } stream ? FolderOf(stream) : null), name, library) is { } unbound) return unbound;
                    Store.SaveObject(library, name, command.Name == "CRTSRVPGM" ? "*SRVPGM" : "*PGM", module.Attribute, module.Source);
                    return new CommandResult(true, $"{(command.Name == "CRTSRVPGM" ? "Service program" : "Program")} {name} created in library {library}.");
                }
                return Fail($"Module {modules[0].Name} not found.", "CPF5D04");
            }
            case "CRTBNDDIR":
            {
                if (Target("BNDDIR", 0) is not var (library, name)) return Fail("Parameter BNDDIR required.", "CPD0072");
                if (!Store.HasLibrary(library)) return Fail($"Library {library} not found.", "CPF9810");
                if (Store.FindObject(library, name, "*BNDDIR") is not null) return Fail($"Binding directory {name} exists in library {library}.", "CPF5D0B");
                Store.SaveObject(library, name, "*BNDDIR", "", "");
                return new CommandResult(true, $"Binding directory {name} created in library {library}.");
            }
            case "ADDBNDDIRE":
            {
                if (Target("BNDDIR", 0) is not var (library, name)) return Fail("Parameter BNDDIR required.", "CPD0072");
                if (Store.FindObject(library, name, "*BNDDIR") is not { } directory) return Fail($"Binding directory {name} in library {library} not found.", "CPF9810");
                var entries = directory.Source.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
                foreach (var group in ClCommand.Groups(command.Value("OBJ", 1) ?? ""))
                {
                    var parts = group.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0) continue;
                    var (l, n) = Qualified(parts[0]);
                    var entry = $"{l ?? "*LIBL"}/{n} {(parts.Length > 1 ? parts[1].ToUpperInvariant() : "*SRVPGM")}";
                    if (!entries.Contains(entry)) entries.Add(entry);
                }
                Store.SaveObject(directory.Library, directory.Name, "*BNDDIR", "", string.Join('\n', entries));
                return new CommandResult(true, "Entries added to binding directory.");
            }
            case "CRTCMD":
            {
                if (Target("CMD", 0) is not var (library, name)) return Fail("Parameter CMD required.", "CPD0072");
                if (command.Value("PGM", 1) is not { } program) return Fail("Parameter PGM required.", "CPD0072");
                if (FindSource(command, name, "QCMDSRC", host) is not { } member) return Fail($"Member {SourceMember(command, name)} not found.", "CPF6201");
                if (member.Type != "CMD") return Fail($"Member {member} has source type {member.Type}, which CRTCMD doesn't compile.", "CPF6201");
                var (pl, pn) = Qualified(program);
                var compiled = Compile(member, library, name, host.User, null, $"PGM({(pl is null ? "" : pl + "/")}{pn})");
                return compiled with { MessageId = compiled.Ok ? null : "CPF6201" };
            }
            case "ADDLIBLE":
            {
                var library = command.Value("LIB", 0)?.ToUpperInvariant() ?? "";
                if (!Store.HasLibrary(library)) return Fail($"Library {library} not found.", "CPF2110");
                if (host.State.LibraryList.Contains(library)) return Fail($"Library {library} already exists in library list.", "CPF2103");
                if (command.Value("POSITION", 1)?.ToUpperInvariant().StartsWith("*FIRST", StringComparison.Ordinal) == true) host.State.LibraryList.Insert(0, library);
                else host.State.LibraryList.Add(library);
                return new CommandResult(true, $"Library {library} added to library list.");
            }
            case "RMVLIBLE":
            {
                var library = command.Value("LIB", 0)?.ToUpperInvariant() ?? "";
                return host.State.LibraryList.Remove(library)
                    ? new CommandResult(true, $"Library {library} removed from library list.")
                    : Fail($"Library {library} not removed from the library list.", "CPF2104");
            }
            case "OVRPRTF":
            {
                var file = command.Value("FILE", 0)?.ToUpperInvariant() ?? "";
                if (file.Length == 0) return Fail("Parameter FILE required.", "CPD0072");
                var queue = command.Value("OUTQ", -1)?.ToUpperInvariant();
                if (queue is not null && queue is not ("*JOB" or "*DEV" or "*SAME") && !OutputQueues.Contains(Qualified(queue).Name))
                    return Fail($"Output queue {queue} not found.", "CPF4103");
                var name = command.Value("SPLFNAME", -1)?.ToUpperInvariant() is { } n && n != "*FILE" ? n : null;
                var data = command.Value("USRDTA", -1) is { } u ? ClCommand.Values(u) is [var (t, _)] ? t : u : null;
                host.State.Overrides[file] = new PrinterOverride(name, command.Value("HOLD", -1)?.ToUpperInvariant() == "*YES", queue, data);
                return new CommandResult(true, "");
            }
            case "DLTOVR":
            {
                var file = command.Value("FILE", 0)?.ToUpperInvariant() ?? "";
                if (file == "*ALL")
                {
                    host.State.Overrides.Clear();
                    host.State.DatabaseOverrides.Clear();
                    return new CommandResult(true, "");
                }
                // A printer file's override (OVRPRTF) or a database file's (OVRDBF).
                return host.State.Overrides.Remove(file) | host.State.DatabaseOverrides.Remove(file)
                    ? new CommandResult(true, "") : Fail($"Override not found for file {file}.", "CPF9841");
            }
            case "CHKOBJ":
            {
                if (command.Value("OBJ", 0) is not { } text) return Fail("Parameter OBJ required.", "CPD0072");
                var (library, name) = Qualified(text);
                var type = command.Value("OBJTYPE", 1)?.ToUpperInvariant() ?? "";
                var exists = type switch
                {
                    "*OUTQ" => OutputQueues.Contains(name),
                    "*LIB" => Store.HasLibrary(name),
                    "*FILE" => Store.FindObject(library, name, "*FILE", first) is not null || IsName(name) && Store.Columns(name, library).Count > 0,
                    "*USRPRF" => Store.FindUser(name) is not null,
                    _ => Store.FindObject(library, name, type, first) is not null,
                };
                return exists ? new CommandResult(true, "") : Fail($"Object {name} in library {library ?? "*LIBL"} type {type} not found.", "CPF9801");
            }
            case "SNDSMTPEMM":
            {
                // There's no mail to send to: the fake keeps who it was for, and the subject.
                var recipients = ClCommand.Groups(command.Value("RCP", 0) ?? "").Select(g => ClCommand.Values(g).FirstOrDefault().Text ?? "").ToList();
                var to = recipients.FirstOrDefault(r => r.Length > 0) ?? "";
                var subject = command.Value("SUBJECT", 1) is { } s ? ClCommand.Values(s) is [var (t, _)] ? t : s : "";
                if (!to.Contains('@')) return Fail($"Recipient {to} not valid.", "TCP5091");
                host.State.Mail.Add((to, subject));
                return new CommandResult(true, $"Email sent to {to}.");
            }
            case "SNDPGMMSG":
            {
                var text = command.Value("MSG", 0) is { } m ? ClCommand.Values(m) is [var (t, _)] ? t : m
                    : command.Value("MSGDTA", -1) is { } d ? ClCommand.Values(d) is [var (dt, _)] ? dt : d : "";
                if (command.Value("MSGTYPE", -1)?.ToUpperInvariant() == "*ESCAPE") return Fail(text, command.Value("MSGID", -1)?.ToUpperInvariant() ?? "CPF9898");
                host.State.Messages.Add(text);
                return new CommandResult(true, text);
            }
            case "GRTOBJAUT" or "RVKOBJAUT" or "CHGOBJOWN" or "CRTSRCPF" or "CHGPFM" or "CHGCURLIB" or "STRCMTCTL" or "ENDCMTCTL" or "RCLRSC" or "RCLACTGRP":
                return new CommandResult(true, "");
            default:
                return null;
        }
    }

    // ---- the fake's own programs ----

    /// <summary>
    /// Puts the fake's own programs in QGPL: USRMNT, the user maintenance System Options runs, and its
    /// display file. A member that's there already is left as it is, so it can be changed or kept.
    /// Returns what it made.
    /// </summary>
    public IReadOnlyList<Member> InstallSystemSource(Action<string>? log = null)
    {
        var installed = new List<Member>();
        var assembly = typeof(Machine).Assembly;
        var names = assembly.GetManifestResourceNames().Where(n => n.StartsWith("System/", StringComparison.Ordinal))
            .Select(n => (Resource: n, File: n["System/".Length..]))
            .Where(n => TypeForExtension(Path.GetExtension(n.File)) is not null)
            .OrderBy(n => Path.GetExtension(n.File) == ".dspf" ? 0 : 1).ThenBy(n => n.File, StringComparer.Ordinal);
        foreach (var (resource, file) in names)
        {
            var type = TypeForExtension(Path.GetExtension(file))!;
            var name = Path.GetFileNameWithoutExtension(file).ToUpperInvariant();
            if (Store.FindMember(CurrentLibrary, DefaultFile(type), name) is not null) continue;
            using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
            var member = new Member(CurrentLibrary, DefaultFile(type), name, type, reader.ReadToEnd().Replace("\r\n", "\n"));
            Save(member);
            var result = Compile(member);
            log?.Invoke($"source: {member} {member.Type}: {result.Message}");
            installed.Add(member);
        }
        return installed;
    }

    // ---- a folder of source ----

    /// <summary>
    /// Reads every source file under a folder (.rpgle, .sqlrpgle, .dspf, .sql) as a member named for
    /// the file. Each folder directly under it is a library, made when it's missing; a folder in that
    /// is the source file (MYLIB/QRPGLESRC/prog.rpgle), and a file directly in the library's folder is
    /// in its type's default source file. Files directly under <paramref name="directory"/> go to the
    /// current library. A member that's new or changed is saved and checked, then SQL members are run,
    /// display files created and programs created, in that order. Without <paramref name="create"/>,
    /// nothing is created (SQL members still run: use a machine to throw away).
    /// Returns how many lines the fake can't run or an IBM i's compiler rejects.
    /// </summary>
    private static IEnumerable<string> ScriptVerbs(string source) =>
        SqlTranslator.SplitScript(source).Select(s => s.Text.TrimStart().Split((char[]?)null, 2)[0].ToUpperInvariant()).Where(v => v is not ("SET" or "LABEL" or "COMMENT"));

    private static bool Creates(string source) => ScriptVerbs(source).Contains("CREATE");

    private static bool OnlyDrops(string source) => ScriptVerbs(source).ToList() is { Count: > 0 } verbs && verbs.All(v => v == "DROP");

    /// <summary>
    /// Saves and checks members, then compiles them (or with <paramref name="create"/> false, only
    /// checks them; SQL members still run), in the order they depend on each other: copy members
    /// first, so the programs that include them find them; then tables (scripts that create before
    /// scripts that fill), so the check of a program's SQL finds them; then display and printer
    /// files, service programs, programs and CL.
    /// </summary>
    public List<BuiltMember> Build(IEnumerable<Member> members, bool create = true, Action<BuiltMember>? progress = null)
    {
        int Order(Member m) => m.Type switch
        {
            "RPGLEINC" => 0,
            "SQL" => Creates(m.Source) ? 1 : 2,
            "PF" => 3,
            "LF" => 4,
            "DSPF" or "PRTF" => 5,
            // Service programs (NOMAIN) before the programs that call them.
            "RPGLE" or "SQLRPGLE" => m.Source.Contains("NOMAIN", StringComparison.OrdinalIgnoreCase) && RpgParser.Parse(m.Source, null, Includes(m.Library)).NoMain ? 6 : 7,
            "CLLE" => 8,
            _ => 9,
        };
        var built = new List<BuiltMember>();
        // Every member is there before any is checked: a program can copy a member of any type (a /COPY of an RPGLE member).
        var all = members.ToList();
        members = all;
        foreach (var member in all.Where(m => Store.FindMember(m.Library, m.File, m.Name) != m)) Store.SaveMember(member, []);
        try
        {
            BuildAll();
        }
        finally
        {
            _fileCache = null;
        }
        return built;

        void BuildAll()
        {
        foreach (var group in members.GroupBy(Order).OrderBy(g => g.Key))
        {
            // A file is built after the files its reference fields (REF, REFFLD) name; one that
            // still can't find its reference file is tried once more at the end of its group.
            var ordered = group.Key is 3 or 4 or 5 ? ReferenceOrder([.. group]) : [.. group.OrderBy(m => m.Name, StringComparer.Ordinal)];
            var retry = new List<Member>();
            foreach (var member in ordered)
            {
                var one = BuildOne(member);
                if (member.Type is "PF" or "LF" && !one.Result.Ok && one.Diagnostics.Any(d => d.Message.StartsWith(DatabaseDds.MissingReference, StringComparison.Ordinal)))
                    retry.Add(member);
                else Done(one);
            }
            foreach (var member in retry) Done(BuildOne(member));
        }
        }

        void Done(BuiltMember one)
        {
            built.Add(one);
            progress?.Invoke(one);
        }

        BuiltMember BuildOne(Member member)
        {
            // Building a file or running a script can make or change files; checking a program can't.
            _fileCache = member.Type is "RPGLE" or "SQLRPGLE" or "RPGLEINC" or "DSPF" or "PRTF" or "CLLE" or "CMD" or "TXT" ? _fileCache ?? new() : null;
            try
            {
                return BuildMember(member);
            }
            finally
            {
                if (member.Type is not ("RPGLE" or "SQLRPGLE" or "RPGLEINC" or "DSPF" or "PRTF" or "CLLE" or "CMD" or "TXT")) _fileCache = null;
            }
        }

        BuiltMember BuildMember(Member member)
        {
            var diagnostics = Save(member);
            // A script that only drops tables isn't run: it's for a build to run when it rebuilds.
            // Checking still runs SQL members and makes database files, so the programs after them find their tables.
            var result = member.Type == "SQL" && OnlyDrops(member.Source) ? new CommandResult(true, "not run: it only drops (RUNSQLSTM runs it).")
                : member.Type is "TXT" or "RPGLEINC" ? new CommandResult(true, "saved.")
                : create || member.Type is "SQL" or "PF" or "LF" or "MSGF" ? Compile(member) : new CommandResult(true, "checked.");
            // A compile that failed for want of something outside the member (a service program, a
            // library) is kept with what checking found, so the member shows as in error.
            if (!result.Ok && !diagnostics.Any(d => d.Severity == Severity.Error))
            {
                diagnostics = [.. diagnostics, new Diagnostic(0, Severity.Error, result.Message)];
                Store.SaveMember(member, diagnostics);
            }
            return new BuiltMember(member, diagnostics, result);
        }
    }

    /// <summary>Database files, each after the files of its group its reference fields name (in name order otherwise).</summary>
    private static List<Member> ReferenceOrder(List<Member> files)
    {
        var byName = files.GroupBy(m => m.Name).ToDictionary(g => g.Key, g => g.ToList());
        var ordered = new List<Member>();
        var state = new Dictionary<Member, bool>(ReferenceEqualityComparer.Instance);
        foreach (var member in files.OrderBy(m => m.Name, StringComparer.Ordinal)) Visit(member);
        return ordered;

        void Visit(Member member)
        {
            // false: being visited (a cycle is left as it is); true: placed.
            if (state.ContainsKey(member)) return;
            state[member] = false;
            foreach (var name in DatabaseDds.ReferencedFiles(member.Source).Distinct())
            {
                if (name == member.Name || !byName.TryGetValue(name, out var referenced)) continue;
                // A file in the member's own library first.
                foreach (var other in referenced.OrderBy(m => m.Library == member.Library ? 0 : 1)) Visit(other);
            }
            state[member] = true;
            ordered.Add(member);
        }
    }

    public int Import(string directory, Action<string> log, bool create = true)
    {
        var root = Path.GetFullPath(directory);
        var changed = new List<Member>();
        var unsupported = 0;
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (TypeForExtension(Path.GetExtension(path)) is not { } type) continue;
            // Folders a tool keeps for itself (.ibmi, .vscode ...) aren't libraries.
            if (Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar).Any(p => p.StartsWith('.'))) continue;
            var name = Path.GetFileNameWithoutExtension(path).ToUpperInvariant();
            var folders = Path.GetRelativePath(root, Path.GetDirectoryName(path)!).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
                .Where(f => f != ".").Select(f => f.ToUpperInvariant()).ToList();
            // A text file is a TXT member only in a source file's folder (LIB/QTXTSRC/NOTES.TXT).
            if (type == "TXT" && folders.Count < 2) continue;
            var library = folders.Count > 0 ? folders[0] : CurrentLibrary;
            var file = folders.Count > 1 ? folders[^1] : DefaultFile(type);
            // RPG in a copy source file (QCPYSRC and the like) is a copy member, not a program.
            if (type is "RPGLE" or "SQLRPGLE" && file.Contains("CPY")) type = "RPGLEINC";
            var bad = !Store.IsLibraryName(library) ? library : !IsName(file) ? file : !IsName(name) ? name : null;
            if (bad is not null)
            {
                log($"source: {Path.GetRelativePath(root, path)} skipped: {bad} isn't a name of up to 10 letters and digits");
                continue;
            }
            if (Store.CreateLibrary(library)) log($"source: library {library} created");
            var member = new Member(library, file, name, type, System.IO.File.ReadAllText(path).Replace("\r\n", "\n").Replace('\0', ' '));
            var existing = Store.FindMember(member.Library, member.File, member.Name);
            var made = type is "SQL" or "RPGLEINC" or "CMD" or "TXT"
                || new[] { "*PGM", "*SRVPGM", "*FILE" }.Any(t => Store.FindObject(member.Library, name, t) is not null);
            if (existing == member && made) continue;
            changed.Add(member);
        }
        foreach (var built in Build(changed, create, Report)) { }
        return unsupported;

        void Report(BuiltMember built)
        {
            log($"source: {built.Member} {built.Member.Type}: {built.Result.Message}");
            foreach (var d in built.Diagnostics.Where(d => d.Severity != Severity.Info && d.Message != built.Result.Message))
            {
                var kind = d.Severity switch { Severity.Error => "error", Severity.Unsupported => "not supported", _ => "warning" };
                log($"source:   {(d.Line > 0 ? $"line {d.Line}: " : "")}{kind}: {d.Message}");
            }
            unsupported += built.Diagnostics.Count(d => d.Severity is Severity.Unsupported or Severity.Error);
        }
    }
}

/// <summary>What building a member did: what checking it found, and what compiling (or running) it answered.</summary>
public sealed record BuiltMember(Member Member, IReadOnlyList<Diagnostic> Diagnostics, CommandResult Result)
{
    /// <summary>"error" when it's in error or didn't compile; "warning" when the fake can't run some of it; else "ok".</summary>
    public string Status =>
        !Result.Ok || Diagnostics.Any(d => d.Severity == Severity.Error) ? "error"
        : Diagnostics.Any(d => d.Severity is Severity.Unsupported or Severity.Warning) ? "warning"
        : "ok";
}

/// <summary>A CL command, parsed: its name, its positional values and its KEYWORD(value) parameters, as written.</summary>
internal sealed record ClCommand(string Name, IReadOnlyList<string> Positional, IReadOnlyDictionary<string, string> Keyed)
{
    /// <summary>The library the command was qualified with (LIB/CMD), or null.</summary>
    public string? Library { get; init; }

    /// <summary>The parameter by keyword, or failing that by position (none when below 0), or null.</summary>
    public string? Value(string keyword, int position) =>
        Keyed.TryGetValue(keyword, out var value) ? value : position >= 0 && position < Positional.Count ? Positional[position] : null;

    public static ClCommand Parse(string text)
    {
        var i = 0;
        string Word()
        {
            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '(') i++;
            return text[start..i];
        }
        void Blanks()
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        }
        Blanks();
        var name = Word().ToUpperInvariant();
        var library = name.Contains('/') ? name[..name.LastIndexOf('/')] : null;
        name = name[(name.LastIndexOf('/') + 1)..];
        if (!Machine.IsName(name)) throw new FormatException($"{(name.Length == 0 ? "That" : name)} isn't a command.");
        var positional = new List<string>();
        var keyed = new Dictionary<string, string>();
        while (true)
        {
            Blanks();
            if (i >= text.Length) break;
            if (text[i] == '(')
            {
                positional.Add(Group());
                continue;
            }
            if (text[i] == '\'')
            {
                var start = i;
                Skip();
                positional.Add(text[start..i]);
                continue;
            }
            var word = Word();
            if (i < text.Length && text[i] == '(') keyed[word.ToUpperInvariant()] = Group();
            else positional.Add(word);
        }
        return new ClCommand(name, positional, keyed) { Library = library is null or "*LIBL" or "" ? null : library };

        // What's inside a parenthesized group, which may nest and hold quoted strings.
        string Group()
        {
            var start = ++i;
            var depth = 1;
            while (i < text.Length && depth > 0)
            {
                if (text[i] == '\'')
                {
                    Skip();
                    continue;
                }
                if (text[i] == '(') depth++;
                else if (text[i] == ')') depth--;
                i++;
            }
            if (depth > 0) throw new FormatException("A parenthesis isn't closed.");
            return text[start..(i - 1)].Trim();
        }
        void Skip()
        {
            i++;
            while (i < text.Length)
            {
                if (text[i] == '\'')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\'') i++;
                    else break;
                }
                i++;
            }
            if (i >= text.Length) throw new FormatException("A quoted value isn't closed.");
            i++;
        }
    }

    /// <summary>The elements of a list of lists, such as OBJ((LIB/A *SRVPGM) (LIB/B *SRVPGM)): what's inside each parenthesized group, or the whole when there's none.</summary>
    public static List<string> Groups(string list)
    {
        var groups = new List<string>();
        var depth = 0;
        var start = -1;
        var quoted = false;
        for (var i = 0; i < list.Length; i++)
        {
            if (list[i] == '\'') quoted = !quoted;
            if (quoted) continue;
            if (list[i] == '(' && depth++ == 0) start = i + 1;
            else if (list[i] == ')' && --depth == 0 && start >= 0) groups.Add(list[start..i].Trim());
        }
        if (groups.Count == 0 && list.Trim().Length > 0) groups.Add(list.Trim());
        return groups;
    }

    /// <summary>The values in a list such as PARM('A B' 12 X): each one's text, and whether it was quoted.</summary>
    public static List<(string Text, bool Quoted)> Values(string list)
    {
        var values = new List<(string, bool)>();
        var i = 0;
        while (i < list.Length)
        {
            if (char.IsWhiteSpace(list[i]))
            {
                i++;
            }
            else if (list[i] == '\'')
            {
                var value = new System.Text.StringBuilder();
                i++;
                while (i < list.Length)
                {
                    if (list[i] == '\'')
                    {
                        if (i + 1 < list.Length && list[i + 1] == '\'') i++;
                        else break;
                    }
                    value.Append(list[i++]);
                }
                i++;
                values.Add((value.ToString(), true));
            }
            else
            {
                var start = i;
                while (i < list.Length && !char.IsWhiteSpace(list[i])) i++;
                values.Add((list[start..i], false));
            }
        }
        return values;
    }
}
