using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SimI.Tests;

/// <summary>
/// The developer the settings name (SIM_I_DEVELOPER_USER): put in the administrators' group at each start, the only
/// user code-server's gate lets in, while only that group's members use the web interface and everyone else is sent
/// to the terminal. Placeholder users and passwords only.
/// </summary>
public class DeveloperTests
{
    private static readonly Dictionary<string, string> Settings = new()
    {
        ["SIM_I_USER"] = Users.DefaultUser,
        ["SIM_I_DEVELOPER_USER"] = "devuser",
        ["SIM_I_DEVELOPER_PASSWORD"] = "Develop1",
    };

    private static CommandResult Run(Machine machine, string command) => machine.Run(command, new BatchJob("DEVUSER", CancellationToken.None));

    private static async Task<(Machine Machine, Users Users, WebServer Server)> Started()
    {
        var machine = Machine.Open();
        var users = Users.Open(machine.Store, Settings);
        var server = await WebServer.StartAsync(0, _ => { }, IPAddress.Loopback, users, machine, new WebServer.Links("/code/", "/terminal/"));
        return (machine, users, server);
    }

    private static HttpClient Client(WebServer server)
    {
        var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{server.Port}/"),
        };
        client.DefaultRequestHeaders.Add("X-Sim-i", "1");
        return client;
    }

    private static Task<HttpResponseMessage> SignOn(HttpClient client, string user, string password) =>
        client.PostAsJsonAsync("api/signon", new { user, password });

    [Fact]
    public void TheDeveloperIsPutInTheAdministratorsGroupWhichHasEveryAuthority()
    {
        using var machine = Machine.Open();
        var users = Users.Open(machine.Store, Settings);

        Assert.Equal("DEVUSER", users.Developer);
        Assert.Equal(Users.DefaultAdminGroup, users.AdminGroup);
        var group = users.Find(Users.DefaultAdminGroup)!;
        Assert.True(group.HasNoPassword);
        Assert.Null(group.GroupProfile);
        Assert.Equal(Users.SpecialAuthorityNames, group.SpecialAuthorities);
        var developer = users.Find("DEVUSER")!;
        Assert.Equal(Users.DefaultAdminGroup, developer.GroupProfile);
        Assert.Equal(SignOnResult.SignedOn, users.Check("devuser", "Develop1").Result);
        Assert.Equal(["DEVUSER"], users.Members(Users.DefaultAdminGroup));

        Assert.True(users.IsAdmin("DEVUSER"));
        Assert.True(users.IsDeveloper("devuser"));
        Assert.False(users.IsAdmin(Users.DefaultUser));
        Assert.False(users.IsDeveloper(Users.DefaultUser));

        // Another administrator: a member of the group, as its group profile or a supplemental group.
        Assert.True(Run(machine, "CRTUSRPRF USRPRF(STAFF) PASSWORD(*NONE)").Ok);
        Assert.True(Run(machine, $"CRTUSRPRF USRPRF(ADMIN2) PASSWORD(Admin123) GRPPRF(STAFF) SUPGRPPRF({Users.DefaultAdminGroup})").Ok);
        Assert.True(users.IsAdmin("ADMIN2"));
        Assert.False(users.IsDeveloper("ADMIN2"));
    }

    [Fact]
    public void EachStartSetsTheDevelopersPasswordAndGroupAndKeepsTheRest()
    {
        using var machine = Machine.Open();
        var users = Users.Open(machine.Store, Settings);
        Assert.True(Run(machine, "CRTUSRPRF USRPRF(STAFF) PASSWORD(*NONE)").Ok);
        Assert.True(Run(machine, "CHGUSRPRF USRPRF(DEVUSER) PASSWORD(Changed1) GRPPRF(STAFF) SUPGRPPRF(*NONE) TEXT('Ada')").Ok);
        Assert.False(users.IsAdmin("DEVUSER"));

        users = Users.Open(machine.Store, Settings);
        var developer = users.Find("DEVUSER")!;
        Assert.Equal(Users.DefaultAdminGroup, developer.GroupProfile);
        Assert.Equal(SignOnResult.SignedOn, users.Check("DEVUSER", "Develop1").Result);
        Assert.True(users.IsAdmin("DEVUSER"));
        Assert.Equal("Ada", machine.Store.FindUser("DEVUSER")!.Text);
    }

    [Fact]
    public void NoDeveloperMeansNoGroupAndEveryUserIsAnAdministrator()
    {
        using var machine = Machine.Open();
        var users = Users.Open(machine.Store, TestUsers.Settings());
        Assert.Null(users.Developer);
        Assert.Null(users.AdminGroup);
        Assert.Null(users.Find(Users.DefaultAdminGroup));
        Assert.True(users.IsAdmin(Users.DefaultUser));
        Assert.False(users.IsDeveloper(Users.DefaultUser));
    }

    [Theory]
    [InlineData("SIM_I_DEVELOPER_PASSWORD", "", "SIM_I_DEVELOPER_PASSWORD is needed")]
    [InlineData("SIM_I_DEVELOPER_PASSWORD", "*NONE", "can't be *NONE")]
    [InlineData("SIM_I_ADMIN_GROUP", "PRTUSER", "already one of the users")]
    [InlineData("SIM_I_ADMIN_GROUP", "DEVUSER", "can't be the developer too")]
    [InlineData("SIM_I_DEVELOPER_USER", "EXPUSER", "already one of the other users")]
    [InlineData("SIM_I_DEVELOPER_USER", "1DEV", "is not an IBM i user name")]
    public void SettingsThatCantWorkStopTheStart(string name, string value, string expected)
    {
        using var machine = Machine.Open();
        var settings = new Dictionary<string, string>(Settings) { [name] = value };
        var error = Assert.Throws<ArgumentException>(() => Users.Open(machine.Store, settings));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public async Task OnlyAdministratorsUseThePagesAndEveryoneElseIsSentToTheTerminal()
    {
        var (machine, _, server) = await Started();
        await using var _ = server;
        using var __ = machine;

        using var user = Client(server);
        var refused = await SignOn(user, Users.DefaultUser, Users.DefaultPassword);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("/terminal/", body.GetProperty("redirect").GetString());
        // No session was made.
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.GetAsync("api/members")).StatusCode);

        using var developer = Client(server);
        Assert.Equal(HttpStatusCode.OK, (await SignOn(developer, "DEVUSER", "Develop1")).StatusCode);
        var me = await developer.GetFromJsonAsync<JsonElement>("api/me");
        Assert.Equal("/code/", me.GetProperty("links").GetProperty("code").GetString());
        Assert.Equal("/terminal/", me.GetProperty("links").GetProperty("terminal").GetString());

        // Another administrator uses the pages, without code-server's link.
        Assert.True(Run(machine, $"CRTUSRPRF USRPRF(ADMIN2) PASSWORD(Admin123) GRPPRF({Users.DefaultAdminGroup})").Ok);
        using var admin = Client(server);
        Assert.Equal(HttpStatusCode.OK, (await SignOn(admin, "ADMIN2", "Admin123")).StatusCode);
        me = await admin.GetFromJsonAsync<JsonElement>("api/me");
        Assert.Equal(JsonValueKind.Null, me.GetProperty("links").GetProperty("code").ValueKind);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("api/members")).StatusCode);

        // Taken out of the group, the next request loses the session and is sent to the terminal.
        Assert.True(Run(machine, "CHGUSRPRF USRPRF(ADMIN2) GRPPRF(*NONE)").Ok);
        var after = await admin.GetAsync("api/members");
        Assert.Equal(HttpStatusCode.Forbidden, after.StatusCode);
        Assert.Equal("/terminal/", (await after.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("redirect").GetString());
        Assert.True(Run(machine, $"CHGUSRPRF USRPRF(ADMIN2) GRPPRF({Users.DefaultAdminGroup})").Ok);
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.GetAsync("api/members")).StatusCode);
    }

    [Fact]
    public async Task CodeServersGateLetsOnlyTheDeveloperIn()
    {
        var (machine, _, server) = await Started();
        await using var _ = server;
        using var __ = machine;

        // Not signed on: to the sign-on page, and back to where they were going.
        using var nobody = Client(server);
        var request = new HttpRequestMessage(HttpMethod.Get, "api/gate/code");
        request.Headers.Add("X-Forwarded-Uri", "/code/?folder=/source");
        var redirected = await nobody.SendAsync(request);
        Assert.Equal(HttpStatusCode.Redirect, redirected.StatusCode);
        Assert.Equal("/?next=%2Fcode%2F%3Ffolder%3D%2Fsource", redirected.Headers.Location!.OriginalString);
        // A forwarded address on another site isn't followed.
        request = new HttpRequestMessage(HttpMethod.Get, "api/gate/code");
        request.Headers.Add("X-Forwarded-Uri", "//evil.example/");
        Assert.Equal("/?next=%2Fcode%2F", (await nobody.SendAsync(request)).Headers.Location!.OriginalString);

        using var developer = Client(server);
        await SignOn(developer, "DEVUSER", "Develop1");
        Assert.Equal(HttpStatusCode.NoContent, (await developer.GetAsync("api/gate/code")).StatusCode);

        Assert.True(Run(machine, $"CRTUSRPRF USRPRF(ADMIN2) PASSWORD(Admin123) GRPPRF({Users.DefaultAdminGroup})").Ok);
        using var admin = Client(server);
        await SignOn(admin, "ADMIN2", "Admin123");
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("api/gate/code")).StatusCode);

        // A disabled developer is shut out too.
        Assert.True(Run(machine, "CHGUSRPRF USRPRF(DEVUSER) STATUS(*DISABLED)").Ok);
        Assert.NotEqual(HttpStatusCode.NoContent, (await developer.GetAsync("api/gate/code")).StatusCode);
    }

    [Theory]
    [InlineData("/code/", "/code/")]
    [InlineData("/", "/")]
    [InlineData("//evil.example/", null)]
    [InlineData("/\\evil.example/", null)]
    [InlineData("https://evil.example/", null)]
    [InlineData("code/", null)]
    [InlineData("/code/\n", null)]
    [InlineData("", null)]
    public void OnlyPathsOnThisSiteAreGoneBackTo(string path, string? expected) => Assert.Equal(expected, WebServer.LocalPath(path));
}
