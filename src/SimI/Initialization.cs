// A library's initial.json: what Sim i does once, when a library first comes from the source folder, after its
// members are built. It's in the library's folder (CRMDEMO/initial.json), and has three parts, run in this order:
//
//   {
//     "user": "QSECOFR",                     who the commands run as (the developer unless given; QSECOFR without one)
//     "users": [                             user profiles: made with CRTUSRPRF, or changed with CHGUSRPRF when there
//       { "name": "CRMUSR1", "password": "Crm12345", "text": "Jordan Lee", "currentLibrary": "CRMDEMO",
//         "initialProgram": "CRMDEMO/CRMMENU", "initialMenu": "*SIGNOFF" }
//     ],
//     "commands": [                          CL commands, as on a command line
//       "CALL CRMDEMO/CRMBUILD PARM('*YES')"
//     ],
//     "data": [                              CSV files in the library's folder, into its tables, as CPYFRMIMPF copies
//       { "file": "data/regions.csv", "table": "REGION", "header": true, "replace": true }
//     ]
//   }
//
// A user's other keys are groupProfile, supplementalGroups, specialAuthorities (a string, or a list), status and
// passwordExpired. A data file's table is in this library unless qualified (LIB/TABLE); "header" skips its first line,
// and "replace" (the default) clears the table first, as MBROPT(*REPLACE) does: false adds to it.
//
// When a step fails, the rest isn't run and the library isn't marked as initialized, so it's all tried again at the
// next start. That's why users are changed when they're there, and data replaces what's in a table: a library's own
// commands should be safe to run twice too. Libraries made before Sim i kept track count as initialized.

using System.Text.Json;

namespace SimI;

public sealed partial class Machine
{
    /// <summary>The file in a library's source folder that initializes it.</summary>
    public const string InitialFile = "initial.json";

    /// <summary>The keys of a user in initial.json, and the CRTUSRPRF parameters they are.</summary>
    private static readonly Dictionary<string, string> UserKeys = new()
    {
        ["password"] = "PASSWORD",
        ["text"] = "TEXT",
        ["currentLibrary"] = "CURLIB",
        ["initialProgram"] = "INLPGM",
        ["initialMenu"] = "INLMNU",
        ["groupProfile"] = "GRPPRF",
        ["supplementalGroups"] = "SUPGRPPRF",
        ["specialAuthorities"] = "SPCAUT",
        ["status"] = "STATUS",
        ["passwordExpired"] = "PWDEXP",
    };

    /// <summary>
    /// Runs the initial.json of each library in <paramref name="directory"/> (a folder for each library, as --source-dir
    /// has it) that hasn't been initialized, making the library when its folder has nothing else. Its commands run as
    /// <paramref name="user"/> unless the file names someone. Returns how many libraries it couldn't finish.
    /// </summary>
    public int Initialize(string directory, string user, Action<string> log)
    {
        var root = Path.GetFullPath(directory);
        var unfinished = 0;
        foreach (var folder in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            var file = Directory.EnumerateFiles(folder)
                .FirstOrDefault(f => Path.GetFileName(f).Equals(InitialFile, StringComparison.OrdinalIgnoreCase));
            if (file is null) continue;
            var library = Path.GetFileName(folder).ToUpperInvariant();
            if (!Store.IsLibraryName(library))
            {
                log($"initialize: {Path.GetRelativePath(root, file)} skipped: {library} isn't a library name");
                continue;
            }
            if (Store.CreateLibrary(library)) log($"initialize: library {library} created");
            if (Store.IsInitialized(library)) continue;
            log($"initialize: {library}, from {Path.GetRelativePath(root, file)}");
            if (InitializeLibrary(library, folder, file, user, log) is { } problem)
            {
                log($"initialize: {library} not finished: {problem} It's tried again at the next start.");
                unfinished++;
                continue;
            }
            Store.MarkInitialized(library);
            log($"initialize: {library} done");
        }
        return unfinished;
    }

    /// <summary>Runs one library's initial.json: null when every step worked, else what went wrong.</summary>
    private string? InitializeLibrary(string library, string folder, string file, string user, Action<string> log)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(file),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException e)
        {
            return $"{InitialFile} isn't JSON: {e.Message}";
        }
        using (document)
        {
            var top = document.RootElement;
            if (top.ValueKind != JsonValueKind.Object) return $"{InitialFile} must be an object, with users, commands and data.";
            if (Unknown(top, ["user", "users", "commands", "data"]) is { } unknown) return unknown;
            if (top.TryGetProperty("user", out var runAs))
            {
                if (runAs.ValueKind != JsonValueKind.String || runAs.GetString()!.Trim() is not { Length: > 0 } name) return "\"user\" must be a user profile's name.";
                user = name.ToUpperInvariant();
            }
            if (Array(top, "users") is { } usersProblem) return usersProblem;
            if (Array(top, "commands") is { } commandsProblem) return commandsProblem;
            if (Array(top, "data") is { } dataProblem) return dataProblem;

            if (top.TryGetProperty("users", out var users))
            {
                foreach (var entry in users.EnumerateArray())
                {
                    if (User(entry) is { } problem) return problem;
                }
            }
            if (top.TryGetProperty("commands", out var commands))
            {
                foreach (var entry in commands.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.String || entry.GetString()!.Trim() is not { Length: > 0 } text) return "Each command must be a string.";
                    var job = new BatchJob(user, CancellationToken.None);
                    var result = Run(text, job);
                    var said = result.Message.Length > 0 ? result.Message : job.State.Messages.LastOrDefault() ?? "";
                    log($"initialize: {text}: {(result.Ok ? said.Length > 0 ? said : "done." : "failed.")}");
                    if (!result.Ok) return $"{text} failed: {(said.Length > 0 ? said : "no message.")}";
                }
            }
            if (top.TryGetProperty("data", out var data))
            {
                foreach (var entry in data.EnumerateArray())
                {
                    if (Data(entry) is { } problem) return problem;
                }
            }
            return null;
        }

        // A user: made, or changed when it's there (as when a start before this one stopped part way).
        string? User(JsonElement entry)
        {
            if (entry.ValueKind != JsonValueKind.Object) return "Each user must be an object with a name.";
            if (Unknown(entry, ["name", .. UserKeys.Keys]) is { } unknown) return unknown;
            if (!entry.TryGetProperty("name", out var nameValue) || nameValue.ValueKind != JsonValueKind.String) return "Each user needs a name.";
            var name = nameValue.GetString()!.Trim().ToUpperInvariant();
            var exists = Store.FindUser(name) is not null;
            var parts = new List<string> { $"{(exists ? "CHGUSRPRF" : "CRTUSRPRF")} USRPRF({name})" };
            foreach (var (key, keyword) in UserKeys)
            {
                if (!entry.TryGetProperty(key, out var value)) continue;
                var text = value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString()!,
                    JsonValueKind.Array when value.EnumerateArray().All(v => v.ValueKind == JsonValueKind.String) =>
                        string.Join(' ', value.EnumerateArray().Select(v => v.GetString())),
                    _ => null,
                };
                if (text is null) return $"User {name}'s {key} must be a string{(key is "supplementalGroups" or "specialAuthorities" ? " or a list of them" : "")}.";
                // A password and a text are taken as they are; the others are names and special values.
                parts.Add(key is "password" or "text" ? $"{keyword}('{text.Replace("'", "''")}')" : $"{keyword}({text})");
            }
            // As CRTUSRPRF has it, the password is the profile's name when none is given: one that's there keeps its own.
            var result = Run(string.Join(' ', parts), new BatchJob(user, CancellationToken.None));
            // The command itself isn't logged: it holds the password.
            log($"initialize: user {name}: {result.Message}");
            return result.Ok ? null : $"user {name}: {result.Message}";
        }

        // A CSV file in the library's folder, into a table.
        string? Data(JsonElement entry)
        {
            if (entry.ValueKind != JsonValueKind.Object) return "Each data entry must be an object with a file and a table.";
            if (Unknown(entry, ["file", "table", "header", "replace"]) is { } unknown) return unknown;
            if (!entry.TryGetProperty("file", out var fileValue) || fileValue.ValueKind != JsonValueKind.String
                || !entry.TryGetProperty("table", out var tableValue) || tableValue.ValueKind != JsonValueKind.String)
                return "Each data entry needs a file and a table.";
            // true or false, or what it is when it isn't there; null when it's something else.
            bool? Flag(string key, bool absent) => !entry.TryGetProperty(key, out var value) ? absent
                : value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
            if (Flag("header", false) is not { } header) return "A data entry's header must be true or false.";
            if (Flag("replace", true) is not { } replace) return "A data entry's replace must be true or false.";
            var relative = fileValue.GetString()!;
            var path = Path.GetFullPath(Path.Combine(folder, relative));
            if (!path.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return $"{relative} isn't in the library's folder.";
            if (!System.IO.File.Exists(path)) return $"{relative} not found in the library's folder.";
            var table = tableValue.GetString()!.Trim().ToUpperInvariant();
            if (!table.Contains('/')) table = $"{library}/{table}";
            if (FindDatabaseFile(table, library) is not { } to) return $"Table {table} not found, for {relative}.";
            var text = System.IO.File.ReadAllText(path);
            // A byte order mark isn't the first field's.
            if (text.StartsWith('﻿')) text = text[1..];
            var result = CopyDelimited(text, relative, to, replace, header ? 1 : 0);
            log($"initialize: {relative} into {to.Library}/{to.Name}: {result.Message}");
            return result.Ok ? null : result.Message;
        }
    }

    /// <summary>Says which key isn't one of <paramref name="known"/>, or null.</summary>
    private static string? Unknown(JsonElement element, IReadOnlyCollection<string> known) =>
        element.EnumerateObject().Select(p => p.Name).FirstOrDefault(name => !known.Contains(name)) is { } unknown
            ? $"{unknown} isn't something {InitialFile} has here: use {string.Join(", ", known)}."
            : null;

    /// <summary>Says what's wrong when <paramref name="key"/> is there and isn't a list, or null.</summary>
    private static string? Array(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Array ? $"\"{key}\" must be a list." : null;
}
