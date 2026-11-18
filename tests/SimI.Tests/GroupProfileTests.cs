using SimI;

namespace SimI.Tests;

/// <summary>
/// User and group profiles through CRTUSRPRF, CHGUSRPRF, DLTUSRPRF and DSPUSRPRF, with an IBM i's rules
/// for group profiles. Placeholder users and passwords only.
/// </summary>
public class GroupProfileTests
{
    private static CommandResult Run(Machine machine, string command) => machine.Run(command, new BatchJob("PRTUSER", CancellationToken.None));

    private static void Ok(Machine machine, string command)
    {
        var result = Run(machine, command);
        Assert.True(result.Ok, $"{command}: {result.Message}");
    }

    private static void Refused(Machine machine, string command, string expected)
    {
        var result = Run(machine, command);
        Assert.False(result.Ok, $"{command} should have been refused");
        Assert.Contains(expected, result.Message);
    }

    [Fact]
    public void AGroupProfileIsMadeAndItsMembersSignOnAsThemselves()
    {
        using var machine = Machine.Open();
        var users = Users.Open(machine.Store, new Dictionary<string, string>());

        Ok(machine, "CRTUSRPRF USRPRF(GGADMIN) PASSWORD(*NONE) TEXT('GreenGlow administrators')");
        Ok(machine, "CRTUSRPRF USRPRF(STAFF) PASSWORD(*NONE)");
        Ok(machine, "CRTUSRPRF USRPRF(JSMITH) PASSWORD(Pass1234) GRPPRF(GGADMIN) SUPGRPPRF(STAFF) SPCAUT(*JOBCTL *SECADM)");
        Ok(machine, "CRTUSRPRF ADA");

        var jsmith = users.Find("JSMITH")!;
        Assert.Equal("GGADMIN", jsmith.GroupProfile);
        Assert.Equal(["STAFF"], jsmith.SupplementalGroups);
        // Kept in QSYRUSRI's order, whatever order they were given in.
        Assert.Equal(["*SECADM", "*JOBCTL"], jsmith.SpecialAuthorities);
        Assert.Equal(SignOnResult.SignedOn, users.Check("jsmith", "Pass1234").Result);
        // PASSWORD(*NONE): nobody signs on as the group; PASSWORD(*USRPRF), the default, is the profile's name.
        Assert.Equal(SignOnResult.NoPassword, users.Check("GGADMIN", Users.NoPassword).Result);
        Assert.Equal(SignOnResult.SignedOn, users.Check("ADA", "ADA").Result);
        Assert.Equal(["JSMITH"], users.Members("ggadmin"));
        Assert.Equal(["JSMITH"], users.Members("STAFF"));

        Assert.Contains("JSMITH", Run(machine, "DSPUSRPRF USRPRF(GGADMIN) TYPE(*GRPMBR)").Message);
        var shown = Run(machine, "DSPUSRPRF JSMITH").Message;
        Assert.Contains("group profile GGADMIN", shown);
        Assert.Contains("special authorities *SECADM *JOBCTL", shown);
        Assert.DoesNotContain("Pass1234", shown);
    }

    [Fact]
    public void TheIbmIsRulesForGroupProfilesHold()
    {
        using var machine = Machine.Open();
        Users.Open(machine.Store, new Dictionary<string, string>());
        Ok(machine, "CRTUSRPRF USRPRF(GGADMIN) PASSWORD(*NONE)");
        Ok(machine, "CRTUSRPRF USRPRF(JSMITH) PASSWORD(Pass1234) GRPPRF(GGADMIN)");

        Refused(machine, "CRTUSRPRF USRPRF(BOB) SUPGRPPRF(GGADMIN)", "needs a group profile");
        Refused(machine, "CRTUSRPRF USRPRF(BOB) GRPPRF(NOBODY)", "Group profile NOBODY not found");
        // JSMITH is in a group, so it can't be one.
        Refused(machine, "CRTUSRPRF USRPRF(BOB) GRPPRF(JSMITH)", "has a group profile of its own");
        Refused(machine, "CHGUSRPRF USRPRF(GGADMIN) GRPPRF(GGADMIN)", "can't be its own group profile");
        // GGADMIN has a member, so it can't be in a group.
        Ok(machine, "CRTUSRPRF USRPRF(STAFF) PASSWORD(*NONE)");
        Refused(machine, "CHGUSRPRF USRPRF(GGADMIN) GRPPRF(STAFF)", "group profile with members");
        Refused(machine, "CRTUSRPRF USRPRF(BOB) SPCAUT(*EVERYTHING)", "isn't a special authority");
        Refused(machine, "CRTUSRPRF USRPRF(GGADMIN)", "already exists");
        Refused(machine, "CRTUSRPRF USRPRF(1BAD)", "isn't a user profile name");
        Assert.Equal("CPF2204", Run(machine, "CHGUSRPRF USRPRF(NOBODY) TEXT('x')").MessageId);
        var tooMany = string.Join(' ', Enumerable.Range(1, 16).Select(i => $"G{i}"));
        Refused(machine, $"CHGUSRPRF USRPRF(JSMITH) SUPGRPPRF({tooMany})", "at most 15");

        // A group profile with members isn't deleted until they leave it.
        Refused(machine, "DLTUSRPRF GGADMIN", "is the group profile of JSMITH");
        Ok(machine, "CHGUSRPRF USRPRF(JSMITH) GRPPRF(*NONE)");
        Ok(machine, "DLTUSRPRF GGADMIN");
        Assert.Null(machine.Store.FindUser("GGADMIN"));
    }

    [Fact]
    public void ChangingAProfileKeepsWhatTheCommandDoesntName()
    {
        using var machine = Machine.Open();
        var users = Users.Open(machine.Store, new Dictionary<string, string>());
        Ok(machine, "CRTUSRPRF USRPRF(GGADMIN) PASSWORD(*NONE)");
        Ok(machine, "CRTUSRPRF USRPRF(JSMITH) PASSWORD(Pass1234) GRPPRF(GGADMIN) SPCAUT(*SECADM)");

        Ok(machine, "CHGUSRPRF USRPRF(JSMITH) TEXT('Jo Smith')");
        Assert.Equal("GGADMIN", users.Find("JSMITH")!.GroupProfile);
        Assert.Equal(["*SECADM"], users.Find("JSMITH")!.SpecialAuthorities);
        Assert.Equal(SignOnResult.SignedOn, users.Check("JSMITH", "Pass1234").Result);

        Ok(machine, "CHGUSRPRF USRPRF(JSMITH) PASSWORD(Other123) SPCAUT(*NONE)");
        Assert.Empty(users.Find("JSMITH")!.SpecialAuthorities);
        Assert.Equal(SignOnResult.SignedOn, users.Check("JSMITH", "Other123").Result);
        Ok(machine, "CHGUSRPRF USRPRF(JSMITH) PASSWORD(*NONE)");
        Assert.Equal(SignOnResult.NoPassword, users.Check("JSMITH", "Other123").Result);
    }

    [Fact]
    public void TheSettingsUsersKeepTheirGroupsAcrossAStart()
    {
        using var machine = Machine.Open();
        Users.Open(machine.Store, new Dictionary<string, string>());
        Ok(machine, "CRTUSRPRF USRPRF(GGADMIN) PASSWORD(*NONE)");
        Ok(machine, "CHGUSRPRF USRPRF(PRTUSER) GRPPRF(GGADMIN) SPCAUT(*SECADM)");

        // At each start the settings' three users are written again: their groups are kept.
        var users = Users.Open(machine.Store, new Dictionary<string, string>());
        Assert.Equal("GGADMIN", users.Main.GroupProfile);
        Assert.Equal(["*SECADM"], users.Main.SpecialAuthorities);
    }
}
