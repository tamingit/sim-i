// The fake's database, on PostgreSQL. Each library is a schema of it, with its source members, the
// objects made from them (programs and display files), what the last save of a member found, and
// the tables embedded SQL works on. QGPL's schema also holds the system's tables (the users, the
// spooled files and the libraries). A name with no library is looked for in every library, QGPL
// first (the search path). Db2's functions PostgreSQL lacks are in the schema qfake. One
// connection, used under a lock: programs run on their own threads.

using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;

namespace FakeIbmI;

public enum Severity
{
    /// <summary>Something translated or ignored; the code runs.</summary>
    Info,

    /// <summary>It may fail when it runs, such as SQL on a table that doesn't exist yet.</summary>
    Warning,

    /// <summary>The fake can't run this line. Reaching it at run time ends the program with an error.</summary>
    Unsupported,

    /// <summary>The source is wrong: an IBM i's compiler rejects it, so the fake makes nothing from it either.</summary>
    Error,
}

/// <summary>One finding from checking a member: its line (1-based; 0 for the whole member), and what was found.</summary>
public sealed record Diagnostic(int Line, Severity Severity, string Message);

/// <summary>
/// A source member, such as QGPL/QRPGLESRC(HELLO), type RPGLE. <see cref="Path"/> is the IFS path of a
/// stream file compiled as if it were a member (SRCSTMF); it isn't kept with the member.
/// </summary>
public sealed record Member(string Library, string File, string Name, string Type, string Source, string? Path = null)
{
    public override string ToString() => Path ?? $"{Library}/{File}({Name})";
}

/// <summary>A spooled file a program printed: its lines, as text.</summary>
public sealed record SpoolRecord(long Id, string Name, string User, int Pages, int Width, DateTime Created, string Text);

/// <summary>One user profile as the table QFAKE_USER keeps it. Status is *ENABLED or *DISABLED, PasswordExpired *YES or *NO; DefaultLibrary (DFTLIB) is the library the user's names are looked for in first, and new source goes to.</summary>
public sealed record UserRow(string User, string Password, string Status, string PasswordExpired, string Text, string DefaultLibrary = "QGPL");

/// <summary>
/// An object made from a member: type *PGM or *FILE, with the source it was made from. Its attribute is
/// the source type, then keywords: DSPF WAITRCD(2), SQLRPGLE SRCSTMF(/home/me/prog.sqlrpgle).
/// </summary>
public sealed record StoredObject(string Library, string Name, string Type, string Attribute, string Source, long Version)
{
    /// <summary>The source type it was made from: the attribute's first word.</summary>
    public string Kind => Attribute.Split(' ', 2)[0];

    /// <summary>The stream file it was compiled from (SRCSTMF), or null.</summary>
    public string? StreamFile => Regex.Match(Attribute, @"\bSRCSTMF\(([^)]*)\)") is { Success: true } m ? m.Groups[1].Value : null;
}

public sealed partial class Store : IDisposable
{
    /// <summary>The library that holds the system's tables and the SQL a program doesn't qualify.</summary>
    public const string Current = "QGPL";

    /// <summary>The hidden column each table has for its relative record number (RRN), in arrival order.</summary>
    public const string RowNumber = "qfake_rrn";

    private readonly NpgsqlConnection _connection;
    private readonly (string Admin, string Database)? _temporary;
    private readonly SortedSet<string> _others = new(StringComparer.Ordinal);

    private Store(NpgsqlConnection connection, (string, string)? temporary)
    {
        _connection = connection;
        _temporary = temporary;
    }

    /// <summary>Guards every use of <see cref="Connection"/>.</summary>
    internal object Gate { get; } = new();

    internal NpgsqlConnection Connection => _connection;

    [GeneratedRegex("^[A-Z#$@][A-Z0-9#$@_]{0,9}$")]
    private static partial Regex LibraryNamePattern();

    /// <summary>Whether this is a name a library can have: 1 to 10 letters, digits, #, $, @ and _, not starting with a digit.</summary>
    public static bool IsLibraryName(string name) => LibraryNamePattern().IsMatch(name);

    /// <summary>The PostgreSQL server tests and checking use when none is named: FAKE_IBMI_SCRATCH_DATABASE, or a development server on port 54329.</summary>
    public static string ScratchServer =>
        Environment.GetEnvironmentVariable("FAKE_IBMI_SCRATCH_DATABASE") is { Length: > 0 } scratch
            ? scratch
            : "Host=127.0.0.1;Port=54329;Username=fakeibmi;Password=fakeibmi;Database=fakeibmi";

    /// <summary>
    /// Opens the fake's database (made ready when it's new). With <paramref name="temporary"/>, the
    /// connection names a server: a database of its own is made there, and dropped when the store is.
    /// With no connection, that's on the scratch server.
    /// </summary>
    public static Store Open(string? connectionString = null, bool temporary = false)
    {
        // None: a database of its own on the scratch server.
        if (connectionString is null) (connectionString, temporary) = (ScratchServer, true);
        // No Kerberos: the fake signs on with a password, and an image needn't have its library.
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false, Enlist = false, IncludeErrorDetail = true, GssEncryptionMode = GssEncryptionMode.Disable,
        };
        (string, string)? dropping = null;
        if (temporary)
        {
            var template = Template(builder.ConnectionString);
            // The process's id in the name: a database left by a process that's gone is dropped (see Tidy).
            var name = $"fakeibmi_t_{Environment.ProcessId}_{Guid.NewGuid().ToString("N")[..12]}";
            Admin(builder.ConnectionString, $"CREATE DATABASE {name} TEMPLATE {template}");
            dropping = (builder.ConnectionString, name);
            lock (Living) Living[name] = builder.ConnectionString;
            builder.Database = name;
        }
        var connection = new NpgsqlConnection(builder.ConnectionString);
        connection.Open();
        var store = new Store(connection, dropping);
        // A commit needn't wait for the disk: a fake loses at most its last moments in a crash, and
        // building thousands of files (each a CREATE TABLE) is many times faster.
        store.Run("SET synchronous_commit = off");
        if (!temporary) store.Run(Setup);
        foreach (var library in store.Query("SELECT name FROM qgpl.qfake_library ORDER BY name", r => r.GetString(0)))
        {
            if (library != Current && IsLibraryName(library)) store._others.Add(library);
        }
        // QGPL is always there.
        store.Run("INSERT INTO qgpl.qfake_library VALUES ('QGPL') ON CONFLICT DO NOTHING");
        store.SetSearchPath();
        return store;
    }

    /// <summary>Runs a statement on the server's own database (making and dropping databases).</summary>
    private static void Admin(string connectionString, string sql)
    {
        using var admin = new NpgsqlConnection(connectionString);
        admin.Open();
        using var command = new NpgsqlCommand(sql, admin);
        command.ExecuteNonQuery();
    }

    private static readonly Lock TemplateGate = new();

    /// <summary>The temporary databases this process made that are still there, with the server's connection: dropped when it ends.</summary>
    private static readonly Dictionary<string, string> Living = InitLiving();

    private static Dictionary<string, string> InitLiving()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            List<KeyValuePair<string, string>> left;
            lock (Living) left = [.. Living];
            foreach (var (database, server) in left)
            {
                try
                {
                    Admin(server, $"DROP DATABASE IF EXISTS {database} WITH (FORCE)");
                }
                catch (NpgsqlException)
                {
                }
            }
        };
        return [];
    }

    private static readonly HashSet<string> Tidied = [];

    /// <summary>
    /// Drops the temporary databases of processes that are gone (and ones from before they were named
    /// for their process), and templates made for another setup. Once a server a process.
    /// </summary>
    private static void Tidy(NpgsqlConnection admin, string template)
    {
        if (!Tidied.Add(admin.ConnectionString)) return;
        var names = new List<string>();
        using (var list = new NpgsqlCommand("SELECT datname::text FROM pg_database WHERE datname LIKE 'fakeibmi!_t!_%' ESCAPE '!' OR datname LIKE 'fakeibmi!_tpl!_%' ESCAPE '!'", admin))
        using (var reader = list.ExecuteReader())
        {
            while (reader.Read()) names.Add(reader.GetString(0));
        }
        foreach (var name in names)
        {
            var stale = name.StartsWith("fakeibmi_tpl_") ? name != template && !name.Contains("_b")
                : name.Split('_') is [_, _, var pid, _] && int.TryParse(pid, out var id) ? id != Environment.ProcessId && !Running(id)
                : true;
            if (!stale) continue;
            try
            {
                using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)", admin);
                drop.ExecuteNonQuery();
            }
            catch (PostgresException)
            {
            }
        }
    }

    private static bool Running(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>A template database made ready once (named for its setup, so a change to it makes a new one), for temporary ones to copy.</summary>
    private static string Template(string connectionString)
    {
        var name = "fakeibmi_tpl_" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Setup)))[..12].ToLowerInvariant();
        lock (TemplateGate)
        {
            using var admin = new NpgsqlConnection(connectionString);
            admin.Open();
            Tidy(admin, name);
            using (var exists = new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname = @n", admin))
            {
                exists.Parameters.AddWithValue("n", name);
                if ((long)exists.ExecuteScalar()! > 0) return name;
            }
            var building = name + "_b" + Environment.ProcessId;
            using (var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {building}", admin)) drop.ExecuteNonQuery();
            using (var create = new NpgsqlCommand($"CREATE DATABASE {building}", admin)) create.ExecuteNonQuery();
            var builder = new NpgsqlConnectionStringBuilder(connectionString) { Database = building, Pooling = false };
            using (var setup = new NpgsqlConnection(builder.ConnectionString))
            {
                setup.Open();
                using var command = new NpgsqlCommand(Setup, setup);
                command.ExecuteNonQuery();
            }
            try
            {
                using var rename = new NpgsqlCommand($"ALTER DATABASE {building} RENAME TO {name}", admin);
                rename.ExecuteNonQuery();
            }
            catch (PostgresException e) when (e.SqlState == "42P04")
            {
                // Another process made it first.
                using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {building}", admin);
                drop.ExecuteNonQuery();
            }
            return name;
        }
    }

    /// <summary>What a new database needs: QGPL's schema with the system's tables, sysibm's SYSDUMMY1, and the Db2 functions.</summary>
    private static readonly string Setup = $"""
        CREATE SCHEMA IF NOT EXISTS qgpl;
        CREATE SCHEMA IF NOT EXISTS qfake;
        CREATE SCHEMA IF NOT EXISTS sysibm;
        CREATE TABLE IF NOT EXISTS qgpl.qfake_spool (
            id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY, name TEXT NOT NULL, username TEXT NOT NULL, pages INT NOT NULL,
            width INT NOT NULL, created TIMESTAMP NOT NULL, text TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS qgpl.qfake_user (
            username TEXT NOT NULL PRIMARY KEY, password TEXT NOT NULL, status TEXT NOT NULL DEFAULT '*ENABLED',
            pwdexp TEXT NOT NULL DEFAULT '*NO', text TEXT NOT NULL DEFAULT '', dftlib TEXT NOT NULL DEFAULT 'QGPL');
        CREATE TABLE IF NOT EXISTS qgpl.qfake_library (name TEXT NOT NULL PRIMARY KEY);
        CREATE TABLE IF NOT EXISTS qgpl.sysdummy1 (ibmreqd CHAR(1) NOT NULL);
        INSERT INTO qgpl.sysdummy1 SELECT 'Y' WHERE NOT EXISTS (SELECT 1 FROM qgpl.sysdummy1);
        CREATE OR REPLACE VIEW sysibm.sysdummy1 AS SELECT ibmreqd FROM qgpl.sysdummy1;
        {LibraryTables("qgpl")}
        CREATE OR REPLACE FUNCTION qfake.ucase(text) RETURNS text LANGUAGE sql IMMUTABLE AS 'SELECT upper($1)';
        CREATE OR REPLACE FUNCTION qfake.lcase(text) RETURNS text LANGUAGE sql IMMUTABLE AS 'SELECT lower($1)';
        CREATE OR REPLACE FUNCTION qfake.strip(text) RETURNS text LANGUAGE sql IMMUTABLE AS 'SELECT btrim($1, '' '')';
        CREATE OR REPLACE FUNCTION qfake.posstr(text, text) RETURNS int LANGUAGE sql IMMUTABLE AS 'SELECT position($2 in $1)';
        CREATE OR REPLACE FUNCTION qfake.locate(text, text) RETURNS int LANGUAGE sql IMMUTABLE AS 'SELECT position($1 in $2)';
        CREATE OR REPLACE FUNCTION qfake.locate(text, text, int) RETURNS int LANGUAGE sql IMMUTABLE AS
            'SELECT CASE WHEN position($1 in substr($2, $3)) = 0 THEN 0 ELSE position($1 in substr($2, $3)) + $3 - 1 END';
        CREATE OR REPLACE FUNCTION qfake.space(int) RETURNS text LANGUAGE sql IMMUTABLE AS 'SELECT repeat('' '', greatest($1, 0))';
        CREATE OR REPLACE FUNCTION qfake.year(timestamp) RETURNS int LANGUAGE sql IMMUTABLE AS 'SELECT extract(year from $1)::int';
        CREATE OR REPLACE FUNCTION qfake.month(timestamp) RETURNS int LANGUAGE sql IMMUTABLE AS 'SELECT extract(month from $1)::int';
        CREATE OR REPLACE FUNCTION qfake.day(timestamp) RETURNS int LANGUAGE sql IMMUTABLE AS 'SELECT extract(day from $1)::int';
        CREATE OR REPLACE FUNCTION qfake.hour(timestamp) RETURNS int LANGUAGE sql IMMUTABLE AS 'SELECT extract(hour from $1)::int';
        CREATE OR REPLACE FUNCTION qfake.minute(timestamp) RETURNS int LANGUAGE sql IMMUTABLE AS 'SELECT extract(minute from $1)::int';
        CREATE OR REPLACE FUNCTION qfake.second(timestamp) RETURNS int LANGUAGE sql IMMUTABLE AS 'SELECT floor(extract(second from $1))::int';
        CREATE OR REPLACE FUNCTION qfake.dayofweek(timestamp) RETURNS int LANGUAGE sql IMMUTABLE AS 'SELECT extract(dow from $1)::int + 1';
        CREATE OR REPLACE FUNCTION qfake.days(timestamp) RETURNS int LANGUAGE sql IMMUTABLE AS 'SELECT ($1::date - date ''0001-01-01'') + 1';
        CREATE OR REPLACE FUNCTION qfake.curdate() RETURNS date LANGUAGE sql STABLE AS 'SELECT current_date';
        CREATE OR REPLACE FUNCTION qfake.curtime() RETURNS time LANGUAGE sql STABLE AS 'SELECT localtime(0)';
        CREATE OR REPLACE FUNCTION qfake.digits(smallint) RETURNS text LANGUAGE sql IMMUTABLE AS 'SELECT lpad(abs($1)::text, 5, ''0'')';
        CREATE OR REPLACE FUNCTION qfake.digits(int) RETURNS text LANGUAGE sql IMMUTABLE AS 'SELECT lpad(abs($1::bigint)::text, 10, ''0'')';
        CREATE OR REPLACE FUNCTION qfake.digits(bigint) RETURNS text LANGUAGE sql IMMUTABLE AS 'SELECT lpad(abs($1::numeric)::text, 19, ''0'')';
        CREATE OR REPLACE FUNCTION qfake.digits(numeric) RETURNS text LANGUAGE sql IMMUTABLE AS 'SELECT replace(abs($1)::text, ''.'', '''')';
        CREATE OR REPLACE FUNCTION qfake.add_duration(date, numeric, text) RETURNS date LANGUAGE sql IMMUTABLE AS
            'SELECT ($1 + ($2 || '' '' || $3)::interval)::date';
        CREATE OR REPLACE FUNCTION qfake.add_duration(timestamp, numeric, text) RETURNS timestamp LANGUAGE sql IMMUTABLE AS
            'SELECT $1 + ($2 || '' '' || $3)::interval';
        CREATE OR REPLACE FUNCTION qfake.add_duration(time, numeric, text) RETURNS time LANGUAGE sql IMMUTABLE AS
            'SELECT $1 + ($2 || '' '' || $3)::interval';
        CREATE OR REPLACE FUNCTION qfake.row_change() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            NEW := jsonb_populate_record(NEW, jsonb_build_object(TG_ARGV[0], localtimestamp));
            RETURN NEW;
        END $$;
        """;

    /// <summary>A library's own tables: its members, what checking them found, and its objects.</summary>
    private static string LibraryTables(string schema) => $"""
        CREATE TABLE IF NOT EXISTS "{schema}".qfake_member (
            library TEXT NOT NULL, file TEXT NOT NULL, name TEXT NOT NULL, type TEXT NOT NULL, source TEXT NOT NULL,
            PRIMARY KEY (library, file, name));
        CREATE TABLE IF NOT EXISTS "{schema}".qfake_diagnostic (
            library TEXT NOT NULL, file TEXT NOT NULL, name TEXT NOT NULL, seq INT NOT NULL,
            line INT NOT NULL, severity INT NOT NULL, message TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS "{schema}".qfake_object (
            library TEXT NOT NULL, name TEXT NOT NULL, type TEXT NOT NULL, attribute TEXT NOT NULL, source TEXT NOT NULL,
            version BIGINT NOT NULL, PRIMARY KEY (library, name, type));
        """;

    // ---- libraries ----

    /// <summary>
    /// The libraries in the order a name without one is looked for: <paramref name="first"/> (a user's default
    /// library, when there is such a library), then QGPL, then the rest by name.
    /// </summary>
    public IReadOnlyList<string> Libraries(string? first = null)
    {
        lock (Gate)
        {
            List<string> all = [Current, .. _others];
            if (first?.ToUpperInvariant() is { } f && all.Remove(f)) all.Insert(0, f);
            return all;
        }
    }

    public bool HasLibrary(string name)
    {
        name = name.ToUpperInvariant();
        lock (Gate) return name == Current || _others.Contains(name);
    }

    /// <summary>The schema of a library, as SQL names it: QTEMP is the connection's temporary schema.</summary>
    internal string Schema(string library)
    {
        library = library.ToUpperInvariant();
        if (library == "QTEMP") return "pg_temp";
        lock (Gate)
        {
            if (library != Current && !_others.Contains(library)) throw new ArgumentException($"Library {library} not found.");
        }
        return SchemaName(library);
    }

    /// <summary>A library's schema name, quoted: QGPL is "qgpl".</summary>
    public static string SchemaName(string library) => $"\"{library.ToLowerInvariant()}\"";

    /// <summary>
    /// A column of a file made from DDS, quoted: a field name can have # @ $ in it, or be a word
    /// PostgreSQL keeps for itself (DESC, DECIMAL ...). In lowercase, it's the name SQL folds an unquoted one to.
    /// </summary>
    public static string ColumnName(string field) => $"\"{field.ToLowerInvariant()}\"";

    /// <summary>Unqualified names are looked for in every library, QGPL first, then the Db2 functions.</summary>
    private void SetSearchPath() =>
        Run($"SET search_path TO {string.Join(", ", Libraries().Select(SchemaName))}, qfake, public");

    /// <summary>Makes the library (its schema) or opens it. False when it already was there.</summary>
    public bool CreateLibrary(string name)
    {
        name = name.ToUpperInvariant();
        if (!IsLibraryName(name)) throw new ArgumentException($"{name} isn't a library name: 1 to 10 letters, digits, #, $, @ and _.");
        lock (Gate)
        {
            if (HasLibrary(name)) return false;
            Run($"CREATE SCHEMA IF NOT EXISTS {SchemaName(name)};\n{LibraryTables(name.ToLowerInvariant())}");
            _others.Add(name);
            Run("INSERT INTO qgpl.qfake_library VALUES (@n) ON CONFLICT DO NOTHING", ("n", name));
            SetSearchPath();
            return true;
        }
    }

    /// <summary>Takes the library away, with everything in it. False when there's no such library. QGPL can't be deleted.</summary>
    public bool DeleteLibrary(string name)
    {
        name = name.ToUpperInvariant();
        if (name == Current) throw new ArgumentException($"Library {Current} can't be deleted: it holds the system's tables.");
        lock (Gate)
        {
            if (!_others.Remove(name)) return false;
            Run($"DROP SCHEMA IF EXISTS {SchemaName(name)} CASCADE");
            Run("DELETE FROM qgpl.qfake_library WHERE name = @n", ("n", name));
            SetSearchPath();
            return true;
        }
    }

    /// <summary>What a library holds: its source members, its objects (programs and display files) and its SQL tables.</summary>
    public (int Members, int Objects, int Tables) Counts(string library)
    {
        var schema = Schema(library);
        int Count(string sql, params (string, object?)[] p) => (int)Query(sql, r => r.GetInt64(0), p)[0];
        return (Count($"SELECT count(*) FROM {schema}.qfake_member"), Count($"SELECT count(*) FROM {schema}.qfake_object"),
            Count("SELECT count(*) FROM information_schema.tables WHERE table_schema = @s AND table_name NOT LIKE 'qfake!_%' ESCAPE '!' AND table_name <> 'sysdummy1'",
                ("s", schema.Trim('"'))));
    }

    public void Dispose()
    {
        _connection.Dispose();
        if (_temporary is var (admin, database))
        {
            try
            {
                Admin(admin, $"DROP DATABASE IF EXISTS {database} WITH (FORCE)");
                lock (Living) Living.Remove(database);
            }
            catch (NpgsqlException)
            {
            }
        }
    }

    // ---- running SQL ----

    internal void Run(string sql, params (string Name, object? Value)[] parameters)
    {
        lock (Gate)
        {
            using var command = Command(sql, parameters);
            command.ExecuteNonQuery();
        }
    }

    private NpgsqlCommand Command(string sql, (string Name, object? Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        // A source member can hold x'00' (a printer file's control characters); PostgreSQL text can't, so it's kept as a blank.
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value is string text && text.Contains('\0') ? text.Replace('\0', ' ') : value ?? DBNull.Value);
        return command;
    }

    private List<T> Query<T>(string sql, Func<NpgsqlDataReader, T> read, params (string Name, object? Value)[] parameters)
    {
        lock (Gate)
        {
            using var command = Command(sql, parameters);
            using var reader = command.ExecuteReader();
            var rows = new List<T>();
            while (reader.Read()) rows.Add(read(reader));
            return rows;
        }
    }

    /// <summary>A query's rows, each an array of its columns' values (null for NULL).</summary>
    internal List<object?[]> Select(string sql, params (string Name, object? Value)[] parameters) =>
        Query(sql, r =>
        {
            var row = new object?[r.FieldCount];
            for (var i = 0; i < row.Length; i++) row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
            return row;
        }, parameters);

    /// <summary>Loads rows with COPY: <paramref name="rows"/> is its text format, a line each.</summary>
    internal void Copy(string copy, string rows)
    {
        lock (Gate)
        {
            using var writer = _connection.BeginTextImport(copy);
            writer.Write(rows);
        }
    }

    /// <summary>A parameter of no type: PostgreSQL works out its type from where it's used, as Db2 does for a host variable.</summary>
    internal static NpgsqlParameter Untyped(string name, object? value) =>
        value is string s ? new NpgsqlParameter(name, NpgsqlDbType.Unknown) { Value = s } : new NpgsqlParameter(name, value ?? DBNull.Value);

    // ---- members ----

    /// <summary>Saves the member, replacing one of the same name, with what checking it found.</summary>
    public void SaveMember(Member member, IReadOnlyList<Diagnostic> diagnostics)
    {
        lock (Gate)
        {
            var schema = Schema(member.Library);
            using var transaction = _connection.BeginTransaction();
            Run($"""
                INSERT INTO {schema}.qfake_member VALUES (@l, @f, @n, @t, @s)
                ON CONFLICT (library, file, name) DO UPDATE SET type = excluded.type, source = excluded.source
                """,
                ("l", member.Library), ("f", member.File), ("n", member.Name), ("t", member.Type), ("s", member.Source));
            Run($"DELETE FROM {schema}.qfake_diagnostic WHERE library = @l AND file = @f AND name = @n",
                ("l", member.Library), ("f", member.File), ("n", member.Name));
            for (var i = 0; i < diagnostics.Count; i++)
            {
                Run($"INSERT INTO {schema}.qfake_diagnostic VALUES (@l, @f, @n, @q, @line, @sev, @m)",
                    ("l", member.Library), ("f", member.File), ("n", member.Name), ("q", i),
                    ("line", diagnostics[i].Line), ("sev", (int)diagnostics[i].Severity), ("m", diagnostics[i].Message));
            }
            transaction.Commit();
        }
    }

    private static Member ReadMember(NpgsqlDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4));

    /// <summary>Every member, by library, file and name.</summary>
    public IReadOnlyList<Member> Members() =>
        [.. Libraries()
            .SelectMany(l => Query($"SELECT library, file, name, type, source FROM {Schema(l)}.qfake_member", ReadMember))
            .OrderBy(m => m.Library, StringComparer.Ordinal).ThenBy(m => m.File, StringComparer.Ordinal).ThenBy(m => m.Name, StringComparer.Ordinal)];

    /// <summary>The libraries a lookup goes through: the one named, or (none named: *LIBL) all of them, in order.</summary>
    private IEnumerable<string> LibrariesFor(string? library, string? first = null) =>
        library is null ? Libraries(first) : HasLibrary(library) ? [library.ToUpperInvariant()] : [];

    /// <summary>The member of this name. A null library or file matches any, as *LIBL would.</summary>
    public Member? FindMember(string? library, string? file, string name, string? first = null) =>
        LibrariesFor(library, first).Select(l =>
            Query($"SELECT library, file, name, type, source FROM {Schema(l)}.qfake_member WHERE name = @n AND (@f::text IS NULL OR file = @f) ORDER BY file",
                ReadMember, ("n", name.ToUpperInvariant()), ("f", file?.ToUpperInvariant())).FirstOrDefault())
            .FirstOrDefault(m => m is not null);

    /// <summary>The member of this name and one of these source types, in any library, looked for in <paramref name="first"/> first.</summary>
    public Member? FindMemberOfType(string name, IReadOnlyCollection<string> types, string? first = null) =>
        LibrariesFor(null, first).Select(l =>
            Query($"SELECT library, file, name, type, source FROM {Schema(l)}.qfake_member WHERE name = @n ORDER BY file",
                ReadMember, ("n", name.ToUpperInvariant())).FirstOrDefault(m => types.Contains(m.Type)))
            .FirstOrDefault(m => m is not null);

    public void DeleteMember(Member member)
    {
        foreach (var table in new[] { "qfake_member", "qfake_diagnostic" })
        {
            Run($"DELETE FROM {Schema(member.Library)}.{table} WHERE library = @l AND file = @f AND name = @n",
                ("l", member.Library), ("f", member.File), ("n", member.Name));
        }
    }

    /// <summary>What the last save of the member found.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics(Member member) =>
        Query($"SELECT line, severity, message FROM {Schema(member.Library)}.qfake_diagnostic WHERE library = @l AND file = @f AND name = @n ORDER BY seq",
            r => new Diagnostic(r.GetInt32(0), (Severity)r.GetInt32(1), r.GetString(2)),
            ("l", member.Library), ("f", member.File), ("n", member.Name));

    // ---- objects ----

    public StoredObject SaveObject(string library, string name, string type, string attribute, string source)
    {
        var version = DateTime.UtcNow.Ticks;
        Run($"""
            INSERT INTO {Schema(library)}.qfake_object VALUES (@l, @n, @t, @a, @s, @v)
            ON CONFLICT (library, name, type) DO UPDATE SET attribute = excluded.attribute, source = excluded.source, version = excluded.version
            """,
            ("l", library.ToUpperInvariant()), ("n", name), ("t", type), ("a", attribute), ("s", source), ("v", version));
        return new StoredObject(library.ToUpperInvariant(), name, type, attribute, source, version);
    }

    private static StoredObject ReadObject(NpgsqlDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5));

    /// <summary>The object of this name and type (*PGM or *FILE). A null library matches any.</summary>
    public StoredObject? FindObject(string? library, string name, string type, string? first = null) =>
        LibrariesFor(library, first).Select(l =>
            Query($"SELECT library, name, type, attribute, source, version FROM {Schema(l)}.qfake_object WHERE name = @n AND type = @t",
                ReadObject, ("n", name.ToUpperInvariant()), ("t", type)).FirstOrDefault())
            .FirstOrDefault(o => o is not null);

    /// <summary>Every object of this type, in every library, in the order names are looked for.</summary>
    public IReadOnlyList<StoredObject> Objects(string type) =>
        [.. Libraries().SelectMany(l => Query($"SELECT library, name, type, attribute, source, version FROM {Schema(l)}.qfake_object WHERE type = @t ORDER BY name",
            ReadObject, ("t", type)))];

    public bool DeleteObject(StoredObject o)
    {
        lock (Gate)
        {
            using var command = Command($"DELETE FROM {Schema(o.Library)}.qfake_object WHERE library = @l AND name = @n AND type = @t",
                [("l", o.Library), ("n", o.Name), ("t", o.Type)]);
            return command.ExecuteNonQuery() > 0;
        }
    }

    // ---- users ----

    private static UserRow ReadUser(NpgsqlDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5));

    /// <summary>Every user profile, by name.</summary>
    public IReadOnlyList<UserRow> UserRows() =>
        Query("SELECT username, password, status, pwdexp, text, dftlib FROM qgpl.qfake_user ORDER BY username", ReadUser);

    public UserRow? FindUser(string name) =>
        Query("SELECT username, password, status, pwdexp, text, dftlib FROM qgpl.qfake_user WHERE username = @n", ReadUser, ("n", name)).FirstOrDefault();

    /// <summary>Adds the user, or replaces the one of that name.</summary>
    public void SaveUser(UserRow user) =>
        Run("""
            INSERT INTO qgpl.qfake_user (username, password, status, pwdexp, text, dftlib) VALUES (@u, @p, @s, @e, @t, @d)
            ON CONFLICT (username) DO UPDATE SET password = excluded.password, status = excluded.status, pwdexp = excluded.pwdexp,
                text = excluded.text, dftlib = excluded.dftlib
            """,
            ("u", user.User), ("p", user.Password), ("s", user.Status), ("e", user.PasswordExpired), ("t", user.Text), ("d", user.DefaultLibrary));

    /// <summary>Keeps a user's password and flags, as signing on changes them, leaving its text.</summary>
    public void UpdateUser(string name, string password, bool disabled, bool passwordExpired) =>
        Run("UPDATE qgpl.qfake_user SET password = @p, status = @s, pwdexp = @e WHERE username = @u",
            ("u", name), ("p", password), ("s", disabled ? "*DISABLED" : "*ENABLED"), ("e", passwordExpired ? "*YES" : "*NO"));

    // ---- spooled files ----

    /// <summary>
    /// Keeps the printed lines as a new spooled file, 60 lines to a page, or with <paramref name="pages"/>, as
    /// many pages as there are form feed lines ("\f") between them. Returns its number.
    /// </summary>
    public long AddSpool(string name, string user, int width, IReadOnlyList<string> lines, int? pages = null) =>
        Query("INSERT INTO qgpl.qfake_spool (name, username, pages, width, created, text) VALUES (@n, @u, @p, @w, @c, @t) RETURNING id",
            r => r.GetInt64(0),
            ("n", name), ("u", user), ("p", pages ?? Math.Max(1, (lines.Count + 59) / 60)), ("w", width),
            ("c", DateTime.Now), ("t", string.Join('\n', lines)))[0];

    /// <summary>Every spooled file, oldest first.</summary>
    public IReadOnlyList<SpoolRecord> Spools() =>
        Query("SELECT id, name, username, pages, width, created, text FROM qgpl.qfake_spool ORDER BY id",
            r => new SpoolRecord(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4), r.GetDateTime(5), r.GetString(6)));

    // ---- tables ----

    /// <summary>The schemas a table is looked for in: the library's, or (none) every library's and then the temporary one.</summary>
    private List<string> SchemasFor(string? library, string? first = null)
    {
        var qtemp = library is not null && library.Equals("QTEMP", StringComparison.OrdinalIgnoreCase);
        var schemas = qtemp ? [] : LibrariesFor(library, first).Select(l => Schema(l).Trim('"')).ToList();
        if ((library is null || qtemp) && TemporarySchema() is { } own) schemas.Add(own);
        return schemas;
    }

    /// <summary>This connection's temporary schema (QTEMP), or null when it has none yet.</summary>
    private string? TemporarySchema() =>
        Query("SELECT nspname::text FROM pg_namespace WHERE oid = pg_my_temp_schema()", r => r.GetString(0)).FirstOrDefault();

    /// <summary>The library of a schema: pg_temp_n is QTEMP.</summary>
    private static string LibraryOf(string schema) => schema.StartsWith("pg_temp") ? "QTEMP" : schema.ToUpperInvariant();

    /// <summary>
    /// A table's columns and declared types (CHAR(10), DECIMAL(7, 2), INTEGER ...), in order, without the
    /// relative record number; empty when there's no such table. A null library looks in every library, in order.
    /// </summary>
    /// <summary>A table's columns by the names RPG and DDS know them: a column's system name (FOR COLUMN) when it has one.</summary>
    public IReadOnlyList<(string Name, string Type)> Columns(string table, string? library = null) =>
        [.. SqlColumns(table, library).Select(c => (c.Name, c.Type))];

    /// <summary>The comment that keeps a table's record format name (Db2's RCDFMT).</summary>
    public const string RecordFormatNote = "QFAKE RCDFMT ";

    /// <summary>A table's record format name (RCDFMT), or null when it was made without one.</summary>
    public string? RecordFormat(string table, string library)
    {
        var note = Query("""
            SELECT obj_description(c.oid, 'pg_class') FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @s AND lower(c.relname) = lower(@t)
            """, r => r.IsDBNull(0) ? null : r.GetString(0), ("s", SchemasFor(library).FirstOrDefault() ?? library.ToLowerInvariant()), ("t", table)).FirstOrDefault();
        return note is not null && note.StartsWith(RecordFormatNote, StringComparison.Ordinal) ? note[RecordFormatNote.Length..] : null;
    }

    /// <summary>The comment that keeps a column's system name (Db2's FOR COLUMN), which PostgreSQL has no place for.</summary>
    public const string SystemNameNote = "QFAKE SYSTEM NAME ";

    /// <summary>A table's columns: the name RPG knows (its system name, or its own), its type, and the column itself.</summary>
    public IReadOnlyList<(string Name, string Type, string Column)> SqlColumns(string table, string? library = null)
    {
        foreach (var schema in SchemasFor(library))
        {
            var found = Query("""
                SELECT c.column_name::text, c.data_type::text, c.character_maximum_length::int, c.numeric_precision::int, c.numeric_scale::int,
                    col_description(format('%I.%I', c.table_schema, c.table_name)::regclass, a.attnum)
                FROM information_schema.columns c
                JOIN pg_attribute a ON a.attrelid = format('%I.%I', c.table_schema, c.table_name)::regclass AND a.attname = c.column_name
                WHERE c.table_schema = @s AND lower(c.table_name) = lower(@t) AND c.column_name <> @r
                ORDER BY c.ordinal_position
                """,
                r =>
                {
                    var column = r.GetString(0);
                    var note = r.IsDBNull(5) ? null : r.GetString(5);
                    var name = note is not null && note.StartsWith(SystemNameNote, StringComparison.Ordinal) ? note[SystemNameNote.Length..] : column.ToUpperInvariant();
                    return (name, SqlType(r.GetString(1), r.IsDBNull(2) ? null : r.GetInt32(2), r.IsDBNull(3) ? null : r.GetInt32(3), r.IsDBNull(4) ? null : r.GetInt32(4)), column);
                },
                ("s", schema), ("t", table), ("r", RowNumber));
            if (found.Count > 0) return found;
        }
        return [];
    }

    /// <summary>A column's type as Db2 (and the fake's RPG) would name it.</summary>
    private static string SqlType(string type, int? length, int? precision, int? scale) => type switch
    {
        "character" => $"CHAR({length ?? 1})",
        "character varying" => length is { } l ? $"VARCHAR({l})" : "VARCHAR(32000)",
        "text" => "VARCHAR(32000)",
        "numeric" => precision is { } p ? $"DECIMAL({p}, {scale ?? 0})" : "DECIMAL(31, 9)",
        "integer" => "INTEGER",
        "smallint" => "SMALLINT",
        "bigint" => "BIGINT",
        "double precision" or "real" => "DOUBLE",
        "date" => "DATE",
        "time without time zone" or "time with time zone" => "TIME",
        "timestamp without time zone" or "timestamp with time zone" => "TIMESTAMP",
        "boolean" => "CHAR(1)",
        _ => "VARCHAR(32000)",
    };

    /// <summary>The library that has a table (or view) of this name, looked for in <paramref name="first"/> first; null when none has.</summary>
    public string? TableLibrary(string table, string? library = null, string? first = null)
    {
        foreach (var schema in SchemasFor(library, first))
        {
            var found = Query("SELECT count(*) FROM information_schema.tables WHERE table_schema = @s AND lower(table_name) = lower(@t)",
                r => r.GetInt64(0), ("s", schema), ("t", table))[0];
            if (found > 0) return LibraryOf(schema);
        }
        return null;
    }

    /// <summary>A table's name as SQL on the store says it: its library's schema and the table, as it was created.</summary>
    public string SqlName(string library, string table) => $"{Schema(library)}.\"{ActualName(library, table)}\"";

    /// <summary>The table's name as it was created (its case), or the name lowered when there's none.</summary>
    private string ActualName(string library, string table)
    {
        var schema = library.Equals("QTEMP", StringComparison.OrdinalIgnoreCase) ? TemporarySchema() ?? "pg_temp" : Schema(library).Trim('"');
        return Query("SELECT table_name::text FROM information_schema.tables WHERE table_schema = @s AND lower(table_name) = lower(@t)",
            r => r.GetString(0), ("s", schema), ("t", table)).FirstOrDefault() ?? table.ToLowerInvariant();
    }

    /// <summary>Whether a table has the hidden relative record number column.</summary>
    public bool HasRowNumber(string library, string table) =>
        Query("SELECT count(*) FROM information_schema.columns WHERE table_schema = @s AND table_name = @t AND column_name = @r",
            r => r.GetInt64(0), ("s", Schema(library).Trim('"')), ("t", ActualName(library, table)), ("r", RowNumber))[0] > 0;

    /// <summary>A table's primary key columns, in order; empty when it has none.</summary>
    public IReadOnlyList<string> PrimaryKey(string library, string table) =>
        Query("""
            SELECT k.column_name::text FROM information_schema.table_constraints c
            JOIN information_schema.key_column_usage k ON k.constraint_name = c.constraint_name AND k.table_schema = c.table_schema AND k.table_name = c.table_name
            WHERE c.constraint_type = 'PRIMARY KEY' AND c.table_schema = @s AND c.table_name = @t ORDER BY k.ordinal_position
            """, r => r.GetString(0).ToUpperInvariant(), ("s", Schema(library).Trim('"')), ("t", ActualName(library, table)));

    /// <summary>An index of this name: its table, its columns in order, and whether it's unique. Null when there's none.</summary>
    public (string Table, IReadOnlyList<string> Columns, bool Unique)? Index(string library, string name)
    {
        var rows = Query("""
            SELECT t.relname::text, a.attname::text, ix.indisunique
            FROM pg_index ix
            JOIN pg_class i ON i.oid = ix.indexrelid
            JOIN pg_class t ON t.oid = ix.indrelid
            JOIN pg_namespace n ON n.oid = i.relnamespace
            CROSS JOIN LATERAL unnest(ix.indkey) WITH ORDINALITY AS k(attnum, ord)
            JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum
            WHERE n.nspname = @s AND lower(i.relname) = lower(@i)
            ORDER BY k.ord
            """, r => (r.GetString(0), r.GetString(1).ToUpperInvariant(), r.GetBoolean(2)), ("s", Schema(library).Trim('"')), ("i", name));
        if (rows.Count == 0) return null;
        return (rows[0].Item1.ToUpperInvariant(), [.. rows.Select(r => r.Item2)], rows[0].Item3);
    }

    /// <summary>The library an index of this name is in, looked for in <paramref name="first"/> first.</summary>
    public string? IndexLibrary(string name, string? library = null, string? first = null) =>
        LibrariesFor(library, first).FirstOrDefault(l => Index(l, name) is not null);

    /// <summary>
    /// Why the database won't take this statement, or null when it will. Each statement of the text is
    /// planned (EXPLAIN), or for definitions run and rolled back. Host variables are its @ parameters.
    /// </summary>
    public string? PrepareProblem(string sql)
    {
        lock (Gate)
        {
            // A table created with no library is checked as if in QGPL.
            sql = sql.Replace(Machine.DefaultSchemaMarker, SchemaName(Current), StringComparison.Ordinal);
            using var transaction = _connection.BeginTransaction();
            try
            {
                foreach (var statement in sql.Split(";\n", StringSplitOptions.RemoveEmptyEntries))
                {
                    using var command = _connection.CreateCommand();
                    var verb = statement.TrimStart().Split((char[]?)null, 2)[0].ToUpperInvariant();
                    command.CommandText = verb is "SELECT" or "WITH" or "VALUES" or "INSERT" or "UPDATE" or "DELETE" ? "EXPLAIN " + statement : statement;
                    foreach (Match m in Regex.Matches(statement, "@h[0-9_]+"))
                    {
                        if (!command.Parameters.Contains(m.Value[1..])) command.Parameters.Add(new NpgsqlParameter(m.Value[1..], NpgsqlDbType.Unknown) { Value = DBNull.Value });
                    }
                    command.ExecuteNonQuery();
                }
                return null;
            }
            catch (PostgresException e)
            {
                return Clean(e);
            }
            finally
            {
                transaction.Rollback();
            }
        }
    }

    /// <summary>A PostgreSQL error as the fake reports it: a table or column that isn't there said plainly.</summary>
    internal static string Clean(PostgresException e) => e.SqlState switch
    {
        "42P01" => $"no such table: {Regex.Match(e.MessageText, "\"([^\"]+)\"").Groups[1].Value}",
        "42703" => $"no such column: {Regex.Match(e.MessageText, "\"([^\"]+)\"").Groups[1].Value}",
        "42P07" or "42710" or "42723" => $"{e.MessageText} (already exists)",
        _ => e.MessageText,
    };

    /// <summary>Any database error's message, as <see cref="Clean(PostgresException)"/> says it.</summary>
    internal static string Clean(Exception e) => e is PostgresException p ? Clean(p) : e.Message;
}
