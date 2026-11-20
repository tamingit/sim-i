// The source editor on the web (--web-port): sign on with a user profile, list, edit and delete
// source members, and upload source files. Saving or uploading checks and compiles, as importing a
// folder does; each member comes back as "error" (the compiler rejects it, or it didn't compile),
// "warning" (Sim i can't run some of it) or "ok", with what checking found, by line.
//
// Its second page (/receive) receives a library from a real IBM i, as SAVRSTLIB and SAVRSTOBJ do:
// it lists what the remote library holds with each object's size there and an estimate of the room
// it takes here, beside the room the database has, and then receives what's chosen while showing
// how far it is.
//
// With a developer in the settings (SIM_I_DEVELOPER_USER), only the administrators' group's members use these pages; anyone
// else who signs on is sent to the terminal (GreenGlow), and /api/gate/code lets only the developer into code-server, for
// the proxy in front of them all (see compose.yaml).

using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SimI;

public sealed class WebServer : IAsyncDisposable
{
    private const string Cookie = "simi_session";

    /// <summary>The header every request that changes something carries, which a page on another site can't send.</summary>
    private const string Guard = "X-Sim-i";

    private const long MaxUpload = 20 * 1024 * 1024;

    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, (string User, DateTime Expires)> _sessions = new();

    /// <summary>The receive that's running, or the last one that ran: one at a time, as they share the database's room.</summary>
    private Transfer? _transfer;
    private readonly Lock _transferGate = new();

    private sealed class Transfer(string id, string user, string what)
    {
        public string Id => id;
        public string User => user;
        public string What => what;
        public Machine.ReceiveStatus Status { get; } = new();
        public CancellationTokenSource Cancel { get; } = new();
        public DateTime Started { get; } = DateTime.UtcNow;
        public long DatabaseAtStart { get; init; }
        public int Chosen { get; init; }
        public volatile CommandResult? Result;
    }

    private WebServer(WebApplication app)
    {
        _app = app;
    }

    public int Port { get; private set; }

    /// <summary>Where the apps beside Sim i are, on the same site: code-server and the terminal (GreenGlow). Null: not there.</summary>
    public sealed record Links(string? Code, string? Terminal);

    private Users _users = null!;
    private Links _links = new(null, null);

    public static async Task<WebServer> StartAsync(int port, Action<string> log, IPAddress bind, Users users, Machine machine, Links? links = null)
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
        var server = new WebServer(app) { _users = users, _links = links ?? new(null, null) };
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

    /// <summary>
    /// The handler, run only for a signed-on user who may use these pages (see <see cref="Users.IsAdmin"/>), and (for a
    /// change) only with the guard header.
    /// </summary>
    private Func<HttpContext, Task> Authorized(Func<HttpContext, string, Task> handler, bool changes = false) => async context =>
    {
        if (SignedOn(context) is not { } user)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { message = "Sign on first." });
            return;
        }
        // Checked at each request: a profile taken out of the group, disabled or deleted loses its session.
        if (!_users.IsAdmin(user))
        {
            if (context.Request.Cookies[Cookie] is { } token) _sessions.TryRemove(token, out _);
            context.Response.Cookies.Delete(Cookie);
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(NotAdmin(user));
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

    /// <summary>What a user who isn't an administrator is told, and where they're sent: the terminal, when there is one.</summary>
    private object NotAdmin(string user) => new
    {
        message = _links.Terminal is null
            ? $"{user} can't use the web interface: it's for the {_users.AdminGroup} group's members."
            : $"{user} uses the terminal: the web interface is for the {_users.AdminGroup} group's members.",
        redirect = _links.Terminal,
    };

    /// <summary>A path on this site to go back to after signing on: one that starts with a single /, or null.</summary>
    internal static string? LocalPath(string? path) =>
        path is { Length: > 0 } && path[0] == '/' && (path.Length == 1 || path[1] is not ('/' or '\\')) && !path.Any(char.IsControl) ? path : null;

    // ---- the API ----

    private void Map(Action<string> log, Users users, Machine machine)
    {
        // The launchpad (and sign-on): each app opens from it in a tab of its own.
        _app.MapGet("/", async context =>
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; img-src 'self' data:";
            await using var page = typeof(WebServer).Assembly.GetManifestResourceStream("Web/home.html")!;
            await page.CopyToAsync(context.Response.Body);
        });

        _app.MapGet("/source", async context =>
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; img-src 'self' data:";
            await using var page = typeof(WebServer).Assembly.GetManifestResourceStream("Web/index.html")!;
            await page.CopyToAsync(context.Response.Body);
        });

        _app.MapGet("/receive", async context =>
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; img-src 'self' data:";
            await using var page = typeof(WebServer).Assembly.GetManifestResourceStream("Web/receive.html")!;
            await page.CopyToAsync(context.Response.Body);
        });
        MapReceive(log, machine);
        MapProfiles(machine);

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
            if (!users.IsAdmin(profile.User))
            {
                // No session: these pages aren't theirs. The page sends them to the terminal, which they sign on to.
                log($"web: sign-on by {profile.User}, who isn't in {users.AdminGroup}: sent to {_links.Terminal ?? "nowhere"}");
                context.Response.StatusCode = 403;
                await context.Response.WriteAsJsonAsync(NotAdmin(profile.User));
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
            context.Response.WriteAsJsonAsync(new
            {
                user, library = machine.DefaultLibrary(user), libraries = machine.Store.Libraries(machine.DefaultLibrary(user)),
                // code-server only for the developer.
                links = new { code = users.IsDeveloper(user) ? _links.Code : null, terminal = _links.Terminal },
            })));

        // The proxy asks this before each request to code-server (Caddy's forward_auth, with the browser's cookie): yes
        // (204) for the developer; to the sign-on page, and back, for anyone not signed on; no (403) for anyone else.
        _app.MapGet("/api/gate/code", async context =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var user = SignedOn(context);
            if (user is not null && users.IsDeveloper(user))
            {
                context.Response.StatusCode = 204;
                return;
            }
            if (user is null)
            {
                var back = LocalPath(context.Request.Headers["X-Forwarded-Uri"].ToString()) ?? _links.Code ?? "/";
                context.Response.Redirect("/?next=" + Uri.EscapeDataString(back));
                return;
            }
            log($"web: {user} was refused code-server: it's the developer's ({users.Developer ?? "none"})");
            context.Response.StatusCode = 403;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync(users.Developer is null
                ? "code-server isn't for anyone: no developer is set (SIM_I_DEVELOPER_USER)."
                : $"code-server is only for {users.Developer}.");
        });

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

        // What the last save of each file's member found, for files named as an upload names them: the editor's
        // Problems for all its source, not only what it has sent. A file with nothing found isn't in the answer.
        _app.MapPost("/api/diagnostics", Authorized(async (context, user) =>
        {
            var request = await context.Request.ReadFromJsonAsync<DiagnosticsRequest>();
            var library = machine.DefaultLibrary(user).ToUpperInvariant();
            var libraries = new Dictionary<string, IReadOnlyDictionary<(string File, string Name), IReadOnlyList<Diagnostic>>>();
            var results = new List<object>();
            foreach (var path in request?.Paths ?? [])
            {
                if (MemberAt(path, library, "") is not { } at) continue;
                if (!libraries.TryGetValue(at.Library, out var members))
                    libraries[at.Library] = members = machine.Store.HasLibrary(at.Library) ? machine.Store.LibraryDiagnostics(at.Library) : new Dictionary<(string, string), IReadOnlyList<Diagnostic>>();
                var found = members.GetValueOrDefault((at.File, at.Name), []).Where(d => d.Severity != Severity.Info).ToList();
                if (found.Count > 0) results.Add(new { path, status = StatusOf(found), member = $"{at.Library}/{at.File}({at.Name})", diagnostics = found.Select(Json) });
            }
            await context.Response.WriteAsJsonAsync(results);
        }));
    }

    private sealed record DiagnosticsRequest(List<string>? Paths);

    // ---- receiving from a real IBM i ----

    private sealed record RemoteRequest(string? Host, int? Port, string? User, string? Password, string? Library, bool? Secure,
        bool? All, List<string>? Objects, List<string>? Omit, bool? Data, string? RestoreTo,
        Dictionary<string, string>? Filters = null, List<string>? Libraries = null);

    private static object Json((long Database, long? Free, long? Total) storage) => new { database = storage.Database, free = storage.Free, total = storage.Total };

    private void MapReceive(Action<string> log, Machine machine)
    {
        // Who signs on to the remote system: the request's user and password, or the settings' (SIM_I_REMOTE_USER).
        Machine.RemoteLogin? Login(RemoteRequest request, out string? problem)
        {
            var host = request.Host?.Trim() ?? "";
            var user = request.User?.Trim().ToUpperInvariant() is { Length: > 0 } named ? named : machine.RemoteUser;
            var password = request.Password is { Length: > 0 } given ? given : user == machine.RemoteUser ? machine.RemotePassword : null;
            var port = request.Port ?? 21;
            problem = host.Length == 0 || host.Any(c => char.IsWhiteSpace(c) || c is '/' or '(' or ')') ? "Name the remote system: its host name or address."
                : port is < 1 or > 65535 ? "The port isn't one."
                : string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password) ? "The remote user and password are needed."
                : (request.Libraries is { Count: > 0 } several ? several : [request.Library ?? ""]).Any(l => !Machine.IsName(l.Trim().ToUpperInvariant())) ? "Name the library."
                : null;
            return problem is null ? new Machine.RemoteLogin(host, port, user!, password!, request.Secure == true ? Remote.RemoteSecurity.Required : Remote.RemoteSecurity.Default) : null;
        }
        async Task<(RemoteRequest? Request, Machine.RemoteLogin? Login)> Read(HttpContext context)
        {
            var request = await context.Request.ReadFromJsonAsync<RemoteRequest>();
            string? problem = "The request is empty.";
            var login = request is null ? null : Login(request, out problem);
            if (login is null)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new { message = problem });
            }
            return (request, login);
        }
        object State(Transfer t, int after)
        {
            var (lines, count) = t.Status.Lines(after);
            var result = t.Result;
            return new
            {
                id = t.Id, what = t.What, user = t.User, running = result is null, cancelling = result is null && t.Cancel.IsCancellationRequested,
                seconds = (int)(DateTime.UtcNow - t.Started).TotalSeconds, chosen = t.Chosen,
                restored = t.Status.Restored, failed = t.Status.Failed, records = t.Status.Records,
                lines, next = count, ok = result?.Ok, message = result?.Message,
                storage = Json(machine.Store.Storage()), databaseAtStart = t.DatabaseAtStart,
            };
        }

        _app.MapGet("/api/remote", Authorized((context, _) =>
        {
            Transfer? transfer;
            lock (_transferGate) transfer = _transfer;
            return context.Response.WriteAsJsonAsync(new
            {
                user = machine.RemoteUser, hasPassword = !string.IsNullOrEmpty(machine.RemotePassword),
                storage = Json(machine.Store.Storage()), libraries = machine.Store.Libraries(),
                transfer = transfer is null ? null : State(transfer, 0),
            });
        }));

        // What the remote library holds, with sizes and estimates: nothing is received.
        _app.MapPost("/api/remote/survey", Authorized(async (context, user) =>
        {
            var (request, login) = await Read(context);
            if (request is null || login is null) return;
            var library = request.Library!.Trim().ToUpperInvariant();
            try
            {
                var (objects, encrypted) = await Task.Run(() => machine.Survey(login, library, new BatchJob(user, context.RequestAborted)));
                log($"web: {user} listed {library} on {login.Host}: {objects.Count} objects");
                await context.Response.WriteAsJsonAsync(new
                {
                    library, host = login.Host, encrypted, here = machine.Store.HasLibrary(library), storage = Json(machine.Store.Storage()),
                    objects = objects.Select(o => new
                    {
                        name = o.Name, type = o.Type, attribute = o.Attribute, kind = o.Kind, text = o.Text, receivable = o.Receivable, size = o.Size,
                        records = o.Records, deleted = o.Deleted, members = o.Members, estimate = o.Estimate, bare = o.EstimateWithoutData,
                    }),
                });
            }
            catch (Exception e) when (e is Remote.RemoteException or IOException or System.Net.Sockets.SocketException)
            {
                context.Response.StatusCode = 502;
                await context.Response.WriteAsJsonAsync(new { message = e.Message });
            }
            catch (OperationCanceledException)
            {
            }
        }, changes: true));

        // The files a program uses, and what each is here.
        _app.MapGet("/api/program/files", Authorized(async (context, user) =>
        {
            var name = context.Request.Query["name"].ToString().Trim().ToUpperInvariant();
            var library = context.Request.Query["library"].ToString().Trim().ToUpperInvariant();
            var files = Machine.IsName(name) ? machine.FilesOf(library.Length > 0 ? library : null, name) : null;
            if (files is null)
            {
                context.Response.StatusCode = 404;
                await context.Response.WriteAsJsonAsync(new { message = $"{name} isn't an RPG member or program on Sim i: save its source first." });
                return;
            }
            await context.Response.WriteAsJsonAsync(new
            {
                program = name, storage = Json(machine.Store.Storage()), remoteUser = machine.RemoteUser, hasRemotePassword = !string.IsNullOrEmpty(machine.RemotePassword),
                files = files.Select(f => new
                {
                    name = f.Name, library = f.Library, how = f.How, use = f.Use, line = f.Line, database = f.Database, kind = f.Kind, hereLibrary = f.HereLibrary,
                    records = f.Records, basedOn = f.BasedOn,
                }),
            });
        }));

        // Where files of these names are on the remote system (the first of the libraries that has each), with sizes and
        // estimates, and with them the physical files their logical files are over.
        _app.MapPost("/api/remote/files", Authorized(async (context, user) =>
        {
            var (request, login) = await Read(context);
            if (request is null || login is null) return;
            var libraries = (request.Libraries is { Count: > 0 } several ? several : [request.Library!]).Select(l => l.Trim().ToUpperInvariant()).Distinct().ToList();
            var wanted = (request.Objects ?? []).Select(n => n.Trim().ToUpperInvariant()).Where(Machine.IsName).Distinct().ToList();
            try
            {
                var found = new List<(string Library, Machine.SurveyedObject File, string? NeededBy)>();
                await Task.Run(() =>
                {
                    var job = new BatchJob(user, context.RequestAborted);
                    foreach (var library in libraries)
                    {
                        var missing = wanted.Where(n => !found.Any(f => f.File.Name == n)).ToList();
                        if (missing.Count == 0) break;
                        found.AddRange(machine.Survey(login, library, job, null, missing).Objects.Where(o => o.Type == "*FILE").Select(o => (library, o, (string?)null)));
                    }
                    // A logical file needs its physical files: they're looked up too, twice over (a view over a view).
                    for (var round = 0; round < 2; round++)
                    {
                        var needed = found.SelectMany(f => f.File.BasedOn.Select(b => (Based: b.Split('/'), By: f.File.Name)))
                            .Where(n => n.Based.Length == 2 && !found.Any(f => f.Library == n.Based[0] && f.File.Name == n.Based[1])).ToList();
                        if (needed.Count == 0) break;
                        foreach (var group in needed.GroupBy(n => n.Based[0]))
                        {
                            var names = group.Select(n => n.Based[1]).Distinct().ToList();
                            foreach (var o in machine.Survey(login, group.Key, job, null, names).Objects.Where(o => o.Type == "*FILE"))
                                found.Add((group.Key, o, group.First(n => n.Based[1] == o.Name).By));
                        }
                    }
                });
                log($"web: {user} looked for {wanted.Count} files on {login.Host}: {found.Count} found");
                await context.Response.WriteAsJsonAsync(new
                {
                    host = login.Host, storage = Json(machine.Store.Storage()),
                    files = found.Select(f => new
                    {
                        name = f.File.Name, library = f.Library, kind = f.File.Kind, text = f.File.Text, receivable = f.File.Receivable, size = f.File.Size, records = f.File.Records,
                        deleted = f.File.Deleted, members = f.File.Members, estimate = f.File.Estimate, bare = f.File.EstimateWithoutData, basedOn = f.File.BasedOn, neededBy = f.NeededBy,
                        here = machine.Here(f.File.Name, machine.Store.HasLibrary(f.Library) ? f.Library : null, null) is var (kind, hereLibrary, records, _)
                            ? new { kind, library = hereLibrary, records } : null,
                    }),
                });
            }
            catch (Exception e) when (e is Remote.RemoteException or IOException or System.Net.Sockets.SocketException)
            {
                context.Response.StatusCode = 502;
                await context.Response.WriteAsJsonAsync(new { message = e.Message });
            }
            catch (OperationCanceledException)
            {
            }
        }, changes: true));

        // Receives the objects chosen (or all of them but those omitted), in the background.
        _app.MapPost("/api/remote/pull", Authorized(async (context, user) =>
        {
            var (request, login) = await Read(context);
            if (request is null || login is null) return;
            var library = request.Library!.Trim().ToUpperInvariant();
            var restoreTo = request.RestoreTo?.Trim().ToUpperInvariant() is { Length: > 0 } to && to != library ? to : null;
            List<string> Names(List<string>? names) => [.. (names ?? []).Select(n => n.Trim().ToUpperInvariant()).Where(n => n.Length > 0).Distinct()];
            var (objects, omit) = (request.All == true ? ["*ALL"] : Names(request.Objects), Names(request.Omit));
            var problem = !Store.IsLibraryName(restoreTo ?? library) ? $"{restoreTo ?? library} isn't a name a library can have here."
                : objects.Count == 0 ? "Choose what to receive."
                : objects.Concat(omit).FirstOrDefault(n => n != "*ALL" && !Machine.IsName(n)) is { } wrong ? $"{wrong} isn't an object name."
                : null;
            Transfer? transfer = null;
            if (problem is null)
            {
                lock (_transferGate)
                {
                    if (_transfer is { Result: null }) problem = $"{_transfer.User} is receiving {_transfer.What}: one at a time.";
                    else
                    {
                        _transfer = transfer = new Transfer(Convert.ToHexString(RandomNumberGenerator.GetBytes(8)), user,
                            $"{library} from {login.Host}{(restoreTo is null ? "" : $" to {restoreTo}")}")
                        {
                            DatabaseAtStart = machine.Store.Storage().Database,
                            Chosen = request.All == true ? 0 : objects.Count,
                        };
                    }
                }
            }
            if (transfer is null)
            {
                context.Response.StatusCode = problem!.Contains("one at a time") ? 409 : 400;
                await context.Response.WriteAsJsonAsync(new { message = problem });
                return;
            }
            log($"web: {user} receives {transfer.What} ({(request.All == true ? "all" : objects.Count)} objects{(request.Data == false ? ", without records" : "")})");
            _ = Task.Run(() =>
            {
                try
                {
                    var filters = request.Filters?.Where(f => !string.IsNullOrWhiteSpace(f.Value)).ToDictionary(f => f.Key.Trim().ToUpperInvariant(), f => f.Value.Trim());
                    transfer.Result = machine.Receive(login, [library], objects, ["*ALL"], omit, restoreTo, request.Data != false, new BatchJob(user, transfer.Cancel.Token), transfer.Status,
                        filters is { Count: > 0 } ? filters : null);
                }
                catch (Exception e)
                {
                    transfer.Result = new CommandResult(false, $"The receive ended: {e.Message}");
                }
                log($"web: {user}'s receive of {transfer.What} ended: {transfer.Result.Message}");
            });
            await context.Response.WriteAsJsonAsync(State(transfer, 0));
        }, changes: true));

        _app.MapGet("/api/remote/pull", Authorized(async (context, _) =>
        {
            Transfer? transfer;
            lock (_transferGate) transfer = _transfer;
            if (transfer is null)
            {
                context.Response.StatusCode = 404;
                await context.Response.WriteAsJsonAsync(new { message = "Nothing has been received." });
                return;
            }
            await context.Response.WriteAsJsonAsync(State(transfer, int.TryParse(context.Request.Query["after"], out var after) ? after : 0));
        }));

        _app.MapPost("/api/remote/cancel", Authorized((context, user) =>
        {
            Transfer? transfer;
            lock (_transferGate) transfer = _transfer;
            if (transfer is { Result: null })
            {
                transfer.Cancel.Cancel();
                log($"web: {user} ended the receive of {transfer.What}");
            }
            return Task.CompletedTask;
        }, changes: true));
    }

    /// <summary>
    /// The member an uploaded file is. A path with folders (from uploading a folder) is read as importing does:
    /// LIBRARY/FILE/name.ext under the folder chosen; otherwise it goes in <paramref name="library"/>, in
    /// <paramref name="file"/> or its type's default source file.
    /// </summary>
    private static (Member? Member, string? Problem) Uploaded(IFormFile upload, string library, string file)
    {
        if (MemberAt(upload.FileName, library, file) is not { } at)
            return (null, $"{Path.GetFileName(upload.FileName)}: the extension isn't one of .rpgle, .sqlrpgle, .rpgleinc, .dspf, .prtf, .pf, .lf, .sql, .clle, .clp or .cmd");
        using var reader = new StreamReader(upload.OpenReadStream());
        var source = reader.ReadToEnd().Replace("\r\n", "\n");
        var member = MemberFrom(at.Library, at.File, at.Name, at.Type, source, out var problem);
        return (member, problem);
    }

    /// <summary>The member a file's path names, as an upload reads it; null when it isn't source.</summary>
    private static (string Library, string File, string Name, string Type)? MemberAt(string path, string library, string file)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count == 0) return null;
        var fileName = parts[^1];
        // A folder upload's paths start with the folder chosen.
        var folders = parts.Count > 2 ? parts.Skip(1).Take(parts.Count - 2).ToList() : [];
        // A text file is a TXT member only in a source file's folder (LIB/QTXTSRC/NOTES.TXT).
        if (Machine.TypeForExtension(Path.GetExtension(fileName)) is not { } type || type == "TXT" && folders.Count < 2) return null;
        var lib = folders.Count > 0 ? Machine.NameFromPath(folders[0]) : library;
        var srcFile = folders.Count > 1 ? Machine.NameFromPath(folders[^1]) : file.Length > 0 ? file.ToUpperInvariant() : Machine.DefaultFile(type);
        return (lib, srcFile, Machine.NameFromPath(Path.GetFileNameWithoutExtension(fileName)), type);
    }

    private static Member? MemberFrom(string? library, string? file, string? name, string? type, string source, out string? problem)
    {
        library = library?.Trim().ToUpperInvariant() ?? "";
        file = file?.Trim().ToUpperInvariant() ?? "";
        name = name?.Trim().ToUpperInvariant() ?? "";
        type = type?.Trim().ToUpperInvariant() ?? "";
        var bad = !Store.IsLibraryName(library) ? library : !Machine.IsName(file) ? file : !Machine.IsName(name) ? name : null;
        problem = bad is not null ? $"{(bad.Length == 0 ? "A name" : bad)} isn't a name of up to 10 letters and digits"
            : Machine.TypeForExtension(type) is null ? $"{type} isn't a source type Sim i reads" : null;
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

    // ---- profiles (the flame graph page) ----

    private void MapProfiles(Machine machine)
    {
        _app.MapGet("/profile", async context =>
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; img-src 'self' data:";
            await using var page = typeof(WebServer).Assembly.GetManifestResourceStream("Web/profile.html")!;
            await page.CopyToAsync(context.Response.Body);
        });

        static object Summary(Profile p) => new
        {
            id = p.Id,
            program = p.Program,
            user = p.User,
            job = p.Job,
            started = p.Started,
            milliseconds = Profile.Milliseconds(p.Elapsed),
            failure = p.Failure,
        };

        static object Tree(ProfileNode n) => new
        {
            kind = n.Kind,
            name = n.Name,
            count = n.Count,
            self = Profile.Milliseconds(n.Self),
            total = Profile.Milliseconds(n.Total),
            children = n.Children.Select(Tree),
        };

        _app.MapGet("/api/profiles", Authorized((context, _) =>
            context.Response.WriteAsJsonAsync(machine.Profiles.All.Select(Summary))));

        _app.MapGet("/api/profiles/{id:int}", Authorized(async (context, _) =>
        {
            if (machine.Profiles.Find(int.Parse((string)context.Request.RouteValues["id"]!, System.Globalization.CultureInfo.InvariantCulture)) is not { } profile)
            {
                context.Response.StatusCode = 404;
                await context.Response.WriteAsJsonAsync(new { message = "That run isn't kept any more." });
                return;
            }
            await context.Response.WriteAsJsonAsync(new { summary = Summary(profile), root = Tree(profile.Root) });
        }));

        _app.MapGet("/api/profiles/{id:int}/folded", Authorized(async (context, _) =>
        {
            if (machine.Profiles.Find(int.Parse((string)context.Request.RouteValues["id"]!, System.Globalization.CultureInfo.InvariantCulture)) is not { } profile)
            {
                context.Response.StatusCode = 404;
                return;
            }
            context.Response.ContentType = "text/plain; charset=utf-8";
            context.Response.Headers.ContentDisposition = $"attachment; filename=\"{profile.Program.Replace('/', '-')}-{profile.Id}.folded.txt\"";
            await context.Response.WriteAsync(profile.Folded());
        }));
    }
}
