// SAVRSTLIB and SAVRSTOBJ: receiving a library, or some of its objects, from a real IBM i.
//
// On an IBM i these commands save objects and restore them on the system RMTLOCNAME names, over
// ObjectConnect. Sim i can't read what a save holds, so here they work the other way round and
// by other means: run on Sim i, they fetch the library FROM the system RMTLOCNAME names, over
// its FTP server, signed on as RMTUSER. What comes across is what Sim i can keep:
//
//   physical files and SQL tables   their fields, key and records (deleted records leave their
//                                   relative record numbers free, null values stay null);
//                                   DATA(*NO) leaves the records behind
//   logical files, indexes, views   made again over the files received
//   source physical files           every member, with its source type
//   data areas and message files    their values and message descriptions
//
// Programs, display files and other compiled objects aren't received: they're made here by
// compiling their source. Nothing is changed on the remote system outside the session's QTEMP.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SimI.Dds;
using SimI.Remote;
using SimI.Rpg;
using Npgsql;

namespace SimI;

public sealed partial class Machine
{
    /// <summary>The user SAVRSTLIB signs on to the remote system as when the command names none (SIM_I_REMOTE_USER).</summary>
    public string? RemoteUser { get; set; }

    /// <summary>That user's password (SIM_I_REMOTE_PASSWORD).</summary>
    public string? RemotePassword { get; set; }

    /// <summary>Where long work says how far it is: the server's log.</summary>
    public Action<string> Progress { get; set; } = _ => { };

    /// <summary>The remote system to receive from, and who signs on to it.</summary>
    internal sealed record RemoteLogin(string Host, int Port, string User, string Password, RemoteSecurity Security);

    /// <summary>How far a receive is: what it says as it goes, and what it has done so far. Read from another thread while it runs.</summary>
    internal sealed class ReceiveStatus
    {
        private readonly List<string> _lines = [];
        private int _restored, _failed;
        private long _records;

        public int Restored => _restored;
        public int Failed => _failed;
        public long Records => Interlocked.Read(ref _records);

        public void Say(string line)
        {
            lock (_lines) _lines.Add(line);
        }

        public void Done(bool ok)
        {
            if (ok) Interlocked.Increment(ref _restored);
            else Interlocked.Increment(ref _failed);
        }

        public void Loaded(long records) => Interlocked.Add(ref _records, records);

        /// <summary>The lines said from line <paramref name="after"/> on, and how many there are in all.</summary>
        public (List<string> Lines, int Count) Lines(int after)
        {
            lock (_lines) return (_lines.Skip(Math.Max(0, after)).ToList(), _lines.Count);
        }
    }

    /// <summary>An object of a remote library, as a survey of it finds it: what it is, how big it is there, and about how big it would be here.</summary>
    internal sealed record SurveyedObject(string Name, string Type, string Attribute, string Kind, string Text, bool Receivable, long Size, long Records, long Deleted, int Members, long Estimate, long EstimateWithoutData)
    {
        /// <summary>A logical file's, index's or view's physical files, each as LIBRARY/FILE: what has to be here for it to be made.</summary>
        public IReadOnlyList<string> BasedOn { get; init; } = [];
    }

    /// <summary>
    /// What a library on a remote system holds, without receiving any of it: each object with its size there
    /// and an estimate of the room it takes here (see <see cref="Receiver.Survey"/>).
    /// </summary>
    internal (List<SurveyedObject> Objects, bool Encrypted) Survey(RemoteLogin login, string library, IJobHost host, ReceiveStatus? status = null, IReadOnlyList<string>? names = null)
    {
        using var remote = RemoteSystem.Connect(login.Host, login.Port, login.User, login.Password, login.Security);
        return (new Receiver(this, remote, host, library.ToUpperInvariant(), library.ToUpperInvariant(), false, status ?? new ReceiveStatus(), null).Survey(names ?? ["*ALL"]), remote.Encrypted);
    }

    private CommandResult RunSaveRestore(ClCommand command, IJobHost host)
    {
        CommandResult Fail(string message, string id) => new(false, message, MessageId: id);
        static List<string> Words(string? value) =>
            [.. (value ?? "").Replace('(', ' ').Replace(')', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToUpperInvariant())];
        var some = command.Name == "SAVRSTOBJ";
        var names = some ? Words(command.Value("OBJ", 0)) : ["*ALL"];
        var libraries = Words(command.Value("LIB", some ? 1 : 0));
        var location = (command.Value("RMTLOCNAME", some ? 2 : 1) ?? "").Replace('(', ' ').Replace(')', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (some && names.Count == 0) return Fail("Parameter OBJ required.", "CPD0071");
        if (libraries.Count == 0) return Fail("Parameter LIB required.", "CPD0071");
        if (location is null) return Fail("Parameter RMTLOCNAME required.", "CPD0071");
        if (libraries.FirstOrDefault(l => !IsName(l)) is { } wrong) return Fail($"{wrong} isn't a library name.", "CPD0078");
        if (names.FirstOrDefault(n => n != "*ALL" && !IsName(n.TrimEnd('*'))) is { } wrongName) return Fail($"{wrongName} isn't an object name.", "CPD0078");
        var restoreTo = Words(command.Value("RSTLIB", -1)).FirstOrDefault() is { } to && to != "*LIB" && to != "*SAVLIB" ? to : null;
        if (restoreTo is not null && (libraries.Count > 1 || !Store.IsLibraryName(restoreTo)))
            return Fail(libraries.Count > 1 ? "RSTLIB takes a library only when LIB names one library." : $"{restoreTo} isn't a library name.", "CPD0078");
        if (libraries.FirstOrDefault(l => !Store.IsLibraryName(restoreTo ?? l)) is { } unfit) return Fail($"{unfit} isn't a name a library can have here.", "CPD0078");
        var types = Words(command.Value("OBJTYPE", -1));
        if (types.Count == 0) types.Add("*ALL");
        var omit = Words(command.Value("OMITOBJ", -1));
        // WHERE('CUSTNO > 1000'): only the records of the files received that meet an SQL condition, as it's written on the remote system.
        var where = command.Value("WHERE", -1) is { } condition ? Unquoted(condition) : null;
        // DATA(*NO), as CRTDUPOBJ has it: the files come without their records (source members come all the same).
        var data = Words(command.Value("DATA", -1)).FirstOrDefault() != "*NO";
        var user = command.Value("RMTUSER", -1)?.Trim().ToUpperInvariant() ?? RemoteUser;
        var password = command.Value("RMTPWD", -1) is { } p ? Unquoted(p) : RemotePassword;
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password))
            return Fail("The user and password to sign on to the remote system with are needed: RMTUSER and RMTPWD, or SIM_I_REMOTE_USER and SIM_I_REMOTE_PASSWORD in the settings.", "CPD0071");
        var port = 21;
        if (command.Value("PORT", -1) is { } portText && (!int.TryParse(portText, out port) || port is < 1 or > 65535)) return Fail($"PORT({portText}) isn't a port.", "CPD0084");
        RemoteSecurity security;
        switch (Words(command.Value("SECCNN", -1)).FirstOrDefault())
        {
            case null or "*DFT": security = RemoteSecurity.Default; break;
            case "*SSL" or "*TLS": security = RemoteSecurity.Required; break;
            case "*NONE": security = RemoteSecurity.None; break;
            default: return Fail($"SECCNN({command.Value("SECCNN", -1)}) isn't *DFT, *SSL or *NONE.", "CPD0084");
        }

        return Receive(new RemoteLogin(location, port, user, password, security), libraries, names, types, omit, restoreTo, data, host,
            filters: where is { Length: > 0 } ? new Dictionary<string, string> { ["*ALL"] = where } : null);
    }

    /// <summary>
    /// Receives objects of libraries on a remote system: those of <paramref name="names"/> (names, generic names or
    /// *ALL) and <paramref name="types"/>, but for <paramref name="omit"/>. <paramref name="status"/> is told how far it is.
    /// </summary>
    internal CommandResult Receive(RemoteLogin login, IReadOnlyList<string> libraries, IReadOnlyList<string> names, IReadOnlyList<string> types,
        IReadOnlyList<string> omit, string? restoreTo, bool data, IJobHost host, ReceiveStatus? status = null, IReadOnlyDictionary<string, string>? filters = null)
    {
        CommandResult Fail(string message, string id) => new(false, message, MessageId: id);
        var (location, user) = (login.Host, login.User);
        status ??= new ReceiveStatus();
        var listing = new List<string>();
        int restored = 0, failed = 0, left = 0;
        long records = 0;
        try
        {
            using var remote = RemoteSystem.Connect(login.Host, login.Port, login.User, login.Password, login.Security);
            listing.Add($"Objects received from {location.ToUpperInvariant()} as {user}, {DateTime.Now:yyyy-MM-dd HH:mm:ss}"
                + (remote.Encrypted ? "" : " (the connection isn't encrypted: its FTP server offers no TLS)"));
            foreach (var library in libraries)
            {
                var receiver = new Receiver(this, remote, host, library, restoreTo ?? library, data, status, filters);
                try
                {
                    receiver.Receive(names, types, omit);
                }
                finally
                {
                    // What came before something ended it is still here, and still listed.
                    listing.AddRange(receiver.Listing);
                    restored += receiver.Restored;
                    failed += receiver.Failed;
                    left += receiver.Left;
                    records += receiver.Records;
                }
            }
        }
        catch (Exception e) when (e is RemoteException or IOException or System.Net.Sockets.SocketException)
        {
            var why = e is RemoteException ? e.Message : $"The connection to {location} was lost: {e.Message}";
            if (restored == 0 && failed == 0) return Fail(why, why.Contains("not found", StringComparison.OrdinalIgnoreCase) ? "CPF3781" : "CPF3770");
            listing.Add($"Ended early: {why}");
            status.Say($"Ended early: {why}");
            failed++;
        }
        catch (OperationCanceledException)
        {
            listing.Add("Ended early: the job was ended.");
            status.Say("Ended early: the job was ended.");
            failed++;
        }
        var spool = Store.AddSpool("QPSRODSP", host.User, 132, listing);
        var target = restoreTo ?? string.Join(" ", libraries);
        var also = (records > 0 ? $" {records:N0} records." : "") + (left > 0 ? $" {left} of types Sim i doesn't receive (programs and such) were left." : "")
            + $" Spooled file QPSRODSP ({spool}) lists them.";
        if (failed == 0 && restored == 0) return Fail($"No objects restored to {target}.{also}", "CPF3770");
        return failed == 0
            ? new CommandResult(true, $"{restored} objects restored from {string.Join(" ", libraries)} to {target}.{also}")
            : Fail($"{restored} objects restored. {failed} not restored to {target}.{also}", "CPF3773");
    }

    /// <summary>What's left of a file that's replaced: its object, and its table, view or index (and what was built on it).</summary>
    private void DropFile(string library, string name)
    {
        if (Store.FindObject(library, name, "*FILE") is { } o) Store.DeleteObject(o);
        foreach (var row in Store.Select("""
            SELECT c.relname::text, c.relkind::text FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @s AND lower(c.relname) = lower(@t) AND c.relkind IN ('r', 'v', 'i', 'm')
            """, ("s", Store.Schema(library).Trim('"')), ("t", name)))
        {
            var kind = (string)row[1]! switch { "r" => "TABLE", "v" => "VIEW", "m" => "MATERIALIZED VIEW", _ => "INDEX" };
            Store.Run($"DROP {kind} IF EXISTS {Store.Schema(library)}.\"{((string)row[0]!).Replace("\"", "\"\"")}\" CASCADE");
        }
    }

    /// <summary>Logical files whose view went with the file it was over: their objects go too.</summary>
    private void DropOrphans(string library)
    {
        var relations = Store.Select("SELECT lower(c.relname::text) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = @s",
            ("s", Store.Schema(library).Trim('"'))).Select(r => (string)r[0]!).ToHashSet();
        foreach (var o in Store.Objects("*FILE").Where(o => o.Library == library && o.Kind == "LF" && !relations.Contains(o.Name.ToLowerInvariant())))
            Store.DeleteObject(o);
    }

    /// <summary>A DDS line: its type (R, K, S, O or blank) in column 17, a name in 19, length, data type and decimals in 30 to 37, keywords from 45.</summary>
    internal static string DdsLine(char kind, string name, string length = "", char type = ' ', string decimals = "", string keywords = "", char usage = ' ') =>
        $"     A{new string(' ', 10)}{kind} {name,-10} {length,5}{type}{decimals,2}{usage}{new string(' ', 6)}{keywords}".TrimEnd();

    /// <summary>A keyword that doesn't fit a line's 36 columns, over as many lines as it takes: each but the last ends in - (which continues it from column 45).</summary>
    private static IEnumerable<string> DdsKeyword(string keyword)
    {
        for (var at = 0; at < keyword.Length; at += 35)
        {
            var last = at + 35 >= keyword.Length;
            yield return $"{new string(' ', 44)}{keyword.Substring(at, Math.Min(35, keyword.Length - at))}{(last ? "" : "-")}";
        }
    }

    [GeneratedRegex(@"VARLEN|REFFLD|RENAME|SST\(|CONCAT\(|DESCEND|UNIQUE|PFILE|JFILE|REF\(|ALWNULL", RegexOptions.IgnoreCase)]
    private static partial Regex DdsWord();

    /// <summary>
    /// The DDS of a physical file with these fields: its record format, each field's type and length,
    /// and its key. It's what Sim i makes the file from, and what CRTPF would take on an IBM i.
    /// </summary>
    internal static string PhysicalDds(string format, IReadOnlyList<RemoteField> fields, IReadOnlyList<(string Field, bool Descending)> keys, bool unique)
    {
        var dds = new List<string>();
        if (unique) dds.Add(DdsLine(' ', "", keywords: "UNIQUE"));
        dds.Add(DdsLine('R', format));
        foreach (var field in fields)
        {
            var (type, length, decimals) = field.Dds;
            var words = new List<string>();
            if (field.Varying) words.Add("VARLEN");
            if (field.Type == 'F' && field.Bytes > 4) words.Add("FLTPCN(*DOUBLE)");
            if (field.Nullable) words.Add("ALWNULL");
            dds.Add(DdsLine(' ', field.Name, length, type, decimals, string.Join(' ', words)));
            // A text that holds a DDS keyword's name is left out: it's a comment, and not worth being misread.
            if (field.Text.Trim() is { Length: > 0 } text && !DdsWord().IsMatch(text))
                dds.AddRange(DdsKeyword($"TEXT('{text.Replace("'", "''")}')").Select(l => "     A" + l[6..]));
        }
        dds.AddRange(keys.Select(k => DdsLine('K', k.Field, keywords: k.Descending ? "DESCEND" : "")));
        return string.Join('\n', dds);
    }

    /// <summary>One select/omit condition as DSPFD lists it: S, O or A (and), a comparison (EQ, NE ... VA, RG, AL) and its values.</summary>
    internal sealed record RemoteRule(char Rule, string Field, string Comparison, List<string> Values);

    /// <summary>
    /// A logical file's record format: its physical file, its fields (none: all of the physical file's; each
    /// its own name and, when it isn't the physical file's field of that name, the RENAME or SST that it is), key and select/omit rules.
    /// </summary>
    internal sealed record LogicalFormat(string Name, string Physical, IReadOnlyList<(string Field, string Keyword)> Fields,
        IReadOnlyList<(string Field, bool Descending)> Keys, IReadOnlyList<RemoteRule> Rules);

    /// <summary>The DDS of a logical file: each record format over its physical file, with its fields, key and select/omit rules.</summary>
    internal static string LogicalDds(IReadOnlyList<LogicalFormat> formats, bool unique)
    {
        var dds = new List<string>();
        if (unique) dds.Add(DdsLine(' ', "", keywords: "UNIQUE"));
        foreach (var format in formats)
        {
            dds.Add(DdsLine('R', format.Name, keywords: $"PFILE({format.Physical})"));
            // A part of a field (SST) is for input only, which DDS wants said (I in column 38).
            dds.AddRange(format.Fields.Select(f => DdsLine(' ', f.Field, keywords: f.Keyword, usage: f.Keyword.StartsWith("SST(", StringComparison.Ordinal) ? 'I' : ' ')));
            dds.AddRange(format.Keys.Select(k => DdsLine('K', k.Field, keywords: k.Descending ? "DESCEND" : "")));
            foreach (var rule in format.Rules)
            {
                var condition = rule.Comparison switch
                {
                    "AL" => "ALL",
                    "VA" => $"VALUES({string.Join(' ', rule.Values)})",
                    "RG" => $"RANGE({string.Join(' ', rule.Values)})",
                    _ => $"COMP({rule.Comparison} {rule.Values.FirstOrDefault() ?? "' '"})",
                };
                dds.Add(DdsLine(rule.Rule == 'A' ? ' ' : rule.Rule, rule.Field, keywords: condition));
            }
        }
        return string.Join('\n', dds);
    }

    /// <summary>Whether a name is the one a pattern names: *ALL, a generic name (ABC*) or the name itself.</summary>
    internal static bool NameMatches(string name, string pattern) =>
        pattern == "*ALL" || (pattern.EndsWith('*') ? name.StartsWith(pattern[..^1], StringComparison.Ordinal) : name == pattern);

    /// <summary>Receives one library's objects.</summary>
    private sealed class Receiver(Machine machine, RemoteSystem remote, IJobHost host, string library, string target, bool data, ReceiveStatus status,
        IReadOnlyDictionary<string, string>? filters)
    {
        private sealed record RemoteObject(string Name, string Type, string Attribute, string Text, string SourceFile, string SourceLibrary, string SourceMember, long Size = 0);

        private sealed record RemoteMember(string Name, long Records, long Deleted, string SourceType, string Text, long Size = 0);

        /// <summary>Says how far it is: to whoever follows this receive, and to the server's log.</summary>
        private void Progress(string line)
        {
            status.Say(line);
            machine.Progress(line);
        }

        public List<string> Listing { get; } = [];
        public int Restored { get; private set; }
        public int Failed { get; private set; }
        public int Left { get; private set; }
        public long Records { get; private set; }

        private readonly Store _store = machine.Store;
        private Dictionary<string, RemoteLayout> _layouts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, string>> _attributes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Dictionary<string, string>>> _paths = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Dictionary<string, string>>> _rules = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<RemoteMember>> _members = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _unreadable = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _longNames = new(StringComparer.Ordinal);
        private readonly HashSet<string> _received = new(StringComparer.Ordinal);
        private bool _dropped;

        private void Done(RemoteObject o, string what)
        {
            Restored++;
            status.Done(true);
            Listing.Add($"  {o.Name,-10} {o.Type,-8} {o.Attribute,-10} {what}");
            Progress($"{library}/{o.Name} {o.Type}: {what}");
        }

        private void NotDone(RemoteObject o, string why)
        {
            Failed++;
            status.Done(false);
            Listing.Add($"  {o.Name,-10} {o.Type,-8} {o.Attribute,-10} NOT RESTORED: {why}");
            Progress($"{library}/{o.Name} {o.Type} not restored: {why}");
        }

        public void Receive(IReadOnlyList<string> names, IReadOnlyList<string> types, IReadOnlyList<string> omit)
        {
            Listing.Add("");
            Listing.Add($"Library {library}{(target == library ? "" : $", restored to {target}")}");
            var objects = Inventory(names).Where(o => (types.Contains("*ALL") || types.Contains(o.Type)) && !omit.Any(pattern => NameMatches(o.Name, pattern)))
                .DistinctBy(o => (o.Name, o.Type)).OrderBy(o => o.Name, StringComparer.Ordinal).ToList();
            if (objects.Count == 0)
            {
                Listing.Add("  No objects of those names and types.");
                return;
            }
            _store.CreateLibrary(target);
            var files = objects.Where(o => o.Type == "*FILE" && o.Attribute is "PF" or "LF" or "PF38" or "LF38").ToList();
            if (files.Count > 0) Describe(names, [.. files.Select(f => f.Name)]);

            bool Is(RemoteObject o, char kind, string dataType) =>
                _layouts.TryGetValue(o.Name, out var layout) && layout.Kind == kind && _attributes.TryGetValue(o.Name, out var attributes)
                && (dataType.Length == 0 || attributes["ATDTAT"] == dataType);
            // A file some listing couldn't describe (a damaged one, say) isn't received.
            foreach (var file in files.Where(f => !_layouts.ContainsKey(f.Name) || !_attributes.ContainsKey(f.Name)))
                NotDone(file, _unreadable.GetValueOrDefault(file.Name) ?? "the remote system doesn't describe its fields");
            foreach (var file in files.Where(f => Is(f, 'P', "D"))) Guarded(file, () => Physical(file));
            foreach (var file in files.Where(f => Is(f, 'P', "S"))) Guarded(file, () => Source(file));
            var logicals = files.Where(f => Is(f, 'L', "")).ToList();
            foreach (var file in logicals.Where(f => _layouts[f.Name].SqlType == ' ')) Guarded(file, () => Logical(file));
            foreach (var file in logicals.Where(f => _layouts[f.Name].SqlType == 'I')) Guarded(file, () => Index(file));
            Views([.. logicals.Where(f => _layouts[f.Name].SqlType == 'V')]);
            if (_dropped) machine.DropOrphans(target);

            var areas = objects.Where(o => o.Type == "*DTAARA").ToList();
            if (areas.Count > 0) DataAreas(areas);
            var messageFiles = objects.Where(o => o.Type == "*MSGF").ToList();
            if (messageFiles.Count > 0) MessageFiles(messageFiles);

            var others = objects.Where(o => o.Type is not ("*DTAARA" or "*MSGF") && !files.Contains(o)).ToList();
            Left += others.Count;
            foreach (var group in others.GroupBy(o => o.Type == "*FILE" ? $"*FILE {o.Attribute}" : o.Type).OrderBy(g => g.Key, StringComparer.Ordinal))
                Listing.Add($"  Not received, {group.Key}: {group.Count()} ({string.Join(' ', group.Take(12).Select(o => o.Name))}{(group.Count() > 12 ? " ..." : "")})");
        }

        /// <summary>One object's work: what goes wrong with it is said of it, and the rest go on.</summary>
        private void Guarded(RemoteObject o, Action work)
        {
            host.Cancelled.ThrowIfCancellationRequested();
            try
            {
                work();
            }
            catch (RemoteException e)
            {
                NotDone(o, e.Message);
            }
            catch (Exception e) when (e is PostgresException or ArgumentException or FormatException or InvalidOperationException or KeyNotFoundException)
            {
                NotDone(o, Store.Clean(e));
            }
        }

        private static bool NotThere(string problem) => Regex.IsMatch(problem, "not found|No files|No objects|no records|not exist", RegexOptions.IgnoreCase);

        /// <summary>The library's objects of these names (DSPOBJD).</summary>
        private List<RemoteObject> Inventory(IReadOnlyList<string> names)
        {
            var found = new List<RemoteObject>();
            foreach (var pattern in names)
            {
                var problem = remote.TryCommand($"DSPOBJD OBJ({RemoteSystem.ClName(library)}/{RemoteSystem.ClName(pattern)}) OBJTYPE(*ALL) OUTPUT(*OUTFILE) OUTFILE(QTEMP/QFKOBJ) OUTMBR(*FIRST *REPLACE)");
                if (problem is not null)
                {
                    if (Regex.IsMatch(problem, @"Library \S+ not found", RegexOptions.IgnoreCase)) throw new RemoteException($"Library {library} not found on {remote.Host}.");
                    if (!NotThere(problem)) throw new RemoteException(problem);
                    Listing.Add($"  {pattern}: no such object.");
                    continue;
                }
                found.AddRange(remote.Rows("QTEMP", "QFKOBJ", "QFKOBJ", "DSPOBJD").Select(r =>
                    new RemoteObject(r["ODOBNM"], r["ODOBTP"], r["ODOBAT"], r["ODOBTX"], r["ODSRCF"], r["ODSRCL"], r["ODSRCM"], ObjectSize(r))));
            }
            return found;
        }

        /// <summary>An object's size in bytes: DSPOBJD gives a big one in units.</summary>
        private static long ObjectSize(Dictionary<string, string> row)
        {
            long Number(string field) => long.TryParse(row.GetValueOrDefault(field), NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? n : 0;
            return Number("ODOBSZ") is var size && size < 9_999_999_999 ? size : Number("ODSIZU") * Math.Max(1, Number("ODBPUN"));
        }

        /// <summary>A file's member that's received: the one named as the file, else its first.</summary>
        private static RemoteMember? Chosen(string file, List<RemoteMember> members) => members.FirstOrDefault(m => m.Name == file) ?? members.FirstOrDefault();

        /// <summary>
        /// What the library holds, without receiving it: its objects (DSPOBJD), and of its files what kind
        /// each is and its members' records and sizes (DSPFD). The room a file takes here is estimated from
        /// its member's size there, less its deleted records, and a fifth more: what PostgreSQL's rows, the
        /// relative record number and the key's index were found to add. A file without records takes a few pages.
        /// </summary>
        public List<SurveyedObject> Survey(IReadOnlyList<string> names)
        {
            const long Empty = 16 * 1024;
            var objects = Inventory(names).DistinctBy(o => (o.Name, o.Type)).OrderBy(o => o.Name, StringComparer.Ordinal).ToList();
            var files = objects.Where(o => o.Type == "*FILE" && o.Attribute is "PF" or "LF" or "PF38" or "LF38").ToList();
            // Asked for by name, what each logical file is over is worth its listing; of a whole library it isn't.
            var byName = !names.Contains("*ALL");
            if (files.Count > 0) Describe(names, [.. files.Select(f => f.Name)], survey: true, paths: byName);
            var surveyed = new List<SurveyedObject>();
            foreach (var o in objects)
            {
                var (kind, receivable) = (o.Type == "*FILE" ? $"*FILE {o.Attribute}" : o.Type, o.Type is "*DTAARA" or "*MSGF");
                long records = 0, deleted = 0, estimate = receivable ? 8 * 1024 : 0, bare = estimate;
                var members = 0;
                if (files.Contains(o))
                {
                    if (!_attributes.TryGetValue(o.Name, out var attributes))
                    {
                        surveyed.Add(new SurveyedObject(o.Name, o.Type, o.Attribute, "File (not described: " + (_unreadable.GetValueOrDefault(o.Name) ?? "damaged?") + ")", o.Text, false, o.Size, 0, 0, 0, 0, 0));
                        continue;
                    }
                    receivable = true;
                    var all = _members.GetValueOrDefault(o.Name) ?? [];
                    members = all.Count;
                    var physical = attributes["ATFTYP"] == "P";
                    var sql = attributes["ATSQLT"];
                    if (physical && attributes["ATDTAT"] == "S")
                    {
                        kind = "Source file";
                        records = all.Sum(m => m.Records);
                        // Source is kept as text, without each record's trailing blanks.
                        estimate = bare = Empty + all.Sum(m => m.Size) / 2;
                    }
                    else if (physical)
                    {
                        kind = sql == "T" ? "SQL table" : "Physical file";
                        bare = Empty;
                        if (Chosen(o.Name, all) is { } member)
                        {
                            (records, deleted) = (member.Records, member.Deleted);
                            estimate = Empty + (records == 0 ? 0 : (long)(member.Size * (double)records / (records + deleted) * 1.2));
                        }
                        else estimate = Empty;
                    }
                    else
                    {
                        kind = sql == "V" ? "SQL view" : sql == "I" ? "SQL index" : attributes["ATJOIN"] == "Y" ? "Join logical file" : "Logical file";
                        // A logical file and a view are views here, which take no room; an index is an index, taken to be as big as it is there.
                        (estimate, bare) = sql == "I" ? (o.Size, 8 * 1024) : (0, 0);
                    }
                }
                surveyed.Add(new SurveyedObject(o.Name, o.Type, o.Attribute, kind, o.Text, receivable, o.Size, records, deleted, members, estimate, bare)
                {
                    BasedOn = [.. (_paths.GetValueOrDefault(o.Name) ?? []).Where(r => r["APBOF"].Length > 0).Select(r => $"{r["APBOL"]}/{r["APBOF"]}").Distinct()],
                });
            }
            return surveyed;
        }

        /// <summary>
        /// What the remote system says of the files: their fields (DSPFFD), attributes, access paths,
        /// select/omit rules and members (DSPFD). A listing of many files that fails (one damaged
        /// file stops it) is asked again by first letter, and then file by file.
        /// </summary>
        private void Describe(IReadOnlyList<string> patterns, IReadOnlyList<string> files, bool survey = false, bool paths = false)
        {
            void Listed(string pattern, Func<string, string?> ask)
            {
                var problem = ask(pattern);
                if (problem is null || NotThere(problem)) return;
                var narrower = pattern == "*ALL" ? files.Select(f => f[..1] + "*").Distinct().ToList()
                    : pattern.EndsWith('*') ? files.Where(f => NameMatches(f, pattern)).ToList()
                    : [];
                if (narrower.Count == 0) _unreadable[pattern] = problem;
                foreach (var narrow in narrower) Listed(narrow, ask);
            }
            string? Outfile(string pattern, string type, string attributes, Action<Dictionary<string, string>> row)
            {
                var problem = remote.TryCommand($"DSPFD FILE({RemoteSystem.ClName(library)}/{RemoteSystem.ClName(pattern)}) TYPE({type}) OUTPUT(*OUTFILE) FILEATR({attributes}) "
                    + "OUTFILE(QTEMP/QFKFD) OUTMBR(*FIRST *REPLACE)");
                if (problem is not null) return problem;
                foreach (var r in remote.Rows("QTEMP", "QFKFD", "QFKFD", "DSPFD " + type)) row(r);
                remote.TryCommand("DLTF FILE(QTEMP/QFKFD)");
                return null;
            }
            static void Add(Dictionary<string, List<Dictionary<string, string>>> to, string file, Dictionary<string, string> row)
            {
                if (!to.TryGetValue(file, out var rows)) to[file] = rows = [];
                rows.Add(row);
            }
            foreach (var pattern in patterns)
            {
                Progress($"{library}: reading the descriptions of {pattern}");
                if (!survey) Listed(pattern, p =>
                {
                    try
                    {
                        foreach (var (name, layout) in remote.Describe(library, p)) _layouts[name] = layout;
                        return null;
                    }
                    catch (RemoteException e)
                    {
                        return e.Message;
                    }
                });
                Listed(pattern, p => Outfile(p, "*BASATR", "*PF *LF", r => _attributes[r["ATFILE"]] = r));
                if (!survey || paths) Listed(pattern, p => Outfile(p, "*ACCPTH", "*PF *LF", r => Add(_paths, r["APFILE"], r)));
                if (!survey) Listed(pattern, p => Outfile(p, "*SELECT", "*LF", r => Add(_rules, r["SOFILE"], r)));
                Listed(pattern, p => Outfile(p, "*MBRLIST", "*PF", r =>
                {
                    if (!_members.TryGetValue(r["MLFILE"], out var members)) _members[r["MLFILE"]] = members = [];
                    if (r["MLNAME"].Length > 0)
                        members.Add(new RemoteMember(r["MLNAME"], long.Parse(r["MLNRCD"], CultureInfo.InvariantCulture), long.Parse(r["MLNDTR"], CultureInfo.InvariantCulture), r["MLSEU2"], r["MLMTXT"],
                            long.TryParse(r.GetValueOrDefault("MLSIZ2"), NumberStyles.Number, CultureInfo.InvariantCulture, out var size) ? size : 0));
                }));
            }
            _layouts = _layouts.Where(l => files.Contains(l.Key)).ToDictionary(StringComparer.Ordinal);
            // SQL names longer than a file's own (ten characters).
            if (_layouts.Values.Any(l => l.SqlType is 'T' or 'V'))
            {
                try
                {
                    foreach (var row in remote.Query($"SELECT SYSTEM_TABLE_NAME AS S, TABLE_NAME AS N FROM QSYS2.SYSTABLES WHERE SYSTEM_TABLE_SCHEMA = '{library}' AND TABLE_NAME <> SYSTEM_TABLE_NAME"))
                        _longNames[row["S"]] = row["N"];
                }
                catch (RemoteException e)
                {
                    Listing.Add($"  The files' SQL names couldn't be read, so they have their system names: {e.Message}");
                }
            }
        }

        /// <summary>A file's key: its fields in order, whether each descends, and whether the key is unique.</summary>
        private (List<(string Field, bool Descending)> Keys, bool Unique, string Constraint) KeyOf(string file, string? format = null)
        {
            var rows = _paths.GetValueOrDefault(file) ?? [];
            var keys = rows.Where(r => r["APKEYF"].Length > 0 && (format is null || r["APBOLF"].Length == 0 || r["APBOLF"] == format))
                .Select(r => (r["APKEYF"], r["APKSEQ"] == "D")).ToList();
            return (keys, rows.Any(r => r["APUNIQ"] == "Y"), rows.Select(r => r["APRIKY"]).FirstOrDefault(c => c.Length > 0) ?? "");
        }

        /// <summary>The name a file has here: its SQL name when that's longer than its own.</summary>
        private string NameHere(string file) => _longNames.GetValueOrDefault(file, file);

        // ---- physical files and tables ----

        private void Physical(RemoteObject o)
        {
            var layout = _layouts[o.Name];
            var fields = layout.Fields.Where(f => f.Supported).ToList();
            var leftOut = layout.Fields.Where(f => !f.Supported).Select(f => f.Name).ToList();
            if (fields.Count == 0) throw new RemoteException("none of its fields are of a type Sim i receives");
            var (keys, unique, constraint) = KeyOf(o.Name);
            var name = NameHere(o.Name);
            machine.DropFile(target, o.Name);
            if (name != o.Name) machine.DropFile(target, name);
            _dropped = true;
            if (layout.SqlType == 'T')
            {
                string Column(string field) => RemoteSystem.Quoted(fields.First(f => f.Name == field) is { Alias.Length: > 0 } f ? f.Alias : field);
                var columns = fields.Select(f =>
                    $"{RemoteSystem.Quoted(f.Alias.Length > 0 ? f.Alias : f.Name)}{(f.Alias.Length > 0 ? $" FOR COLUMN {RemoteSystem.Quoted(f.Name)}" : "")} {f.SqlType}"
                    + (f.Identity && f.Type == 'B' ? " GENERATED BY DEFAULT AS IDENTITY" : f.Nullable ? "" : " NOT NULL WITH DEFAULT")).ToList();
                if (keys.Count > 0 && unique && keys.All(k => fields.Any(f => f.Name == k.Field)))
                    columns.Add($"{(constraint == "U" ? "UNIQUE" : "PRIMARY KEY")} ({string.Join(", ", keys.Select(k => Column(k.Field)))})");
                var script = $"CREATE TABLE {RemoteSystem.Quoted(target)}.{RemoteSystem.Quoted(name)} ({string.Join(", ", columns)}) RCDFMT {layout.Format}";
                if (name != o.Name) script += $";\nRENAME TABLE {RemoteSystem.Quoted(target)}.{RemoteSystem.Quoted(name)} TO SYSTEM NAME {RemoteSystem.Quoted(o.Name)}";
                var (_, failures) = machine.RunScript(script, host.User, target);
                if (failures.Count > 0) throw new RemoteException($"its table can't be made: {failures[0]}");
            }
            else
            {
                var dds = PhysicalDds(layout.Format, fields, keys, unique && keys.Count > 0);
                var made = machine.CreateDatabaseFile(new Member(target, "QDDSSRC", o.Name, "PF", dds), target, o.Name);
                if (!made.Ok) throw new RemoteException(made.Message);
                if (_store.TableLibrary(o.Name, target) is null)
                {
                    _received.Add(o.Name);
                    Done(o, "restored without its records: " + made.Message);
                    return;
                }
                // Sim i's DDS makes every field NOT NULL; a null-capable one (ALWNULL) keeps its nulls.
                foreach (var field in fields.Where(f => f.Nullable))
                    _store.Run($"ALTER TABLE {_store.SqlName(target, o.Name)} ALTER COLUMN {Store.ColumnName(field.Name)} DROP NOT NULL");
            }
            _received.Add(o.Name);

            var members = _members.GetValueOrDefault(o.Name) ?? [];
            var notes = new List<string>();
            if (leftOut.Count > 0) notes.Add($"without {string.Join(", ", leftOut)} (LOB, DATALINK or ROWID)");
            long count = 0, bad = 0;
            if (!data) notes.Add("DATA(*NO)");
            else if (members.Count > 0)
            {
                var member = Chosen(o.Name, members)!;
                if (members.Count > 1) notes.Add($"member {member.Name} only, of {members.Count}");
                try
                {
                    if (member.Records > 0) (count, bad) = Load(o.Name, name, layout, fields, member, members.Count > 1);
                }
                catch
                {
                    // Some of its records are worse than none: the file is left empty, and said not to be restored.
                    _store.Run($"TRUNCATE TABLE {_store.SqlName(target, name)}");
                    throw;
                }
            }
            if (data && filters is not null && (filters.GetValueOrDefault(o.Name) ?? filters.GetValueOrDefault("*ALL")) is { Length: > 0 } only) notes.Add($"only where {only}");
            if (bad > 0) notes.Add($"{bad:N0} values weren't valid and are zero");
            Records += count;
            Done(o, $"restored, {(data ? $"{count:N0} records" : "without its records")}{(notes.Count > 0 ? "; " + string.Join("; ", notes) : "")}");
        }

        /// <summary>
        /// A member's records into the file's table. They're received as they are when every record is
        /// there and no field can be null; otherwise through a view (in the session's QTEMP) that adds
        /// each record's relative record number and a flag for each field that can be null.
        /// </summary>
        private (long Count, long Bad) Load(string file, string table, RemoteLayout layout, List<RemoteField> fields, RemoteMember member, bool namedMember)
        {
            var nullable = fields.Where(f => f.Nullable).ToList();
            // Only the records that meet the file's filter, when it has one: an SQL condition on its fields (A is the file, so RRN(A) <= 1000 is its first thousand).
            var filter = filters is null ? null : filters.GetValueOrDefault(file) ?? filters.GetValueOrDefault("*ALL");
            if (string.IsNullOrWhiteSpace(filter)) filter = null;
            var direct = member.Deleted == 0 && nullable.Count == 0 && fields.Count == layout.Fields.Count && filter is null;
            var (fromLibrary, fromFile, fromMember, recordLength) = (library, file, member.Name, layout.RecordLength);
            RemoteField? rrn = null;
            var flags = new List<RemoteField>();
            if (!direct)
            {
                var source = $"{RemoteSystem.Quoted(library)}.{RemoteSystem.Quoted(file)}";
                if (namedMember)
                {
                    remote.TryCommand("RUNSQL SQL('DROP ALIAS QTEMP.QFKA') COMMIT(*NONE) NAMING(*SQL)");
                    remote.Sql($"CREATE ALIAS QTEMP.QFKA FOR {source} ({RemoteSystem.Quoted(member.Name)})");
                    source = "QTEMP.QFKA";
                }
                remote.TryCommand("DLTF FILE(QTEMP/QFKV)");
                var select = new List<string> { "RRN(A) AS QFKRRN" };
                // A null can't be read through FTP, so a null-capable field comes as its value or a default, with its flag after.
                select.AddRange(fields.Select(f => f.Nullable ? $"COALESCE(A.{RemoteSystem.Quoted(f.Name)}, {NullDefault(f)}) AS {RemoteSystem.Quoted(f.Name)}" : $"A.{RemoteSystem.Quoted(f.Name)}"));
                select.AddRange(nullable.Select((f, i) => $"CAST(CASE WHEN A.{RemoteSystem.Quoted(f.Name)} IS NULL THEN '1' ELSE '0' END AS CHAR(1)) AS QFN{i + 1:00000}"));
                try
                {
                    remote.Sql($"CREATE VIEW QTEMP.QFKV AS SELECT {string.Join(", ", select)} FROM {source} A{(filter is null ? "" : $" WHERE {filter}")}");
                }
                catch (RemoteException e) when (filter is not null)
                {
                    throw new RemoteException($"its filter ({filter}) isn't a condition the remote system takes: {e.Message}");
                }
                var view = remote.Describe("QTEMP", "QFKV")["QFKV"];
                if (view.Fields.Count != 1 + fields.Count + nullable.Count)
                    throw new RemoteException($"the view to receive it through has {view.Fields.Count} fields, not {1 + fields.Count + nullable.Count}");
                rrn = view.Fields[0];
                // The view's fields are the file's, each at its place in the view's record.
                fields = [.. view.Fields.Skip(1).Take(fields.Count).Select((f, i) => f with { Name = fields[i].Name })];
                flags = [.. view.Fields.Skip(1 + fields.Count)];
                (fromLibrary, fromFile, fromMember, recordLength) = ("QTEMP", "QFKV", "QFKV", view.RecordLength);
            }

            var columns = _store.SqlColumns(table, target).ToDictionary(c => c.Name.ToUpperInvariant(), c => c.Column, StringComparer.Ordinal);
            var loaded = fields.Where(f => columns.ContainsKey(f.Name)).ToList();
            var flagOf = new RemoteField?[loaded.Count];
            for (var i = 0; i < loaded.Count; i++)
            {
                var at = nullable.FindIndex(f => f.Name == loaded[i].Name);
                flagOf[i] = at >= 0 && flags.Count > at ? flags[at] : null;
            }
            var sqlName = _store.SqlName(target, table);
            var copy = $"COPY {sqlName} ({string.Join(", ", loaded.Select(f => $"\"{columns[f.Name].Replace("\"", "\"\"")}\""))}, {Store.RowNumber}) FROM STDIN";
            var rows = new StringBuilder();
            long bad = 0, number = 0, highest = 0;
            long unsaid = 0;
            void Flush()
            {
                if (rows.Length == 0) return;
                _store.Copy(copy, rows.ToString());
                rows.Clear();
                status.Loaded(unsaid);
                unsaid = 0;
            }
            var count = remote.Download(fromLibrary, fromFile, fromMember, recordLength, record =>
            {
                for (var i = 0; i < loaded.Count; i++)
                {
                    if (flagOf[i] is { } flag && record[flag.Offset] == 0xF1) rows.Append(@"\N");
                    else RemoteRecords.AppendCopy(rows, RemoteRecords.Value(record, loaded[i], ref bad));
                    rows.Append('\t');
                }
                number = rrn is null ? number + 1 : long.Parse(RemoteRecords.Value(record, rrn, ref bad), CultureInfo.InvariantCulture);
                highest = Math.Max(highest, number);
                rows.Append(number).Append('\n');
                unsaid++;
                if (rows.Length >= 4_000_000)
                {
                    Flush();
                    Progress($"{library}/{file}: {highest:N0} of {member.Records + member.Deleted:N0}");
                }
            }, host.Cancelled);
            Flush();
            if (!direct) remote.TryCommand("DLTF FILE(QTEMP/QFKV)");
            // New records go on from the highest relative record number, and identity columns from their highest value.
            if (highest > 0) _store.Run($"SELECT setval(pg_get_serial_sequence(@t, '{Store.RowNumber}'), @n)", ("t", sqlName), ("n", highest));
            foreach (var identity in loaded.Where(f => f.Identity && f.Type == 'B'))
            {
                var column = columns[identity.Name];
                _store.Run($"SELECT setval(pg_get_serial_sequence(@t, @c), greatest((SELECT max(\"{column}\") FROM {sqlName}), 1))", ("t", sqlName), ("c", column));
            }
            return (count, bad);
        }

        /// <summary>What a null field is read as (its flag says it's null): any value of its type.</summary>
        private static string NullDefault(RemoteField field) => field.Type switch
        {
            'P' or 'S' or 'B' or 'F' or '6' => "0",
            'L' => "DATE('0001-01-01')",
            'T' => "TIME('00.00.00')",
            'Z' => "TIMESTAMP('0001-01-01-00.00.00.000000')",
            'G' => "UX'0020'",
            '5' => "BX'00'",
            _ => "' '",
        };

        // ---- source files ----

        private void Source(RemoteObject o)
        {
            var layout = _layouts[o.Name];
            var data = layout.Fields.FirstOrDefault(f => f.Name == "SRCDTA") ?? layout.Fields[^1];
            var members = _members.GetValueOrDefault(o.Name) ?? [];
            int saved = 0, unsupported = 0;
            foreach (var member in members)
            {
                host.Cancelled.ThrowIfCancellationRequested();
                var source = member.Records == 0 ? "" : SourceText(library, o.Name, member.Name, data, layout.RecordLength);
                var seu = member.SourceType.Trim().ToUpperInvariant();
                var received = new Member(target, o.Name, member.Name, TypeForExtension(seu) ?? (seu.Length == 0 ? "TXT" : seu), source);
                IReadOnlyList<Diagnostic> found;
                try
                {
                    found = machine.Save(received);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Source the checker can't get through is kept all the same.
                    _store.SaveMember(received, [new Diagnostic(0, Severity.Warning, $"the member couldn't be checked: {e.Message}")]);
                    found = [];
                }
                if (found.Any(d => d.Severity is Severity.Unsupported or Severity.Error)) unsupported++;
                saved++;
                if (saved % 100 == 0) Progress($"{library}/{o.Name}: {saved} of {members.Count} members");
            }
            Done(o, $"restored, {saved} member{(saved == 1 ? "" : "s")}{(unsupported > 0 ? $"; {unsupported} with lines Sim i doesn't support or that are in error" : "")}");
        }

        /// <summary>A source member's text: FTP sends its source data without the sequence number and date (or, when not, they're skipped).</summary>
        private string SourceText(string fromLibrary, string file, string member, RemoteField data, int recordLength)
        {
            var bytes = remote.Download(fromLibrary, file, member);
            var (step, skip) = bytes.Length % data.Bytes == 0 ? (data.Bytes, 0) : bytes.Length % recordLength == 0 ? (recordLength, data.Offset)
                : throw new RemoteException($"member {member} isn't records of {data.Bytes} or {recordLength} bytes");
            var text = new StringBuilder(bytes.Length);
            for (var at = 0; at < bytes.Length; at += step)
                text.Append(RemoteRecords.SourceLine(bytes.AsSpan(at + skip, data.Bytes), data.Ccsid)).Append('\n');
            return text.ToString();
        }

        // ---- logical files, indexes and views ----

        private void Logical(RemoteObject o)
        {
            var layout = _layouts[o.Name];
            machine.DropFile(target, o.Name);
            var why = "";
            if (_attributes[o.Name]["ATJOIN"] == "Y") why = "it's a join logical file";
            else if (LogicalSource(o, layout) is { } dds)
            {
                var made = machine.CreateDatabaseFile(new Member(target, "QDDSSRC", o.Name, "LF", dds), target, o.Name);
                if (made.Ok)
                {
                    Done(o, "restored");
                    return;
                }
                why = made.Message;
            }
            else why = "its fields are more than its physical file's own, renamed or in part";
            // What its description can't say (renamed and derived fields, joins), its DDS source can.
            if (o.SourceMember.Length > 0 && o.SourceFile.Length > 0)
            {
                try
                {
                    var described = remote.Describe(o.SourceLibrary, o.SourceFile).GetValueOrDefault(o.SourceFile);
                    if (described?.Fields.FirstOrDefault(f => f.Name == "SRCDTA") is { } data)
                    {
                        var source = SourceText(o.SourceLibrary, o.SourceFile, o.SourceMember, data, described.RecordLength);
                        var made = machine.CreateDatabaseFile(new Member(target, o.SourceFile, o.Name, "LF", source), target, o.Name);
                        if (made.Ok)
                        {
                            Done(o, $"restored from its source, {o.SourceLibrary}/{o.SourceFile}({o.SourceMember})");
                            return;
                        }
                        why += $"; from its source {o.SourceLibrary}/{o.SourceFile}({o.SourceMember}): {made.Message}";
                    }
                }
                catch (RemoteException e)
                {
                    why += $"; its source {o.SourceLibrary}/{o.SourceFile}({o.SourceMember}) can't be read: {e.Message}";
                }
            }
            NotDone(o, why);
        }

        /// <summary>
        /// A logical file's DDS from its description: each record format over its physical file, with the
        /// fields it has of it (renamed, or a part of one), its key and its select/omit rules. Null when a
        /// field is none of the physical file's (a concatenation, say), which only its source can say.
        /// </summary>
        private string? LogicalSource(RemoteObject o, RemoteLayout layout)
        {
            var paths = _paths.GetValueOrDefault(o.Name) ?? [];
            var formats = new List<LogicalFormat>();
            foreach (var format in layout.Formats)
            {
                var based = paths.Where(r => r["APBOLF"] == format || layout.Formats.Count == 1).Select(r => r["APBOF"]).FirstOrDefault(f => f.Length > 0);
                if (based is null) return null;
                var physical = _layouts.TryGetValue(based, out var received) ? received.Fields.Where(f => f.Supported).Select(f => f.Name).ToList()
                    : machine.FindDatabaseFile(based, target)?.Fields.Select(f => f.Name).ToList();
                if (physical is null) throw new RemoteException($"its physical file {based} isn't here");
                var own = layout.Fields.Where(f => f.RecordFormat == format).ToList();
                if (own.Any(f => !physical.Contains(f.Internal))) return null;
                var fields = own.Select(f => (f.Name, f.Substring is var (start, length) ? $"SST({f.Internal} {start} {length})" : f.Internal != f.Name ? $"RENAME({f.Internal})" : "")).ToList();
                var rules = new List<RemoteRule>();
                foreach (var row in (_rules.GetValueOrDefault(o.Name) ?? []).Where(r => r["SORFMT"] == format || layout.Formats.Count == 1))
                {
                    var rule = row["SORULE"].FirstOrDefault(' ');
                    var value = row["SOVALU"];
                    // A VALUES or RANGE rule's further values come on lines of their own.
                    if (rules.Count > 0 && rules[^1].Comparison is "VA" or "RG" && row["SOCOMP"] == rules[^1].Comparison && (rule == ' ' || (rule == rules[^1].Rule && row["SOFLD"] == rules[^1].Field)))
                    {
                        if (value.Length > 0) rules[^1].Values.Add(value);
                        continue;
                    }
                    if (rule == ' ') continue;
                    rules.Add(new RemoteRule(rule, row["SOFLD"], row["SOCOMP"], value.Length > 0 ? [value] : []));
                }
                var plain = fields.All(f => f.Item2.Length == 0) && own.Select(f => f.Name).SequenceEqual(physical);
                formats.Add(new LogicalFormat(format, based, plain ? [] : fields, KeyOf(o.Name, layout.Formats.Count == 1 ? null : format).Keys, rules));
            }
            var (keys, unique, _) = KeyOf(o.Name);
            return LogicalDds(formats, unique && keys.Count > 0);
        }

        private void Index(RemoteObject o)
        {
            var based = (_paths.GetValueOrDefault(o.Name) ?? []).Select(r => r["APBOF"]).FirstOrDefault(f => f.Length > 0)
                ?? throw new RemoteException("the remote system doesn't say which table it's over");
            var (keys, unique, _) = KeyOf(o.Name);
            var table = NameHere(based);
            if (_store.TableLibrary(table, target) is null) throw new RemoteException($"its table {based} isn't here");
            var columns = _store.SqlColumns(table, target).ToDictionary(c => c.Name.ToUpperInvariant(), c => c.Column, StringComparer.Ordinal);
            if (keys.Count == 0) throw new RemoteException("it has no key");
            if (keys.FirstOrDefault(k => !columns.ContainsKey(k.Field)).Field is { } missing) throw new RemoteException($"its key field {missing} isn't a column of its table");
            machine.DropFile(target, o.Name);
            _store.Run($"CREATE {(unique ? "UNIQUE " : "")}INDEX {Store.ColumnName(o.Name)} ON {_store.SqlName(target, table)} "
                + $"({string.Join(", ", keys.Select(k => $"\"{columns[k.Field].Replace("\"", "\"\"")}\"{(k.Descending ? " DESC" : "")}"))})");
            Done(o, "restored");
        }

        /// <summary>SQL views, from their definitions: a view over another is made after it, so they're tried until none more can be made.</summary>
        private void Views(List<RemoteObject> views)
        {
            if (views.Count == 0) return;
            var definitions = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                var which = views.Count <= 40 ? $" AND SYSTEM_VIEW_NAME IN ({string.Join(", ", views.Select(v => $"'{v.Name}'"))})" : "";
                foreach (var row in remote.Query("SELECT SYSTEM_VIEW_NAME AS S, COALESCE(CAST(VIEW_DEFINITION AS VARGRAPHIC(10000) CCSID 1200), UX'0020') AS D "
                    + $"FROM QSYS2.SYSVIEWS WHERE SYSTEM_VIEW_SCHEMA = '{library}'{which}"))
                    definitions[row["S"]] = row["D"];
            }
            catch (RemoteException e)
            {
                foreach (var view in views) NotDone(view, $"the views' definitions can't be read: {e.Message}");
                return;
            }
            var waiting = new List<(RemoteObject View, string Problem)>();
            foreach (var view in views)
            {
                if (definitions.GetValueOrDefault(view.Name)?.Trim() is { Length: > 0 }) waiting.Add((view, ""));
                else NotDone(view, "the remote system has no definition of it to give (one over 10,000 characters isn't kept)");
            }
            while (waiting.Count > 0)
            {
                host.Cancelled.ThrowIfCancellationRequested();
                var still = new List<(RemoteObject, string)>();
                foreach (var (view, _) in waiting)
                {
                    var name = NameHere(view.Name);
                    var definition = definitions[view.Name];
                    if (target != library) definition = Regex.Replace(definition, $@"\b{Regex.Escape(library)}(?=[./])", target, RegexOptions.IgnoreCase);
                    var columns = string.Join(", ", _layouts[view.Name].Fields.Select(f => RemoteSystem.Quoted(f.Alias.Length > 0 ? f.Alias : f.Name)));
                    machine.DropFile(target, view.Name);
                    if (name != view.Name) machine.DropFile(target, name);
                    var script = $"CREATE VIEW {RemoteSystem.Quoted(target)}.{RemoteSystem.Quoted(name)} ({columns}) AS {definition.Trim().TrimEnd(';')}";
                    if (name != view.Name)
                        script += $";\nCREATE VIEW {RemoteSystem.Quoted(target)}.{RemoteSystem.Quoted(view.Name)} AS SELECT * FROM {RemoteSystem.Quoted(target)}.{RemoteSystem.Quoted(name)}";
                    var (_, failures) = machine.RunScript(script, host.User, target);
                    if (failures.Count == 0) Done(view, "restored");
                    else still.Add((view, failures[0]));
                }
                if (still.Count == waiting.Count)
                {
                    foreach (var (view, problem) in still) NotDone(view, $"its definition doesn't run here: {problem}");
                    return;
                }
                waiting = still;
            }
        }

        // ---- data areas and message files ----

        private static string InList(IEnumerable<RemoteObject> objects) => string.Join(", ", objects.Select(o => $"'{o.Name}'"));

        private void DataAreas(List<RemoteObject> areas)
        {
            Dictionary<string, Dictionary<string, string>> values;
            try
            {
                values = remote.Query("SELECT DATA_AREA_NAME AS N, DATA_AREA_TYPE AS T, LENGTH AS L, COALESCE(DECIMAL_POSITIONS, 0) AS D, COALESCE(DATA_AREA_VALUE, '') AS V "
                    + $"FROM QSYS2.DATA_AREA_INFO WHERE DATA_AREA_LIBRARY = '{library}'{(areas.Count <= 40 ? $" AND DATA_AREA_NAME IN ({InList(areas)})" : "")}")
                    .ToDictionary(r => r["N"].Trim(), StringComparer.Ordinal);
            }
            catch (RemoteException e)
            {
                foreach (var area in areas) NotDone(area, $"the data areas' values can't be read: {e.Message}");
                return;
            }
            foreach (var area in areas)
            {
                if (!values.TryGetValue(area.Name, out var row))
                {
                    NotDone(area, "the remote system gives no value for it");
                    continue;
                }
                var type = row["T"].Trim();
                if (type is not ("*CHAR" or "*DEC" or "*LGL"))
                {
                    NotDone(area, $"a data area of type {type} isn't supported");
                    continue;
                }
                _store.SaveObject(target, area.Name, "*DTAARA", type == "*DEC" ? $"*DEC {row["L"]} {row["D"]}" : $"{type} {row["L"]}",
                    type == "*CHAR" ? row["V"].TrimEnd() : row["V"].Trim());
                Done(area, "restored");
            }
        }

        private void MessageFiles(List<RemoteObject> files)
        {
            var descriptions = files.ToDictionary(f => f.Name, _ => new Dictionary<string, MessageDescription>(StringComparer.OrdinalIgnoreCase), StringComparer.Ordinal);
            try
            {
                foreach (var row in remote.Query("SELECT MESSAGE_FILE AS F, MESSAGE_ID AS I, COALESCE(MESSAGE_TEXT, UX'0020') AS T, COALESCE(MESSAGE_SECOND_LEVEL_TEXT, UX'0020') AS H "
                    + $"FROM QSYS2.MESSAGE_FILE_DATA WHERE MESSAGE_FILE_LIBRARY = '{library}'{(files.Count <= 40 ? $" AND MESSAGE_FILE IN ({InList(files)})" : "")}"))
                {
                    if (descriptions.TryGetValue(row["F"].Trim(), out var file))
                        file[row["I"]] = new MessageDescription(row["T"].TrimEnd(), row["H"].TrimEnd() is { Length: > 0 } help ? help : null);
                }
            }
            catch (RemoteException e)
            {
                foreach (var file in files) NotDone(file, $"the message descriptions can't be read: {e.Message}");
                return;
            }
            foreach (var file in files)
            {
                _store.SaveObject(target, file.Name, "*MSGF", "", Serialized(descriptions[file.Name]));
                Done(file, $"restored, {descriptions[file.Name].Count} messages");
            }
        }
    }
}
