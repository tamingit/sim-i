// RPG's types and values. A value is a string (character), decimal (every numeric type but
// float), double (float), bool (indicator), DateOnly, TimeOnly, DateTime (timestamp), a Pointer,
// a DsValue or, for an array, the Cell that holds its elements.
//
// A data structure is bytes, as on an IBM i: a Storage, one character for each byte (the byte's
// character in CCSID 37), and each subfield a cell that reads and writes its bytes in its type's
// layout (packed, zoned, binary, integer, float, a varying length's prefix ...). So subfields can
// overlay each other, a structure can be moved as characters, and a pointer can point into one.
// Any other variable keeps its value as an object, until its address is taken: then it moves into
// a Storage of its own.

using System.Buffers.Binary;
using System.Globalization;

namespace SimI.Rpg;

internal enum RpgKind
{
    Char,
    Varchar,

    /// <summary>Packed, zoned and binary decimal: Length digits, Decimals of them after the point.</summary>
    Decimal,

    /// <summary>Integer, signed or not: Length is 3, 5, 10 or 20 digits.</summary>
    Int,
    Float,
    Ind,
    Date,
    Time,
    Timestamp,
    Ds,
    Pointer,
}

/// <param name="Form">How a decimal lies in storage: P packed, S zoned, B binary.</param>
/// <param name="Varying">For DIM(*VAR : n), V; for DIM(*AUTO : n), A: the array's number of elements changes, up to Dim.</param>
internal sealed record RpgType(RpgKind Kind, int Length = 0, int Decimals = 0, bool Unsigned = false, DsLayout? Ds = null, int Dim = 0, char Form = 'P',
    char? Varying = null)
{
    public bool IsNumeric => Kind is RpgKind.Decimal or RpgKind.Int or RpgKind.Float;
    public RpgType Element => this with { Dim = 0 };

    public static RpgType Char(int length) => new(RpgKind.Char, length);
    public static RpgType Number(int digits, int decimals, char form = 'P') => new(RpgKind.Decimal, digits, decimals, Form: form);
    public static readonly RpgType Indicator = new(RpgKind.Ind, 1);
    public static readonly RpgType PointerType = new(RpgKind.Pointer, 16);
}

/// <param name="Position">Where the subfield starts in the structure, 1 first.</param>
internal sealed record DsField(string Name, RpgType Type, Expr? Inz, int Position);

/// <param name="Length">The structure's length in bytes.</param>
internal sealed record DsLayout(IReadOnlyList<DsField> Fields, bool Qualified, int Length)
{
    /// <summary>
    /// The subfields that don't lie over one before them: what stands for the structure as a list of
    /// values (an SQL host structure, a structure assigned to another of the same layout).
    /// </summary>
    public IReadOnlyList<int> Owners { get; } = OwnersOf(Fields);

    private static List<int> OwnersOf(IReadOnlyList<DsField> fields)
    {
        var owners = new List<int>();
        for (var i = 0; i < fields.Count; i++)
        {
            var (start, end) = (fields[i].Position - 1, fields[i].Position - 1 + Values.ByteLength(fields[i].Type) * Math.Max(1, fields[i].Type.Dim));
            if (owners.All(k => end <= fields[k].Position - 1 || start >= fields[k].Position - 1 + Values.ByteLength(fields[k].Type) * Math.Max(1, fields[k].Type.Dim)))
                owners.Add(i);
        }
        return owners;
    }
}

/// <summary>Bytes: one character for each, the byte's character in CCSID 37. Pointers in it are kept beside the bytes.</summary>
internal sealed class Storage
{
    public Storage(int length)
    {
        Bytes = new char[Math.Max(length, 0)];
        Array.Fill(Bytes, ' ');
    }

    public char[] Bytes { get; private set; }

    /// <summary>The pointers stored in it, by their offset.</summary>
    public Dictionary<int, Pointer> Pointers { get; } = [];

    /// <summary>Makes it longer or shorter (%REALLOC), keeping what fits.</summary>
    public void Resize(int length)
    {
        var bytes = new char[length];
        Array.Fill(bytes, ' ');
        Array.Copy(Bytes, bytes, Math.Min(length, Bytes.Length));
        Bytes = bytes;
    }

    public string Text(int offset, int length) => new(Bytes, offset, Math.Max(0, Math.Min(length, Bytes.Length - offset)));

    /// <summary>Puts characters at the offset, as many as fit.</summary>
    public void Put(int offset, string text)
    {
        for (var i = 0; i < text.Length && offset + i < Bytes.Length; i++) Bytes[offset + i] = text[i];
    }
}

/// <summary>A pointer: to a place in a Storage, or null (*NULL). Arithmetic moves its offset.</summary>
internal sealed record Pointer(Storage? Target, int Offset = 0)
{
    public static readonly Pointer Null = new((Storage?)null);
    public bool IsNull => Target is null;
}

/// <summary>A data structure's subfields, in order, each a view of its bytes in the structure's storage.</summary>
internal sealed class DsValue
{
    public DsValue(DsLayout layout, Storage storage, int offset)
    {
        Layout = layout;
        Storage = storage;
        Offset = offset;
        Cells = [.. layout.Fields.Select(f => new Cell(f.Type, storage, offset + f.Position - 1))];
    }

    public DsLayout Layout { get; }
    public Storage Storage { get; }

    /// <summary>Where the structure starts in its storage.</summary>
    public int Offset { get; }

    public IReadOnlyList<Cell> Cells { get; }

    /// <summary>The subfields that don't lie over others, in order: what stands for the structure as a list of values.</summary>
    public IEnumerable<Cell> Data => Layout.Owners.Select(i => Cells[i]);

    /// <summary>The structure's bytes, as characters.</summary>
    public string Text => Storage.Text(Offset, Layout.Length);

    /// <summary>Sets the structure's bytes from characters, padded with blanks or cut to its length.</summary>
    public void Fill(string image)
    {
        Storage.Put(Offset, image.Length >= Layout.Length ? image[..Layout.Length] : image.PadRight(Layout.Length));
        for (var i = 0; i < Layout.Length; i++) Storage.Pointers.Remove(Offset + i);
    }

    public Cell? Field(string name)
    {
        for (var i = 0; i < Layout.Fields.Count; i++)
        {
            if (Layout.Fields[i].Name == name) return Cells[i];
        }
        return null;
    }
}

/// <summary>*BLANKS, *ZEROS, *HIVAL, *LOVAL, *ON, *OFF, *ALL'x' or *SYS: a value that takes its target's type.</summary>
internal sealed record SpecialValue(string Name, string? All = null);

/// <summary>%MSG(id : file : data): a message to send.</summary>
internal sealed record MessageValue(string Id, string Text);

/// <summary>%RANGE(low : high), for IN.</summary>
internal sealed record RangeValue(object Low, object High);

/// <summary>%DAYS(n) and the like.</summary>
internal sealed record Duration(string Unit, decimal Count);

/// <summary>A run-time error, with RPG's status code (%STATUS). MONITOR catches it; otherwise it ends the program.</summary>
/// <param name="messageId">The message that went with it, such as CPF9801 from a command QCMDEXC ran; null for RPG's own (RNX and the status).</param>
internal sealed class RpgError(int status, string message, string? messageId = null) : Exception(message)
{
    public int Status { get; } = status;
    public string MessageId { get; } = messageId ?? $"RNX{status:0000}";
}

/// <summary>
/// A variable, subfield, array or array element: its type and current value. A cell in a Storage
/// (a subfield, or a variable whose address was taken) reads and writes its bytes there; any other
/// keeps its value. A BASED cell finds its storage through its pointer each time it's used.
/// </summary>
internal sealed class Cell
{
    private object _value = null!;
    private Storage? _storage;
    private int _offset;
    private readonly Func<Pointer>? _basedOn;

    public Cell(RpgType type) : this(type, null, 0)
    {
    }

    /// <summary>A cell that is the bytes at <paramref name="offset"/> in <paramref name="storage"/>; with none, a cell of its own (a structure gets storage of its own).</summary>
    public Cell(RpgType type, Storage? storage, int offset)
    {
        Type = type;
        if (storage is null && type.Kind == RpgKind.Ds && type.Dim == 0)
        {
            storage = new Storage(Values.ByteLength(type));
            offset = 0;
        }
        _storage = storage;
        _offset = offset;
        Build();
        Clear();
    }

    /// <summary>An array whose elements are other cells (%SUBARR, %SPLIT).</summary>
    public Cell(RpgType type, Cell[] elements)
    {
        Type = type;
        _elements = elements;
    }

    /// <summary>A BASED cell: its bytes are wherever the pointer points.</summary>
    public Cell(RpgType type, Func<Pointer> basedOn)
    {
        Type = type;
        _basedOn = basedOn;
    }

    private void Build()
    {
        if (Type.Dim > 0)
        {
            var element = Values.ByteLength(Type.Element);
            Elements = [.. Enumerable.Range(0, Type.Dim).Select(i => new Cell(Type.Element, _storage, _offset + i * element))];
        }
        else if (Type.Kind == RpgKind.Ds)
        {
            _value = new DsValue(Type.Ds!, _storage!, _offset);
        }
    }

    public RpgType Type { get; }

    public Cell[]? Elements
    {
        get => _basedOn is not null ? Based().Elements : Type.Varying is null || _elements is null ? _elements : _elements[..Count];
        private set => _elements = value;
    }

    /// <summary>A DIM(*VAR) or DIM(*AUTO) array's number of elements now (%ELEM).</summary>
    public int Count
    {
        get;
        set => field = value < 0 || value > Type.Dim ? throw new RpgError(121, "An array index is out of range.") : value;
    }

    /// <summary>An element, by its index from 1: a DIM(*AUTO) array grows to take it.</summary>
    public Cell Element(int index)
    {
        if (Type.Varying == 'A' && index > Count && index <= Type.Dim) Count = index;
        var elements = Elements ?? throw new RpgError(100, "this isn't an array");
        if (index < 1 || index > elements.Length) throw new RpgError(121, "An array index is out of range.");
        return elements[index - 1];
    }

    private Cell[]? _elements;

    /// <summary>Where the cell's bytes are: its storage and offset, or null when it keeps its value as an object.</summary>
    public (Storage Storage, int Offset)? Place
    {
        get
        {
            if (_basedOn is not null)
            {
                var p = _basedOn();
                return p.Target is null ? throw new RpgError(222, "Pointer or parameter error: a BASED variable's pointer is *NULL.") : (p.Target, p.Offset);
            }
            return _storage is null ? null : (_storage, _offset);
        }
    }

    /// <summary>The cell a BASED cell is at the moment: a view at its pointer.</summary>
    private Cell Based()
    {
        var (storage, offset) = Place!.Value;
        return new Cell(Type, storage, offset, keep: true);
    }

    /// <summary>A view of existing bytes, which keeps them as they are.</summary>
    private Cell(RpgType type, Storage storage, int offset, bool keep)
    {
        Type = type;
        _storage = storage;
        _offset = offset;
        Build();
    }

    /// <summary>The value; for an array, null (see <see cref="Elements"/>).</summary>
    public object Value
    {
        get
        {
            if (_basedOn is not null) return Based().Value;
            if (_storage is null || Type.Kind == RpgKind.Ds || Type.Dim > 0) return _value;
            return Bytes.Decode(Type, _storage, _offset);
        }
    }

    public DsValue Ds => (DsValue)Value;

    /// <summary>The value as an expression sees it: an array is the cell itself.</summary>
    public object Get() => Elements is null ? Value : this;

    /// <summary>
    /// Moves the cell into a storage of its own (its address is being taken), keeping its value. An
    /// array's elements move with it. Returns where it is.
    /// </summary>
    public (Storage Storage, int Offset) Pin()
    {
        if (Place is { } place) return place;
        var storage = new Storage(Values.ByteLength(Type) * Math.Max(1, Type.Dim));
        Attach(storage, 0);
        return (storage, 0);
    }

    private void Attach(Storage storage, int offset)
    {
        if (Elements is { } elements)
        {
            var size = Values.ByteLength(Type.Element);
            for (var i = 0; i < elements.Length; i++) elements[i].Attach(storage, offset + i * size);
            _storage = storage;
            _offset = offset;
            return;
        }
        var value = _value;
        _storage = storage;
        _offset = offset;
        Bytes.Encode(Type, value, storage, offset);
    }

    public void Clear()
    {
        if (_basedOn is not null)
        {
            Based().Clear();
            return;
        }
        if (Elements is { } elements)
        {
            foreach (var e in elements) e.Clear();
            return;
        }
        if (Type.Kind == RpgKind.Ds)
        {
            foreach (var c in Ds.Cells) c.Clear();
            return;
        }
        Store(Values.Default(Type));
    }

    private void Store(object value)
    {
        if (_storage is null) _value = value;
        else Bytes.Encode(Type, value, _storage, _offset);
    }

    /// <summary>Assigns as EVAL does: converted to this type, padded or truncated; an array takes a value in every element.</summary>
    public void Set(object value, bool halfAdjust = false, bool right = false)
    {
        if (_basedOn is not null)
        {
            Based().Set(value, halfAdjust, right);
            return;
        }
        if (Type.Varying is not null && value is Cell { Elements: { } whole }) Count = Math.Min(whole.Length, Type.Dim);
        if (Elements is { } elements)
        {
            if (value is Cell { Elements: { } source })
            {
                for (var i = 0; i < Math.Min(source.Length, elements.Length); i++) elements[i].Set(source[i].Get());
            }
            else
            {
                foreach (var e in elements) e.Set(value, halfAdjust, right);
            }
            return;
        }
        if (Type.Kind == RpgKind.Ds)
        {
            var ds = Ds;
            switch (value)
            {
                case DsValue other:
                    // A structure is moved as its bytes, padded with blanks.
                    ds.Fill(other.Text);
                    foreach (var (at, pointer) in other.Storage.Pointers.Where(p => p.Key >= other.Offset && p.Key < other.Offset + other.Layout.Length).ToList())
                        ds.Storage.Pointers[ds.Offset + at - other.Offset] = pointer;
                    break;
                case SpecialValue { Name: "*BLANK" or "*BLANKS" }:
                    ds.Fill("");
                    break;
                case SpecialValue { Name: "*ALL", All: var all }:
                    ds.Fill(string.Concat(Enumerable.Repeat(all is { Length: > 0 } a ? a : " ", ds.Layout.Length)));
                    break;
                case SpecialValue { Name: "*HIVAL" or "*LOVAL" } special:
                    ds.Fill(new string(special.Name == "*HIVAL" ? Ebcdic.ToChar(0xff) : '\0', ds.Layout.Length));
                    break;
                case SpecialValue:
                    foreach (var c in ds.Data) c.Set(value);
                    break;
                default:
                    ds.Fill(right ? Values.ToText(value).PadLeft(ds.Layout.Length) : Values.ToText(value));
                    break;
            }
            return;
        }
        Store(Values.Convert(value, Type, halfAdjust, right));
    }
}

/// <summary>Values as bytes in storage, laid out as on an IBM i (one character a byte, in CCSID 37).</summary>
internal static class Bytes
{
    private static byte B(char c) => Ebcdic.ToByte(c);
    private static char C(byte b) => Ebcdic.ToChar(b);

    private static RpgError DataError() => new(907, "Decimal-data error occurred (digit or sign not valid).");

    public static void Encode(RpgType type, object value, Storage storage, int offset)
    {
        var bytes = storage.Bytes;
        void Put(ReadOnlySpan<byte> data)
        {
            for (var i = 0; i < data.Length && offset + i < bytes.Length; i++) bytes[offset + i] = C(data[i]);
        }
        switch (type.Kind)
        {
            case RpgKind.Char:
                storage.Put(offset, (string)value);
                break;
            case RpgKind.Varchar:
            {
                var s = (string)value;
                Span<byte> length = stackalloc byte[2];
                BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)s.Length);
                Put(length);
                storage.Put(offset + 2, s.PadRight(type.Length));
                break;
            }
            case RpgKind.Ind:
                bytes[offset] = (bool)value ? '1' : '0';
                break;
            case RpgKind.Date or RpgKind.Time or RpgKind.Timestamp:
                storage.Put(offset, Values.ToText(value));
                break;
            case RpgKind.Float:
            {
                Span<byte> data = stackalloc byte[type.Length == 4 ? 4 : 8];
                if (type.Length == 4) BinaryPrimitives.WriteSingleBigEndian(data, (float)(double)value);
                else BinaryPrimitives.WriteDoubleBigEndian(data, (double)value);
                Put(data);
                break;
            }
            case RpgKind.Int:
                Put(Integer((decimal)value, Values.ByteLength(type), type.Unsigned));
                break;
            case RpgKind.Decimal:
            {
                var d = (decimal)value;
                var digits = decimal.Truncate(Math.Abs(d) * Values.Pow10(type.Decimals)).ToString(CultureInfo.InvariantCulture);
                switch (type.Form)
                {
                    case 'S':
                    {
                        digits = digits.PadLeft(type.Length, '0')[^type.Length..];
                        var data = digits.Select(c => (byte)(0xf0 | c - '0')).ToArray();
                        if (d < 0 && data.Length > 0) data[^1] = (byte)(0xd0 | data[^1] & 0x0f);
                        Put(data);
                        break;
                    }
                    case 'B':
                        Put(Integer(decimal.Truncate(d * Values.Pow10(type.Decimals)), Values.ByteLength(type), false));
                        break;
                    default:
                    {
                        var size = type.Length / 2 + 1;
                        var nibbles = digits.PadLeft(size * 2 - 1, '0')[^(size * 2 - 1)..] + (d < 0 ? 'D' : 'F');
                        Put(Convert.FromHexString(nibbles));
                        break;
                    }
                }
                break;
            }
            case RpgKind.Pointer:
            {
                var pointer = (Pointer)value;
                storage.Put(offset, new string('\0', 16));
                if (pointer.IsNull) storage.Pointers.Remove(offset);
                else storage.Pointers[offset] = pointer;
                break;
            }
            default:
                throw new InvalidOperationException($"a {type.Kind} can't be stored as bytes");
        }
    }

    private static byte[] Integer(decimal value, int size, bool unsigned)
    {
        var data = new byte[size];
        var n = (long)decimal.Truncate(unsigned ? Math.Max(value, 0) : value);
        switch (size)
        {
            case 1: data[0] = (byte)n; break;
            case 2: BinaryPrimitives.WriteInt16BigEndian(data, (short)n); break;
            case 4: BinaryPrimitives.WriteInt32BigEndian(data, (int)n); break;
            default:
                if (unsigned) BinaryPrimitives.WriteUInt64BigEndian(data, (ulong)decimal.Truncate(value));
                else BinaryPrimitives.WriteInt64BigEndian(data, n);
                break;
        }
        return data;
    }

    public static object Decode(RpgType type, Storage storage, int offset)
    {
        var bytes = storage.Bytes;
        byte[] Take(int count)
        {
            var data = new byte[count];
            for (var i = 0; i < count; i++) data[i] = offset + i < bytes.Length ? B(bytes[offset + i]) : (byte)0x40;
            return data;
        }
        switch (type.Kind)
        {
            case RpgKind.Char:
                return storage.Text(offset, type.Length).PadRight(type.Length);
            case RpgKind.Varchar:
            {
                var length = BinaryPrimitives.ReadUInt16BigEndian(Take(2));
                return storage.Text(offset + 2, Math.Min(length, type.Length));
            }
            case RpgKind.Ind:
                return bytes[offset] == '1';
            case RpgKind.Date:
                return Values.ParseDate(storage.Text(offset, 10), "*ISO");
            case RpgKind.Time:
                return Values.ParseTime(storage.Text(offset, 8));
            case RpgKind.Timestamp:
                return Values.ParseTimestamp(storage.Text(offset, 26));
            case RpgKind.Float:
                return type.Length == 4 ? BinaryPrimitives.ReadSingleBigEndian(Take(4)) : BinaryPrimitives.ReadDoubleBigEndian(Take(8));
            case RpgKind.Int:
            {
                var data = Take(Values.ByteLength(type));
                return (data.Length, type.Unsigned) switch
                {
                    (1, true) => data[0],
                    (1, false) => (sbyte)data[0],
                    (2, true) => BinaryPrimitives.ReadUInt16BigEndian(data),
                    (2, false) => BinaryPrimitives.ReadInt16BigEndian(data),
                    (4, true) => BinaryPrimitives.ReadUInt32BigEndian(data),
                    (4, false) => BinaryPrimitives.ReadInt32BigEndian(data),
                    (_, true) => BinaryPrimitives.ReadUInt64BigEndian(data),
                    _ => (decimal)BinaryPrimitives.ReadInt64BigEndian(data),
                };
            }
            case RpgKind.Decimal:
            {
                decimal unscaled;
                switch (type.Form)
                {
                    case 'S':
                    {
                        var data = Take(type.Length);
                        unscaled = 0;
                        // Every digit but the last has zone F; the last's zone is its sign (A to F).
                        for (var i = 0; i < data.Length; i++)
                        {
                            var b = data[i];
                            if ((b & 0x0f) > 9 || (i < data.Length - 1 ? b >> 4 != 0xf : b >> 4 < 0xa)) throw DataError();
                            unscaled = unscaled * 10 + (b & 0x0f);
                        }
                        if (data.Length > 0 && (data[^1] >> 4) is 0xd or 0xb) unscaled = -unscaled;
                        break;
                    }
                    case 'B':
                    {
                        var data = Take(Values.ByteLength(type));
                        unscaled = data.Length switch
                        {
                            2 => BinaryPrimitives.ReadInt16BigEndian(data),
                            4 => BinaryPrimitives.ReadInt32BigEndian(data),
                            _ => BinaryPrimitives.ReadInt64BigEndian(data),
                        };
                        break;
                    }
                    default:
                    {
                        var hex = Convert.ToHexString(Take(type.Length / 2 + 1));
                        var digits = hex[..^1];
                        if (!digits.All(char.IsAsciiDigit) || hex[^1] is < 'A') throw DataError();
                        unscaled = decimal.Parse(digits, CultureInfo.InvariantCulture);
                        if (hex[^1] is 'D' or 'B') unscaled = -unscaled;
                        break;
                    }
                }
                return Values.WithScale(unscaled / Values.Pow10(type.Decimals), type.Decimals);
            }
            case RpgKind.Pointer:
                return storage.Pointers.TryGetValue(offset, out var pointer) ? pointer : Pointer.Null;
            default:
                throw new InvalidOperationException($"a {type.Kind} can't be read from bytes");
        }
    }
}

internal static class Values
{
    public static object Default(RpgType type) => type.Kind switch
    {
        RpgKind.Char => new string(' ', type.Length),
        RpgKind.Varchar => "",
        RpgKind.Decimal => WithScale(0m, type.Decimals),
        RpgKind.Int => 0m,
        RpgKind.Float => 0d,
        RpgKind.Ind => false,
        RpgKind.Date => new DateOnly(1, 1, 1),
        RpgKind.Time => new TimeOnly(0, 0, 0),
        RpgKind.Timestamp => new DateTime(1, 1, 1),
        RpgKind.Pointer => Pointer.Null,
        _ => throw new InvalidOperationException(),
    };

    /// <summary>How many bytes the type takes (one element of an array), as %SIZE says.</summary>
    public static int ByteLength(RpgType type) => type.Kind switch
    {
        RpgKind.Ds => type.Ds!.Length,
        RpgKind.Date => 10,
        RpgKind.Time => 8,
        RpgKind.Timestamp => 26,
        RpgKind.Ind => 1,
        RpgKind.Varchar => type.Length + (type.Length > 65535 ? 4 : 2),
        RpgKind.Int => type.Length switch { 3 => 1, 5 => 2, 10 => 4, _ => 8 },
        RpgKind.Float => type.Length == 4 ? 4 : 8,
        RpgKind.Pointer => 16,
        RpgKind.Decimal => type.Form switch
        {
            'S' => type.Length,
            'B' => type.Length <= 4 ? 2 : type.Length <= 9 ? 4 : 8,
            _ => type.Length / 2 + 1,
        },
        _ => type.Length,
    };

    /// <summary>The decimal with exactly this many decimal places, so %CHAR and the like show them.</summary>
    public static decimal WithScale(decimal value, int scale) =>
        decimal.Round(value, scale, MidpointRounding.ToZero) + new decimal(0, 0, 0, false, (byte)scale);

    /// <summary>Digits read from a data structure's characters, with the type's implied decimal point.</summary>
    public static decimal Scaled(decimal digits, RpgType type) => type.Kind == RpgKind.Decimal && digits == Math.Truncate(digits)
        ? digits / Pow10(type.Decimals)
        : digits;

    public static decimal Pow10(int n)
    {
        var result = 1m;
        for (var i = 0; i < n; i++) result *= 10;
        return result;
    }

    public static bool IsNumber(object v) => v is decimal or double;

    public static decimal ToDecimal(object v) => v switch
    {
        decimal d => d,
        double f => double.IsFinite(f) && Math.Abs(f) < 7.9e28 ? (decimal)f : throw new RpgError(103, "The target for a numeric operation is too small to hold the result."),
        bool b => b ? 1 : 0,
        string s => ParseNumber(s),
        SpecialValue { Name: "*ZERO" or "*ZEROS" } => 0,
        _ => throw new RpgError(105, "A character representation of a numeric value is in error."),
    };

    /// <summary>A number from characters, as %DEC and %INT read them: blanks ignored, a sign either end, a point or comma.</summary>
    public static decimal ParseNumber(string text)
    {
        var s = text.Replace(" ", "");
        if (s.Length == 0) return 0;
        var negative = s.StartsWith('-') || s.EndsWith('-');
        s = s.Trim('-', '+').Replace(',', '.');
        if (!decimal.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n))
            throw new RpgError(105, "A character representation of a numeric value is in error.");
        return negative ? -n : n;
    }

    public static string ToText(object v) => v switch
    {
        string s => s,
        bool b => b ? "1" : "0",
        decimal d => NumberText(d),
        double f => f.ToString("E", CultureInfo.InvariantCulture),
        DateOnly d => FormatDate(d, "*ISO"),
        TimeOnly t => FormatTime(t, "*ISO"),
        DateTime t => t.ToString("yyyy-MM-dd-HH.mm.ss.ffffff", CultureInfo.InvariantCulture),
        DsValue ds => ds.Text,
        Cell { Elements: { } e } => string.Concat(e.Select(c => ToText(c.Get()))),
        SpecialValue { Name: "*BLANK" or "*BLANKS" } => "",
        SpecialValue { Name: "*ON" } => "1",
        SpecialValue { Name: "*OFF" } => "0",
        SpecialValue { Name: "*ALL", All: { } all } => all,
        _ => throw new RpgError(100, "this value has no character form"),
    };

    /// <summary>A number as %CHAR shows it: every decimal place, no leading zero before the point.</summary>
    public static string NumberText(decimal d)
    {
        var text = d.ToString(CultureInfo.InvariantCulture);
        if (text.StartsWith("0.")) return text[1..];
        return text.StartsWith("-0.") ? "-" + text[2..] : text;
    }

    public static bool ToBool(object v) => v switch
    {
        bool b => b,
        string s => s.TrimEnd() == "1",
        SpecialValue { Name: "*ON" } => true,
        SpecialValue { Name: "*OFF" or "*BLANK" or "*BLANKS" or "*ZERO" or "*ZEROS" } => false,
        decimal d => d != 0,
        _ => throw new RpgError(100, "this value isn't an indicator"),
    };

    public static object Convert(object value, RpgType type, bool halfAdjust = false, bool right = false)
    {
        if (type.Kind == RpgKind.Pointer)
        {
            return value switch
            {
                Pointer p => p,
                SpecialValue { Name: "*NULL" } => Pointer.Null,
                _ => throw new RpgError(100, "a pointer can only be given a pointer or *NULL"),
            };
        }
        if (value is Pointer) throw new RpgError(100, "a pointer can only be assigned to a pointer");
        if (value is SpecialValue special) return FromSpecial(special, type);
        if (value is Cell or Duration) throw new RpgError(100, "the value and the variable it's assigned to don't match");
        switch (type.Kind)
        {
            case RpgKind.Char:
            {
                var s = ToText(value);
                if (right) return s.Length >= type.Length ? s[^type.Length..] : s.PadLeft(type.Length);
                return s.Length >= type.Length ? s[..type.Length] : s.PadRight(type.Length);
            }
            case RpgKind.Varchar:
            {
                var s = ToText(value);
                return s.Length > type.Length ? s[..type.Length] : s;
            }
            case RpgKind.Decimal:
            {
                var d = decimal.Round(ToDecimal(value), type.Decimals, halfAdjust ? MidpointRounding.AwayFromZero : MidpointRounding.ToZero);
                if (Math.Abs(decimal.Truncate(d)) >= Pow10(type.Length - type.Decimals))
                    throw new RpgError(103, "The target for a numeric operation is too small to hold the result.");
                return WithScale(d, type.Decimals);
            }
            case RpgKind.Int:
            {
                var d = decimal.Round(ToDecimal(value), 0, halfAdjust ? MidpointRounding.AwayFromZero : MidpointRounding.ToZero);
                var (low, high) = IntRange(type);
                if (d < low || d > high) throw new RpgError(103, "The target for a numeric operation is too small to hold the result.");
                return d;
            }
            case RpgKind.Float:
                return value is double f ? f : (double)ToDecimal(value);
            case RpgKind.Ind:
                return ToBool(value);
            case RpgKind.Date:
                return value switch
                {
                    DateOnly d => d,
                    DateTime t => DateOnly.FromDateTime(t),
                    string s => ParseDate(s, "*ISO"),
                    _ => throw new RpgError(112, "Date, Time or Timestamp value is not valid."),
                };
            case RpgKind.Time:
                return value switch
                {
                    TimeOnly t => t,
                    DateTime t => TimeOnly.FromDateTime(t),
                    string s => ParseTime(s),
                    _ => throw new RpgError(112, "Date, Time or Timestamp value is not valid."),
                };
            case RpgKind.Timestamp:
                return value switch
                {
                    DateTime t => t,
                    DateOnly d => d.ToDateTime(TimeOnly.MinValue),
                    string s => ParseTimestamp(s),
                    _ => throw new RpgError(112, "Date, Time or Timestamp value is not valid."),
                };
            default:
                throw new InvalidOperationException();
        }
    }

    private static (decimal Low, decimal High) IntRange(RpgType type) => (type.Length, type.Unsigned) switch
    {
        (3, false) => (sbyte.MinValue, sbyte.MaxValue),
        (3, true) => (0, byte.MaxValue),
        (5, false) => (short.MinValue, short.MaxValue),
        (5, true) => (0, ushort.MaxValue),
        (10, false) => (int.MinValue, int.MaxValue),
        (10, true) => (0, uint.MaxValue),
        (_, false) => (long.MinValue, long.MaxValue),
        _ => (0, ulong.MaxValue),
    };

    private static object FromSpecial(SpecialValue special, RpgType type)
    {
        var high = special.Name == "*HIVAL";
        switch (special.Name)
        {
            case "*BLANK" or "*BLANKS":
                return type.Kind switch
                {
                    RpgKind.Char or RpgKind.Varchar or RpgKind.Ind => Default(type),
                    _ => throw new RpgError(100, "*BLANKS is for character variables"),
                };
            case "*ZERO" or "*ZEROS":
                return type.Kind == RpgKind.Char ? new string('0', type.Length) : type.Kind == RpgKind.Varchar ? "" : Default(type);
            case "*ON" or "*OFF":
                var on = special.Name == "*ON";
                return type.Kind switch
                {
                    RpgKind.Ind => on,
                    RpgKind.Char => new string(on ? '1' : '0', type.Length),
                    _ => throw new RpgError(100, $"{special.Name} is for indicators"),
                };
            case "*ALL":
                var all = special.All is { Length: > 0 } a ? a : " ";
                var repeated = string.Concat(Enumerable.Repeat(all, type.Length / all.Length + 1))[..Math.Max(type.Length, 0)];
                return type.IsNumeric ? Convert(ParseNumber(repeated), type) : Convert(repeated, type);
            case "*HIVAL" or "*LOVAL":
                return type.Kind switch
                {
                    RpgKind.Char or RpgKind.Varchar => new string(high ? Ebcdic.ToChar(0xff) : '\0', type.Length),
                    RpgKind.Decimal => WithScale((high ? 1 : -1) * (Pow10(type.Length) - 1) / Pow10(type.Decimals), type.Decimals),
                    RpgKind.Int => high ? IntRange(type).High : IntRange(type).Low,
                    RpgKind.Float => high ? double.MaxValue : double.MinValue,
                    RpgKind.Date => high ? new DateOnly(9999, 12, 31) : new DateOnly(1, 1, 1),
                    RpgKind.Time => high ? new TimeOnly(23, 59, 59) : new TimeOnly(0, 0, 0),
                    RpgKind.Timestamp => high ? new DateTime(9999, 12, 31, 23, 59, 59) : new DateTime(1, 1, 1),
                    _ => high,
                };
            case "*SYS":
                return type.Kind switch
                {
                    RpgKind.Date => DateOnly.FromDateTime(DateTime.Now),
                    RpgKind.Time => TimeOnly.FromDateTime(DateTime.Now),
                    RpgKind.Timestamp => DateTime.Now,
                    _ => throw new RpgError(100, "*SYS is for dates, times and timestamps"),
                };
            default:
                throw new RpgError(100, $"{special.Name} isn't supported as a value");
        }
    }

    /// <summary>Compares as RPG does: numbers by value, characters padded with blanks and in EBCDIC order.</summary>
    public static int Compare(object a, object b)
    {
        if (a is SpecialValue { Name: "*NULL" }) a = Pointer.Null;
        if (b is SpecialValue { Name: "*NULL" }) b = Pointer.Null;
        if (a is SpecialValue sa && b is not SpecialValue) return -Compare(b, sa);
        if (b is SpecialValue special)
        {
            b = a switch
            {
                string s => FromSpecial(special, RpgType.Char(Math.Max(s.Length, 1))),
                bool => FromSpecial(special, RpgType.Indicator),
                decimal => special.Name is "*HIVAL" ? decimal.MaxValue : special.Name is "*LOVAL" ? decimal.MinValue : FromSpecial(special, RpgType.Number(28, 0)),
                double => FromSpecial(special, new RpgType(RpgKind.Float, 8)),
                DateOnly => FromSpecial(special, new RpgType(RpgKind.Date)),
                TimeOnly => FromSpecial(special, new RpgType(RpgKind.Time)),
                DateTime => FromSpecial(special, new RpgType(RpgKind.Timestamp)),
                _ => throw new RpgError(100, "these values can't be compared"),
            };
        }
        if (a is Pointer || b is Pointer)
        {
            var (p, q) = (a as Pointer ?? Pointer.Null, b as Pointer ?? Pointer.Null);
            if (p.Target != q.Target)
                return p.IsNull ? -1 : q.IsNull ? 1 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(p.Target!) < System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(q.Target!) ? -1 : 1;
            return p.Offset.CompareTo(q.Offset);
        }
        if (a is bool || b is bool) (a, b) = (a is bool x ? (x ? "1" : "0") : a, b is bool y ? (y ? "1" : "0") : b);
        if (IsNumber(a) && IsNumber(b))
            return a is double || b is double ? System.Convert.ToDouble(a).CompareTo(System.Convert.ToDouble(b)) : ((decimal)a).CompareTo((decimal)b);
        if (a is DsValue or Cell) a = ToText(a);
        if (b is DsValue or Cell) b = ToText(b);
        if (a is string s1 && b is string s2)
        {
            var length = Math.Max(s1.Length, s2.Length);
            return Ebcdic.Encode(s1.PadRight(length)).AsSpan().SequenceCompareTo(Ebcdic.Encode(s2.PadRight(length)));
        }
        if (a.GetType() == b.GetType() && a is IComparable c) return c.CompareTo(b);
        if (a is DateTime t1 && b is DateOnly d2) return t1.CompareTo(d2.ToDateTime(TimeOnly.MinValue));
        if (a is DateOnly d1 && b is DateTime t2) return d1.ToDateTime(TimeOnly.MinValue).CompareTo(t2);
        throw new RpgError(100, "these values can't be compared");
    }

    // ---- dates and times ----

    private static readonly Dictionary<string, string> DateFormats = new()
    {
        ["*ISO"] = "yyyy-MM-dd", ["*USA"] = "MM/dd/yyyy", ["*EUR"] = "dd.MM.yyyy", ["*JIS"] = "yyyy-MM-dd",
        ["*MDY"] = "MM/dd/yy", ["*DMY"] = "dd/MM/yy", ["*YMD"] = "yy/MM/dd", ["*JUL"] = "yy/ddd", ["*LONGJUL"] = "yyyy/ddd",
        ["*CYMD"] = "cyy/MM/dd", ["*CMDY"] = "cMM/dd/yy", ["*CDMY"] = "cdd/MM/yy",
    };

    /// <summary>The .NET pattern for an RPG date format, such as *USA or *ISO0 (a 0 on the end: no separators).</summary>
    private static string DatePattern(string format)
    {
        var (name, separator) = SplitFormat(format);
        if (!DateFormats.TryGetValue(name, out var pattern)) throw new RpgError(100, $"the date format {format} isn't supported");
        return separator switch
        {
            null => pattern,
            '0' => pattern.Replace("-", "").Replace("/", "").Replace(".", ""),
            char c => pattern.Replace('-', c).Replace('/', c).Replace('.', c),
        };
    }

    /// <summary>A date or time format and its separator: *USA/ is *USA and /, *ISO0 *ISO and none (0), & a blank.</summary>
    public static (string Name, char? Separator) SplitFormat(string format) =>
        format.Length > 2 && format[^1] is '0' or '/' or '-' or '.' or ',' or '&' or ':'
            ? (format[..^1], format[^1] == '&' ? ' ' : format[^1])
            : (format, null);

    public static string FormatDate(DateOnly date, string format)
    {
        var pattern = DatePattern(format);
        if (pattern.Contains("ddd"))
        {
            var year = pattern.StartsWith("yyyy") ? date.Year.ToString("0000") : (date.Year % 100).ToString("00");
            return year + (pattern.Contains('/') ? "/" : "") + date.DayOfYear.ToString("000");
        }
        var century = pattern.StartsWith('c') ? ((date.Year - 1900) / 100).ToString() : "";
        return century + date.ToString(pattern.TrimStart('c'), CultureInfo.InvariantCulture);
    }

    public static DateOnly ParseDate(string text, string format)
    {
        var s = text.Trim();
        var pattern = DatePattern(format);
        if (pattern.StartsWith('c'))
        {
            // c is the century digit: 0 is 19xx, 1 is 20xx.
            var digits = new string(s.Where(char.IsDigit).ToArray());
            if (digits.Length == 7)
            {
                var order = SplitFormat(format).Name;
                var (yy, mm, dd) = order switch
                {
                    "*CYMD" => (digits[1..3], digits[3..5], digits[5..7]),
                    "*CMDY" => (digits[5..7], digits[1..3], digits[3..5]),
                    _ => (digits[5..7], digits[3..5], digits[1..3]),
                };
                var iso = $"{1900 + (digits[0] - '0') * 100 + int.Parse(yy, CultureInfo.InvariantCulture):0000}-{mm}-{dd}";
                if (DateOnly.TryParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var c)) return c;
            }
            throw new RpgError(112, "Date, Time or Timestamp value is not valid.");
        }
        if (pattern.Contains("ddd"))
        {
            var digits = new string(s.Where(char.IsDigit).ToArray());
            var yearLength = pattern.StartsWith("yyyy") ? 4 : 2;
            if (digits.Length == yearLength + 3)
            {
                var year = int.Parse(digits[..yearLength], CultureInfo.InvariantCulture);
                if (yearLength == 2) year += year >= 40 ? 1900 : 2000;
                var day = int.Parse(digits[yearLength..], CultureInfo.InvariantCulture);
                if (day >= 1 && day <= (DateTime.IsLeapYear(year) ? 366 : 365)) return new DateOnly(year, 1, 1).AddDays(day - 1);
            }
            throw new RpgError(112, "Date, Time or Timestamp value is not valid.");
        }
        // Two-digit years: 40 to 99 are 19xx, as RPG has it.
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.Calendar.TwoDigitYearMax = 2039;
        if (DateOnly.TryParseExact(s, pattern, culture, DateTimeStyles.None, out var date)) return date;
        // Any separator will do, as will ISO whatever the format.
        var loose = pattern.Replace("/", "-").Replace(".", "-");
        if (DateOnly.TryParseExact(s.Replace("/", "-").Replace(".", "-"), loose, culture, DateTimeStyles.None, out date)) return date;
        if (DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return date;
        throw new RpgError(112, "Date, Time or Timestamp value is not valid.");
    }

    public static string FormatTime(TimeOnly time, string format)
    {
        var (name, separator) = SplitFormat(format);
        if (separator is null or '0' || name == "*USA") return FormatTimeIn(time, format);
        return FormatTimeIn(time, name).Replace(':', separator.Value).Replace('.', separator.Value);
    }

    private static string FormatTimeIn(TimeOnly time, string format) => format switch
    {
        "*ISO" or "*EUR" => time.ToString("HH.mm.ss", CultureInfo.InvariantCulture),
        "*HMS" or "*JIS" => time.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        "*USA" => time.ToString("hh:mm tt", CultureInfo.InvariantCulture),
        "*ISO0" or "*HMS0" or "*EUR0" or "*JIS0" => time.ToString("HHmmss", CultureInfo.InvariantCulture),
        _ => throw new RpgError(100, $"the time format {format} isn't supported"),
    };

    public static TimeOnly ParseTime(string text)
    {
        var s = text.Trim().Replace('.', ':');
        if (s.Length == 6 && s.All(char.IsDigit)) s = $"{s[..2]}:{s[2..4]}:{s[4..]}";
        return TimeOnly.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time
            : throw new RpgError(112, "Date, Time or Timestamp value is not valid.");
    }

    public static DateTime ParseTimestamp(string text)
    {
        var s = text.Trim();
        string[] patterns =
        [
            "yyyy-MM-dd-HH.mm.ss.ffffff", "yyyy-MM-dd-HH.mm.ss", "yyyy-MM-dd HH:mm:ss.ffffff", "yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd", "yyyyMMddHHmmss", "yyyyMMddHHmmssffffff",
        ];
        return DateTime.TryParseExact(s, patterns, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t
            : throw new RpgError(112, "Date, Time or Timestamp value is not valid.");
    }

    // ---- edit codes ----

    /// <summary>
    /// A number edited with an edit code, as %EDITC and a display file's EDTCDE show it:
    /// 1 to 4, A to D (CR), J to M (trailing minus), N to Q (leading minus), X, Y and Z.
    /// </summary>
    public static string Edit(decimal value, int digits, int decimals, char code)
    {
        code = char.ToUpperInvariant(code);
        var negative = value < 0;
        var unscaled = decimal.Truncate(Math.Abs(value) * Pow10(decimals)).ToString(CultureInfo.InvariantCulture);
        var all = unscaled.Length > digits ? unscaled[^digits..] : unscaled.PadLeft(digits, '0');
        switch (code)
        {
            case 'X':
                return all;
            case 'Z':
                return all.TrimStart('0').PadLeft(digits);
            case 'Y':
            {
                var d = all.PadLeft(digits <= 6 ? 6 : 8, '0');
                var text = d.Length == 6 ? $"{d[..2]}/{d[2..4]}/{d[4..]}" : $"{d[..2]}/{d[2..4]}/{d[4..]}";
                return text[0] == '0' ? " " + text[1..] : text;
            }
        }
        var group = "1AJN2BKO".Contains(code);
        var showZero = "1AJN3CLP".Contains(code);
        var sign = "ABCD".Contains(code) ? "CR" : "JKLM".Contains(code) ? "-" : "";
        var leading = "NOPQ".Contains(code);
        if (!group && !showZero && !"4DMQ".Contains(code)) throw new RpgError(100, $"the edit code {code} isn't supported");
        var integerDigits = digits - decimals;
        var width = integerDigits + (group ? Math.Max(0, (integerDigits - 1) / 3) : 0) + (decimals > 0 ? decimals + 1 : 0) + sign.Length + (leading ? 1 : 0);
        if (value == 0 && !showZero) return new string(' ', width);
        var whole = all[..integerDigits].TrimStart('0');
        if (group)
        {
            for (var i = whole.Length - 3; i > 0; i -= 3) whole = whole.Insert(i, ",");
        }
        if (whole.Length == 0 && decimals == 0) whole = "0";
        var body = whole + (decimals > 0 ? "." + all[integerDigits..] : "");
        if (leading && negative) body = "-" + body;
        body += sign.Length == 0 ? "" : negative ? sign : new string(' ', sign.Length);
        return body.PadLeft(width);
    }
}
