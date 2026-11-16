// The files a program uses, for whoever wants to bring them from a real IBM i: its file
// declarations (F specs and DCL-F), the tables its embedded SQL names, and the files its data
// structures are described by (EXTNAME); and for each whether it's here, and how many records it has.

using System.Text.RegularExpressions;
using FakeIbmI.Dds;
using FakeIbmI.Rpg;

namespace FakeIbmI;

/// <summary>
/// A file a program uses. <see cref="How"/> says where the program names it (DCL-F, SQL, EXTNAME);
/// <see cref="Kind"/> what it is here ("Physical file", "SQL view" ...), or null when it isn't here.
/// </summary>
internal sealed record ProgramFile(string Name, string? Library, string How, string Use, int Line, bool Database, string? Kind, string? HereLibrary,
    long? Records, IReadOnlyList<string> BasedOn);

public sealed partial class Machine
{
    [GeneratedRegex(@"\b(FROM|JOIN|INTO|UPDATE|TABLE)\s+(?!SELECT\b|TABLE\b|LATERAL\b|FINAL\b|\()((?:[A-Z#$@][\w#$@]*[./])?[A-Z#$@][\w#$@]*)", RegexOptions.IgnoreCase)]
    private static partial Regex SqlTable();

    [GeneratedRegex(@"EXEC\s+SQL\b(.*?)(?:;|END-EXEC)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ExecSql();

    private static readonly HashSet<string> NotTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "SET", "VALUES", "DEFAULT", "CURRENT", "SQLCA", "ONLY", "SYSDUMMY1", "SESSION", "INPUT", "OUTPUT", "DESCRIPTOR", "ROWS", "NEXT", "PRIOR", "FIRST", "LAST",
    };

    /// <summary>The tables a member's embedded SQL names, each with the line of the statement that first names it.</summary>
    internal static List<(string Name, string? Library, int Line)> SqlTables(string source)
    {
        var found = new List<(string, string?, int)>();
        // Fixed form's SQL has C/EXEC SQL and C+ lines: without their prefixes they read as free form's.
        var text = Regex.Replace(source.Replace("\r\n", "\n"), @"^.{5}[Cc][/+]", m => new string(' ', m.Length), RegexOptions.Multiline);
        foreach (Match statement in ExecSql().Matches(text))
        {
            var sql = statement.Groups[1].Value;
            var verb = sql.TrimStart().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToUpperInvariant();
            // FETCH and OPEN name a cursor; DECLARE ... CURSOR FOR goes on to a SELECT, which names tables.
            if (verb is "FETCH" or "OPEN" or "CLOSE" or "SET" or "PREPARE" or "EXECUTE" or "GET" or "INCLUDE" or "WHENEVER" or "COMMIT" or "ROLLBACK") continue;
            var line = text[..statement.Index].Count(c => c == '\n') + 1;
            foreach (Match table in SqlTable().Matches(sql))
            {
                var name = table.Groups[2].Value.ToUpperInvariant();
                // INTO :host is a host variable; UPDATE of a cursor's FOR UPDATE OF names columns.
                if (table.Groups[1].Value.Equals("INTO", StringComparison.OrdinalIgnoreCase) && verb is not ("INSERT" or "MERGE")) continue;
                if (table.Groups[1].Value.Equals("UPDATE", StringComparison.OrdinalIgnoreCase) && verb is not "UPDATE") continue;
                var parts = name.Split('.', '/');
                var (library, file) = parts.Length == 2 ? (parts[0], parts[1]) : (null, parts[0]);
                if (NotTables.Contains(file) || library is "QTEMP" or "SYSIBM" or "QSYS2" or "SESSION") continue;
                if (!found.Any(f => f.Item1 == file)) found.Add((file, library, line));
            }
        }
        return found;
    }

    /// <summary>
    /// The files the program (or source member) of this name uses, with what each is here. Null when
    /// there's neither a member nor a program of that name.
    /// </summary>
    internal List<ProgramFile>? FilesOf(string? library, string name)
    {
        name = name.ToUpperInvariant();
        library = library?.ToUpperInvariant() is { Length: > 0 } l && Store.HasLibrary(l) ? l : null;
        var source = Store.FindMember(library, null, name) is { Type: "RPGLE" or "SQLRPGLE" } member ? (member.Source, member.Library)
            : new[] { "*PGM", "*SRVPGM", "*MODULE" }.Select(type => Store.FindObject(library, name, type)).FirstOrDefault(o => o is { Kind: "RPGLE" or "SQLRPGLE" }) is { } made ? (made.Source, made.Library)
            : ((string, string)?)null;
        if (source is not var (text, from)) return null;
        var program = RpgParser.Parse(text, null, Includes(from));
        var uses = new List<(string Name, string? Library, string How, string Use, int Line, bool Database)>();
        void Add(string file, string? fileLibrary, string how, string use, int line, bool database)
        {
            if (file.Length > 0 && !uses.Any(u => u.Name == file && u.Database == database)) uses.Add((file, fileLibrary, how, use, line, database));
        }
        foreach (var file in program.Files)
        {
            // EXTFILE('LIB/FILE') or EXTDESC: the file it really is.
            var external = file.ExtFile?.Trim('\'', ' ') is { Length: > 0 } named && !named.StartsWith('*') ? named.ToUpperInvariant() : file.Name.ToUpperInvariant();
            var (fileLibrary, fileName) = Qualified(external);
            var usage = file.Usage?.ToLowerInvariant().Replace("*", "").Replace(":", ", ").Replace("  ", " ").Trim() is { Length: > 0 } said ? said : "input";
            var use = file.Disk ? (file.Keyed ? "keyed, " : "") + usage : file.Printer > 0 ? "printer file" : "display file";
            Add(fileName, fileLibrary, "DCL-F", use, file.Line, file.Disk);
        }
        foreach (var (table, tableLibrary, line) in SqlTables(text)) Add(table, tableLibrary, "SQL", "embedded SQL", line, true);
        foreach (var declaration in program.Procedures.Values.Prepend(program.Main).SelectMany(p => p.Locals).OfType<VarDecl>())
        {
            if (declaration.Type.ExtName is { Length: > 0 } described)
            {
                var (fileLibrary, fileName) = Qualified(described.Trim('\'').Split(':')[0].Trim());
                Add(fileName, fileLibrary, "EXTNAME", $"describes {declaration.Name}", declaration.Line, true);
            }
        }
        return [.. uses.Select(u =>
        {
            if (!u.Database)
            {
                var device = Store.FindObject(u.Library, u.Name, "*FILE", from);
                return new ProgramFile(u.Name, u.Library, u.How, u.Use, u.Line, false, device is null ? null : device.Kind == "PRTF" ? "Printer file" : "Display file", device?.Library, null, []);
            }
            return Here(u.Name, u.Library, from) is var (kind, hereLibrary, records, basedOn)
                ? new ProgramFile(u.Name, u.Library, u.How, u.Use, u.Line, true, kind, hereLibrary, records, basedOn)
                : new ProgramFile(u.Name, u.Library, u.How, u.Use, u.Line, true, null, null, null, []);
        })];
    }

    /// <summary>What a database file is here: its kind, its library, how many records it has, and (a logical file) the physical files it's over. Null when it isn't here.</summary>
    internal (string Kind, string Library, long? Records, IReadOnlyList<string> BasedOn)? Here(string name, string? library, string? first)
    {
        if (FindDatabaseFile(library is null ? name : $"{library}/{name}", first) is not { } file) return null;
        var made = Store.FindObject(file.Library, file.Name, "*FILE");
        IReadOnlyList<string> basedOn = [];
        string kind;
        if (made is { Kind: "LF" })
        {
            var dds = DatabaseDds.Parse(made.Name, made.Source, logical: true);
            basedOn = [.. dds.Others.Prepend(dds).Select(f => f.BasedOn).OfType<string>().Distinct()];
            kind = "Logical file";
        }
        else if (made is { Kind: "PF" }) kind = "Physical file";
        else
        {
            var relation = Store.Select("""
                SELECT c.relkind::text FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = @s AND lower(c.relname) = lower(@t)
                """, ("s", Store.SchemaName(file.Library).Trim('"')), ("t", file.Name)).Select(r => (string)r[0]!).FirstOrDefault();
            kind = relation switch { "v" or "m" => "SQL view", "i" => "SQL index", _ => "SQL table" };
        }
        long? records = null;
        if (file.Table.Length > 0)
        {
            try
            {
                records = (long)Store.Select($"SELECT count(*) FROM {file.Table}{(file.Where is { } where ? $" WHERE {where}" : "")}")[0][0]!;
            }
            catch (Npgsql.PostgresException)
            {
            }
        }
        return (kind, file.Library, records, basedOn);
    }
}
