using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using SimI.Debugging;

namespace SimI.Tests;

/// <summary>The debug server: a debugger (as VS Code is one) sets breakpoints, steps, and reads and changes variables.</summary>
public class DebugTests
{
    // Line 1 is **FREE; the numbers at the right are the lines the tests name.
    private const string Program = """
        **FREE
        dcl-s total packed(7:2);
        dcl-s i int(10);
        dcl-s arr int(10) dim(3);
        dcl-ds cust qualified;
          no packed(5);
          name char(10);
        end-ds;
        dcl-pi *n;
          start packed(15:5);
        end-pi;
        total = start;
        for i = 1 to 3;
          total += twice(i);
          arr(i) = i * 10;
        endfor;
        cust.no = 7;
        cust.name = 'Acme';
        dsply (%char(total));
        *inlr = *on;
        return;
        dcl-proc twice;
          dcl-pi *n packed(7:2);
            n int(10) const;
          end-pi;
          dcl-s doubled packed(7:2);
          doubled = n * 2;
          return doubled;
        end-proc;
        """;

    private const int AddLine = 14, ArrLine = 15, CustLine = 17, DsplyLine = 19, DoubledLine = 27;
    private const string Path = "/work/src/QGPL/QRPGLESRC/DBGPGM.sqlrpgle";

    /// <summary>A debugger's end of the connection.</summary>
    private sealed class Debugger : IDisposable
    {
        private readonly TcpClient _client;
        private readonly Stream _stream;
        private readonly List<JsonObject> _events = [];
        private int _sequence;

        public Debugger(int port)
        {
            _client = new TcpClient("127.0.0.1", port) { ReceiveTimeout = 20000 };
            _stream = _client.GetStream();
        }

        private JsonObject Read()
        {
            var header = new StringBuilder();
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                var b = _stream.ReadByte();
                if (b < 0) throw new IOException("the debug server closed the connection");
                header.Append((char)b);
            }
            var body = new byte[int.Parse(header.ToString().Split(':')[1].Trim())];
            _stream.ReadExactly(body);
            return (JsonObject)JsonNode.Parse(body)!;
        }

        /// <summary>Sends a request and returns its response; events that come first are kept.</summary>
        public JsonObject Request(string command, JsonObject? arguments = null, bool fails = false)
        {
            var request = new JsonObject { ["seq"] = ++_sequence, ["type"] = "request", ["command"] = command, ["arguments"] = arguments ?? [] };
            var bytes = Encoding.UTF8.GetBytes(request.ToJsonString());
            _stream.Write(Encoding.ASCII.GetBytes($"Content-Length: {bytes.Length}\r\n\r\n"));
            _stream.Write(bytes);
            while (true)
            {
                var message = Read();
                if (message["type"]!.GetValue<string>() == "event")
                {
                    _events.Add(message);
                    continue;
                }
                Assert.Equal(command, message["command"]!.GetValue<string>());
                Assert.True(message["success"]!.GetValue<bool>() != fails, message.ToJsonString());
                return message["body"] as JsonObject ?? [];
            }
        }

        /// <summary>The next event of this name, and its body.</summary>
        public JsonObject Event(string name)
        {
            while (true)
            {
                if (_events.FindIndex(e => e["event"]!.GetValue<string>() == name) is >= 0 and var at)
                {
                    var found = _events[at];
                    _events.RemoveAt(at);
                    return (JsonObject)found["body"]!;
                }
                var message = Read();
                Assert.Equal("event", message["type"]!.GetValue<string>());
                _events.Add(message);
            }
        }

        /// <summary>Everything written to the debug console so far, up to and including the output that has this text.</summary>
        public string OutputUntil(string text)
        {
            var all = new StringBuilder();
            while (!all.ToString().Contains(text)) all.Append(Event("output")["output"]!.GetValue<string>());
            return all.ToString();
        }

        public JsonObject Start(string command, JsonObject arguments, params (int Line, string? Condition)[] breakpoints)
        {
            Request("initialize", new JsonObject { ["adapterID"] = "sim-i" });
            arguments["user"] ??= "PRTUSER";
            arguments["password"] ??= "Secret12";
            arguments["sourceRoot"] = "/work/src";
            Request(command, arguments);
            Event("initialized");
            var set = Breakpoints(breakpoints);
            Request("configurationDone");
            return set;
        }

        public JsonObject Breakpoints(params (int Line, string? Condition)[] breakpoints) => Request("setBreakpoints", new JsonObject
        {
            ["source"] = new JsonObject { ["path"] = Path },
            ["breakpoints"] = new JsonArray([.. breakpoints.Select(b => (JsonNode)new JsonObject { ["line"] = b.Line, ["condition"] = b.Condition })]),
        });

        /// <summary>Waits for a job to stop, and returns why, its thread and its top frame (id, name and line).</summary>
        public (string Reason, int Thread, int Frame, string Name, int Line, JsonObject Body) Stopped()
        {
            var body = Event("stopped");
            var thread = body["threadId"]!.GetValue<int>();
            var top = (JsonObject)Request("stackTrace", new JsonObject { ["threadId"] = thread })["stackFrames"]![0]!;
            return (body["reason"]!.GetValue<string>(), thread, top["id"]!.GetValue<int>(), top["name"]!.GetValue<string>(), top["line"]!.GetValue<int>(), body);
        }

        /// <summary>A frame's variables by scope: each scope's name, and its variables' values by name.</summary>
        public Dictionary<string, Dictionary<string, JsonObject>> Variables(int frame) =>
            Request("scopes", new JsonObject { ["frameId"] = frame })["scopes"]!.AsArray().Where(s => !s!["name"]!.GetValue<string>().StartsWith("Indicators")).ToDictionary(
                s => s!["name"]!.GetValue<string>().Split(' ')[0],
                s => Children(s!["variablesReference"]!.GetValue<int>()));

        public Dictionary<string, JsonObject> Children(int reference) =>
            Request("variables", new JsonObject { ["variablesReference"] = reference })["variables"]!.AsArray().ToDictionary(v => v!["name"]!.GetValue<string>(), v => (JsonObject)v!);

        public string Evaluate(string expression, int frame, string context = "watch") =>
            Request("evaluate", new JsonObject { ["expression"] = expression, ["frameId"] = frame, ["context"] = context })["result"]!.GetValue<string>();

        public void Go(int thread, string how = "continue") => Request(how, new JsonObject { ["threadId"] = thread });

        public void Dispose() => _client.Dispose();
    }

    private static async Task<(Machine Machine, DapServer Server)> Started()
    {
        var machine = Machine.Open();
        var member = new Member("QGPL", "QRPGLESRC", "DBGPGM", "SQLRPGLE", Program);
        machine.Save(member);
        var compiled = machine.Compile(member);
        Assert.True(compiled.Ok, compiled.Message);
        var server = await DapServer.StartAsync(0, _ => { }, IPAddress.Loopback, Users.Open(machine.Store, new Dictionary<string, string>()), machine);
        return (machine, server);
    }

    private static string Value(JsonObject variable) => variable["value"]!.GetValue<string>();

    [Fact]
    public async Task ABreakpointStopsALaunchedProgramAndShowsItsVariables()
    {
        var (machine, server) = await Started();
        await using var _ = server;
        using var __ = machine;
        using var debugger = new Debugger(server.Port);
        var set = debugger.Start("launch", new JsonObject { ["program"] = "QGPL/DBGPGM", ["parms"] = new JsonArray(100) }, (AddLine, null));
        Assert.True(set["breakpoints"]![0]!["verified"]!.GetValue<bool>());

        var stop = debugger.Stopped();
        Assert.Equal(("breakpoint", "DBGPGM", AddLine), (stop.Reason, stop.Name, stop.Line));
        var variables = debugger.Variables(stop.Frame);
        Assert.Equal(["Program"], variables.Keys);
        Assert.Equal("100.00", Value(variables["Program"]["TOTAL"]));
        Assert.Equal("packed(7:2)", variables["Program"]["TOTAL"]["type"]!.GetValue<string>());
        Assert.Equal("1", Value(variables["Program"]["I"]));
        Assert.Equal("3 elements", Value(variables["Program"]["ARR"]));

        // Into the procedure: its own variables, and the call it came from below it.
        debugger.Go(stop.Thread, "stepIn");
        var inside = debugger.Stopped();
        Assert.Equal(("step", "DBGPGM.TWICE", DoubledLine), (inside.Reason, inside.Name, inside.Line));
        var local = debugger.Variables(inside.Frame);
        Assert.Equal("1", Value(local["Local"]["N"]));
        Assert.Equal("0.00", Value(local["Local"]["DOUBLED"]));
        Assert.Equal("100.00", Value(local["Program"]["TOTAL"]));
        var frames = debugger.Request("stackTrace", new JsonObject { ["threadId"] = inside.Thread })["stackFrames"]!.AsArray();
        Assert.Equal(["DBGPGM.TWICE", "DBGPGM"], frames.Select(f => f!["name"]!.GetValue<string>()));
        Assert.Equal(Path, frames[1]!["source"]!["path"]!.GetValue<string>());
        Assert.Equal(AddLine, frames[1]!["line"]!.GetValue<int>());

        // Out again, and over the next line.
        debugger.Go(inside.Thread, "stepOut");
        var back = debugger.Stopped();
        Assert.Equal(("DBGPGM", ArrLine), (back.Name, back.Line));
        Assert.Equal("102.00", debugger.Evaluate("total", back.Frame));
        debugger.Go(back.Thread, "next");
        var next = debugger.Stopped();
        Assert.Equal(AddLine, next.Line);
        // An array opens up into its elements; an expression is worked out where the program is.
        var program = debugger.Variables(next.Frame)["Program"];
        Assert.Equal("10", Value(debugger.Children(program["ARR"]["variablesReference"]!.GetValue<int>())["(1)"]));
        Assert.Equal("112.00", debugger.Evaluate("total + arr(1)", next.Frame));
        Assert.Equal("'1'", debugger.Evaluate("i = 2 and total > 100", next.Frame, "hover"));
        debugger.Request("evaluate", new JsonObject { ["expression"] = "nosuch", ["frameId"] = next.Frame }, fails: true);

        // No breakpoints: it runs to its end, and what it showed is in the console.
        debugger.Breakpoints();
        debugger.Go(next.Thread);
        Assert.Contains("DSPLY  112.00", debugger.OutputUntil("DSPLY"));
        Assert.Equal(0, debugger.Event("exited")["exitCode"]!.GetValue<int>());
        debugger.Event("terminated");
    }

    [Fact]
    public async Task ABreakpointsConditionAVariablesNewValueAndAWatchedValue()
    {
        var (machine, server) = await Started();
        await using var _ = server;
        using var __ = machine;
        using var debugger = new Debugger(server.Port);
        // Only the third time round the loop.
        debugger.Start("launch", new JsonObject { ["program"] = "DBGPGM", ["parms"] = new JsonArray(0) }, (ArrLine, "i = 3"));
        var stop = debugger.Stopped();
        Assert.Equal(("breakpoint", ArrLine), (stop.Reason, stop.Line));
        Assert.Equal("3", debugger.Evaluate("i", stop.Frame));
        Assert.Equal("12.00", debugger.Evaluate("total", stop.Frame));

        // A variable changed from the debugger, as the variables view and the console (EVAL) do it.
        var scope = debugger.Request("scopes", new JsonObject { ["frameId"] = stop.Frame })["scopes"]![0]!["variablesReference"]!.GetValue<int>();
        debugger.Children(scope);
        var changed = debugger.Request("setVariable", new JsonObject { ["variablesReference"] = scope, ["name"] = "TOTAL", ["value"] = "500" });
        Assert.Equal("500.00", changed["value"]!.GetValue<string>());
        Assert.Equal("'Zed'", debugger.Evaluate("eval cust.name = 'Zed'", stop.Frame, "repl"));
        debugger.Request("setVariable", new JsonObject { ["variablesReference"] = scope, ["name"] = "TOTAL", ["value"] = "'abc'" }, fails: true);

        // A watch on a subfield: the program stops after the statement that changes it.
        var cust = debugger.Children(scope)["CUST"]["variablesReference"]!.GetValue<int>();
        Assert.Equal("'Zed'", Value(debugger.Children(cust)["NAME"]));
        var info = debugger.Request("dataBreakpointInfo", new JsonObject { ["variablesReference"] = cust, ["name"] = "NO" });
        Assert.Equal("CUST.NO in DBGPGM", info["description"]!.GetValue<string>());
        debugger.Request("setDataBreakpoints", new JsonObject { ["breakpoints"] = new JsonArray(new JsonObject { ["dataId"] = info["dataId"]!.GetValue<string>() }) });
        debugger.Breakpoints();
        debugger.Go(stop.Thread);
        var watched = debugger.Stopped();
        Assert.Equal(("data breakpoint", CustLine + 1), (watched.Reason, watched.Line));
        Assert.Equal("CUST.NO changed from 0 to 7", watched.Body["description"]!.GetValue<string>());

        debugger.Request("setDataBreakpoints", new JsonObject { ["breakpoints"] = new JsonArray() });
        debugger.Go(watched.Thread);
        // The 500 it was given, the loop's adding being done by then.
        Assert.Contains("DSPLY  500.00", debugger.OutputUntil("DSPLY"));
        debugger.Event("terminated");
    }

    [Fact]
    public async Task AnErrorStopsTheProgramWhereItHappens()
    {
        var (machine, server) = await Started();
        await using var _ = server;
        using var __ = machine;
        var member = new Member("QGPL", "QRPGLESRC", "DBGERR", "SQLRPGLE", """
            **FREE
            dcl-s a int(10) dim(3);
            dcl-s i int(10) inz(4);
            a(i) = 1;
            return;
            """);
        machine.Save(member);
        Assert.True(machine.Compile(member).Ok);
        using var debugger = new Debugger(server.Port);
        debugger.Start("launch", new JsonObject { ["sourceFile"] = "/work/src/QGPL/QRPGLESRC/DBGERR.sqlrpgle" });
        var stop = debugger.Stopped();
        Assert.Equal(("exception", "DBGERR", 4), (stop.Reason, stop.Name, stop.Line));
        Assert.Equal("An array index is out of range.", stop.Body["description"]!.GetValue<string>());
        Assert.Equal("4", debugger.Evaluate("i", stop.Frame));
        debugger.Go(stop.Thread);
        Assert.Contains("ended in error at line 4", debugger.OutputUntil("ended in error"));
        Assert.Equal(1, debugger.Event("exited")["exitCode"]!.GetValue<int>());
    }

    [Fact]
    public async Task AttachedToAUsersJobsItStopsThemAndLetsThemGoWhenItLeaves()
    {
        var (machine, server) = await Started();
        await using var _ = server;
        using var __ = machine;
        using var debugger = new Debugger(server.Port);
        debugger.Start("attach", [], (DsplyLine, null));
        debugger.OutputUntil("Attached to PRTUSER's jobs");

        // The user's own job (a 5250 session's would be one) and another user's, which isn't debugged.
        var other = new BatchJob("EXPUSER", CancellationToken.None);
        Assert.True(machine.Run("CALL DBGPGM (1)", other).Ok);
        Assert.Equal(["13.00"], other.Messages);
        var job = new BatchJob("PRTUSER", CancellationToken.None);
        var running = Task.Run(() => machine.Run("CALL DBGPGM (1)", job));
        var stop = debugger.Stopped();
        Assert.Equal(("breakpoint", "DBGPGM", DsplyLine), (stop.Reason, stop.Name, stop.Line));
        Assert.Equal("'Acme'", debugger.Evaluate("cust.name", stop.Frame));
        Assert.Contains("PRTUSER", debugger.Request("threads")["threads"]![0]!["name"]!.GetValue<string>());
        Assert.False(running.IsCompleted);

        // The debugger leaves while the job is stopped: the job goes on to its end.
        debugger.Request("disconnect");
        Assert.True((await running.WaitAsync(TimeSpan.FromSeconds(20))).Ok);
        Assert.Equal(["13.00"], job.Messages);
        Assert.False(machine.Debugger.Active);
    }

    [Fact]
    public async Task NothingWithoutSigningOnAndABreakpointInWhatIsNotAProgramSaysSo()
    {
        var (machine, server) = await Started();
        await using var _ = server;
        using var __ = machine;
        using (var refused = new Debugger(server.Port))
        {
            refused.Request("initialize");
            refused.Request("threads", fails: true);
            refused.Request("attach", new JsonObject { ["user"] = "PRTUSER", ["password"] = "wrong" }, fails: true);
            refused.Request("setBreakpoints", new JsonObject { ["source"] = new JsonObject { ["path"] = Path }, ["breakpoints"] = new JsonArray() }, fails: true);
        }
        using var debugger = new Debugger(server.Port);
        debugger.Start("attach", []);
        var set = debugger.Request("setBreakpoints", new JsonObject
        {
            ["source"] = new JsonObject { ["path"] = "/work/src/QGPL/QRPGLESRC/NOTYET.rpgle" },
            ["breakpoints"] = new JsonArray(new JsonObject { ["line"] = 3 }),
        });
        Assert.False(set["breakpoints"]![0]!["verified"]!.GetValue<bool>());
        Assert.Contains("NOTYET isn't a program on Sim i yet", set["breakpoints"]![0]!["message"]!.GetValue<string>());
        var bad = debugger.Breakpoints((AddLine, "i = = 3"));
        Assert.Contains("isn't an RPG expression", bad["breakpoints"]![0]!["message"]!.GetValue<string>());
    }
}
