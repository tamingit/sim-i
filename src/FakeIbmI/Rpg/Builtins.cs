// RPG's built-in functions. RpgParser.Bifs lists the ones here; any other is reported when the member is saved.

using System.Globalization;

namespace FakeIbmI.Rpg;

internal sealed partial class Interpreter
{
    private object Builtin(BifExpr bif, Frame frame)
    {
        var args = bif.Args;
        object Value(int i) => i < args.Count ? Evaluate(args[i], frame) : throw new RpgError(100, $"{bif.Name} needs more parameters");
        string Text(int i) => Values.ToText(Value(i));
        decimal Number(int i) => Values.ToDecimal(Value(i));
        int Int(int i) => (int)Number(i);
        bool Has(int i) => i < args.Count;
        string Word(int i) => i < args.Count && args[i] is SpecialExpr s ? s.Name : throw new RpgError(100, $"{bif.Name} needs a word such as *ISO or *DAYS");
        RpgError Range() => new(100, "Value out of range for string operation.");

        switch (bif.Name)
        {
            // ---- pointers ----
            case "%ADDR":
            {
                var target = Locate(args.Count > 0 ? args[0] : throw new RpgError(100, "%ADDR needs a variable"), frame);
                var (storage, offset) = target.Pin();
                // %ADDR(v : *DATA): a varying-length field's data, after its length.
                return new Pointer(storage, offset + (Has(1) && Word(1) == "*DATA" && target.Type.Kind == RpgKind.Varchar ? 2 : 0));
            }
            case "%ALLOC":
                return new Pointer(new Storage(Math.Max(Int(0), 1)), 0);
            case "%REALLOC":
            {
                if (Value(0) is not Pointer { Target: { } storage } pointer) throw new RpgError(222, "Pointer or parameter error: %REALLOC of a *NULL pointer.");
                storage.Resize(pointer.Offset + Int(1));
                return pointer;
            }
            case "%STR":
                return CString(Value(0) as Pointer ?? throw new RpgError(222, "Pointer or parameter error: %STR needs a pointer."), Has(1) ? Int(1) : int.MaxValue);
            case "%PADDR":
                throw new RpgError(100, "procedure pointers aren't supported");

            // ---- characters ----
            case "%SCANR":
            {
                var (search, source) = (Text(0), Text(1));
                var start = Has(2) ? Int(2) : 1;
                var end = Has(3) ? start + Int(3) - 1 : source.Length;
                if (start < 1 || end > source.Length || search.Length == 0) throw Range();
                var last = source[..end].LastIndexOf(search, StringComparison.Ordinal);
                return (decimal)(last + 1 >= start ? last + 1 : 0);
            }
            case "%EDITW":
            {
                var value = Number(0);
                var type = IsVariable(args[0]) && Find(Root(args[0]), frame) is not null ? Locate(args[0], frame).Type : null;
                return EditWord(value, type?.Decimals ?? (int)value.Scale, Text(1));
            }
            case "%EDITFLT":
            {
                var real = Value(0) is double d ? d : (double)Number(0);
                var exponent = real == 0 ? 0 : (int)Math.Floor(Math.Log10(Math.Abs(real)));
                var mantissa = real == 0 ? 0 : real / Math.Pow(10, exponent);
                return $"{(real < 0 ? '-' : '+')}{Math.Abs(mantissa).ToString("0.000000000000000", CultureInfo.InvariantCulture)}E{(exponent < 0 ? '-' : '+')}{Math.Abs(exponent):000}";
            }
            case "%CHARCOUNT":
                return (decimal)Text(0).Length;
            case "%GRAPH" or "%UCS2":
                return Text(0);
            case "%CONCATARR":
                return string.Join(Value(0) is SpecialValue ? "" : Text(0), Elements(1).Select(e => Values.ToText(e.Get())));
            case "%BITAND" or "%BITOR" or "%BITXOR":
            {
                var values = Enumerable.Range(0, args.Count).Select(Value).ToList();
                if (values.All(Values.IsNumber))
                {
                    var result = (long)Values.ToDecimal(values[0]);
                    foreach (var v in values.Skip(1))
                    {
                        var n = (long)Values.ToDecimal(v);
                        result = bif.Name switch { "%BITAND" => result & n, "%BITOR" => result | n, _ => result ^ n };
                    }
                    return (decimal)result;
                }
                var texts = values.Select(Values.ToText).ToList();
                var bytes = texts[0].Select(Ebcdic.ToByte).ToArray();
                foreach (var t in texts.Skip(1))
                {
                    for (var i = 0; i < bytes.Length; i++)
                    {
                        var b = i < t.Length ? Ebcdic.ToByte(t[i]) : (byte)0;
                        bytes[i] = bif.Name switch { "%BITAND" => (byte)(bytes[i] & b), "%BITOR" => (byte)(bytes[i] | b), _ => (byte)(bytes[i] ^ b) };
                    }
                }
                return new string([.. bytes.Select(Ebcdic.ToChar)]);
            }
            case "%BITNOT":
                return Value(0) is var bits && Values.IsNumber(bits) ? (decimal)~(long)Values.ToDecimal(bits)
                    : new string([.. Text(0).Select(c => Ebcdic.ToChar((byte)~Ebcdic.ToByte(c)))]);

            // ---- arrays and lists ----
            case "%SUBARR":
            {
                var elements = Elements(0);
                var start = Int(1);
                var count = Has(2) ? Int(2) : elements.Length - start + 1;
                if (start < 1 || count < 0 || start + count - 1 > elements.Length) throw new RpgError(121, "An array index is out of range.");
                return new Cell(elements[0].Type with { Dim = count }, elements[(start - 1)..(start - 1 + count)]);
            }
            case "%SPLIT":
            {
                var text = Text(0);
                var separators = Has(1) ? Text(1).ToCharArray() : [' '];
                var pieces = text.Split(separators, StringSplitOptions.RemoveEmptyEntries);
                return List(pieces.Cast<object>().ToList(), new RpgType(RpgKind.Varchar, Math.Max(text.Length, 1)));
            }
            case "%LIST":
            {
                var items = Enumerable.Range(0, args.Count).Select(Value).ToList();
                return List(items, TypeOf(items[0]) is { Kind: RpgKind.Char } c ? new RpgType(RpgKind.Varchar, Math.Max(items.Max(i => Values.ToText(i).Length), 1)) : TypeOf(items[0]));
            }
            case "%RANGE":
                return new RangeValue(Value(0), Value(1));
            case "%LOOKUPLT" or "%LOOKUPLE" or "%LOOKUPGT" or "%LOOKUPGE":
            {
                // A sorted array: the element nearest the value on the side the name says.
                var (wanted, elements) = (Value(0), Elements(1));
                var start = Has(2) ? Int(2) : 1;
                var end = Has(3) ? Math.Min(elements.Length, start + Int(3) - 1) : elements.Length;
                var found = 0;
                for (var i = Math.Max(start, 1); i <= end; i++)
                {
                    var c = Values.Compare(elements[i - 1].Get(), wanted);
                    var fits = bif.Name switch { "%LOOKUPLT" => c < 0, "%LOOKUPLE" => c <= 0, "%LOOKUPGT" => c > 0, _ => c >= 0 };
                    if (!fits) continue;
                    if (bif.Name is "%LOOKUPGT" or "%LOOKUPGE") return (decimal)i;
                    found = i;
                }
                return (decimal)found;
            }
            case "%HIVAL" or "%LOVAL":
                return Values.Convert(new SpecialValue(bif.Name == "%HIVAL" ? "*HIVAL" : "*LOVAL"), Locate(args[0], frame).Type);

            // ---- the program ----
            case "%PROC":
                return frame.Procedure == _program.Main ? _name : frame.Procedure.Name;
            case "%PARMNUM":
                return args is [NameExpr parameter] && frame.Procedure.Params.FindIndex(p => p.Name == parameter.Name) is var index and >= 0
                    ? (decimal)(index + 1)
                    : throw new RpgError(100, "%PARMNUM needs a parameter's name");
            case "%OPEN":
                return args is [NameExpr openFile] && _program.Files.FirstOrDefault(f => f.Name == openFile.Name) is { } decl ? IsOpen(decl) : throw new RpgError(100, "%OPEN needs a file of the program");
            case "%SHTDN":
                return false;
            case "%MSG":
            {
                // The fake has no message files: the data is the text, or the id is.
                var id = Text(0).Trim().ToUpperInvariant();
                return new MessageValue(id, Has(2) ? Text(2).TrimEnd() : id);
            }
            case "%TARGET":
                return new SpecialValue("*CALLER");
            case "%MSECONDS":
                return new Duration("*MSECONDS", Number(0));
            case "%TRIM":
                return Has(1) ? Text(0).Trim(Text(1).ToCharArray()) : Text(0).Trim(' ');
            case "%TRIMR":
                return Has(1) ? Text(0).TrimEnd(Text(1).ToCharArray()) : Text(0).TrimEnd(' ');
            case "%TRIML":
                return Has(1) ? Text(0).TrimStart(Text(1).ToCharArray()) : Text(0).TrimStart(' ');
            case "%LEN":
                if (Has(0) && IsVariable(args[0]) && Root(args[0]).Length > 0 && Find(Root(args[0]), frame) is not null)
                {
                    var cell = Locate(args[0], frame);
                    if (cell.Type.Kind != RpgKind.Varchar && cell.Elements is null) return (decimal)Values.ByteLength(cell.Type);
                }
                return (decimal)Text(0).Length;
            case "%SIZE":
                return (decimal)Values.ByteLength(Locate(args.Count > 0 ? args[0] : throw new RpgError(100, "%SIZE needs a variable"), frame).Type);
            case "%DECPOS":
                return (decimal)Number(0).Scale;
            case "%SUBST":
            {
                var (s, start) = (Text(0), Int(1));
                var length = Has(2) ? Int(2) : s.Length - start + 1;
                if (start < 1 || length < 0 || start + length - 1 > s.Length) throw Range();
                return s.Substring(start - 1, length);
            }
            case "%LEFT":
                return Text(0)[..Math.Clamp(Int(1), 0, Text(0).Length)];
            case "%RIGHT":
            {
                var s = Text(0);
                return s[(s.Length - Math.Clamp(Int(1), 0, s.Length))..];
            }
            case "%SCAN":
            {
                var (find, s) = (Text(0), Text(1));
                var start = Has(2) ? Int(2) : 1;
                if (start < 1 || start > Math.Max(s.Length, 1)) throw Range();
                return (decimal)(find.Length == 0 ? 0 : s.IndexOf(find, start - 1, StringComparison.Ordinal) + 1);
            }
            case "%SCANRPL":
                return Text(0).Length == 0 ? Text(2) : Text(2).Replace(Text(0), Text(1), StringComparison.Ordinal);
            case "%REPLACE":
            {
                var (replacement, s) = (Text(0), Text(1));
                var start = Has(2) ? Int(2) : 1;
                var length = Has(3) ? Int(3) : replacement.Length;
                if (start < 1 || start > s.Length + 1 || length < 0) throw Range();
                var end = Math.Min(start - 1 + length, s.Length);
                return s[..(start - 1)] + replacement + s[end..];
            }
            case "%XLATE":
            {
                var (from, to, s) = (Text(0), Text(1), Text(2));
                return string.Concat(s.Select(c => from.IndexOf(c) is >= 0 and var i && i < to.Length ? to[i] : c));
            }
            case "%UPPER":
                return Text(0).ToUpperInvariant();
            case "%LOWER":
                return Text(0).ToLowerInvariant();
            case "%CONCAT":
                return string.Join(Value(0) is SpecialValue ? "" : Text(0), Enumerable.Range(1, args.Count - 1).Select(Text));
            case "%CHECK" or "%CHECKR":
            {
                var (allowed, s) = (Text(0), Text(1));
                var forward = bif.Name == "%CHECK";
                var start = Has(2) ? Int(2) : forward ? 1 : s.Length;
                if (s.Length > 0 && (start < 1 || start > s.Length)) throw Range();
                for (var i = start - 1; i >= 0 && i < s.Length; i += forward ? 1 : -1)
                {
                    if (!allowed.Contains(s[i])) return (decimal)(i + 1);
                }
                return 0m;
            }
            case "%CHAR":
            {
                var value = Value(0);
                return value switch
                {
                    DateOnly d => Values.FormatDate(d, Has(1) ? Word(1) : _program.DateFormat),
                    TimeOnly t => Values.FormatTime(t, Has(1) ? Word(1) : _program.TimeFormat),
                    string s => s,
                    _ => Values.ToText(value),
                };
            }
            case "%DEC" or "%DECH":
            {
                var value = Value(0);
                if (value is DateOnly date) return Values.ParseNumber(Values.FormatDate(date, Values.SplitFormat(Has(1) ? Word(1) : "*ISO").Name + "0"));
                if (value is TimeOnly time) return Values.ParseNumber(Values.FormatTime(time, "*ISO0"));
                if (value is DateTime stamp) return Values.ParseNumber(stamp.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));
                if (!Has(2)) return value is string ? Values.ToDecimal(value) : decimal.Truncate(Values.ToDecimal(value));
                return Values.Convert(value, RpgType.Number(Int(1), Int(2)), halfAdjust: bif.Name == "%DECH");
            }
            case "%INT" or "%UNS":
                return decimal.Truncate(Number(0));
            case "%INTH" or "%UNSH":
                return decimal.Round(Number(0), 0, MidpointRounding.AwayFromZero);
            case "%FLOAT":
                return (double)Number(0);
            case "%EDITC":
            {
                var value = Number(0);
                var code = Text(1);
                if (code.Length != 1) throw new RpgError(100, "%EDITC needs a one-character edit code");
                var (digits, decimals) = (Math.Max(decimal.Truncate(Math.Abs(value)).ToString(CultureInfo.InvariantCulture).Length, 1) + value.Scale, (int)value.Scale);
                if (IsVariable(args[0]) && Find(Root(args[0]), frame) is not null && Locate(args[0], frame) is { Type: { IsNumeric: true } type, Elements: null })
                    (digits, decimals) = (type.Length, type.Decimals);
                return Values.Edit(value, digits, decimals, code[0]);
            }
            case "%ABS":
                return Value(0) is double f ? Math.Abs(f) : Math.Abs(Number(0));
            case "%SQRT":
                return Number(0) < 0 ? throw new RpgError(101, "Negative square root.") : (double)Math.Sqrt((double)Number(0));
            case "%REM" or "%DIV":
            {
                var (a, b) = (decimal.Truncate(Number(0)), decimal.Truncate(Number(1)));
                if (b == 0) throw new RpgError(102, "Attempt to divide by zero.");
                return bif.Name == "%REM" ? a % b : decimal.Truncate(a / b);
            }
            case "%MIN" or "%MAX":
            {
                var best = Value(0);
                for (var i = 1; i < args.Count; i++)
                {
                    var other = Value(i);
                    if (bif.Name == "%MIN" ? Values.Compare(other, best) < 0 : Values.Compare(other, best) > 0) best = other;
                }
                return best;
            }
            case "%EOF":
                return args is [NameExpr { Name: var eofFile }] && DiskFor(eofFile) is { } eofDisk ? eofDisk.Eof : _eof;
            case "%FOUND":
                return args is [NameExpr { Name: var foundFile }] && DiskFor(foundFile) is { } foundDisk ? foundDisk.Found : _found;
            case "%EQUAL":
                return args is [NameExpr { Name: var equalFile }] && DiskFor(equalFile) is { } equalDisk ? equalDisk.Equal : _disks.Values.Any(d => d.Equal);
            case "%ERROR":
                return _error;
            case "%STATUS":
                return (decimal)_status;
            case "%PARMS":
                return (decimal)frame.Parameters;
            case "%PASSED":
                return args is [NameExpr name] && frame.Variables.ContainsKey(name.Name);
            case "%OMITTED":
                return args is [NameExpr omitted] && frame.Procedure.Params.FindIndex(p => p.Name == omitted.Name) is var at and >= 0 && frame.Omitted.Contains(at);
            case "%ELEM":
                // %ELEM(array : *MAX) or (*ALLOC): what a DIM(*VAR) array can hold.
                if (Has(1) && Word(1) is "*MAX" or "*ALLOC") return (decimal)Locate(args[0], frame).Type.Dim;
                return (decimal)(Elements(0).Length);
            case "%OCCUR":
                return _occurrences.TryGetValue(Locate(args[0], frame), out var occurrence) ? (decimal)occurrence.Current : throw new RpgError(100, "%OCCUR needs a multiple-occurrence data structure (OCCURS)");
            case "%TLOOKUP" or "%TLOOKUPLT" or "%TLOOKUPLE" or "%TLOOKUPGT" or "%TLOOKUPGE":
            {
                // A table: whether the value is in it; the element found becomes the table's current one (and its alternate table's).
                var (wanted, table) = (Value(0), Locate(args[1], frame));
                var elements = table.Elements ?? throw new RpgError(100, $"{bif.Name} needs a table");
                for (var i = 0; i < elements.Length; i++)
                {
                    var c = Values.Compare(elements[i].Get(), wanted);
                    if (!(bif.Name switch { "%TLOOKUPLT" => c < 0, "%TLOOKUPLE" => c <= 0, "%TLOOKUPGT" => c > 0, "%TLOOKUPGE" => c >= 0, _ => c == 0 })) continue;
                    _tableCurrent[table] = i;
                    if (Has(2)) _tableCurrent[Locate(args[2], frame)] = i;
                    return true;
                }
                return false;
            }
            case "%XFOOT":
                return Elements(0).Sum(c => Values.ToDecimal(c.Get()));
            case "%LOOKUP":
            {
                var (wanted, elements) = (Value(0), Elements(1));
                var start = Has(2) ? Int(2) : 1;
                var count = Has(3) ? Int(3) : elements.Length;
                for (var i = Math.Max(start, 1); i <= Math.Min(elements.Length, start + count - 1); i++)
                {
                    if (Values.Compare(elements[i - 1].Get(), wanted) == 0) return (decimal)i;
                }
                return 0m;
            }
            case "%MAXARR" or "%MINARR":
            {
                var elements = Elements(0);
                var best = 0;
                for (var i = 1; i < elements.Length; i++)
                {
                    var compared = Values.Compare(elements[i].Get(), elements[best].Get());
                    if (bif.Name == "%MAXARR" ? compared > 0 : compared < 0) best = i;
                }
                return (decimal)(best + 1);
            }
            case "%DATE":
            {
                if (!Has(0)) return DateOnly.FromDateTime(DateTime.Now);
                var value = Value(0);
                var format = Has(1) ? Word(1) : _program.DateFormat;
                return value switch
                {
                    DateOnly d => d,
                    DateTime t => DateOnly.FromDateTime(t),
                    decimal n => Values.ParseDate(decimal.Truncate(n).ToString(CultureInfo.InvariantCulture).PadLeft(DigitsOf(format), '0'), Values.SplitFormat(format).Name + "0"),
                    _ => Values.ParseDate(Values.ToText(value), format),
                };
            }
            case "%TIME":
            {
                if (!Has(0)) return TimeOnly.FromDateTime(DateTime.Now);
                var value = Value(0);
                return value switch
                {
                    TimeOnly t => t,
                    DateTime t => TimeOnly.FromDateTime(t),
                    decimal n => Values.ParseTime(decimal.Truncate(n).ToString(CultureInfo.InvariantCulture).PadLeft(6, '0')),
                    _ => Values.ParseTime(Values.ToText(value)),
                };
            }
            case "%TIMESTAMP":
            {
                if (!Has(0)) return DateTime.Now;
                var value = Value(0);
                return value switch
                {
                    DateTime t => t,
                    DateOnly d => d.ToDateTime(TimeOnly.MinValue),
                    decimal n => Values.ParseTimestamp(decimal.Truncate(n).ToString(CultureInfo.InvariantCulture)),
                    _ => Values.ParseTimestamp(Values.ToText(value)),
                };
            }
            case "%DAYS" or "%MONTHS" or "%YEARS" or "%HOURS" or "%MINUTES" or "%SECONDS":
                return new Duration("*" + bif.Name[1..], Number(0));
            case "%DIFF":
            {
                var (a, b) = (Moment(Value(0)), Moment(Value(1)));
                var span = a - b;
                return Unit(Word(2)) switch
                {
                    "*DAYS" => decimal.Truncate((decimal)span.TotalDays),
                    "*HOURS" => decimal.Truncate((decimal)span.TotalHours),
                    "*MINUTES" => decimal.Truncate((decimal)span.TotalMinutes),
                    "*SECONDS" => decimal.Truncate((decimal)span.TotalSeconds),
                    "*MSECONDS" => span.Ticks / 10,
                    "*MONTHS" => Months(a, b),
                    "*YEARS" => decimal.Truncate(Months(a, b) / 12),
                    var unit => throw new RpgError(100, $"%DIFF doesn't support {unit}"),
                };
            }
            case "%SUBDT":
            {
                var moment = Moment(Value(0));
                return Unit(Word(1)) switch
                {
                    "*YEARS" => moment.Year,
                    "*MONTHS" => moment.Month,
                    "*DAYS" => moment.Day,
                    "*HOURS" => moment.Hour,
                    "*MINUTES" => moment.Minute,
                    "*SECONDS" => moment.Second,
                    "*MSECONDS" => (decimal)(moment.Ticks % TimeSpan.TicksPerSecond / 10),
                    var unit => throw new RpgError(100, $"%SUBDT doesn't support {unit}"),
                };
            }
            default:
                throw new RpgError(100, $"{bif.Name} isn't supported");
        }

        Cell[] Elements(int i) => Value(i) is Cell { Elements: { } elements } ? elements : throw new RpgError(100, $"{bif.Name} needs an array");
    }

    /// <summary>A temporary array of these values (%LIST, %SPLIT).</summary>
    private static Cell List(List<object> values, RpgType element)
    {
        var cell = new Cell(element with { Dim = Math.Max(values.Count, 1) });
        for (var i = 0; i < values.Count; i++) cell.Elements![i].Set(values[i]);
        return values.Count == 0 ? new Cell(element with { Dim = 0 }, []) : cell;
    }

    /// <summary>
    /// A number edited with an edit word (%EDITW): blanks are digit positions, a 0 or * stops zero
    /// suppression after it (* fills with asterisks), &amp; is a blank, and other characters show when the digits
    /// left of them do. After the last digit position, CR or - shows only for a negative number.
    /// </summary>
    internal static string EditWord(decimal value, int decimals, string word)
    {
        var positions = word.Count(c => c is ' ' or '0' or '*');
        var lastDigit = word.LastIndexOfAny([' ', '0', '*']);
        var body = word[..(lastDigit + 1)];
        var status = word[(lastDigit + 1)..];
        var digits = decimal.Truncate(Math.Abs(value) * Values.Pow10(decimals)).ToString(CultureInfo.InvariantCulture);
        digits = digits.Length > positions ? digits[^positions..] : digits.PadLeft(positions, '0');
        var stop = body.IndexOfAny(['0', '*']);
        var fill = stop >= 0 && body[stop] == '*' ? '*' : ' ';
        var result = new System.Text.StringBuilder();
        var significant = false;
        var d = 0;
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (c is ' ' or '0' or '*')
            {
                var digit = digits[d++];
                if (digit != '0') significant = true;
                result.Append(significant ? digit : fill);
                if (i == stop) significant = true;
            }
            else if (c == '&') result.Append(' ');
            else result.Append(significant || i == 0 && c is '$' ? c : fill);
        }
        var negative = value < 0;
        foreach (var c in status) result.Append(negative ? (c == '&' ? ' ' : c) : c is 'C' or 'R' or '-' ? ' ' : c == '&' ? ' ' : c);
        return result.ToString();
    }

    /// <summary>How many digits a numeric date has in this format.</summary>
    private static int DigitsOf(string format) => Values.SplitFormat(format).Name switch
    {
        "*ISO" or "*USA" or "*EUR" or "*JIS" => 8,
        "*CYMD" or "*CMDY" or "*CDMY" or "*LONGJUL" => 7,
        "*JUL" => 5,
        _ => 6,
    };

    private static DateTime Moment(object value) => value switch
    {
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        TimeOnly t => new DateTime(2000, 1, 1).Add(t.ToTimeSpan()),
        DateTime t => t,
        _ => throw new RpgError(112, "Date, Time or Timestamp value is not valid."),
    };

    private static string Unit(string word) => word switch
    {
        "*D" or "*DAYS" => "*DAYS",
        "*M" or "*MONTHS" => "*MONTHS",
        "*Y" or "*YEARS" => "*YEARS",
        "*H" or "*HOURS" => "*HOURS",
        "*MN" or "*MINUTES" => "*MINUTES",
        "*S" or "*SECONDS" => "*SECONDS",
        "*MS" or "*MSECONDS" => "*MSECONDS",
        _ => word,
    };

    /// <summary>Whole months from <paramref name="b"/> to <paramref name="a"/>.</summary>
    private static decimal Months(DateTime a, DateTime b)
    {
        var months = (a.Year - b.Year) * 12 + a.Month - b.Month;
        if (months > 0 && a.Day < b.Day) months--;
        if (months < 0 && a.Day > b.Day) months++;
        return months;
    }
}
