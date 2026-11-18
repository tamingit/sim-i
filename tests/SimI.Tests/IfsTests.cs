namespace SimI.Tests;

/// <summary>The integrated file system: a host folder, used by /COPY, SRCSTMF and the IFS commands.</summary>
public class IfsTests
{
    private static void Write(Machine machine, string path, string text)
    {
        var host = machine.Ifs.HostPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(host)!);
        File.WriteAllText(host, text);
    }

    [Fact]
    public void PathsStayInTheRootAndAreFoundWhateverTheirCase()
    {
        using var machine = Machine.Open();
        Assert.Equal("/home/me/x.rpgle", Ifs.Absolute("../me/./x.rpgle", "/home/you"));
        Assert.Equal("/etc/passwd", Ifs.Absolute("../../../../etc/passwd", "/home"));
        Assert.StartsWith(machine.Ifs.Root, machine.Ifs.HostPath("../../../../etc/passwd"));
        Write(machine, "/Home/Me/Protos.RpgleInc", "x");
        Assert.True(machine.Ifs.FileExists("/home/me/protos.rpgleinc"));
        Assert.Equal(("MYLIB", "QRPGLESRC", "HELLO"), Ifs.QsysMember("/qsys.lib/mylib.lib/qrpglesrc.file/hello.mbr"));

        // A symbolic link out of the root isn't followed.
        var outside = Path.Combine(Path.GetTempPath(), "sim-i-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(machine.Ifs.Root, "escape"), outside);
            Assert.Throws<UnauthorizedAccessException>(() => machine.Ifs.HostPath("/escape/secret"));
        }
        finally
        {
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public void AHomeDirectoryIsMadeOnceAndOneThereIsKept()
    {
        using var machine = Machine.Open();
        Assert.True(machine.Ifs.MakeHome("prtuser"));
        Assert.True(Directory.Exists(Path.Combine(machine.Ifs.Root, "home", "PRTUSER")));
        Assert.False(machine.Ifs.MakeHome("PRTUSER"));

        // One made by hand, in another case, is the user's: no second one is made beside it.
        Directory.CreateDirectory(Path.Combine(machine.Ifs.Root, "home", "devuser"));
        File.WriteAllText(Path.Combine(machine.Ifs.Root, "home", "devuser", "keep.txt"), "kept");
        Assert.False(machine.Ifs.MakeHome("DEVUSER"));
        Assert.Equal(["PRTUSER", "devuser"], Directory.GetDirectories(Path.Combine(machine.Ifs.Root, "home")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.True(machine.Ifs.FileExists("/home/DEVUSER/keep.txt"));
    }

    [Fact]
    public void AHomeDirectoryThatCantBeMadeDoesntStopTheSignOn()
    {
        using var machine = Machine.Open();
        var users = Users.Defaults();
        var log = new List<string>();
        machine.Ifs.MakeHomesFor(users, log.Add);
        Directory.CreateDirectory(Path.Combine(machine.Ifs.Root, "home"));
        File.WriteAllText(Path.Combine(machine.Ifs.Root, "home", "PRTUSER"), "a file in the way");

        Assert.Equal(SignOnResult.SignedOn, users.Check(Users.DefaultUser, Users.DefaultPassword).Result);
        Assert.StartsWith("integrated file system: couldn't make /home/PRTUSER: ", Assert.Single(log));
    }

    [Fact]
    public void CopyOfAStreamFileBringsItInWithRelativePaths()
    {
        using var machine = Machine.Open();
        Write(machine, "/home/me/inc/greeting.rpgleinc", "**FREE\ndcl-c GREETING 'hello';\n/include 'more.rpgleinc'\n");
        Write(machine, "/home/me/inc/more.rpgleinc", "**FREE\ndcl-c OTHER 'and more';\n");
        var (shown, error) = RpgTests.Run(machine, """
            /copy '/home/me/inc/greeting.rpgleinc'
            dsply GREETING;
            dsply OTHER;
            *inlr = *on;
            """);
        Assert.Null(error);
        Assert.Equal(["hello", "and more"], shown);

        var missing = machine.Save(new Member("QGPL", "QRPGLESRC", "MISSING", "RPGLE", "**FREE\n/copy '/nothere.rpgleinc'\n*inlr = *on;\n"));
        Assert.Contains(missing, d => d.Severity == Severity.Error && d.Message == "/COPY /nothere.rpgleinc: there's no such stream file");
    }

    [Fact]
    public void ProgramsAndScriptsCompileFromStreamFiles()
    {
        using var machine = Machine.Open();
        Write(machine, "/src/inc/msg.rpgleinc", "**FREE\ndcl-c MESSAGE 'from a stream file';\n");
        Write(machine, "/src/hello.rpgle", "**FREE\n/copy 'inc/msg.rpgleinc'\ndsply MESSAGE;\n*inlr = *on;\n");
        Write(machine, "/src/tables.sql", "create table qgpl.notes (id int, text varchar(20));\ninsert into qgpl.notes values (1, 'one');\n");
        var job = new BatchJob("PRTUSER", CancellationToken.None);
        Assert.True(machine.Run("CHGCURDIR DIR('/src')", job).Ok);
        var compiled = machine.Run("CRTBNDRPG PGM(QGPL/HELLO) SRCSTMF('hello.rpgle')", job);
        Assert.True(compiled.Ok, compiled.Message);
        Assert.Equal("RPGLE SRCSTMF(/src/hello.rpgle)", machine.Store.FindObject("QGPL", "HELLO", "*PGM")!.Attribute);
        Assert.True(machine.Run("CALL QGPL/HELLO", job).Ok);
        Assert.Equal(["from a stream file"], job.Messages);
        Assert.True(machine.Run("RUNSQLSTM SRCSTMF('/src/tables.sql')", job).Ok);
        var none = machine.Run("CRTBNDRPG PGM(QGPL/NONE) SRCSTMF('/src/none.rpgle')", job);
        Assert.Equal(("RNS9310", "Stream file /src/none.rpgle not found."), (none.MessageId, none.Message));
    }

    [Fact]
    public void TheIfsCommands()
    {
        using var machine = Machine.Open();
        var job = new BatchJob("PRTUSER", CancellationToken.None);
        Assert.True(machine.Run("MKDIR DIR('/home')", job).Ok);
        Assert.True(machine.Run("MKDIR '/home/me'", job).Ok);
        Assert.Equal("CPFA0A0", machine.Run("MKDIR '/home/me'", job).MessageId);
        Assert.Equal("CPFA0A9", machine.Run("MKDIR '/no/such/parent'", job).MessageId);
        Assert.True(machine.Run("CD '/home/me'", job).Ok);
        Assert.Equal("/home/me", job.State.CurrentDirectory);

        machine.Save(new Member("QGPL", "QRPGLESRC", "HELLO", "RPGLE", "**FREE\ndsply 'hi';\n*inlr = *on;\n"));
        Assert.True(machine.Run("CPYTOSTMF FROMMBR('/QSYS.LIB/QGPL.LIB/QRPGLESRC.FILE/HELLO.MBR') TOSTMF('hello.rpgle')", job).Ok);
        Assert.Equal("**FREE\ndsply 'hi';\n*inlr = *on;\n", machine.Ifs.ReadText("/home/me/hello.rpgle"));
        Assert.Equal("CPFA0A0", machine.Run("CPYTOSTMF FROMMBR('/QSYS.LIB/QGPL.LIB/QRPGLESRC.FILE/HELLO.MBR') TOSTMF('hello.rpgle')", job).MessageId);
        machine.Ifs.WriteText("/home/me/hello.rpgle", "**FREE\ndsply 'changed';\n*inlr = *on;\n");
        Assert.True(machine.Run("CPYFRMSTMF FROMSTMF('hello.rpgle') TOMBR('/QSYS.LIB/QGPL.LIB/QRPGLESRC.FILE/HELLO.MBR') MBROPT(*REPLACE)", job).Ok);
        Assert.Contains("changed", machine.Store.FindMember("QGPL", "QRPGLESRC", "HELLO")!.Source);
        Assert.True(machine.Run("CPY OBJ('hello.rpgle') TOOBJ('copy.rpgle')", job).Ok);
        Assert.True(machine.Run("RMVLNK OBJLNK('copy.rpgle')", job).Ok);
        Assert.Equal("CPFA0A9", machine.Run("DEL 'copy.rpgle'", job).MessageId);
        Assert.Equal("CPFA0AC", machine.Run("RMDIR '/home/me'", job).MessageId);
        Assert.True(machine.Run("RMDIR '/home/me' SUBTREE(*ALL)", job).Ok);
        Assert.False(machine.Ifs.DirectoryExists("/home/me"));
    }
}
