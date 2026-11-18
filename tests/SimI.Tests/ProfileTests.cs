using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SimI.Tests;

/// <summary>The profile of each top-level program call, for the flame graph page.</summary>
public class ProfileTests
{
    private static void Compile(Machine machine, string name, string type, string source)
    {
        var member = new Member("QGPL", type.StartsWith("CL", StringComparison.Ordinal) ? "QCLLESRC" : "QRPGLESRC", name, type, source);
        var found = machine.Save(member);
        var compiled = machine.Compile(member);
        Assert.True(compiled.Ok, $"{compiled.Message} {string.Join("; ", found.Where(d => d.Severity == Severity.Error).Select(d => $"line {d.Line}: {d.Message}"))}");
    }

    private static ProfileNode Child(ProfileNode node, string kind, string name) =>
        Assert.Single(node.Children, c => c.Kind == kind && c.Name == name);

    /// <summary>The first node of that kind and name anywhere below.</summary>
    private static ProfileNode? Find(ProfileNode node, string kind, string name) =>
        node.Children.FirstOrDefault(c => c.Kind == kind && c.Name == name) ?? node.Children.Select(c => Find(c, kind, name)).FirstOrDefault(f => f is not null);

    [Fact]
    public void ACallIsATreeOfLinesProceduresSubroutinesSqlAndProgramsItCalls()
    {
        using var machine = Machine.Open();
        Compile(machine, "INNER", "RPGLE", """
            **FREE
            dsply 'inner';
            *inlr = *on;
            """);
        Compile(machine, "TEST", "SQLRPGLE", """
            **FREE
            dcl-pr inner extpgm('INNER');
            end-pr;
            dcl-s i int(10);
            dcl-s n int(10);
            for i = 1 to 3;
              n = twice(i);
            endfor;
            exsr greet;
            exec sql set :n = 5;
            inner();
            *inlr = *on;
            return;

            begsr greet;
              dsply 'hello';
            endsr;

            dcl-proc twice;
              dcl-pi *n int(10);
                v int(10) value;
              end-pi;
              return v * 2;
            end-proc;
            """);
        var job = new BatchJob("PRTUSER", CancellationToken.None);
        Assert.True(machine.Run("CALL TEST", job).Ok);

        var profile = Assert.Single(machine.Profiles.All);
        Assert.Equal("QGPL/TEST", profile.Program);
        Assert.Equal("PRTUSER", profile.User);
        Assert.Null(profile.Failure);
        Assert.True(profile.Elapsed > 0);
        Assert.Null(job.State.Profiler);

        var root = profile.Root;
        Assert.Equal(("program", "QGPL/TEST"), (root.Kind, root.Name));
        // The tree accounts for the whole run, give or take the moments before and after.
        Assert.InRange(root.Total, profile.Elapsed * 9 / 10, profile.Elapsed);

        // The procedure, called three times from line 7, and its own line.
        var twice = Child(Child(root, "line", "7"), "procedure", "TWICE");
        Assert.Equal(3, twice.Count);
        Assert.Contains(twice.Children, c => c.Kind == "line");
        // The subroutine runs its DSPLY; the SQL statement and the program called are bars of their lines.
        var greet = Child(Child(root, "line", "9"), "subroutine", "GREET");
        Assert.NotNull(Find(greet, "display", "DSPLY"));
        Assert.Single(Child(root, "line", "10").Children, c => c.Kind == "sql");
        var inner = Child(Child(root, "line", "11"), "program", "QGPL/INNER");
        Assert.NotNull(Find(inner, "display", "DSPLY"));
        // Only one profile: the program INNER is part of TEST's.
        Assert.Single(machine.Profiles.All);

        var folded = profile.Folded();
        Assert.Contains("QGPL/TEST;line 9;subroutine: GREET;", folded);
        Assert.Contains("QGPL/TEST;line 11;QGPL/INNER;", folded);
        Assert.All(folded.Split('\n', StringSplitOptions.RemoveEmptyEntries), l => Assert.Matches(@"^[^ ].* \d+$", l));
    }

    [Fact]
    public void ACallThatFailsKeepsWhyAndTheNextCallStartsAFreshProfile()
    {
        using var machine = Machine.Open();
        Compile(machine, "BAD", "RPGLE", """
            **FREE
            dcl-s a int(10) inz(1);
            dcl-s b int(10) inz(0);
            exsr divide;
            return;
            begsr divide;
              a = a / b;
            endsr;
            """);
        var job = new BatchJob("PRTUSER", CancellationToken.None);
        Assert.False(machine.Run("CALL BAD", job).Ok);
        Assert.False(machine.Run("CALL BAD", job).Ok);

        var profiles = machine.Profiles.All;
        Assert.Equal(2, profiles.Count);
        Assert.True(profiles[0].Id > profiles[1].Id);
        Assert.All(profiles, p => Assert.NotNull(p.Failure));
        // The second run's tree is its own, not inside the first's.
        Assert.Equal(1, Find(profiles[0].Root, "subroutine", "DIVIDE")!.Count);
        Assert.Null(job.State.Profiler);
    }

    [Fact]
    public void ClProgramsHaveTheirCommandsAndTheProgramsTheyCall()
    {
        using var machine = Machine.Open();
        Compile(machine, "LEAF", "RPGLE", """
            **FREE
            *inlr = *on;
            """);
        Compile(machine, "DRIVER", "CLLE", """
            PGM
              DCL VAR(&N) TYPE(*DEC) LEN(5 0)
              CHGVAR VAR(&N) VALUE(1)
              CALL PGM(LEAF)
            ENDPGM
            """);
        Assert.True(machine.Run("CALL DRIVER", new BatchJob("PRTUSER", CancellationToken.None)).Ok);

        var root = Assert.Single(machine.Profiles.All).Root;
        Assert.Equal("QGPL/DRIVER", root.Name);
        Assert.NotNull(Find(root, "command", "CHGVAR"));
        Assert.NotNull(Find(Find(root, "command", "CALL")!, "program", "QGPL/LEAF"));
    }

    [Fact]
    public void OnlyTheLastThirtyAreKept()
    {
        using var machine = Machine.Open();
        Compile(machine, "QUICK", "RPGLE", "**FREE\n*inlr = *on;\n");
        var job = new BatchJob("PRTUSER", CancellationToken.None);
        for (var i = 0; i < ProfileStore.Kept + 5; i++) Assert.True(machine.Run("CALL QUICK", job).Ok);
        var all = machine.Profiles.All;
        Assert.Equal(ProfileStore.Kept, all.Count);
        Assert.Equal(ProfileStore.Kept + 5, all[0].Id);
        Assert.Null(machine.Profiles.Find(1));
        Assert.NotNull(machine.Profiles.Find(all[^1].Id));
    }

    [Fact]
    public async Task ThePageServesProfilesToASignedOnUser()
    {
        var machine = Machine.Open();
        using var _ = machine;
        var users = Users.Open(machine.Store, TestUsers.Settings());
        await using var server = await WebServer.StartAsync(0, _ => { }, IPAddress.Loopback, users, machine);
        using var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{server.Port}/"),
        };
        client.DefaultRequestHeaders.Add("X-Sim-i", "1");
        Compile(machine, "QUICK", "RPGLE", "**FREE\ndsply 'x';\n*inlr = *on;\n");
        Assert.True(machine.Run("CALL QUICK", new BatchJob("PRTUSER", CancellationToken.None)).Ok);

        Assert.Contains("Program profiles", await client.GetStringAsync("/profile"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/profiles")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/signon", new { user = "prtuser", password = "Secret12" })).StatusCode);

        var list = await client.GetFromJsonAsync<JsonElement>("/api/profiles");
        var run = Assert.Single(list.EnumerateArray());
        Assert.Equal("QGPL/QUICK", run.GetProperty("program").GetString());
        var id = run.GetProperty("id").GetInt32();

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/profiles/{id}");
        var root = detail.GetProperty("root");
        Assert.Equal("program", root.GetProperty("kind").GetString());
        Assert.True(root.GetProperty("total").GetDouble() >= root.GetProperty("self").GetDouble());
        Assert.NotEmpty(root.GetProperty("children").EnumerateArray());

        var folded = await client.GetAsync($"/api/profiles/{id}/folded");
        Assert.Equal("attachment", folded.Content.Headers.ContentDisposition?.DispositionType);
        Assert.StartsWith("QGPL/QUICK", await folded.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/profiles/9999")).StatusCode);
    }
}
