namespace SimI.Tests;

/// <summary>CL programs: GOTO and labels, data areas, the job's attributes, and declared files.</summary>
public class ClTests
{
    private static List<string> Call(Machine machine, string name, string source)
    {
        var member = new Member("QGPL", "QCLLESRC", name, "CLLE", source);
        Assert.DoesNotContain(machine.Save(member), d => d.Severity is Severity.Error or Severity.Unsupported);
        var compiled = machine.Compile(member);
        Assert.True(compiled.Ok, compiled.Message);
        var job = new BatchJob("PRTUSER", CancellationToken.None);
        var result = machine.Run($"CALL {name}", job);
        Assert.True(result.Ok, result.Message);
        return [.. job.State.Messages];
    }

    [Fact]
    public void GotoGoesBackForwardAndIntoAGroup()
    {
        using var machine = Machine.Open();
        Assert.Equal(["count 3", "in the group", "done"], Call(machine, "JUMPS", """
            PGM
              DCL VAR(&N) TYPE(*DEC) LEN(3 0) VALUE(0)
              DCL VAR(&SKIP) TYPE(*LGL) VALUE('1')
            AGAIN:
              CHGVAR VAR(&N) VALUE(&N + 1)
              IF COND(&N *LT 3) THEN(GOTO CMDLBL(AGAIN))
              SNDPGMMSG MSG('count' *BCAT %CHAR(&N))
              GOTO INSIDE
              IF COND(&SKIP) THEN(DO)
                SNDPGMMSG MSG('never')
            INSIDE: SNDPGMMSG MSG('in the group')
              ENDDO
              GOTO CMDLBL(END)
              SNDPGMMSG MSG('skipped')
            END:
              SNDPGMMSG MSG('done')
            ENDPGM
            """));
    }

    [Fact]
    public void DataAreasAndTheJobAreRetrieved()
    {
        using var machine = Machine.Open();
        var job = new BatchJob("PRTUSER", CancellationToken.None);
        Assert.True(machine.Run("CRTDTAARA DTAARA(QGPL/CTL) TYPE(*CHAR) LEN(20) VALUE('ABCDEFGHIJ')", job).Ok);
        Assert.Equal(["CDE PRTUSER batch", "now XYZDEFGHIJ"], Call(machine, "AREAS", """
            PGM
              DCL VAR(&PART) TYPE(*CHAR) LEN(3)
              DCL VAR(&ALL) TYPE(*CHAR) LEN(20)
              DCL VAR(&USER) TYPE(*CHAR) LEN(10)
              DCL VAR(&TYPE) TYPE(*CHAR) LEN(1)
              RTVDTAARA DTAARA(QGPL/CTL (3 3)) RTNVAR(&PART)
              RTVJOBA USER(&USER) TYPE(&TYPE)
              IF COND(&TYPE *EQ '0') THEN(SNDPGMMSG MSG(&PART *BCAT &USER *BCAT 'batch'))
              CHGDTAARA DTAARA(QGPL/CTL (1 3)) VALUE('XYZ')
              RTVDTAARA DTAARA(QGPL/CTL) RTNVAR(&ALL)
              SNDPGMMSG MSG('now' *BCAT &ALL)
            ENDPGM
            """));
    }

    [Fact]
    public void RcvfReadsADatabaseFileInKeyOrderToItsEnd()
    {
        using var machine = Machine.Open();
        var job = new BatchJob("PRTUSER", CancellationToken.None);
        Assert.True(machine.Run("RUNSQL SQL('create table qgpl.city (code char(3) not null primary key, pop decimal(7, 0) not null)')", job).Ok);
        Assert.True(machine.Run("RUNSQL SQL('insert into qgpl.city values (''NYC'', 8000), (''BOS'', 650), (''CHI'', 2700)')", job).Ok);
        Assert.Equal(["BOS 650", "CHI 2700", "NYC 8000", "3 read"], Call(machine, "CITIES", """
            PGM
              DCLF FILE(CITY)
              DCL VAR(&COUNT) TYPE(*DEC) LEN(3 0)
            LOOP:
              RCVF
              MONMSG MSGID(CPF0864) EXEC(GOTO CMDLBL(DONE))
              CHGVAR VAR(&COUNT) VALUE(&COUNT + 1)
              SNDPGMMSG MSG(&CODE *BCAT %CHAR(&POP))
              GOTO LOOP
            DONE:
              SNDPGMMSG MSG(%CHAR(&COUNT) *BCAT 'read')
            ENDPGM
            """));
    }

    [Fact]
    public void FilesAreOverriddenCopiedClearedDuplicatedAndExported()
    {
        using var machine = Machine.Open();
        var job = new BatchJob("PRTUSER", CancellationToken.None);
        Assert.True(machine.Run("RUNSQL SQL('create table qgpl.city (code char(3) not null primary key, pop decimal(7, 0) not null)')", job).Ok);
        Assert.True(machine.Run("RUNSQL SQL('insert into qgpl.city values (''NYC'', 8000), (''BOS'', 650), (''CHI'', 2700)')", job).Ok);
        Assert.True(machine.Run("RUNSQL SQL('create table qgpl.town (code char(3) not null primary key, pop decimal(7, 0) not null)')", job).Ok);
        var counter = new Member("QGPL", "QRPGLESRC", "COUNTER", "RPGLE", """
            **FREE
            dcl-f city keyed;
            dcl-s n int(10);
            read city;
            dow not %eof(city);
              n += 1;
              read city;
            enddo;
            dsply ('rows ' + %char(n));
            *inlr = *on;
            """);
        machine.Save(counter);
        Assert.True(machine.Compile(counter).Ok);
        var messages = new Member("QGPL", "QMSGFSRC", "OPSMSG", "MSGF", """
            CRTMSGF MSGF(&O/&N)
            ADDMSGD MSGID(OPS0001) MSGF(&O/&N) MSG('Copied &1 records.') SEV(0)
            """);
        Assert.True(Assert.Single(machine.Build([messages])).Result.Ok);

        var source = new Member("QGPL", "QCLLESRC", "FILEOPS", "CLLE", """
            PGM
              CPYF FROMFILE(CITY) TOFILE(QGPL/BIG) MBROPT(*REPLACE) CRTFILE(*YES) +
                   INCREL((*IF POP *GT 1000))
              OVRDBF FILE(CITY) TOFILE(QGPL/BIG)
              CALL PGM(COUNTER)
              DLTOVR FILE(CITY)
              CRTDUPOBJ OBJ(CITY) FROMLIB(QGPL) OBJTYPE(*FILE) TOLIB(QGPL) NEWOBJ(CITY2) DATA(*YES)
              CPYF FROMFILE(CITY2) TOFILE(TOWN) MBROPT(*ADD)
              CLRPFM FILE(CITY2)
              OVRDBF FILE(CITY) TOFILE(CITY2)
              CALL PGM(COUNTER)
              DLTOVR FILE(*ALL)
              CALL PGM(COUNTER)
              MKDIR DIR('/home')
              MONMSG MSGID(CPFA0A0)
              CPYTOIMPF FROMFILE(TOWN) TOSTMF('/home/town.csv') MBROPT(*REPLACE) RCDDLM(*LF)
              SNDPGMMSG MSGID(OPS0001) MSGF(OPSMSG) MSGDTA('3')
            ENDPGM
            """);
        Assert.DoesNotContain(machine.Save(source), d => d.Severity is Severity.Error or Severity.Unsupported);
        Assert.True(machine.Compile(source).Ok);
        var run = new BatchJob("PRTUSER", CancellationToken.None);
        var result = machine.Run("CALL FILEOPS", run);
        Assert.True(result.Ok, result.Message);
        Assert.Equal(["rows 2", "rows 0", "rows 3"], run.Messages);
        Assert.Equal(["Copied 3 records."], run.State.Messages);
        Assert.Equal("\"BOS\",650\n\"CHI\",2700\n\"NYC\",8000\n", machine.Ifs.ReadText("/home/town.csv"));
    }

    [Fact]
    public void AnOpenQuerySelectsMapsOrdersJoinsAndGroupsWhatAProgramReads()
    {
        using var machine = Machine.Open();
        var job = new BatchJob("PRTUSER", CancellationToken.None);
        Assert.True(machine.Run("RUNSQL SQL('create table qgpl.sale (branch char(2) not null, item char(4) not null, sold decimal(8, 0) not null, qty decimal(5, 0) not null, primary key (branch, item))')", job).Ok);
        Assert.True(machine.Run("RUNSQL SQL('insert into qgpl.sale values (''01'', ''A1'', 20261114, 5), (''01'', ''B2'', 20260101, 7), (''02'', ''A1'', 20250601, 2), (''03'', ''C3'', 20261001, 9)')", job).Ok);
        Assert.True(machine.Run("RUNSQL SQL('create table qgpl.branch (code char(2) not null primary key, bname char(10) not null)')", job).Ok);
        Assert.True(machine.Run("RUNSQL SQL('insert into qgpl.branch values (''01'', ''North''), (''02'', ''South''), (''03'', ''East'')')", job).Ok);
        // The format the program reads: a field mapped from the date's digits.
        Assert.True(machine.Run("RUNSQL SQL('create table qgpl.salefmt (branch char(2) not null, item char(4) not null, qty decimal(5, 0) not null, year char(4) not null)')", job).Ok);
        Assert.True(machine.Run("RUNSQL SQL('create table qgpl.totals (bname char(10) not null, total decimal(7, 0) not null)')", job).Ok);
        var reader = new Member("QGPL", "QRPGLESRC", "READSALE", "RPGLE", """
            **FREE
            dcl-f salefmt;
            read salefmt;
            dow not %eof(salefmt);
              dsply (branch + ' ' + item + ' ' + %char(qty) + ' ' + year);
              read salefmt;
            enddo;
            *inlr = *on;
            """);
        machine.Save(reader);
        Assert.True(machine.Compile(reader).Ok);
        var totals = new Member("QGPL", "QRPGLESRC", "READTOT", "RPGLE", """
            **FREE
            dcl-f totals;
            read totals;
            dow not %eof(totals);
              dsply (%trim(bname) + ' ' + %char(total));
              read totals;
            enddo;
            *inlr = *on;
            """);
        machine.Save(totals);
        Assert.True(machine.Compile(totals).Ok);

        var source = new Member("QGPL", "QCLLESRC", "QUERY", "CLLE", """
            PGM
              DCL VAR(&LOW) TYPE(*CHAR) LEN(2) VALUE('01')
              DCL VAR(&SLT) TYPE(*CHAR) LEN(100)
              CHGVAR VAR(&SLT) VALUE('BRANCH *EQ %RANGE("' *CAT &LOW *CAT '" "02") *AND QTY *GT 3')
              OVRDBF FILE(SALEFMT) TOFILE(SALE) SHARE(*YES)
              OPNQRYF FILE((SALE)) FORMAT(SALEFMT) QRYSLT(&SLT) +
                      KEYFLD((QTY *DESCEND)) MAPFLD((YEAR '%SST(%DIGITS(SOLD) 1 4)'))
              CALL PGM(READSALE)
              CPYFRMQRYF FROMOPNID(SALE) TOFILE(QGPL/PICKED) MBROPT(*REPLACE) CRTFILE(*YES)
              CLOF OPNID(SALE)
              DLTOVR FILE(*ALL)
              CLOF OPNID(SALE)
              MONMSG MSGID(CPF4520) EXEC(SNDPGMMSG MSG('closed'))
              OVRDBF FILE(TOTALS) TOFILE(SALE) SHARE(*YES)
              OPNQRYF FILE((SALE) (BRANCH)) FORMAT(TOTALS) JFLD((1/BRANCH 2/CODE)) +
                      GRPFLD((BNAME)) KEYFLD((BNAME)) MAPFLD((TOTAL '%SUM(QTY)'))
              CALL PGM(READTOT)
              CLOF OPNID(SALE)
            ENDPGM
            """);
        Assert.DoesNotContain(machine.Save(source), d => d.Severity is Severity.Error or Severity.Unsupported);
        Assert.True(machine.Compile(source).Ok);
        var run = new BatchJob("PRTUSER", CancellationToken.None);
        var result = machine.Run("CALL QUERY", run);
        Assert.True(result.Ok, result.Message);
        Assert.Equal(["01 B2   7 2026", "01 A1   5 2026", "East 9", "North 12", "South 2"], run.Messages);
        Assert.Equal(["closed"], run.State.Messages);
        Assert.Equal(2, machine.FindDatabaseFile("QGPL/PICKED", "QGPL") is { } picked ? Rows(machine, picked.Table) : -1);
    }

    private static int Rows(Machine machine, string table)
    {
        lock (machine.Store.Gate)
        {
            using var command = machine.Store.Connection.CreateCommand();
            command.CommandText = $"SELECT count(*) FROM {table}";
            return Convert.ToInt32(command.ExecuteScalar());
        }
    }

    [Fact]
    public void DatesConvertMembersAreDescribedAndRecordsImported()
    {
        using var machine = Machine.Open();
        var job = new BatchJob("PRTUSER", CancellationToken.None);
        Assert.True(machine.Run("RUNSQL SQL('create table qgpl.price (item char(4) not null, amount decimal(7, 2) not null)')", job).Ok);
        Assert.True(machine.Run("MKDIR DIR('/in')", job).Ok);
        machine.Ifs.WriteText("/in/price.csv", "\"A1\",1.50\n\"B2\",20\n");
        Assert.Equal(["20261114 14/11/26", "2026318", "2 records", "reply Y"], Call(machine, "MISC", """
            PGM
              DCL VAR(&MDY) TYPE(*CHAR) LEN(8) VALUE('11/14/26')
              DCL VAR(&YMD) TYPE(*CHAR) LEN(8)
              DCL VAR(&DMY) TYPE(*CHAR) LEN(8)
              DCL VAR(&JUL) TYPE(*DEC) LEN(7 0)
              DCL VAR(&COUNT) TYPE(*DEC) LEN(10 0)
              DCL VAR(&REPLY) TYPE(*CHAR) LEN(1)
              CVTDAT DATE(&MDY) TOVAR(&YMD) FROMFMT(*MDY) TOFMT(*YYMD) TOSEP(*NONE)
              CVTDAT DATE(&YMD) TOVAR(&DMY) FROMFMT(*YYMD) TOFMT(*DMY)
              SNDPGMMSG MSG(&YMD *BCAT &DMY)
              CVTDAT DATE(&MDY) TOVAR(&JUL) FROMFMT(*MDY) TOFMT(*LONGJUL) TOSEP(*NONE)
              SNDPGMMSG MSG(%CHAR(&JUL))
              CPYFRMIMPF FROMSTMF('/in/price.csv') TOFILE(PRICE) MBROPT(*REPLACE) RCDDLM(*LF)
              RTVMBRD FILE(PRICE) NBRCURRCD(&COUNT)
              SNDPGMMSG MSG(%CHAR(&COUNT) *BCAT 'records')
              SNDUSRMSG MSG('Go on?') VALUES(Y N) DFT(Y) MSGRPY(&REPLY)
              SNDPGMMSG MSG('reply' *BCAT &REPLY)
            ENDPGM
            """));
    }

    [Fact]
    public void ANameEndingInSlashStarIsntACommentAndPromptsAreRunAsTheyAre()
    {
        using var machine = Machine.Open();
        Assert.Equal(["prompted", "after"], Call(machine, "PROMPTS", """
            PGM /* a comment */
              ALCOBJ OBJ((QGPL/*ALL *FILE *SHRRD)) /* names ending in / and * aren't +
                comments *//* and a comment can follow one */
              ?SNDPGMMSG ??MSG('prompted')
              ?          SNDPGMMSG MSG('after')
            ENDPGM
            """));
    }

    [Fact]
    public void CallprcCallsAServiceProgramsProcedure()
    {
        using var machine = Machine.Open();
        var service = new Member("QGPL", "QRPGLESRC", "MATHS", "RPGLE", """
            **FREE
            ctl-opt nomain;
            dcl-proc double export;
              dcl-pi *n packed(9 : 2);
                value packed(9 : 2);
                label char(10);
              end-pi;
              label = 'doubled';
              return value * 2;
            end-proc;
            """);
        machine.Save(service);
        Assert.True(machine.Compile(service).Ok);
        Assert.Equal(["42.00 doubled"], Call(machine, "USEPRC", """
            PGM
              DCL VAR(&IN) TYPE(*DEC) LEN(9 2) VALUE(21)
              DCL VAR(&OUT) TYPE(*DEC) LEN(9 2)
              DCL VAR(&LABEL) TYPE(*CHAR) LEN(10)
              CALLPRC PRC('DOUBLE') PARM((&IN) (&LABEL)) RTNVAL(&OUT)
              SNDPGMMSG MSG(%CHAR(&OUT) *BCAT &LABEL)
            ENDPGM
            """));
    }

    [Fact]
    public void RcvmsgReceivesTheMessageMonmsgCaught()
    {
        using var machine = Machine.Open();
        Assert.Equal(["CPF9801 Object NOTHERE in library QGPL type *FILE not found."], Call(machine, "CAUGHT", """
            PGM
              DCL VAR(&ID) TYPE(*CHAR) LEN(7)
              DCL VAR(&TEXT) TYPE(*CHAR) LEN(132)
              CHKOBJ OBJ(QGPL/NOTHERE) OBJTYPE(*FILE)
              MONMSG MSGID(CPF0000) EXEC(DO)
                RCVMSG MSGTYPE(*EXCP) MSGID(&ID) MSG(&TEXT)
                SNDPGMMSG MSG(&ID *BCAT &TEXT)
              ENDDO
            ENDPGM
            """));
    }
}
