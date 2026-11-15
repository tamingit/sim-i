namespace FakeIbmI.Tests;

/// <summary>Data structures as bytes, pointers, the IFS APIs and the built-in functions that came with them.</summary>
public class RpgStorageTests
{
    private static IReadOnlyList<string> Shown(string source, Machine? on = null)
    {
        using var own = on is null ? Machine.Open() : null;
        var (shown, error) = RpgTests.Run(on ?? own!, source);
        Assert.Null(error);
        return shown;
    }

    [Fact]
    public void SubfieldsOverlayEachOtherAsBytes()
    {
        Assert.Equal(["2024", "11", "13", "20241113", "AB", "12", "A1B2"], Shown("""
            dcl-ds when;
              ymd char(8);
              year char(4) overlay(ymd);
              month char(2) overlay(ymd : 5);
              day char(2) overlay(ymd : *next);
            end-ds;
            dcl-ds pair qualified;
              whole char(4);
              a char(1) pos(1);
              b char(1) pos(3);
            end-ds;
            dcl-ds digits;
              number zoned(2:0);
              text char(2) pos(1);
            end-ds;
            ymd = '20241113';
            dsply year;
            dsply month;
            dsply day;
            dsply when;
            pair.whole = 'A1B2';
            dsply (pair.a + pair.b);
            number = 12;
            dsply text;
            dsply pair.whole;
            *inlr = *on;
            """));
    }

    [Fact]
    public void NumbersHaveTheirIbmILayouts()
    {
        Assert.Equal(["4", "5", "4", "8", "12", "-345", "5"], Shown("""
            dcl-ds packed qualified;
              p packed(7:2);
            end-ds;
            dcl-ds zoned qualified;
              z zoned(5:0);
            end-ds;
            dcl-ds ints qualified;
              i int(10);
              j int(20);
            end-ds;
            dcl-ds image qualified;
              text char(5);
              z zoned(5:0) pos(1);
            end-ds;
            dsply %char(%size(packed));
            dsply %char(%size(zoned));
            dsply %char(%size(ints.i));
            dsply %char(%size(ints.j));
            ints.i = 12;
            dsply %char(ints.i);
            image.z = -345;
            dsply %char(image.z);
            // A negative zoned number's last digit carries the sign: 5 becomes N.
            dsply %char(%scan('N' : image.text));
            *inlr = *on;
            """));
    }

    [Fact]
    public void BlanksInANumberAreADecimalDataError()
    {
        using var machine = Machine.Open();
        var (_, error) = RpgTests.Run(machine, """
            dcl-ds rec qualified;
              text char(5);
              amount zoned(5:0) pos(1);
            end-ds;
            rec.text = 'ABCDE';
            dsply %char(rec.amount);
            *inlr = *on;
            """);
        Assert.Contains("Decimal-data error", error);
    }

    [Fact]
    public void PointersAndBasedVariables()
    {
        Assert.Equal(["Hello", "World", "llo", "1", "5", "hi there"], Shown("""
            dcl-s text char(10) inz('Hello');
            dcl-s p pointer;
            dcl-s q pointer;
            dcl-s view char(5) based(p);
            dcl-s buffer pointer;
            dcl-s c char(1) based(q);
            p = %addr(text);
            dsply view;
            view = 'World';
            dsply %trim(text);
            text = 'Hello';
            q = p + 2;
            dsply %subst(view : 3);
            dsply %char(p = %addr(text));
            dsply %char(q - p + 3);
            buffer = %alloc(20);
            %str(buffer : 20) = 'hi there';
            dsply %str(buffer);
            dealloc(n) buffer;
            *inlr = *on;
            """));
    }

    [Fact]
    public void AProgramWritesAndReadsStreamFiles()
    {
        using var machine = Machine.Open();
        Directory.CreateDirectory(machine.Ifs.HostPath("/home"));
        var shown = Shown("""
            dcl-pr open int(10) extproc('open');
              path pointer value options(*string);
              flags int(10) value;
              mode uns(10) value options(*nopass);
            end-pr;
            dcl-pr write int(10) extproc('write');
              fd int(10) value;
              buf pointer value;
              len uns(10) value;
            end-pr;
            dcl-pr read int(10) extproc('read');
              fd int(10) value;
              buf pointer value;
              len uns(10) value;
            end-pr;
            dcl-pr close int(10) extproc('close');
              fd int(10) value;
            end-pr;
            dcl-pr errno pointer extproc('__errno') end-pr;
            dcl-pr strerror pointer extproc('strerror');
              n int(10) value;
            end-pr;
            dcl-c O_RDONLY 1;
            dcl-c O_WRONLY 2;
            dcl-c O_CREAT 8;
            dcl-c O_TRUNC 64;
            dcl-c O_TEXTDATA 16777216;
            dcl-s fd int(10);
            dcl-s line varchar(100);
            dcl-s buffer char(100);
            dcl-s got int(10);
            dcl-s err int(10) based(perr);
            dcl-s perr pointer;

            fd = open('/home/hello.txt' : O_WRONLY + O_CREAT + O_TRUNC + O_TEXTDATA : 438);
            line = 'Hello, IFS!';
            callp write(fd : %addr(line : *data) : %len(line));
            callp close(fd);
            fd = open('/home/hello.txt' : O_RDONLY + O_TEXTDATA);
            got = read(fd : %addr(buffer) : %size(buffer));
            callp close(fd);
            dsply %subst(buffer : 1 : got);
            fd = open('/home/none.txt' : O_RDONLY);
            perr = errno();
            dsply (%char(fd) + ' ' + %char(err) + ' ' + %str(strerror(err)));
            *inlr = *on;
            """, machine);
        Assert.Equal(["Hello, IFS!", "-1 3025 No such path or directory."], shown);
        Assert.Equal("Hello, IFS!", machine.Ifs.ReadText("/home/hello.txt"));
    }

    [Fact]
    public void TheNewBuiltInFunctions()
    {
        Assert.Equal(
        [
            "9", " 1,234.50", "3", "b", "a-b-c", "1", "1", "3", "4", "1", "OK", "*N", "TEST", "6", "8",
        ], Shown("""
            dcl-s words varchar(20) dim(5);
            dcl-s sorted int(10) dim(5) inz(0);
            dcl-s i int(10);
            dcl-s n int(10);
            dsply %char(%scanr('a' : 'banana bat'));
            dsply %editw(1234.50 : '  ,   .  ');
            dsply %char(%elem(%split('a b  c')));
            words = %split('a b c');
            dsply %subarr(words : 2 : 1);
            dsply %concatarr('-' : %subarr(words : 1 : 3));
            for i = 1 to 5;
              sorted(i) = i * 10;
            endfor;
            dsply %char(%lookuple(15 : sorted));
            dsply %char(%bitand(5 : 3));
            dsply %char(%bitor(1 : 2));
            dsply %char(%lookupge(31 : sorted));
            n = 3;
            if n in %range(1 : 5) and n in %list(1 : 3 : 7);
              dsply '1';
            endif;
            if not (n in %list(4 : 5));
              dsply 'OK';
            endif;
            dsply '*N';
            dsply %proc();
            dsply %char(%charcount('abcdef'));
            dsply %char(%len(%editflt(%float(1.5))) - 15);
            *inlr = *on;
            """));
    }

    [Fact]
    public void SortaSelectOperandEvalCorrAndTest()
    {
        Assert.Equal(["1 2 3", "3 2 1", "Bo Al", "two", "Al 7", "ok", "bad"], Shown("""
            dcl-s n int(10) dim(3);
            dcl-ds person qualified dim(2);
              name char(2);
              age int(5);
            end-ds;
            dcl-ds a qualified;
              name char(2);
              age int(5);
              extra char(3);
            end-ds;
            dcl-ds b qualified;
              age int(5);
              name char(2);
            end-ds;
            dcl-s date char(10);
            n(1) = 3;
            n(2) = 1;
            n(3) = 2;
            sorta n;
            dsply (%char(n(1)) + ' ' + %char(n(2)) + ' ' + %char(n(3)));
            sorta(d) n;
            dsply (%char(n(1)) + ' ' + %char(n(2)) + ' ' + %char(n(3)));
            person(1).name = 'Al';
            person(1).age = 40;
            person(2).name = 'Bo';
            person(2).age = 30;
            sorta person(*).age;
            dsply (person(1).name + ' ' + person(2).name);
            select n(2);
              when-is 1;
                dsply 'one';
              when-in %list(2 : 4);
                dsply 'two';
              other;
                dsply 'other';
            endsl;
            b.name = 'Al';
            b.age = 7;
            eval-corr a = b;
            dsply (a.name + ' ' + %char(a.age));
            date = '2024-02-29';
            test(de) *iso date;
            if not %error;
              dsply 'ok';
            endif;
            date = '2023-02-29';
            test(de) *iso date;
            if %error;
              dsply 'bad';
            endif;
            *inlr = *on;
            """));
    }

    [Fact]
    public void InzsrPssrOnExitAndMessages()
    {
        using var machine = Machine.Open();
        var (shown, error) = RpgTests.Run(machine, """
            dcl-s started char(10);
            dcl-s n int(10);
            dsply started;
            tidy();
            monitor;
              snd-msg *escape %msg('CPF9898' : 'QCPFMSG' : 'stopped');
            on-excp 'CPF9898';
              dsply 'caught';
            endmon;
            snd-msg *info 'for the job log';
            n = 1 / n;
            dsply 'not reached';
            *inlr = *on;
            begsr *inzsr;
              started = 'started';
            endsr;
            begsr *pssr;
              dsply 'error handled';
              return;
            endsr;
            dcl-proc tidy;
              dcl-s failed ind;
              dsply 'working';
              return;
            on-exit failed;
              dsply 'exiting';
            end-proc;
            """);
        Assert.Null(error);
        Assert.Equal(["started", "working", "exiting", "caught", "error handled"], shown);
    }

    [Fact]
    public void DataAreas()
    {
        using var machine = Machine.Open();
        var job = new BatchJob("PRTUSER", CancellationToken.None);
        Assert.True(machine.Run("CRTDTAARA DTAARA(QGPL/COUNTER) TYPE(*DEC) LEN(5 0) VALUE(41)", job).Ok);
        Assert.True(machine.Run("CRTDTAARA DTAARA(QGPL/NAME) TYPE(*CHAR) LEN(10) VALUE('fake')", job).Ok);
        var (shown, error) = RpgTests.Run(machine, """
            dcl-s counter packed(5:0) dtaara;
            dcl-s name char(10) dtaara('NAME');
            dcl-ds lda dtaara(*lda) qualified;
              first char(4);
            end-ds;
            in counter;
            counter += 1;
            out counter;
            in name;
            dsply %trim(name);
            lda.first = 'LDA!';
            out lda;
            *inlr = *on;
            """);
        Assert.Null(error);
        Assert.Equal(["fake"], shown);
        Assert.Equal("QGPL/COUNTER: 42", machine.Run("DSPDTAARA QGPL/COUNTER", job).Message);
        Assert.True(machine.Run("CHGDTAARA DTAARA(QGPL/NAME (1 2)) VALUE('XY')", job).Ok);
        Assert.Equal("QGPL/NAME: XYke", machine.Run("DSPDTAARA QGPL/NAME", job).Message);
        Assert.Equal("CPF1015", machine.Run("DSPDTAARA NONE", job).MessageId);
    }

    [Fact]
    public void MoreDeclarations()
    {
        Assert.Equal(["Jan", "Mar", "3", "12345", "2", "B2", "6", "+2.500000000000000E+000", "8", "07/04/2024", "B"], Shown("""
            ctl-opt datfmt(*usa);
            dcl-s months char(3) dim(12) ctdata perrcd(4);
            dcl-s code char(5);
            dcl-s longer like(code : +5);
            dcl-c START 3;
            dcl-ds parts qualified;
              all char(6);
              middle char(2) pos(START);
            end-ds;
            dcl-ds history occurs(3) qualified;
              value char(2);
            end-ds;
            dcl-s names varchar(5) dim(*auto : 10);
            dcl-s f float(8) inz(2.5e0);
            dcl-s n int(10) inz(2);
            dcl-s day date inz(d'2024-07-04');
            dsply months(1);
            dsply months(3);
            dsply %char(%elem(months) / 4);
            longer = '1234567890';
            dsply %subst(longer : 1 : 5);
            n **= 1;
            dsply %char(n);
            parts.all = 'A1B2C3';
            dsply parts.middle;
            names(6) = 'six';
            dsply %char(%elem(names));
            dsply %trim(%editflt(f));
            dsply %char(%size(longer) - 2);
            dsply %char(day);
            %occur(history) = 2;
            history.value = 'B';
            %occur(history) = 1;
            history.value = 'A';
            %occur(history) = 2;
            dsply %trim(history.value);
            *inlr = *on;
            **CTDATA months
            JanFebMarApr
            MayJunJulAug
            SepOctNovDec
            """));
    }
}
