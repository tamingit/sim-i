namespace FakeIbmI.Tests;

/// <summary>Native record I/O: physical and logical files from DDS, and SQL tables, read and written by key.</summary>
public class DiskIoTests
{
    private const string Customer = """
             A                                      UNIQUE
             A          R CUSTREC
             A            CUSTNO         5P 0
             A            NAME          20A
             A            REGION         2A
             A            BAL            9P 2
             A          K CUSTNO
        """;

    private const string ByRegion = """
             A          R CUSTREC                   PFILE(CUSTPF)
             A          K REGION
             A          K CUSTNO
             A          S REGION                    COMP(NE 'XX')
        """;

    private static Machine WithFiles()
    {
        var machine = Machine.Open();
        var job = new BatchJob("PRTUSER", CancellationToken.None);
        machine.Save(new Member("QGPL", "QDDSSRC", "CUSTPF", "PF", Customer));
        machine.Save(new Member("QGPL", "QDDSSRC", "CUSTBYREG", "LF", ByRegion));
        var pf = machine.Run("CRTPF FILE(QGPL/CUSTPF) SRCFILE(QGPL/QDDSSRC)", job);
        Assert.True(pf.Ok, pf.Message);
        var lf = machine.Run("CRTLF FILE(QGPL/CUSTBYREG) SRCFILE(QGPL/QDDSSRC)", job);
        Assert.True(lf.Ok, lf.Message);
        return machine;
    }

    private static List<string> Run(Machine machine, string source)
    {
        var (shown, error) = RpgTests.Run(machine, source);
        Assert.Null(error);
        return [.. shown];
    }

    [Fact]
    public void WriteChainUpdateAndDelete()
    {
        using var machine = WithFiles();
        Assert.Equal(["Acme 10.50", "not found", "Acme 20.00", "duplicate", "3", "deleted"], Run(machine, """
            dcl-f custpf usage(*update : *delete : *output) keyed;
            dcl-s n int(10);
            custno = 1;
            name = 'Acme';
            region = 'NE';
            bal = 10.50;
            write custrec;
            custno = 2;
            name = 'Bolt';
            region = 'SW';
            write custrec;
            custno = 3;
            name = 'Cog';
            region = 'XX';
            write custrec;
            chain 1 custpf;
            if %found(custpf);
              dsply (%trim(name) + ' ' + %char(bal));
            endif;
            chain 9 custpf;
            if not %found(custpf);
              dsply 'not found';
            endif;
            chain 1 custrec;
            bal = 20;
            update custrec;
            chain(n) 1 custpf;
            dsply (%trim(name) + ' ' + %char(bal));
            custno = 1;
            write(e) custrec;
            if %error and %status = 1021;
              dsply 'duplicate';
            endif;
            setll *loval custpf;
            read custpf;
            dow not %eof(custpf);
              n += 1;
              read custpf;
            enddo;
            dsply %char(n);
            delete 2 custpf;
            chain 2 custpf;
            if not %found;
              dsply 'deleted';
            endif;
            *inlr = *on;
            """));
    }

    [Fact]
    public void ReadingByKeyForwardsAndBackwards()
    {
        using var machine = WithFiles();
        lock (machine.Store.Gate)
        {
            using var command = machine.Store.Connection.CreateCommand();
            command.CommandText = """
                INSERT INTO qgpl.custpf (custno, name, region, bal) VALUES (1, 'Acme', 'NE', 1), (2, 'Bolt', 'SW', 2), (3, 'Cog', 'NE', 3),
                    (4, 'Dent', 'XX', 4), (5, 'Elk', 'SW', 5), (6, 'Fig', 'NE', 6)
                """;
            command.ExecuteNonQuery();
        }
        Assert.Equal(["NE: Acme Cog Fig", "SW back: Elk Bolt", "equal", "after NE: Bolt", "all: Acme Cog Fig Bolt Elk", "last: Elk"], Run(machine, """
            dcl-f custbyreg keyed;
            dcl-ds key qualified;
              region char(2);
            end-ds;
            dcl-s list varchar(50);
            list = 'NE:';
            setll 'NE' custbyreg;
            reade 'NE' custbyreg;
            dow not %eof;
              list += ' ' + %trim(name);
              reade 'NE' custbyreg;
            enddo;
            dsply list;
            list = 'SW back:';
            setgt 'SW' custbyreg;
            readpe 'SW' custbyreg;
            dow not %eof;
              list += ' ' + %trim(name);
              readpe 'SW' custbyreg;
            enddo;
            dsply list;
            key.region = 'NE';
            setll %kds(key) custbyreg;
            if %equal(custbyreg);
              dsply 'equal';
            endif;
            setgt ('NE') custbyreg;
            read custbyreg;
            dsply ('after NE: ' + %trim(name));
            list = 'all:';
            setll *start custbyreg;
            read custbyreg;
            dow not %eof;
              list += ' ' + %trim(name);
              read custbyreg;
            enddo;
            dsply list;
            setgt *end custbyreg;
            readp custbyreg;
            dsply ('last: ' + %trim(name));
            *inlr = *on;
            """));
    }

    [Fact]
    public void AnSqlTableIsAKeyedFileWithAPrefixAndADataStructure()
    {
        using var machine = Machine.Open();
        var job = new BatchJob("PRTUSER", CancellationToken.None);
        Assert.True(machine.Run("RUNSQL SQL('create table qgpl.part (code char(4) not null primary key, qty int not null)')", job).Ok);
        Assert.True(machine.Run("RUNSQL SQL('insert into qgpl.part values (''B1'', 5), (''A1'', 3)')", job).Ok);
        Assert.Equal(["A1 3", "B1 5", "C1 7"], Run(machine, """
            dcl-f part keyed usage(*input : *output) prefix(p_) rename(part : partrec);
            dcl-ds row likerec(partrec);
            read part;
            dow not %eof(part);
              dsply (%trim(p_code) + ' ' + %char(p_qty));
              read part;
            enddo;
            row.p_code = 'C1';
            row.p_qty = 7;
            write partrec row;
            chain 'C1' part row;
            dsply (%trim(row.p_code) + ' ' + %char(row.p_qty));
            *inlr = *on;
            """).Skip(0).ToList() is var shown && shown.Count == 3 ? shown : throw new Exception(string.Join("|", shown)));
    }

    [Fact]
    public void ReferenceFieldsTakeTheirDefinitionsFromTheReferenceFile()
    {
        using var machine = Machine.Open();
        var job = new BatchJob("PRTUSER", CancellationToken.None);
        // The order file is built first, and waits for the field reference file it names.
        var built = machine.Build(
        [
            new Member("QGPL", "QDDSSRC", "ORDPF", "PF", """
                     A                                      REF(FLDREF)
                     A          R ORDREC
                     A            ORDNO     R
                     A            CUST#     R               REFFLD(CUSTNO)
                     A            TOTAL     R   +2          REFFLD(AMOUNT)
                     A            NOTE      R   30
                                  DESC          10
                     A          K ORDNO
                """),
            new Member("QGPL", "QDDSSRC", "FLDREF", "PF", """
                     A          R FLDRECD
                     A            ORDNO          7P 0
                     A            CUSTNO         5S 0
                     A            AMOUNT         9P 2
                     A            NOTE          20A
                """),
        ]);
        Assert.All(built, b => Assert.True(b.Result.Ok, $"{b.Member}: {b.Result.Message}"));
        var file = machine.FindDatabaseFile("ORDPF", "QGPL")!;
        Assert.Equal(["ORDNO DECIMAL(7, 0)", "CUST# NUMERIC(5, 0)", "TOTAL DECIMAL(11, 2)", "NOTE CHAR(30)", "DESC CHAR(10)"],
            file.Fields.Select(f => $"{f.Name} {f.SqlType}"));
        Assert.Equal(["7 Acme", "1"], Run(machine, """
            dcl-f ordpf usage(*output : *input) keyed;
            ordno = 7;
            cust# = 42;
            note = 'Acme';
            desc = 'x';
            write ordrec;
            chain 7 ordpf;
            dsply (%char(ordno) + ' ' + %trim(note));
            exec sql select count(*) into :ordno from ordpf where cust# = 42 and "DESC" = 'x';
            dsply %char(ordno);
            *inlr = *on;
            """));
        var missing = machine.Save(new Member("QGPL", "QDDSSRC", "BADREF", "PF", "     A          R BADREC\n     A            X         R               REFFLD(Y NOFILE)\n"));
        Assert.Contains(missing, d => d.Severity == Severity.Error && d.Message.StartsWith("the reference file NOFILE"));
    }

    [Fact]
    public void AFieldReferenceFileWiderThanATableAndOldDdsSpellingsAreTaken()
    {
        using var machine = Machine.Open();
        // More fields than a PostgreSQL table holds: made without a table, its fields still referred to.
        var wide = new System.Text.StringBuilder("     A          R WIDEREC\n");
        for (var i = 1; i <= 1700; i++) wide.Append($"     A            {"F" + i.ToString("D4"),-10} {"5",5}A\n");
        var built = machine.Build(
        [
            new Member("QGPL", "QDDSSRC", "WIDEREF", "PF", wide.ToString()),
            new Member("QGPL", "QDDSSRC", "NARROW", "PF", """
                     A                                      REF(WIDEREF)
                     A          R NARROWREC
                     A            F1700     R
                     A            KIND           1A
                     A          K F1700
                """),
            // CMP is COMP's older name; an R field in a logical file is its physical file's.
            new Member("QGPL", "QDDSSRC", "NARROWA", "LF", """
                     A          R NARROWREC                 PFILE(NARROW)
                     A            F1700     R
                     A            KIND      R
                     A          K F1700
                     A          S KIND                      CMP(EQ 'A')
                """),
        ]);
        Assert.All(built, b => Assert.True(b.Result.Ok, $"{b.Member}: {b.Result.Message}"));
        Assert.Contains("without a table", built.Single(b => b.Member.Name == "WIDEREF").Result.Message);
        Assert.Equal(["F1700 CHAR(5)", "KIND CHAR(1)"], machine.FindDatabaseFile("NARROWA", "QGPL")!.Fields.Select(f => $"{f.Name} {f.SqlType}"));
    }

    [Fact]
    public void ATableWithSystemNamesIsReadByThemAndALogicalFileRenamesItsFields()
    {
        using var machine = Machine.Open();
        var job = new BatchJob("PRTUSER", CancellationToken.None);
        var table = new Member("QGPL", "QSQLSRC", "ITEMMASTER", "SQL", """
            CREATE TABLE ITEMMASTER (
              ITEM_NUMBER FOR COLUMN ITMNUM CHAR(6) NOT NULL ,
              TITLE_LINE1 FOR COLUMN TITLE1 CHAR(20) NOT NULL ,
              ITEM_STATUS FOR COLUMN ITSTAT CHAR(1) NOT NULL DEFAULT ' ' ,
              PRIMARY KEY (ITEM_NUMBER)) RCDFMT ITEMREC;
            INSERT INTO ITEMMASTER VALUES ('A1', 'Rake', ' '), ('B2', 'Hoe', 'D'), ('C3', 'Spade', ' ');
            """);
        var view = new Member("QGPL", "QDDSSRC", "ITMMSTN", "LF", """
                 A          R IT11                      PFILE(ITEMMASTER)
                 A            ITMNUM
                 A            ITMDSC                    RENAME(TITLE1)
                 A            ITSTAT
                 A            INITIAL        1   I      SST(TITLE1 1 1)
                 A          K ITMDSC
                 A          O ITSTAT                    CMP(EQ 'D')
            """);
        var built = machine.Build([table, view]);
        Assert.All(built, b => Assert.True(b.Result.Ok, $"{b.Member}: {b.Result.Message}"));
        Assert.Equal(["Rake A1 R", "Spade C3 S", "Hoe", "3 Spade"], Run(machine, """
            dcl-f itmmstn keyed;
            dcl-f itemmaster keyed usage(*input) prefix(m_);
            dcl-s n int(10);
            read itmmstn;
            dow not %eof(itmmstn);
              dsply (%trim(itmdsc) + ' ' + %trim(itmnum) + ' ' + initial);
              read itmmstn;
            enddo;
            chain 'B2' itemrec;
            dsply %trim(m_title1);
            exec sql select count(*), max(title_line1) into :n, :itmdsc from itemmaster;
            dsply (%char(n) + ' ' + %trim(itmdsc));
            *inlr = *on;
            """));
    }

    [Fact]
    public void ADisplayFileTakesItsReferenceFieldsFromTheDatabaseFile()
    {
        using var machine = WithFiles();
        var built = Assert.Single(machine.Build([new Member("QGPL", "QDDSSRC", "CUSTD", "DSPF", """
                 A                                      REF(CUSTPF)
                 A          R CUSTSCR
                 A            CUSTNO    R        B  3  2
                 A            NAME      R   +5   O  4  2
                 A            OWED      R        O  5  2REFFLD(BAL CUSTPF)
            """)]));
        Assert.True(built.Result.Ok, built.Result.Message);
        Assert.DoesNotContain(built.Diagnostics, d => d.Severity != Severity.Info);
        var fields = machine.LoadDisplayFile("CUSTD")!.Records["CUSTSCR"].Fields.ToDictionary(f => f.Name!);
        Assert.Equal((5, 0, true), (fields["CUSTNO"].Length, fields["CUSTNO"].Decimals, fields["CUSTNO"].Numeric));
        Assert.Equal((25, false), (fields["NAME"].Length, fields["NAME"].Numeric));
        Assert.Equal((9, 2, true), (fields["OWED"].Length, fields["OWED"].Decimals, fields["OWED"].Numeric));
    }

    [Fact]
    public void ALogicalFileWithTwoRecordFormatsReadsEachFromItsPhysicalFile()
    {
        using var machine = WithFiles();
        var job = new BatchJob("PRTUSER", CancellationToken.None);
        var built = machine.Build(
        [
            new Member("QGPL", "QDDSSRC", "NOTEPF", "PF", """
                     A          R NOTEREC
                     A            NCUST          5P 0
                     A            NOTE          30A
                     A          K NCUST
                """),
            new Member("QGPL", "QDDSSRC", "CUSTNOTES", "LF", """
                     A          R CUSTREC                   PFILE(CUSTPF)
                     A          K CUSTNO
                     A          R NOTEREC                   PFILE(NOTEPF)
                     A          K NCUST
                """),
        ]);
        Assert.All(built, b => Assert.True(b.Result.Ok, $"{b.Member}: {b.Result.Message}"));
        Assert.Equal(["Acme: paid late"], Run(machine, """
            dcl-f custpf usage(*output) rename(custrec : custout);
            dcl-f notepf usage(*output) rename(noterec : noteout);
            dcl-f custnotes keyed;
            custno = 1;
            name = 'Acme';
            write custout;
            ncust = 1;
            note = 'paid late';
            write noteout;
            chain 1 custrec;
            chain 1 noterec;
            if %found;
              dsply (%trim(name) + ': ' + %trim(note));
            endif;
            *inlr = *on;
            """));
    }

    [Fact]
    public void AFileThatIsNotThereIsAnError()
    {
        using var machine = Machine.Open();
        var found = machine.Save(new Member("QGPL", "QRPGLESRC", "NOFILE", "RPGLE", "**FREE\ndcl-f nothere keyed;\n*inlr = *on;\n"));
        Assert.Contains(found, d => d.Severity == Severity.Error && d.Message.StartsWith("the file NOTHERE isn't found"));
    }
}
