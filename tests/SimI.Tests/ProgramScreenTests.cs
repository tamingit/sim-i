using SimI.Tests.Client;

namespace SimI.Tests;

/// <summary>Programs, display files and the source screens, through a real 5250 session.</summary>
public class ProgramScreenTests
{
    private static Key Click(int row, int column) => new CursorKey(row, column);

    private static async Task<TestClient> SignOnAsync(TelnetServer host)
    {
        var client = await TestClient.ConnectAsync(host, null);
        await client.WaitForTextAsync("Sign On");
        await client.PressAsync(Click(6, 53));
        await client.TypeAsync("PRTUSER");
        await client.PressAsync(Click(7, 53));
        await client.TypeAsync("Secret12");
        await client.EnterAsync();
        await client.WaitForTextAsync("IBM i Main Menu");
        return client;
    }

    /// <summary>The screen's text with each display attribute as the blank it shows as.</summary>
    private static string Plain(TestClient client) => System.Text.RegularExpressions.Regex.Replace(client.Text(), "[\u0080-\u009f]", " ");

    private static async Task CommandAsync(TestClient client, string command)
    {
        await client.PressAsync(Click(20, 7));
        await client.TypeAsync(command);
        await client.EnterAsync();
    }

    private static async Task TypeAtAsync(TestClient client, int row, int column, string text)
    {
        await client.PressAsync(Click(row, column));
        await client.TypeAsync(text);
    }

    /// <summary>A DDS line, with each part in its columns.</summary>
    private static string Dds(string keywords = "", string spec = " ", string name = "", string length = "", string type = " ",
        string decimals = "", string usage = " ", string row = "", string column = "", string when = "") =>
        $"     A {when,-9}{spec} {name,-10} {length,5}{type}{decimals,2}{usage}{row,3}{column,3}{keywords}";

    private static void Create(Machine machine, string name, string type, string source)
    {
        var member = new Member("QGPL", Machine.DefaultFile(type), name, type, source);
        Assert.DoesNotContain(machine.Save(member), d => d.Severity == Severity.Unsupported);
        Assert.True(machine.Compile(member).Ok);
    }

    [Fact]
    public async Task DsplyShowsItsMessagesAndTakesAReply()
    {
        using var machine = Machine.Open();
        Create(machine, "GREET", "RPGLE", """
            **FREE
            dcl-s name char(10);
            dsply 'Who are you?' '' name;
            dsply ('Hello ' + %trim(name) + '.');
            return;
            """);
        await using var host = await TelnetServer.StartAsync(machine: machine);
        await using var client = await SignOnAsync(host);

        await CommandAsync(client, "call greet");
        await client.WaitForTextAsync("Type reply, press Enter.");
        Assert.Contains("Display Program Messages", client.Text());
        Assert.Contains("Who are you?", client.Text());
        await client.TypeAsync("Ada");
        await client.EnterAsync();
        await client.WaitForTextAsync("Hello Ada.");
        Assert.Contains("Press Enter to continue.", client.Text());
        await client.EnterAsync();
        await client.WaitForTextAsync("IBM i Main Menu");
    }

    [Fact]
    public async Task CommandsThatFailSayWhyOnTheMenu()
    {
        await using var host = await TelnetServer.StartAsync();
        await using var client = await SignOnAsync(host);

        await CommandAsync(client, "call nothere");
        await client.WaitForTextAsync("Program NOTHERE in library *LIBL not found.");
        await CommandAsync(client, "wrkactjob");
        await client.WaitForTextAsync("Command WRKACTJOB in library *LIBL not found.");
        await CommandAsync(client, "crtbndrpg pgm(qgpl/nosrc)");
        await client.WaitForTextAsync("Member NOSRC not found in file QRPGLESRC.");
    }

    [Fact]
    public async Task AProgramShowsItsDisplayFileAndReadsWhatIsTyped()
    {
        using var machine = Machine.Open();
        Create(machine, "CUSTD", "DSPF", string.Join('\n',
            Dds("DSPSIZ(24 80 *DS3)"),
            Dds("CA03(03 'Exit')"),
            Dds(spec: "R", name: "ENTRY"),
            Dds("'Customer Entry'", row: "1", column: "30"),
            Dds("DSPATR(HI)"),
            Dds("'Number . . .'", row: "3", column: "2"),
            Dds("EDTCDE(Z)", name: "CUSTNO", length: "5", type: "Y", decimals: "0", usage: "B", row: "3", column: "16"),
            Dds("'Name . . . .'", row: "4", column: "2"),
            Dds("CHECK(LC)", name: "NAME", length: "20", type: "A", usage: "B", row: "4", column: "16"),
            Dds("ERRMSG('Name required' 50)", when: " 50"),
            Dds("'Balance  . .'", row: "5", column: "2"),
            Dds("EDTCDE(J)", name: "BALANCE", length: "9", type: "Y", decimals: "2", usage: "O", row: "5", column: "16"),
            Dds("'F3=Exit'", row: "23", column: "2")));
        Create(machine, "CUSTPGM", "RPGLE", """
            **FREE
            dcl-f custd workstn;
            dcl-s saved int(10);

            balance = 1234.5;
            dow not *in03;
              exfmt entry;
              if *in03;
                leave;
              endif;
              *in50 = (name = *blanks);
              if not *in50;
                saved += 1;
                balance += custno;
              endif;
            enddo;
            dsply ('saved ' + %char(saved) + ' for ' + %trim(name));
            *inlr = *on;
            """);
        await using var host = await TelnetServer.StartAsync(machine: machine);
        await using var client = await SignOnAsync(host);

        await CommandAsync(client, "CALL PGM(CUSTPGM)");
        await client.WaitForTextAsync("Customer Entry");
        Assert.Contains("Balance  . .      1,234.50", Plain(client));

        await TypeAtAsync(client, 3, 16, "7");
        await client.EnterAsync();
        await client.WaitForTextAsync("Name required");

        await TypeAtAsync(client, 4, 16, "Ada");
        await client.EnterAsync();
        await client.WaitForTextAsync("1,241.50");
        Assert.DoesNotContain("Name required", client.Text());

        await client.PressAsync(new FunctionKey("F5"));
        await client.WaitForTextAsync("Function key not allowed.");
        await client.PressAsync(new FunctionKey("F3"));
        await client.WaitForTextAsync("saved 1 for Ada");
        await client.EnterAsync();
        await client.WaitForTextAsync("IBM i Main Menu");
    }

    /// <summary>A DDS line with A (and) or O (or) in column 7, joining its option indicators to the line before's.</summary>
    private static string Joined(char andOr, string line) => line[..6] + andOr + line[7..];

    [Fact]
    public async Task DisplayKeywordsCheckWhatIsTypedAndShowMessagesFromAMessageFile()
    {
        using var machine = Machine.Open();
        var messages = new Member("QGPL", "QMSGFSRC", "APPMSG", "MSGF", """
                 /* Source generated by ARCAD Software */
            CRTMSGF MSGF(&O/&N) CCSID(65535)
            ADDMSGD MSGID(APP0001) MSGF(&O/&N) +
               MSG('Code must be +
            A or B.') SEV(0)
            ADDMSGD MSGID(APP0002) MSGF(&O/&N) MSG('Quantity &1 is too big.') SEV(10) +
               FMT((*CHAR 5))
            """);
        var built = Assert.Single(machine.Build([messages]));
        Assert.True(built.Result.Ok, built.Result.Message);
        Assert.Equal("Code must be A or B.", machine.MessageText("APP0001", "APPMSG"));

        Create(machine, "ORDERD", "DSPF", string.Join('\n',
            Dds("CA03(03 'Exit')"),
            Dds(spec: "R", name: "SCREEN"),
            Dds("CHANGE(60)"),
            Dds(spec: "H"),
            Dds("HLPARA(1 1 24 80)"),
            Dds("'Order entry'", row: "1", column: "30"),
            Dds("VALUES('A' 'B')", name: "CODE", length: "1", type: "A", usage: "B", row: "3", column: "2"),
            Dds("CHKMSGID(APP0001 APPMSG)"),
            Dds(name: "QTY", length: "5", type: "Y", decimals: "0", usage: "B", row: "4", column: "2"),
            Dds("ERRMSGID(APP0002 APPMSG 41)", when: " 41"),
            Dds("EDTWRD('  ,   .  ')", name: "AMOUNT", length: "7", type: "Y", decimals: "2", usage: "O", row: "5", column: "2"),
            Dds(when: " 51"),
            Joined('O', Dds(when: " 52")),
            Joined('A', Dds("'Either'", when: " 53", row: "6", column: "2")),
            Dds(name: "PF", length: "1", type: "A", usage: "P"),
            Dds("DSPATR(&PF)", name: "NOTE", length: "10", type: "A", usage: "B", row: "7", column: "2")));
        Create(machine, "ORDERPGM", "RPGLE", """
            **FREE
            dcl-f orderd workstn;
            amount = 12345.67;
            pf = x'21';
            *in52 = *on;
            *in53 = *on;
            dow not *in03;
              exfmt screen;
              if *in03;
                leave;
              endif;
              *in41 = qty > 100;
              if *in60 and not *in41;
                dsply ('changed ' + code);
                leave;
              endif;
            enddo;
            *inlr = *on;
            """);
        await using var host = await TelnetServer.StartAsync(machine: machine);
        await using var client = await SignOnAsync(host);

        await CommandAsync(client, "CALL ORDERPGM");
        await client.WaitForTextAsync("Order entry");
        Assert.Contains("12,345.67", Plain(client));
        Assert.Contains("Either", Plain(client));
        // The P field's attribute (x'21', reverse image) is the field's.
        Assert.Equal(0x21, client.Screen.Buffer[6 * 80]);

        await TypeAtAsync(client, 3, 2, "C");
        await client.EnterAsync();
        await client.WaitForTextAsync("Code must be A or B.");

        await TypeAtAsync(client, 3, 2, "B");
        await TypeAtAsync(client, 4, 2, "500");
        await client.EnterAsync();
        await client.WaitForTextAsync("Quantity  is too big.");

        await TypeAtAsync(client, 4, 2, "00007");
        await client.EnterAsync();
        await client.WaitForTextAsync("changed B");
    }

    [Fact]
    public async Task SubfilesFoldMarkRecordsChangedAndShowProgramMessages()
    {
        using var machine = Machine.Open();
        Create(machine, "LISTD", "DSPF", string.Join('\n',
            Dds("CA03(03)"),
            Dds("SFL", spec: "R", name: "ROW"),
            Dds("SFLNXTCHG", when: " 70"),
            Dds(name: "OPT", length: "1", type: "A", usage: "B", row: "6", column: "3"),
            Dds(name: "ITEM", length: "10", type: "A", usage: "O", row: "6", column: "7"),
            Dds(name: "DESC", length: "20", type: "A", usage: "O", row: "7", column: "7"),
            Dds("SFLCTL(ROW)", spec: "R", name: "CTL"),
            Dds("SFLSIZ(20) SFLPAG(3)"),
            Dds("SFLDROP(CF11)"),
            Dds("OVERLAY"),
            Dds("CSRLOC(CROW CCOL)"),
            Dds("SFLDSP", when: " 40"),
            Dds("SFLDSPCTL", when: " 41"),
            Dds("SFLCLR", when: " 42"),
            Dds(name: "CROW", length: "3", type: "S", decimals: "0", usage: "H"),
            Dds(name: "CCOL", length: "3", type: "S", decimals: "0", usage: "H"),
            Dds("'Items'", row: "1", column: "30"),
            Dds("SFL SFLMSGRCD(24)", spec: "R", name: "MSGSFL"),
            Dds("SFLMSGKEY", name: "MSGKEY"),
            Dds("SFLPGMQ", name: "PGMQ"),
            Dds("SFLCTL(MSGSFL)", spec: "R", name: "MSGCTL"),
            Dds("SFLSIZ(5) SFLPAG(1) OVERLAY"),
            Dds("SFLDSP SFLDSPCTL SFLINZ"),
            Dds("SFLPGMQ", name: "PGMQ")));
        Create(machine, "LISTPGM", "RPGLE", """
            **FREE
            dcl-f listd workstn sfile(row : rrn);
            dcl-pr sendMessage extpgm('QMHSNDPM');
              id char(7) const;
              file char(20) const;
              data char(80) const;
              length int(10) const;
              type char(10) const;
              entry char(10) const;
              counter int(10) const;
              key char(4);
              error char(8);
            end-pr;
            dcl-s rrn int(5);
            dcl-s key char(4);
            dcl-s error char(8);

            *in42 = *on;
            write ctl;
            *in42 = *off;
            for rrn = 1 to 4;
              item = 'ITEM' + %char(rrn);
              desc = 'Desc ' + %char(rrn);
              *in70 = rrn = 2;
              write row;
            endfor;
            *in70 = *off;
            *in40 = *on;
            *in41 = *on;
            sendMessage('' : '' : 'Two is ready' : 12 : '*INFO' : '*' : 0 : key : error);
            pgmq = '*';
            crow = 6;
            ccol = 3;
            write msgctl;
            exfmt ctl;
            readc row;
            if not %eof;
              dsply ('next ' + %trim(item));
            endif;
            *inlr = *on;
            """);
        await using var host = await TelnetServer.StartAsync(machine: machine);
        await using var client = await SignOnAsync(host);

        await CommandAsync(client, "CALL LISTPGM");
        await client.WaitForTextAsync("Two is ready");
        // Truncated: one line a record, so all four fit, and no descriptions.
        Assert.Contains("ITEM4", client.Text());
        Assert.DoesNotContain("Desc 1", client.Text());
        Assert.Equal(new Client.Position(6, 3), client.Screen.Cursor);

        await client.PressAsync(new FunctionKey("F11"));
        await client.WaitForTextAsync("Desc 1");
        Assert.DoesNotContain("ITEM4", client.Text());

        await client.EnterAsync();
        await client.WaitForTextAsync("next ITEM2");
    }

    [Fact]
    public async Task AWindowTheProgramPlacesProtectsTheScreenUnderIt()
    {
        using var machine = Machine.Open();
        Create(machine, "ASKD", "DSPF", string.Join('\n',
            Dds(spec: "R", name: "ENTRY"),
            Dds("'Entry'", row: "1", column: "2"),
            Dds(name: "NAME", length: "10", type: "A", usage: "B", row: "3", column: "2"),
            Dds("WINDOW(&WROW &WCOL 3 20)", spec: "R", name: "CONFIRM"),
            Dds("PROTECT OVERLAY"),
            Dds(name: "WROW", length: "2", type: "S", decimals: "0", usage: "P"),
            Dds(name: "WCOL", length: "2", type: "S", decimals: "0", usage: "P"),
            Dds("'Sure?'", row: "1", column: "1"),
            Dds(name: "ANSWER", length: "1", type: "A", usage: "B", row: "2", column: "1")));
        Create(machine, "ASKPGM", "RPGLE", """
            **FREE
            dcl-f askd workstn;
            write entry;
            wrow = 10;
            wcol = 30;
            exfmt confirm;
            dsply ('answer ' + answer);
            *inlr = *on;
            """);
        await using var host = await TelnetServer.StartAsync(machine: machine);
        await using var client = await SignOnAsync(host);

        await CommandAsync(client, "CALL ASKPGM");
        await client.WaitForTextAsync("Sure?");
        var lines = client.Text().Split('\n');
        // The border's top left at line 10, position 30; inside it the record's fields.
        Assert.Contains("Sure?", lines[10]);
        Assert.Equal(30, lines[9].IndexOf('.'));
        // Only the window's field takes input: the entry screen's is protected.
        Assert.Single(client.Screen.Fields);
        await TypeAtAsync(client, 12, 32, "Y");
        await client.EnterAsync();
        await client.WaitForTextAsync("answer Y");
    }

    [Fact]
    public async Task ASubfilePagesByItselfAndReturnsTheChangedRecords()
    {
        using var machine = Machine.Open();
        Create(machine, "ITEMD", "DSPF", string.Join('\n',
            Dds("CA03(03)"),
            Dds("SFL", spec: "R", name: "ROW"),
            Dds(name: "OPT", length: "1", type: "A", usage: "B", row: "6", column: "3"),
            Dds(name: "ITEM", length: "10", type: "A", usage: "O", row: "6", column: "7"),
            Dds("EDTCDE(Z)", name: "QTY", length: "5", type: "Y", decimals: "0", usage: "O", row: "6", column: "20"),
            Dds("SFLCTL(ROW)", spec: "R", name: "CTL"),
            Dds("SFLSIZ(50) SFLPAG(5)"),
            Dds("OVERLAY"),
            Dds("SFLDSP", when: " 40"),
            Dds("SFLDSPCTL", when: " 41"),
            Dds("SFLCLR", when: " 42"),
            Dds("SFLEND(*MORE)", when: " 43"),
            Dds("'Items'", row: "1", column: "30"),
            Dds("'Opt Item          Qty'", row: "5", column: "2"),
            Dds(spec: "R", name: "FOOT"),
            Dds("'F3=Exit'", row: "23", column: "2")));
        Create(machine, "ITEMPGM", "RPGLE", """
            **FREE
            dcl-f itemd workstn sfile(row:rrn);
            dcl-s rrn int(5);
            dcl-s picked varchar(50);

            *in42 = *on;
            write ctl;
            *in42 = *off;
            for rrn = 1 to 12;
              opt = ' ';
              item = 'ITEM' + %char(rrn);
              qty = rrn * 10;
              write row;
            endfor;
            *in40 = *on;
            *in41 = *on;
            *in43 = *on;
            dow not *in03;
              write foot;
              exfmt ctl;
              if *in03;
                leave;
              endif;
              picked = '';
              readc row;
              dow not %eof;
                picked += %trim(item) + '=' + opt + ' ';
                opt = ' ';
                update row;
                readc row;
              enddo;
              dsply picked;
            enddo;
            *inlr = *on;
            """);
        await using var host = await TelnetServer.StartAsync(machine: machine);
        await using var client = await SignOnAsync(host);

        await CommandAsync(client, "call itempgm");
        await client.WaitForTextAsync("ITEM5");
        Assert.Contains("ITEM1           10", Plain(client));
        Assert.DoesNotContain("ITEM6", client.Text());
        Assert.Contains("More...", client.Text());
        Assert.Contains("F3=Exit", client.Text());

        await client.PressAsync(new FunctionKey("RollUp"));
        await client.WaitForTextAsync("ITEM10");
        await TypeAtAsync(client, 7, 3, "4"); // ITEM7
        await client.PressAsync(new FunctionKey("RollUp"));
        await client.WaitForTextAsync("Bottom");
        Assert.Contains("ITEM12", client.Text());
        await client.PressAsync(new FunctionKey("RollUp"));
        await client.WaitForTextAsync("Roll up or down past the first or last record in file.");

        await client.PressAsync(new FunctionKey("RollDown"));
        await client.WaitForTextAsync("ITEM7");
        await client.PressAsync(new FunctionKey("RollDown"));
        await client.WaitForTextAsync("ITEM1 ");
        await TypeAtAsync(client, 7, 3, "5"); // ITEM2
        await client.EnterAsync();
        // Back on the first page, the options cleared by the program.
        await client.WaitForAsync(c => c.Text().Contains("ITEM1 ") && !c.Screen.KeyboardLocked && host.Inputs.Count(i => i.Aid == 0xf1) >= 3, "the list again");

        await client.PressAsync(new FunctionKey("F3"));
        await client.WaitForTextAsync("ITEM2=5 ITEM7=4");
        await client.EnterAsync();
        await client.WaitForTextAsync("IBM i Main Menu");
    }

    [Fact]
    public async Task AMemberIsWrittenInTheEditorCheckedOnSavingCompiledAndCalled()
    {
        var lines = new List<string>();
        await using var host = await TelnetServer.StartAsync(log: line => { lock (lines) lines.Add(line); });
        await using var client = await SignOnAsync(host);

        await CommandAsync(client, "5");
        await client.WaitForTextAsync("System Options");
        await TypeAtAsync(client, 12, 20, "3");
        await client.EnterAsync();
        await client.WaitForTextAsync("Work with Members");
        Assert.Contains("No members", client.Text());
        await client.PressAsync(new FunctionKey("F6"));
        await client.WaitForTextAsync("Create Member");
        await TypeAtAsync(client, 6, 30, "hello");
        await client.EnterAsync();

        await client.WaitForTextAsync("QGPL/QRPGLESRC(HELLO)  RPGLE");
        Assert.Contains("0001   **FREE", Plain(client));
        await TypeAtAsync(client, 5, 8, "dsply 'Hi from the editor';");
        await TypeAtAsync(client, 6, 8, "  n = %nullind(n);");
        await TypeAtAsync(client, 7, 8, "*inlr = *on;");
        await client.PressAsync(new FunctionKey("F3"));

        await client.WaitForTextAsync("Member HELLO saved: 1 line not supported.");
        Assert.Contains("Member Diagnostics", client.Text());
        Assert.Contains("    3  Not supported  the built-in function %NULLIND isn't supported", client.Text());
        Assert.Equal("**FREE\ndsply 'Hi from the editor';\n  n = %nullind(n);\n*inlr = *on;\n", host.Machine.Store.FindMember(null, null, "HELLO")!.Source);
        await client.EnterAsync();

        // The list counts what isn't supported; 2 edits the member again, and D deletes the line.
        await client.WaitForTextAsync("Work with Members");
        Assert.Matches(@"HELLO +RPGLE +QRPGLESRC +QGPL +1", client.Text());
        await TypeAtAsync(client, 7, 3, "2");
        await client.EnterAsync();
        await client.WaitForAsync(c => Plain(c).Contains("0003     n = %nullind(n);") && !c.Screen.KeyboardLocked, "the editor");
        await TypeAtAsync(client, 6, 6, "d");
        await client.EnterAsync();
        await client.WaitForTextAsync("3 lines");
        Assert.DoesNotContain("nullind", client.Text());
        await client.PressAsync(new FunctionKey("F3"));
        await client.WaitForTextAsync("Member HELLO saved.");

        await TypeAtAsync(client, 7, 3, "14");
        await client.EnterAsync();
        await client.WaitForTextAsync("Program HELLO created in library QGPL.");
        await client.PressAsync(new FunctionKey("F3"));
        await client.WaitForTextAsync("IBM i Main Menu");

        await CommandAsync(client, "call hello");
        await client.WaitForTextAsync("Hi from the editor");
        await client.EnterAsync();
        await client.WaitForTextAsync("IBM i Main Menu");
        lock (lines)
        {
            Assert.Contains(lines, l => l.Contains("command CALL by PRTUSER"));
            Assert.Contains(lines, l => l.Contains("member QGPL/QRPGLESRC(HELLO) saved: 1 not supported"));
        }
    }

    [Fact]
    public async Task StrseuOpensAMemberAndF12LeavesItUnchanged()
    {
        using var machine = Machine.Open();
        machine.Save(new Member("QGPL", "QSQLSRC", "TABLES", "SQL", "create table t (a int);\n"));
        await using var host = await TelnetServer.StartAsync(machine: machine);
        await using var client = await SignOnAsync(host);

        await CommandAsync(client, "strseu srcfile(qsqlsrc) srcmbr(tables)");
        await client.WaitForTextAsync("QGPL/QSQLSRC(TABLES)  SQL");
        await TypeAtAsync(client, 5, 8, "drop table t;");
        await client.PressAsync(new FunctionKey("F12"));

        await client.WaitForTextAsync("Edit cancelled: member TABLES not changed.");
        Assert.Equal("create table t (a int);\n", machine.Store.FindMember(null, null, "TABLES")!.Source);
    }

    [Fact]
    public async Task ADisplayFileIsCheckedOnSaving()
    {
        using var machine = Machine.Open();

        var found = machine.Save(new Member("QGPL", "QDDSSRC", "BADD", "DSPF", string.Join('\n',
            Dds("DSPSIZ(*DS5)"),
            Dds("WINDOW(*DFT 10 40)", spec: "R", name: "POPUP"),
            Dds("EDTCDE(J XY)", name: "WHEN", length: "6", type: "Y", decimals: "0", usage: "O", row: "2", column: "2"),
            Dds(name: "GRAPH", length: "26", type: "G", usage: "O", row: "3", column: "2"),
            Dds("'ok'", row: "30", column: "2"))));

        Assert.Equal(
        [
            (1, "DSPSIZ(*DS5): only 24 by 80 (*DS3) and 27 by 132 (*DS4) isn't supported"),
            (3, "EDTCDE(J XY) isn't supported"),
            (4, "data type G isn't supported (GRAPH): use A, S or Y"),
            (5, "line 30 position 2 isn't in the window of 10 by 40"),
        ], found.Where(d => d.Severity != Severity.Info).Select(d => (d.Line, d.Message)));
    }

    /// <summary>The repository's examples/source folder.</summary>
    private static string Examples()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !Directory.Exists(Path.Combine(folder.FullName, "examples", "source"))) folder = folder.Parent;
        return Path.Combine(folder!.FullName, "examples", "source");
    }

    [Fact]
    public async Task TheExamplesAreSupportedAndTheCustomerListWorks()
    {
        using var machine = Machine.Open();
        var lines = new List<string>();
        Assert.Equal(0, machine.Import(Examples(), lines.Add));
        Assert.DoesNotContain(lines, l => l.Contains("warning") || l.Contains("not supported"));
        await using var host = await TelnetServer.StartAsync(machine: machine);
        await using var client = await SignOnAsync(host);

        await CommandAsync(client, "call hello ('Ada')");
        await client.WaitForTextAsync("Hello, Ada!");
        await client.EnterAsync();
        await client.WaitForTextAsync("IBM i Main Menu");

        await CommandAsync(client, "call custlist");
        await client.WaitForTextAsync("Work with Customers");
        Assert.Contains("Ada Lovelace", client.Text());
        Assert.Contains("Frances Allen", client.Text());
        Assert.DoesNotContain("John Backus", client.Text());
        Assert.Contains("More...", client.Text());
        await client.PressAsync(new FunctionKey("RollUp"));
        await client.WaitForTextAsync("Niklaus Wirth");

        // 5 beside Niklaus Wirth, the second on this page, then a new name.
        await TypeAtAsync(client, 8, 3, "5");
        await client.EnterAsync();
        await client.WaitForTextAsync("Enter=Save   F12=Cancel");
        Assert.Contains("Niklaus Wirth", client.Text());
        await TypeAtAsync(client, 5, 16, "N. Wirth     ");
        await client.EnterAsync();
        await client.WaitForTextAsync("Work with Customers");
        await client.PressAsync(new FunctionKey("RollUp"));
        await client.WaitForTextAsync("N. Wirth");
        Assert.DoesNotContain("Niklaus", client.Text());

        await client.PressAsync(new FunctionKey("F3"));
        await client.WaitForTextAsync("IBM i Main Menu");
    }

    [Fact]
    public async Task TheUserAppCreatesChangesAndDeletesUsersTheServersSignOn()
    {
        using var machine = Machine.Open();
        machine.Store.CreateLibrary("SALES");
        var installed = machine.InstallSystemSource();
        Assert.Equal(["QGPL/QDDSSRC(USRMNTD)", "QGPL/QRPGLESRC(USRMNT)"], installed.Select(m => m.ToString()));
        Assert.All(installed, m => Assert.DoesNotContain(machine.Store.Diagnostics(m), d => d.Severity != Severity.Info));
        Assert.Empty(machine.InstallSystemSource());
        var users = Users.Open(machine.Store, new Dictionary<string, string>());
        await using var host = await TelnetServer.StartAsync(users: users, machine: machine);
        await using var client = await SignOnAsync(host);
        // System Options on the main menu, then option 1.
        await CommandAsync(client, "5");
        await client.WaitForTextAsync("System Options");
        await TypeAtAsync(client, 12, 20, "1");
        await client.EnterAsync();
        await client.WaitForTextAsync("Work with Users");
        Assert.Contains("PRTUSER", client.Text());
        Assert.Contains("*DISABLED", client.Text());

        // F6: a new user needs a valid name and the same password twice.
        await client.PressAsync(new FunctionKey("F6"));
        await client.WaitForTextAsync("Create User");
        await client.EnterAsync();
        await client.WaitForTextAsync("A user name is 1 to 10");
        await TypeAtAsync(client, 4, 25, "ada");
        await TypeAtAsync(client, 5, 25, "Pass1");
        await TypeAtAsync(client, 6, 25, "Pass2");
        await client.EnterAsync();
        await client.WaitForTextAsync("The passwords are not the same");
        await TypeAtAsync(client, 6, 25, "Pass1");
        // The default library has to be one there is.
        await TypeAtAsync(client, 10, 25, "nope");
        await client.EnterAsync();
        await client.WaitForTextAsync("Library not found");
        await TypeAtAsync(client, 10, 25, "sales");
        await client.EnterAsync();
        await client.WaitForTextAsync("User ADA created.");
        Assert.Equal(SignOnResult.SignedOn, users.Check("ada", "Pass1").Result);
        Assert.Equal("SALES", users.Find("ADA")!.DefaultLibrary);
        Assert.Contains("SALES", client.Text());

        // 2 beside ADA (the first, by name): disable it. A blank password keeps the old one.
        await TypeAtAsync(client, 7, 3, "2");
        await client.EnterAsync();
        await client.WaitForTextAsync("Change User");
        await TypeAtAsync(client, 7, 25, "*DISABLED");
        await client.EnterAsync();
        await client.WaitForTextAsync("User ADA changed.");
        Assert.Equal(SignOnResult.Disabled, users.Check("ADA", "Pass1").Result);
        Assert.Equal("Pass1", users.Find("ADA")!.Password);

        // 4 beside ADA: delete it.
        await TypeAtAsync(client, 7, 3, "4");
        await client.EnterAsync();
        await client.WaitForTextAsync("Delete User");
        await client.EnterAsync();
        await client.WaitForTextAsync("User ADA deleted.");
        Assert.Equal(SignOnResult.UnknownUser, users.Check("ADA", "Pass1").Result);

        await client.PressAsync(new FunctionKey("F3"));
        await client.WaitForTextAsync("IBM i Main Menu");
    }

    [Fact]
    public async Task TheUserAppMakesGroupProfilesAndSaysWhoIsInThem()
    {
        using var machine = Machine.Open();
        machine.InstallSystemSource();
        var users = Users.Open(machine.Store, new Dictionary<string, string>());
        await using var host = await TelnetServer.StartAsync(users: users, machine: machine);
        await using var client = await SignOnAsync(host);
        await CommandAsync(client, "wrkusrprf");
        await client.WaitForTextAsync("Work with Users");

        // A group profile: password *NONE (no confirmation needed), so nobody signs on as it.
        await client.PressAsync(new FunctionKey("F6"));
        await client.WaitForTextAsync("Create User");
        await TypeAtAsync(client, 4, 25, "ggadmin");
        await TypeAtAsync(client, 5, 25, "*none");
        await client.EnterAsync();
        await client.WaitForTextAsync("User GGADMIN created.");
        Assert.Equal(SignOnResult.NoPassword, users.Check("GGADMIN", Users.NoPassword).Result);

        // A member: the group must be one there is, and the special authorities ones an IBM i has.
        await client.PressAsync(new FunctionKey("F6"));
        await client.WaitForTextAsync("Create User");
        await TypeAtAsync(client, 4, 25, "alice");
        await TypeAtAsync(client, 5, 25, "Alice123");
        await TypeAtAsync(client, 6, 25, "Alice123");
        await TypeAtAsync(client, 11, 25, "nobody");
        await client.EnterAsync();
        await client.WaitForTextAsync("Group profile: *NONE, or another");
        await TypeAtAsync(client, 11, 25, "ggadmin   ");
        await TypeAtAsync(client, 12, 25, "prtuser");
        await TypeAtAsync(client, 13, 25, "*secadm *bogus");
        await client.EnterAsync();
        await client.WaitForTextAsync("Special authorities are");
        await TypeAtAsync(client, 13, 25, "*secadm       ");
        await client.EnterAsync();
        await client.WaitForTextAsync("User ALICE created.");
        var alice = users.Find("ALICE")!;
        Assert.Equal("GGADMIN", alice.GroupProfile);
        Assert.Equal(["PRTUSER"], alice.SupplementalGroups);
        Assert.Equal(["*SECADM"], alice.SpecialAuthorities);
        Assert.Contains("GGADMIN", client.Text());

        // By name: ALICE, EXPUSER, GGADMIN (row 9). 5 lists its members; 4 won't delete it while it has them.
        await TypeAtAsync(client, 9, 3, "5");
        await client.EnterAsync();
        await client.WaitForTextAsync("Members of GGADMIN: ALICE");
        await TypeAtAsync(client, 9, 3, "4");
        await client.EnterAsync();
        await client.WaitForTextAsync("GGADMIN is the group profile of 1 users");
        Assert.NotNull(users.Find("GGADMIN"));

        // ...nor give it a group of its own.
        await TypeAtAsync(client, 9, 3, "2");
        await client.EnterAsync();
        await client.WaitForTextAsync("Change User");
        await TypeAtAsync(client, 11, 25, "prtuser   ");
        await client.EnterAsync();
        await client.WaitForTextAsync("Group profile: *NONE, or another");
        await client.PressAsync(new FunctionKey("F12"));
        await client.WaitForTextAsync("Work with Users");
        Assert.Null(users.Find("GGADMIN")!.GroupProfile);

        // ALICE leaves the group (and so its supplemental group goes too), then GGADMIN can go.
        await TypeAtAsync(client, 7, 3, "2");
        await client.EnterAsync();
        await client.WaitForTextAsync("Change User");
        await TypeAtAsync(client, 11, 25, "*none     ");
        await TypeAtAsync(client, 12, 25, "          ");
        await client.EnterAsync();
        await client.WaitForTextAsync("User ALICE changed.");
        Assert.Null(users.Find("ALICE")!.GroupProfile);
        Assert.Equal(SignOnResult.SignedOn, users.Check("ALICE", "Alice123").Result);
        await TypeAtAsync(client, 9, 3, "4");
        await client.EnterAsync();
        await client.WaitForTextAsync("Delete User");
        await client.EnterAsync();
        await client.WaitForTextAsync("User GGADMIN deleted.");
    }

    [Fact]
    public void TheUserAppAsAnEarlierSimIShippedItIsBroughtUpToDate()
    {
        using var machine = Machine.Open();
        var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        machine.Save(new Member("QGPL", "QRPGLESRC", "USRMNT", "SQLRPGLE", File.ReadAllText(Path.Combine(fixtures, "usrmnt-1.sqlrpgle"))));
        machine.Save(new Member("QGPL", "QDDSSRC", "USRMNTD", "DSPF", File.ReadAllText(Path.Combine(fixtures, "usrmntd-1.dspf"))));
        Assert.Equal(["QGPL/QDDSSRC(USRMNTD)", "QGPL/QRPGLESRC(USRMNT)"], machine.InstallSystemSource().Select(m => m.ToString()));
        Assert.Contains("GRPPRF", machine.Store.FindMember("QGPL", "QRPGLESRC", "USRMNT")!.Source, StringComparison.OrdinalIgnoreCase);

        // A copy someone changed is theirs: it stays.
        var mine = machine.Store.FindMember("QGPL", "QRPGLESRC", "USRMNT")!;
        machine.Save(mine with { Source = mine.Source + "// mine\n" });
        Assert.Empty(machine.InstallSystemSource());
        Assert.EndsWith("// mine\n", machine.Store.FindMember("QGPL", "QRPGLESRC", "USRMNT")!.Source);
    }

    [Fact]
    public async Task WrkLibListsMakesAndDeletesLibraries()
    {
        using var machine = Machine.Open();
        machine.Save(new Member("QGPL", "QRPGLESRC", "ONE", "RPGLE", "**FREE\nreturn;\n"));
        await using var host = await TelnetServer.StartAsync(machine: machine);
        await using var client = await SignOnAsync(host);
        await CommandAsync(client, "wrklib");
        await client.WaitForTextAsync("Work with Libraries");
        Assert.Contains("QGPL", client.Text());
        Assert.Contains("Your default library", client.Text());

        // F6: a name is needed, and one that's there already is refused.
        await client.PressAsync(new FunctionKey("F6"));
        await client.WaitForTextAsync("Create Library");
        await client.EnterAsync();
        await client.WaitForTextAsync("Library name required.");
        await TypeAtAsync(client, 6, 30, "qgpl");
        await client.EnterAsync();
        await client.WaitForTextAsync("Library QGPL already exists.");
        await TypeAtAsync(client, 6, 30, "sales");
        await client.EnterAsync();
        await client.WaitForTextAsync("Library SALES created.");
        Assert.Contains("SALES", client.Text());
        Assert.True(machine.Store.HasLibrary("SALES"));

        // 4 beside QGPL is refused; beside SALES it asks first.
        await TypeAtAsync(client, 7, 3, "4");
        await client.EnterAsync();
        await client.WaitForTextAsync("can't be deleted");
        await TypeAtAsync(client, 8, 3, "4");
        await client.EnterAsync();
        await client.WaitForTextAsync("Confirm Delete of Library");
        Assert.Contains("0 source members", client.Text());
        await client.PressAsync(new FunctionKey("F12"));
        await client.WaitForTextAsync("Work with Libraries");
        Assert.True(machine.Store.HasLibrary("SALES"));
        await TypeAtAsync(client, 8, 3, "4");
        await client.EnterAsync();
        await client.WaitForTextAsync("Confirm Delete of Library");
        await client.EnterAsync();
        await client.WaitForTextAsync("Library SALES deleted.");
        Assert.False(machine.Store.HasLibrary("SALES"));

        await client.PressAsync(new FunctionKey("F3"));
        await client.WaitForTextAsync("IBM i Main Menu");
    }

    [Fact]
    public async Task TheCustomerAppListsSearchesLooksUpAndCreates()
    {
        using var machine = Machine.Open();
        var lines = new List<string>();
        Assert.Equal(0, machine.Import(Path.Combine(Examples(), "..", "customers"), lines.Add));
        Assert.DoesNotContain(lines, l => l.Contains("warning") || l.Contains("not supported"));
        lock (machine.Store.Gate)
        {
            using var count = machine.Store.Connection.CreateCommand();
            count.CommandText = "select count(*) from customers";
            Assert.Equal(50L, count.ExecuteScalar());
        }
        await using var host = await TelnetServer.StartAsync(machine: machine);
        await using var client = await SignOnAsync(host);
        await CommandAsync(client, "call custmnt");
        await client.WaitForTextAsync("Customer Maintenance");

        // 1: the list, 12 to a page, then a search.
        await TypeAtAsync(client, 12, 20, "1");
        await client.EnterAsync();
        await client.WaitForTextAsync("Search name");
        Assert.Contains("More...", Plain(client));
        await TypeAtAsync(client, 4, 18, "lovelace");
        await client.EnterAsync();
        await client.WaitForAsync(c => !Plain(c).Contains("More...") && Plain(c).Contains("Lovelace"), "the search result");
        Assert.DoesNotContain("Hopper", client.Text());

        // 5 beside the first match shows its detail.
        await TypeAtAsync(client, 7, 3, "5");
        await client.EnterAsync();
        await client.WaitForTextAsync("Email");
        Assert.Contains("Rochester", client.Text());
        await client.EnterAsync();
        await client.WaitForTextAsync("Search name");
        await client.PressAsync(new FunctionKey("F12"));
        await client.WaitForTextAsync("Customer Maintenance");

        // 2: look up by number, first one that isn't there.
        await TypeAtAsync(client, 12, 20, "2");
        await client.EnterAsync();
        await client.WaitForTextAsync("Look Up Customer");
        await TypeAtAsync(client, 5, 20, "99");
        await client.EnterAsync();
        await client.WaitForTextAsync("Customer not found");
        await TypeAtAsync(client, 5, 20, "2");
        await client.EnterAsync();
        await client.WaitForTextAsync("Email");
        Assert.Contains("Austin", client.Text());
        await client.EnterAsync();
        await client.WaitForTextAsync("Customer Maintenance");

        // 3: a new customer needs a name.
        await TypeAtAsync(client, 12, 20, "3");
        await client.EnterAsync();
        await client.WaitForTextAsync("New Customer");
        await client.EnterAsync();
        await client.WaitForTextAsync("Name required");
        await TypeAtAsync(client, 5, 20, "Test Person");
        await TypeAtAsync(client, 6, 20, "Oslo");
        await client.EnterAsync();
        await client.WaitForTextAsync("Customer 51 created.");

        await TypeAtAsync(client, 12, 20, "2");
        await client.EnterAsync();
        await client.WaitForTextAsync("Look Up Customer");
        await TypeAtAsync(client, 5, 20, "51");
        await client.EnterAsync();
        await client.WaitForTextAsync("Email");
        Assert.Contains("Test Person", client.Text());
        Assert.Contains("Oslo", client.Text());
        await client.EnterAsync();
        await client.WaitForTextAsync("Customer Maintenance");

        // 4: print customer 3 from the menu.
        await TypeAtAsync(client, 12, 20, "4");
        await client.EnterAsync();
        await client.WaitForTextAsync("Print Customer Record");
        await TypeAtAsync(client, 5, 20, "3");
        await client.EnterAsync();
        await client.WaitForTextAsync("Customer 3 printed to spooled file CUSTPRT.");

        // And 6 beside the first customer in the list (by name: Alan ...).
        await TypeAtAsync(client, 12, 20, "1");
        await client.EnterAsync();
        await client.WaitForTextAsync("Search name");
        await TypeAtAsync(client, 7, 3, "6");
        await client.EnterAsync();
        await client.WaitForTextAsync("printed to spooled file CUSTPRT.");
        await client.PressAsync(new FunctionKey("F12"));
        await client.WaitForTextAsync("Customer Maintenance");
        await client.PressAsync(new FunctionKey("F3"));
        await client.WaitForTextAsync("IBM i Main Menu");

        // The run made one spooled file, as the print server lists it: SCS, EBCDIC lines.
        var spools = machine.Store.Spools();
        Assert.Single(spools);
        var scs = SpooledFile.From(spools[0]);
        Assert.Equal(("CUSTPRT", "*SCS", 1), (scs.Name, scs.DeviceType, scs.Pages));
        var text = Ebcdic.Decode(scs.Data).Replace('\u0085', '\n').Replace('\u000c', '\n');
        Assert.Contains("CUSTOMER RECORD\n---------------\nNumber  : 3\n", text);
        Assert.Contains("Email   : ", text);
        Assert.Contains("Balance : ", text);
        // Two records of ten lines each, from the one run of the program.
        Assert.Equal(20, spools[0].Text.Split('\n').Length);
        Assert.Equal(2, text.Split("CUSTOMER RECORD").Length - 1);
    }

    [Fact]
    public void AFolderOfSourceIsReadCheckedAndCreated()
    {
        var folder = Directory.CreateTempSubdirectory("sim-i-source");
        try
        {
            Directory.CreateDirectory(Path.Combine(folder.FullName, "MYLIB", "MYSRC"));
            File.WriteAllText(Path.Combine(folder.FullName, "MYLIB", "tables.sql"), "create table mylib.part (code char(5) not null, qty int);\ninsert into part values ('A1', 3);\n");
            File.WriteAllText(Path.Combine(folder.FullName, "count.sqlrpgle"),
                "**FREE\ndcl-s n int(10);\nexec sql select sum(qty) into :n from mylib/part;\ndsply %char(n);\nn = %nullind(n);\nreturn;\n");
            File.WriteAllText(Path.Combine(folder.FullName, "MYLIB", "MYSRC", "menu.dspf"), Dds(spec: "R", name: "MENU") + "\n" + Dds("'Menu'", row: "1", column: "2") + "\n");
            File.WriteAllText(Path.Combine(folder.FullName, "notes.txt"), "not source");
            using var machine = Machine.Open();
            var lines = new List<string>();

            var unsupported = machine.Import(folder.FullName, lines.Add);
            var again = machine.Import(folder.FullName, lines.Add);

            Assert.Equal(1, unsupported);
            Assert.Equal(0, again);
            Assert.Equal(
            [
                "source: library MYLIB created",
                "source: MYLIB/QSQLSRC(TABLES) SQL: 2 SQL statements run from MYLIB/QSQLSRC(TABLES).",
                "source: MYLIB/MYSRC(MENU) DSPF: File MENU created in library MYLIB.",
                "source: QGPL/QRPGLESRC(COUNT) SQLRPGLE: Program COUNT created in library QGPL. 1 line can't be run: see the member's diagnostics.",
                "source:   line 5: not supported: the built-in function %NULLIND isn't supported",
            ], lines);
            var job = new BatchJob("PRTUSER", CancellationToken.None);
            var result = machine.Run("CALL COUNT", job);
            Assert.Equal(["3"], job.Messages);
            Assert.Equal("COUNT reached line 5, which isn't supported: the built-in function %NULLIND isn't supported", result.Message);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }
}
