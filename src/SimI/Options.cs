using System.Net;

namespace SimI;

/// <summary>
/// Sim i's command line: --port (2323), --print-port (none: no Network Print server),
/// --signon-port (none: no sign-on server), --command-port (none: no remote command server), --file-port (none: no file server),
/// --mapper-port (none: no port mapper), --bind
/// (loopback; the container passes 0.0.0.0 so it can be reached from outside it) and --trace
/// (log each Telnet negotiation step, and each record's length). For TLS: --tls-port,
/// --signon-tls-port, --print-tls-port, --command-tls-port and --file-tls-port (each none), --tls-cert (a PKCS #12 file, made when
/// missing) and --tls-name (more names for a certificate it makes, comma-separated). --env-file
/// names the file of settings, such as the users (default: .env, when there is one). --database
/// is the PostgreSQL database's connection string (Host=...;Database=...;Username=...;Password=...), --source-dir a
/// folder of source to read at start-up, a folder for each library, and --check reads that folder, reports what isn't
/// supported and stops. --web-port runs the web source editor (none: no editor). --ifs-dir names the folder that is the
/// integrated file system's root (default: a temporary folder). Settings (SIM_I_PORT, SIM_I_BIND, SIM_I_SOURCE_DIR, SIM_I_DATABASE and the rest, see .env.example) give
/// what the command line doesn't: the command line wins.
/// </summary>
public sealed record Options(
    int Port, int? PrintPort, IPAddress Bind, bool Trace = false, int? SignonPort = null, int? MapperPort = null,
    int? TlsPort = null, int? SignonTlsPort = null, int? PrintTlsPort = null, string? TlsCert = null, string? TlsNames = null,
    string? EnvFile = null, int? CommandPort = null, int? CommandTlsPort = null,
    string? Database = null, string? SourceDir = null, bool Check = false, int? WebPort = null, string? IfsDir = null, int? DebugPort = null,
    int? FilePort = null, int? FileTlsPort = null)
{
    /// <summary>Whether any server listens for TLS, and so needs a certificate.</summary>
    public bool AnyTls => TlsPort is not null || SignonTlsPort is not null || PrintTlsPort is not null || CommandTlsPort is not null
                          || FileTlsPort is not null;

    /// <summary>The environment variables that set the ports, the address and the certificate, as .env.example lists them.</summary>
    public static readonly string[] Variables =
    [
        "SIM_I_BIND", "SIM_I_PORT", "SIM_I_PRINT_PORT", "SIM_I_SIGNON_PORT", "SIM_I_COMMAND_PORT",
        "SIM_I_MAPPER_PORT", "SIM_I_TLS_PORT", "SIM_I_PRINT_TLS_PORT", "SIM_I_SIGNON_TLS_PORT",
        "SIM_I_COMMAND_TLS_PORT", "SIM_I_TLS_CERT", "SIM_I_TLS_NAMES", "SIM_I_SOURCE_DIR", "SIM_I_DATABASE",
        "SIM_I_WEB_PORT", "SIM_I_IFS_DIR", "SIM_I_REMOTE_USER", "SIM_I_REMOTE_PASSWORD", "SIM_I_DEBUG_PORT",
        "SIM_I_FILE_PORT", "SIM_I_FILE_TLS_PORT", "SIM_I_CODE_URL", "SIM_I_TERMINAL_URL",
    ];

    /// <summary>The file --env-file names, which has to be known before the settings in it can be read.</summary>
    public static string? EnvFileIn(IReadOnlyList<string> args)
    {
        var at = args.ToList().LastIndexOf("--env-file");
        return at >= 0 && at + 1 < args.Count ? args[at + 1] : null;
    }

    /// <summary>
    /// The options from the command line. <paramref name="settings"/> (see <see cref="Variables"/>) give what
    /// the command line doesn't: a blank setting is no setting, and a server with no port doesn't run.
    /// </summary>
    public static Options Parse(IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? settings = null)
    {
        string? Setting(string name) =>
            settings is not null && settings.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
        int? SettingPort(string name) => Setting(name) is { } v
            ? int.TryParse(v, out var p) && p is >= 0 and <= 65535 ? p : throw new ArgumentException($"{name} {v} is not a port")
            : null;
        var options = new Options(SettingPort("SIM_I_PORT") ?? 2323, SettingPort("SIM_I_PRINT_PORT"),
            Setting("SIM_I_BIND") is { } bind
                ? IPAddress.TryParse(bind, out var bindAddress) ? bindAddress : throw new ArgumentException($"SIM_I_BIND {bind} is not an IP address")
                : IPAddress.Loopback,
            SignonPort: SettingPort("SIM_I_SIGNON_PORT"), MapperPort: SettingPort("SIM_I_MAPPER_PORT"),
            TlsPort: SettingPort("SIM_I_TLS_PORT"), SignonTlsPort: SettingPort("SIM_I_SIGNON_TLS_PORT"),
            PrintTlsPort: SettingPort("SIM_I_PRINT_TLS_PORT"), TlsCert: Setting("SIM_I_TLS_CERT"),
            TlsNames: Setting("SIM_I_TLS_NAMES"), CommandPort: SettingPort("SIM_I_COMMAND_PORT"),
            CommandTlsPort: SettingPort("SIM_I_COMMAND_TLS_PORT"),
            Database: Setting("SIM_I_DATABASE"), SourceDir: Setting("SIM_I_SOURCE_DIR"),
            WebPort: SettingPort("SIM_I_WEB_PORT"), IfsDir: Setting("SIM_I_IFS_DIR"), DebugPort: SettingPort("SIM_I_DEBUG_PORT"),
            FilePort: SettingPort("SIM_I_FILE_PORT"), FileTlsPort: SettingPort("SIM_I_FILE_TLS_PORT"));
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--trace")
            {
                options = options with { Trace = true };
                continue;
            }
            if (args[i] == "--check")
            {
                options = options with { Check = true };
                continue;
            }
            if (i + 1 >= args.Count) break;
            var (option, value) = (args[i], args[++i]);
            int PortValue() => int.TryParse(value, out var p) && p is >= 0 and <= 65535
                ? p
                : throw new ArgumentException($"{option} {value} is not a port");
            options = option switch
            {
                "--port" => options with { Port = PortValue() },
                "--print-port" => options with { PrintPort = PortValue() },
                "--signon-port" => options with { SignonPort = PortValue() },
                "--mapper-port" => options with { MapperPort = PortValue() },
                "--command-port" => options with { CommandPort = PortValue() },
                "--command-tls-port" => options with { CommandTlsPort = PortValue() },
                "--file-port" => options with { FilePort = PortValue() },
                "--file-tls-port" => options with { FileTlsPort = PortValue() },
                "--tls-port" => options with { TlsPort = PortValue() },
                "--signon-tls-port" => options with { SignonTlsPort = PortValue() },
                "--print-tls-port" => options with { PrintTlsPort = PortValue() },
                "--tls-cert" => options with { TlsCert = value },
                "--env-file" => options with { EnvFile = value },
                "--database" => options with { Database = value },
                "--source-dir" => options with { SourceDir = value },
                "--web-port" => options with { WebPort = PortValue() },
                "--debug-port" => options with { DebugPort = PortValue() },
                "--ifs-dir" => options with { IfsDir = value },
                "--tls-name" => options with { TlsNames = options.TlsNames is null ? value : $"{options.TlsNames},{value}" },
                "--bind" => options with
                {
                    Bind = IPAddress.TryParse(value, out var address) ? address : throw new ArgumentException($"--bind {value} is not an IP address"),
                },
                _ => options,
            };
        }
        // A TLS port is a second port for a server, so the server must run.
        if (options.SignonTlsPort is not null && options.SignonPort is null)
            throw new ArgumentException("--signon-tls-port needs --signon-port");
        if (options.PrintTlsPort is not null && options.PrintPort is null)
            throw new ArgumentException("--print-tls-port needs --print-port");
        if (options.CommandTlsPort is not null && options.CommandPort is null)
            throw new ArgumentException("--command-tls-port needs --command-port");
        if (options.FileTlsPort is not null && options.FilePort is null)
            throw new ArgumentException("--file-tls-port needs --file-port");
        if (options.Check && options.SourceDir is null) throw new ArgumentException("--check needs --source-dir");
        return options;
    }

    /// <summary>The extra names for a certificate Sim i makes.</summary>
    public IEnumerable<string> ExtraTlsNames =>
        (TlsNames ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
