// Embedded SQL: runs each translated statement on the store's PostgreSQL, moving values between it
// and the program's host variables, and setting SQLCODE and SQLSTATE as Db2 would.

using System.Globalization;
using Npgsql;

namespace FakeIbmI.Rpg;

internal sealed partial class Interpreter
{
    /// <summary>An open cursor: its rows, read when it was opened (the connection takes one reader at a time), and the next one.</summary>
    /// <summary>A cursor's rows, and where it is: 0 before the first, 1 to N on a row, N + 1 after the last.</summary>
    private sealed class OpenCursor(List<object[]> rows) : IDisposable
    {
        private int _at;

        public int Count => rows.Count;

        /// <summary>Moves the cursor; the row it's on, or null when it's before the first or after the last.</summary>
        public object[]? Move(string orientation, long amount)
        {
            long at = orientation switch
            {
                "NEXT" => _at + 1,
                "PRIOR" => _at - 1,
                "FIRST" => 1,
                "LAST" => rows.Count,
                "BEFORE" => 0,
                "AFTER" => rows.Count + 1,
                "CURRENT" => _at,
                "RELATIVE" => _at + amount,
                "ABSOLUTE" => amount >= 0 ? amount : rows.Count + 1 + amount,
                _ => _at + 1,
            };
            _at = (int)Math.Clamp(at, 0, rows.Count + 1);
            if (orientation is "FIRST" or "LAST" && rows.Count == 0) _at = 0;
            return _at >= 1 && _at <= rows.Count ? rows[_at - 1] : null;
        }

        public void Dispose()
        {
        }
    }

    private readonly Dictionary<string, OpenCursor> _cursors = [];
    private readonly Dictionary<string, SqlTranslation> _prepared = [];
    private Cell _sqlCode = null!;
    private Cell _sqlState = null!;
    private Cell _sqlErrd = null!;
    private Cell _sqlEr3 = null!;
    private long _rowCount;

    private void DeclareSql()
    {
        _sqlCode = new Cell(new RpgType(RpgKind.Int, 10));
        _sqlState = new Cell(RpgType.Char(5));
        _sqlErrd = new Cell(new RpgType(RpgKind.Int, 10, Dim: 6));
        _sqlEr3 = new Cell(new RpgType(RpgKind.Int, 10));
        _sqlState.Set("00000");
        _globals["SQLCODE"] = _globals["SQLCOD"] = _sqlCode;
        _globals["SQLSTATE"] = _globals["SQLSTT"] = _sqlState;
        _globals["SQLERRD"] = _sqlErrd;
        _globals["SQLER3"] = _sqlEr3;
    }

    private void CloseSql()
    {
        lock (_machine.Store.Gate)
        {
            foreach (var cursor in _cursors.Values) cursor.Dispose();
        }
        _cursors.Clear();
    }

    private void SetSql(int code, string state, long rows = 0)
    {
        _sqlCode.Set((decimal)code);
        _sqlState.Set(state);
        _rowCount = rows;
        _sqlErrd.Elements![2].Set((decimal)rows);
        _sqlErrd.Elements![4].Set(0m);
        _sqlEr3.Set((decimal)rows);
    }

    private void ExecuteSql(SqlTranslation sql, Frame frame)
    {
        try
        {
            lock (_machine.Store.Gate) RunSql(sql, frame, []);
        }
        catch (PostgresException e)
        {
            // PostgreSQL's SQLSTATE, as Db2's SQLCODE and SQLSTATE.
            var (code, state) = e.SqlState switch
            {
                "42P01" => (-204, "42704"),
                "42703" => (-206, "42703"),
                "42P07" or "42710" => (-601, "42710"),
                "23505" => (-803, "23505"),
                "23502" => (-407, "23502"),
                "23503" => (-530, "23503"),
                "23514" => (-545, "23513"),
                "22001" => (-404, "22001"),
                "22003" => (-406, "22003"),
                "22P02" or "22007" or "22008" => (-180, "22007"),
                "22012" => (-802, "22012"),
                "21000" => (-811, "21000"),
                "42601" or "42883" or "42804" or "42725" => (-104, "42601"),
                "40P01" or "55P03" => (-913, "57033"),
                _ => (-901, "58004"),
            };
            SetSql(code, state);
        }
    }

    private void RunSql(SqlTranslation sql, Frame frame, IReadOnlyList<string> usingNames)
    {
        switch (sql.Kind)
        {
            case SqlKind.NoOp or SqlKind.DeclareCursor:
                SetSql(0, "00000");
                break;
            case SqlKind.Unsupported:
                throw new RpgHalt($"{_name} reached line {_line}, an SQL statement that isn't supported: {sql.Problem}");
            case SqlKind.Query:
            {
                using var command = Command(sql, frame, usingNames);
                object[] row;
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                    {
                        SetSql(100, "02000");
                        break;
                    }
                    row = new object[reader.FieldCount];
                    reader.GetValues(row);
                    if (reader.Read())
                    {
                        SetSql(-811, "21000");
                        break;
                    }
                }
                SetSql(0, "00000", 1);
                Receive(row, sql.Outputs, frame);
                break;
            }
            case SqlKind.Execute or SqlKind.Select:
            {
                using var command = Command(sql, frame, usingNames);
                var rows = command.ExecuteNonQuery();
                var verb = sql.Sql!.TrimStart().Split(' ', 2)[0].ToUpperInvariant();
                var changes = verb is "INSERT" or "UPDATE" or "DELETE" || verb == "WITH" && sql.Sql.Contains("INSERT", StringComparison.OrdinalIgnoreCase);
                if (rows > 0 && (verb == "INSERT" || verb == "WITH") && IdentityOf(command.CommandText) is { } identity) _host.State.LastIdentity = identity;
                if (changes && rows == 0) SetSql(100, "02000");
                else SetSql(0, "00000", changes ? Math.Max(rows, 0) : 0);
                break;
            }
            case SqlKind.Open:
            {
                var name = sql.Cursor!;
                if (!_program.Cursors.TryGetValue(name, out var declared))
                {
                    SetSql(-504, "34000");
                    break;
                }
                if (_cursors.ContainsKey(name))
                {
                    SetSql(-502, "24502");
                    break;
                }
                var select = declared;
                if (declared.Statement is { } statement)
                {
                    if (!_prepared.TryGetValue(statement, out select!))
                    {
                        SetSql(-514, "26501");
                        break;
                    }
                    if (select.Kind is not (SqlKind.Select or SqlKind.Query)) throw new RpgError(100, $"cursor {name}'s statement {statement} isn't a SELECT");
                }
                using (var command = Command(select, frame, sql.Inputs))
                using (var reader = command.ExecuteReader())
                {
                    var rows = new List<object[]>();
                    while (reader.Read())
                    {
                        var row = new object[reader.FieldCount];
                        reader.GetValues(row);
                        rows.Add(row);
                    }
                    _cursors[name] = new OpenCursor(rows);
                }
                SetSql(0, "00000");
                break;
            }
            case SqlKind.Fetch:
            {
                if (!_cursors.TryGetValue(sql.Cursor!, out var cursor))
                {
                    SetSql(-501, "24501");
                    break;
                }
                long Number(string text) => text.StartsWith(':') ? (long)Values.ToDecimal(HostCell(text[1..], frame).Get()) : long.Parse(text);
                var orientation = sql.Orientation ?? "NEXT";
                var amount = sql.Amount is null ? 0 : Number(sql.Amount);
                if (sql.Rows is not null)
                {
                    // A multiple-row FETCH fills a data structure array from its first element. Fewer
                    // rows than asked for is still SQLCODE 0, with +100 in SQLERRD(5); none is +100.
                    var wanted = Number(sql.Rows);
                    var target = HostCell(sql.Outputs[0].Name, frame);
                    var elements = target.Elements ?? throw new RpgError(100, $"a multiple-row FETCH needs an array: {sql.Outputs[0].Name} isn't one");
                    if (wanted < 1 || wanted > 32767)
                    {
                        SetSql(-150, "42815");
                        break;
                    }
                    var got = 0;
                    var row = cursor.Move(orientation, amount);
                    while (row is not null)
                    {
                        if (got >= elements.Length) break;
                        Receive(row, [("", null)], frame, elements[got]);
                        if (++got >= wanted) break;
                        row = cursor.Move("NEXT", 0);
                    }
                    if (got == 0)
                    {
                        SetSql(100, "02000");
                        break;
                    }
                    SetSql(0, "00000", got);
                    _sqlErrd.Elements![4].Set(row is null ? 100m : 0m);
                    break;
                }
                var found = cursor.Move(orientation, amount);
                if (found is null)
                {
                    if (orientation is "BEFORE" or "AFTER") SetSql(0, "00000");
                    else SetSql(100, "02000");
                    break;
                }
                SetSql(0, "00000", 1);
                if (sql.Outputs.Count > 0) Receive(found, sql.Outputs, frame);
                break;
            }
            case SqlKind.Call:
            {
                // A host variable is passed by reference; a literal as a value of its own.
                var cells = new List<Cell>();
                foreach (var argument in sql.Arguments ?? [])
                {
                    if (argument.StartsWith(':')) cells.Add(HostCell(argument[1..], frame));
                    else if (argument == "NULL") cells.Add(new Cell(RpgType.Char(1)));
                    else if (argument.StartsWith('\''))
                    {
                        var text = argument[1..^1].Replace("''", "'");
                        var cell = new Cell(RpgType.Char(Math.Max(1, text.Length)));
                        cell.Set(text);
                        cells.Add(cell);
                    }
                    else
                    {
                        var value = decimal.Parse(argument, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture);
                        var decimals = argument.Contains('.') ? argument.Length - argument.IndexOf('.') - 1 : 0;
                        var cell = new Cell(RpgType.Number(15, Math.Min(decimals, 9)));
                        cell.Set(value);
                        cells.Add(cell);
                    }
                }
                try
                {
                    _machine.Call(sql.Statement!, cells, _host, _depth + 1);
                    SetSql(0, "00000");
                }
                catch (RpgHalt e) when (e.MessageId == "CPF0001" && e.Message.Contains("not found", StringComparison.Ordinal))
                {
                    // SQL0204: the procedure (here, the program) isn't there.
                    SetSql(-204, "42704");
                }
                catch (RpgHalt)
                {
                    // SQL0443: the external program ended in error.
                    SetSql(-443, "38000");
                }
                break;
            }
            case SqlKind.Close:
                if (_cursors.Remove(sql.Cursor!, out var open))
                {
                    open.Dispose();
                    SetSql(0, "00000");
                }
                else SetSql(-501, "24501");
                break;
            case SqlKind.Prepare or SqlKind.ExecuteImmediate:
            {
                var text = sql.Sql ?? Values.ToText(HostCell(sql.Inputs[0], frame).Get());
                var translated = SqlTranslator.Translate(text);
                if (translated.Kind == SqlKind.Unsupported || translated.Inputs.Any(i => i != "?" && i != "*USER"))
                {
                    // As Db2 answers SQL it can't prepare.
                    SetSql(-104, "42601");
                    break;
                }
                if (sql.Kind == SqlKind.Prepare)
                {
                    _prepared[sql.Statement!] = translated;
                    SetSql(0, "00000");
                }
                else RunSql(translated, frame, []);
                break;
            }
            case SqlKind.ExecutePrepared:
                if (_prepared.TryGetValue(sql.Statement!, out var prepared)) RunSql(prepared, frame, sql.Inputs);
                else SetSql(-518, "07003");
                break;
            case SqlKind.RowCount:
                HostCell(sql.Outputs[0].Name, frame).Set((decimal)_rowCount);
                break;
        }
    }

    /// <summary>A host variable: NAME, or STRUCTURE.SUBFIELD.</summary>
    private Cell HostCell(string name, Frame frame)
    {
        var parts = name.ToUpperInvariant().Split('.');
        var cell = Find(parts[0], frame) ?? throw new RpgError(100, $"the host variable {name} isn't defined");
        foreach (var part in parts.Skip(1))
        {
            if (cell.Type.Kind != RpgKind.Ds || cell.Elements is not null) throw new RpgError(100, $"the host variable {name} isn't in a data structure");
            cell = cell.Ds.Field(part) ?? throw new RpgError(100, $"the host variable {name} isn't defined");
        }
        return cell;
    }

    /// <summary>
    /// The identity value the INSERT just made in its table (IDENTITY_VAL_LOCAL), from the sequence of
    /// the table's identity column; null when it has none.
    /// </summary>
    private long? IdentityOf(string sql)
    {
        if (System.Text.RegularExpressions.Regex.Match(sql, @"INSERT\s+INTO\s+((?:""[^""]+""|[\w#$@]+)(?:\.(?:""[^""]+""|[\w#$@]+))?)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase) is not { Success: true } m) return null;
        var table = m.Groups[1].Value;
        try
        {
            using var command = _machine.Store.Connection.CreateCommand();
            command.CommandText = $"""
                SELECT currval(pg_get_serial_sequence(@t, a.attname)) FROM pg_attribute a
                WHERE a.attrelid = @t::regclass AND a.attidentity <> '' AND a.attname <> '{Store.RowNumber}' LIMIT 1
                """;
            command.Parameters.AddWithValue("t", table);
            return command.ExecuteScalar() is long value ? value : null;
        }
        catch (PostgresException)
        {
            return null;
        }
    }

    /// <summary>
    /// The command for a statement, with its host variables bound. A data structure stands
    /// for its subfields, as in VALUES(:structure). A ? marker takes the next of <paramref name="usingNames"/>.
    /// </summary>
    private NpgsqlCommand Command(SqlTranslation sql, Frame frame, IReadOnlyList<string> usingNames)
    {
        var command = _machine.Store.Connection.CreateCommand();
        var text = _machine.Resolve(sql.Sql!, _host.User);
        var markers = 0;
        for (var i = 0; i < sql.Inputs.Count; i++)
        {
            var parameter = $"@h{i}";
            if (sql.Inputs[i] == "*USER")
            {
                command.Parameters.Add(Store.Untyped(parameter[1..], _host.User));
                continue;
            }
            if (sql.Inputs[i] == "*IDENTITY")
            {
                command.Parameters.AddWithValue(parameter[1..], _host.State.LastIdentity);
                continue;
            }
            var name = sql.Inputs[i];
            if (name == "?")
            {
                if (markers >= usingNames.Count) throw new RpgError(100, "the statement has more ? markers than USING has variables");
                name = usingNames[markers++];
            }
            var cell = HostCell(name, frame);
            if (cell.Type.Kind == RpgKind.Ds && cell.Elements is null)
            {
                var data = cell.Ds.Data.ToList();
                var names = data.Select((_, k) => $"{parameter}_{k}").ToList();
                text = System.Text.RegularExpressions.Regex.Replace(text, parameter + @"\b", string.Join(", ", names));
                for (var k = 0; k < names.Count; k++) command.Parameters.Add(Store.Untyped(names[k][1..], Bind(data[k].Get())));
            }
            else
            {
                command.Parameters.Add(Store.Untyped(parameter[1..], Bind(cell.Get())));
            }
        }
        command.CommandText = text;
        return command;
    }

    /// <summary>
    /// A value as the database takes it. Character values lose their trailing blanks, as Db2 doesn't
    /// count them when it compares: so a CHAR(10) host variable still finds its row in a VARCHAR column.
    /// </summary>
    private static object Bind(object value) => value switch
    {
        string s => s.TrimEnd(' '),
        decimal d when d == decimal.Truncate(d) && d is >= long.MinValue and <= long.MaxValue => (long)d,
        decimal d => d,
        long l => l,
        bool b => b ? "1" : "0",
        DateOnly d => d,
        TimeOnly t => t,
        DateTime t => DateTime.SpecifyKind(t, DateTimeKind.Unspecified),
        double f => f,
        DBNull => DBNull.Value,
        _ => throw new RpgError(100, "this host variable's type can't be used in SQL"),
    };

    /// <summary>Moves a row into the INTO variables: a data structure takes a column for each subfield.</summary>
    private void Receive(object[] row, IReadOnlyList<(string Name, string? Indicator)> outputs, Frame frame, Cell? into = null)
    {
        var column = 0;
        foreach (var (name, indicator) in outputs)
        {
            var cell = into ?? HostCell(name, frame);
            var targets = cell.Type.Kind == RpgKind.Ds && cell.Elements is null ? [.. cell.Ds.Data] : new List<Cell> { cell };
            var indicators = indicator is null ? null : HostCell(indicator, frame);
            for (var i = 0; i < targets.Count; i++, column++)
            {
                if (column >= row.Length) return;
                var isNull = row[column] is DBNull;
                // The indicator: -1 for null, 0 otherwise. An array of them goes with a data structure.
                var flag = indicators?.Elements is { } array ? (i < array.Length ? array[i] : null) : i == 0 ? indicators : null;
                flag?.Set(isNull ? -1m : 0m);
                if (isNull)
                {
                    // Db2 would answer -305 with no indicator; the variable is cleared instead.
                    targets[i].Clear();
                    continue;
                }
                targets[i].Set(DbValue(row[column], targets[i].Type), halfAdjust: true);
            }
        }
    }
}
