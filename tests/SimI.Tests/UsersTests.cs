namespace SimI.Tests;

/// <summary>The users every server signs on, and the .env settings that set them.</summary>
public class UsersTests
{
    [Fact]
    public void TheDefaultsAreANormalAnExpiredAndADisabledUser()
    {
        var users = Users.Defaults();

        Assert.Equal(["PRTUSER", "EXPUSER (password expired)", "LOCKUSER (disabled)"], users.All.Select(u => u.ToString()));
        Assert.Same(users.Main, users.Find("prtuser"));
    }

    [Fact]
    public void SettingsChooseTheUsersAndPasswords()
    {
        var users = Users.FromSettings(new Dictionary<string, string>
        {
            ["SIM_I_USER"] = "devuser",
            ["SIM_I_PASSWORD"] = "A long pass phrase",
            ["SIM_I_EXPIRED_USER"] = "OLDPW",
            ["SIM_I_LOCKED_PASSWORD"] = "",
        });

        Assert.Equal("DEVUSER", users.Main.User);
        Assert.Equal("A long pass phrase", users.Main.Password);
        Assert.True(users.Find("OLDPW")!.PasswordExpired);
        Assert.Equal(Users.DefaultExpiredPassword, users.Find("OLDPW")!.Password);
        Assert.Equal(Users.DefaultLockedPassword, users.Find("LOCKUSER")!.Password);
    }

    [Fact]
    public void OnlyASignOnThatSucceedsIsAnnounced()
    {
        var users = Users.Defaults();
        var signedOn = new List<string>();
        users.SignedOn += p => signedOn.Add(p.User);

        users.Check("prtuser", "wrong");
        users.Check(Users.DefaultExpiredUser, Users.DefaultExpiredPassword);
        users.Check(Users.DefaultLockedUser, Users.DefaultLockedPassword);
        users.Check("NOBODY", "Secret12");
        Assert.Empty(signedOn);

        Assert.Equal(SignOnResult.SignedOn, users.Check("prtuser", Users.DefaultPassword).Result);
        Assert.Equal(["PRTUSER"], signedOn);
    }

    [Theory]
    [InlineData("SIM_I_USER", "1ABC")]
    [InlineData("SIM_I_USER", "ELEVENCHARS")]
    [InlineData("SIM_I_LOCKED_USER", "has space")]
    [InlineData("SIM_I_EXPIRED_USER", "PRTUSER")]
    public void ABadOrRepeatedUserNameStopsTheStart(string name, string value) =>
        Assert.Throws<ArgumentException>(() => Users.FromSettings(new Dictionary<string, string> { [name] = value }));

    [Theory]
    [InlineData("PRTUSER", "Secret12", SignOnResult.SignedOn)]
    [InlineData("PRTUSER", "SECRET12", SignOnResult.WrongPassword)]
    [InlineData("NOBODY", "Secret12", SignOnResult.UnknownUser)]
    [InlineData("EXPUSER", "Expired1", SignOnResult.PasswordExpired)]
    [InlineData("EXPUSER", "wrong", SignOnResult.WrongPassword)]
    [InlineData("LOCKUSER", "Locked12", SignOnResult.Disabled)]
    [InlineData("LOCKUSER", "wrong", SignOnResult.Disabled)]
    public void ASignOnTurnsOutAsOnAnIbmI(string user, string password, SignOnResult result) =>
        Assert.Equal(result, Users.Defaults().Check(user, password).Result);

    [Fact]
    public void AtPasswordLevel0PasswordsAreNotCaseSensitive()
    {
        var users = Users.Defaults();
        users.PasswordLevel = 0;

        Assert.Equal(SignOnResult.SignedOn, users.Check("PRTUSER", "SECRET12").Result);
    }

    [Fact]
    public void AnEnvFileIsReadAsDockerReadsIt()
    {
        var settings = Settings.Parse(
        [
            "# a comment",
            "",
            "SIM_I_USER=DEVUSER",
            "  SIM_I_PASSWORD=has = sign and \"quotes\"\r",
            "PASSED_THROUGH",
        ]);

        Assert.Equal("DEVUSER", settings["SIM_I_USER"]);
        Assert.Equal("has = sign and \"quotes\"", settings["SIM_I_PASSWORD"]);
        Assert.Equal(2, settings.Count);
    }

    [Fact]
    public void TheEnvironmentWinsOverTheFile()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(file, ["SIM_I_USER=FROMFILE", "SIM_I_PASSWORD=FilePw1"]);

            var settings = Settings.Load(file, new System.Collections.Hashtable { ["SIM_I_USER"] = "FROMENV" });

            Assert.Equal("FROMENV", settings["SIM_I_USER"]);
            Assert.Equal("FilePw1", settings["SIM_I_PASSWORD"]);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void ABadLineNamesItsNumber()
    {
        var e = Assert.Throws<ArgumentException>(() => Settings.Parse(["A=1", "NOT A NAME=2"]));
        Assert.Contains("line 2", e.Message);
    }

    [Fact]
    public void TheExampleFileHasEveryVariableAndTheDefaults()
    {
        var example = Settings.Parse(File.ReadLines(Path.Combine(RepoRoot(), ".env.example")));

        Assert.Equal(Users.Variables.Concat(Options.Variables).Order(), example.Keys.Order());
        var users = Users.FromSettings(example);
        Assert.Equal(Users.Defaults().All.Select(u => (u.ToString(), u.Password)), users.All.Select(u => (u.ToString(), u.Password)));
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SimI.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("no SimI.slnx above the test output");
    }

    [Fact]
    public void UsersInTheDatabaseKeepWhatChangesThemAndOtherUsersSurviveAStart()
    {
        using var store = Store.Open();
        var users = Users.Open(store, new Dictionary<string, string>());
        Assert.Equal(["EXPUSER (password expired)", "LOCKUSER (disabled)", "PRTUSER"], users.All.Select(u => u.ToString()));

        // A password changed through a server is kept; so is a user added with SQL.
        users.Find("EXPUSER")!.ChangePassword("NewPass1");
        Assert.Equal("NewPass1", store.FindUser("EXPUSER")!.Password);
        Assert.Equal("*NO", store.FindUser("EXPUSER")!.PasswordExpired);
        Assert.Equal(SignOnResult.SignedOn, users.Check("expuser", "NewPass1").Result);
        store.SaveUser(new UserRow("EXTRA", "Extra123", "*ENABLED", "*NO", "Added"));
        Assert.Equal(SignOnResult.SignedOn, users.Check("EXTRA", "Extra123").Result);

        // A default library is kept through the next start too.
        Assert.Equal("QGPL", users.Find("PRTUSER")!.DefaultLibrary);
        store.CreateLibrary("SALES");
        store.SaveUser(store.FindUser("PRTUSER")! with { DefaultLibrary = "SALES" });

        // The next start sets the three the settings name again, and leaves the rest.
        users = Users.Open(store, new Dictionary<string, string> { ["SIM_I_PASSWORD"] = "Changed1" });
        Assert.Equal(SignOnResult.PasswordExpired, users.Check("EXPUSER", "Expired1").Result);
        Assert.Equal(SignOnResult.SignedOn, users.Check("PRTUSER", "Changed1").Result);
        Assert.Equal(SignOnResult.SignedOn, users.Check("EXTRA", "Extra123").Result);
        Assert.Equal("Added", store.FindUser("EXTRA")!.Text);
        Assert.Equal("SALES", users.Find("PRTUSER")!.DefaultLibrary);

        // Deleting one takes it away from every server at once.
        using (var command = store.Connection.CreateCommand())
        {
            command.CommandText = "delete from qsim_user where username = 'EXTRA'";
            command.ExecuteNonQuery();
        }
        Assert.Equal(SignOnResult.UnknownUser, users.Check("EXTRA", "Extra123").Result);
        Assert.Equal("PRTUSER", users.Main.User);
    }

}
