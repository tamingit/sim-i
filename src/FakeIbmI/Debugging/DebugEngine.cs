// The debugger's engine: what stops a running RPG program and shows what it holds. The
// interpreter tells it of every statement it's about to run; when a debug session is watching that
// job and the statement is at a breakpoint (or the job is stepping, or a watched value changed),
// the job's thread waits here until the session lets it go on. While it waits, the session reads
// the job's call stack and variables, which nothing else is changing.
//
// A session is one debugger's connection (see DapServer). It debugs the job it started, or every
// job of a user it attached to, such as that user's 5250 session.

using FakeIbmI.Rpg;

namespace FakeIbmI.Debugging;

/// <summary>A place a job stops: a line of a program, when its condition holds and it has been reached often enough.</summary>
internal sealed class Breakpoint
{
    public int Id { get; init; }

    /// <summary>The program (or module, or service program) by name; and its library, when the source's folder says it.</summary>
    public string Program { get; init; } = "";
    public string? Library { get; init; }
    public int Line { get; init; }

    /// <summary>An RPG expression that has to be true for the job to stop, or null.</summary>
    public string? Condition { get; init; }

    /// <summary>The job stops from this many times reaching the line on; 0 for every time.</summary>
    public int HitCount { get; init; }

    /// <summary>A message to write instead of stopping (a logpoint): {expression} is its value.</summary>
    public string? LogMessage { get; init; }

    public int Hits;
}

/// <summary>A watch on a value: the job stops when the expression's value changes in the program (and, with a condition, the condition holds).</summary>
internal sealed class DataBreakpoint
{
    public int Id { get; init; }
    public string Program { get; init; } = "";
    public string Expression { get; init; } = "";
    public string? Condition { get; init; }
    public string? Last;
}

/// <summary>A call on a job's stack: the program running, its frame (the mainline's or a procedure's), and the line it's at.</summary>
internal sealed class StackEntry(Interpreter program, object frame, int line)
{
    public Interpreter Program => program;
    public object Frame => frame;
    public int Line { get; set; } = line;
}

internal enum StepMode
{
    None,
    Into,
    Over,
    Out,
}

/// <summary>A job as the debugger knows it: its calls, and whether it's stopped.</summary>
internal sealed class DebugThread(DebugEngine engine, int id, IJobHost host)
{
    public DebugEngine Engine => engine;
    public int Id => id;
    public IJobHost Host { get; set; } = host;
    public List<StackEntry> Stack { get; } = [];
    public DebugSession? Session { get; set; }
    public bool Known { get; set; }
    public volatile bool Stopped;
    public volatile bool PauseRequested;
    public StepMode Mode;
    public int StepDepth;
    public ManualResetEventSlim Go { get; } = new(false);

    public string Name => $"{Host.State.Number}/{Host.User}/{Host.State.Name}";
}

/// <summary>A debugger's connection: which jobs it debugs, where they stop, and what it's told.</summary>
internal abstract class DebugSession
{
    private volatile Dictionary<string, Breakpoint[]> _breakpoints = new(StringComparer.Ordinal);

    /// <summary>The breakpoints, by program name. Replaced whole when they change, so a running job reads them without a lock.</summary>
    public Dictionary<string, Breakpoint[]> Breakpoints
    {
        get => _breakpoints;
        set => _breakpoints = value;
    }

    public volatile DataBreakpoint[] DataBreakpoints = [];

    /// <summary>Whether a job stops where an error is about to end its program.</summary>
    public volatile bool BreakOnError = true;

    /// <summary>Whether this session debugs the job.</summary>
    public abstract bool Owns(IJobHost host);

    public abstract void OnStopped(DebugThread thread, string reason, string? description, int? breakpoint);
    public abstract void OnThread(DebugThread thread, bool started);
    public abstract void OnOutput(string text);
}

internal sealed class DebugEngine
{
    private volatile DebugSession[] _sessions = [];
    private readonly Lock _gate = new();
    private int _threadIds;

    [ThreadStatic]
    private static DebugThread? _current;

    /// <summary>Whether any debugger is connected: when none is, a statement costs one test.</summary>
    public bool Active => _sessions.Length > 0;

    public void Add(DebugSession session)
    {
        lock (_gate) _sessions = [.. _sessions, session];
    }

    /// <summary>Ends a session: the jobs it stopped go on.</summary>
    public void Remove(DebugSession session, IEnumerable<DebugThread> threads)
    {
        lock (_gate) _sessions = [.. _sessions.Where(s => s != session)];
        foreach (var thread in threads)
        {
            thread.Mode = StepMode.None;
            thread.PauseRequested = false;
            thread.Session = null;
            thread.Known = false;
            thread.Go.Set();
        }
    }

    /// <summary>A program is called on this thread: what its stack holds now, to go back to when it returns.</summary>
    public int Enter() => _current is { } thread && thread.Engine == this ? thread.Stack.Count : 0;

    /// <summary>The program has returned: its calls are off the stack. A job whose last call returned is gone from its session.</summary>
    public void Leave(int mark)
    {
        if (_current is not { } thread || thread.Engine != this) return;
        if (thread.Stack.Count > mark) thread.Stack.RemoveRange(mark, thread.Stack.Count - mark);
        if (mark == 0 && thread.Known)
        {
            thread.Known = false;
            thread.Mode = StepMode.None;
            thread.Session?.OnThread(thread, started: false);
        }
    }

    private DebugThread Current(IJobHost host)
    {
        if (_current is not { } thread || thread.Engine != this) _current = thread = new DebugThread(this, Interlocked.Increment(ref _threadIds), host);
        thread.Host = host;
        return thread;
    }

    /// <summary>The session that debugs this job, if any.</summary>
    private DebugSession? SessionFor(DebugThread thread)
    {
        var sessions = _sessions;
        if (thread.Session is { } known && Array.IndexOf(sessions, known) >= 0 && known.Owns(thread.Host)) return known;
        thread.Session = sessions.FirstOrDefault(s => s.Owns(thread.Host));
        thread.Known = false;
        return thread.Session;
    }

    /// <summary>The interpreter is about to run a statement at this line.</summary>
    public void Statement(Interpreter program, object frame, int line)
    {
        var thread = Current(program.DebugHost);
        // The stack: this frame is on it (calls above it have returned), or it's a new call.
        var stack = thread.Stack;
        var at = stack.Count - 1;
        while (at >= 0 && !(ReferenceEquals(stack[at].Program, program) && ReferenceEquals(stack[at].Frame, frame))) at--;
        if (at < 0) stack.Add(new StackEntry(program, frame, line));
        else
        {
            if (at < stack.Count - 1) stack.RemoveRange(at + 1, stack.Count - at - 1);
            stack[at].Line = line;
        }
        if (SessionFor(thread) is not { } session) return;
        if (!thread.Known)
        {
            thread.Known = true;
            session.OnThread(thread, started: true);
        }

        string? reason = null, description = null;
        int? hit = null;
        // Watched values are looked at before every statement, so the one that changed them is the one just run.
        var watches = session.DataBreakpoints;
        if (watches.Length > 0)
        {
            foreach (var watch in watches)
            {
                if (watch.Program != program.DebugName) continue;
                string now;
                try
                {
                    now = DebugValues.Text(program.DebugEvaluate(watch.Expression, frame));
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Not in reach here (a procedure's local, seen from another procedure): it keeps its last value.
                    continue;
                }
                var before = watch.Last;
                watch.Last = now;
                if (before is null || before == now || reason is not null) continue;
                if (watch.Condition is { Length: > 0 } condition && !Holds(program, frame, condition, session)) continue;
                (reason, description, hit) = ("data breakpoint", $"{watch.Expression} changed from {before} to {now}", watch.Id);
            }
        }
        if (reason is null && thread.PauseRequested) reason = "pause";
        if (reason is null && thread.Mode != StepMode.None)
        {
            var depth = stack.Count;
            if (thread.Mode == StepMode.Into || thread.Mode == StepMode.Over && depth <= thread.StepDepth || thread.Mode == StepMode.Out && depth < thread.StepDepth) reason = "step";
        }
        if (session.Breakpoints.TryGetValue(program.DebugName, out var breakpoints))
        {
            foreach (var breakpoint in breakpoints)
            {
                if (breakpoint.Line != line || breakpoint.Library is { } library && program.DebugLibrary.Length > 0 && library != program.DebugLibrary) continue;
                if (breakpoint.Condition is { Length: > 0 } condition && !Holds(program, frame, condition, session)) continue;
                if (Interlocked.Increment(ref breakpoint.Hits) < breakpoint.HitCount) continue;
                if (breakpoint.LogMessage is { } message)
                {
                    session.OnOutput(Interpolated(program, frame, message) + "\n");
                    continue;
                }
                (reason, hit) = ("breakpoint", breakpoint.Id);
                description = null;
                break;
            }
        }
        if (reason is not null) Stop(thread, session, reason, description, hit);
    }

    /// <summary>An error is about to end the program: with a session that wants it, the job stops where it happened.</summary>
    public void Failed(Interpreter program, string message)
    {
        if (!Active || _current is not { } thread || thread.Engine != this || thread.Stack.Count == 0) return;
        if (SessionFor(thread) is not { BreakOnError: true } session) return;
        // The call the error is in is the deepest of this program's on the stack.
        var top = thread.Stack[^1];
        if (ReferenceEquals(top.Program, program)) top.Line = program.DebugLine;
        Stop(thread, session, "exception", message, null);
    }

    private void Stop(DebugThread thread, DebugSession session, string reason, string? description, int? breakpoint)
    {
        thread.Mode = StepMode.None;
        thread.PauseRequested = false;
        thread.Go.Reset();
        thread.Stopped = true;
        try
        {
            session.OnStopped(thread, reason, description, breakpoint);
            // Until the debugger says go, or the job is ended (its 5250 session closed, say).
            WaitHandle.WaitAny([thread.Go.WaitHandle, thread.Host.Cancelled.WaitHandle]);
        }
        finally
        {
            thread.Stopped = false;
        }
        thread.Host.Cancelled.ThrowIfCancellationRequested();
    }

    /// <summary>Lets a stopped job go on: freely, or to the next statement (into calls, over them, or out of this one).</summary>
    public static void Resume(DebugThread thread, StepMode mode)
    {
        thread.Mode = mode;
        thread.StepDepth = thread.Stack.Count;
        thread.Go.Set();
    }

    private static bool Holds(Interpreter program, object frame, string condition, DebugSession session)
    {
        try
        {
            return Values.ToBool(program.DebugEvaluate(condition, frame));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A condition that can't be worked out stops the job, so it's seen rather than silently never met.
            session.OnOutput($"The breakpoint condition {condition} can't be evaluated: {e.Message}\n");
            return true;
        }
    }

    /// <summary>A logpoint's message, with each {expression} as its value.</summary>
    private static string Interpolated(Interpreter program, object frame, string message) =>
        System.Text.RegularExpressions.Regex.Replace(message, @"\{([^{}]+)\}", m =>
        {
            try
            {
                return DebugValues.Text(program.DebugEvaluate(m.Groups[1].Value, frame));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return $"{{{m.Groups[1].Value}: {e.Message}}}";
            }
        });
}

/// <summary>Values as a debugger shows them.</summary>
internal static class DebugValues
{
    /// <summary>A value as text: characters quoted, without their trailing blanks; a structure as its bytes; an array as its size.</summary>
    public static string Text(object? value)
    {
        switch (value)
        {
            case null:
                return "";
            case string s:
                return $"'{s.TrimEnd(' ')}'";
            case bool b:
                return b ? "'1'" : "'0'";
            case decimal d:
                return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
            case double f:
                return f.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            case DsValue ds:
                return ds.Text.TrimEnd(' ') is var image && image.Length > 80 ? $"'{image[..80]}...'" : $"'{ds.Text.TrimEnd(' ')}'";
            case Cell { Elements: { } elements }:
                return $"{elements.Length} elements";
            case Cell cell:
                return Text(cell.Get());
        }
        try
        {
            return Values.ToText(value);
        }
        catch (RpgError)
        {
            return value.ToString() ?? "";
        }
    }

    /// <summary>A type as RPG declares it: packed(7:2), char(10), dim(5).</summary>
    public static string TypeName(RpgType type)
    {
        var name = type.Kind switch
        {
            RpgKind.Char => $"char({type.Length})",
            RpgKind.Varchar => $"varchar({type.Length})",
            RpgKind.Decimal => $"{(type.Form == 'S' ? "zoned" : type.Form == 'B' ? "bindec" : "packed")}({type.Length}:{type.Decimals})",
            RpgKind.Int => $"{(type.Unsigned ? "uns" : "int")}({type.Length})",
            RpgKind.Ds => "ds",
            _ => type.Kind.ToString().ToLowerInvariant(),
        };
        return type.Dim > 0 ? $"{name} dim({type.Dim})" : name;
    }

    /// <summary>What a value holds, to open it up: an array's elements, a structure's subfields. None for a single value.</summary>
    public static List<(string Name, Cell Cell)> Children(Cell cell)
    {
        if (cell.Elements is { } elements) return [.. elements.Select((e, i) => ($"({i + 1})", e))];
        if (cell.Type.Kind == RpgKind.Ds && cell.Get() is DsValue ds) return [.. ds.Layout.Fields.Select((f, i) => (f.Name, ds.Cells[i])).Where(f => f.Name.Length > 0)];
        return [];
    }
}
