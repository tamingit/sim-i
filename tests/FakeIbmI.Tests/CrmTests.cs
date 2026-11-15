using FakeIbmI.Tests.Client;

namespace FakeIbmI.Tests;

/// <summary>The CRM demo in examples/crm: its tables, sample data, programs and screens.</summary>
public class CrmTests
{
    /// <summary>The folder of the CRM's source: examples/crm, found from where the tests run.</summary>
    internal static string SourceDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "examples", "crm");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("examples/crm");
    }

    private static string Member(string file, string name) =>
        File.ReadAllText(Directory.EnumerateFiles(Path.Combine(SourceDir(), "CRMDEMO", file), name + ".*").Single());

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
            // A CHAR column's value comes back padded with blanks, as Db2 gives it.
            return (Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? "").TrimEnd();
        }
    }

    /// <summary>A machine with the CRM's source read in, its three users, and (with <paramref name="build"/>) CRMBUILD run.</summary>
    internal static Machine Loaded(bool build = true)
    {
        var machine = Machine.Open();
        var log = new List<string>();
        Assert.Equal(0, machine.Import(SourceDir(), log.Add));
        // Every member compiles at import, the service program before the programs bound to it.
        Assert.DoesNotContain(log, line => line.Contains("Program") && line.Contains("not created") || line.Contains("not compiled"));
        Assert.Contains(log, line => line.Contains("CRMDEMO/QRPGLESRC(CRMMENU) SQLRPGLE: Program CRMMENU created"));
        foreach (var user in new[] { "CRMUSR1", "CRMUSR2", "CRMUSR3" }) machine.Store.SaveUser(new UserRow(user, "Crm12345", "*ENABLED", "*NO", $"CRM demo user {user[^1]}"));
        if (build)
        {
            var job = new BatchJob("CRMUSR1", CancellationToken.None);
            var result = machine.Run("CALL CRMDEMO/CRMBUILD PARM('*YES')", job);
            Assert.True(result.Ok, result.Message);
            Assert.Equal("CRMBUILD complete. Run CRMDEMO/CRM to start.", job.State.Messages[^1]);
        }
        return machine;
    }

    [Fact]
    public void CrmBuildMakesEverything()
    {
        using var machine = Loaded();

        foreach (var (name, type) in new[]
        {
            ("CRMMENU", "*PGM"), ("CRMDASH", "*PGM"), ("CRMC", "*PGM"), ("CRMSRV", "*SRVPGM"), ("CRMSRV", "*MODULE"), ("CRMBND", "*BNDDIR"),
            ("CRM", "*CMD"), ("CRMRPTP", "*FILE"), ("CRMDASHD", "*FILE"),
        })
        {
            Assert.True(machine.Store.FindObject("CRMDEMO", name, type) is not null, $"{name} {type}");
        }
        Assert.Contains("WAITRCD(2)", machine.Store.FindObject("CRMDEMO", "CRMDASHD", "*FILE")!.Attribute);
        Assert.Equal("CRMDEMO/CRMSRV *SRVPGM", machine.Store.FindObject("CRMDEMO", "CRMBND", "*BNDDIR")!.Source);
        Assert.Equal(50, Count(machine, "SELECT count(*) FROM CRMDEMO.CUSTOMER"));
    }

    [Fact]
    public void TheTablesAndSampleDataLoad()
    {
        using var machine = Machine.Open();
        machine.Store.CreateLibrary("CRMDEMO");

        var (_, drop) = machine.RunScript(Member("QSQLSRC", "CRMDROP"), "QSECOFR");
        Assert.Equal(7, drop.Count);
        var (_, tables) = machine.RunScript(Member("QSQLSRC", "CRMTABLES"), "QSECOFR");
        Assert.Empty(tables);
        var (_, seed) = machine.RunScript(Member("QSQLSRC", "CRMSEED"), "QSECOFR");
        Assert.Empty(seed);

        Assert.Equal(3, Count(machine, "SELECT count(*) FROM CRMDEMO.CRMUSER"));
        Assert.Equal(50, Count(machine, "SELECT count(*) FROM CRMDEMO.CUSTOMER"));
        Assert.InRange(Count(machine, "SELECT count(*) FROM CRMDEMO.CONTACT"), 110, 120);
        Assert.InRange(Count(machine, "SELECT count(*) FROM CRMDEMO.ACTIVITY"), 200, 300);
        Assert.InRange(Count(machine, "SELECT count(*) FROM CRMDEMO.OPPTY"), 50, 90);
        Assert.Equal(1, Count(machine, "SELECT count(*) FROM CRMDEMO.CRMEVENT"));
        // The tables are CRMDEMO's, not QGPL's.
        Assert.Equal(0, Count(machine, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'qgpl' AND table_name = 'customer'"));

        Assert.Equal("CRMUSR2", Text(machine, "SELECT OWNER FROM CRMDEMO.CUSTOMER WHERE CUSTID = 1001"));
        Assert.Equal("(216) 555-0107", Text(machine, "SELECT PHONE FROM CRMDEMO.CUSTOMER WHERE CUSTID = 1001"));
        Assert.Equal("www.acmewidgetworks.example", Text(machine, "SELECT WEBSITE FROM CRMDEMO.CUSTOMER WHERE CUSTID = 1001"));
        Assert.Matches(@"^\d+ Oak Avenue$", Text(machine, "SELECT ADDR1 FROM CRMDEMO.CUSTOMER WHERE CUSTID = 1001"));
        Assert.Equal(5, Text(machine, "SELECT ZIP FROM CRMDEMO.CUSTOMER WHERE CUSTID = 1001").Length);
        // Dates are relative to today.
        Assert.True(Count(machine, $"SELECT count(*) FROM CRMDEMO.ACTIVITY WHERE DONE = 'N' AND DUEDATE < '{DateTime.Today:yyyy-MM-dd}'") > 0);
        // The identity carries on after the sample data.
        machine.RunScript("INSERT INTO CRMDEMO.CUSTOMER (NAME) VALUES ('New one')", "QSECOFR");
        Assert.Equal(1051, Count(machine, "SELECT max(CUSTID) FROM CRMDEMO.CUSTOMER"));
        // A row change timestamp changes when the row does.
        var before = Text(machine, "SELECT to_char(CHGTS, 'YYYY-MM-DD HH24:MI:SS.US') FROM CRMDEMO.CUSTOMER WHERE CUSTID = 1001");
        Assert.Matches(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{6}$", before);
        Thread.Sleep(5);
        machine.RunScript("UPDATE CRMDEMO.CUSTOMER SET PHONE = 'x' WHERE CUSTID = 1001", "QSECOFR");
        Assert.NotEqual(before, Text(machine, "SELECT to_char(CHGTS, 'YYYY-MM-DD HH24:MI:SS.US') FROM CRMDEMO.CUSTOMER WHERE CUSTID = 1001"));
    }
}

/// <summary>The CRM's screens, through 5250 sessions.</summary>
public class CrmScreenTests
{
    private static Key Click(int row, int column) => new CursorKey(row, column);

    /// <summary>A Telnet server on the machine, signing users on against its users table.</summary>
    internal static Task<TelnetServer> StartAsync(Machine machine) =>
        TelnetServer.StartAsync(machine: machine, users: Users.Open(machine.Store, new Dictionary<string, string>()));

    internal static async Task<TestClient> SignOnAsync(TelnetServer host, string user)
    {
        var client = await TestClient.ConnectAsync(host, null);
        await client.WaitForTextAsync("Sign On");
        await client.PressAsync(Click(6, 53));
        await client.TypeAsync(user);
        await client.PressAsync(Click(7, 53));
        await client.TypeAsync("Crm12345");
        await client.EnterAsync();
        await client.WaitForTextAsync("IBM i Main Menu");
        return client;
    }

    internal static async Task CommandAsync(TestClient client, string command)
    {
        await client.PressAsync(Click(20, 7));
        await client.TypeAsync(command);
        await client.EnterAsync();
    }

    /// <summary>The screen's text with each display attribute as the blank it shows as.</summary>
    internal static string Plain(TestClient client) => System.Text.RegularExpressions.Regex.Replace(client.Text(), "[\u0080-\u009f]", " ");

    private static void Dump(TestClient client, string name)
    {
        var dir = Environment.GetEnvironmentVariable("CRM_SCREENS");
        if (dir is not null) File.WriteAllText(Path.Combine(dir, name + ".txt"), Plain(client));
    }

    [Fact]
    public async Task TheMenuAndItsScreens()
    {
        using var machine = CrmTests.Loaded();
        await using var host = await StartAsync(machine);
        await using var client = await SignOnAsync(host, "CRMUSR1");

        await CommandAsync(client, "CRMDEMO/CRM");
        await client.WaitForTextAsync("CRM Demo - Main Menu");
        Dump(client, "menu");
        Assert.Contains("Jordan Lee", client.Text());

        await client.PressAsync(Click(21, 7));
        await client.TypeAsync("1");
        await client.EnterAsync();
        await client.WaitForTextAsync("customers shown");
        Dump(client, "customers");
        Assert.Contains("Acme Widget Works", Plain(client));

        // 2 on Acme: the detail screen; F4 on Industry: the prompt window over it.
        await client.PressAsync(Click(9, 3));
        await client.TypeAsync("2");
        await client.EnterAsync();
        await client.WaitForTextAsync("Customer - Change");
        Dump(client, "detail");
        await client.PressAsync(Click(7, 22));
        await client.PressAsync(new FunctionKey("F4"));
        await client.WaitForTextAsync("Select an industry");
        Dump(client, "prompt");
        await client.PressAsync(Click(4 + 4, 30 + 2 + 2));
        await client.TypeAsync("1");
        await client.EnterAsync();
        await client.WaitForTextAsync("Customer - Change");
        Dump(client, "detail2");

        // F8: contacts, two lines each.
        await client.PressAsync(new FunctionKey("F8"));
        await client.WaitForTextAsync("Contacts");
        Dump(client, "contacts");
        await client.PressAsync(new FunctionKey("F6"));
        await client.WaitForTextAsync("Add contact for");
        Dump(client, "contactwindow");
        await client.PressAsync(new FunctionKey("F12"));
        await client.WaitForTextAsync("Name / Email");
        await client.PressAsync(new FunctionKey("F12"));
        await client.WaitForTextAsync("Customer - Change");

        // F7: activities.
        await client.PressAsync(new FunctionKey("F7"));
        await client.WaitForTextAsync("Showing:");
        Dump(client, "activities");
        await client.PressAsync(new FunctionKey("F6"));
        await client.WaitForTextAsync("Add activity");
        Dump(client, "activitywindow");
        await client.PressAsync(new FunctionKey("F12"));
        await client.WaitForTextAsync("Showing:");
        await client.PressAsync(new FunctionKey("F12"));
        await client.WaitForTextAsync("Customer - Change");
        await client.EnterAsync();
        await client.WaitForTextAsync("customers shown");
        Dump(client, "customers2");

        // F21: the print window, over the list.
        await client.PressAsync(new FunctionKey("F21"));
        await client.WaitForTextAsync("Print reports");
        Dump(client, "printwindow");
        await client.EnterAsync();
        await client.WaitForTextAsync("customers shown");
        Dump(client, "customers3");
        await client.PressAsync(new FunctionKey("F3"));
        await client.WaitForTextAsync("CRM Demo - Main Menu");

        // 3: the pipeline, 27 x 132.
        await client.PressAsync(Click(21, 7));
        await client.TypeAsync("3");
        await client.EnterAsync();
        await client.WaitForTextAsync("Sales Pipeline");
        Dump(client, "pipeline");
        await client.PressAsync(new FunctionKey("F3"));
        await client.WaitForTextAsync("CRM Demo - Main Menu");

        // 4: the dashboard.
        await client.PressAsync(Click(21, 7));
        await client.TypeAsync("4");
        await client.EnterAsync();
        await client.WaitForTextAsync("Live Dashboard");
        Dump(client, "dashboard");
        await Task.Delay(2500);
        Dump(client, "dashboard2");
        await client.PressAsync(new FunctionKey("F3"));
        await client.WaitForTextAsync("CRM Demo - Main Menu");
        await client.PressAsync(new FunctionKey("F3"));
        await client.WaitForTextAsync("IBM i Main Menu");
        Dump(client, "end");
        var spool = Assert.Single(machine.Store.Spools(), s => s.Name == "CRMCUST");
        Assert.Equal("CRMUSR1", spool.User);
        Assert.True(spool.Pages > 1);
        Assert.StartsWith("CRMDEMO", spool.Text);
        Assert.Contains("* Harper Quinn", spool.Text);
    }

    /// <summary>The row (1 first) the text is on, or 0.</summary>
    private static int RowOf(TestClient client, string text) =>
        Plain(client).Split('\n').Select((line, i) => (line, i)).FirstOrDefault(x => x.line.Contains(text)).i is var i && Plain(client).Split('\n')[i].Contains(text) ? i + 1 : 0;

    private static async Task OptionAsync(TestClient client, string text, int column, string option)
    {
        var row = RowOf(client, text);
        Assert.True(row > 0, $"{text} isn't on the screen:\n{Plain(client)}");
        await client.PressAsync(Click(row, column));
        await client.TypeAsync(option);
        await client.EnterAsync();
    }

    private static async Task ReplaceAsync(TestClient client, int row, int column, int length, string text)
    {
        await client.PressAsync(Click(row, column));
        await client.PressAsync(new FunctionKey("FieldExit"));
        await client.PressAsync(Click(row, column));
        await client.TypeAsync(text);
    }

    [Fact]
    public async Task TheDemoScriptInFourSessions()
    {
        using var machine = CrmTests.Loaded();
        await using var host = await StartAsync(machine);
        await using var a = await SignOnAsync(host, "CRMUSR1");
        await using var b = await SignOnAsync(host, "CRMUSR2");
        await using var c = await SignOnAsync(host, "CRMUSR3");
        await using var d = await SignOnAsync(host, "CRMUSR2");

        await CommandAsync(a, "CRMDEMO/CRM *DASH");
        await a.WaitForTextAsync("Live Dashboard");
        await CommandAsync(b, "CRMDEMO/CRM *CUST");
        await b.WaitForTextAsync("customers shown");
        await CommandAsync(c, "CRMDEMO/CRM *CUST");
        await c.WaitForTextAsync("customers shown");
        await CommandAsync(d, "CRMDEMO/CRM *PIPE");
        await d.WaitForTextAsync("Sales Pipeline");

        // 1. Live updates: B changes Acme's phone; A's feed shows it within about 2 seconds.
        await OptionAsync(b, "Acme Widget Works", 3, "2");
        await b.WaitForTextAsync("Customer - Change");
        await ReplaceAsync(b, 10, 22, 15, "(216) 555-9999");
        await b.EnterAsync();
        await b.WaitForTextAsync("customers shown");
        Assert.Equal("(216) 555-9999", Text(machine, "SELECT PHONE FROM CRMDEMO.CUSTOMER WHERE CUSTID = 1001"));
        await a.WaitForAsync(x => Plain(x).Split('\n')[16].Contains("Updated customer Acme Widget Works"), "the change in A's feed");

        // 2. Edit conflict: B and C both change Acme; C saves first, then B gets the red window.
        await OptionAsync(b, "Acme Widget Works", 3, "2");
        await b.WaitForTextAsync("Customer - Change");
        await OptionAsync(c, "Acme Widget Works", 3, "2");
        await c.WaitForTextAsync("Customer - Change");
        await ReplaceAsync(c, 10, 22, 15, "(216) 555-3333");
        await c.EnterAsync();
        await c.WaitForTextAsync("customers shown");
        await ReplaceAsync(b, 10, 22, 15, "(216) 555-2222");
        await b.EnterAsync();
        await b.WaitForTextAsync("Changed by");
        Assert.Contains("CRMUSR3 at", Plain(b));
        await b.PressAsync(new FunctionKey("F12"));
        await b.WaitForTextAsync("Your changes are still on the screen.");
        await b.EnterAsync();
        await b.WaitForTextAsync("Changed by");
        await b.PressAsync(new FunctionKey("F10"));
        await b.WaitForTextAsync("customers shown");
        Assert.Equal("(216) 555-2222", Text(machine, "SELECT PHONE FROM CRMDEMO.CUSTOMER WHERE CUSTID = 1001"));

        // 3. B gives CRMUSR1 a task, picking the user with F4: A beeps and shows the alert.
        await OptionAsync(b, "Copperline Foods", 3, "7");
        await b.WaitForTextAsync("Showing:");
        await b.PressAsync(new FunctionKey("F6"));
        await b.WaitForTextAsync("Add activity");
        await ReplaceAsync(b, 7, 3 + 13, 5, "TASK");
        await b.PressAsync(Click(8, 3 + 13));
        await b.TypeAsync("Call about the renewal");
        await b.PressAsync(Click(14, 3 + 13));
        await b.PressAsync(new FunctionKey("F4"));
        await b.WaitForTextAsync("Select a CRM user");
        await OptionAsync(b, "Jordan Lee", 33, "1");
        await b.WaitForAsync(x => Plain(x).Contains("Add activity") && Plain(x).Contains("CRMUSR1") && !x.Screen.KeyboardLocked, "CRMUSR1 picked");
        await b.EnterAsync();
        await b.WaitForTextAsync("Activity added.");
        await a.WaitForAsync(x => Plain(x).Contains("For you from CRMUSR2") && Plain(x).Contains("Call about the renewal"), "the alert on A");

        // 4. D moves a Negotiation deal on: it's won, and the feed says so.
        int NegotiationRow() => Enumerable.Range(11, 14).FirstOrDefault(r => Plain(d).Split('\n')[r - 1].Contains("Negotiation"));
        for (var page = 0; page < 6 && NegotiationRow() == 0; page++)
        {
            await d.PressAsync(new FunctionKey("RollUp"));
            await Task.Delay(200);
            await d.WaitForTextAsync("Sales Pipeline");
        }
        var row = NegotiationRow();
        Assert.True(row > 0, Plain(d));
        await d.PressAsync(Click(row, 2));
        await d.TypeAsync("3");
        await d.EnterAsync();
        await d.WaitForTextAsync("moved to Won");
        await a.WaitForAsync(x => Plain(x).Contains("WON!"), "WON! in A's feed");

        // 5. B emails a contact: F8, 9, F10.
        await b.PressAsync(new FunctionKey("F12"));
        await b.WaitForTextAsync("customers shown");
        await OptionAsync(b, "Acme Widget Works", 3, "8");
        await b.WaitForTextAsync("Name / Email");
        await OptionAsync(b, "Harper Quinn", 3, "9");
        await b.WaitForTextAsync("Send email");
        await ReplaceAsync(b, 5, 2 + 1 + 13, 60, "me@example.org");
        await b.PressAsync(new FunctionKey("F10"));
        await b.WaitForTextAsync("Email sent to me@example.org.");
        Assert.Equal(1, Count(machine, "SELECT count(*) FROM CRMDEMO.ACTIVITY WHERE ACTTYPE = 'EMAIL' AND NOTES LIKE 'To me@example.org%'"));

        // F3 all the way out: back to the command line.
        await a.PressAsync(new FunctionKey("F3"));
        await a.WaitForTextAsync("IBM i Main Menu");
    }

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
            // A CHAR column's value comes back padded with blanks, as Db2 gives it.
            return (Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? "").TrimEnd();
        }
    }

    [Fact]
    public async Task TheCrmCommandChecksItsValueAndStartsWhereItSays()
    {
        using var machine = CrmTests.Loaded();
        await using var host = await StartAsync(machine);
        await using var client = await SignOnAsync(host, "CRMUSR2");

        await CommandAsync(client, "CRMDEMO/CRM *NOPE");
        await client.WaitForTextAsync("not a valid value");
        await CommandAsync(client, "CRMDEMO/CRM START(*TASKS)");
        await client.WaitForTextAsync("Open Tasks");
        Assert.Contains("Assigned to: CRMUSR2 Sam Patel", Plain(client));
        await client.PressAsync(new FunctionKey("F3"));
        await client.WaitForTextAsync("IBM i Main Menu");

        // *PRINT: the options window over the command line, then the report's completion message.
        await CommandAsync(client, "CRMDEMO/CRM *PRINT");
        await client.WaitForTextAsync("Print reports");
        await client.PressAsync(Click(5 + 2, 12 + 1 + 16));
        await client.TypeAsync("2");
        await client.EnterAsync();
        await client.WaitForTextAsync("Report CRMPIPE sent to output queue *JOB.");
        var spool = Assert.Single(machine.Store.Spools(), s => s.Name == "CRMPIPE");
        Assert.Contains("Open pipeline total:", spool.Text);
    }
}
