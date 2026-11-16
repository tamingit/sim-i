// The files a CL program declares (DCLF): a display file, which SNDF, RCVF and SNDRCVF show and
// read through the same session RPG uses, with &IN01 to &IN99 its indicators; or a database file,
// which RCVF reads a record at a time, in its key's order. Each field is a variable, &FIELD (or
// &OPNID_FIELD with OPNID).

using SimI.Dds;
using SimI.Rpg;

namespace SimI.Cl;

internal sealed partial class ClProgram
{
    private sealed record DeclaredFile(int Line, string Name, string? OpenId);

    private readonly List<DeclaredFile> _files = [];

    /// <summary>A declared file as the program has it open.</summary>
    private sealed class OpenFile(string name, string prefix)
    {
        public string Name { get; } = name;

        /// <summary>The variables' prefix: & or &OPNID_.</summary>
        public string Prefix { get; } = prefix;

        public DisplaySession? Display { get; init; }
        public DatabaseFile? Database { get; init; }
        public List<object[]>? Rows { get; set; }
        public int Next { get; set; }

        /// <summary>CLOSE: a database file's next RCVF starts again at its first record.</summary>
        public void Close()
        {
            Rows = null;
            Next = 0;
        }
    }

    /// <summary>Opens the declared files and declares their fields (and a display file's indicators).</summary>
    private void OpenFiles(Run run)
    {
        foreach (var declared in _files)
        {
            var prefix = declared.OpenId is null ? "&" : $"&{declared.OpenId}_";
            var key = declared.OpenId ?? "";
            Cell Declare(string field, RpgType type, object? initial = null)
            {
                var name = prefix + field;
                if (!run.Variables.TryGetValue(name, out var cell))
                {
                    run.Variables[name] = cell = new Cell(type);
                    if (initial is not null) cell.Set(initial);
                }
                return cell;
            }
            if (run.Machine.LoadDisplayFile(declared.Name, run.Host.User) is { } display)
            {
                foreach (var field in display.Records.Values.SelectMany(r => r.Fields).Where(f => f.Name is not null))
                    Declare(field.Name!, field.Numeric ? RpgType.Number(Math.Max(field.Length, 1), field.Decimals) : RpgType.Char(Math.Max(field.Length, 1)));
                for (var n = 1; n <= 99; n++) Declare($"IN{n:00}", RpgType.Char(1), "0");
                var declaration = new FileDecl(declared.Line, declared.Name, null, new Dictionary<string, string>());
                var session = new DisplaySession(display, declaration,
                    name => run.Variables.GetValueOrDefault(prefix + name),
                    (_, n) => run.Variables[$"{prefix}IN{n:00}"],
                    run.Host.User, run.Host.State,
                    (id, file) => run.Machine.MessageText(id, file));
                run.Files[key] = new OpenFile(declared.Name, prefix) { Display = session };
                continue;
            }
            if (run.Machine.OpenedFile(declared.Name, run.Host.State, run.Machine.DefaultLibrary(run.Host.User)) is { } database)
            {
                foreach (var field in database.Formats.SelectMany(f => f.Fields)) Declare(field.Name, field.Type);
                run.Files[key] = new OpenFile(declared.Name, prefix) { Database = database };
                continue;
            }
            throw new ClEscape("CPF4101", $"File {declared.Name} not found.");
        }
    }

    /// <summary>RCVF, SNDF, SNDRCVF (and ENDRCV and WAIT, which have nothing to do here).</summary>
    private static void FileOperation(ClCommand c, Run run)
    {
        if (c.Name is "ENDRCV" or "WAIT") return;
        var id = c.Value("OPNID", -1)?.Trim().ToUpperInvariant() ?? "";
        if (!run.Files.TryGetValue(id, out var file))
            throw new ClEscape("CPF4101", id.Length == 0 ? "The program declares no file (DCLF) without OPNID." : $"No file declared with OPNID({id}).");
        var record = c.Value("RCDFMT", -1)?.Trim().ToUpperInvariant();
        if (file.Display is { } session)
        {
            var workstation = run.Host.Workstation ?? throw new ClEscape("CPF4102", $"File {file.Name} needs a display: the job has none.");
            record ??= session.File.Records.Keys.FirstOrDefault() ?? "";
            if (!session.File.Records.ContainsKey(record)) throw new ClEscape("CPF4131", $"Record format {record} not in file {file.Name}.");
            try
            {
                switch (c.Name)
                {
                    case "SNDF":
                        session.Write(record, workstation);
                        break;
                    case "SNDRCVF":
                        session.Write(record, workstation, read: true);
                        break;
                    default:
                        session.Read(record, workstation);
                        break;
                }
            }
            catch (RpgError e)
            {
                throw new ClEscape(e.MessageId ?? "CPF4101", e.Message);
            }
            return;
        }
        if (c.Name != "RCVF") throw new ClEscape("CPF4131", $"{c.Name} is for display files: {file.Name} is a database file.");
        var database = file.Database!;
        file.Rows ??= Rows(database, run.Machine);
        if (file.Next >= file.Rows.Count) throw new ClEscape("CPF0864", $"End of file detected for file {file.Name} in library {database.Library}.");
        var row = file.Rows[file.Next++];
        for (var i = 0; i < database.Fields.Count; i++)
        {
            var cell = run.Variables[file.Prefix + database.Fields[i].Name];
            var value = row[i];
            if (value is DBNull) cell.Clear();
            else cell.Set(value is string text ? text : value is decimal or int or long or short or double ? Values.ToDecimal(Convert.ToDecimal(value)) : Values.ToText(value));
        }
    }

    /// <summary>A database file's records, in its key's order (arrival order without one), with its select/omit rules.</summary>
    private static List<object[]> Rows(DatabaseFile file, Machine machine)
    {
        var order = string.Join(", ", file.Keys.Select(k => (file.Fields.FirstOrDefault(f => f.Name == k.Field)?.SqlColumn ?? SimI.Store.ColumnName(k.Field)) + (k.Descending ? " DESC" : ""))
            .Append(file.RowId));
        var sql = $"SELECT {string.Join(", ", file.Fields.Select(f => f.SqlColumn))} FROM {file.Table}{(file.Where is { } where ? $" WHERE {where}" : "")} ORDER BY {order}";
        var rows = new List<object[]>();
        lock (machine.Store.Gate)
        {
            using var command = machine.Store.Connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var row = new object[reader.FieldCount];
                reader.GetValues(row);
                rows.Add(row);
            }
        }
        return rows;
    }
}
