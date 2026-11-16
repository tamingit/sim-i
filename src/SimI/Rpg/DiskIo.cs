// Native record I/O on database files: CHAIN, SETLL, SETGT, READ, READE, READP, READPE, WRITE,
// UPDATE, DELETE, UNLOCK and FEOD, as RPG does them, on the table a file is (a physical file, a
// logical file's physical file with its select/omit condition, an SQL table or view, or an SQL
// index's table). A keyed file is read in its key's order, an unkeyed one in arrival order (the
// row's id breaks ties, and is the relative record number). The file's position is the last record
// read, or a key that SETLL or SETGT gave: each read is a query for the next row past it.

using System.Globalization;
using SimI.Dds;
using Npgsql;

namespace SimI.Rpg;

internal sealed partial class Interpreter
{
    private sealed class DiskState(FileDecl declaration, DatabaseFile file)
    {
        public FileDecl Declaration { get; } = declaration;
        public DatabaseFile File { get; } = file;

        /// <summary>The record format's name in the program (after RENAME).</summary>
        public string Format => Declaration.Renames?.GetValueOrDefault(File.Format) ?? File.Format;

        /// <summary>The key's columns: none for a file read in arrival order.</summary>
        public IReadOnlyList<string> Keys => Declaration.Keyed ? [.. File.Keys.Select(k => k.Field)] : [];

        public bool Open { get; set; }

        /// <summary>The record last read: its key's values and its row id.</summary>
        public object[]? After { get; set; }

        /// <summary>Where SETLL (inclusive) or SETGT left the file: a key, or part of one.</summary>
        public (object[] Key, bool Inclusive)? Bound { get; set; }

        /// <summary>SETGT *HIVAL, or a read past the last record: the file is at its end.</summary>
        public bool AtEnd { get; set; }

        /// <summary>The search argument of the last CHAIN, SETLL or SETGT, for READE and READPE with no key of their own.</summary>
        public object[]? LastKey { get; set; }

        /// <summary>The row the last read locked, for UPDATE and DELETE: its row id.</summary>
        public object? Locked { get; set; }

        public bool Eof { get; set; }
        public bool Found { get; set; }
        public bool Equal { get; set; }

        /// <summary>The field's name in the program (after PREFIX).</summary>
        /// <summary>A field's column, quoted, for SQL; the row ID's as it is.</summary>
        public string Column(string field) =>
            File.Fields.FirstOrDefault(f => f.Name == field)?.SqlColumn ?? (field == File.RowId ? field : Store.ColumnName(field));

        /// <summary>The fields I specs give other names (external name to the program's), for this record format.</summary>
        public Dictionary<string, string> InputNames { get; init; } = [];

        public string Name(DbField field) =>
            InputNames.TryGetValue(field.Name, out var renamed) ? renamed
            : Declaration.Prefix is { } prefix ? Prefixed(field.Name, prefix, Declaration.PrefixReplace) : field.Name;

        public bool Updates => Declaration.Usage is null ? false : Declaration.Usage.Contains("*UPDATE") || Declaration.Usage.Contains("*DELETE");

        public void Reset()
        {
            (After, Bound, AtEnd, LastKey, Locked, Eof, Found, Equal) = (null, null, false, null, null, false, false, false);
        }
    }

    private readonly Dictionary<string, DiskState> _disks = [];

    /// <summary>A database file's fields: variables of the program, as a display file's are.</summary>
    private void DefineDiskFields(FileDecl file)
    {
        // OVRDBF: the file the job's override names.
        var database = _machine.OpenedFile(file.ExtFile ?? file.Name, _host.State, _library.Length > 0 ? _library : _machine.DefaultLibrary(_host.User))
            ?? throw new RpgError(1211, $"File {file.ExtFile ?? file.Name} not found: create it first (CRTPF, CRTLF or CREATE TABLE).", "CPF4101");
        var state = new DiskState(file, database) { InputNames = InputNames(file, database) };
        _disks[file.Name] = state;
        foreach (var field in database.Fields) _globals.TryAdd(state.Name(field), new Cell(field.Type));
        // A multiple-format logical file: an operation naming another format works on that format's
        // records (one naming the file, on its first format's).
        foreach (var other in database.Others)
        {
            var more = new DiskState(file, other) { InputNames = InputNames(file, other) };
            _disks[$"{file.Name}/{other.Format}"] = more;
            foreach (var field in other.Fields) _globals.TryAdd(more.Name(field), new Cell(field.Type));
        }
    }

    /// <summary>A record format's fields that its I specs rename (columns 21 to 30 external, 49 to 62 the program's).</summary>
    private Dictionary<string, string> InputNames(FileDecl file, DatabaseFile format)
    {
        var name = file.Renames?.GetValueOrDefault(format.Format) ?? format.Format;
        return _program.InputRecords.Where(r => r.Format == name || r.Format == format.Format).SelectMany(r => r.Fields)
            .Where(f => f.Name != f.External).GroupBy(f => f.External).ToDictionary(g => g.Key, g => g.First().Name);
    }

    private DiskState? DiskFor(string name) =>
        _disks.Values.FirstOrDefault(d => d.Declaration.Name == name) ?? _disks.Values.FirstOrDefault(d => d.Format == name);

    private void DiskOperation(DisplayStmt statement, DiskState disk, Frame frame)
    {
        if (!disk.Open) throw new RpgError(1211, $"File {disk.Declaration.Name} isn't open.");
        var op = statement.Op;
        var keys = statement.Keys is { } list ? SearchValues(list, frame) : null;
        switch (op)
        {
            case "CHAIN":
            {
                var row = disk.Keys.Count == 0
                    ? Fetch(disk, $"{disk.File.RowId} = @k0", [Values.ToDecimal(keys![0])], forward: true, fromPosition: false)
                    : Fetch(disk, Equal(disk, keys!.Length), keys, forward: true, fromPosition: false);
                disk.LastKey = keys;
                disk.Found = row is not null;
                _found = disk.Found;
                if (row is null)
                {
                    disk.Locked = null;
                    break;
                }
                Take(disk, row, statement, frame);
                break;
            }
            case "SETLL" or "SETGT":
            {
                disk.Reset();
                if (keys is [SpecialValue { Name: "*LOVAL" or "*START" }] && op == "SETLL" || keys is [SpecialValue { Name: "*START" }])
                    break;
                if (keys is [SpecialValue { Name: "*HIVAL" or "*END" }])
                {
                    disk.AtEnd = true;
                    break;
                }
                disk.LastKey = keys;
                if (disk.Keys.Count == 0)
                {
                    // An unkeyed file: a relative record number.
                    var rrn = (long)Values.ToDecimal(keys![0]);
                    disk.After = [op == "SETLL" ? rrn - 1 : rrn];
                    disk.Equal = Count(disk, $"{disk.File.RowId} = @k0", [rrn]) > 0;
                    break;
                }
                disk.Bound = (keys!, op == "SETLL");
                disk.Equal = Count(disk, Equal(disk, keys!.Length), keys) > 0;
                disk.Found = op == "SETLL"
                    ? Count(disk, $"{Row(disk, disk.Keys.Take(keys.Length))} >= {Row(keys.Length)}", keys) > 0
                    : Count(disk, $"{Row(disk, disk.Keys.Take(keys.Length))} > {Row(keys.Length)}", keys) > 0;
                _found = disk.Found;
                break;
            }
            case "READ" or "READP":
            {
                var row = Fetch(disk, null, [], forward: op == "READ", fromPosition: true);
                Read(disk, row, statement, frame, op == "READ");
                break;
            }
            case "READE" or "READPE":
            {
                var key = keys ?? disk.LastKey ?? [];
                if (key is [SpecialValue { Name: "*KEY" }]) key = disk.LastKey ?? [];
                var row = key.Length == 0
                    ? Fetch(disk, null, [], forward: op == "READE", fromPosition: true)
                    : Fetch(disk, Equal(disk, key.Length), key, forward: op == "READE", fromPosition: true);
                Read(disk, row, statement, frame, op == "READE");
                break;
            }
            case "WRITE":
            {
                // A derived field (SST, CONCAT) is for input only.
                var fields = disk.File.Fields.Where(f => f.Expression is null).ToList();
                var values = fields.Select(f => Bind(FieldValue(disk, f, statement.Data, frame))).ToList();
                Run(disk, $"INSERT INTO {disk.File.Table} ({string.Join(", ", fields.Select(f => f.SqlColumn))}) VALUES ({string.Join(", ", fields.Select((_, i) => $"@v{i}"))})", values);
                break;
            }
            case "UPDATE":
            {
                if (disk.Locked is not { } rowid) throw new RpgError(1221, $"UPDATE of {statement.Record} without a record read for update first.");
                var fields = (statement.Fields is { } named ? disk.File.Fields.Where(f => named.Contains(disk.Name(f)) || named.Contains(f.Name)) : disk.File.Fields)
                    .Where(f => f.Expression is null).ToList();
                var values = fields.Select(f => Bind(FieldValue(disk, f, statement.Data, frame))).Append((object)rowid).ToList();
                Run(disk, $"UPDATE {disk.File.Table} SET {string.Join(", ", fields.Select((f, i) => $"{f.SqlColumn} = @v{i}"))} WHERE {disk.File.RowId} = @v{fields.Count}", values);
                disk.Locked = null;
                break;
            }
            case "DELETE":
            {
                object rowid;
                if (keys is not null)
                {
                    var row = disk.Keys.Count == 0
                        ? Fetch(disk, $"{disk.File.RowId} = @k0", [Values.ToDecimal(keys[0])], forward: true, fromPosition: false)
                        : Fetch(disk, Equal(disk, keys.Length), keys, forward: true, fromPosition: false);
                    disk.Found = row is not null;
                    _found = disk.Found;
                    if (row is null) break;
                    rowid = row[0];
                }
                else rowid = disk.Locked ?? throw new RpgError(1221, $"DELETE of {statement.Record} without a record read for update first.");
                Run(disk, $"DELETE FROM {disk.File.Table} WHERE {disk.File.RowId} = @v0", [rowid]);
                disk.Locked = null;
                break;
            }
            case "UNLOCK":
                disk.Locked = null;
                break;
            case "FEOD":
                break;
            default:
                throw new RpgError(1299, $"{op} isn't an operation on a database file.");
        }
    }

    /// <summary>The search argument's values: a list of them, %KDS(ds : n), or a special value.</summary>
    private object[] SearchValues(IReadOnlyList<Expr> keys, Frame frame)
    {
        if (keys is [BifExpr { Name: "%KDS" } kds])
        {
            var ds = Locate(kds.Args[0], frame);
            if (ds.Type.Kind != RpgKind.Ds) throw new RpgError(100, "%KDS needs a data structure");
            var count = kds.Args.Count > 1 ? (int)Values.ToDecimal(Evaluate(kds.Args[1], frame)) : ds.Ds.Cells.Count;
            return [.. ds.Ds.Cells.Take(count).Select(c => c.Get())];
        }
        if (keys is [SpecialExpr { Name: "*KEY" }]) return [new SpecialValue("*KEY")];
        return [.. keys.Select(k => Evaluate(k, frame))];
    }

    /// <summary>A record read, or none: the fields (or the data structure) take it, and the file's at it; none is end of file.</summary>
    private void Read(DiskState disk, object[]? row, DisplayStmt statement, Frame frame, bool forward)
    {
        disk.Eof = row is null;
        _eof = disk.Eof;
        if (row is null)
        {
            disk.Locked = null;
            if (forward)
            {
                disk.AtEnd = true;
                disk.After = null;
                disk.Bound = null;
            }
            else
            {
                // Before the first record: a READ starts again from it.
                (disk.After, disk.Bound, disk.AtEnd) = (null, null, false);
            }
            return;
        }
        Take(disk, row, statement, frame);
    }

    /// <summary>Moves a row into the record's fields (or the data structure), and makes it the file's position.</summary>
    private void Take(DiskState disk, object[] row, DisplayStmt statement, Frame frame)
    {
        var rowid = row[0];
        var fields = disk.File.Fields;
        var target = statement.Data is { } data ? Locate(data, frame) : null;
        for (var i = 0; i < fields.Count; i++)
        {
            var cell = target is null ? _globals.GetValueOrDefault(disk.Name(fields[i])) : target.Ds.Field(disk.Name(fields[i])) ?? target.Ds.Field(fields[i].Name);
            if (cell is null) continue;
            var value = row[i + 1];
            if (value is DBNull) cell.Clear();
            else cell.Set(DbValue(value, cell.Type), halfAdjust: true);
        }
        var keyValues = disk.Keys.Select(k => row[1 + fields.ToList().FindIndex(f => f.Name == k)]).Append(rowid).ToArray();
        (disk.After, disk.Bound, disk.AtEnd) = (keyValues, null, false);
        disk.Locked = statement.NoLock || !disk.Updates ? null : rowid;
    }

    /// <summary>A field's value to write: from the data structure, when there is one, or the program's field.</summary>
    private object FieldValue(DiskState disk, DbField field, Expr? data, Frame frame)
    {
        var name = disk.Name(field);
        if (data is not null)
        {
            var ds = Locate(data, frame);
            return (ds.Ds.Field(name) ?? ds.Ds.Field(field.Name))?.Get() ?? Values.Default(field.Type);
        }
        return _globals.GetValueOrDefault(name)?.Get() ?? Values.Default(field.Type);
    }

    /// <summary>A database value as an RPG variable of this type takes it.</summary>
    internal static object DbValue(object value, RpgType type) => value switch
    {
        long l => (decimal)l,
        int i => (decimal)i,
        short s => (decimal)s,
        double d => type.Kind == RpgKind.Float ? d : (decimal)d,
        float f => type.Kind == RpgKind.Float ? f : (decimal)f,
        string s when type.IsNumeric => Values.ParseNumber(s),
        string s when type.Kind == RpgKind.Time => s.Replace('.', ':'),
        TimeSpan t => TimeOnly.FromTimeSpan(t),
        DateTime t when type.Kind == RpgKind.Date => DateOnly.FromDateTime(t),
        bool b => b ? "1" : "0",
        byte[] bytes => Convert.ToHexString(bytes),
        var other => other,
    };

    /// <summary>A value as a parameter: an RPG value bound as embedded SQL binds it, a value read from the database as it is.</summary>
    private static object Parameter(object value) => value switch
    {
        string or decimal or bool or DateOnly or TimeOnly or DateTime or double or long or DBNull => Bind(value),
        var other => other,
    };

    private static string Row(DiskState disk, IEnumerable<string> fields) => $"({string.Join(", ", fields.Select(disk.Column))})";

    private static string Row(int count, string prefix = "@k") => $"({string.Join(", ", Enumerable.Range(0, count).Select(i => $"{prefix}{i}"))})";

    /// <summary>The condition that the key's first n fields are the search argument's.</summary>
    private static string Equal(DiskState disk, int n) => $"{Row(disk, disk.Keys.Take(n))} = {Row(n)}";

    /// <summary>
    /// The next row (or the one before) from the file's position, or the first that meets the
    /// condition: its row id, then its fields. Null when there's none.
    /// </summary>
    private object[]? Fetch(DiskState disk, string? condition, object[] arguments, bool forward, bool fromPosition)
    {
        var conditions = new List<string>();
        var parameters = new List<(string, object)>();
        for (var i = 0; i < arguments.Length; i++) parameters.Add(($"k{i}", Parameter(arguments[i])));
        if (condition is not null) conditions.Add(condition);
        if (disk.File.Where is { } where) conditions.Add($"({where})");
        var order = disk.Keys.Append(disk.File.RowId).ToList();
        if (fromPosition)
        {
            if (disk.After is { } after)
            {
                conditions.Add($"{Row(disk, order)} {(forward ? ">" : "<")} {Row(order.Count, "@p")}");
                for (var i = 0; i < after.Length; i++) parameters.Add(($"p{i}", Parameter(after[i])));
            }
            else if (disk.Bound is var (key, inclusive))
            {
                var columns = Row(disk, disk.Keys.Take(key.Length));
                var op = forward ? (inclusive ? ">=" : ">") : (inclusive ? "<" : "<=");
                conditions.Add($"{columns} {op} {Row(key.Length, "@b")}");
                for (var i = 0; i < key.Length; i++) parameters.Add(($"b{i}", Parameter(key[i])));
            }
            else if (forward && disk.AtEnd) return null;
            else if (!forward && !disk.AtEnd) return null;
        }
        var sql = $"SELECT {disk.File.RowId}, {string.Join(", ", disk.File.Fields.Select(f => f.SqlColumn))} FROM {disk.File.Table}"
            + (conditions.Count > 0 ? " WHERE " + string.Join(" AND ", conditions) : "")
            + $" ORDER BY {string.Join(", ", order.Select(c => forward ? disk.Column(c) : disk.Column(c) + " DESC"))} LIMIT 1";
        lock (_machine.Store.Gate)
        {
            using var command = _machine.Store.Connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters) command.Parameters.Add(Store.Untyped(name, value));
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            var row = new object[reader.FieldCount];
            reader.GetValues(row);
            return row;
        }
    }

    private long Count(DiskState disk, string condition, object[] arguments)
    {
        var where = disk.File.Where is { } w ? $" AND ({w})" : "";
        lock (_machine.Store.Gate)
        {
            using var command = _machine.Store.Connection.CreateCommand();
            command.CommandText = $"SELECT count(*) FROM {disk.File.Table} WHERE {condition}{where}";
            for (var i = 0; i < arguments.Length; i++) command.Parameters.Add(Store.Untyped($"k{i}", Parameter(arguments[i])));
            return (long)command.ExecuteScalar()!;
        }
    }

    private void Run(DiskState disk, string sql, List<object> values)
    {
        lock (_machine.Store.Gate)
        {
            using var command = _machine.Store.Connection.CreateCommand();
            command.CommandText = sql;
            for (var i = 0; i < values.Count; i++) command.Parameters.Add(Store.Untyped($"v{i}", Parameter(values[i])));
            try
            {
                command.ExecuteNonQuery();
            }
            catch (PostgresException e) when (e.SqlState == "23505")
            {
                throw new RpgError(1021, $"Duplicate key not allowed for file {disk.Declaration.Name}.", "CPF5026");
            }
            catch (PostgresException e)
            {
                throw new RpgError(1299, $"I/O error on file {disk.Declaration.Name}: {Store.Clean(e)}", "CPF5029");
            }
        }
    }
}
