// Fixed-form RPG: each specification turned into the free-form statements it means, so the rest of
// Sim i (parser, checker, interpreter) has one language. H becomes CTL-OPT, F DCL-F, D DCL-S,
// DCL-C, DCL-DS, DCL-PR and DCL-PI, P DCL-PROC. A C spec becomes its operation, its conditioning
// indicators an IF round it, and its resulting indicators assignments after it; operations free
// form doesn't have (MOVE, GOTO, CALL with PARM, TIME ...) become Sim i's own (@MOVE, @GOTO ...).
// I specs (record identifying indicators, control levels, field renames) and O specs (printed and
// exception output) are kept for the RPG cycle.

using System.Text;

namespace SimI.Rpg;

/// <summary>An I spec: a record format's identifying indicator, and its fields' new names, control levels and indicators.</summary>
internal sealed record InputRecord(string Format, string? Indicator, List<InputField> Fields);

internal sealed record InputField(string External, string Name, string? ControlLevel, string? Matching, string? Plus, string? Minus, string? Zero);

/// <summary>An O spec record: its file, type (H, D, T or E), conditions, EXCEPT name, spacing and skipping, ADD or DEL, and fields.</summary>
internal sealed record OutputRecord(int Line, string File, char Type, string? Condition, bool FetchOverflow, string? ExceptName,
    int SpaceBefore, int SpaceAfter, int SkipBefore, int SkipAfter, string? Operation, List<OutputField> Fields);

/// <summary>
/// An O spec field: a field (or a constant), its condition, edit code or edit word, whether it's
/// blanked after it's printed (B), and where it ends (an end position, or +n after the field before).
/// </summary>
internal sealed record OutputField(string? Condition, string? Name, string? Constant, char? EditCode, string? EditWord, bool BlankAfter,
    int End, bool Relative, char DataFormat);

internal sealed class FixedForm
{
    private readonly List<SourceStatement> _out = [];
    private readonly RpgProgram _program;
    private readonly Action<int, Severity, string> _report;

    // D specs: the DS, PR or PI whose subfields or parameters are being read.
    private string? _group;
    private int _groupLine;
    private string? _longName;

    // C specs.
    private sealed record Block(string Kind, bool Wrapped, int ForAt = -1);
    private readonly List<Block> _blocks = [];
    private (string Kind, List<List<string>> Groups, int Line, string? Wrap, bool Wrapped)? _pending;
    private List<List<string>>? _conditions;
    private (string Dividend, string Divisor, string Quotient)? _lastDivide;
    private bool _inTotals;
    private readonly Dictionary<string, List<string>> _keyLists = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<(string Target, string Factor1, string Factor2)>> _parameterLists = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _declared = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
    private int _hidden;

    private FixedForm(RpgProgram program, Action<int, Severity, string> report)
    {
        _program = program;
        _report = report;
    }

    /// <summary>The statements with every fixed-form specification turned into free-form ones.</summary>
    public static List<SourceStatement> Translate(List<SourceStatement> statements, RpgProgram program, Action<int, Severity, string> report)
    {
        if (statements.All(s => s.Kind != StatementKind.Fixed)) return statements;
        var form = new FixedForm(program, report);
        form.Prepare(statements);
        for (var i = 0; i < statements.Count; i++)
        {
            var statement = statements[i];
            if (statement.Kind != StatementKind.Fixed)
            {
                form.Leave(statement.Line, keepTotals: statement.Kind == StatementKind.Directive);
                if (statement.Kind != StatementKind.Code) form._out.Add(statement);
                else foreach (var text in form.FreeBlock(form.FreeKeyList(statement.Text))) form._out.Add(statement with { Text = text });
                continue;
            }
            i = form.Spec(statements, i);
        }
        form.Leave(statements[^1].Line, keepTotals: false);
        form.FlushPending();
        form.SpecialFields(statements);
        return form._out;
    }

    private static string Col(string line, int from, int to) =>
        line.Length < from ? "" : line.Substring(from - 1, Math.Min(to, line.Length) - from + 1);

    private void Emit(int line, string text) => _out.Add(new SourceStatement(StatementKind.Code, text, line, Translated: true));

    private void Problem(int line, string message, Severity severity = Severity.Error) => _report(line, severity, message);

    /// <summary>
    /// The fields fixed form has without defining them, when the program uses them: PAGE to PAGE7
    /// (4 digits, counted by the printer output) and UDATE, UDAY, UMONTH and UYEAR (the job's date, as *MDY).
    /// </summary>
    private void SpecialFields(List<SourceStatement> statements)
    {
        var words = new HashSet<string>(statements.Where(s => s.Kind is StatementKind.Fixed or StatementKind.Code)
            .SelectMany(s => System.Text.RegularExpressions.Regex.Matches(s.Kind == StatementKind.Fixed && s.Text.Length > 7 ? s.Text[7..Math.Min(s.Text.Length, 80)] : s.Text,
                @"(?<![\w@#$*%])(PAGE[1-7]?|UDATE|UDAY|UMONTH|UYEAR)(?![\w@#$])", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Select(m => m.Value.ToUpperInvariant())));
        var line = statements.Count > 0 ? statements[0].Line : 1;
        foreach (var page in words.Where(w => w.StartsWith("PAGE")).Order())
        {
            if (_declared.Add(page)) Insert(line, $"DCL-S {page} ZONED(4 : 0)");
        }
        (string Name, string Length, string Value)[] dates =
        [
            ("UDATE", "6", "%DEC(%CHAR(%DATE() : *MDY0) : 6 : 0)"), ("UDAY", "2", "%SUBDT(%DATE() : *DAYS)"),
            ("UMONTH", "2", "%SUBDT(%DATE() : *MONTHS)"), ("UYEAR", "2", "%REM(%SUBDT(%DATE() : *YEARS) : 100)"),
        ];
        foreach (var (name, length, value) in dates)
        {
            if (!words.Contains(name) || !_declared.Add(name)) continue;
            Insert(line, $"{name} = {value}");
            Insert(line, $"DCL-S {name} ZONED({length} : 0)");
        }
    }

    /// <summary>A first look at the C specs: their key lists, parameter lists, and the fields they define.</summary>
    private void Prepare(List<SourceStatement> statements)
    {
        string? keyList = null;
        string? parameterList = null;
        foreach (var statement in statements.Where(s => s.Kind == StatementKind.Fixed))
        {
            var line = statement.Text;
            var spec = char.ToUpperInvariant(line[5]);
            if (spec == 'D')
            {
                var name = Col(line, 7, 21).Trim();
                if (name.Length > 0 && !name.EndsWith("...")) _declared.Add(name);
                continue;
            }
            if (spec != 'C' || line.Length > 6 && line[6] == '*') continue;
            var (op, _) = Operation(Col(line, 26, 35));
            var factor1 = Col(line, 12, 25).Trim();
            var factor2 = Col(line, 36, 49).Trim();
            var result = Col(line, 50, 63).Trim();
            if (op == "KLIST")
            {
                keyList = factor1;
                _keyLists[keyList] = [];
                continue;
            }
            if (op == "KFLD" && keyList is not null)
            {
                _keyLists[keyList].Add(result);
                continue;
            }
            if (op == "PLIST")
            {
                parameterList = factor1;
                _parameterLists[parameterList] = [];
                continue;
            }
            if (op == "PARM" && parameterList is not null && keyList is null)
            {
                _parameterLists[parameterList].Add((result, factor1, factor2));
                continue;
            }
            if (op is not ("KFLD" or "PARM")) (keyList, parameterList) = (null, null);
        }
    }

    /// <summary>
    /// A free-form statement among fixed-form ones: the blocks it starts or ends count, so a fixed-form
    /// END ends the right one.
    /// </summary>
    private IEnumerable<string> FreeBlock(string text)
    {
        var word = text.TrimStart().Split([' ', '(', ';'], 2)[0].ToUpperInvariant();
        switch (word)
        {
            case "IF" or "SELECT" or "MONITOR" or "DOW" or "DOU" or "FOR" or "FOR-EACH":
                _blocks.Add(new Block(word is "DOW" or "DOU" ? "DO" : word == "FOR-EACH" ? "FOR" : word, false));
                break;
            case "ENDIF" or "ENDSL" or "ENDMON" or "ENDDO" or "ENDFOR":
                if (_blocks.Count == 0) break;
                var block = _blocks[^1];
                _blocks.RemoveAt(_blocks.Count - 1);
                // A fixed-form DO is a FOR here: its free-form ENDDO is ENDFOR, and adds 1.
                if (block.ForAt >= 0)
                {
                    _out[block.ForAt] = _out[block.ForAt] with { Text = _out[block.ForAt].Text.Replace("BY @@", "BY 1") };
                    return block.Wrapped ? ["ENDFOR", "ENDIF"] : ["ENDFOR"];
                }
                if (block.Wrapped) return [text, "ENDIF"];
                break;
        }
        return [text];
    }

    /// <summary>A free-form CHAIN, SETLL, SETGT, READE, READPE or DELETE can name a fixed-form key list: its fields, in parentheses.</summary>
    private string FreeKeyList(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, @"^\s*(CHAIN|SETLL|SETGT|READE|READPE|DELETE)(\([A-Z]*\))?\s+([A-Z_@#$][\w@#$]*)\s",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success || !_keyLists.TryGetValue(match.Groups[3].Value, out var fields) || fields.Count == 0) return text;
        return text[..match.Groups[3].Index] + $"({string.Join(" : ", fields)})" + text[(match.Groups[3].Index + match.Groups[3].Length)..];
    }

    /// <summary>Ends a D group, totals, or pending condition when something else comes.</summary>
    private void Leave(int line, bool keepTotals)
    {
        EndGroup(line);
        FlushPending();
        if (_inTotals && !keepTotals)
        {
            Emit(line, "@ENDTOTAL");
            _inTotals = false;
        }
    }

    private void EndGroup(int line)
    {
        if (_group is null) return;
        Emit(line, $"END-{_group}");
        _group = null;
    }

    /// <summary>One specification (and any it consumes after it). Returns the index of the last one used.</summary>
    private int Spec(List<SourceStatement> statements, int i)
    {
        var statement = statements[i];
        var line = statement.Text;
        var n = statement.Line;
        var spec = char.ToUpperInvariant(line[5]);
        if (line.Length > 6 && line[6] == '*' || Col(line, 7, 80).Trim().StartsWith("//")) return i;
        if (spec != 'D') EndGroup(n);
        if (spec != 'C')
        {
            FlushPending();
            if (_inTotals)
            {
                Emit(n, "@ENDTOTAL");
                _inTotals = false;
            }
        }
        switch (spec)
        {
            case 'H':
                if (Col(line, 7, 80).Trim() is { Length: > 0 } keywords) Emit(n, "CTL-OPT " + keywords);
                break;
            case 'F':
                FileSpec(n, line, statements, ref i);
                break;
            case 'D':
                Definition(n, line);
                break;
            case 'P':
            {
                var name = Col(line, 7, 21).Trim();
                var begin = char.ToUpperInvariant(Col(line, 24, 24).FirstOrDefault(' '));
                if (begin == 'B') Emit(n, $"DCL-PROC {name} {Col(line, 44, 80).Trim()}".TrimEnd());
                else if (begin == 'E') Emit(n, "END-PROC");
                else Problem(n, "a P spec has B or E in column 24");
                break;
            }
            case 'C':
                return Calculation(statements, i);
            case 'I':
                InputSpec(n, line);
                break;
            case 'O':
                return OutputSpec(statements, i);
            default:
                Problem(n, $"{spec} isn't a specification type");
                break;
        }
        return i;
    }

    // ---- F specs ----

    private void FileSpec(int n, string line, List<SourceStatement> statements, ref int i)
    {
        var name = Col(line, 7, 16).Trim();
        var type = char.ToUpperInvariant(Col(line, 17, 17).FirstOrDefault(' '));
        var designation = char.ToUpperInvariant(Col(line, 18, 18).FirstOrDefault(' '));
        var addition = char.ToUpperInvariant(Col(line, 20, 20).FirstOrDefault(' '));
        var format = char.ToUpperInvariant(Col(line, 22, 22).FirstOrDefault(' '));
        var recordLength = Col(line, 23, 27).Trim();
        var keyed = char.ToUpperInvariant(Col(line, 34, 34).FirstOrDefault(' ')) == 'K';
        var device = Col(line, 36, 42).Trim().ToUpperInvariant();
        var keywords = new StringBuilder(Col(line, 44, 80).Trim());
        // Keyword continuation lines: an F spec with only keywords.
        while (i + 1 < statements.Count && statements[i + 1].Kind == StatementKind.Fixed && char.ToUpperInvariant(statements[i + 1].Text[5]) == 'F'
               && Col(statements[i + 1].Text, 7, 43).Trim().Length == 0)
        {
            keywords.Append(' ').Append(Col(statements[++i].Text, 44, 80).Trim());
        }
        if (name.Length == 0)
        {
            Problem(n, "an F spec needs a file name");
            return;
        }
        _files.Add(name);
        var free = new StringBuilder($"DCL-F {name}");
        switch (device)
        {
            case "WORKSTN":
                free.Append(" WORKSTN");
                break;
            case "PRINTER":
                free.Append(format == 'F' ? $" PRINTER({(recordLength.Length > 0 ? recordLength : "132")})" : " PRINTER");
                break;
            case "DISK":
                if (format == 'F')
                {
                    free.Append($" DISK({(recordLength.Length > 0 ? recordLength : "0")})");
                    break;
                }
                free.Append(" DISK");
                var usage = type switch
                {
                    'I' => "*INPUT",
                    'U' => "*INPUT : *UPDATE : *DELETE",
                    'O' => "*OUTPUT",
                    'C' => "*INPUT : *OUTPUT",
                    _ => "*INPUT",
                };
                if (addition == 'A' && type is 'I' or 'U') usage += " : *OUTPUT";
                free.Append($" USAGE({usage})");
                if (keyed) free.Append(" KEYED");
                break;
            case "SEQ" or "SPECIAL":
                free.Append(' ').Append(device);
                break;
            default:
                Problem(n, $"{device} isn't a device: DISK, PRINTER, WORKSTN, SEQ or SPECIAL");
                return;
        }
        if (designation is 'P' or 'S') free.Append(designation == 'P' ? " @PRIMARY" : " @SECONDARY");
        if (keywords.Length > 0) free.Append(' ').Append(keywords);
        Emit(n, free.ToString());
    }

    // ---- D specs ----

    private bool _psds;

    private static readonly Dictionary<string, (int From, int To, char Type)> PsdsKeywords = new()
    {
        ["*PROC"] = (1, 10, 'A'), ["*STATUS"] = (11, 15, 'S'), ["*ROUTINE"] = (29, 36, 'A'), ["*PARMS"] = (37, 39, 'S'),
    };

    private static readonly Dictionary<string, (int From, int To, char Type)> InfdsKeywords = new()
    {
        ["*FILE"] = (1, 8, 'A'), ["*STATUS"] = (11, 15, 'S'), ["*OPCODE"] = (16, 21, 'A'), ["*ROUTINE"] = (22, 29, 'A'),
        ["*RECORD"] = (38, 45, 'A'), ["*SIZE"] = (67, 70, 'S'), ["*INP"] = (71, 72, 'S'), ["*OUT"] = (73, 74, 'S'), ["*MODE"] = (75, 76, 'S'),
    };

    private void Definition(int n, string line)
    {
        var name = Col(line, 7, 21).Trim();
        // A long name: NAME... on its own line, the rest of the definition on the next.
        if (name.EndsWith("..."))
        {
            _longName = (_longName ?? "") + name[..^3];
            return;
        }
        if (_longName is not null)
        {
            name = _longName + name;
            _longName = null;
        }
        var external = char.ToUpperInvariant(Col(line, 22, 22).FirstOrDefault(' ')) == 'E';
        var special = char.ToUpperInvariant(Col(line, 23, 23).FirstOrDefault(' '));
        var kind = Col(line, 24, 25).Trim().ToUpperInvariant();
        var from = Col(line, 26, 32).Trim();
        var to = Col(line, 33, 39).Trim();
        var dataType = char.ToUpperInvariant(Col(line, 40, 40).FirstOrDefault(' '));
        var decimals = Col(line, 41, 42).Trim();
        var keywords = Col(line, 44, 80).Trim();

        // A program-status or file-information subfield named by its keyword (*PROC, *STATUS ...): its positions.
        if (from.StartsWith('*') && to.Length == 0 && _group == "DS" && (_psds ? PsdsKeywords : InfdsKeywords).TryGetValue(from.ToUpperInvariant(), out var at))
            (from, to, dataType, decimals) = (at.From.ToString(), at.To.ToString(), at.Type, at.Type == 'S' ? "0" : "");

        // A line of keywords only: more of the definition before it.
        if (name.Length == 0 && kind.Length == 0 && from.Length == 0 && to.Length == 0 && dataType == ' ' && keywords.Length > 0 && !external && _out.Count > 0)
        {
            var last = _out[^1];
            _out[^1] = last with { Text = last.Text + " " + keywords };
            return;
        }
        if (kind.Length > 0 || _group is null) EndGroup(n);
        var type = TypeOf(n, from, to, dataType, decimals, keywords, inStructure: _group == "DS");
        switch (kind)
        {
            case "S":
                Emit(n, $"DCL-S {name} {type} {keywords}".TrimEnd());
                break;
            case "C":
                Emit(n, $"DCL-C {name} {(keywords.StartsWith("CONST", StringComparison.OrdinalIgnoreCase) ? keywords : keywords)}".TrimEnd());
                break;
            case "DS":
            {
                var words = new StringBuilder($"DCL-DS {(name.Length == 0 ? "*N" : name)}");
                if (external && !keywords.Contains("EXTNAME", StringComparison.OrdinalIgnoreCase)) words.Append($" EXTNAME({name})");
                if (special == 'S') words.Append(" PSDS");
                _psds = special == 'S';
                if (special == 'U') words.Append(name.Length == 0 ? " DTAARA(*LDA)" : " DTAARA(*AUTO)");
                if (to.Length > 0 && from.Length == 0) words.Append($" LEN({to})");
                if (keywords.Length > 0) words.Append(' ').Append(keywords);
                Emit(n, words.ToString());
                // A DS like another (LIKEDS, LIKEREC) has no subfields of its own, and no END-DS.
                if (System.Text.RegularExpressions.Regex.IsMatch(keywords, @"\bLIKE(DS|REC)\s*\(", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) break;
                (_group, _groupLine) = ("DS", n);
                break;
            }
            case "PR" or "PI":
                Emit(n, $"DCL-{kind} {(name.Length == 0 ? "*N" : name)} {type} {keywords}".TrimEnd());
                (_group, _groupLine) = (kind, n);
                break;
            case "":
                if (_group is null)
                {
                    Problem(n, $"{(name.Length == 0 ? "a subfield" : name)} has no definition type (S, C, DS, PR or PI) and isn't in a data structure or prototype");
                    return;
                }
                var position = from.Length > 0 && _group == "DS" ? $" POS({from})" : "";
                // E: one of the structure's external fields, renamed (EXTFLD) or with an INZ of its own.
                if (external && _group == "DS" && !keywords.Contains("EXTFLD", StringComparison.OrdinalIgnoreCase)) keywords = ("EXTFLD " + keywords).TrimEnd();
                Emit(n, $"{(name.Length == 0 ? "*N" : name)} {type}{position} {keywords}".TrimEnd());
                break;
            default:
                Problem(n, $"{kind} isn't a definition type: S, C, DS, PR or PI");
                break;
        }
    }

    /// <summary>The free-form type of a definition from its positions or length, data type and decimals.</summary>
    private string TypeOf(int n, string from, string to, char dataType, string decimals, string keywords, bool inStructure)
    {
        if (to.Length == 0 && from.Length == 0)
        {
            return dataType switch
            {
                'D' => "DATE",
                'T' => "TIME",
                'Z' => "TIMESTAMP",
                'N' => "IND",
                '*' => "POINTER",
                _ => "",
            };
        }
        // +n or -n: LIKE's length adjustment, in the keywords' LIKE.
        if (to.StartsWith('+') || to.StartsWith('-')) return "";
        int length;
        if (from.Length > 0)
        {
            if (!int.TryParse(from, out var start) || !int.TryParse(to, out var end) || end < start)
            {
                Problem(n, "the from and to positions aren't numbers, with to at or after from");
                return "CHAR(1)";
            }
            var bytes = end - start + 1;
            length = dataType switch
            {
                'P' => bytes * 2 - 1,
                'B' => bytes == 2 ? 4 : 9,
                'I' or 'U' => bytes switch { 1 => 3, 2 => 5, 4 => 10, _ => 20 },
                _ => bytes,
            };
        }
        else if (!int.TryParse(to, out length))
        {
            Problem(n, $"the length {to} isn't a number");
            return "CHAR(1)";
        }
        var d = decimals.Length > 0 ? decimals : "0";
        var varying = keywords.Contains("VARYING", StringComparison.OrdinalIgnoreCase);
        return dataType switch
        {
            'A' => varying ? $"VARCHAR({length})" : $"CHAR({length})",
            'P' => $"PACKED({length} : {d})",
            'S' => $"ZONED({length} : {d})",
            'B' => $"BINDEC({length} : {d})",
            'I' => $"INT({length})",
            'U' => $"UNS({length})",
            'F' => $"FLOAT({length})",
            'D' => "DATE",
            'T' => "TIME",
            'Z' => "TIMESTAMP",
            'N' => "IND",
            '*' => "POINTER",
            'G' => varying ? $"VARGRAPH({length})" : $"GRAPH({length})",
            'C' => varying ? $"VARUCS2({length})" : $"UCS2({length})",
            ' ' when decimals.Length == 0 => $"CHAR({length})",
            ' ' => inStructure || from.Length > 0 ? $"ZONED({length} : {d})" : $"PACKED({length} : {d})",
            _ => $"CHAR({length})",
        };
    }

    // ---- C specs ----

    private static (string Op, string Extender) Operation(string text)
    {
        var t = text.Trim().ToUpperInvariant();
        var open = t.IndexOf('(');
        return open < 0 ? (t, "") : (t[..open].Trim(), t[(open + 1)..].TrimEnd(')').Trim());
    }

    private static readonly HashSet<string> Extended =
    [
        "EVAL", "EVALR", "EVAL-CORR", "IF", "ELSEIF", "DOW", "DOU", "WHEN", "FOR", "FOR-EACH", "RETURN", "CALLP", "ON-ERROR",
        "ON-EXCP", "XML-INTO", "XML-SAX", "DATA-INTO", "DATA-GEN", "SND-MSG", "SORTA", "DEALLOC", "WHEN-IS", "WHEN-IN", "SELECT",
    ];

    /// <summary>An indicator as a free-form value: 01 is *IN01, N01 is NOT *IN01.</summary>
    private static string Indicator(string text)
    {
        var t = text.Trim().ToUpperInvariant();
        var not = t.StartsWith('N');
        var name = not ? t[1..] : t;
        return $"{(not ? "NOT " : "")}*IN{name}";
    }

    /// <summary>An operand as free form says it: a key list's name becomes its fields.</summary>
    private string Operand(string text) => _keyLists.TryGetValue(text, out var fields) ? $"({string.Join(" : ", fields)})" : text;

    /// <summary>Splits name:value at its colon, outside quotes and parentheses.</summary>
    private static (string Left, string? Right) Pair(string text)
    {
        var depth = 0;
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\'') quoted = !quoted;
            else if (!quoted && c == '(') depth++;
            else if (!quoted && c == ')') depth--;
            else if (!quoted && depth == 0 && c == ':') return (text[..i].Trim(), text[(i + 1)..].Trim());
        }
        return (text.Trim(), null);
    }

    private int Calculation(List<SourceStatement> statements, int i)
    {
        var statement = statements[i];
        var line = statement.Text;
        var n = statement.Line;
        var level = Col(line, 7, 8).Trim().ToUpperInvariant();
        var condition = Col(line, 9, 11).Trim();
        var (op, extender) = Operation(Col(line, 26, 35));
        var factor1 = Col(line, 12, 25).Trim();
        var factor2 = Col(line, 36, 49).Trim();
        var extended = Col(line, 36, 80).Trim();
        if (ExtendedFactor2.Contains(op)) extended = Continued(statements, ref i, Col(line, 36, 80));
        var result = Col(line, 50, 63).Trim();
        var length = Col(line, 64, 68).Trim();
        var decimals = Col(line, 69, 70).Trim();
        var (hi, lo, eq) = (Col(line, 71, 72).Trim(), Col(line, 73, 74).Trim(), Col(line, 75, 76).Trim());
        // Extended factor 2 runs to column 80: there are no resulting indicators.
        if (ExtendedFactor2.Contains(op)) (hi, lo, eq) = ("", "", "");

        // A line with no operation: conditioning indicators for the line that has one.
        if (op.Length == 0)
        {
            if (condition.Length == 0 && level is not ("AN" or "OR")) return i;
            AddCondition(level, condition);
            return i;
        }
        // AND and OR lines of IFxx, DOWxx, DOUxx and WHENxx.
        if (op.Length == 5 && op[..3] is "AND" && _pending is not null || op.Length == 4 && op[..2] == "OR" && _pending is not null)
        {
            var relation = Relation(op[^2..]);
            if (relation is null) Problem(n, $"{op} isn't an operation code");
            var comparison = $"{factor1} {relation} {factor2}";
            if (op.StartsWith("AND")) _pending!.Value.Groups[^1].Add(comparison);
            else _pending!.Value.Groups.Add([comparison]);
            return i;
        }
        FlushPending();

        // Total-time calculations: L0 to L9 and LR in columns 7 and 8.
        var total = level is ['L', >= '0' and <= '9'] or "LR";
        if (total && !_inTotals)
        {
            Emit(n, "@TOTAL");
            _inTotals = true;
        }
        if (_inTotals && !total && level != "SR" && level is not ("AN" or "OR") && op != "BEGSR")
        {
            Problem(n, "detail calculations can't follow total calculations", Severity.Error);
        }
        if (_inTotals && op == "BEGSR")
        {
            Emit(n, "@ENDTOTAL");
            _inTotals = false;
        }

        // Extended factor 2 takes columns 36 to 80: only the other operations have a result field to define.
        if (!ExtendedFactor2.Contains(op)) DefineResult(line, n);

        // Conditioning indicators: on this line, and AN/OR lines before it; L1 to L9 at total time.
        if (condition.Length > 0) AddCondition(level is "AN" or "OR" ? level : "", condition);
        var conditions = Condition();
        if (total && level is not ("L0" or "LR" or "AN" or "OR")) conditions = conditions is null ? $"*IN{level}" : $"*IN{level} AND ({conditions})";
        else if (total && level == "LR") conditions = conditions is null ? "*INLR" : $"*INLR AND ({conditions})";
        _conditions = null;

        var startsBlock = op is "IF" or "DOW" or "DOU" or "FOR" or "SELECT" or "MONITOR" or "DO" or "FOR-EACH"
            || op.Length == 4 && op[..2] is "IF" || op.Length == 5 && op[..3] is "DOW" or "DOU" || op.Length == 5 && op[..3] == "CAS" && Top() != "CAS";
        if (conditions is not null && !IsEnd(op) && op is not ("ELSE" or "ELSEIF" or "WHEN" or "OTHER" or "ON-ERROR" or "BEGSR" or "ENDSR") && !op.StartsWith("WHEN"))
        {
            Emit(n, $"IF {conditions}");
            if (!startsBlock)
            {
                Operation(statements, ref i, n, op, extender, factor1, factor2, extended, result, hi, lo, eq, wrapped: false);
                Emit(n, "ENDIF");
                return i;
            }
            Operation(statements, ref i, n, op, extender, factor1, factor2, extended, result, hi, lo, eq, wrapped: true);
            return i;
        }
        Operation(statements, ref i, n, op, extender, factor1, factor2, extended, result, hi, lo, eq, wrapped: false);
        return i;
    }

    private static bool IsEnd(string op) => op is "END" or "ENDIF" or "ENDDO" or "ENDSL" or "ENDFOR" or "ENDCS" or "ENDMON";

    /// <summary>
    /// What %EOF, %FOUND and %EQUAL take for an operation on this name: the file, or (for a record
    /// format, which they don't take) nothing, so they answer for the operation just done.
    /// </summary>
    private string FileArgument(string name) => _files.Contains(name) ? $"({name})" : "";

    private string? Top() => _blocks.Count == 0 ? null : _blocks[^1].Kind;

    private static readonly HashSet<string> ExtendedFactor2 =
    [
        "EVAL", "EVALR", "EVAL-CORR", "CALLP", "RETURN", "SORTA", "DEALLOC", "SND-MSG", "IF", "ELSEIF", "DOW", "DOU", "FOR", "FOR-EACH",
        "WHEN", "WHEN-IS", "WHEN-IN", "ON-ERROR", "ON-EXCP", "XML-INTO", "XML-SAX", "DATA-INTO", "DATA-GEN",
    ];

    /// <summary>
    /// Extended factor 2 with the C spec lines that go on with it: lines with nothing in columns 7 to
    /// 35 and more in 36 to 80. A literal goes on after + (from the next line's first non-blank) or - (from column 36).
    /// </summary>
    private static string Continued(List<SourceStatement> statements, ref int i, string first)
    {
        var text = new StringBuilder(first.TrimEnd());
        while (i + 1 < statements.Count && statements[i + 1] is { Kind: StatementKind.Fixed } next && char.ToUpperInvariant(next.Text[5]) == 'C'
               && !(next.Text.Length > 6 && next.Text[6] == '*') && Col(next.Text, 7, 35).Trim().Length == 0 && Col(next.Text, 36, 80).Trim().Length > 0)
        {
            var more = Col(next.Text, 36, 80).TrimEnd();
            var soFar = text.ToString();
            if (soFar.Count(c => c == '\'') % 2 == 1 && soFar.EndsWith('+')) text.Length--;
            else if (soFar.Count(c => c == '\'') % 2 == 1 && soFar.EndsWith('-'))
            {
                text.Length--;
                text.Append(more);
                i++;
                continue;
            }
            else text.Append(' ');
            text.Append(more.TrimStart());
            i++;
        }
        return text.ToString().Trim();
    }

    /// <summary>The field a C spec's result defines, with its length (columns 64 to 68) and decimals (69 and 70).</summary>
    private void DefineResult(string line, int n)
    {
        var result = Col(line, 50, 63).Trim();
        var length = Col(line, 64, 68).Trim();
        var decimals = Col(line, 69, 70).Trim();
        if (length.Length == 0 || result.Length == 0 || _declared.Contains(Pair(result).Left)) return;
        var field = Pair(result).Left;
        _declared.Add(field);
        Insert(n, decimals.Length > 0 ? $"DCL-S {field} PACKED({length} : {decimals})" : $"DCL-S {field} CHAR({length})");
    }

    /// <summary>A field definition goes before the first statement, so it's there for every one.</summary>
    private void Insert(int line, string text)
    {
        var at = DefinitionPoint();
        var statement = new SourceStatement(StatementKind.Code, text, line, Translated: true) { Calculated = true };
        if (at < 0) _out.Add(statement);
        else _out.Insert(at, statement);
        // A DO's patch point moves with what's inserted before it.
        for (var k = 0; k < _blocks.Count; k++)
        {
            if (_blocks[k].ForAt >= at && at >= 0) _blocks[k] = _blocks[k] with { ForAt = _blocks[k].ForAt + 1 };
        }
    }

    /// <summary>
    /// Where a definition can go: before the first calculation or procedure, and never inside a data
    /// structure, prototype or interface (whose subfields and parameters don't start with DCL-).
    /// </summary>
    private int DefinitionPoint()
    {
        var depth = 0;
        for (var k = 0; k < _out.Count; k++)
        {
            if (_out[k].Kind != StatementKind.Code) continue;
            var text = _out[k].Text.TrimStart();
            bool Starts(string word) => text.StartsWith(word, StringComparison.OrdinalIgnoreCase);
            if (Starts("END-DS") || Starts("END-PR") || Starts("END-PI"))
            {
                if (depth > 0) depth--;
                continue;
            }
            if (Starts("DCL-DS") || Starts("DCL-PR ") || Starts("DCL-PI"))
            {
                // A DS like another has no END-DS; a prototype's LIKEDS is its return value's, and it still has END-PR.
                var whole = Starts("DCL-DS") && (text.Contains("LIKEDS(", StringComparison.OrdinalIgnoreCase) || text.Contains("LIKEREC(", StringComparison.OrdinalIgnoreCase))
                    || text.Contains("END-", StringComparison.OrdinalIgnoreCase);
                if (!whole) depth++;
                continue;
            }
            if (depth > 0) continue;
            if (Starts("DCL-PROC")) return k;
            if (Starts("DCL-") || Starts("CTL-OPT") || Starts("END-")) continue;
            return k;
        }
        return -1;
    }

    private void AddCondition(string level, string indicator)
    {
        _conditions ??= [[]];
        if (level == "OR") _conditions.Add([]);
        _conditions[^1].Add(Indicator(indicator));
    }

    private string? Condition()
    {
        if (_conditions is null || _conditions.All(g => g.Count == 0)) return null;
        var groups = _conditions.Where(g => g.Count > 0).Select(g => string.Join(" AND ", g)).ToList();
        return groups.Count == 1 ? groups[0] : string.Join(" OR ", groups.Select(g => $"({g})"));
    }

    private static string? Relation(string code) => code switch
    {
        "EQ" => "=",
        "NE" => "<>",
        "LT" => "<",
        "LE" => "<=",
        "GT" => ">",
        "GE" => ">=",
        _ => null,
    };

    /// <summary>An IFxx, DOWxx, DOUxx or WHENxx whose ANDxx and ORxx lines have all come.</summary>
    private void FlushPending()
    {
        if (_pending is not { } pending) return;
        _pending = null;
        var groups = pending.Groups.Select(g => string.Join(" AND ", g.Select(c => $"({c})"))).ToList();
        var condition = groups.Count == 1 ? groups[0] : string.Join(" OR ", groups.Select(g => $"({g})"));
        Emit(pending.Line, $"{pending.Kind} {condition}");
        if (pending.Kind != "WHEN") _blocks.Add(new Block(pending.Kind == "IF" ? "IF" : "DO", pending.Wrapped));
    }

    /// <summary>Sets the resulting indicators from conditions on the result: HI, LO and EQ.</summary>
    private void Resulting(int n, string hi, string lo, string eq, string? high, string? low, string? equal)
    {
        if (hi.Length > 0 && high is not null) Emit(n, $"*IN{hi.ToUpperInvariant()} = {high}");
        if (lo.Length > 0 && low is not null) Emit(n, $"*IN{lo.ToUpperInvariant()} = {low}");
        if (eq.Length > 0 && equal is not null) Emit(n, $"*IN{eq.ToUpperInvariant()} = {equal}");
    }

    private void Arithmetic(int n, string target, string expression, string extender, string hi, string lo, string eq)
    {
        Emit(n, $"@ZADD{(extender.Contains('H') ? "(H)" : "")} {target} = {expression}");
        Resulting(n, hi, lo, eq, $"{target} > 0", $"{target} < 0", $"{target} = 0");
    }

    private void Operation(List<SourceStatement> statements, ref int i, int n, string op, string extender, string factor1, string factor2, string extended,
        string result, string hi, string lo, string eq, bool wrapped)
    {
        var ext = extender.Length > 0 ? $"({extender})" : "";
        // An E extender (for %ERROR) where a LO indicator wants errors.
        var errorExt = lo.Length > 0 && !extender.Contains('E') ? $"({extender}E)" : ext;
        switch (op)
        {
            // ---- free form's own operations, with extended factor 2 ----
            case "EVAL" or "EVALR" or "EVAL-CORR" or "CALLP" or "RETURN" or "SORTA" or "DEALLOC" or "SND-MSG" or "XML-INTO" or "XML-SAX" or "DATA-INTO" or "DATA-GEN":
                Emit(n, $"{op}{ext} {extended}".TrimEnd());
                if (op == "CALLP" && lo.Length > 0) Resulting(n, "", lo, "", null, "%ERROR", null);
                return;
            case "IF" or "DOW" or "DOU" or "FOR" or "FOR-EACH" or "SELECT":
                Emit(n, $"{op}{ext} {extended}".TrimEnd());
                _blocks.Add(new Block(op is "IF" ? "IF" : op is "SELECT" ? "SELECT" : op is "FOR" or "FOR-EACH" ? "FOR" : "DO", wrapped));
                return;
            case "ELSEIF" or "WHEN" or "WHEN-IS" or "WHEN-IN" or "ON-ERROR" or "ON-EXCP":
                Emit(n, $"{op} {extended}".TrimEnd());
                return;
            case "ELSE" or "OTHER":
                Emit(n, op);
                return;
            case "MONITOR":
                Emit(n, "MONITOR");
                _blocks.Add(new Block("MONITOR", wrapped));
                return;
            case "END" or "ENDIF" or "ENDDO" or "ENDSL" or "ENDFOR" or "ENDCS" or "ENDMON":
            {
                if (_blocks.Count == 0)
                {
                    Problem(n, $"{op} has no block to end");
                    return;
                }
                var block = _blocks[^1];
                _blocks.RemoveAt(_blocks.Count - 1);
                if (block.ForAt >= 0)
                {
                    // DO's increment is ENDDO's factor 2.
                    var by = factor2.Length > 0 ? factor2 : "1";
                    _out[block.ForAt] = _out[block.ForAt] with { Text = _out[block.ForAt].Text.Replace("BY @@", $"BY {by}") };
                }
                Emit(n, block.Kind switch { "IF" => "ENDIF", "SELECT" or "CAS" => "ENDSL", "FOR" => "ENDFOR", "MONITOR" => "ENDMON", _ => "ENDDO" });
                if (block.Wrapped) Emit(n, "ENDIF");
                return;
            }
            case "ITER" or "LEAVE" or "LEAVESR" or "DUMP":
                Emit(n, op);
                return;
            case "BEGSR":
                Emit(n, $"BEGSR {factor1}");
                return;
            case "ENDSR":
                // A label in factor 1 is a place GOTO and CABxx can go to: the end of the subroutine.
                if (factor1.Length > 0) Emit(n, $"@TAG {factor1}");
                Emit(n, "ENDSR");
                return;
            case "EXSR":
                Emit(n, $"EXSR {factor2}");
                return;
            case "DSPLY":
                Emit(n, $"DSPLY{ext} {factor1} {factor2} {result}".TrimEnd());
                return;
            case "CLEAR" or "RESET":
                Emit(n, $"{op} {(factor1.StartsWith('*') ? factor1 + " " : "")}{(factor2.Length > 0 ? factor2 : result)}");
                return;

            // ---- arithmetic ----
            case "Z-ADD":
                Arithmetic(n, result, factor2, extender, hi, lo, eq);
                return;
            case "Z-SUB":
                Arithmetic(n, result, $"-({factor2})", extender, hi, lo, eq);
                return;
            case "ADD" or "SUB" or "MULT":
            {
                var symbol = op switch { "ADD" => "+", "SUB" => "-", _ => "*" };
                Arithmetic(n, result, $"({(factor1.Length > 0 ? factor1 : result)}) {symbol} ({factor2})", extender, hi, lo, eq);
                return;
            }
            case "DIV":
            {
                var dividend = factor1.Length > 0 ? factor1 : result;
                // A remainder (MVR) comes from what was divided before the quotient took its place:
                // kept in the widest number Sim i runs (28 digits), with 19 of them before the point.
                var kept = $"@DIV{++_hidden}";
                Insert(n, $"DCL-S {kept} PACKED(28 : 9)");
                Emit(n, $"{kept} = {dividend}");
                Arithmetic(n, result, $"({kept}) / ({factor2})", extender, hi, lo, eq);
                _lastDivide = (kept, factor2, result);
                return;
            }
            case "MVR":
                if (_lastDivide is not var (divided, divisor, quotient)) Problem(n, "MVR has to follow a DIV");
                else Arithmetic(n, result, $"({divided}) - ({quotient}) * ({divisor})", "", hi, lo, eq);
                return;
            case "SQRT":
                Arithmetic(n, result, $"%SQRT({factor2})", extender, hi, lo, eq);
                return;
            case "XFOOT":
                Arithmetic(n, result, $"%XFOOT({factor2})", extender, hi, lo, eq);
                return;

            // ---- moving ----
            case "MOVE" or "MOVEL":
                Emit(n, $"@{op}{ext} {result} = {factor2}{(factor1.Length > 0 ? $" : {factor1}" : "")}");
                Resulting(n, hi, lo, eq, $"@SIGN({result}) > 0", $"@SIGN({result}) < 0", $"@SIGN({result}) = 0");
                return;
            case "MOVEA":
                Emit(n, $"@MOVEA{ext} {result} = {factor2}");
                return;
            case "COMP":
                Resulting(n, hi, lo, eq, $"{factor1} > {factor2}", $"{factor1} < {factor2}", $"{factor1} = {factor2}");
                return;
            case "SETON" or "SETOFF":
                foreach (var indicator in new[] { hi, lo, eq }.Where(x => x.Length > 0)) Emit(n, $"*IN{indicator.ToUpperInvariant()} = {(op == "SETON" ? "*ON" : "*OFF")}");
                return;

            // ---- characters ----
            case "CAT":
            {
                var (right, blanks) = Pair(factor2);
                var left = factor1.Length > 0 ? factor1 : result;
                var joined = blanks is null ? $"{left} + {right}" : $"%TRIMR({left}) + '{new string(' ', int.TryParse(blanks, out var b) ? b : 0)}' + {right}";
                if (extender.Contains('P')) Emit(n, $"{result} = {joined}");
                else Emit(n, $"%SUBST({result} : 1 : %MIN(%LEN({joined}) : %LEN({result}))) = {joined}");
                return;
            }
            case "SUBST":
            {
                var (source, start) = Pair(factor2);
                var piece = factor1.Length > 0 ? $"%SUBST({source} : {start ?? "1"} : {factor1})" : $"%SUBST({source} : {start ?? "1"})";
                if (extender.Contains('P')) Emit(n, $"{result} = {piece}");
                else Emit(n, $"%SUBST({result} : 1 : %MIN(%LEN({piece}) : %LEN({result}))) = {piece}");
                if (lo.Length > 0) Resulting(n, "", lo, "", null, "%ERROR", null);
                return;
            }
            case "SCAN":
            {
                var (search, searchLength) = Pair(factor1);
                var (source, start) = Pair(factor2);
                var argument = searchLength is null ? search : $"%SUBST({search} : 1 : {searchLength})";
                var scan = $"%SCAN({argument} : {source}{(start is null ? "" : $" : {start}")})";
                if (result.Length > 0) Emit(n, $"{result} = {scan}");
                Resulting(n, "", lo, eq, null, lo.Length > 0 ? "%ERROR" : null, result.Length > 0 ? $"{result} > 0" : $"{scan} > 0");
                return;
            }
            case "CHECK" or "CHECKR":
            {
                var (source, start) = Pair(factor2);
                var check = $"%{op}({factor1} : {source}{(start is null ? "" : $" : {start}")})";
                if (result.Length > 0) Emit(n, $"{result} = {check}");
                Resulting(n, "", lo, eq, null, lo.Length > 0 ? "%ERROR" : null, result.Length > 0 ? $"{result} > 0" : $"{check} > 0");
                return;
            }
            case "XLATE":
            {
                var (from, to) = Pair(factor1);
                var (source, start) = Pair(factor2);
                var translated = $"%XLATE({from} : {to ?? "' '"} : {source}{(start is null ? "" : $" : {start}")})";
                Emit(n, extender.Contains('P') ? $"{result} = {translated}" : $"%SUBST({result} : 1 : %MIN(%LEN({translated}) : %LEN({result}))) = {translated}");
                return;
            }
            case "LOOKUP":
            {
                // An array, or array(index) to start at and take the index found; a table (TAB...) and its alternate table.
                var (array, start) = factor2.EndsWith(')') && factor2.IndexOf('(') is var open and > 0 ? (factor2[..open], factor2[(open + 1)..^1]) : (factor2, (string?)null);
                var which = (hi.Length > 0, lo.Length > 0, eq.Length > 0) switch
                {
                    (true, false, true) => "%LOOKUPGE",
                    (true, false, false) => "%LOOKUPGT",
                    (false, true, true) => "%LOOKUPLE",
                    (false, true, false) => "%LOOKUPLT",
                    _ => "%LOOKUP",
                };
                var indicator = new[] { hi, lo, eq }.First(x => x.Length > 0);
                if (array.StartsWith("TAB", StringComparison.OrdinalIgnoreCase))
                {
                    Emit(n, $"*IN{indicator.ToUpperInvariant()} = %TLOOKUP{which[7..]}({factor1} : {array}{(result.Length > 0 ? $" : {result}" : "")})");
                    return;
                }
                if (start is not null && !char.IsDigit(start[0]))
                {
                    Emit(n, $"{start} = {which}({factor1} : {array} : {start})");
                    Emit(n, $"*IN{indicator.ToUpperInvariant()} = {start} > 0");
                    Emit(n, $"IF {start} = 0");
                    Emit(n, $"{start} = 1");
                    Emit(n, "ENDIF");
                }
                else Emit(n, $"*IN{indicator.ToUpperInvariant()} = {which}({factor1} : {array}{(start is null ? "" : $" : {start}")}) > 0");
                return;
            }

            // ---- jumps and structure ----
            case "GOTO":
                Emit(n, $"@GOTO {factor2}");
                return;
            case "TAG":
                Emit(n, $"@TAG {factor1}");
                return;
            case "DO":
            {
                var index = result.Length > 0 ? result : $"@DO{++_hidden}";
                if (result.Length == 0) Insert(n, $"DCL-S {index} INT(10)");
                Emit(n, $"FOR {index} = {(factor1.Length > 0 ? factor1 : "1")} TO {(factor2.Length > 0 ? factor2 : "1")} BY @@");
                _blocks.Add(new Block("FOR", wrapped, _out.Count - 1));
                return;
            }
            case "CAS":
                Emit(n, "OTHER");
                Emit(n, $"EXSR {result}");
                return;
            case "CALL":
            {
                // The PARM lines after it, or a PLIST named in its result field.
                var parameters = new List<(string Target, string Factor1, string Factor2)>();
                if (result.Length > 0 && _parameterLists.TryGetValue(result, out var listed)) parameters.AddRange(listed);
                while (i + 1 < statements.Count && statements[i + 1].Kind == StatementKind.Fixed && char.ToUpperInvariant(statements[i + 1].Text[5]) == 'C'
                       && Operation(Col(statements[i + 1].Text, 26, 35)).Op == "PARM")
                {
                    var parm = statements[++i].Text;
                    DefineResult(parm, statements[i].Line);
                    parameters.Add((Col(parm, 50, 63).Trim(), Col(parm, 12, 25).Trim(), Col(parm, 36, 49).Trim()));
                }
                var groups = parameters.Select(p => $"({p.Target} : {(p.Factor2.Length > 0 ? p.Factor2 : "*OMIT")} : {(p.Factor1.Length > 0 ? p.Factor1 : "*OMIT")})");
                Emit(n, $"@CALL{errorExt} {factor2} {string.Join(" ", groups)}".TrimEnd());
                Resulting(n, "", lo, "", null, "%ERROR", null);
                return;
            }
            case "PLIST":
                if (factor1.Equals("*ENTRY", StringComparison.OrdinalIgnoreCase) && _parameterLists.TryGetValue("*ENTRY", out var entry))
                    Emit(n, $"@ENTRY {string.Join(" ", entry.Select(p => p.Target))}");
                return;
            case "PARM" or "KLIST" or "KFLD":
                return;
            case "DEFINE":
                // A field defined again the same way, or a data area given to a field that's already there
                // (*DTAARA DEFINE *LDA LDA, with LDA a data structure), isn't a second definition.
                if (!_declared.Add(result)) return;
                // Like a special word: *DATE (8 digits), UDATE or *YEAR and the like, or an indicator.
                var like = factor2.ToUpperInvariant();
                var special = like switch
                {
                    "*DATE" => "ZONED(8 : 0)", "UDATE" => "ZONED(6 : 0)", "*YEAR" => "ZONED(4 : 0)", "UYEAR" or "*MONTH" or "UMONTH" or "*DAY" or "UDAY" => "ZONED(2 : 0)",
                    _ when like.StartsWith("*IN") => "IND",
                    _ => null,
                };
                if (factor1.Equals("*LIKE", StringComparison.OrdinalIgnoreCase) && special is not null)
                    Insert(n, $"DCL-S {result} {special}");
                else if (factor1.Equals("*LIKE", StringComparison.OrdinalIgnoreCase))
                    Insert(n, $"DCL-S {result} LIKE({factor2}{(Col(statements[i].Text, 64, 68).Trim() is { Length: > 0 } adjust ? $" : {adjust}" : "")})");
                else if (factor1.Equals("*DTAARA", StringComparison.OrdinalIgnoreCase))
                    Insert(n, $"DCL-S {result} CHAR(2000) DTAARA({(factor2.Length > 0 ? $"'{factor2}'" : "")})");
                return;
            case "TIME":
                Emit(n, $"@TIME {result}");
                return;
            case "ADDDUR" or "SUBDUR":
            {
                var (count, unit) = Pair(factor2);
                var (target, resultUnit) = Pair(result);
                var start = factor1.Length > 0 ? factor1 : target;
                if (op == "SUBDUR" && unit is null)
                    Emit(n, $"{target} = %DIFF({start} : {count} : {resultUnit ?? "*DAYS"})");
                else
                    Emit(n, $"{target} = {start} {(op == "ADDDUR" ? "+" : "-")} %{Duration(unit)}({count})");
                if (lo.Length > 0) Resulting(n, "", lo, "", null, "%ERROR", null);
                return;
            }
            case "EXTRCT":
            {
                var (value, unit) = Pair(factor2);
                Emit(n, $"{result} = %SUBDT({value} : {unit ?? "*DAYS"})");
                return;
            }
            case "TEST":
                Emit(n, $"TEST({(extender.Contains('E') ? extender : extender + "E")}) {factor1} {result}".Replace("  ", " "));
                if (lo.Length > 0) Resulting(n, "", lo, "", null, "%ERROR", null);
                return;
            case "TESTN":
                Emit(n, $"@TESTN {result} : {(hi.Length > 0 ? "*IN" + hi : "*OMIT")} : {(lo.Length > 0 ? "*IN" + lo : "*OMIT")} : {(eq.Length > 0 ? "*IN" + eq : "*OMIT")}");
                return;
            case "TESTB" or "BITON" or "BITOFF":
                Emit(n, $"@{op} {result} : {factor2}{(op == "TESTB" ? $" : {(hi.Length > 0 ? "*IN" + hi : "*OMIT")} : {(lo.Length > 0 ? "*IN" + lo : "*OMIT")} : {(eq.Length > 0 ? "*IN" + eq : "*OMIT")}" : "")}");
                return;
            case "OCCUR":
                if (factor1.Length > 0) Emit(n, $"%OCCUR({factor2}) = {factor1}");
                if (result.Length > 0) Emit(n, $"{result} = %OCCUR({factor2})");
                return;
            case "SHTDN":
                Resulting(n, hi, "", "", "%SHTDN", null, null);
                return;
            case "EXCEPT":
                Emit(n, $"@EXCEPT {factor2}".TrimEnd());
                return;
            case "IN" or "OUT" or "UNLOCK" when !(op == "UNLOCK" && _files.Contains(factor2)):
                Emit(n, $"{op}{errorExt} {(factor1.Length > 0 ? factor1 + " " : "")}{factor2}");
                if (lo.Length > 0) Resulting(n, "", lo, "", null, "%ERROR", null);
                return;
            case "ALLOC":
                Emit(n, $"{result} = %ALLOC({factor2})");
                return;
            case "REALLOC":
                Emit(n, $"{result} = %REALLOC({result} : {factor2})");
                return;

            // ---- files ----
            case "CHAIN" or "SETLL" or "SETGT" or "READE" or "READPE" or "DELETE":
            {
                var key = factor1.Length > 0 ? Operand(factor1) + " " : "";
                var of = FileArgument(factor2);
                Emit(n, $"{op}{errorExt} {key}{factor2} {result}".TrimEnd());
                switch (op)
                {
                    case "CHAIN":
                        Resulting(n, hi, lo, "", $"NOT %FOUND{of}", "%ERROR", null);
                        break;
                    case "SETLL":
                        Resulting(n, hi, lo, eq, $"NOT %FOUND{of}", "%ERROR", $"%EQUAL{of}");
                        break;
                    case "SETGT":
                        Resulting(n, hi, lo, "", $"NOT %FOUND{of}", "%ERROR", null);
                        break;
                    case "DELETE":
                        Resulting(n, hi, lo, "", $"NOT %FOUND{of}", "%ERROR", null);
                        break;
                    default:
                        Resulting(n, "", lo, eq, null, "%ERROR", $"%EOF{of}");
                        break;
                }
                return;
            }
            case "READ" or "READP" or "READC":
            {
                var of = FileArgument(factor2);
                Emit(n, $"{op}{errorExt} {factor2} {result}".TrimEnd());
                Resulting(n, "", lo, eq, null, "%ERROR", $"%EOF{of}");
                return;
            }
            case "EXFMT" or "WRITE" or "UPDATE" or "UNLOCK" or "FEOD" or "OPEN" or "CLOSE":
                Emit(n, $"{op}{errorExt} {factor2} {result}".TrimEnd());
                Resulting(n, "", lo, "", null, "%ERROR", null);
                return;

            default:
                // IFxx, DOWxx, DOUxx, WHENxx and CASxx, with the ANDxx and ORxx lines after them.
                if (op.Length == 4 && op.StartsWith("IF") || op.Length == 5 && op[..3] is "DOW" or "DOU" || op.Length == 6 && op.StartsWith("WHEN"))
                {
                    var kind = op.StartsWith("IF") ? "IF" : op.StartsWith("WHEN") ? "WHEN" : op[..3];
                    var relation = Relation(op[^2..]);
                    if (relation is null)
                    {
                        Problem(n, $"{op} isn't an operation code");
                        return;
                    }
                    _pending = (kind, [[$"{factor1} {relation} {factor2}"]], n, null, wrapped);
                    return;
                }
                if (op.Length == 5 && op.StartsWith("CAS"))
                {
                    var relation = Relation(op[^2..]);
                    if (Top() != "CAS")
                    {
                        Emit(n, "SELECT");
                        _blocks.Add(new Block("CAS", wrapped));
                    }
                    Emit(n, $"WHEN {factor1} {relation} {factor2}");
                    Emit(n, $"EXSR {result}");
                    return;
                }
                if (op.Length == 5 && op.StartsWith("CAB") || op == "CAB")
                {
                    var relation = op.Length == 5 ? Relation(op[^2..]) : null;
                    Resulting(n, hi, lo, eq, $"{factor1} > {factor2}", $"{factor1} < {factor2}", $"{factor1} = {factor2}");
                    if (relation is null) Emit(n, $"@GOTO {result}");
                    else
                    {
                        Emit(n, $"IF {factor1} {relation} {factor2}");
                        Emit(n, $"@GOTO {result}");
                        Emit(n, "ENDIF");
                    }
                    return;
                }
                if (RpgLanguage.FreeFormOperations.Contains(op) || RpgLanguage.FixedFormOnly.ContainsKey(op) || op is "ACQ" or "REL" or "POST" or "NEXT" or "FORCE" or "COMMIT" or "ROLBK" or "CALLB")
                {
                    Emit(n, $"{op}{ext} {factor1} {factor2} {result}".Replace("  ", " ").TrimEnd());
                    return;
                }
                Problem(n, $"{op} isn't an operation code");
                return;
        }
    }

    private static string Duration(string? unit) => (unit ?? "*DAYS").ToUpperInvariant() switch
    {
        "*D" or "*DAYS" => "DAYS",
        "*M" or "*MONTHS" => "MONTHS",
        "*Y" or "*YEARS" => "YEARS",
        "*H" or "*HOURS" => "HOURS",
        "*MN" or "*MINUTES" => "MINUTES",
        "*S" or "*SECONDS" => "SECONDS",
        _ => "MSECONDS",
    };

    // ---- I specs ----

    private InputRecord? _inputRecord;

    private void InputSpec(int n, string line)
    {
        var record = Col(line, 7, 16).Trim();
        var external = Col(line, 21, 30).Trim();
        if (record.Length > 0)
        {
            // A record: its format and its identifying indicator.
            var indicator = Col(line, 21, 22).Trim();
            _inputRecord = new InputRecord(record.ToUpperInvariant(), indicator.Length > 0 ? indicator.ToUpperInvariant() : null, []);
            _program.InputRecords.Add(_inputRecord);
            return;
        }
        if (_inputRecord is null)
        {
            Problem(n, "an I spec field comes before its record");
            return;
        }
        if (external.Length == 0)
        {
            Problem(n, "program-described input (I specs with positions) isn't supported", Severity.Unsupported);
            return;
        }
        var name = Col(line, 49, 62).Trim();
        string? Value(int from, int to) => Col(line, from, to).Trim() is { Length: > 0 } v ? v.ToUpperInvariant() : null;
        var matching = Value(65, 66);
        if (matching is not null) Problem(n, "matching fields (M1 to M9) aren't supported", Severity.Unsupported);
        _inputRecord.Fields.Add(new InputField(external.ToUpperInvariant(), (name.Length > 0 ? name : external).ToUpperInvariant(),
            Value(63, 64), matching, Value(69, 70), Value(71, 72), Value(73, 74)));
    }

    // ---- O specs ----

    private int OutputSpec(List<SourceStatement> statements, int i)
    {
        var line = statements[i].Text;
        var n = statements[i].Line;
        var file = Col(line, 7, 16).Trim();
        var type = char.ToUpperInvariant(Col(line, 17, 17).FirstOrDefault(' '));
        var isRecord = file.Length > 0 || type is 'H' or 'D' or 'T' or 'E';
        if (!isRecord)
        {
            Problem(n, "an O spec field comes before its record");
            return i;
        }
        int Number(string text) => int.TryParse(text.Trim(), out var v) ? v : 0;
        var fetch = Col(line, 18, 18).Trim().ToUpperInvariant() == "F";
        var operation = Col(line, 18, 20).Trim().ToUpperInvariant() is "ADD" or "DEL" ? Col(line, 18, 20).Trim().ToUpperInvariant() : null;
        var record = new OutputRecord(n, file.Length > 0 ? file.ToUpperInvariant() : _program.OutputRecords.LastOrDefault()?.File ?? "", type,
            OutputCondition(Col(line, 21, 29)), fetch, Col(line, 30, 39).Trim() is { Length: > 0 } except ? except.ToUpperInvariant() : null,
            Number(Col(line, 40, 42)), Number(Col(line, 43, 45)), Number(Col(line, 46, 48)), Number(Col(line, 49, 51)), operation, []);
        _program.OutputRecords.Add(record);
        // Its fields: the O specs after it with no file and no type.
        while (i + 1 < statements.Count && statements[i + 1].Kind == StatementKind.Fixed && char.ToUpperInvariant(statements[i + 1].Text[5]) == 'O')
        {
            var field = statements[i + 1].Text;
            if (field.Length > 6 && field[6] == '*')
            {
                i++;
                continue;
            }
            if (Col(field, 7, 16).Trim().Length > 0 || Col(field, 17, 17).Trim().Length > 0) break;
            // An AND or OR line of the record's conditions.
            if (Col(field, 16, 18).Trim().ToUpperInvariant() is "AND" or "OR")
            {
                i++;
                continue;
            }
            i++;
            var name = Col(field, 30, 43).Trim();
            var constant = Col(field, 53, 80).TrimEnd();
            var end = Col(field, 47, 51).Trim();
            var editCode = Col(field, 44, 44).Trim();
            string? text = null;
            string? editWord = null;
            if (constant.TrimStart().StartsWith('\''))
            {
                var literal = constant.Trim();
                var close = literal.LastIndexOf('\'');
                var value = close > 0 ? literal[1..close].Replace("''", "'") : literal[1..];
                if (name.Length > 0) editWord = value;
                else text = value;
            }
            record.Fields.Add(new OutputField(OutputCondition(Col(field, 21, 29)), name.Length > 0 ? name.ToUpperInvariant() : null, text,
                editCode.Length > 0 ? char.ToUpperInvariant(editCode[0]) : null, editWord, Col(field, 45, 45).Trim().ToUpperInvariant() == "B",
                Number(end.TrimStart('+')), end.StartsWith('+'), char.ToUpperInvariant(Col(field, 52, 52).FirstOrDefault(' '))));
        }
        return i;
    }

    /// <summary>O spec conditions: three indicators, each N or not, all of which have to hold.</summary>
    private static string? OutputCondition(string columns)
    {
        columns = columns.PadRight(9);
        var parts = new List<string>();
        for (var at = 0; at + 3 <= columns.Length; at += 3)
        {
            var piece = columns.Substring(at, 3).Trim();
            if (piece.Length > 0) parts.Add(Indicator(piece));
        }
        return parts.Count == 0 ? null : string.Join(" AND ", parts);
    }
}
