using SimI.Tests.Client;

namespace SimI.Tests;

/// <summary>
/// A library's initial.json, run once after its source is read: users, commands and CSV data. And the user profile's
/// initial program (INLPGM) and menu (INLMNU), which a 5250 sign-on follows. Placeholder users and passwords only.
/// </summary>
public class InitializationTests
{
    private static CommandResult Run(Machine machine, string command) => machine.Run(command, new BatchJob("PRTUSER", CancellationToken.None));

    private static long Count(Machine machine, string sql)
    {
        lock (machine.Store.Gate)
        {
            using var command = machine.Store.Connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar());
        }
    }

    private static string Text(Machine machine, string sql)
    {
        lock (machine.Store.Gate)
        {
            using var command = machine.Store.Connection.CreateCommand();
            command.CommandText = sql;
            return (Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? "").TrimEnd();
        }
    }

    /// <summary>A source folder with library INITLIB: a table, a CSV file for it, and the initial.json given.</summary>
    private static string Source(string initial)
    {
        var root = Directory.CreateTempSubdirectory("sim-i-initial-").FullName;
        var library = Path.Combine(root, "INITLIB");
        Directory.CreateDirectory(Path.Combine(library, "data"));
        File.WriteAllText(Path.Combine(library, "regions.sql"), """
            create or replace table initlib.region (
              code char(4) not null primary key,
              name varchar(30) not null with default,
              target decimal(9, 2) not null with default
            );
            """);
        File.WriteAllText(Path.Combine(library, "data", "regions.csv"), "﻿code,name,target\nNE,\"North, East\",1500.50\nSW,South West,200\n\n");
        File.WriteAllText(Path.Combine(library, Machine.InitialFile), initial);
        return root;
    }

    private const string Initial = """
        {
          "users": [
            { "name": "alice", "password": "Alice123", "text": "Alice's profile", "currentLibrary": "INITLIB",
              "initialProgram": "INITLIB/MENU", "initialMenu": "*SIGNOFF", "specialAuthorities": ["*JOBCTL", "*SPLCTL"] },
            { "name": "BOB" }
          ],
          "commands": [ "CHGUSRPRF USRPRF(BOB) TEXT('Changed by a command')" ],
          "data": [ { "file": "data/regions.csv", "table": "REGION", "header": true } ]
        }
        """;

    [Fact]
    public void AUserProfileHasAnInitialProgramAndMenu()
    {
        using var machine = Machine.Open();

        Assert.True(Run(machine, "CRTUSRPRF USRPRF(CLERK) PASSWORD(Clerk123) INLPGM(APPLIB/START) INLMNU(*SIGNOFF)").Ok);
        var clerk = machine.Store.FindUser("CLERK")!;
        Assert.Equal("APPLIB/START", clerk.InitialProgram);
        Assert.Equal("*SIGNOFF", clerk.InitialMenu);
        Assert.Contains("initial program APPLIB/START, initial menu *SIGNOFF", Run(machine, "DSPUSRPRF CLERK").Message);

        // *LIBL is no library; *SAME keeps it.
        Assert.True(Run(machine, "CHGUSRPRF USRPRF(CLERK) INLPGM(*LIBL/START) INLMNU(*SAME)").Ok);
        Assert.Equal("START", machine.Store.FindUser("CLERK")!.InitialProgram);
        Assert.Equal("*SIGNOFF", machine.Store.FindUser("CLERK")!.InitialMenu);
        Assert.True(Run(machine, "CHGUSRPRF USRPRF(CLERK) INLPGM(*NONE) INLMNU(MAIN)").Ok);
        Assert.Equal(("*NONE", "MAIN"), (machine.Store.FindUser("CLERK")!.InitialProgram, machine.Store.FindUser("CLERK")!.InitialMenu));

        Assert.Contains("Menu ORDERS not found", Run(machine, "CHGUSRPRF USRPRF(CLERK) INLMNU(ORDERS)").Message);
        Assert.Contains("isn't a program", Run(machine, "CHGUSRPRF USRPRF(CLERK) INLPGM(NOT-A-NAME)").Message);
        // A new profile has none, and the main menu.
        Assert.True(Run(machine, "CRTUSRPRF USRPRF(PLAIN)").Ok);
        Assert.Equal(("*NONE", "MAIN"), (machine.Store.FindUser("PLAIN")!.InitialProgram, machine.Store.FindUser("PLAIN")!.InitialMenu));
    }

    [Fact]
    public void ALibraryIsInitializedOnce()
    {
        using var machine = Machine.Open();
        var source = Source(Initial);
        var log = new List<string>();
        Assert.Equal(0, machine.Import(source, log.Add));
        Assert.False(machine.Store.IsInitialized("INITLIB"));

        Assert.Equal(0, machine.Initialize(source, "QSECOFR", log.Add));
        Assert.True(machine.Store.IsInitialized("INITLIB"), string.Join("\n", log));

        var alice = machine.Store.FindUser("ALICE")!;
        Assert.Equal(("Alice123", "Alice's profile", "INITLIB"), (alice.Password, alice.Text, alice.DefaultLibrary));
        Assert.Equal(("INITLIB/MENU", "*SIGNOFF", "*JOBCTL *SPLCTL"), (alice.InitialProgram, alice.InitialMenu, alice.SpecialAuthorities));
        // No password given: the profile's name, as CRTUSRPRF has it. The users come before the commands.
        Assert.Equal(("BOB", "Changed by a command"), (machine.Store.FindUser("BOB")!.Password, machine.Store.FindUser("BOB")!.Text));
        // The header line is skipped; a quoted field keeps its comma.
        Assert.Equal(2, Count(machine, "SELECT count(*) FROM INITLIB.REGION"));
        Assert.Equal("North, East", Text(machine, "SELECT NAME FROM INITLIB.REGION WHERE CODE = 'NE'"));
        Assert.Equal(1500.50m, Convert.ToDecimal(Text(machine, "SELECT TARGET FROM INITLIB.REGION WHERE CODE = 'NE'"), System.Globalization.CultureInfo.InvariantCulture));
        // The password isn't logged.
        Assert.DoesNotContain(log, line => line.Contains("Alice123"));
        Assert.Contains(log, line => line.Contains("initialize: INITLIB done"));

        // Not again: at the next start, nothing is run.
        machine.RunScript("DELETE FROM INITLIB.REGION", "QSECOFR");
        log.Clear();
        Assert.Equal(0, machine.Initialize(source, "QSECOFR", log.Add));
        Assert.Empty(log);
        Assert.Equal(0, Count(machine, "SELECT count(*) FROM INITLIB.REGION"));
    }

    [Fact]
    public void ALibraryThatDidNotFinishIsTriedAgain()
    {
        using var machine = Machine.Open();
        var source = Source(Initial.Replace("CHGUSRPRF USRPRF(BOB) TEXT('Changed by a command')", "CALL INITLIB/NOSUCH"));
        var log = new List<string>();
        machine.Import(source, log.Add);

        Assert.Equal(1, machine.Initialize(source, "QSECOFR", log.Add));
        Assert.False(machine.Store.IsInitialized("INITLIB"));
        Assert.Contains(log, line => line.Contains("INITLIB not finished: CALL INITLIB/NOSUCH failed") && line.Contains("tried again at the next start"));
        // The users were made; the data, after the commands, wasn't loaded.
        Assert.NotNull(machine.Store.FindUser("ALICE"));
        Assert.Equal(0, Count(machine, "SELECT count(*) FROM INITLIB.REGION"));

        // Fixed, it runs again: the users that are there are changed, not made twice.
        File.WriteAllText(Path.Combine(source, "INITLIB", Machine.InitialFile), Initial.Replace("Alice's profile", "Alice, again"));
        log.Clear();
        Assert.Equal(0, machine.Initialize(source, "QSECOFR", log.Add));
        Assert.True(machine.Store.IsInitialized("INITLIB"));
        Assert.Contains(log, line => line.Contains("user ALICE: User profile ALICE changed."));
        Assert.Equal("Alice, again", machine.Store.FindUser("ALICE")!.Text);
        Assert.Equal(2, Count(machine, "SELECT count(*) FROM INITLIB.REGION"));
    }

    [Theory]
    [InlineData("[]", "must be an object")]
    [InlineData("{ \"users\": [ { \"name\": \"X1\", \"inlpgm\": \"A/B\" } ] }", "inlpgm isn't something initial.json has here")]
    [InlineData("{ \"commands\": \"CALL X\" }", "\"commands\" must be a list")]
    [InlineData("{ \"data\": [ { \"file\": \"../outside.csv\", \"table\": \"REGION\" } ] }", "isn't in the library's folder")]
    [InlineData("{ \"data\": [ { \"file\": \"data/regions.csv\", \"table\": \"NOSUCH\" } ] }", "Table INITLIB/NOSUCH not found")]
    [InlineData("{ \"users\": [ { \"name\": \"X1\", \"initialMenu\": \"ORDERS\" } ] }", "Menu ORDERS not found")]
    [InlineData("{ nope", "isn't JSON")]
    public void AProblemIsSaid(string initial, string expected)
    {
        using var machine = Machine.Open();
        var source = Source(initial);
        var log = new List<string>();
        machine.Import(source, log.Add);
        Assert.Equal(1, machine.Initialize(source, "QSECOFR", log.Add));
        Assert.Contains(log, line => line.Contains(expected));
        Assert.False(machine.Store.IsInitialized("INITLIB"));
    }

    [Fact]
    public void AFolderWithOnlyAnInitialFileIsALibrary()
    {
        using var machine = Machine.Open();
        var root = Directory.CreateTempSubdirectory("sim-i-initial-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "USERSONLY"));
        File.WriteAllText(Path.Combine(root, "USERSONLY", Machine.InitialFile), """{ "users": [ { "name": "CAROL", "password": "Carol123" } ] }""");
        var log = new List<string>();
        machine.Import(root, log.Add);
        Assert.Equal(0, machine.Initialize(root, "QSECOFR", log.Add));
        Assert.Contains("USERSONLY", machine.Store.Libraries());
        Assert.NotNull(machine.Store.FindUser("CAROL"));
    }

    /// <summary>The CRM in the examples, read and initialized as Sim i does at start: its users sign on to its menu.</summary>
    [Fact]
    public async Task TheCrmInitializesAndItsUsersSignOnToItsMenu()
    {
        using var machine = Machine.Open();
        var log = new List<string>();
        Assert.Equal(0, machine.Import(Examples.CrmSource(), log.Add));
        Assert.Equal(0, machine.Initialize(Examples.CrmSource(), "QSECOFR", log.Add));
        Assert.Contains(log, line => line.Contains("CALL CRMDEMO/CRMBUILD PARM('*YES'): CRMBUILD complete."));
        Assert.Equal(50, Count(machine, "SELECT count(*) FROM CRMDEMO.CUSTOMER"));
        Assert.Equal(("CRMDEMO/CRMMENU", "*SIGNOFF", "CRMDEMO"),
            (machine.Store.FindUser("CRMUSR1")!.InitialProgram, machine.Store.FindUser("CRMUSR1")!.InitialMenu, machine.Store.FindUser("CRMUSR1")!.DefaultLibrary));

        await using var host = await CrmScreenTests.StartAsync(machine);
        // The menu at sign-on, and its programs found in the current library.
        await using (var client = await SignOnAsync(host, "CRMUSR1", "Crm12345"))
        {
            await client.WaitForTextAsync("CRM Demo - Main Menu");
            Assert.Contains("Jordan Lee", client.Text());
            await client.PressAsync(new CursorKey(21, 7));
            await client.TypeAsync("1");
            await client.EnterAsync();
            await client.WaitForTextAsync("customers shown");
            await client.PressAsync(new FunctionKey("F3"));
            await client.WaitForTextAsync("CRM Demo - Main Menu");
            // F3 on the menu ends the initial program, and INLMNU(*SIGNOFF) signs off.
            await client.PressAsync(new FunctionKey("F3"));
            await client.WaitForAsync(c => c.HostClosed, "sign-off");
        }

        // INLMNU(MAIN): the main menu after it.
        Assert.True(Run(machine, "CHGUSRPRF USRPRF(CRMUSR2) INLMNU(MAIN)").Ok);
        await using (var client = await SignOnAsync(host, "CRMUSR2", "Crm12345"))
        {
            await client.WaitForTextAsync("CRM Demo - Main Menu");
            await client.PressAsync(new FunctionKey("F3"));
            await client.WaitForTextAsync("IBM i Main Menu");
            Assert.False(client.HostClosed);
        }
    }

    [Fact]
    public async Task AnInitialProgramThatFailsLeavesTheSignOnScreen()
    {
        using var machine = Machine.Open();
        Assert.True(Run(machine, "CRTUSRPRF USRPRF(CLERK) PASSWORD(Clerk123) INLPGM(QGPL/NOSUCH) INLMNU(*SIGNOFF)").Ok);
        await using var host = await CrmScreenTests.StartAsync(machine);
        await using var client = await SignOnAsync(host, "CLERK", "Clerk123");
        await client.WaitForTextAsync("Sign On");
        Assert.Contains("NOSUCH", client.Text());
        Assert.False(client.HostClosed);
    }

    [Fact]
    public async Task NoInitialProgramAndSignOffSignsOffAtOnce()
    {
        using var machine = Machine.Open();
        Assert.True(Run(machine, "CRTUSRPRF USRPRF(CLERK) PASSWORD(Clerk123) INLMNU(*SIGNOFF)").Ok);
        await using var host = await CrmScreenTests.StartAsync(machine);
        await using var client = await SignOnAsync(host, "CLERK", "Clerk123");
        await client.WaitForAsync(c => c.HostClosed, "sign-off");
    }

    /// <summary>Signs on, not waiting for any screen after it.</summary>
    private static async Task<TestClient> SignOnAsync(TelnetServer host, string user, string password)
    {
        var client = await TestClient.ConnectAsync(host, null);
        await client.WaitForTextAsync("Sign On");
        await client.PressAsync(new CursorKey(6, 53));
        await client.TypeAsync(user);
        await client.PressAsync(new CursorKey(7, 53));
        await client.TypeAsync(password);
        await client.EnterAsync();
        return client;
    }
}
