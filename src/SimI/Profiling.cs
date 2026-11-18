// Where a program run's time went, for a flame graph. Each top-level CALL in a job is one profile:
// a tree of programs, procedures, subroutines and source lines, with leaves for SQL, file and
// display operations. Time is wall-clock time, charged to whatever is on top of the stack between
// two events, so waiting (for PostgreSQL, for the person at the display, at a breakpoint) shows as
// the operation it waited in. Profiles hold names, source line numbers and SQL text as translated
// (with parameter markers, not values), never a field's value.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace SimI;

/// <summary>One bar of a flame graph: what ran, and the time spent in it but not in its children.</summary>
public sealed class ProfileNode(string kind, string name, int line = 0)
{
    private readonly Dictionary<(string Kind, string Name), ProfileNode> _children = [];
    private readonly Dictionary<int, ProfileNode> _lines = [];
    private readonly List<ProfileNode> _all = [];

    /// <summary>program, procedure, subroutine, line, sql, file, display or print.</summary>
    public string Kind { get; } = kind;

    public string Name { get; } = name;

    /// <summary>The source line, for a line; otherwise 0.</summary>
    public int Line { get; } = line;

    /// <summary>Time spent here and in no child, in Stopwatch ticks.</summary>
    public long Self { get; internal set; }

    /// <summary>How many times it was entered.</summary>
    public long Count { get; internal set; }

    /// <summary>The children, in the order they first ran.</summary>
    public IReadOnlyList<ProfileNode> Children => _all;

    /// <summary>The time here and in every child, in Stopwatch ticks.</summary>
    public long Total => Self + _all.Sum(c => c.Total);

    internal ProfileNode Child(string kind, string name)
    {
        if (!_children.TryGetValue((kind, name), out var child))
        {
            _children[(kind, name)] = child = new ProfileNode(kind, name);
            _all.Add(child);
        }
        return child;
    }

    internal ProfileNode LineChild(int line)
    {
        if (!_lines.TryGetValue(line, out var child))
        {
            _lines[line] = child = new ProfileNode("line", line.ToString(System.Globalization.CultureInfo.InvariantCulture), line);
            _all.Add(child);
        }
        return child;
    }
}

/// <summary>One top-level program call: who ran it, when, how long, and its tree.</summary>
public sealed class Profile(int id, string program, string user, string job)
{
    public int Id { get; } = id;
    public string Program { get; } = program;
    public string User { get; } = user;
    public string Job { get; } = job;
    public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;

    /// <summary>The run's elapsed time, in Stopwatch ticks, once it ended.</summary>
    public long Elapsed { get; internal set; }

    /// <summary>Why it ended in error, or null when it ended normally.</summary>
    public string? Failure { get; internal set; }

    public ProfileNode Root { get; } = new("program", program);

    public static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    /// <summary>
    /// The folded-stacks form flame graph tools read (speedscope, flamegraph.pl): one line per
    /// stack, its frames joined by ;, then its self time in microseconds.
    /// </summary>
    public string Folded()
    {
        var text = new StringBuilder();
        void Walk(ProfileNode node, string stack)
        {
            var here = stack.Length == 0 ? Label(node) : $"{stack};{Label(node)}";
            var micros = (long)Math.Round(node.Self * 1_000_000.0 / Stopwatch.Frequency);
            if (micros > 0) text.Append(here).Append(' ').Append(micros).Append('\n');
            foreach (var child in node.Children) Walk(child, here);
        }
        Walk(Root, "");
        return text.ToString();
    }

    private static string Label(ProfileNode node) =>
        (node.Kind == "line" ? $"line {node.Name}" : node.Kind is "program" or "procedure" ? node.Name : $"{node.Kind}: {node.Name}")
            .Replace(';', ',').Replace('\n', ' ');
}

/// <summary>
/// Records one profile while a job's top-level call runs. Not thread-safe: a job runs one thing
/// at a time.
/// </summary>
internal sealed class Profiler
{
    private readonly List<ProfileNode> _stack;
    private long _last;

    public Profiler(Profile profile)
    {
        Profile = profile;
        _stack = [profile.Root];
        profile.Root.Count = 1;
        _started = _last = Stopwatch.GetTimestamp();
    }

    public Profile Profile { get; }

    private readonly long _started;

    /// <summary>Charges the time since the last event to what is on top.</summary>
    private void Flush()
    {
        var now = Stopwatch.GetTimestamp();
        _stack[^1].Self += now - _last;
        _last = now;
    }

    /// <summary>
    /// Enters a program, procedure, subroutine or operation, under the line it was called from.
    /// Returns the depth to hand <see cref="Leave"/>.
    /// </summary>
    public int Enter(string kind, string name)
    {
        Flush();
        var depth = _stack.Count;
        var node = _stack[^1].Child(kind, name);
        node.Count++;
        _stack.Add(node);
        return depth;
    }

    /// <summary>Leaves what <see cref="Enter"/> entered, with the lines and anything left open in it (after an error).</summary>
    public void Leave(int depth)
    {
        Flush();
        if (depth >= 1 && depth < _stack.Count) _stack.RemoveRange(depth, _stack.Count - depth);
    }

    /// <summary>A statement on a line starts: the line replaces the one before it in the same program, procedure or subroutine.</summary>
    public void Line(int line)
    {
        var top = _stack[^1];
        if (top.Kind == "line" && top.Line == line) return;
        Flush();
        if (top.Kind == "line") _stack.RemoveAt(_stack.Count - 1);
        var node = _stack[^1].LineChild(line);
        node.Count++;
        _stack.Add(node);
    }

    /// <summary>The run ended: what is left on the stack is charged, and the elapsed time kept.</summary>
    public void Finish(string? failure)
    {
        Flush();
        Profile.Failure = failure;
        Profile.Elapsed = _last - _started;
    }
}

/// <summary>The last <see cref="Kept"/> profiles, newest first.</summary>
public sealed class ProfileStore
{
    public const int Kept = 30;

    private readonly ConcurrentQueue<Profile> _profiles = new();
    private int _next;

    internal int NextId() => Interlocked.Increment(ref _next);

    internal void Add(Profile profile)
    {
        _profiles.Enqueue(profile);
        while (_profiles.Count > Kept) _profiles.TryDequeue(out _);
    }

    public IReadOnlyList<Profile> All => [.. _profiles.Reverse()];

    public Profile? Find(int id) => _profiles.FirstOrDefault(p => p.Id == id);
}
