using System.Text;

namespace SimI.Tests;

/// <summary>Fixed-form RPG: H, F, D, C, I and O specs, and the RPG cycle.</summary>
public class FixedFormTests
{
    /// <summary>Puts text in columns: each (column, text) pair, 1-based, on a line of spaces.</summary>
    private static string Spec(char type, params (int Column, string Text)[] parts)
    {
        var line = new StringBuilder(new string(' ', 80));
        line[5] = type;
        foreach (var (column, text) in parts)
        {
            for (var i = 0; i < text.Length; i++) line[column - 1 + i] = text[i];
        }
        return line.ToString().TrimEnd();
    }

    private static string C(string op, string factor1 = "", string factor2 = "", string result = "", string length = "", string decimals = "",
        string hi = "", string lo = "", string eq = "", string condition = "", string level = "") =>
        Spec('C', (7, level), (9, condition), (12, factor1), (26, op), (36, factor2), (50, result), (64, length.PadLeft(5)), (69, decimals.PadLeft(2)),
            (71, hi), (73, lo), (75, eq));

    private static string Extended(string op, string expression, string condition = "") => Spec('C', (9, condition), (26, op), (36, expression));

    private static string D(string name, string kind, string length = "", char type = ' ', string decimals = "", string keywords = "", string from = "") =>
        Spec('D', (7, name), (24, kind), (26, from.PadLeft(7)), (33, length.PadLeft(7)), (40, type.ToString()), (41, decimals.PadLeft(2)), (44, keywords));

    private static (IReadOnlyList<string> Shown, string? Error) Run(Machine machine, string name, IEnumerable<string> lines, params string[] parameters)
    {
        var member = new Member("QGPL", "QRPGLESRC", name, "RPGLE", string.Join('\n', lines) + "\n");
        var found = machine.Save(member);
        var compiled = machine.Compile(member);
        Assert.True(compiled.Ok, $"{compiled.Message} {string.Join("; ", found.Where(d => d.Severity == Severity.Error).Select(d => $"line {d.Line}: {d.Message}"))}");
        Assert.DoesNotContain(found, d => d.Severity == Severity.Unsupported);
        var job = new BatchJob("PRTUSER", CancellationToken.None);
        var result = machine.Run($"CALL {name} ({string.Join(' ', parameters)})", job);
        return (job.Messages, result.Ok ? null : result.Message);
    }

    [Fact]
    public void CalculationsWithIndicatorsAndTheOldOperations()
    {
        using var machine = Machine.Open();
        var (shown, error) = Run(machine, "CALC",
        [
            Spec('H', (7, "DFTACTGRP(*NO)")),
            D("TOTAL", "S", "5", 'P', "0"),
            D("NAME", "S", "10", 'A', "", "INZ('ABCDEFGHIJ')"),
            D("SHORT", "S", "3", 'A'),
            D("I", "S", "3", 'P', "0"),
            D("ARR", "S", "2", 'A', "", "DIM(3) CTDATA PERRCD(3)"),
            C("Z-ADD", factor2: "99990", result: "TOTAL"),
            C("ADD", factor2: "15", result: "TOTAL", hi: "50"),
            C("DSPLY", factor1: "TOTAL"),
            C("MOVE", factor2: "NAME", result: "SHORT"),
            C("DSPLY", factor1: "SHORT"),
            C("MOVEL", factor2: "NAME", result: "SHORT"),
            C("DSPLY", factor1: "SHORT"),
            C("IFEQ", factor1: "SHORT", factor2: "'ABC'"),
            C("ANDGT", factor1: "TOTAL", factor2: "0"),
            C("DSPLY", factor1: "'both'"),
            C("ENDIF"),
            C("DO", factor1: "1", factor2: "3", result: "I"),
            C("CASEQ", factor1: "I", factor2: "2", result: "TWO"),
            C("CAS", result: "OTHER"),
            C("ENDCS"),
            C("ENDDO"),
            C("SETON", hi: "60"),
            C("DSPLY", factor1: "'sixty'", condition: "60"),
            C("DSPLY", factor1: "'not 60'", condition: "N60"),
            C("GOTO", factor2: "SKIP"),
            C("DSPLY", factor1: "'skipped'"),
            C("TAG", factor1: "SKIP"),
            C("LOOKUP", factor1: "'BB'", factor2: "ARR", eq: "70"),
            C("DSPLY", factor1: "'found BB'", condition: "70"),
            C("SETON", hi: "LR"),
            C("BEGSR", factor1: "TWO"),
            C("DSPLY", factor1: "'two'"),
            C("ENDSR"),
            C("BEGSR", factor1: "OTHER"),
            C("DSPLY", factor1: "'other'"),
            C("ENDSR"),
            "**CTDATA ARR",
            "AABBCC",
        ]);
        Assert.True(error is null, error);
        // 99990 + 15 is 100005: five digits keep 5, and HI is on for it.
        Assert.Equal(["5", "HIJ", "ABC", "both", "other", "two", "other", "sixty", "found BB"], shown);
    }

    [Fact]
    public void SequenceNumbersCopyLinesBlankSpecsAndParameterFieldsAreAsTheCompilerTakesThem()
    {
        using var machine = Machine.Open();
        machine.Save(new Member("QGPL", "QRPGLESRC", "GREETING", "RPGLEINC", D("HELLO", "C", keywords: "'hello'") + "\n"));
        var (shown, error) = Run(machine, "SEQ",
        [
            // Anything in columns 1 to 5 is a sequence number the compiler ignores.
            "00100" + D("ONE", "DS")[5..],
            "AB\u0080  " + D("PART", "", "4", 'A')[5..],
            // A spec with nothing after its letter is empty; a directive can follow a spec letter.
            "     D",
            "     D/COPY QGPL/QRPGLESRC,GREETING",
            C("PLIST", factor1: "*ENTRY"),
            C("PARM", result: "WHEN", length: "8"),
            Extended("EVAL", "PART = 'abcd'"),
            "  10" + C("DSPLY", factor1: "PART")[4..],
            C("DSPLY", factor1: "HELLO"),
            C("DSPLY", factor1: "WHEN"),
            C("SETON", hi: "LR"),
        ], "'20261114'");
        Assert.True(error is null, error);
        Assert.Equal(["abcd", "hello", "20261114"], shown);
    }

    [Fact]
    public void AnOperationOnARecordFormatSetsItsIndicatorsFromThatOperation()
    {
        using var machine = Machine.Open();
        var job = new BatchJob("PRTUSER", CancellationToken.None);
        Assert.True(machine.Run("RUNSQL SQL('create table qgpl.line (ord char(4) not null, seq int not null, primary key (ord, seq))')", job).Ok);
        Assert.True(machine.Run("RUNSQL SQL('insert into qgpl.line values (''A'', 1), (''A'', 2), (''B'', 1)')", job).Ok);
        var (shown, error) = Run(machine, "LINES",
        [
            Spec('F', (7, "LINE"), (17, "IF"), (22, "E"), (34, "K"), (36, "DISK"), (44, "RENAME(LINE:LINEREC)")),
            D("COUNT", "S", "3", 'P', "0"),
            C("SETLL", factor1: "'A'", factor2: "LINEREC", eq: "40"),
            C("DSPLY", factor1: "'equal'", condition: "40"),
            C("READE", factor1: "'A'", factor2: "LINEREC", eq: "50"),
            C("DOWEQ", factor1: "*IN50", factor2: "*OFF"),
            C("ADD", factor2: "1", result: "COUNT"),
            C("READE", factor1: "'A'", factor2: "LINEREC", eq: "50"),
            C("ENDDO"),
            C("DSPLY", factor1: "COUNT"),
            C("SETON", hi: "LR"),
        ]);
        Assert.True(error is null, error);
        Assert.Equal(["equal", "2"], shown);
    }

    [Fact]
    public void ALabelOnEndsrIsWhereGotoEndsTheSubroutine()
    {
        using var machine = Machine.Open();
        var (shown, error) = Run(machine, "EARLY",
        [
            C("EXSR", factor2: "CHECK"),
            C("SETON", hi: "LR"),
            C("BEGSR", factor1: "CHECK"),
            C("DSPLY", factor1: "'in'"),
            C("CABEQ", factor1: "1", factor2: "1", result: "CHKEND"),
            C("DSPLY", factor1: "'skipped'"),
            C("ENDSR", factor1: "CHKEND"),
        ]);
        Assert.True(error is null, error);
        Assert.Equal(["in"], shown);
    }

    [Fact]
    public void ExternalSubfieldsAreRenamedAndAProcedureIsCalledWithoutParentheses()
    {
        using var machine = Machine.Open();
        var job = new BatchJob("PRTUSER", CancellationToken.None);
        Assert.True(machine.Run("RUNSQL SQL('create table qgpl.dc11 (compny char(3) not null, divnam char(10) not null)')", job).Ok);
        var (shown, error) = Run(machine, "EXTFLDS",
        [
            "     D DC11DS        E DS                  EXTNAME(DC11)",
            "     D  DC11CO       E                     EXTFLD(COMPNY)",
            "     D  DIVNAM       E                     INZ('North')",
            "     D QQFENV          PR            10I 0 Extproc('QqfEnvironment')",
            "     D RC              S             10I 0",
            "      * Check for web environment",
            "     C                   EVAL      DC11CO = '001'",
            "     C                   EVAL      RC = QQFENV",
            C("DSPLY", factor1: "DC11CO"),
            C("DSPLY", factor1: "DIVNAM"),
            C("DSPLY", factor1: "DC11DS"),
            C("DSPLY", factor1: "RC"),
            C("SETON", hi: "LR"),
        ]);
        Assert.Null(error);
        Assert.Equal(["001", "North", "001North", "0"], shown);
    }

    [Fact]
    public void InputSpecsRenameFieldsAndSqlRunsInFixedForm()
    {
        using var machine = Machine.Open();
        var job = new BatchJob("PRTUSER", CancellationToken.None);
        Assert.True(machine.Run("RUNSQL SQL('create table qgpl.comp (glcomp char(3) not null primary key, glname char(10) not null)')", job).Ok);
        Assert.True(machine.Run("RUNSQL SQL('insert into qgpl.comp values (''001'', ''Main'')')", job).Ok);
        var member = new Member("QGPL", "QRPGLESRC", "RENAMES", "SQLRPGLE", string.Join('\n',
        [
            Spec('F', (7, "COMP"), (17, "IF"), (22, "E"), (34, "K"), (36, "DISK"), (44, "RENAME(COMP:COMPREC)")),
            D("COUNT", "S", "5", 'P', "0"),
            Spec('I', (7, "COMPREC")),
            Spec('I', (21, "GLCOMP"), (49, "GSCOMP")),
            C("CHAIN", factor1: "'001'", factor2: "COMP"),
            C("DSPLY", factor1: "GSCOMP"),
            "     C/EXEC SQL",
            "     C+ SELECT COUNT(*) INTO :COUNT",
            "     C+   FROM COMP WHERE GLCOMP = :GSCOMP",
            "     C/END-EXEC",
            C("DSPLY", factor1: "COUNT"),
            C("SETON", hi: "LR"),
        ]) + "\n");
        var found = machine.Save(member);
        Assert.True(machine.Compile(member).Ok, string.Join("; ", found.Where(d => d.Severity == Severity.Error).Select(d => $"{d.Line}: {d.Message}")));
        var run = new BatchJob("PRTUSER", CancellationToken.None);
        Assert.True(machine.Run("CALL RENAMES", run).Ok);
        Assert.Equal(["001", "1"], run.Messages);
    }

    [Fact]
    public void FixedAndFreeFormBlocksCanCloseEachOther()
    {
        using var machine = Machine.Open();
        var (shown, error) = Run(machine, "MIXED",
        [
            D("TOTAL", "S", "5", 'P', "0"),
            D("NDX", "S", "3", 'P', "0"),
            D("BASE", "DS"),
            D("PART", "", "4", 'A'),
            D("COPY", "DS", keywords: "LIKEDS(BASE)"),
            C("DO", factor2: "3", result: "NDX"),
            "         if ndx > 1;",
            "           total += ndx;",
            "         endif;",
            "       enddo;",
            Extended("EVAL", "COPY.PART = 'abcd'"),
            C("DSPLY", factor1: "TOTAL"),
            C("DSPLY", factor1: "COPY.PART"),
            C("SETON", hi: "LR"),
        ]);
        Assert.True(error is null, error);
        Assert.Equal(["5", "abcd"], shown);
    }

    [Fact]
    public void CalculationsCanDefineFieldsAgainAndLikeSpecialWords()
    {
        using var machine = Machine.Open();
        var job = new BatchJob("PRTUSER", CancellationToken.None);
        Assert.True(machine.Run("CRTDTAARA DTAARA(QGPL/NEXTNO) TYPE(*CHAR) LEN(5) VALUE('00042')", job).Ok);
        var (shown, error) = Run(machine, "AGAIN",
        [
            D("DATA", "DS"),
            D("CODE", "", "3", 'A'),
            C("MOVE", factor2: "'ABC'", result: "CODE", length: "3"),
            C("DEFINE", factor1: "*LIKE", factor2: "*DATE", result: "TODAY"),
            C("DEFINE", factor1: "*LIKE", factor2: "*INLR", result: "FLAG"),
            C("DEFINE", factor1: "*DTAARA", factor2: "NEXTNO", result: "NEXT"),
            C("IN", factor1: "*LOCK", factor2: "*DTAARA"),
            C("Z-ADD", factor2: "20261114", result: "TODAY"),
            C("MOVE", factor2: "*ON", result: "FLAG"),
            C("DSPLY", factor1: "DATA"),
            C("DSPLY", factor1: "TODAY"),
            C("DSPLY", factor1: "NEXT"),
            C("UNLOCK", factor2: "*DTAARA"),
            C("SETON", hi: "LR"),
        ]);
        Assert.True(error is null, error);
        Assert.Equal(["ABC", "20261114", "00042"], shown.Select(x => x.TrimEnd()));
    }

    [Fact]
    public void FreeFormUsesKeyListsAndTheSpecialFields()
    {
        using var machine = Machine.Open();
        var job = new BatchJob("PRTUSER", CancellationToken.None);
        Assert.True(machine.Run("RUNSQL SQL('create table qgpl.price (item char(4) not null, size char(1) not null, amount decimal(7,2) not null, primary key (item, size))')", job).Ok);
        Assert.True(machine.Run("RUNSQL SQL('insert into qgpl.price values (''TEE'', ''L'', 14)')", job).Ok);
        var (shown, error) = Run(machine, "SPECIAL",
        [
            Spec('F', (7, "PRICE"), (17, "IF"), (22, "E"), (34, "K"), (36, "DISK"), (44, "RENAME(PRICE:PRICEREC)")),
            D("WHEN", "S", type: 'T', keywords: "TIMFMT(*HMS:)"),
            C("KLIST", factor1: "KEY"),
            C("KFLD", result: "ITEM"),
            C("KFLD", result: "SIZE"),
            C("Z-ADD", factor2: "0", result: "PAGE"),
            "       item = 'TEE';",
            "       size = 'L';",
            "       chain key price;",
            "       if %found(price) and udate > 0 and page = 0;",
            "         dsply %char(amount);",
            "       endif;",
            C("SETON", hi: "LR"),
        ]);
        Assert.True(error is null, error);
        Assert.Equal(["14.00"], shown);
    }

    [Fact]
    public void KeyListsChainAndEntryParameters()
    {
        using var machine = Machine.Open();
        var job = new BatchJob("PRTUSER", CancellationToken.None);
        Assert.True(machine.Run("RUNSQL SQL('create table qgpl.price (item char(4) not null, size char(1) not null, amount decimal(7,2) not null, primary key (item, size))')", job).Ok);
        Assert.True(machine.Run("RUNSQL SQL('insert into qgpl.price values (''TEE'', ''M'', 12.50), (''TEE'', ''L'', 14)')", job).Ok);
        var (shown, error) = Run(machine, "PRICE",
        [
            Spec('F', (7, "PRICE"), (17, "IF"), (22, "E"), (34, "K"), (36, "DISK"), (44, "RENAME(PRICE:PRICEREC)")),
            D("WANT", "S", "1", 'A'),
            C("PLIST", factor1: "*ENTRY"),
            C("PARM", result: "WANT"),
            C("KLIST", factor1: "KEY"),
            C("KFLD", result: "ITEM"),
            C("KFLD", result: "SIZE"),
            C("MOVEL(P)", factor2: "'TEE'", result: "ITEM"),
            C("MOVE", factor2: "WANT", result: "SIZE"),
            C("CHAIN", factor1: "KEY", factor2: "PRICE", hi: "90"),
            C("DSPLY", factor1: "'none'", condition: "90"),
            Extended("EVAL", "*IN91 = NOT *IN90"),
            C("DSPLY", factor1: "AMOUNT", condition: "91"),
            C("SETON", hi: "LR"),
        ], "'L'");
        Assert.True(error is null, error);
        Assert.Equal(["14.00"], shown);
    }

    [Fact]
    public void TheCycleReadsThePrimaryFileWithControlBreaksAndPrintsTotals()
    {
        using var machine = Machine.Open();
        var job = new BatchJob("PRTUSER", CancellationToken.None);
        Assert.True(machine.Run("RUNSQL SQL('create table qgpl.sale (region char(2) not null, item char(6) not null, amount decimal(5,0) not null, primary key (region, item))')", job).Ok);
        Assert.True(machine.Run("RUNSQL SQL('insert into qgpl.sale values (''NE'', ''A1'', 10), (''NE'', ''B2'', 5), (''SW'', ''C3'', 7)')", job).Ok);
        var (_, error) = Run(machine, "SALES",
        [
            Spec('F', (7, "SALE"), (17, "IP"), (22, "E"), (34, "K"), (36, "DISK"), (44, "RENAME(SALE:SALEREC)")),
            Spec('F', (7, "QPRINT"), (17, "O"), (22, "F"), (23, "  132"), (36, "PRINTER")),
            D("REGTOT", "S", "7", 'P', "0"),
            D("GRAND", "S", "7", 'P', "0"),
            Spec('I', (7, "SALEREC"), (21, "01")),
            Spec('I', (21, "REGION"), (63, "L1")),
            C("ADD", factor2: "AMOUNT", result: "REGTOT", condition: "01"),
            C("ADD", factor2: "REGTOT", result: "GRAND", level: "L1"),
            Spec('O', (7, "QPRINT"), (17, "H"), (21, "1P")),
            Spec('O', (53, "'SALES BY REGION'")),
            Spec('O', (7, "QPRINT"), (17, "D"), (21, "01")),
            Spec('O', (30, "REGION"), (47, "    5")),
            Spec('O', (30, "ITEM"), (47, "   15")),
            Spec('O', (30, "AMOUNT"), (44, "Z"), (47, "   25")),
            Spec('O', (7, "QPRINT"), (17, "T"), (21, "L1")),
            Spec('O', (30, "REGTOT"), (44, "Z"), (45, "B"), (47, "   25")),
            Spec('O', (53, "'REGION TOTAL'")),
            Spec('O', (7, "QPRINT"), (17, "T"), (21, "LR")),
            Spec('O', (30, "GRAND"), (44, "Z"), (47, "   25")),
            Spec('O', (53, "'GRAND TOTAL'")),
        ]);
        Assert.True(error is null, error);
        var spool = Assert.Single(machine.Store.Spools());
        var lines = spool.Text.Split('\n').Where(l => l.Trim().Length > 0).Select(l => l.TrimEnd()).ToList();
        Assert.Equal(
        [
            // Each field ends at its end position: REGION at 5, ITEM at 15, AMOUNT (edited) at 25; a constant follows the field before it.
            "SALES BY REGION",
            "   NE    A1            10",
            "   NE    B2             5",
            "                       15REGION TOTAL",
            "   SW    C3             7",
            "                        7REGION TOTAL",
            "                       22GRAND TOTAL",
        ], lines);
    }
}
