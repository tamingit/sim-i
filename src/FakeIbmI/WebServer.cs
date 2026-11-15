// The source editor on the web (--web-port): sign on with a user profile, list, edit and delete
// source members, and upload source files. Saving or uploading checks and compiles, as importing a
// folder does; each member comes back as "error" (the compiler rejects it, or it didn't compile),
// "warning" (the fake can't run some of it) or "ok", with what checking found, by line.

using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FakeIbmI;

public sealed class WebServer : IAsyncDisposable
{
    private const string Cookie = "fakeibmi_session";

    /// <summary>The header every request that changes something carries, which a page on another site can't send.</summary>
    private const string Guard = "X-Fake-IBMi";

    private const long MaxUpload = 20 * 1024 * 1024;

    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, (string User, DateTime Expires)> _sessions = new();

    private WebServer(WebApplication app)
    {
        _app = app;
    }

    public int Port { get; private set; }

    public static async Task<WebServer> StartAsync(int port, Action<string> log, IPAddress bind, Users users, Machine machine)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(bind, port);
            k.Limits.MaxRequestBodySize = MaxUpload;
        });
        builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = MaxUpload);
        var app = builder.Build();
        var server = new WebServer(app);
        server.Map(log, users, machine);
        await app.StartAsync();
        server.Port = new Uri(app.Urls.First()).Port;
        return server;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    // ---- sessions ----

    private string? SignedOn(HttpContext context)
    {
        if (context.Request.Cookies[Cookie] is not { } token || !_sessions.TryGetValue(token, out var session)) return null;
        if (session.Expires < DateTime.UtcNow)
        {
            _sessions.TryRemove(token, out _);
            return null;
        }
        return session.User;
    }

    /// <summary>The handler, run only for a signed-on user, and (for a change) only with the guard header.</summary>
    private Func<HttpContext, Task> Authorized(Func<HttpContext, string, Task> handler, bool changes = false) => async context =>
    {
        if (SignedOn(context) is not { } user)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { message = "Sign on first." });
            return;
        }
        if (changes && context.Request.Headers[Guard] != "1")
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new { message = $"The request needs the {Guard} header." });
            return;
        }
        await handler(context, user);
    };

    // ---- the API ----

    private void Map(Action<string> log, Users users, Machine machine)
    {
        _app.MapGet("/", async context =>
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; img-src 'self' data:";
            await using var page = typeof(WebServer).Assembly.GetManifestResourceStream("Web/index.html")!;
            await page.CopyToAsync(context.Response.Body);
        });

        _app.MapPost("/api/signon", async context =>
        {
            if (context.Request.Headers[Guard] != "1")
            {
                context.Response.StatusCode = 403;
                return;
            }
            var request = await context.Request.ReadFromJsonAsync<SignOnRequest>();
            var (result, profile) = users.Check(request?.User ?? "", request?.Password ?? "");
            if (result != SignOnResult.SignedOn || profile is null)
            {
                log($"web: sign-on by {request?.User?.ToUpperInvariant()} refused ({result})");
                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(new
                {
                    message = result switch
                    {
                        SignOnResult.Disabled => "The user profile is disabled.",
                        SignOnResult.PasswordExpired => "The password has expired: change it by signing on to the 5250 display.",
                        _ => "The user or password isn't right.",
                    },
                });
                return;
            }
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            _sessions[token] = (profile.User, DateTime.UtcNow.AddHours(8));
            context.Response.Cookies.Append(Cookie, token, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Secure = context.Request.IsHttps,
                MaxAge = TimeSpan.FromHours(8),
            });
            log($"web: sign-on by {profile.User}");
            await context.Response.WriteAsJsonAsync(new { user = profile.User, library = machine.DefaultLibrary(profile.User) });
        });

        _app.MapPost("/api/signoff", Authorized((context, signedOn) =>
        {
            if (context.Request.Cookies[Cookie] is { } token) _sessions.TryRemove(token, out _);
            context.Response.Cookies.Delete(Cookie);
            return Task.CompletedTask;
        }, changes: true));

        _app.MapGet("/api/me", Authorized((context, user) =>
            context.Response.WriteAsJsonAsync(new { user, library = machine.DefaultLibrary(user), libraries = machine.Store.Libraries(machine.DefaultLibrary(user)) })));

        _app.MapGet("/api/members", Authorized((context, _) =>
            context.Response.WriteAsJsonAsync(machine.Store.Members().Select(m =>
            {
                var found = machine.Store.Diagnostics(m);
                return new
                {
                    library = m.Library,
                    file = m.File,
                    name = m.Name,
                    type = m.Type,
                    status = StatusOf(found),
                    errors = found.Count(d => d.Severity == Severity.Error),
                    warnings = found.Count(d => d.Severity is Severity.Unsupported or Severity.Warning),
                };
            }))));

        _app.MapGet("/api/member", Authorized(async (context, _) =>
        {
            var q = context.Request.Query;
            if (machine.Store.FindMember(q["library"], q["file"], q["name"].ToString()) is not { } member || member.Library != q["library"].ToString().ToUpperInvariant())
            {
                context.Response.StatusCode = 404;
                await context.Response.WriteAsJsonAsync(new { message = "There's no such member." });
                return;
            }
            var found = machine.Store.Diagnostics(member);
            await context.Response.WriteAsJsonAsync(new
            {
                library = member.Library,
                file = member.File,
                name = member.Name,
                type = member.Type,
                source = member.Source,
                status = StatusOf(found),
                diagnostics = found.Select(Json),
            });
        }));

        _app.MapPut("/api/member", Authorized(async (context, user) =>
        {
            var request = await context.Request.ReadFromJsonAsync<SaveRequest>();
            string? problem = "The request has no member.";
            var member = request is null ? null : MemberFrom(request.Library, request.File, request.Name, request.Type, request.Source ?? "", out problem);
            if (member is null)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new { message = problem });
                return;
            }
            if (!machine.Store.HasLibrary(member.Library))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new { message = $"Library {member.Library} not found." });
                return;
            }
            var built = machine.Build([member]).Single();
            log($"web: {user} saved {member} {member.Type}: {built.Result.Message}");
            await context.Response.WriteAsJsonAsync(Json(built));
        }, changes: true));

        _app.MapDelete("/api/member", Authorized(async (context, user) =>
        {
            var q = context.Request.Query;
            if (machine.Store.FindMember(q["library"], q["file"], q["name"].ToString()) is not { } member)
            {
                context.Response.StatusCode = 404;
                return;
            }
            machine.Store.DeleteMember(member);
            log($"web: {user} deleted {member}");
            await context.Response.WriteAsJsonAsync(new { deleted = member.ToString() });
        }, changes: true));

        _app.MapPost("/api/upload", Authorized(async (context, user) =>
        {
            var form = await context.Request.ReadFormAsync();
            var library = (form["library"].ToString() is { Length: > 0 } l ? l : machine.DefaultLibrary(user)).ToUpperInvariant();
            var results = new List<object>();
            var members = new List<Member>();
            var paths = new Dictionary<(string, string, string), string>();
            foreach (var upload in form.Files)
            {
                var (member, problem) = Uploaded(upload, library, form["file"].ToString());
                if (member is null)
                {
                    results.Add(new { path = upload.FileName, status = "error", message = problem, diagnostics = Array.Empty<object>() });
                    continue;
                }
                if (!machine.Store.HasLibrary(member.Library))
                {
                    try
                    {
                        machine.Store.CreateLibrary(member.Library);
                        log($"web: {user} created library {member.Library}");
                    }
                    catch (Exception e) when (e is ArgumentException or InvalidOperationException)
                    {
                        results.Add(new { path = upload.FileName, status = "error", message = e.Message, diagnostics = Array.Empty<object>() });
                        continue;
                    }
                }
                members.RemoveAll(m => m.Library == member.Library && m.File == member.File && m.Name == member.Name);
                members.Add(member);
                paths[(member.Library, member.File, member.Name)] = upload.FileName;
            }
            foreach (var built in machine.Build(members))
            {
                log($"web: {user} uploaded {built.Member} {built.Member.Type}: {built.Status}");
                results.Add(new
                {
                    path = paths.GetValueOrDefault((built.Member.Library, built.Member.File, built.Member.Name), built.Member.ToString()),
                    built.Status,
                    member = built.Member.ToString(),
                    library = built.Member.Library,
                    file = built.Member.File,
                    name = built.Member.Name,
                    type = built.Member.Type,
                    message = built.Result.Message,
                    diagnostics = built.Diagnostics.Where(d => d.Severity != Severity.Info).Select(Json),
                });
            }
            await context.Response.WriteAsJsonAsync(results);
        }, changes: true));
    }

    /// <summary>
    /// The member an uploaded file is. A path with folders (from uploading a folder) is read as importing does:
    /// LIBRARY/FILE/name.ext under the folder chosen; otherwise it goes in <paramref name="library"/>, in
    /// <paramref name="file"/> or its type's default source file.
    /// </summary>
    private static (Member? Member, string? Problem) Uploaded(IFormFile upload, string library, string file)
    {
        var parts = upload.FileName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        var fileName = parts[^1];
        // A folder upload's paths start with the folder chosen.
        var folders = parts.Count > 2 ? parts.Skip(1).Take(parts.Count - 2).ToList() : [];
        // A text file is a TXT member only in a source file's folder (LIB/QTXTSRC/NOTES.TXT).
        if (Machine.TypeForExtension(Path.GetExtension(fileName)) is not { } type || type == "TXT" && folders.Count < 2)
            return (null, $"{fileName}: the extension isn't one of .rpgle, .sqlrpgle, .rpgleinc, .dspf, .prtf, .pf, .lf, .sql, .clle, .clp or .cmd");
        var lib = folders.Count > 0 ? folders[0].ToUpperInvariant() : library;
        var srcFile = folders.Count > 1 ? folders[^1].ToUpperInvariant() : file.Length > 0 ? file.ToUpperInvariant() : Machine.DefaultFile(type);
        using var reader = new StreamReader(upload.OpenReadStream());
        var source = reader.ReadToEnd().Replace("\r\n", "\n");
        var member = MemberFrom(lib, srcFile, Path.GetFileNameWithoutExtension(fileName), type, source, out var problem);
        return (member, problem);
    }

    private static Member? MemberFrom(string? library, string? file, string? name, string? type, string source, out string? problem)
    {
        library = library?.Trim().ToUpperInvariant() ?? "";
        file = file?.Trim().ToUpperInvariant() ?? "";
        name = name?.Trim().ToUpperInvariant() ?? "";
        type = type?.Trim().ToUpperInvariant() ?? "";
        var bad = !Store.IsLibraryName(library) ? library : !Machine.IsName(file) ? file : !Machine.IsName(name) ? name : null;
        problem = bad is not null ? $"{(bad.Length == 0 ? "A name" : bad)} isn't a name of up to 10 letters and digits"
            : Machine.TypeForExtension(type) is null ? $"{type} isn't a source type the fake reads" : null;
        return problem is null ? new Member(library, file, name, Machine.TypeForExtension(type)!, source) : null;
    }

    private static string StatusOf(IReadOnlyList<Diagnostic> found) =>
        found.Any(d => d.Severity == Severity.Error) ? "error"
        : found.Any(d => d.Severity is Severity.Unsupported or Severity.Warning) ? "warning"
        : "ok";

    private static object Json(Diagnostic d) => new
    {
        line = d.Line,
        severity = d.Severity switch
        {
            Severity.Error => "error",
            Severity.Unsupported => "unsupported",
            Severity.Warning => "warning",
            _ => "info",
        },
        message = d.Message,
    };

    private static object Json(BuiltMember built) => new
    {
        member = built.Member.ToString(),
        library = built.Member.Library,
        file = built.Member.File,
        name = built.Member.Name,
        type = built.Member.Type,
        built.Status,
        message = built.Result.Message,
        diagnostics = built.Diagnostics.Select(Json),
    };

    private sealed record SignOnRequest(string? User, string? Password);

    private sealed record SaveRequest(string? Library, string? File, string? Name, string? Type, string? Source);
}
