// A session with a real IBM i, for receiving its objects: what its commands and catalog say about
// them (DSPFFD, DSPFD and DSPOBJD to outfiles in the session's QTEMP, and SQL), and their records.
// Nothing is changed on the remote system outside that QTEMP.

using System.Text;

namespace SimI.Remote;

internal sealed class RemoteSystem : IDisposable
{
    private readonly IbmFtp _ftp;
    private bool _scriptFile;
    private readonly Dictionary<string, RemoteLayout> _layouts = new(StringComparer.Ordinal);

    private RemoteSystem(IbmFtp ftp, string host)
    {
        _ftp = ftp;
        Host = host;
    }

    public string Host { get; }

    public bool Encrypted => _ftp.Encrypted;

    public static RemoteSystem Connect(string host, int port, string user, string password, RemoteSecurity security) =>
        new(IbmFtp.Connect(host, port, user, password, security), host);

    public void Dispose() => _ftp.Dispose();

    /// <summary>A name as SQL on the remote system takes any name: quoted.</summary>
    public static string Quoted(string name) => $"\"{name.Replace("\"", "\"\"")}\"";

    /// <summary>A name as a CL command takes it: quoted when it has a character an ordinary name hasn't (a period, say).</summary>
    public static string ClName(string name) => name.All(c => char.IsAsciiLetterOrDigit(c) || c is '#' or '$' or '@' or '_' or '*') ? name : $"\"{name}\"";

    public void Command(string command) => _ftp.Command(command);

    public string? TryCommand(string command) => _ftp.TryCommand(command);

    /// <summary>
    /// Runs an SQL statement in the session's job: with RUNSQL, or when it's too long for a command,
    /// from a source member in QTEMP with RUNSQLSTM. A failure's reason is taken from the job log.
    /// </summary>
    public void Sql(string statement)
    {
        var command = $"RUNSQL SQL('{statement.Replace("'", "''")}') COMMIT(*NONE) NAMING(*SQL)";
        string? problem;
        if (Encoding.UTF8.GetByteCount(command) < IbmFtp.MaxLine - 100)
        {
            problem = _ftp.TryCommand(command);
        }
        else
        {
            if (!_scriptFile)
            {
                _ftp.Command("CRTSRCPF FILE(QTEMP/QFKSQL) RCDLEN(92) MBR(S)");
                _scriptFile = true;
            }
            _ftp.StoreText(IbmFtp.Path("QTEMP", "QFKSQL", "S"), Wrapped(statement + ";", 78));
            // ERRLVL(20): a table made in QTEMP isn't journaled, which is a warning of severity 20.
            problem = _ftp.TryCommand("RUNSQLSTM SRCFILE(QTEMP/QFKSQL) SRCMBR(S) COMMIT(*NONE) NAMING(*SQL) ERRLVL(20) OUTPUT(*NONE)");
        }
        if (problem is null) return;
        throw new RemoteException(SqlProblem() ?? problem);
    }

    /// <summary>A statement as source lines: broken at blanks outside quoted text, so no word or string is split.</summary>
    internal static List<string> Wrapped(string statement, int width)
    {
        var lines = new List<string>();
        var line = new StringBuilder();
        foreach (var word in Words(statement))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                lines.Add(line.ToString());
                line.Clear();
            }
            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }
        if (line.Length > 0) lines.Add(line.ToString());
        return lines;

        static IEnumerable<string> Words(string text)
        {
            var word = new StringBuilder();
            char? quote = null;
            foreach (var c in text)
            {
                if (quote is null && char.IsWhiteSpace(c))
                {
                    if (word.Length > 0) yield return word.ToString();
                    word.Clear();
                    continue;
                }
                if (c is '\'' or '"') quote = quote is null ? c : quote == c ? null : quote;
                word.Append(c);
            }
            if (word.Length > 0) yield return word.ToString();
        }
    }

    /// <summary>The job log's last SQL errors, which the FTP server's reply leaves out; null when they can't be had.</summary>
    private string? SqlProblem()
    {
        try
        {
            _ftp.TryCommand("DLTF FILE(QTEMP/QFKJL)");
            var problem = _ftp.TryCommand("RUNSQL SQL('CREATE TABLE QTEMP.QFKJL AS (SELECT MESSAGE_ID, CAST(SUBSTR(MESSAGE_TEXT, 1, 300) AS VARCHAR(300) CCSID 37) AS MESSAGE_TEXT "
                + "FROM TABLE(QSYS2.JOBLOG_INFO(''*'')) X WHERE MESSAGE_TYPE IN (''DIAGNOSTIC'', ''ESCAPE'') AND MESSAGE_ID LIKE ''SQ%'' AND MESSAGE_ID <> ''SQL9010'' "
                + "ORDER BY ORDINAL_POSITION DESC FETCH FIRST 2 ROWS ONLY) WITH DATA') COMMIT(*NONE) NAMING(*SQL)");
            if (problem is not null) return null;
            var rows = Rows("QTEMP", "QFKJL", "QFKJL");
            return rows.Count == 0 ? null : string.Join(" ", rows.Select(r => $"{r["MESSAGE_ID"]} {r["MESSAGE_TEXT"].Trim()}"));
        }
        catch (RemoteException)
        {
            return null;
        }
    }

    /// <summary>A query's rows, each column's value by its name, as text. A null value comes as its type's default: use COALESCE where it matters.</summary>
    public List<Dictionary<string, string>> Query(string select)
    {
        _ftp.TryCommand("DLTF FILE(QTEMP/QFKT)");
        Sql($"CREATE TABLE QTEMP.QFKT AS ({select}) WITH DATA");
        return Rows("QTEMP", "QFKT", "QFKT");
    }

    // DSPFFD's outfile (QADSPFFD), by which every other file's records are read. IBM adds fields at its
    // end only, so these places hold from release to release; its record length is asked of the system.
    private int _fieldFileLength;

    private static readonly RemoteField[] FieldFile =
    [
        new("WHFILE", 'A', 0, 10), new("WHFTYP", 'A', 27, 1), new("WHNAME", 'A', 46, 10), new("WHRLEN", 'S', 124, 5),
        new("WHFLDE", 'A', 139, 10), new("WHFOBO", 'S', 149, 5), new("WHFLDB", 'S', 159, 5), new("WHFLDD", 'S', 164, 2),
        new("WHFLDP", 'S', 166, 2), new("WHFTXT", 'A', 168, 50), new("WHFLDT", 'A', 321, 1), new("WHALIS", 'A', 370, 30),
        new("WHSQLT", 'A', 486, 1), new("WHCSID", 'P', 491, 3), new("WHFMT", 'A', 494, 4), new("WHVARL", 'A', 499, 1),
        new("WHNULL", 'A', 503, 1), new("WHFLDG", 'S', 586, 5), new("WHIDC", 'A', 774, 1), new("WHALI2", 'A', 781, 258),
        new("WHFLDI", 'A', 129, 10), new("WHMAP", 'A', 465, 1), new("WHMAPS", 'S', 466, 5), new("WHMAPL", 'S', 471, 5),
    ];

    /// <summary>The record length of DSPFFD's outfile on this system: what DSPFFD says of the outfile itself.</summary>
    private int FieldFileLength()
    {
        if (_fieldFileLength > 0) return _fieldFileLength;
        _ftp.Command("DSPFFD FILE(QTEMP/QFKFFD) OUTPUT(*OUTFILE) OUTFILE(QTEMP/QFKFF2) OUTMBR(*FIRST *REPLACE)");
        var first = Download("QTEMP", "QFKFF2", "QFKFF2");
        long bad = 0;
        var length = first.Length >= 129 ? int.Parse(RemoteRecords.Value(first, FieldFile[3], ref bad)) : 0;
        if (length < FieldFile.Max(f => f.Offset + f.Bytes) || first.Length % length != 0)
            throw new RemoteException($"{Host} describes fields in a way Sim i doesn't know (DSPFFD's outfile has records of {length} bytes).");
        _ftp.TryCommand("DLTF FILE(QTEMP/QFKFF2)");
        return _fieldFileLength = length;
    }

    /// <summary>
    /// The fields of files, as DSPFFD describes them, by file: <paramref name="file"/> is a name, a generic
    /// name (ABC*) or *ALL. Each file's kind is P, L or D (a device file); its SQL type T, V, I or blank.
    /// </summary>
    public Dictionary<string, RemoteLayout> Describe(string library, string file)
    {
        _ftp.Command($"DSPFFD FILE({ClName(library)}/{ClName(file)}) OUTPUT(*OUTFILE) OUTFILE(QTEMP/QFKFFD) OUTMBR(*FIRST *REPLACE)");
        var layouts = new Dictionary<string, RemoteLayout>(StringComparer.Ordinal);
        long bad = 0;
        Download("QTEMP", "QFKFFD", "QFKFFD", FieldFileLength(), record =>
        {
            var values = new string[FieldFile.Length];
            for (var i = 0; i < values.Length; i++) values[i] = RemoteRecords.Value(record, FieldFile[i], ref bad);
            string Text(int i) => values[i];
            int Number(int i) => int.Parse(values[i]);
            var name = Text(0);
            if (!layouts.TryGetValue(name, out var layout))
                layouts[name] = layout = new RemoteLayout(name, Text(1).FirstOrDefault(' '), Text(12).FirstOrDefault(' ') is var sql && sql is 'T' or 'V' or 'I' ? sql : ' ');
            var type = Text(10).FirstOrDefault(' ');
            // A device file's indicators and constants have no type.
            if (type == ' ') return;
            var format = Text(2);
            if (!layout.RecordLengths.ContainsKey(format)) layout.RecordLengths[format] = Number(3);
            var alias = Text(19) is { Length: > 0 } longAlias ? longAlias : Text(11);
            var field = Text(4);
            layout.Fields.Add(new RemoteField(field, type, Number(5) - 1, Number(6), Number(7), Number(8), Text(15) == "Y", Text(16) == "Y", Number(13), Text(14), Number(17))
            {
                Alias = alias == field ? "" : alias, Text = Text(9), RecordFormat = format, Identity = Text(18) == "Y",
                Internal = Text(20) is { Length: > 0 } inner ? inner : field, Substring = Text(21) == "Y" ? (Number(22), Number(23)) : null,
            });
        });
        return layouts;
    }

    /// <summary>The records of a file in the session's QTEMP (an outfile, or a query's table), each field's value by its name.</summary>
    /// <remarks><paramref name="kind"/> names what the file is (DSPFD's *MBRLIST outfile, say), so its layout is asked for once.</remarks>
    public List<Dictionary<string, string>> Rows(string library, string file, string member, string? kind = null)
    {
        if (kind is null || !_layouts.TryGetValue(kind, out var layout))
        {
            if (!Describe(library, file).TryGetValue(file, out layout)) return [];
            if (kind is not null) _layouts[kind] = layout;
        }
        var rows = new List<Dictionary<string, string>>();
        long bad = 0;
        Download(library, file, member, layout.RecordLength, record =>
        {
            var row = new Dictionary<string, string>(layout.Fields.Count, StringComparer.Ordinal);
            foreach (var field in layout.Fields)
            {
                // By its field name and, when it has one, its longer SQL name.
                var value = RemoteRecords.Value(record, field, ref bad);
                row[field.Name] = value;
                if (field.Alias.Length > 0) row[field.Alias] = value;
            }
            rows.Add(row);
        });
        return rows;
    }

    /// <summary>
    /// Receives a member's records, each <paramref name="recordLength"/> bytes, giving each to
    /// <paramref name="each"/>. Returns how many records came.
    /// </summary>
    public long Download(string library, string file, string member, int recordLength, RecordReader each, CancellationToken cancelled = default)
    {
        long count = 0;
        _ftp.Retrieve(IbmFtp.Path(library, file, member), stream =>
        {
            var buffer = new byte[Math.Max(recordLength * Math.Max(1, 262144 / recordLength), recordLength)];
            var filled = 0;
            while (true)
            {
                cancelled.ThrowIfCancellationRequested();
                var read = stream.Read(buffer, filled, buffer.Length - filled);
                if (read <= 0) break;
                filled += read;
                var whole = filled / recordLength * recordLength;
                for (var at = 0; at < whole; at += recordLength)
                {
                    each(buffer.AsSpan(at, recordLength));
                    count++;
                }
                Buffer.BlockCopy(buffer, whole, buffer, 0, filled - whole);
                filled -= whole;
            }
            if (filled != 0) throw new RemoteException($"{library}/{file}({member}) ended in a record of {filled} bytes, not {recordLength}.");
        });
        return count;
    }

    /// <summary>A member's bytes, whole.</summary>
    public byte[] Download(string library, string file, string member)
    {
        using var all = new MemoryStream();
        _ftp.Retrieve(IbmFtp.Path(library, file, member), stream => stream.CopyTo(all));
        return all.ToArray();
    }
}

internal delegate void RecordReader(ReadOnlySpan<byte> record);

/// <summary>A file's fields on the remote system, in the order of its record formats and their buffers.</summary>
internal sealed class RemoteLayout(string name, char kind, char sqlType)
{
    public string Name => name;

    /// <summary>P (physical), L (logical) or D (device).</summary>
    public char Kind => kind;

    /// <summary>T (an SQL table), V (a view), I (an index) or blank (made from DDS).</summary>
    public char SqlType => sqlType;

    public List<RemoteField> Fields { get; } = [];
    public Dictionary<string, int> RecordLengths { get; } = new(StringComparer.Ordinal);

    /// <summary>The first record format's name.</summary>
    public string Format => Fields.Count > 0 ? Fields[0].RecordFormat : name;

    public int RecordLength => RecordLengths.Count > 0 ? RecordLengths.Values.First() : 0;

    /// <summary>The record formats' names, in order.</summary>
    public List<string> Formats => [.. Fields.Select(f => f.RecordFormat).Distinct()];
}
