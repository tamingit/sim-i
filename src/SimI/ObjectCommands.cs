// CL commands on database files and objects that CL programs use: OVRDBF and DLTOVR (which file a
// program's open gets), CLRPFM, CPYF, CRTDUPOBJ, CPYTOIMPF, SBMJOB; and those with nothing for the
// Sim i to do (members, authorities, closing), which are taken.

using System.Text;
using SimI.Dds;
using SimI.Rpg;
using Npgsql;

namespace SimI;

/// <summary>OVRDBF: the file a program's open of <c>FILE</c> gets, and its member and sharing.</summary>
internal sealed record DatabaseOverride(string ToFile, string? Member, bool Share);

public sealed partial class Machine
{
    /// <summary>The file a program opening this name gets: OVRDBF's TOFILE, or the name itself.</summary>
    internal static string Overridden(string name, JobState state) =>
        state.DatabaseOverrides.TryGetValue(Qualified(name).Name, out var o) ? o.ToFile : name;

    private CommandResult RunObjectCommand(ClCommand command, IJobHost host)
    {
        CommandResult Fail(string message, string id) => new(false, message, MessageId: id);
        var first = DefaultLibrary(host.User);
        switch (command.Name)
        {
            case "OVRDBF":
            {
                var file = (command.Value("FILE", 0) ?? "").Trim().ToUpperInvariant();
                if (file.Length == 0) return Fail("Parameter FILE required.", "CPD0072");
                var to = command.Value("TOFILE", 1)?.Trim().ToUpperInvariant() is { Length: > 0 } t && !t.StartsWith("*FILE") ? t.Replace("*LIBL/", "") : file;
                var member = command.Value("MBR", 2)?.Trim().ToUpperInvariant();
                host.State.DatabaseOverrides[file] = new DatabaseOverride(to, member is null or "*FIRST" ? null : member,
                    command.Value("SHARE", -1)?.Trim().ToUpperInvariant() == "*YES");
                return new CommandResult(true, "");
            }
            case "CLRPFM":
            {
                var name = command.Value("FILE", 0) ?? "";
                if (FindDatabaseFile(Overridden(name, host.State), first) is not { } file) return Fail($"File {name.ToUpperInvariant()} not found.", "CPF3142");
                return Sql($"DELETE FROM {file.Table}") is { } error ? Fail($"Member of file {file.Name} not cleared: {error}", "CPF3133")
                    : new CommandResult(true, $"Member {file.Name} file {file.Name} in {file.Library} cleared.");
            }
            case "CPYF" or "CPYFRMQRYF":
                return CopyFile(command, host);
            case "OPNQRYF":
                return OpenQuery(command, host);
            case "CPYFRMIMPF":
                return CopyFromImport(command, host);
            case "CLOF":
                return CloseFile(command, host);
            case "CRTDUPOBJ":
                return DuplicateObject(command, host);
            case "CPYTOIMPF":
                return CopyToImport(command, host);
            case "SBMJOB":
            {
                // The command runs in a job of its own, as a batch job, while this one goes on.
                var text = command.Value("CMD", 0) ?? command.Value("RQSDTA", -1) ?? "";
                if (text.Length == 0) return Fail("Parameter CMD required.", "CPD0072");
                var name = command.Value("JOB", 1)?.Trim().ToUpperInvariant() is { Length: > 0 } j && !j.StartsWith('*') ? j : "QDFTJOBD";
                var job = new BatchJob(host.User, CancellationToken.None, new JobState(name));
                job.State.LibraryList.AddRange(host.State.LibraryList);
                _ = Task.Run(() =>
                {
                    try
                    {
                        Run(Unquoted(text), job);
                    }
                    catch (Exception e)
                    {
                        Console.Error.WriteLine($"Sim i: submitted job {name} ended in error: {e.Message}");
                    }
                });
                return new CommandResult(true, $"Job {job.State.Number}/{host.User}/{name} submitted to job queue QBATCH in library QGPL.");
            }
            case "ADDPFM" or "RMVM" or "CHGPFM" or "INZPFM" or "RGZPFM":
            {
                // A file here has one member: the file itself.
                var name = command.Value("FILE", 0) ?? "";
                return FindDatabaseFile(name, first) is null ? Fail($"File {name.ToUpperInvariant()} not found.", "CPF3142") : new CommandResult(true, "");
            }
            default:
                // CLOF, OPNDBF, POSDBF, authorities, ownership, journals, messages to users: nothing for Sim i to do.
                return new CommandResult(true, "");
        }
    }

    /// <summary>How many records a file has (meeting its select/omit rules).</summary>
    internal long CountRecords(DatabaseFile file)
    {
        lock (Store.Gate)
        {
            using var command = Store.Connection.CreateCommand();
            command.CommandText = $"SELECT count(*) FROM {file.Table}{(file.Where is { } where ? $" WHERE {where}" : "")}";
            return Convert.ToInt64(command.ExecuteScalar());
        }
    }

    /// <summary>
    /// CPYFRMIMPF: a stream file's delimited records into a file (added, or in place of what's there with
    /// MBROPT(*REPLACE)): fields split at FLDDLM (a comma), strings in STRDLM (a double quote).
    /// </summary>
    private CommandResult CopyFromImport(ClCommand command, IJobHost host)
    {
        CommandResult Fail(string message, string id) => new(false, message, MessageId: id);
        if (command.Value("FROMSTMF", -1) is not { } stmf) return Fail("CPYFRMIMPF from a file (FROMFILE) isn't supported here: give FROMSTMF.", "CPF2817");
        var path = Ifs.Absolute(Unquoted(stmf), host.State.CurrentDirectory);
        if (Ifs.ReadText(path) is not { } text) return Fail($"Stream file {path} not found.", "CPFA0A9");
        var toName = command.Value("TOFILE", -1) ?? "";
        if (FindDatabaseFile(Overridden(toName, host.State), DefaultLibrary(host.User)) is not { } to) return Fail($"File {toName.ToUpperInvariant()} not found.", "CPF2817");
        var field = command.Value("FLDDLM", -1) is { } f ? Unquoted(f) : ",";
        var quote = command.Value("STRDLM", -1) is { } q ? Unquoted(q) : "\"";
        if (quote.Equals("*NONE", StringComparison.OrdinalIgnoreCase)) quote = "";
        var skip = int.TryParse(command.Value("FROMRCD", -1)?.Trim(), out var fromRecord) && fromRecord > 1 ? fromRecord - 1 : 0;
        if (command.Value("MBROPT", -1)?.Trim().ToUpperInvariant() == "*REPLACE" && Sql($"DELETE FROM {to.Table}") is { } cleared) return Fail(cleared, "CPF2817");
        var columns = to.Fields.Where(c => c.Expression is null).ToList();
        var copied = 0;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n').Skip(skip))
        {
            if (line.Trim().Length == 0) continue;
            var values = new List<string>();
            var current = new StringBuilder();
            var inQuote = false;
            for (var i = 0; i < line.Length; i++)
            {
                if (quote.Length > 0 && line[i] == quote[0])
                {
                    if (inQuote && i + 1 < line.Length && line[i + 1] == quote[0])
                    {
                        current.Append(quote[0]);
                        i++;
                    }
                    else inQuote = !inQuote;
                    continue;
                }
                if (!inQuote && field.Length > 0 && line[i] == field[0])
                {
                    values.Add(current.ToString());
                    current.Clear();
                    continue;
                }
                current.Append(line[i]);
            }
            values.Add(current.ToString());
            var parameters = new List<(string, object)>();
            for (var i = 0; i < columns.Count; i++)
            {
                var raw = i < values.Count ? values[i].Trim() : "";
                parameters.Add(($"v{i}", columns[i].Type.IsNumeric ? Values.ParseNumber(raw.Length > 0 ? raw : "0")
                    : columns[i].Type.Kind is RpgKind.Date ? Values.ParseDate(raw, "*ISO") : raw));
            }
            var sql = $"INSERT INTO {to.Table} ({string.Join(", ", columns.Select(c => c.SqlColumn))}) VALUES ({string.Join(", ", columns.Select((_, i) => $"@v{i}"))})";
            if (Sql(sql, [.. parameters]) is { } failed) return Fail($"Record {copied + skip + 1} of {path} not copied: {failed}", "CPF2817");
            copied++;
        }
        return new CommandResult(true, $"{copied} records copied to member {to.Name}.");
    }

    /// <summary>Runs SQL; null when it worked, else why not.</summary>
    private string? Sql(string sql, params (string Name, object Value)[] parameters)
    {
        try
        {
            lock (Store.Gate)
            {
                using var command = Store.Connection.CreateCommand();
                command.CommandText = sql;
                foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
                command.ExecuteNonQuery();
            }
            return null;
        }
        catch (PostgresException e)
        {
            return Store.Clean(e);
        }
    }

    /// <summary>
    /// CPYF: FROMFILE's records into TOFILE, added (MBROPT(*ADD)) or in place of what's there
    /// (*REPLACE), making TOFILE like FROMFILE with CRTFILE(*YES). Fields go by position, or by name
    /// with FMTOPT(*MAP) or *DROP. INCREL selects records.
    /// </summary>
    private CommandResult CopyFile(ClCommand command, IJobHost host)
    {
        CommandResult Fail(string message, string id) => new(false, message, MessageId: id);
        var first = DefaultLibrary(host.User);
        // CPYFRMQRYF: from an open query (FROMOPNID).
        var fromName = command.Name == "CPYFRMQRYF" ? command.Value("FROMOPNID", 0) ?? "" : command.Value("FROMFILE", 0) ?? "";
        var toName = command.Value("TOFILE", 1) ?? "";
        var from = command.Name == "CPYFRMQRYF" ? QueryFile(fromName, host.State) : OpenedFile(fromName, host.State, first);
        if (from is null) return Fail(command.Name == "CPYFRMQRYF" ? $"Open identifier {fromName.ToUpperInvariant()} not found." : $"File {fromName.ToUpperInvariant()} not found.",
            command.Name == "CPYFRMQRYF" ? "CPF2963" : "CPF2817");
        var option = command.Value("MBROPT", -1)?.Trim().ToUpperInvariant() ?? "*NONE";
        var to = FindDatabaseFile(Overridden(toName, host.State), first);
        if (to is null)
        {
            if (command.Value("CRTFILE", -1)?.Trim().ToUpperInvariant() != "*YES") return Fail($"File {toName.ToUpperInvariant()} not found.", "CPF2817");
            var (library, name) = Qualified(toName.ToUpperInvariant());
            library ??= from.Library;
            if (Sql($"CREATE TABLE {(library == "QTEMP" ? "pg_temp" : Store.SchemaName(library))}.{Store.ColumnName(name)} (LIKE {from.Table} INCLUDING DEFAULTS INCLUDING IDENTITY)") is { } error)
                return Fail($"File {name} not created: {error}", "CPF2817");
            to = FindDatabaseFile($"{library}/{name}", first) ?? throw new InvalidOperationException($"{library}/{name} isn't there after CREATE TABLE");
        }
        else if (option == "*NONE") return Fail($"Member {to.Name} not cleared and records not added: MBROPT(*ADD) or MBROPT(*REPLACE) is needed.", "CPF2817");
        var format = command.Value("FMTOPT", -1)?.Trim().ToUpperInvariant() ?? "";
        var byName = format.Contains("*MAP") || format.Contains("*DROP");
        var targets = new List<string>();
        var sources = new List<string>();
        for (var i = 0; i < to.Fields.Count; i++)
        {
            var target = to.Fields[i];
            var source = byName ? from.Fields.FirstOrDefault(f => f.Name == target.Name) : i < from.Fields.Count ? from.Fields[i] : null;
            if (source is null) continue;
            targets.Add(target.SqlColumn);
            sources.Add(source.SqlColumn);
        }
        var where = new List<string>();
        if (from.Where is { } selected) where.Add($"({selected})");
        // INCREL((*IF field *EQ value) (*AND ...) (*OR ...)).
        if (command.Value("INCREL", -1) is { } increl)
        {
            var clause = new StringBuilder();
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(increl,
                @"\(\s*(\*IF|\*AND|\*OR)\s+(\S+)\s+(\*EQ|\*NE|\*GT|\*LT|\*GE|\*LE|\*NG|\*NL)\s+('(?:[^']|'')*'|\S+?)\s*\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                var field = from.Fields.FirstOrDefault(f => f.Name.Equals(m.Groups[2].Value, StringComparison.OrdinalIgnoreCase))?.SqlColumn ?? Store.ColumnName(m.Groups[2].Value);
                var op = m.Groups[3].Value.ToUpperInvariant() switch { "*EQ" => "=", "*NE" => "<>", "*GT" => ">", "*LT" => "<", "*GE" or "*NL" => ">=", _ => "<=" };
                var join = m.Groups[1].Value.ToUpperInvariant() switch { "*AND" => " AND ", "*OR" => " OR ", _ => "" };
                clause.Append(join).Append($"{field} {op} {m.Groups[4].Value}");
            }
            if (clause.Length > 0) where.Add($"({clause})");
        }
        if (option == "*REPLACE" && Sql($"DELETE FROM {to.Table}") is { } cleared) return Fail($"Member {to.Name} not cleared: {cleared}", "CPF2817");
        var sql = $"INSERT INTO {to.Table} ({string.Join(", ", targets)}) SELECT {string.Join(", ", sources)} FROM {from.Table}"
            + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
            + $" ORDER BY {string.Join(", ", from.Keys.Select(k => from.Fields.FirstOrDefault(f => f.Name == k.Field)?.SqlColumn ?? Store.ColumnName(k.Field)).Append(from.RowId))}";
        if (Sql(sql) is { } failed) return Fail($"Records not copied from {from.Name} to {to.Name}: {failed}", "CPF2817");
        return new CommandResult(true, $"Records copied from member {from.Name} to member {to.Name}.");
    }

    /// <summary>CRTDUPOBJ: a copy of an object (a file with its records with DATA(*YES), a program, a data area ...) in another library.</summary>
    private CommandResult DuplicateObject(ClCommand command, IJobHost host)
    {
        CommandResult Fail(string message, string id) => new(false, message, MessageId: id);
        var name = (command.Value("OBJ", 0) ?? "").Trim().ToUpperInvariant();
        var fromLibrary = (command.Value("FROMLIB", 1) ?? "*LIBL").Trim().ToUpperInvariant();
        var type = (command.Value("OBJTYPE", 2) ?? "*ALL").Trim().ToUpperInvariant();
        var toLibrary = (command.Value("TOLIB", 3) ?? "*FROMLIB").Trim().ToUpperInvariant();
        var newName = (command.Value("NEWOBJ", 4) ?? "*OBJ").Trim().ToUpperInvariant();
        if (newName == "*OBJ") newName = name;
        var library = fromLibrary is "*LIBL" or "*CURLIB" ? null : fromLibrary;
        if (toLibrary == "*FROMLIB") toLibrary = library ?? DefaultLibrary(host.User);
        var data = command.Value("DATA", -1)?.Trim().ToUpperInvariant() == "*YES";
        var types = type == "*ALL" ? ["*FILE", "*PGM", "*SRVPGM", "*DTAARA", "*MSGF", "*CMD"] : type.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var objectType in types)
        {
            if (objectType == "*FILE" && FindDatabaseFile(library is null ? name : $"{library}/{name}", DefaultLibrary(host.User)) is { } file
                && Store.FindObject(library, name, "*FILE", DefaultLibrary(host.User)) is not { Kind: "LF" })
            {
                var schema = toLibrary == "QTEMP" ? "pg_temp" : Store.SchemaName(toLibrary);
                if (toLibrary != "QTEMP" && !Store.HasLibrary(toLibrary)) return Fail($"Library {toLibrary} not found.", "CPF2110");
                if (Sql($"CREATE TABLE {schema}.{Store.ColumnName(newName)} (LIKE {file.Table} INCLUDING ALL)") is { } error)
                    return Fail($"Object {newName} in {toLibrary} not created: {error}", "CPF2130");
                var columns = string.Join(", ", file.Fields.Select(f => f.SqlColumn));
                if (data && Sql($"INSERT INTO {schema}.{Store.ColumnName(newName)} ({columns}) SELECT {columns} FROM {file.Table}") is { } copyError)
                    return Fail($"Records not copied to {newName}: {copyError}", "CPF2130");
                if (toLibrary != "QTEMP" && Store.FindObject(library, name, "*FILE", DefaultLibrary(host.User)) is { } dds)
                    Store.SaveObject(toLibrary, newName, "*FILE", dds.Attribute, dds.Source);
                return new CommandResult(true, $"Object {name} type *FILE created in library {toLibrary}.");
            }
            if (objectType != "*FILE" && Store.FindObject(library, name, objectType, DefaultLibrary(host.User)) is { } o)
            {
                if (toLibrary == "QTEMP") return Fail($"Object {name} type {objectType} can't be put in QTEMP here.", "CPF2130");
                if (Store.FindObject(toLibrary, newName, objectType) is not null) return Fail($"Object {newName} type {objectType} already exists in library {toLibrary}.", "CPF2130");
                Store.SaveObject(toLibrary, newName, objectType, o.Attribute, o.Source);
                return new CommandResult(true, $"Object {name} type {objectType} created in library {toLibrary}.");
            }
        }
        return Fail($"Object {name} in library {fromLibrary} type {type} not found.", "CPF2105");
    }

    /// <summary>CPYTOIMPF: a file's records to a stream file, delimited (DTAFMT(*DLM), the default) or fixed.</summary>
    private CommandResult CopyToImport(ClCommand command, IJobHost host)
    {
        CommandResult Fail(string message, string id) => new(false, message, MessageId: id);
        var fromName = command.Value("FROMFILE", 0) ?? "";
        if (FindDatabaseFile(Overridden(fromName, host.State), DefaultLibrary(host.User)) is not { } from) return Fail($"File {fromName.ToUpperInvariant()} not found.", "CPF2817");
        if (command.Value("TOSTMF", -1) is not { } stmf) return Fail("CPYTOIMPF to a file (TOFILE) isn't supported here: give TOSTMF.", "CPF2817");
        var path = Ifs.Absolute(Unquoted(stmf), host.State.CurrentDirectory);
        var delimited = command.Value("DTAFMT", -1)?.Trim().ToUpperInvariant() != "*FIXED";
        var field = command.Value("FLDDLM", -1) is { } f ? Unquoted(f) : ",";
        var quote = command.Value("STRDLM", -1) is { } q ? Unquoted(q) : "\"";
        if (quote.Equals("*NONE", StringComparison.OrdinalIgnoreCase)) quote = "";
        var end = command.Value("RCDDLM", -1)?.Trim().ToUpperInvariant() switch { "*CRLF" => "\r\n", "*CR" => "\r", _ => "\n" };
        var header = command.Value("ADDCOLNAM", -1)?.Trim().ToUpperInvariant() is "*SQL" or "*SYS";
        var text = new StringBuilder();
        if (header && delimited) text.Append(string.Join(field, from.Fields.Select(c => c.Name))).Append(end);
        lock (Store.Gate)
        {
            using var sql = Store.Connection.CreateCommand();
            sql.CommandText = $"SELECT {string.Join(", ", from.Fields.Select(c => c.SqlColumn))} FROM {from.Table}{(from.Where is { } w ? $" WHERE {w}" : "")}"
                + $" ORDER BY {string.Join(", ", from.Keys.Select(k => from.Fields.FirstOrDefault(c => c.Name == k.Field)?.SqlColumn ?? Store.ColumnName(k.Field)).Append(from.RowId))}";
            using var reader = sql.ExecuteReader();
            while (reader.Read())
            {
                var values = new List<string>();
                for (var i = 0; i < from.Fields.Count; i++)
                {
                    var type = from.Fields[i].Type;
                    var value = reader.IsDBNull(i) ? "" : reader.GetValue(i) is decimal d ? Values.NumberText(d) : Values.ToText(reader.GetValue(i));
                    if (!delimited) values.Add(type.IsNumeric ? value.PadLeft(type.Length + 2) : value.PadRight(type.Length));
                    else values.Add(type.IsNumeric ? value : quote + value.TrimEnd().Replace(quote.Length > 0 ? quote : "\u0000", quote + quote) + quote);
                }
                text.Append(string.Join(delimited ? field : "", values)).Append(end);
            }
        }
        var option = command.Value("MBROPT", -1)?.Trim().ToUpperInvariant() ?? "*ADD";
        var existing = option == "*ADD" ? Ifs.ReadText(path) ?? "" : "";
        try
        {
            Ifs.WriteText(path, existing + text);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Fail($"Stream file {path} not written: {e.Message}", "CPFA0A9");
        }
        return new CommandResult(true, $"Data copied to stream file {path}.");
    }
}
