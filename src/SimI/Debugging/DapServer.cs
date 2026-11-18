// The debug server (--debug-port): the Debug Adapter Protocol, which VS Code and other editors
// speak, over TCP. A debugger connects, signs on with a user profile in its launch or attach
// request, and then either
//
//   launches   a program: one with screens (a display file) in the user's 5250 session that waits at a command line, as
//              though CALL were typed there, so its screens show there; any other in a job of its own, DSPLY going
//              to the debug console. "terminal": true or false in the launch request says which instead; or
//   attaches   to a user's jobs: when that user's 5250 session (or any job of theirs) reaches a
//              breakpoint it stops there, its screen waiting, until the debugger lets it go on.
//
// Breakpoints are set by source file: the file's name is the program's (CUSTMNT.sqlrpgle is
// CUSTMNT), and its folder under the debugger's source root, when it has one, the library.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SimI.Rpg;

namespace SimI.Debugging;

public sealed class DapServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accepting;
    private readonly ConcurrentDictionary<Session, bool> _sessions = new();

    private DapServer(TcpListener listener, Action<string> log, Users users, Machine machine, TelnetServer? terminals)
    {
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _accepting = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }
                var session = new Session(client, log, users, machine, terminals);
                _sessions[session] = true;
                new Thread(() =>
                {
                    try
                    {
                        session.Serve();
                    }
                    finally
                    {
                        _sessions.TryRemove(session, out _);
                    }
                }) { IsBackground = true, Name = "debug session" }.Start();
            }
        });
    }

    public int Port { get; }

    /// <summary>
    /// Listens for debuggers. With <paramref name="terminals"/> (the 5250 server), a launched program with screens runs in
    /// the user's 5250 session; without it, every launched program runs in a job of its own.
    /// </summary>
    public static Task<DapServer> StartAsync(int port, Action<string> log, IPAddress bind, Users users, Machine machine, TelnetServer? terminals = null)
    {
        var listener = new TcpListener(bind, port);
        listener.Start();
        return Task.FromResult(new DapServer(listener, log, users, machine, terminals));
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        foreach (var session in _sessions.Keys) session.Close();
        await _accepting;
    }

    /// <summary>A launched program's job: no display, DSPLY to the debug console.</summary>
    private sealed class DebugJob(string user, CancellationToken cancelled, Action<string> output) : IJobHost
    {
        public string User => user;
        public CancellationToken Cancelled => cancelled;
        public IWorkstation? Workstation => null;
        public JobState State { get; } = new("QDBGJOB");

        public string? Dsply(string message, int replyLength)
        {
            output($"DSPLY  {message}\n");
            return null;
        }
    }

    /// <summary>Something a debugger can open up: a scope, a structure or an array, in a call of a program.</summary>
    private sealed record Handle(Interpreter Program, object Frame, Func<List<(string Name, Cell Cell)>> Children, Func<string, string> Path)
    {
        public Dictionary<string, Cell> Cells { get; } = new(StringComparer.Ordinal);
    }

    private sealed class Session(TcpClient client, Action<string> log, Users users, Machine machine, TelnetServer? terminals) : DebugSession
    {
        private readonly Stream _stream = client.GetStream();
        private readonly Lock _sending = new();
        private readonly ConcurrentDictionary<int, DebugThread> _threads = new();
        private readonly List<Handle> _handles = [];
        private readonly List<string> _sources = [];
        private readonly Dictionary<(string Library, string Name), int> _sourceIds = [];
        private readonly Dictionary<string, List<Breakpoint>> _byPath = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<(string? Library, string Path)>> _paths = new(StringComparer.Ordinal);
        private readonly CancellationTokenSource _jobEnd = new();
        private int _sequence, _ids;
        private string? _user, _attachedTo, _sourceRoot;
        private JsonObject? _launch;
        private DebugJob? _job;
        // The job of the 5250 session a launched program runs in, once the session has taken it.
        private volatile JobState? _sessionJob;
        private volatile bool _closed;
        private bool _added;

        public volatile bool StopOnEntry;

        public override bool Owns(IJobHost host) =>
            _sessionJob is { } inSession ? ReferenceEquals(host.State, inSession)
            : _job is not null ? ReferenceEquals(host, _job) : _attachedTo is { } user && (user == "*ALL" || host.User.Equals(user, StringComparison.OrdinalIgnoreCase));

        // ---- what the engine says ----

        public override void OnStopped(DebugThread thread, string reason, string? description, int? breakpoint)
        {
            _threads[thread.Id] = thread;
            var body = new JsonObject { ["reason"] = reason, ["threadId"] = thread.Id, ["allThreadsStopped"] = false };
            if (description is not null) (body["description"], body["text"]) = (description, description);
            if (breakpoint is { } id) body["hitBreakpointIds"] = new JsonArray(id);
            Event("stopped", body);
        }

        public override void OnThread(DebugThread thread, bool started)
        {
            if (started)
            {
                _threads[thread.Id] = thread;
                if (StopOnEntry)
                {
                    StopOnEntry = false;
                    thread.PauseRequested = true;
                }
            }
            else _threads.TryRemove(thread.Id, out _);
            Event("thread", new JsonObject { ["reason"] = started ? "started" : "exited", ["threadId"] = thread.Id });
        }

        public override void OnOutput(string text) => Event("output", new JsonObject { ["category"] = "console", ["output"] = text });

        // ---- the wire ----

        private void Send(JsonObject message)
        {
            if (_closed) return;
            lock (_sending)
            {
                message["seq"] = ++_sequence;
                var body = Encoding.UTF8.GetBytes(message.ToJsonString());
                try
                {
                    _stream.Write(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
                    _stream.Write(body);
                    _stream.Flush();
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException)
                {
                    _closed = true;
                }
            }
        }

        private void Event(string name, JsonObject? body = null) => Send(new JsonObject { ["type"] = "event", ["event"] = name, ["body"] = body ?? [] });

        private JsonObject? Read()
        {
            var length = -1;
            var line = new StringBuilder();
            while (true)
            {
                var b = _stream.ReadByte();
                if (b < 0) return null;
                if (b != '\n')
                {
                    if (b != '\r') line.Append((char)b);
                    continue;
                }
                if (line.Length == 0)
                {
                    if (length < 0) continue;
                    break;
                }
                if (line.ToString().StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) && int.TryParse(line.ToString()[15..].Trim(), out var n)) length = n;
                line.Clear();
            }
            if (length is < 0 or > 16 * 1024 * 1024) return null;
            var body = new byte[length];
            _stream.ReadExactly(body);
            return JsonNode.Parse(body) as JsonObject;
        }

        public void Close()
        {
            _closed = true;
            client.Close();
        }

        public void Serve()
        {
            try
            {
                while (!_closed && Read() is { } request)
                {
                    if (request["type"]?.GetValue<string>() != "request") continue;
                    var command = request["command"]?.GetValue<string>() ?? "";
                    var response = new JsonObject { ["type"] = "response", ["request_seq"] = request["seq"]?.GetValue<int>() ?? 0, ["command"] = command, ["success"] = true };
                    Action? after = null;
                    try
                    {
                        if (_user is null && command is not ("initialize" or "launch" or "attach" or "disconnect")) throw new InvalidOperationException("Sign on first: the launch or attach request carries the user and password.");
                        response["body"] = Handle(command, request["arguments"] as JsonObject ?? [], out after);
                    }
                    catch (Exception e) when (e is not OutOfMemoryException)
                    {
                        response["success"] = false;
                        response["message"] = e.Message;
                        response["body"] = new JsonObject { ["error"] = new JsonObject { ["id"] = 1, ["format"] = e.Message, ["showUser"] = command is "launch" or "attach" } };
                    }
                    Send(response);
                    after?.Invoke();
                    if (command == "disconnect") break;
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException or JsonException)
            {
            }
            finally
            {
                // The debugger is gone: the program it launched ends, and the jobs it stopped go on.
                _jobEnd.Cancel();
                if (_added) machine.Debugger.Remove(this, _threads.Values);
                if (_user is not null) log($"debug: {_user}'s session ended");
                Close();
            }
        }

        // ---- requests ----

        private static string? Text(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : o[name]?.ToString();
        private static int Number(JsonObject o, string name, int otherwise = 0) => o[name] is JsonValue v && v.TryGetValue<int>(out var n) ? n : otherwise;
        private static bool Flag(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

        private JsonNode? Handle(string command, JsonObject arguments, out Action? after)
        {
            after = null;
            switch (command)
            {
                case "initialize":
                    return new JsonObject
                    {
                        ["supportsConfigurationDoneRequest"] = true, ["supportsConditionalBreakpoints"] = true, ["supportsHitConditionalBreakpoints"] = true,
                        ["supportsLogPoints"] = true, ["supportsEvaluateForHovers"] = true, ["supportsSetVariable"] = true, ["supportsDataBreakpoints"] = true,
                        ["supportsTerminateRequest"] = true,
                        ["exceptionBreakpointFilters"] = new JsonArray(new JsonObject
                        {
                            ["filter"] = "errors", ["label"] = "Errors that end the program", ["default"] = true,
                            ["description"] = "Stop where an RPG error (a decimal data error, an array index out of range, a file error with no handler) is about to end the program.",
                        }),
                    };
                case "launch" or "attach":
                {
                    var user = Text(arguments, "user") ?? "";
                    var (result, profile) = users.Check(user, Text(arguments, "password") ?? "");
                    if (result != SignOnResult.SignedOn || profile is null)
                    {
                        log($"debug: sign-on by {user.ToUpperInvariant()} refused ({result})");
                        throw new InvalidOperationException($"Sim i didn't sign {user.ToUpperInvariant()} on: the user or password isn't right, or the profile can't sign on.");
                    }
                    _user = profile.User;
                    _sourceRoot = Text(arguments, "sourceRoot")?.TrimEnd('/', '\\');
                    foreach (var file in arguments["files"] as JsonArray ?? [])
                    {
                        if (file?.GetValue<string>() is { Length: > 0 } relative && _sourceRoot is not null) Remember(_sourceRoot + Separator + relative.Replace('/', Separator).Replace('\\', Separator));
                    }
                    BreakOnError = true;
                    if (command == "launch")
                    {
                        _launch = arguments;
                        _job = new DebugJob(_user, _jobEnd.Token, OnOutput);
                        StopOnEntry = Flag(arguments, "stopOnEntry");
                    }
                    else _attachedTo = (Text(arguments, "jobUser") is { Length: > 0 } other ? other : _user).ToUpperInvariant();
                    machine.Debugger.Add(this);
                    _added = true;
                    log($"debug: {_user} {(command == "launch" ? "launches a program" : $"attached to {_attachedTo}'s jobs")}");
                    // The debugger now sends its breakpoints, and then says it's done.
                    after = () => Event("initialized");
                    return null;
                }
                case "configurationDone":
                    if (_launch is { } launch) after = () => Launch(launch);
                    else after = () => OnOutput($"Attached to {_attachedTo}'s jobs on Sim i: they stop at breakpoints from now on. Run the program in a 5250 session.\n");
                    return null;
                case "setBreakpoints":
                    return SetBreakpoints(arguments);
                case "setExceptionBreakpoints":
                    BreakOnError = (arguments["filters"] as JsonArray ?? []).Any(f => f?.GetValue<string>() == "errors");
                    return null;
                case "dataBreakpointInfo":
                    return DataBreakpointInfo(arguments);
                case "setDataBreakpoints":
                {
                    var set = new List<DataBreakpoint>();
                    var answer = new JsonArray();
                    foreach (var item in (arguments["breakpoints"] as JsonArray ?? []).OfType<JsonObject>())
                    {
                        var parts = (Text(item, "dataId") ?? "").Split('\u0001');
                        if (parts.Length != 2)
                        {
                            answer.Add(new JsonObject { ["verified"] = false, ["message"] = "This value can't be watched." });
                            continue;
                        }
                        var watch = new DataBreakpoint { Id = ++_ids, Program = parts[0], Expression = parts[1], Condition = Text(item, "condition") };
                        set.Add(watch);
                        answer.Add(new JsonObject { ["verified"] = true, ["id"] = watch.Id });
                    }
                    DataBreakpoints = [.. set];
                    return new JsonObject { ["breakpoints"] = answer };
                }
                case "threads":
                    return new JsonObject
                    {
                        ["threads"] = new JsonArray([.. _threads.Values.OrderBy(t => t.Id).Select(t => (JsonNode)new JsonObject { ["id"] = t.Id, ["name"] = t.Name })]),
                    };
                case "stackTrace":
                {
                    var frames = new JsonArray();
                    if (_threads.TryGetValue(Number(arguments, "threadId"), out var thread) && thread.Stopped)
                    {
                        for (var i = thread.Stack.Count - 1; i >= 0; i--)
                        {
                            var entry = thread.Stack[i];
                            var procedure = entry.Program.DebugProcedure(entry.Frame);
                            frames.Add(new JsonObject
                            {
                                ["id"] = thread.Id * 10000 + i, ["name"] = procedure is null ? entry.Program.DebugName : $"{entry.Program.DebugName}.{procedure}",
                                ["line"] = entry.Line, ["column"] = 1, ["source"] = SourceOf(entry.Program),
                            });
                        }
                    }
                    return new JsonObject { ["stackFrames"] = frames, ["totalFrames"] = frames.Count };
                }
                case "scopes":
                {
                    var (program, frame) = FrameOf(Number(arguments, "frameId"));
                    var scopes = new JsonArray();
                    void Scope(string name, Func<IEnumerable<KeyValuePair<string, Cell>>> variables, bool expensive = false) => scopes.Add(new JsonObject
                    {
                        ["name"] = name, ["expensive"] = expensive,
                        ["variablesReference"] = NewHandle(new Handle(program, frame, () => [.. variables().Select(v => (v.Key, v.Value))], name => name)),
                    });
                    if (program.DebugProcedure(frame) is { } procedure) Scope($"Local ({procedure})", () => program.DebugLocals(frame));
                    Scope($"Program ({program.DebugName})", program.DebugGlobals);
                    Scope("Indicators", program.DebugIndicators, expensive: true);
                    return new JsonObject { ["scopes"] = scopes };
                }
                case "variables":
                {
                    var handle = HandleOf(Number(arguments, "variablesReference"));
                    var variables = new JsonArray();
                    foreach (var (name, cell) in handle.Children())
                    {
                        handle.Cells[name] = cell;
                        variables.Add(Variable(name, cell, handle, handle.Path(name)));
                    }
                    return new JsonObject { ["variables"] = variables };
                }
                case "evaluate":
                    return Evaluate(arguments);
                case "setVariable":
                {
                    var handle = HandleOf(Number(arguments, "variablesReference"));
                    var name = Text(arguments, "name") ?? "";
                    if (!handle.Cells.TryGetValue(name, out var cell)) throw new InvalidOperationException($"{name} isn't a variable here.");
                    Assign(cell, Text(arguments, "value") ?? "", handle.Program, handle.Frame);
                    return Variable(name, cell, handle, handle.Path(name), forSet: true);
                }
                case "continue" or "next" or "stepIn" or "stepOut":
                    if (_threads.TryGetValue(Number(arguments, "threadId"), out var resumed) && resumed.Stopped)
                        DebugEngine.Resume(resumed, command switch { "next" => StepMode.Over, "stepIn" => StepMode.Into, "stepOut" => StepMode.Out, _ => StepMode.None });
                    return command == "continue" ? new JsonObject { ["allThreadsContinued"] = false } : null;
                case "pause":
                    if (_threads.TryGetValue(Number(arguments, "threadId"), out var paused)) paused.PauseRequested = true;
                    return null;
                case "source":
                {
                    var id = Number(arguments, "sourceReference", Number(arguments["source"] as JsonObject ?? [], "sourceReference"));
                    if (id < 1 || id > _sources.Count) throw new InvalidOperationException("There's no such source.");
                    return new JsonObject { ["content"] = _sources[id - 1] };
                }
                case "terminate":
                    _jobEnd.Cancel();
                    return null;
                case "disconnect":
                    return null;
                default:
                    throw new InvalidOperationException($"Sim i's debugger doesn't do {command}.");
            }
        }

        // ---- launching ----

        private void Launch(JsonObject launch)
        {
            var program = Text(launch, "program") is { Length: > 0 } named ? named : "";
            // A source file's path names its program.
            if (program.Length == 0 || program.Contains('.') || program.Contains('\\') || program.Count(c => c == '/') > 1)
            {
                var (library, name) = MemberOf(program.Length > 0 ? program : Text(launch, "sourceFile") ?? "");
                program = library is null ? name : $"{library}/{name}";
            }
            if (program.Length == 0)
            {
                OnOutput("There's no program to call: name it in the launch configuration (\"program\": \"MYLIB/MYPGM\"), or open its source.\n");
                Event("terminated");
                return;
            }
            var parameters = string.Join(' ', (launch["parms"] as JsonArray ?? []).Select(p =>
                p is JsonValue v && v.TryGetValue<decimal>(out var number) ? number.ToString(CultureInfo.InvariantCulture) : $"'{p?.ToString().Replace("'", "''")}'"));
            var command = $"CALL PGM({program.ToUpperInvariant()}){(parameters.Length > 0 ? $" PARM({parameters})" : "")}";
            if (InSession(launch, program))
            {
                LaunchInSession(command);
                return;
            }
            new Thread(() =>
            {
                OnOutput($"{command}\n");
                var code = 0;
                try
                {
                    var result = machine.Run(command, _job!);
                    if (result.Message.Length > 0) Event("output", new JsonObject { ["category"] = result.Ok ? "console" : "stderr", ["output"] = result.Message + "\n" });
                    code = result.Ok ? 0 : 1;
                }
                catch (OperationCanceledException)
                {
                    OnOutput("The program was ended.\n");
                }
                catch (Exception e)
                {
                    Event("output", new JsonObject { ["category"] = "stderr", ["output"] = $"The program ended: {e.Message}\n" });
                    code = 1;
                }
                Event("exited", new JsonObject { ["exitCode"] = code });
                Event("terminated");
            }) { IsBackground = true, Name = "debugged program" }.Start();
        }

        /// <summary>Whether the program runs in the user's 5250 session: as the launch request says, or else when it has a display file.</summary>
        private bool InSession(JsonObject launch, string program)
        {
            if (terminals is null) return false;
            if (launch["terminal"] is JsonValue said && said.TryGetValue<bool>(out var terminal)) return terminal;
            var slash = program.IndexOf('/');
            try
            {
                var files = slash < 0 ? machine.FilesOf(null, program) : machine.FilesOf(program[..slash], program[(slash + 1)..]);
                return files?.Any(f => !f.Database && f.Use == "display file") == true;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // Source that can't be read for its files: the program runs as one without screens.
                return false;
            }
        }

        /// <summary>
        /// Runs the program in the user's 5250 session that waits at a command line, as though CALL were typed there, and
        /// debugs that session's job until the program ends. With no such session, it asks VS Code to open the terminal
        /// (the simI.needTerminal event) and waits for one.
        /// </summary>
        private void LaunchInSession(string command)
        {
            new Thread(() =>
            {
                var code = 0;
                try
                {
                    var run = terminals!.RunInSessionAsync(_user!, command, job => _sessionJob = job, TimeSpan.Zero, _jobEnd.Token).GetAwaiter().GetResult();
                    if (run is null)
                    {
                        OnOutput($"{command}\nIt has screens, so it runs in a 5250 session: waiting for one signed on as {_user} at a command line. Open the terminal and sign on.\n");
                        Event("simI.needTerminal", new JsonObject { ["user"] = _user });
                        run = terminals.RunInSessionAsync(_user!, command, job => _sessionJob = job, SessionWait, _jobEnd.Token).GetAwaiter().GetResult();
                    }
                    if (run is null)
                    {
                        Event("output", new JsonObject { ["category"] = "stderr", ["output"] = $"No 5250 session signed on as {_user} came to a command line: the program wasn't called.\n" });
                        code = 1;
                    }
                    else
                    {
                        log($"debug: {_user} runs {command} in their session on {run.Device}");
                        OnOutput($"{command}\nRunning in {_user}'s 5250 session on {run.Device}: its screens are there.\n");
                        using var ending = _jobEnd.Token.Register(run.Cancel);
                        var (ok, message) = run.Ended.GetAwaiter().GetResult();
                        if (_jobEnd.IsCancellationRequested) OnOutput("The program was ended.\n");
                        else if (message is { Length: > 0 }) Event("output", new JsonObject { ["category"] = ok ? "console" : "stderr", ["output"] = message + "\n" });
                        code = ok ? 0 : 1;
                    }
                }
                catch (OperationCanceledException)
                {
                }
                Event("exited", new JsonObject { ["exitCode"] = code });
                Event("terminated");
            }) { IsBackground = true, Name = "debugged program" }.Start();
        }

        /// <summary>How long a launch waits for a 5250 session to come to a command line.</summary>
        private static readonly TimeSpan SessionWait = TimeSpan.FromMinutes(10);

        // ---- source files and programs ----

        private char Separator => _sourceRoot is { } root && root.Contains('\\') && !root.Contains('/') ? '\\' : '/';

        /// <summary>
        /// The member a source file is: its name without the extension, and (when the file is in a folder
        /// under the source root, laid out LIBRARY/FILE/member.ext or LIBRARY/member.ext) its library.
        /// </summary>
        private (string? Library, string Name) MemberOf(string path)
        {
            var normal = path.Replace('\\', '/');
            var name = System.IO.Path.GetFileNameWithoutExtension(normal).ToUpperInvariant();
            string? library = null;
            if (_sourceRoot is { } root && normal.StartsWith(root.Replace('\\', '/') + "/", StringComparison.OrdinalIgnoreCase))
            {
                var parts = normal[(root.Length + 1)..].Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && Store.IsLibraryName(parts[0].ToUpperInvariant())) library = parts[0].ToUpperInvariant();
            }
            return (library, name);
        }

        private void Remember(string path)
        {
            var (library, name) = MemberOf(path);
            if (name.Length == 0) return;
            if (!_paths.TryGetValue(name, out var known)) _paths[name] = known = [];
            if (!known.Any(k => k.Path == path)) known.Add((library, path));
        }

        /// <summary>A program's source for the debugger: the file it knows the program by, or else the source the program was made from.</summary>
        private JsonObject SourceOf(Interpreter program)
        {
            var (name, library) = (program.DebugName, program.DebugLibrary);
            if (_paths.TryGetValue(name, out var known) && known.Count > 0)
            {
                var path = known.FirstOrDefault(k => k.Library == library).Path ?? known.FirstOrDefault(k => k.Library is null).Path ?? known[0].Path;
                return new JsonObject { ["name"] = path[(path.LastIndexOfAny(['/', '\\']) + 1)..], ["path"] = path };
            }
            if (!_sourceIds.TryGetValue((library, name), out var id))
            {
                var made = new[] { "*PGM", "*SRVPGM", "*MODULE" }.Select(type => machine.Store.FindObject(library.Length > 0 ? library : null, name, type)).FirstOrDefault(o => o is not null);
                _sources.Add(made?.Source ?? $"The source of {name} isn't on Sim i.");
                _sourceIds[(library, name)] = id = _sources.Count;
            }
            return new JsonObject { ["name"] = $"{name} (as compiled on Sim i)", ["sourceReference"] = id };
        }

        private JsonObject SetBreakpoints(JsonObject arguments)
        {
            var path = Text(arguments["source"] as JsonObject ?? [], "path") ?? "";
            Remember(path);
            var (library, name) = MemberOf(path);
            var made = new[] { "*PGM", "*SRVPGM", "*MODULE" }.Any(type => machine.Store.FindObject(library, name, type) is not null || machine.Store.FindObject(null, name, type) is not null);
            var set = new List<Breakpoint>();
            var answer = new JsonArray();
            foreach (var item in (arguments["breakpoints"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var breakpoint = new Breakpoint
                {
                    Id = ++_ids, Program = name, Library = library, Line = Number(item, "line"), Condition = Text(item, "condition"),
                    HitCount = int.TryParse(Text(item, "hitCondition")?.Trim().TrimStart('>', '=', ' '), out var hits) ? hits : 0, LogMessage = Text(item, "logMessage"),
                };
                string? problem = null;
                if (breakpoint.Condition is { Length: > 0 } condition)
                {
                    try
                    {
                        RpgParser.ParseExpression(condition);
                    }
                    catch (Exception e) when (e is not OutOfMemoryException)
                    {
                        problem = $"The condition isn't an RPG expression: {e.Message}";
                    }
                }
                problem ??= made ? null : $"{name} isn't a program on Sim i yet: save its source to compile it there.";
                set.Add(breakpoint);
                var answered = new JsonObject { ["id"] = breakpoint.Id, ["verified"] = problem is null, ["line"] = breakpoint.Line };
                if (problem is not null) answered["message"] = problem;
                answer.Add(answered);
            }
            _byPath[path] = set;
            Breakpoints = _byPath.Values.SelectMany(b => b).GroupBy(b => b.Program, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
            return new JsonObject { ["breakpoints"] = answer };
        }

        // ---- variables ----

        private (Interpreter Program, object Frame) FrameOf(int frameId)
        {
            if (!_threads.TryGetValue(frameId / 10000, out var thread) || !thread.Stopped || frameId % 10000 >= thread.Stack.Count)
                throw new InvalidOperationException("The job isn't stopped there any more.");
            var entry = thread.Stack[frameId % 10000];
            return (entry.Program, entry.Frame);
        }

        private int NewHandle(Handle handle)
        {
            lock (_handles)
            {
                _handles.Add(handle);
                return _handles.Count;
            }
        }

        private Handle HandleOf(int reference)
        {
            lock (_handles) return reference >= 1 && reference <= _handles.Count ? _handles[reference - 1] : throw new InvalidOperationException("That value isn't there any more.");
        }

        /// <summary>A handle to open a structure or an array up; 0 for a single value.</summary>
        private int Open(Cell cell, Interpreter program, object frame, string path)
        {
            if (DebugValues.Children(cell).Count == 0) return 0;
            var qualified = cell.Type.Kind != RpgKind.Ds || cell.Type.Ds is { Qualified: true };
            // An array's element is name(3); a structure's subfield is name.sub when it's qualified, and sub alone when it isn't.
            return NewHandle(new Handle(program, frame, () => DebugValues.Children(cell),
                child => child.StartsWith('(') ? path + child : qualified ? $"{path}.{child}" : child));
        }

        private JsonObject Variable(string name, Cell cell, Handle handle, string path, bool forSet = false)
        {
            var variable = new JsonObject
            {
                ["value"] = DebugValues.Text(cell), ["type"] = DebugValues.TypeName(cell.Type),
                ["variablesReference"] = Open(cell, handle.Program, handle.Frame, path),
            };
            if (!forSet) (variable["name"], variable["evaluateName"]) = (name, path);
            return variable;
        }

        private JsonObject DataBreakpointInfo(JsonObject arguments)
        {
            var name = Text(arguments, "name") ?? "";
            Interpreter program;
            string expression;
            if (Number(arguments, "variablesReference") is > 0 and var reference)
            {
                var handle = HandleOf(reference);
                (program, expression) = (handle.Program, handle.Path(name));
            }
            else if (arguments["frameId"] is not null) (program, expression) = (FrameOf(Number(arguments, "frameId")).Program, name);
            else return new JsonObject { ["dataId"] = null, ["description"] = "Stop the program first: a value is watched in the program it's in." };
            return new JsonObject
            {
                ["dataId"] = $"{program.DebugName}\u0001{expression}", ["description"] = $"{expression} in {program.DebugName}",
                ["accessTypes"] = new JsonArray("write"), ["canPersist"] = true,
            };
        }

        private JsonObject Evaluate(JsonObject arguments)
        {
            var expression = (Text(arguments, "expression") ?? "").Trim();
            if (arguments["frameId"] is null) throw new InvalidOperationException("Stop the program first: an expression is worked out where it's stopped.");
            var (program, frame) = FrameOf(Number(arguments, "frameId"));
            // The debug console takes IBM i's EVAL: EVAL name shows a value, EVAL name = value changes it.
            if (Text(arguments, "context") == "repl" && expression.StartsWith("eval ", StringComparison.OrdinalIgnoreCase))
            {
                expression = expression[5..].Trim();
                var equals = AssignmentAt(expression);
                if (equals > 0)
                {
                    var target = expression[..equals].Trim();
                    Assign(program.DebugLocate(target, frame), expression[(equals + 1)..].Trim(), program, frame);
                    expression = target;
                }
            }
            object value;
            try
            {
                value = program.DebugEvaluate(expression, frame);
            }
            catch (Exception e) when (e is RpgError or RpgHalt or FormatException or InvalidCastException or RpgUnsupported or KeyNotFoundException or ArgumentException)
            {
                throw new InvalidOperationException(e.Message);
            }
            var result = new JsonObject { ["result"] = DebugValues.Text(value), ["variablesReference"] = 0 };
            Cell? cell = value as Cell;
            if (cell is null)
            {
                // A single value: its type comes from the variable, when the expression names one.
                try
                {
                    cell = program.DebugLocate(expression, frame);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    cell = null;
                }
            }
            if (cell is not null)
            {
                result["type"] = DebugValues.TypeName(cell.Type);
                result["variablesReference"] = Open(cell, program, frame, expression);
            }
            return result;
        }

        /// <summary>Where the = of an assignment is in a debug console line; 0 when it's a comparison or has none.</summary>
        private static int AssignmentAt(string text)
        {
            var quoted = false;
            var depth = 0;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '\'') quoted = !quoted;
                else if (!quoted && c == '(') depth++;
                else if (!quoted && c == ')') depth--;
                else if (!quoted && depth == 0 && c == '=' && i > 0 && text[i - 1] is not ('<' or '>')) return i;
            }
            return 0;
        }

        /// <summary>Gives a variable a value written as RPG writes it: a number, 'characters', *ON, another variable, an expression.</summary>
        private static void Assign(Cell cell, string text, Interpreter program, object frame)
        {
            object value;
            try
            {
                value = program.DebugEvaluate(text, frame);
            }
            catch (Exception e) when (e is RpgError or RpgHalt or FormatException or RpgUnsupported)
            {
                // Characters typed without their quotes.
                if (cell.Type.IsNumeric) throw new InvalidOperationException($"{text} isn't a number or an expression: {e.Message}");
                value = text;
            }
            try
            {
                cell.Set(value is Cell whole ? whole.Get() : value);
            }
            catch (Exception e) when (e is RpgError or InvalidCastException or OverflowException or FormatException)
            {
                throw new InvalidOperationException($"The value doesn't fit the variable: {e.Message}");
            }
        }
    }
}
