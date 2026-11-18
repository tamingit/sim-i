using System.Text.RegularExpressions;

namespace SimI;

/// <summary>One user profile: its name, password, and whether the password has expired or the profile is disabled.</summary>
public sealed class UserProfile(string user, string password, bool passwordExpired = false, bool disabled = false)
{
    private readonly object _gate = new();
    private string _user = user.ToUpperInvariant();
    private string _password = password;
    private bool _passwordExpired = passwordExpired;
    private bool _disabled = disabled;

    /// <summary>The library this user's names are looked for in first, and new source goes to (QGPL unless set).</summary>
    public string DefaultLibrary { get; init; } = "QGPL";

    /// <summary>Its group profile (GRPPRF), or null for *NONE.</summary>
    public string? GroupProfile { get; init; }

    /// <summary>Its supplemental group profiles (SUPGRPPRF): up to 15, and only with a group profile.</summary>
    public IReadOnlyList<string> SupplementalGroups { get; init; } = [];

    /// <summary>Its special authorities (SPCAUT), such as *SECADM; none for *NONE.</summary>
    public IReadOnlyList<string> SpecialAuthorities { get; init; } = [];

    /// <summary>PASSWORD(*NONE): it can't sign on with any password, as a group profile usually can't.</summary>
    public bool HasNoPassword => Password == Users.NoPassword;

    /// <summary>Called after a password or flag changes, to keep it (the database's users do).</summary>
    internal Action<UserProfile>? Changed { get; set; }

    /// <summary>The user name, upper case.</summary>
    public string User
    {
        get { lock (_gate) return _user; }
        set { lock (_gate) _user = value.ToUpperInvariant(); }
    }

    public string Password
    {
        get { lock (_gate) return _password; }
        set
        {
            lock (_gate) _password = value;
            Changed?.Invoke(this);
        }
    }

    /// <summary>When set, the right password signs on only to be told it has expired. Changing it clears this.</summary>
    public bool PasswordExpired
    {
        get { lock (_gate) return _passwordExpired; }
        set
        {
            lock (_gate) _passwordExpired = value;
            Changed?.Invoke(this);
        }
    }

    /// <summary>When set (STATUS(*DISABLED), as after too many wrong passwords), it can't sign on at all.</summary>
    public bool Disabled
    {
        get { lock (_gate) return _disabled; }
        set
        {
            lock (_gate) _disabled = value;
            Changed?.Invoke(this);
        }
    }

    internal void ChangePassword(string password)
    {
        lock (_gate)
        {
            _password = password;
            _passwordExpired = false;
        }
        Changed?.Invoke(this);
    }

    public override string ToString() =>
        User + (Disabled ? " (disabled)" : PasswordExpired ? " (password expired)" : "");
}

/// <summary>How a sign-on with a user and password turned out.</summary>
public enum SignOnResult
{
    SignedOn,
    UnknownUser,
    Disabled,
    WrongPassword,

    /// <summary>The password is right, but has to be changed before it signs on.</summary>
    PasswordExpired,

    /// <summary>PASSWORD(*NONE): the profile can't sign on with any password.</summary>
    NoPassword,
}

/// <summary>
/// The system's user profiles and password level (QPWDLVL), which every server checks sign-ons
/// against: the 5250 sign-on screen, the sign-on server and Network Print. A password changed
/// through one holds for the others, until Sim i stops.
/// </summary>
public sealed class Users
{
    public const string DefaultUser = "PRTUSER";
    public const string DefaultPassword = "Secret12";
    public const string DefaultExpiredUser = "EXPUSER";
    public const string DefaultExpiredPassword = "Expired1";
    public const string DefaultLockedUser = "LOCKUSER";
    public const string DefaultLockedPassword = "Locked12";

    /// <summary>The password of a profile that can't sign on: PASSWORD(*NONE), as a group profile usually has.</summary>
    public const string NoPassword = "*NONE";

    /// <summary>The special authorities a profile can have (SPCAUT), in the order QSYRUSRI reports them.</summary>
    public static readonly IReadOnlyList<string> SpecialAuthorityNames =
        ["*ALLOBJ", "*SECADM", "*JOBCTL", "*SPLCTL", "*SAVSYS", "*SERVICE", "*AUDIT", "*IOSYSCFG"];

    /// <summary>The most supplemental group profiles one profile can have.</summary>
    public const int MaxSupplementalGroups = 15;

    /// <summary>The environment variables that set the users, as .env.example lists them.</summary>
    public static readonly string[] Variables =
    [
        "SIM_I_USER", "SIM_I_PASSWORD",
        "SIM_I_EXPIRED_USER", "SIM_I_EXPIRED_PASSWORD",
        "SIM_I_LOCKED_USER", "SIM_I_LOCKED_PASSWORD",
        "SIM_I_DEVELOPER_USER", "SIM_I_DEVELOPER_PASSWORD", "SIM_I_ADMIN_GROUP",
    ];

    /// <summary>The administrators' group profile when the settings don't name one.</summary>
    public const string DefaultAdminGroup = "SIMADMIN";

    private static readonly Regex UserName = new("^[A-Z#$@][A-Z0-9#$@_]{0,9}$");

    /// <summary>Whether this is a user profile name: 1 to 10 letters, digits, #, $, @ and _, not starting with a digit or _.</summary>
    public static bool IsUserName(string name) => UserName.IsMatch(name);

    private readonly object _gate = new();
    private readonly Store? _store;
    private readonly UserProfile _main;
    private readonly IReadOnlyList<UserProfile> _fixed = [];
    private int _passwordLevel = 2;

    public Users(UserProfile main, params UserProfile[] others)
    {
        _main = main;
        _fixed = [main, .. others];
        if (_fixed.Select(p => p.User).Distinct().Count() != _fixed.Count)
            throw new ArgumentException("each user needs a different name");
    }

    private Users(Store store, UserProfile main)
    {
        _store = store;
        _main = main;
    }

    /// <summary>
    /// The developer the settings name (SIM_I_DEVELOPER_USER), or null for none: the only user who may use code-server.
    /// </summary>
    public string? Developer { get; private init; }

    /// <summary>
    /// The administrators' group profile (SIM_I_ADMIN_GROUP), made when there's a developer, or null when there isn't.
    /// Its members use the web interface; with no group, every user does.
    /// </summary>
    public string? AdminGroup { get; private init; }

    /// <summary>
    /// Whether this user may use the web interface's pages: a member of the administrators' group (its group profile,
    /// or one of its supplemental groups), or anyone when there's no such group. A disabled or removed profile may not.
    /// </summary>
    public bool IsAdmin(string user) =>
        Find(user) is { Disabled: false } profile
        && (AdminGroup is null || profile.GroupProfile == AdminGroup || profile.SupplementalGroups.Contains(AdminGroup));

    /// <summary>Whether this user is the developer (and still enabled), who may use code-server.</summary>
    public bool IsDeveloper(string user) =>
        Developer is { } developer && user.Trim().ToUpperInvariant() == developer && Find(developer) is { Disabled: false };

    /// <summary>The user that signs on normally. For users in the database, it's the one the settings name, as the database has it now.</summary>
    public UserProfile Main => _store is null ? _main : Find(_main.User) ?? _main;

    /// <summary>Every user. The database's are read when asked: users can be added, changed and removed while Sim i runs.</summary>
    public IReadOnlyList<UserProfile> All => _store is null ? _fixed : [.. _store.UserRows().Select(Profile)];

    /// <summary>
    /// The users, kept in the database's table QSIM_USER, where the user maintenance program (or any SQL)
    /// can add, change and remove them. The users the settings name are put in the table at each start,
    /// as the settings give them; every other user in it is kept as it is. The test user that signs on normally
    /// (PRTUSER) is put there only when the settings name it (SIM_I_USER), so that no user with a well-known
    /// password is made unasked; the expired and disabled users, which can't sign on, always are.
    /// </summary>
    public static Users Open(Store store, IReadOnlyDictionary<string, string> settings)
    {
        var defaults = FromSettings(settings);
        var testUserListed = settings.TryGetValue("SIM_I_USER", out var listed) && !string.IsNullOrWhiteSpace(listed);
        foreach (var profile in defaults._fixed)
        {
            if (profile == defaults._main && !testUserListed) continue;
            var text = profile == defaults._main ? "Signs on normally" : profile.Disabled ? "Disabled" : "Password expired";
            var existing = store.FindUser(profile.User);
            // Its name, password and flags come from the settings; the rest is kept as the table has it.
            store.SaveUser(new UserRow(profile.User, profile.Password, profile.Disabled ? "*DISABLED" : "*ENABLED",
                profile.PasswordExpired ? "*YES" : "*NO", existing?.Text is { Length: > 0 } kept ? kept : text,
                existing?.DefaultLibrary ?? Store.Current, existing?.GroupProfile ?? "*NONE", existing?.SupplementalGroups ?? "",
                existing?.SpecialAuthorities ?? "*NONE"));
        }
        var (developer, adminGroup) = DeveloperFrom(settings, defaults);
        if (developer is not null && adminGroup is not null)
        {
            // The administrators' group: no password, so nobody signs on as it, and every special authority. As on an
            // IBM i, a group profile has no group of its own.
            var group = store.FindUser(adminGroup);
            store.SaveUser(new UserRow(adminGroup, NoPassword, "*ENABLED", "*NO", group?.Text is { Length: > 0 } groupText ? groupText : "Sim i administrators",
                group?.DefaultLibrary ?? Store.Current, "*NONE", "", string.Join(' ', SpecialAuthorityNames)));
            // The developer: its name and password from the settings, the administrators' group as its group profile, and
            // the rest kept as the table has it (a supplemental group can't be its group profile too).
            var existing = store.FindUser(developer.User);
            store.SaveUser(new UserRow(developer.User, developer.Password, "*ENABLED", "*NO",
                existing?.Text is { Length: > 0 } kept ? kept : "Developer", existing?.DefaultLibrary ?? Store.Current, adminGroup,
                string.Join(' ', Names(existing?.SupplementalGroups ?? "").Where(g => g != adminGroup)), existing?.SpecialAuthorities ?? "*NONE"));
        }
        return new Users(store, defaults._main) { Developer = developer?.User, AdminGroup = adminGroup };
    }

    /// <summary>
    /// The developer (SIM_I_DEVELOPER_USER and SIM_I_DEVELOPER_PASSWORD) and the administrators' group it's put in
    /// (SIM_I_ADMIN_GROUP, SIMADMIN unless set), or nulls when no developer is named.
    /// </summary>
    private static (UserProfile? Developer, string? AdminGroup) DeveloperFrom(IReadOnlyDictionary<string, string> settings, Users defaults)
    {
        string? Get(string name) => settings.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
        if (Get("SIM_I_DEVELOPER_USER")?.ToUpperInvariant() is not { } name) return (null, null);
        if (!UserName.IsMatch(name)) throw new ArgumentException($"SIM_I_DEVELOPER_USER {name} is not an IBM i user name");
        var password = Get("SIM_I_DEVELOPER_PASSWORD") ?? throw new ArgumentException($"SIM_I_DEVELOPER_PASSWORD is needed for the developer {name}");
        if (password == NoPassword) throw new ArgumentException("SIM_I_DEVELOPER_PASSWORD can't be *NONE: the developer signs on");
        var group = Get("SIM_I_ADMIN_GROUP")?.ToUpperInvariant() ?? DefaultAdminGroup;
        if (!UserName.IsMatch(group)) throw new ArgumentException($"SIM_I_ADMIN_GROUP {group} is not an IBM i user name");
        if (group == name) throw new ArgumentException($"SIM_I_ADMIN_GROUP {group} can't be the developer too");
        foreach (var other in defaults._fixed)
        {
            if (other.User == name) throw new ArgumentException($"SIM_I_DEVELOPER_USER {name} is already one of the other users the settings name");
            if (other.User == group) throw new ArgumentException($"SIM_I_ADMIN_GROUP {group} is already one of the users the settings name");
        }
        return (new UserProfile(name, password), group);
    }

    private UserProfile Profile(UserRow row) =>
        new(row.User, row.Password, row.PasswordExpired == "*YES", row.Status == "*DISABLED")
        {
            DefaultLibrary = row.DefaultLibrary,
            GroupProfile = row.GroupProfile is "" or "*NONE" ? null : row.GroupProfile,
            SupplementalGroups = Names(row.SupplementalGroups),
            SpecialAuthorities = Names(row.SpecialAuthorities).Where(a => a != "*NONE").ToList(),
            Changed = p => _store!.UpdateUser(p.User, p.Password, p.Disabled, p.PasswordExpired),
        };

    /// <summary>A list kept as names separated by blanks, upper case.</summary>
    internal static IReadOnlyList<string> Names(string text) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(n => n.ToUpperInvariant()).ToList();

    /// <summary>The profiles whose group profile, or one of whose supplemental groups, is <paramref name="group"/>.</summary>
    public IReadOnlyList<string> Members(string group)
    {
        var name = group.Trim().ToUpperInvariant();
        return [.. All.Where(p => p.GroupProfile == name || p.SupplementalGroups.Contains(name)).Select(p => p.User)];
    }

    /// <summary>QPWDLVL: 0 or 1 for DES substitutes (passwords up to 10 characters, any case), 2 or 3 for SHA-1.</summary>
    public int PasswordLevel
    {
        get { lock (_gate) return _passwordLevel; }
        set { lock (_gate) _passwordLevel = value; }
    }

    /// <summary>The default users: PRTUSER / Secret12, EXPUSER / Expired1 (expired) and LOCKUSER / Locked12 (disabled).</summary>
    public static Users Defaults() => FromSettings(new Dictionary<string, string>());

    /// <summary>
    /// The users from the SIM_I_* settings (see <see cref="Variables"/>), each falling back to
    /// its default. A user name is 1 to 10 characters: letters, digits, #, $, @ and _, not starting
    /// with a digit or _.
    /// </summary>
    public static Users FromSettings(IReadOnlyDictionary<string, string> settings)
    {
        string Get(string name, string fallback) =>
            settings.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : fallback;
        UserProfile Profile(string prefix, string user, string password, bool expired = false, bool disabled = false)
        {
            var name = Get($"SIM_I_{prefix}USER", user).ToUpperInvariant();
            if (!UserName.IsMatch(name)) throw new ArgumentException($"SIM_I_{prefix}USER {name} is not an IBM i user name");
            return new UserProfile(name, Get($"SIM_I_{prefix}PASSWORD", password), expired, disabled);
        }
        return new Users(
            Profile("", DefaultUser, DefaultPassword),
            Profile("EXPIRED_", DefaultExpiredUser, DefaultExpiredPassword, expired: true),
            Profile("LOCKED_", DefaultLockedUser, DefaultLockedPassword, disabled: true));
    }

    public UserProfile? Find(string user)
    {
        var name = user.Trim().ToUpperInvariant();
        if (_store is not null) return _store.FindUser(name) is { } row ? Profile(row) : null;
        return _fixed.FirstOrDefault(p => p.User == name);
    }

    /// <summary>
    /// A sign-on with a clear password, as the 5250 sign-on screen takes it: case-sensitive at
    /// password levels 2 and 3, not at 0 and 1. A disabled profile can't sign on whatever the password.
    /// </summary>
    public (SignOnResult Result, UserProfile? Profile) Check(string user, string password)
    {
        var profile = Find(user);
        if (profile is null) return (SignOnResult.UnknownUser, null);
        if (profile.Disabled) return (SignOnResult.Disabled, profile);
        if (profile.HasNoPassword) return (SignOnResult.NoPassword, profile);
        if (!PasswordMatches(profile, password)) return (SignOnResult.WrongPassword, profile);
        if (profile.PasswordExpired) return (SignOnResult.PasswordExpired, profile);
        NoteSignOn(profile);
        return (SignOnResult.SignedOn, profile);
    }

    /// <summary>
    /// Raised when a user signs on, through any server: <see cref="Check"/> raises it, and the servers that check
    /// a password substitute themselves (or sign on after a password change) call <see cref="NoteSignOn"/>.
    /// </summary>
    public event Action<UserProfile>? SignedOn;

    /// <summary>Tells <see cref="SignedOn"/>'s handlers that the user has signed on.</summary>
    internal void NoteSignOn(UserProfile profile) => SignedOn?.Invoke(profile);

    public bool PasswordMatches(UserProfile profile, string password) =>
        PasswordLevel >= 2
            ? password == profile.Password
            : password.Equals(profile.Password, StringComparison.OrdinalIgnoreCase);

    /// <summary>Why a new password won't do, or null when it will.</summary>
    public string? NewPasswordProblem(UserProfile profile, string newPassword)
    {
        if (newPassword.Length == 0) return "New password required.";
        if (newPassword.Length > (PasswordLevel >= 2 ? 128 : 10)) return "New password is too long.";
        if (PasswordMatches(profile, newPassword)) return "New password must be different from the current password.";
        return null;
    }
}
