// The jobs programs run in: one with no display (the remote command server's), and one on a
// 5250 session, where the program runs on its own thread and the session passes screens out and
// the user's input in.

using System.Collections.Concurrent;
using System.Threading.Channels;
using FakeIbmI.Rpg;
using static FakeIbmI.Stream5250;

namespace FakeIbmI;

/// <summary>An override of a printer file (OVRPRTF): what its spooled file is called, and how it's kept.</summary>
internal sealed record PrinterOverride(string? SpoolName, bool Hold, string? OutputQueue, string? UserData);

/// <summary>
/// What lasts as long as a job, from one command or program to the next: its name and number, its
/// library list, its overrides, the service programs activated in it, the identity value its last
/// INSERT made, and its display's size and what was last shown on it.
/// </summary>
internal sealed class JobState(string name = "QPADEV0001", bool wide = false)
{
    private static int _numbers = 100000;

    public string Name { get; } = name;
    public string Number { get; } = (Interlocked.Increment(ref _numbers) % 900000 + 100000).ToString("000000");

    /// <summary>Whether the job's display can show 27 x 132 (a 3477-FC, 3180-2 and the like).</summary>
    public bool Wide { get; set; } = wide;

    /// <summary>The job's current directory in the integrated file system (CHGCURDIR), where relative paths start.</summary>
    public string CurrentDirectory { get; set; } = "/";

    /// <summary>The job's local data area (*LDA): 1024 characters.</summary>
    public string LocalDataArea { get; set; } = new(' ', 1024);

    /// <summary>The stream files the job's programs have open (the IFS APIs), by descriptor.</summary>
    internal Dictionary<int, Rpg.StreamHandle> StreamFiles { get; } = [];

    /// <summary>Where errno is: __errno() points here.</summary>
    internal Rpg.Storage ErrnoStorage { get; } = new(4);

    /// <summary>The libraries ADDLIBLE added, in order: looked for before the rest.</summary>
    public List<string> LibraryList { get; } = [];

    public Dictionary<string, PrinterOverride> Overrides { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>OVRDBF: the file a program's open of a database file gets, by the name it opens.</summary>
    internal Dictionary<string, DatabaseOverride> DatabaseOverrides { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>OPNQRYF: the job's open queries, by their open id (the first file's name, or OPNID).</summary>
    internal Dictionary<string, DatabaseFile> QueryFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Messages sent to programs' message queues (QMHSNDPM, SNDPGMMSG), which a message subfile shows: the queue (the program) and the text.</summary>
    public List<(string Queue, string Text)> ProgramMessages { get; } = [];

    /// <summary>Service programs activated in the job, which keep their global variables between calls.</summary>
    internal Dictionary<(string Library, string Name), (long Version, Rpg.Interpreter Program)> ServicePrograms { get; } = [];

    /// <summary>The identity value of the job's last INSERT, as IDENTITY_VAL_LOCAL() returns it.</summary>
    public long LastIdentity { get; set; }

    /// <summary>What was last on the display, without its input fields: what a window or ASSUME record shows over.</summary>
    internal Dds.ScreenImage? Screen { get; set; }

    /// <summary>The mail SNDSMTPEMM would have sent: who to, and the subject.</summary>
    public List<(string To, string Subject)> Mail { get; } = [];

    /// <summary>Messages programs sent to the job's external message queue (SNDPGMMSG ... *COMP, *INFO, *DIAG), for the command line to show.</summary>
    public List<string> Messages { get; } = [];
}

/// <summary>A job with no display: DSPLY's messages are kept, and nobody replies.</summary>
internal sealed class BatchJob(string user, CancellationToken cancelled, JobState? state = null) : IJobHost
{
    private readonly List<string> _messages = [];

    public string User => user;
    public CancellationToken Cancelled => cancelled;
    public IWorkstation? Workstation => null;
    public JobState State { get; } = state ?? new JobState("QZRCSRVS");

    /// <summary>What DSPLY showed, in order.</summary>
    public IReadOnlyList<string> Messages => _messages;

    public string? Dsply(string message, int replyLength)
    {
        _messages.Add(message);
        return null;
    }
}

/// <summary>What a running program wants of its 5250 session next.</summary>
/// <param name="Screen">A record to send, or null when the program has ended.</param>
/// <param name="Wait">Whether the program now waits for the user's input.</param>
/// <param name="Message">When it has ended: the message for the command line (why it failed, when <paramref name="Failed"/>), or null.</param>
internal sealed record RunEvent(byte[]? Screen, bool Wait, string? Message, bool Failed = false);

/// <summary>Thrown by a job's body to end it in error with this message.</summary>
internal sealed class ProgramFailed(string message) : Exception(message);

/// <summary>A program running in a 5250 session.</summary>
internal sealed class InteractiveJob : IJobHost, IWorkstation
{
    private readonly Channel<RunEvent> _events = Channel.CreateUnbounded<RunEvent>();
    private readonly BlockingCollection<ReceivedInput> _inputs = [];
    private readonly CancellationTokenSource _cancel;
    private readonly List<string> _messages = [];
    private int _shown;

    private InteractiveJob(string user, CancellationToken stop, JobState state)
    {
        User = user;
        State = state;
        _cancel = CancellationTokenSource.CreateLinkedTokenSource(stop);
    }

    public string User { get; }
    public CancellationToken Cancelled => _cancel.Token;
    public IWorkstation Workstation => this;
    public JobState State { get; }

    /// <summary>
    /// Starts <paramref name="body"/> on its own thread. It returns why it failed, or null. The
    /// session then takes <see cref="NextAsync"/> until the program has ended.
    /// </summary>
    public static InteractiveJob Start(string user, Func<InteractiveJob, string?> body, CancellationToken stop, JobState? state = null)
    {
        var job = new InteractiveJob(user, stop, state ?? new JobState());
        // Its own thread, with room for deep recursion in the program.
        var thread = new Thread(() =>
        {
            string? message = null;
            var failed = false;
            try
            {
                try
                {
                    message = body(job);
                }
                catch (ProgramFailed e)
                {
                    (message, failed) = (e.Message, true);
                }
                // Messages DSPLY left that nobody has seen: shown before the program's screen goes.
                if (job._shown < job._messages.Count) job.Exchange(job.MessagesScreen(0));
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                (message, failed) = ($"The fake failed running the program: {e.GetType().Name}: {e.Message}", true);
            }
            job._events.Writer.TryWrite(new RunEvent(null, false, message, failed));
        }, 16 * 1024 * 1024)
        {
            IsBackground = true,
            Name = "program",
        };
        thread.Start();
        return job;
    }

    /// <summary>The next thing the program wants: a screen, or (Screen null) that it has ended.</summary>
    public ValueTask<RunEvent> NextAsync(CancellationToken stop) => _events.Reader.ReadAsync(stop);

    /// <summary>What the user did on the screen the program waits on.</summary>
    public void Reply(ReceivedInput input) => _inputs.Add(input);

    /// <summary>Ends the program at its next statement or input, as when the session closes.</summary>
    public void Cancel() => _cancel.Cancel();

    public void Send(byte[] record) => _events.Writer.TryWrite(new RunEvent(record, false, null));

    public ReceivedInput Exchange(byte[] record)
    {
        _events.Writer.TryWrite(new RunEvent(record, true, null));
        return _inputs.Take(_cancel.Token);
    }

    public ReceivedInput? Exchange(byte[] record, TimeSpan timeout)
    {
        _events.Writer.TryWrite(new RunEvent(record, true, null));
        return _inputs.TryTake(out var input, (int)timeout.TotalMilliseconds, _cancel.Token) ? input : null;
    }

    public bool Wide => State.Wide;

    public string? Dsply(string message, int replyLength)
    {
        _messages.Add(message);
        if (replyLength == 0) return null;
        return Exchange(MessagesScreen(replyLength)).FieldAt(ReplyRow, ReplyColumn) ?? "";
    }

    private const int ReplyRow = 21;
    private const int ReplyColumn = 19;

    /// <summary>Display Program Messages: the last of DSPLY's messages, and a reply field when one is wanted.</summary>
    private byte[] MessagesScreen(int replyLength)
    {
        _shown = _messages.Count;
        var rows = _messages.Skip(Math.Max(0, _messages.Count - 15)).Select((m, i) => Join(Sba(4 + i, 2), Text(m))).ToArray();
        return Screen5250(
            Header(24),
            Sba(1, 28),
            [Highlight],
            Text("Display Program Messages"),
            [Normal],
            Join(rows),
            replyLength > 0
                ? Join(Sba(20, 2), Text("Type reply, press Enter."), Sba(ReplyRow, 4), Text("Reply . . ."), Sba(ReplyRow, ReplyColumn - 1), Sf(0x4000, Underline, replyLength))
                : Join(Sba(21, 2), Text("Press Enter to continue.")));
    }
}
