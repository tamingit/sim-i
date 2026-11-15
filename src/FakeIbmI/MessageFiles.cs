// Message files (*MSGF): CRTMSGF makes one, ADDMSGD, CHGMSGD and RMVMSGD change its message
// descriptions, DLTMSGF deletes it. A display file's ERRMSGID, MSGID, SFLMSGID and CHKMSGID show
// their text. A message file kept as source is the CL that makes it (as ARCAD writes it, with
// &O/&N for its library and name); building the member runs that CL.

using System.Collections.Concurrent;
using System.Text;
using FakeIbmI.Rpg;

namespace FakeIbmI;

public sealed partial class Machine
{
    /// <summary>A message description: its text, and its second-level (help) text.</summary>
    internal sealed record MessageDescription(string Text, string? Help);

    private readonly ConcurrentDictionary<(string Library, string Name, long Version), Dictionary<string, MessageDescription>> _messageFiles = [];

    /// <summary>A message file's descriptions, by message id. The object's source keeps them, a line each.</summary>
    private Dictionary<string, MessageDescription> Descriptions(StoredObject file) =>
        _messageFiles.GetOrAdd((file.Library, file.Name, file.Version), _ =>
        {
            var descriptions = new Dictionary<string, MessageDescription>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in file.Source.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\u0001');
                if (parts.Length >= 2) descriptions[parts[0]] = new MessageDescription(parts[1], parts.Length > 2 && parts[2].Length > 0 ? parts[2] : null);
            }
            return descriptions;
        });

    private static string Serialized(Dictionary<string, MessageDescription> descriptions) =>
        string.Join('\n', descriptions.OrderBy(d => d.Key, StringComparer.Ordinal)
            .Select(d => $"{d.Key}\u0001{OneLine(d.Value.Text)}\u0001{OneLine(d.Value.Help ?? "")}"));

    private static string OneLine(string text) => text.Replace('\n', ' ').Replace('\r', ' ').Replace('\u0001', ' ');

    /// <summary>
    /// A message's text, from the message file named (LIB/NAME or NAME, on the library list) or, with
    /// none, from any; its substitution variables (&1 ...) are blank, as with no message data. Null when there's no such message.
    /// </summary>
    internal string? MessageText(string id, string? messageFile, string? first = null)
    {
        IEnumerable<StoredObject> files;
        if (messageFile is { Length: > 0 } && !messageFile.Equals("*LIBL", StringComparison.OrdinalIgnoreCase))
        {
            var (library, name) = Qualified(messageFile);
            files = Store.FindObject(library, name, "*MSGF", first) is { } found ? [found] : [];
        }
        else files = Store.Objects("*MSGF");
        foreach (var file in files)
        {
            if (Descriptions(file).TryGetValue(id, out var description))
                return System.Text.RegularExpressions.Regex.Replace(description.Text, @"&\d+", "").TrimEnd();
        }
        return null;
    }

    /// <summary>
    /// QMHSNDPM: a message to a program message queue (the message subfile shows it), from its id and
    /// message file (the first 10 characters the file, the next its library), with its data in
    /// &1, or with no id the data as the text. QMHRMVPM removes the queue's messages.
    /// </summary>
    private void ProgramMessage(string api, IReadOnlyList<Rpg.Cell> arguments, IJobHost host)
    {
        string Text(int i) => i < arguments.Count ? Rpg.Values.ToText(arguments[i].Get()) : "";
        if (api == "QMHRMVPM")
        {
            host.State.ProgramMessages.Clear();
            return;
        }
        var id = Text(0).Trim().ToUpperInvariant();
        var qualified = Text(1).PadRight(20);
        var data = Text(2);
        if (arguments.Count > 3 && arguments[3].Type.IsNumeric && (int)Rpg.Values.ToDecimal(arguments[3].Get()) is var length && length >= 0 && length < data.Length)
            data = data[..length];
        string text;
        if (id.Length == 0) text = data.TrimEnd();
        else
        {
            var file = qualified[..10].Trim();
            var library = qualified[10..20].Trim();
            var described = file.Length == 0 ? null : MessageTemplate(id, library.Length > 0 && !library.StartsWith('*') ? $"{library}/{file}" : file);
            text = described is null ? id : System.Text.RegularExpressions.Regex.Replace(described, @"&1\b", data.Trim()).Replace("&2", "").TrimEnd();
            text = System.Text.RegularExpressions.Regex.Replace(text, @"&\d+", "");
        }
        host.State.ProgramMessages.Add(("*", text));
    }

    /// <summary>A message's text with its data in &1 (the rest blank); null when there's no such message.</summary>
    internal string? MessageTemplateText(string id, string messageFile, string data)
    {
        if (MessageTemplate(id, messageFile.Replace("*LIBL/", "", StringComparison.OrdinalIgnoreCase)) is not { } text) return null;
        text = System.Text.RegularExpressions.Regex.Replace(text, @"&1\b", data.Trim());
        return System.Text.RegularExpressions.Regex.Replace(text, @"&\d+", "").TrimEnd();
    }

    /// <summary>A message's text as its message file has it, with its substitution variables.</summary>
    private string? MessageTemplate(string id, string messageFile)
    {
        var (library, name) = Qualified(messageFile);
        return Store.FindObject(library, name, "*MSGF") is { } file && Descriptions(file).TryGetValue(id, out var description) ? description.Text : null;
    }

    /// <summary>CRTMSGF, ADDMSGD, CHGMSGD, RMVMSGD and DLTMSGF.</summary>
    private CommandResult RunMessageFile(ClCommand command, IJobHost host)
    {
        CommandResult Fail(string message, string id) => new(false, message, MessageId: id);
        var target = command.Value("MSGF", command.Name is "CRTMSGF" or "DLTMSGF" ? 0 : 1);
        if (target is null) return Fail("Parameter MSGF required.", "CPD0072");
        var (library, name) = Qualified(target.ToUpperInvariant());
        if (command.Name == "CRTMSGF")
        {
            var lib = library ?? DefaultLibrary(host.User);
            if (!Store.HasLibrary(lib)) return Fail($"Library {lib} not found.", "CPF9810");
            if (Store.FindObject(lib, name, "*MSGF") is not null) return Fail($"Message file {name} exists in library {lib}.", "CPF2112");
            Store.SaveObject(lib, name, "*MSGF", "", "");
            return new CommandResult(true, $"Message file {name} created in library {lib}.");
        }
        if (Store.FindObject(library, name, "*MSGF", DefaultLibrary(host.User)) is not { } file)
            return Fail($"Message file {name} in library {library ?? "*LIBL"} not found.", "CPF2407");
        if (command.Name == "DLTMSGF")
        {
            Store.DeleteObject(file);
            return new CommandResult(true, $"Message file {name} deleted.");
        }
        var id = command.Value("MSGID", 0)?.Trim().ToUpperInvariant() ?? "";
        if (id.Length != 7) return Fail($"Message identifier {id} not valid.", "CPD2401");
        var descriptions = new Dictionary<string, MessageDescription>(Descriptions(file), StringComparer.OrdinalIgnoreCase);
        switch (command.Name)
        {
            case "ADDMSGD":
                if (descriptions.ContainsKey(id)) return Fail($"Message {id} already exists in message file {name}.", "CPF2412");
                descriptions[id] = new MessageDescription(command.Value("MSG", 2) is { } text ? Unquoted(text) : "",
                    command.Value("SECLVL", 3) is { } help && !help.StartsWith('*') ? Unquoted(help) : null);
                break;
            case "CHGMSGD":
                if (!descriptions.TryGetValue(id, out var old)) return Fail($"Message {id} not found in message file {name}.", "CPF2419");
                descriptions[id] = new MessageDescription(command.Value("MSG", 2) is { } changed && !changed.StartsWith('*') ? Unquoted(changed) : old.Text,
                    command.Value("SECLVL", 3) is { } changedHelp && !changedHelp.StartsWith('*') ? Unquoted(changedHelp) : old.Help);
                break;
            default:
                if (!descriptions.Remove(id)) return Fail($"Message {id} not found in message file {name}.", "CPF2419");
                break;
        }
        Store.SaveObject(file.Library, file.Name, "*MSGF", file.Attribute, Serialized(descriptions));
        return new CommandResult(true, $"Message {id} {command.Name switch { "ADDMSGD" => "added to", "CHGMSGD" => "changed in", _ => "removed from" }} message file {name}.");
    }

    /// <summary>
    /// A message file's source: the CL that makes it, run with &O/&N as its library and name. A
    /// command after + (from the next line's first non-blank) or - (from its start) goes on; /* comments */ are skipped.
    /// </summary>
    private CommandResult BuildMessageFile(Member member, string library, string name, string user)
    {
        var host = new BatchJob(user, CancellationToken.None);
        if (Store.FindObject(library, name, "*MSGF") is { } existing) Store.DeleteObject(existing);
        var source = member.Source.Replace("&O/&N", $"{library}/{name}", StringComparison.OrdinalIgnoreCase);
        var (ran, failures) = (0, new List<string>());
        foreach (var (text, line) in Commands(source))
        {
            var result = Run(text, host);
            if (result.Ok) ran++;
            else failures.Add($"line {line}: {result.Message}");
        }
        if (Store.FindObject(library, name, "*MSGF") is null) Store.SaveObject(library, name, "*MSGF", "", "");
        return new CommandResult(failures.Count == 0,
            failures.Count == 0 ? $"Message file {name} created in library {library}: {ran} command{(ran == 1 ? "" : "s")} run."
            : $"{failures.Count} of {ran + failures.Count} commands failed making message file {name}. First: {failures[0]}");
    }

    private static ClCommand? TryParse(string text)
    {
        try
        {
            return ClCommand.Parse(text);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The commands of a CL script, each with its first line: continuations joined, comments out.</summary>
    internal static IEnumerable<(string Text, int Line)> Commands(string source)
    {
        var command = new StringBuilder();
        var start = 0;
        var lines = source.Replace("\r\n", "\n").Split('\n');
        var inComment = false;
        for (var n = 0; n < lines.Length; n++)
        {
            var text = new StringBuilder();
            var quoted = false;
            var line = lines[n];
            for (var i = 0; i < line.Length; i++)
            {
                if (inComment)
                {
                    if (line[i] == '*' && i + 1 < line.Length && line[i + 1] == '/')
                    {
                        inComment = false;
                        i++;
                    }
                    continue;
                }
                if (!quoted && line[i] == '/' && i + 1 < line.Length && line[i + 1] == '*' && (i == 0 || !(char.IsLetterOrDigit(line[i - 1]) || line[i - 1] is '#' or '@' or '$' or '_')))
                {
                    inComment = true;
                    i++;
                    continue;
                }
                if (line[i] == '\'') quoted = !quoted;
                text.Append(line[i]);
            }
            var part = text.ToString().TrimEnd();
            if (command.Length == 0 && part.Trim().Length == 0) continue;
            if (command.Length == 0) start = n + 1;
            if (part.EndsWith('+') || part.EndsWith('-'))
            {
                command.Append(part[..^1]);
                // + goes on from the next line's first non-blank; - from its start.
                if (part.EndsWith('+') && n + 1 < lines.Length) lines[n + 1] = lines[n + 1].TrimStart();
                continue;
            }
            command.Append(part);
            yield return (command.ToString().Trim(), start);
            command.Clear();
        }
        if (command.ToString().Trim().Length > 0) yield return (command.ToString().Trim(), start);
    }
}
