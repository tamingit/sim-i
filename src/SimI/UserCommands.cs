// The user profile commands: CRTUSRPRF, CHGUSRPRF, DLTUSRPRF and DSPUSRPRF, on the users in
// QSIM_USER, with an IBM i's rules for group profiles. A group profile is a user profile that
// others name as their group (GRPPRF) or a supplemental group (SUPGRPPRF); it usually has
// PASSWORD(*NONE), so nobody signs on as it. INLPGM is the program a 5250 sign-on calls, and INLMNU(*SIGNOFF) signs off
// when it ends (MAIN shows the main menu). Sim i checks no authority: anyone may run them.

namespace SimI;

public sealed partial class Machine
{
    /// <summary>Runs CRTUSRPRF, CHGUSRPRF, DLTUSRPRF or DSPUSRPRF.</summary>
    private CommandResult RunUserProfile(ClCommand command)
    {
        if (command.Value("USRPRF", 0)?.Trim().ToUpperInvariant() is not { Length: > 0 } name)
            return new CommandResult(false, "Parameter USRPRF required.", MessageId: "CPD0072");
        var existing = Store.FindUser(name);
        switch (command.Name)
        {
            case "CRTUSRPRF":
            {
                if (!Users.IsUserName(name)) return Fail($"{name} isn't a user profile name.");
                if (existing is not null) return Fail($"User profile {name} already exists.");
                var password = command.Value("PASSWORD", 1) is { } given ? Unquoted(given) : "*USRPRF";
                var row = new UserRow(name, password.ToUpperInvariant() switch
                    {
                        "*USRPRF" => name,
                        "*NONE" => Users.NoPassword,
                        _ => password,
                    },
                    "*ENABLED", "*NO", "", Store.Current);
                return Save(row, command, creating: true) is { } problem
                    ? problem
                    : new CommandResult(true, $"User profile {name} created.");
            }
            case "CHGUSRPRF":
            {
                if (existing is null) return Fail($"User profile {name} not found.", "CPF2204");
                var row = existing;
                if (command.Value("PASSWORD", 1) is { } given && Unquoted(given) is var password
                    && !password.Equals("*SAME", StringComparison.OrdinalIgnoreCase))
                    row = row with
                    {
                        Password = password.ToUpperInvariant() switch
                        {
                            "*USRPRF" => name,
                            "*NONE" => Users.NoPassword,
                            _ => password,
                        },
                    };
                return Save(row, command, creating: false) is { } problem
                    ? problem
                    : new CommandResult(true, $"User profile {name} changed.");
            }
            case "DLTUSRPRF":
            {
                if (existing is null) return Fail($"User profile {name} not found.", "CPF2204");
                var members = MembersOf(name);
                if (members.Count > 0)
                    return Fail($"User profile {name} is the group profile of {string.Join(", ", members)}: change their group profiles first.");
                Store.DeleteUser(name);
                return new CommandResult(true, $"User profile {name} deleted.");
            }
            default: // DSPUSRPRF
            {
                if (existing is null) return Fail($"User profile {name} not found.", "CPF2204");
                if (command.Value("TYPE", 1)?.Trim().ToUpperInvariant() == "*GRPMBR")
                {
                    var members = MembersOf(name);
                    return new CommandResult(true, members.Count == 0
                        ? $"User profile {name} has no members."
                        : $"Members of group profile {name}: {string.Join(", ", members)}.");
                }
                var supplemental = existing.SupplementalGroups.Trim();
                return new CommandResult(true,
                    $"User profile {name}: status {existing.Status}, password {(existing.Password == Users.NoPassword ? "*NONE" : "set")}" +
                    $"{(existing.PasswordExpired == "*YES" ? " (expired)" : "")}, group profile {existing.GroupProfile}, " +
                    $"supplemental groups {(supplemental.Length == 0 ? "*NONE" : supplemental)}, special authorities {existing.SpecialAuthorities}, " +
                    $"current library {existing.DefaultLibrary}, initial program {existing.InitialProgram}, initial menu {existing.InitialMenu}.");
            }
        }

        CommandResult Fail(string message, string? id = null) => new(false, message, MessageId: id);
    }

    /// <summary>The profiles whose group profile or a supplemental group is <paramref name="group"/>.</summary>
    private List<string> MembersOf(string group) =>
        [.. Store.UserRows().Where(u => u.GroupProfile == group || Users.Names(u.SupplementalGroups).Contains(group)).Select(u => u.User)];

    /// <summary>
    /// Applies the command's STATUS, PWDEXP, TEXT, CURLIB, INLPGM, INLMNU, GRPPRF, SUPGRPPRF and SPCAUT to the row and saves it,
    /// or says what's wrong. The rules are an IBM i's: a group profile is another profile that has no group
    /// itself, supplemental groups need a group profile, and there are at most 15 of them.
    /// </summary>
    private CommandResult? Save(UserRow row, ClCommand command, bool creating)
    {
        string? Keyword(string name) => command.Value(name, -1) is { } value ? Unquoted(value) : null;
        CommandResult Fail(string message, string? id = null) => new(false, message, MessageId: id);

        if (Keyword("STATUS")?.ToUpperInvariant() is { } status)
        {
            if (status is not ("*ENABLED" or "*DISABLED")) return Fail("STATUS must be *ENABLED or *DISABLED.");
            row = row with { Status = status };
        }
        if (Keyword("PWDEXP")?.ToUpperInvariant() is { } expired)
        {
            if (expired is not ("*YES" or "*NO")) return Fail("PWDEXP must be *YES or *NO.");
            row = row with { PasswordExpired = expired };
        }
        if (Keyword("TEXT") is { } text) row = row with { Text = text.Equals("*BLANK", StringComparison.OrdinalIgnoreCase) ? "" : text };
        if (Keyword("CURLIB")?.ToUpperInvariant() is { } library)
        {
            library = library is "*CRTDFT" or "*SAME" ? (library == "*CRTDFT" ? Store.Current : row.DefaultLibrary) : library;
            if (!Store.HasLibrary(library)) return Fail($"Library {library} not found.", "CPF2110");
            row = row with { DefaultLibrary = library };
        }
        if (Keyword("INLPGM")?.ToUpperInvariant().Replace(" ", "") is { } program && program != "*SAME")
        {
            // LIB/PGM or PGM (*LIBL/PGM: found in the library list). The program needn't exist yet, as on an IBM i:
            // a sign-on finds out.
            var (programLibrary, programName) = Qualified(program);
            if (program != "*NONE" && (!IsName(programName) || programLibrary is not null && !Store.IsLibraryName(programLibrary)))
                return Fail($"{program} isn't a program: give LIB/PGM, PGM or *NONE.");
            row = row with { InitialProgram = program == "*NONE" ? program : programLibrary is null ? programName : $"{programLibrary}/{programName}" };
        }
        if (Keyword("INLMNU")?.ToUpperInvariant().Replace(" ", "") is { } menu && menu != "*SAME")
        {
            // Sim i has one menu, the main menu; *SIGNOFF signs off when the initial program ends.
            menu = menu is "*LIBL/MAIN" or "QSYS/MAIN" ? "MAIN" : menu;
            if (menu is not ("MAIN" or "*SIGNOFF")) return Fail($"Menu {menu} not found: give MAIN or *SIGNOFF.", "CPF2105");
            row = row with { InitialMenu = menu };
        }
        if (Keyword("SPCAUT")?.ToUpperInvariant() is { } authorities)
        {
            var list = Users.Names(authorities).Where(a => a is not ("*NONE" or "*USRCLS" or "*SAME")).Distinct().ToList();
            if (list.FirstOrDefault(a => !Users.SpecialAuthorityNames.Contains(a)) is { } bad)
                return Fail($"{bad} isn't a special authority: use {string.Join(", ", Users.SpecialAuthorityNames)} or *NONE.");
            if (!authorities.Contains("*SAME"))
                row = row with { SpecialAuthorities = list.Count == 0 ? "*NONE" : string.Join(' ', Users.SpecialAuthorityNames.Where(list.Contains)) };
        }
        var group = Keyword("GRPPRF")?.ToUpperInvariant() is { } g && g != "*SAME" ? g : row.GroupProfile;
        var supplemental = Keyword("SUPGRPPRF")?.ToUpperInvariant() is { } s && s != "*SAME"
            ? Users.Names(s).Where(n => n != "*NONE").Distinct().ToList()
            : [.. Users.Names(row.SupplementalGroups)];
        if (group != "*NONE" || supplemental.Count > 0)
        {
            if (group == "*NONE") return Fail("A profile with supplemental groups needs a group profile (GRPPRF) too.");
            if (supplemental.Count > Users.MaxSupplementalGroups)
                return Fail($"A profile can have at most {Users.MaxSupplementalGroups} supplemental groups.");
            supplemental.Remove(group);
            foreach (var name in supplemental.Prepend(group))
            {
                if (name == row.User) return Fail($"{name} can't be its own group profile.");
                var other = Store.FindUser(name);
                if (other is null) return Fail($"Group profile {name} not found.", "CPF2204");
                if (other.GroupProfile != "*NONE") return Fail($"{name} has a group profile of its own, so it can't be one.");
            }
            if (!creating && MembersOf(row.User).Count > 0)
                return Fail($"{row.User} is a group profile with members, so it can't have a group profile of its own.");
        }
        row = row with { GroupProfile = group, SupplementalGroups = string.Join(' ', supplemental) };
        Store.SaveUser(row);
        return null;
    }
}
