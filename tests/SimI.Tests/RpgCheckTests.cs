namespace SimI.Tests;

/// <summary>Source an IBM i's compiler rejects is an error, and nothing is made from it; source Sim i only can't run isn't.</summary>
public class RpgCheckTests
{
    /// <summary>The errors in a member: <paramref name="code"/> is its lines after **FREE (so it starts on line 2), separated by |.</summary>
    private static List<(int Line, string Message)> Errors(string code, string type = "RPGLE")
    {
        using var machine = Machine.Open();
        var found = machine.Save(new Member("QGPL", "QRPGLESRC", "CHECKED", type, "**FREE\n" + code.Replace('|', '\n')));
        return [.. found.Where(d => d.Severity == Severity.Error).Select(d => (d.Line, d.Message))];
    }

    [Theory]
    // Syntax.
    [InlineData("dcl-s n int(10);|n = 1", 3, "the statement has no semicolon at its end")]
    [InlineData("dcl-s n int(10);|n = 1|n = 2;", 3, "N isn't expected here")]
    [InlineData("dcl-s n int(10);|if n = 1;|  n = 2;", 3, "ENDIF is missing for the block that starts here")]
    [InlineData("dcl-s n int(10);|endif;", 3, "ENDIF has no block to end")]
    [InlineData("dcl-s n int(10);|n = 1 ! 2;", 3, "the character ! isn't expected here")]
    [InlineData("dcl-s n char(5);|n = 'abc;", 3, "a literal isn't closed")]
    [InlineData("dcl-s n int(10);|frobnicate n;", 3, "FROBNICATE isn't an operation code")]
    [InlineData("dcl-s name char(5);|move 'a' name;", 3, "MOVE is a fixed-form operation code, not free-form: use = or EVALR")]
    [InlineData("dcl-s n int(10);|n = %foo(1);", 3, "%FOO isn't a built-in function")]
    [InlineData("dcl-s n int(10) frob;", 2, "FROB isn't a type or a definition keyword")]
    [InlineData("ctl-opt dftactgrp(*no) frob;", 2, "FROB isn't a CTL-OPT keyword")]
    [InlineData("dcl-s n char;", 2, "CHAR needs a length")]
    [InlineData("dcl-s n;", 2, "N needs a type")]
    [InlineData("dcl-ds d;|  a char(1);", 3, "END-DS is missing")]
    [InlineData("monitor;|on-error x;|endmon;", 3, "ON-ERROR takes status codes, *PROGRAM, *FILE or *ALL, separated by colons, not X")]
    [InlineData("/frob|dcl-s n int(10);", 2, "/FROB isn't a compiler directive")]
    [InlineData("/endif", 2, "/ENDIF has no /IF")]
    // Names.
    [InlineData("dcl-s n int(10);|m = 1;", 3, "M isn't defined")]
    [InlineData("dcl-s n int(10);|dcl-s n char(1);", 3, "N is defined more than once")]
    [InlineData("dcl-c max 10;|max = 5;", 3, "MAX is a constant: it can't be changed")]
    [InlineData("dcl-s n like(nothing);", 2, "LIKE(NOTHING): NOTHING isn't defined")]
    [InlineData("dcl-s n int(10);|dcl-ds d likeds(n);", 3, "LIKEDS(N): N isn't a data structure")]
    [InlineData("dcl-ds d qualified;|  a char(1);|end-ds;|d.b = 'x';", 5, "D has no subfield B")]
    [InlineData("dcl-s n int(10);|n.a = 1;", 3, "N isn't a data structure, so it has no subfield A")]
    [InlineData("dcl-s n int(10);|n(1) = 1;", 3, "N isn't an array")]
    [InlineData("exsr nothere;", 2, "there's no subroutine NOTHERE")]
    [InlineData("nothere();", 2, "NOTHERE isn't defined: a procedure needs a prototype or DCL-PROC")]
    [InlineData("dcl-f nofile workstn;", 2, "the display file NOFILE isn't found: CRTDSPF makes it")]
    [InlineData("dcl-f norpt printer;", 2, "the printer file NORPT isn't found: CRTPRTF makes it (or give PRINTER a line length, for a program-described file)")]
    // Types.
    [InlineData("dcl-s name char(10);|name = 5;", 3, "NAME is character, and can't be given numeric: %CHAR or %EDITC makes characters of it")]
    [InlineData("dcl-s n int(10);|n = 'abc';", 3, "N is numeric, and can't be given character: %DEC or %INT makes a number of it")]
    [InlineData("dcl-s d date;|d = '2024-01-01';", 3, "D is a date, and can't be given character: %DATE makes a date of it")]
    [InlineData("dcl-s name char(10);|if name = 5;|endif;", 3, "character and numeric can't be compared: %CHAR or %EDITC makes characters of it")]
    [InlineData("dcl-s n int(10);|if n;|endif;", 3, "IF's condition is numeric, not an indicator: compare it, such as N <> 0")]
    [InlineData("dcl-s n int(10);|dow n and *on;|enddo;", 3, "AND needs indicators on both sides, not numeric")]
    [InlineData("dcl-s n int(10);|n = 'a' * 2;", 3, "* doesn't work on character and numeric")]
    [InlineData("dcl-s n int(10);|dcl-s d date;|n = d - d;", 4, "a date minus a date isn't RPG: %DIFF gives the time between them")]
    [InlineData("dcl-s n int(10) inz('x');", 2, "INZ: N is numeric, and its INZ is character")]
    [InlineData("dcl-s s char(10);|s = %trim(5);", 3, "parameter 1 of %TRIM has to be character, not numeric")]
    [InlineData("dcl-s s char(10);|s = %subst(s);", 3, "%SUBST takes 2 to 3 parameters, not 1")]
    [InlineData("dcl-s n int(10);|for n = 1 to 'x';|endfor;", 3, "FOR's start, limit and BY have to be numeric")]
    // Flow.
    [InlineData("leave;", 2, "LEAVE isn't in a loop (DOW, DOU, FOR or FOR-EACH)")]
    [InlineData("iter;", 2, "ITER isn't in a loop (DOW, DOU, FOR or FOR-EACH)")]
    [InlineData("leavesr;", 2, "LEAVESR isn't in a subroutine")]
    [InlineData("return 1;", 2, "the main procedure can't return a value: RETURN ends the program")]
    [InlineData("dcl-proc p;|  return 1;|end-proc;", 3, "P doesn't return a value: its RETURN can't have one")]
    [InlineData("dcl-proc p;|  dcl-pi *n int(10);|  end-pi;|  return;|end-proc;", 5, "P returns a value: its RETURN needs one")]
    [InlineData("dcl-proc p;|  dcl-pi *n int(10);|  end-pi;|  return 'x';|end-proc;", 5, "P returns numeric, not character")]
    // Calls.
    [InlineData("dcl-s n int(10);|n = add(1);|dcl-proc add;|  dcl-pi *n int(10);|    a int(10) const;|    b int(10) const;|  end-pi;|  return a + b;|end-proc;", 3, "ADD needs 2 parameters, not 1")]
    [InlineData("dcl-s n int(10);|n = add(1:2:3);|dcl-proc add;|  dcl-pi *n int(10);|    a int(10) const;|    b int(10) const;|  end-pi;|  return a + b;|end-proc;", 3, "ADD takes 2 parameters, not 3")]
    [InlineData("bump(1);|dcl-proc bump;|  dcl-pi *n;|    a int(10);|  end-pi;|  a += 1;|end-proc;", 2, "parameter 1 of BUMP is passed by reference, so it has to be a variable, not a literal: CONST or VALUE on the parameter would take it")]
    [InlineData("dcl-s s char(5);|bump(s);|dcl-proc bump;|  dcl-pi *n;|    a int(10);|  end-pi;|  a += 1;|end-proc;", 3, "parameter 1 of BUMP is numeric, and S is character")]
    [InlineData("show('x');|dcl-proc show;|  dcl-pi *n;|    a int(10) const;|  end-pi;|end-proc;", 2, "parameter 1 of SHOW is numeric, not character")]
    [InlineData("dcl-s n int(10);|n = p();|dcl-proc p;|end-proc;", 3, "P doesn't return a value, so it can't be in an expression")]
    [InlineData("dcl-s n int(10);|n = add;|dcl-proc add;|  dcl-pi *n int(10);|    a int(10) const;|  end-pi;|  return a;|end-proc;", 3, "ADD needs 1 parameter, not 0")]
    [InlineData("dcl-proc p;|  dcl-pi *n;|    a int(10) const;|  end-pi;|  a = 2;|end-proc;", 6, "A is a CONST parameter: the procedure can't change it")]
    [InlineData("dcl-pr p;|  a int(10) const;|end-pr;|dcl-proc p;|  dcl-pi *n;|    a char(1) const;|  end-pi;|end-proc;", 5, "parameter 1 of P is numeric in its prototype, and character in its procedure interface")]
    // SQL.
    [InlineData("exec sql set :n = 1;", 2, "EXEC SQL needs SQLRPGLE source, which CRTSQLRPGI compiles: CRTBNDRPG doesn't take it")]
    public void AnErrorIsReportedOnItsLine(string code, int line, string message)
    {
        Assert.Contains((line, message), Errors(code));
    }

    [Fact]
    public void AnSqlHostVariableHasToBeDefined()
    {
        Assert.Equal([(3, "SQL: the host variable :x isn't defined")], Errors("dcl-s n int(10);|exec sql set :x = 1;", "SQLRPGLE"));
        Assert.Empty(Errors("dcl-s n int(10);|exec sql set :n = 1;|n = sqlcode;", "SQLRPGLE"));
    }

    [Fact]
    public void WhatSimIDoesNotSupportIsNotAnError()
    {
        // An OBJECT, a SPECIAL file and %NULLIND aren't supported, and the names they declare aren't errors.
        Assert.Empty(Errors("""
            dcl-s n int(10);
            dcl-s p object;
            dcl-f custfile special;
            dcl-pr cfunc int(10) extproc('cfunc');
              x int(10) value;
            end-pr;
            n = cfunc(n);
            if %eof(custfile);
            endif;
            n = %bitand(n:1);
            """.Replace("\n", "|")));
    }

    [Fact]
    public void CopyOfAMemberOrStreamFileThatIsNotThereIsAnError()
    {
        using var machine = Machine.Open();
        machine.Save(new Member("QGPL", "QRPGLESRC", "PROTOS", "RPGLEINC", "**FREE\ndcl-c GREETING 'hi';\n"));
        var found = machine.Save(new Member("QGPL", "QRPGLESRC", "COPIER", "RPGLE", """
            **FREE
            /copy qgpl/qrpglesrc,protos
            /copy '/home/me/protos.rpgleinc'
            /include protos.rpgleinc
            /copy nothere
            dsply GREETING;
            *inlr = *on;
            """));
        Assert.Equal(
        [
            (3, Severity.Error, "/COPY /home/me/protos.rpgleinc: there's no such stream file"),
            (4, Severity.Error, "/INCLUDE protos.rpgleinc: there's no such stream file"),
            (5, Severity.Error, "/COPY nothere: there's no such member"),
        ], found.Select(d => (d.Line, d.Severity, d.Message)));
    }

    [Fact]
    public void ACorrectProgramHasNoErrors()
    {
        Assert.Empty(Errors("""
            ctl-opt dftactgrp(*no) actgrp(*new);
            dcl-c LIMIT 10;
            dcl-s n int(10);
            dcl-s total packed(9:2) inz(0);
            dcl-s name varchar(20) inz('x');
            dcl-s due date inz(*sys);
            dcl-s flags ind dim(5);
            dcl-ds point qualified;
              x int(10);
              y int(10);
            end-ds;
            dcl-ds points likeds(point) dim(3);
            for n = 1 to LIMIT by 2;
              total += n * 1.5;
              if n > 5 and not flags(1);
                iter;
              endif;
              select;
                when name = 'x' or %len(name) > 3;
                  name = %trim(name) + '!';
                other;
                  leave;
              endsl;
            endfor;
            points(1).x = point.y;
            due = due + %days(1);
            n = %diff(due: %date(): *days);
            *in03 = *on;
            flags(2) = *in03;
            exsr tidy;
            n = twice(n);
            show(%char(n));
            *inlr = *on;
            return;
            begsr tidy;
              clear point;
              leavesr;
            endsr;
            dcl-proc twice;
              dcl-pi *n int(10);
                v int(10) const;
              end-pi;
              return v * 2;
            end-proc;
            dcl-proc show;
              dcl-pi *n;
                text varchar(50) const options(*nopass);
              end-pi;
              if %parms >= 1;
                dsply text;
              endif;
            end-proc;
            """.Replace("\n", "|")));
    }

    [Fact]
    public void AProgramIsMadeOnlyWhenAServiceProgramExportsWhatItCalls()
    {
        using var machine = Machine.Open();
        var job = new BatchJob("QUSER", CancellationToken.None);
        machine.Save(new Member("QGPL", "QRPGLESRC", "CALLER", "RPGLE",
            "**FREE\ndcl-pr greet varchar(20);\nend-pr;\ndsply greet();\n*inlr = *on;\n"));
        var unbound = machine.Run("CRTBNDRPG PGM(QGPL/CALLER)", job);
        Assert.Equal("CPF5D04", unbound.MessageId);
        Assert.Equal("Program CALLER not created: no service program exports GREET.", unbound.Message);

        machine.Save(new Member("QGPL", "QRPGLESRC", "GREETER", "RPGLE",
            "**FREE\nctl-opt nomain;\ndcl-proc greet export;\n  dcl-pi *n varchar(20);\n  end-pi;\n  return 'hello';\nend-proc;\n"));
        Assert.True(machine.Run("CRTRPGMOD MODULE(QGPL/GREETER)", job).Ok);
        Assert.True(machine.Run("CRTSRVPGM SRVPGM(QGPL/GREETER) MODULE(QGPL/GREETER)", job).Ok);
        Assert.True(machine.Run("CRTBNDRPG PGM(QGPL/CALLER)", job).Ok);
    }

    [Fact]
    public void AMemberInErrorMakesNothing()
    {
        using var machine = Machine.Open();
        machine.Save(new Member("QGPL", "QRPGLESRC", "BAD", "RPGLE", "**FREE\ndcl-s n int(10);\nn = 'x';\n*inlr = *on;\n"));
        var job = new BatchJob("QUSER", CancellationToken.None);
        var result = machine.Run("CRTBNDRPG PGM(QGPL/BAD)", job);
        Assert.False(result.Ok);
        Assert.Equal("RNS9310", result.MessageId);
        Assert.Null(machine.Store.FindObject("QGPL", "BAD", "*PGM"));
    }
}
