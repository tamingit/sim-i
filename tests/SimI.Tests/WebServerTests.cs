using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SimI.Tests;

/// <summary>The web source editor: sign on, upload and compile, edit and save.</summary>
public class WebServerTests
{
    private static async Task<(Machine Machine, WebServer Server, HttpClient Client)> Started()
    {
        var machine = Machine.Open();
        var users = Users.Open(machine.Store, new Dictionary<string, string>());
        var server = await WebServer.StartAsync(0, _ => { }, IPAddress.Loopback, users, machine);
        var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{server.Port}/"),
        };
        client.DefaultRequestHeaders.Add("X-Sim-i", "1");
        return (machine, server, client);
    }

    private static MultipartFormDataContent Files(params (string Path, string Source)[] files)
    {
        var form = new MultipartFormDataContent();
        foreach (var (path, source) in files) form.Add(new StringContent(source), "files", path);
        return form;
    }

    [Fact]
    public async Task TheReceivePageNeedsASignOnAndARemoteSystemThatAnswers()
    {
        var (machine, server, client) = await Started();
        await using var _ = server;
        using var __ = machine;
        // The page itself is served to anyone; what it asks for isn't.
        Assert.Contains("Receive a library", await client.GetStringAsync("/receive"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/remote")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/remote/survey", new { host = "127.0.0.1", library = "MYLIB" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/signon", new { user = "prtuser", password = "Secret12" })).StatusCode);

        // The room the database has: its size, and (the test server's user being a superuser) what's free.
        var remote = await client.GetFromJsonAsync<JsonElement>("/api/remote");
        Assert.True(remote.GetProperty("storage").GetProperty("database").GetInt64() > 0);
        Assert.Equal(JsonValueKind.Null, remote.GetProperty("transfer").ValueKind);

        // No user or password (and none in the settings), no library, and nothing listening there.
        var noUser = await client.PostAsJsonAsync("/api/remote/survey", new { host = "127.0.0.1", library = "MYLIB" });
        Assert.Equal(HttpStatusCode.BadRequest, noUser.StatusCode);
        var noLibrary = await client.PostAsJsonAsync("/api/remote/survey", new { host = "127.0.0.1", user = "ME", password = "secret" });
        Assert.Equal(HttpStatusCode.BadRequest, noLibrary.StatusCode);
        var unreachable = await client.PostAsJsonAsync("/api/remote/survey", new { host = "127.0.0.1", port = 1, user = "ME", password = "secret", library = "MYLIB" });
        Assert.Equal(HttpStatusCode.BadGateway, unreachable.StatusCode);
        Assert.DoesNotContain("secret", await unreachable.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/remote/pull", new { host = "127.0.0.1", port = 1, user = "ME", password = "secret", library = "MYLIB" })).StatusCode);

        // A receive that can't connect ends at once, saying why, and makes no library.
        var started = await client.PostAsJsonAsync("/api/remote/pull", new { host = "127.0.0.1", port = 1, user = "ME", password = "secret", library = "MYLIB", objects = new[] { "CUSTPF" } });
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        JsonElement state;
        do
        {
            await Task.Delay(50);
            state = await client.GetFromJsonAsync<JsonElement>("/api/remote/pull");
        } while (state.GetProperty("running").GetBoolean());
        Assert.False(state.GetProperty("ok").GetBoolean());
        Assert.Contains("127.0.0.1", state.GetProperty("message").GetString());
        Assert.False(machine.Store.HasLibrary("MYLIB"));
    }

    [Fact]
    public async Task NothingWithoutSigningOn()
    {
        var (machine, server, client) = await Started();
        await using var _ = server;
        using var __ = machine;
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/members")).StatusCode);
        var wrong = await client.PostAsJsonAsync("/api/signon", new { user = "PRTUSER", password = "nope" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/signon", new { user = "prtuser", password = "Secret12" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/members")).StatusCode);

        // A change without the guard header (as a page on another site would send it) is refused.
        using var bare = new HttpRequestMessage(HttpMethod.Put, "/api/member") { Content = JsonContent.Create(new { library = "QGPL", file = "QRPGLESRC", name = "X", type = "RPGLE", source = "" }) };
        bare.Headers.Remove("X-Sim-i");
        client.DefaultRequestHeaders.Remove("X-Sim-i");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(bare)).StatusCode);
    }

    [Fact]
    public async Task UploadCompilesAndFlagsErrorsAndWarnings()
    {
        var (machine, server, client) = await Started();
        await using var _ = server;
        using var __ = machine;
        await client.PostAsJsonAsync("/api/signon", new { user = "PRTUSER", password = "Secret12" });

        var response = await client.PostAsync("/api/upload", Files(
            ("good.rpgle", "**FREE\ndsply 'hi';\n*inlr = *on;\n"),
            ("wrong.rpgle", "**FREE\ndcl-s n int(10);\nn = 'x';\n*inlr = *on;\n"),
            ("partly.rpgle", "**FREE\ndcl-s n int(10);\nn = %nullind(n);\n*inlr = *on;\n"),
            ("notes.txt", "hello")));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()
            .ToDictionary(r => r.GetProperty("path").GetString()!, r => r);
        Assert.Equal("ok", results["good.rpgle"].GetProperty("status").GetString());
        Assert.Equal("error", results["wrong.rpgle"].GetProperty("status").GetString());
        Assert.Contains("N is numeric", results["wrong.rpgle"].GetProperty("diagnostics")[0].GetProperty("message").GetString());
        Assert.Equal("warning", results["partly.rpgle"].GetProperty("status").GetString());
        Assert.Equal("unsupported", results["partly.rpgle"].GetProperty("diagnostics")[0].GetProperty("severity").GetString());
        Assert.Equal("error", results["notes.txt"].GetProperty("status").GetString());

        // What compiled is a program; what's in error isn't; both are members.
        Assert.NotNull(machine.Store.FindObject("QGPL", "GOOD", "*PGM"));
        Assert.NotNull(machine.Store.FindObject("QGPL", "PARTLY", "*PGM"));
        Assert.Null(machine.Store.FindObject("QGPL", "WRONG", "*PGM"));
        var members = (await client.GetFromJsonAsync<JsonElement>("/api/members")).EnumerateArray().ToDictionary(m => m.GetProperty("name").GetString()!, m => m.GetProperty("status").GetString());
        Assert.Equal("error", members["WRONG"]);
        Assert.Equal("warning", members["PARTLY"]);
        Assert.Equal("ok", members["GOOD"]);
    }

    [Fact]
    public async Task AFolderUploadNamesTheLibraryAndSourceFile()
    {
        var (machine, server, client) = await Started();
        await using var _ = server;
        using var __ = machine;
        await client.PostAsJsonAsync("/api/signon", new { user = "PRTUSER", password = "Secret12" });
        var response = await client.PostAsync("/api/upload", Files(
            ("myapp/APPLIB/QRPGLESRC/hello.rpgle", "**FREE\ndsply 'hi';\n*inlr = *on;\n")));
        var result = (await response.Content.ReadFromJsonAsync<JsonElement>())[0];
        Assert.Equal("APPLIB/QRPGLESRC(HELLO)", result.GetProperty("member").GetString());
        Assert.NotNull(machine.Store.FindObject("APPLIB", "HELLO", "*PGM"));
    }

    [Fact]
    public async Task SavingAMemberChecksAndCompilesIt()
    {
        var (machine, server, client) = await Started();
        await using var _ = server;
        using var __ = machine;
        await client.PostAsJsonAsync("/api/signon", new { user = "PRTUSER", password = "Secret12" });
        var member = new { library = "QGPL", file = "QRPGLESRC", name = "EDITED", type = "RPGLE", source = "**FREE\nleave;\n*inlr = *on;\n" };
        var saved = await (await client.PutAsJsonAsync("/api/member", member)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("error", saved.GetProperty("status").GetString());
        Assert.Equal(2, saved.GetProperty("diagnostics")[0].GetProperty("line").GetInt32());

        saved = await (await client.PutAsJsonAsync("/api/member", member with { source = "**FREE\ndsply 'ok';\n*inlr = *on;\n" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", saved.GetProperty("status").GetString());
        Assert.NotNull(machine.Store.FindObject("QGPL", "EDITED", "*PGM"));
        var read = await client.GetFromJsonAsync<JsonElement>("/api/member?library=QGPL&file=QRPGLESRC&name=EDITED");
        Assert.Equal("**FREE\ndsply 'ok';\n*inlr = *on;\n", read.GetProperty("source").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync("/api/member?library=QGPL&file=QRPGLESRC&name=EDITED")).StatusCode);
        Assert.Null(machine.Store.FindMember("QGPL", "QRPGLESRC", "EDITED"));
    }
}
