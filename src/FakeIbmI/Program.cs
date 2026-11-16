// Runs the scripted fake IBM i until Ctrl+C:
//   dotnet run --project src/FakeIbmI -- --port 2323 [--print-port 8474] [--signon-port 8476] [--command-port 8475]
//       [--mapper-port 449] [--env-file .env] [--bind 0.0.0.0] [--trace]
// --print-port also runs its Network Print server (printer output), for PRTUSER / Secret12.
// --signon-port also runs its sign-on server, for the same user and password, which it can change.
// --command-port also runs its remote command server, which answers every command and program call
//     with success (and zeros for output), running nothing.
// --mapper-port also runs its port mapper, which tells clients the other servers' ports.
// --tls-port, --signon-tls-port, --print-tls-port and --command-tls-port add TLS ports
//     (IBM's: 992, 9476, 9474, 9475).
//     --tls-cert names a PKCS #12 file for them, made (with a .crt to trust) when it's missing;
//     without it, a certificate is made for this run only. --tls-name adds names to one it makes.
// --env-file reads settings from a file (default: .env, when there is one); the environment wins.
//     FAKE_IBMI_USER / FAKE_IBMI_PASSWORD set the user every server signs on, and
//     FAKE_IBMI_EXPIRED_USER / _PASSWORD and FAKE_IBMI_LOCKED_USER / _PASSWORD the user whose
//     password has expired and the disabled one. See .env.example.
// --database is the PostgreSQL database's connection string (or FAKE_IBMI_DATABASE): each library is a schema of it,
//     with its source members, programs, display files and the tables embedded SQL works on. QGPL's also keeps the users
//     and the spooled files.
// --source-dir reads a folder of source at start-up, with a folder for each library (MYLIB/QRPGLESRC/prog.rpgle;
//     files directly in it go to QGPL): each member that's new or changed is saved, checked and created
//     (.rpgle, .sqlrpgle, .dspf, .sql), and what the fake can't run is logged by line.
// --check does that without creating anything or starting a server; exit code 3 when something isn't supported.
// --web-port also runs the web source editor: sign on with a user profile to edit, upload and compile source.
// --debug-port also runs the debug server: VS Code (with the extension in vscode/) sets breakpoints in RPG and steps through it.
// --ifs-dir names the folder that is the integrated file system's root (default: a temporary one).
// --bind listens on another address than loopback (0.0.0.0: every interface).
// --trace logs each Telnet negotiation step, and each record's length (never its contents).

using FakeIbmI;

void Log(string line) => Console.WriteLine(line);

try
{
    // The settings come first, as they give the options what the command line doesn't.
    var envFile = Options.EnvFileIn(args) ?? (File.Exists(".env") ? ".env" : null);
    if (Options.EnvFileIn(args) is { } named && !File.Exists(named))
        throw new ArgumentException($"--env-file {named} doesn't exist");
    var settings = Settings.Load(envFile, Environment.GetEnvironmentVariables());
    var options = Options.Parse(args, settings);
    if (options.SourceDir is not null && !Directory.Exists(options.SourceDir))
        throw new ArgumentException($"the source folder {options.SourceDir} doesn't exist (--source-dir, or FAKE_IBMI_SOURCE_DIR in the settings)");
    if (options.Check)
    {
        // Checking runs the SQL members: on a database of its own, on the server --database names (or the scratch server).
        using var scratch = new Machine(Store.Open(options.Database ?? Store.ScratchServer, temporary: true));
        var unsupported = scratch.Import(options.SourceDir!, Log, create: false);
        Console.WriteLine(unsupported == 0 ? "source: everything is supported" : $"source: {unsupported} line{(unsupported == 1 ? "" : "s")} not supported or in error");
        return unsupported == 0 ? 0 : 3;
    }
    // Every server works on the same source, programs and tables.
    // The IFS's root: --ifs-dir, or a folder beside the databases, or (in memory) a temporary one.
    if (options.Database is null)
        throw new ArgumentException("the fake needs a PostgreSQL database: --database \"Host=...;Database=...;Username=...;Password=...\", or FAKE_IBMI_DATABASE in the settings");
    var ifsDir = options.IfsDir;
    using var machine = Machine.Open(options.Database, ifsDir);
    Console.WriteLine($"integrated file system: {machine.Ifs.Root}{(ifsDir is null ? " (temporary: --ifs-dir keeps it)" : "")}");
    var database = new Npgsql.NpgsqlConnectionStringBuilder(options.Database);
    Console.WriteLine($"libraries: {string.Join(", ", machine.Store.Libraries())} (PostgreSQL database {database.Database} on {database.Host})");
    // Every server checks sign-ons against the same users, kept in the database, so a password changed through one holds for all.
    // SAVRSTLIB and SAVRSTOBJ sign on to the remote system as this user when the command names none.
    machine.RemoteUser = settings.GetValueOrDefault("FAKE_IBMI_REMOTE_USER") is { Length: > 0 } remoteUser ? remoteUser.Trim().ToUpperInvariant() : null;
    machine.RemotePassword = settings.GetValueOrDefault("FAKE_IBMI_REMOTE_PASSWORD") is { Length: > 0 } remotePassword ? remotePassword : null;
    machine.Progress = Log;
    machine.InstallSystemSource(Log);
    var users = Users.Open(machine.Store, settings);
    Console.WriteLine($"users{(envFile is null ? "" : $" (settings from {envFile})")}: {string.Join(", ", users.All)}");
    if (options.SourceDir is not null) machine.Import(options.SourceDir, Log);
    var certificate = options.AnyTls
        ? Tls.LoadOrCreate(options.TlsCert, Tls.Names(options.Bind, options.ExtraTlsNames), Log)
        : null;
    TlsOptions? TlsOn(int? port) => port is { } p ? new TlsOptions(p, certificate!) : null;

    await using var host = await TelnetServer.StartAsync(options.Port, Log, options.Bind, options.Trace, TlsOn(options.TlsPort), users, machine);
    Console.WriteLine($"fake IBM i listening on {options.Bind} port {host.Port}{TlsText(host.TlsPort)}; Ctrl+C to stop");

    await using var print = options.PrintPort is { } printPort
        ? await NetworkPrintServer.StartAsync(printPort, Log, options.Bind, users, TlsOn(options.PrintTlsPort), machine)
        : null;
    if (print is not null) Console.WriteLine($"fake Network Print server listening on port {print.Port}{TlsText(print.TlsPort)}");
    await using var signon = options.SignonPort is { } signonPort
        ? await SignOnServer.StartAsync(signonPort, Log, options.Bind, users, TlsOn(options.SignonTlsPort))
        : null;
    if (signon is not null) Console.WriteLine($"fake sign-on server listening on port {signon.Port}{TlsText(signon.TlsPort)}");

    await using var command = options.CommandPort is { } commandPort
        ? await RemoteCommandServer.StartAsync(commandPort, Log, options.Bind, users, TlsOn(options.CommandTlsPort), machine)
        : null;
    if (command is not null) Console.WriteLine($"fake remote command server listening on port {command.Port}{TlsText(command.TlsPort)}");

    await using var web = options.WebPort is { } webPort
        ? await WebServer.StartAsync(webPort, Log, options.Bind, users, machine)
        : null;
    if (web is not null) Console.WriteLine($"fake web source editor listening on port {web.Port}: http://{(options.Bind.Equals(System.Net.IPAddress.Any) ? "localhost" : options.Bind.ToString())}:{web.Port}/");

    await using var debug = options.DebugPort is { } debugPort
        ? await FakeIbmI.Debugging.DapServer.StartAsync(debugPort, Log, options.Bind, users, machine)
        : null;
    if (debug is not null) Console.WriteLine($"fake debug server (Debug Adapter Protocol, for VS Code) listening on port {debug.Port}");

    var services = new Dictionary<string, int>();
    if (command is not null) services["as-rmtcmd"] = command.Port;
    if (command?.TlsPort is { } commandTls) services["as-rmtcmd-s"] = commandTls;
    if (print is not null) services["as-netprt"] = print.Port;
    if (print?.TlsPort is { } printTls) services["as-netprt-s"] = printTls;
    if (signon is not null) services["as-signon"] = signon.Port;
    if (signon?.TlsPort is { } signonTls) services["as-signon-s"] = signonTls;
    await using var mapper = options.MapperPort is { } mapperPort
        ? await PortMapper.StartAsync(mapperPort, services, Log, options.Bind)
        : null;
    if (mapper is not null)
        Console.WriteLine($"fake port mapper listening on port {mapper.Port}, for {(services.Count == 0 ? "no services" : string.Join(", ", services.Keys))}");

    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stop.Cancel();
    };
    try
    {
        await Task.Delay(Timeout.Infinite, stop.Token);
    }
    catch (OperationCanceledException)
    {
    }
}
catch (ArgumentException e)
{
    Console.Error.WriteLine($"fake IBM i: {e.Message}");
    return 2;
}
catch (InvalidOperationException e)
{
    // Too many libraries in the database folder.
    Console.Error.WriteLine($"fake IBM i: {e.Message}");
    return 2;
}
catch (Npgsql.NpgsqlException e)
{
    Console.Error.WriteLine($"fake IBM i can't use the database: {e.Message}");
    return 1;
}
catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"fake IBM i can't use the TLS certificate: {e.Message}");
    return 1;
}
catch (System.Net.Sockets.SocketException e)
{
    // Most often a port in use, or one below 1024 (the port mapper's 449) without root.
    Console.Error.WriteLine($"fake IBM i can't listen: {e.Message}");
    if (e.SocketErrorCode == System.Net.Sockets.SocketError.AccessDenied)
        Console.Error.WriteLine("Ports below 1024 need root or CAP_NET_BIND_SERVICE; in a container, Docker allows them.");
    return 1;
}
return 0;

static string TlsText(int? port) => port is { } p ? $" and TLS port {p}" : "";
