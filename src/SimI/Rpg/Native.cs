// Procedures a prototype names with EXTPROC that aren't RPG: the integrated file system's C APIs
// (open, read, write, close, lseek, unlink, stat, mkdir, opendir ...), errno and strerror, sleep,
// system and getenv. They work on the machine's IFS folder, with IBM i's flag and errno values and
// structure layouts. A file opened with O_TEXTDATA is read and written as text (UTF-8 in the folder,
// characters in the program); any other as bytes.

using System.Buffers.Binary;
using System.Text;

namespace SimI.Rpg;

/// <summary>A stream file a job has open: its stream, and whether it's read and written as text.</summary>
internal sealed class StreamHandle(FileStream stream, bool text, bool append) : IDisposable
{
    public FileStream Stream { get; } = stream;
    public bool Text { get; } = text;
    public bool Append { get; } = append;
    public Decoder Decoder { get; } = new UTF8Encoding(false).GetDecoder();

    public void Dispose() => Stream.Dispose();
}

internal sealed partial class Interpreter
{
    /// <summary>The procedures Sim i has for EXTPROC, by their C name.</summary>
    internal static readonly HashSet<string> NativeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "open", "open64", "read", "write", "close", "lseek", "lseek64", "unlink", "access", "mkdir", "rmdir", "rename",
        "Qp0lRenameKeep", "Qp0lRenameUnlink", "__errno", "strerror", "sleep", "usleep", "system", "getenv", "stat", "stat64",
        "lstat", "lstat64", "opendir", "readdir", "closedir",
    };

    // IBM i's open flags and errno values.
    private const int ORdOnly = 1, OWrOnly = 2, ORdWr = 4, OCreat = 8, OExcl = 16, OTrunc = 64, OAppend = 256, OTextData = 16777216;
    private const int ENoEnt = 3025, EAcces = 3401, ENotDir = 3403, EExist = 3441, EBadF = 3450, EInval = 3021, EIsDir = 3529, ENotEmpty = 3534, EIo = 3101;

    private static readonly Dictionary<int, string> ErrorTexts = new()
    {
        [ENoEnt] = "No such path or directory.", [EAcces] = "Permission denied.", [ENotDir] = "Specified target is not a directory.",
        [EExist] = "File exists.", [EBadF] = "Descriptor not valid.", [EInval] = "The value specified for the argument is not correct.",
        [EIsDir] = "Specified target is a directory.", [ENotEmpty] = "Directory not empty.", [EIo] = "Input/output error.",
    };

    /// <summary>Calls a native procedure: its arguments are evaluated as its prototype says, and its result converted to the prototype's return type.</summary>
    private object? CallNative(string name, ProtoDecl prototype, IReadOnlyList<Expr> arguments, Frame frame)
    {
        var args = new List<object>();
        for (var i = 0; i < arguments.Count; i++)
        {
            var parameter = i < prototype.Params.Count ? prototype.Params[i] : null;
            if (parameter is null || parameter.ByValue || parameter.Const)
            {
                var value = Evaluate(arguments[i], frame);
                // OPTIONS(*STRING): characters passed where a pointer is wanted go as a null-ended string.
                if (value is string text && parameter?.Type.Keyword == "POINTER") value = StringPointer(text);
                args.Add(value);
            }
            else
            {
                // By reference: the variable's place, as a pointer.
                var (storage, offset) = Locate(arguments[i], frame).Pin();
                args.Add(new Pointer(storage, offset));
            }
        }
        var result = Native(name.ToLowerInvariant(), args);
        if (prototype.Returns is null || result is null) return null;
        var cell = new Cell(Resolve(prototype.Returns, frame));
        cell.Set(result);
        return cell.Get();
    }

    private static Pointer StringPointer(string text)
    {
        var storage = new Storage(text.Length + 1);
        storage.Put(0, text + '\0');
        return new Pointer(storage, 0);
    }

    /// <summary>The characters a pointer points to, up to the null (or <paramref name="max"/>).</summary>
    internal static string CString(Pointer pointer, int max = int.MaxValue)
    {
        if (pointer.Target is not { } storage) throw new RpgError(222, "Pointer or parameter error: the pointer is *NULL.");
        var end = pointer.Offset;
        while (end < storage.Bytes.Length && end - pointer.Offset < max && storage.Bytes[end] != '\0') end++;
        return new string(storage.Bytes, pointer.Offset, end - pointer.Offset);
    }

    private int _errno;

    private int Fail(int errno)
    {
        _errno = errno;
        Bytes.Encode(new RpgType(RpgKind.Int, 10), (decimal)errno, ErrnoStorage, 0);
        return -1;
    }

    private Storage ErrnoStorage => _host.State.ErrnoStorage;

    private object? Native(string name, List<object> args)
    {
        int Int(int i) => i < args.Count ? (int)Values.ToDecimal(args[i]) : 0;
        Pointer Ptr(int i) => i < args.Count && args[i] is Pointer p ? p : Pointer.Null;
        string Path(int i) => args[i] is string s ? s : CString(Ptr(i));
        var files = _host.State.StreamFiles;
        var ifs = _machine.Ifs;
        string? Host(string path)
        {
            try
            {
                var absolute = Ifs.Absolute(path, _host.State.CurrentDirectory);
                return Ifs.InQsys(absolute) ? null : ifs.HostPath(absolute);
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }
        switch (name)
        {
            case "open" or "open64":
            {
                var host = Host(Path(0));
                if (host is null) return (decimal)Fail(EAcces);
                var flags = Int(1);
                if (Directory.Exists(host)) return (decimal)Fail(EIsDir);
                var exists = File.Exists(host);
                if (!exists && (flags & OCreat) == 0) return (decimal)Fail(ENoEnt);
                if (exists && (flags & OCreat) != 0 && (flags & OExcl) != 0) return (decimal)Fail(EExist);
                if (!exists && !Directory.Exists(System.IO.Path.GetDirectoryName(host))) return (decimal)Fail(ENoEnt);
                var access = (flags & ORdWr) != 0 ? FileAccess.ReadWrite : (flags & OWrOnly) != 0 ? FileAccess.Write : FileAccess.Read;
                if ((flags & (ORdOnly | OWrOnly | ORdWr)) == 0) return (decimal)Fail(EInval);
                try
                {
                    var stream = new FileStream(host, exists ? FileMode.Open : FileMode.CreateNew, access, FileShare.ReadWrite);
                    if ((flags & OTrunc) != 0 && access != FileAccess.Read) stream.SetLength(0);
                    var fd = 0;
                    while (files.ContainsKey(fd)) fd++;
                    files[fd] = new StreamHandle(stream, (flags & OTextData) != 0, (flags & OAppend) != 0);
                    return (decimal)fd;
                }
                catch (IOException)
                {
                    return (decimal)Fail(EIo);
                }
                catch (UnauthorizedAccessException)
                {
                    return (decimal)Fail(EAcces);
                }
            }
            case "read":
            {
                if (!files.TryGetValue(Int(0), out var file) || !file.Stream.CanRead) return (decimal)Fail(EBadF);
                var target = Ptr(1);
                if (target.Target is not { } storage) return (decimal)Fail(EInval);
                var wanted = Math.Min(Int(2), storage.Bytes.Length - target.Offset);
                if (wanted <= 0) return 0m;
                var buffer = new byte[wanted];
                var read = file.Stream.Read(buffer, 0, wanted);
                if (!file.Text)
                {
                    for (var i = 0; i < read; i++) storage.Bytes[target.Offset + i] = Ebcdic.ToChar(buffer[i]);
                    return (decimal)read;
                }
                // Text: the characters, one a byte, as many as the bytes read make.
                var chars = new char[read];
                var count = file.Decoder.GetChars(buffer, 0, read, chars, 0, flush: false);
                Array.Copy(chars, 0, storage.Bytes, target.Offset, count);
                return (decimal)count;
            }
            case "write":
            {
                if (!files.TryGetValue(Int(0), out var file) || !file.Stream.CanWrite) return (decimal)Fail(EBadF);
                var source = Ptr(1);
                if (source.Target is not { } storage) return (decimal)Fail(EInval);
                var length = Math.Min(Int(2), storage.Bytes.Length - source.Offset);
                var chars = new string(storage.Bytes, source.Offset, Math.Max(0, length));
                var data = file.Text ? Encoding.UTF8.GetBytes(chars) : [.. chars.Select(Ebcdic.ToByte)];
                if (file.Append) file.Stream.Seek(0, SeekOrigin.End);
                file.Stream.Write(data);
                file.Stream.Flush();
                return (decimal)Math.Max(0, length);
            }
            case "close":
                if (!files.Remove(Int(0), out var closed)) return (decimal)Fail(EBadF);
                closed.Dispose();
                return 0m;
            case "lseek" or "lseek64":
            {
                if (!files.TryGetValue(Int(0), out var file)) return (decimal)Fail(EBadF);
                var origin = Int(2) switch { 1 => SeekOrigin.Current, 2 => SeekOrigin.End, _ => SeekOrigin.Begin };
                return (decimal)file.Stream.Seek(Int(1), origin);
            }
            case "unlink":
            {
                var host = Host(Path(0));
                if (host is null) return (decimal)Fail(EAcces);
                if (Directory.Exists(host)) return (decimal)Fail(EIsDir);
                if (!File.Exists(host)) return (decimal)Fail(ENoEnt);
                File.Delete(host);
                return 0m;
            }
            case "access":
            {
                var host = Host(Path(0));
                return host is not null && Path(0).Length > 0 && (File.Exists(host) || Directory.Exists(host)) ? 0m : (decimal)Fail(ENoEnt);
            }
            case "mkdir":
            {
                var host = Host(Path(0));
                if (host is null) return (decimal)Fail(EAcces);
                if (File.Exists(host) || Directory.Exists(host)) return (decimal)Fail(EExist);
                if (!Directory.Exists(System.IO.Path.GetDirectoryName(host))) return (decimal)Fail(ENoEnt);
                Directory.CreateDirectory(host);
                return 0m;
            }
            case "rmdir":
            {
                var host = Host(Path(0));
                if (host is null) return (decimal)Fail(EAcces);
                if (File.Exists(host)) return (decimal)Fail(ENotDir);
                if (!Directory.Exists(host)) return (decimal)Fail(ENoEnt);
                if (Directory.EnumerateFileSystemEntries(host).Any()) return (decimal)Fail(ENotEmpty);
                Directory.Delete(host);
                return 0m;
            }
            case "rename" or "qp0lrenamekeep" or "qp0lrenameunlink":
            {
                var (from, to) = (Host(Path(0)), Host(Path(1)));
                if (from is null || to is null) return (decimal)Fail(EAcces);
                if (!File.Exists(from) && !Directory.Exists(from)) return (decimal)Fail(ENoEnt);
                var exists = File.Exists(to) || Directory.Exists(to);
                if (exists && name != "qp0lrenameunlink") return (decimal)Fail(EExist);
                if (File.Exists(from)) File.Move(from, to, overwrite: true);
                else Directory.Move(from, to);
                return 0m;
            }
            case "__errno":
                return new Pointer(ErrnoStorage, 0);
            case "strerror":
                return StringPointer(ErrorTexts.GetValueOrDefault(Int(0), $"Error {Int(0)}."));
            case "sleep":
                Thread.Sleep(TimeSpan.FromSeconds(Math.Clamp(Int(0), 0, 3600)));
                return 0m;
            case "usleep":
                Thread.Sleep(TimeSpan.FromMilliseconds(Math.Clamp(Int(0) / 1000, 0, 3_600_000)));
                return 0m;
            case "system":
            {
                var result = _machine.Run(Path(0), _host, _depth + 1);
                return result.Ok ? 0m : 1m;
            }
            case "getenv":
                return Path(0).ToUpperInvariant() switch
                {
                    "HOME" => StringPointer($"/home/{_host.User}"),
                    "USER" or "LOGNAME" => StringPointer(_host.User),
                    "PWD" => StringPointer(_host.State.CurrentDirectory),
                    _ => Pointer.Null,
                };
            case "stat" or "stat64" or "lstat" or "lstat64":
            {
                var host = Host(Path(0));
                if (host is null) return (decimal)Fail(EAcces);
                var directory = Directory.Exists(host);
                if (!directory && !File.Exists(host)) return (decimal)Fail(ENoEnt);
                if (Ptr(1).Target is not { } storage) return (decimal)Fail(EInval);
                FileSystemInfo info = directory ? new DirectoryInfo(host) : new FileInfo(host);
                // struct stat: st_mode, st_ino, st_nlink, st_uid, st_gid, st_size, the times, st_objtype and st_ccsid.
                var at = Ptr(1).Offset;
                void Number(int offset, int digits, decimal value) => Bytes.Encode(new RpgType(RpgKind.Int, digits, Unsigned: true), value, storage, at + offset);
                storage.Put(at, new string('\0', Math.Min(128, storage.Bytes.Length - at)));
                Number(0, 10, directory ? 0x41ed : 0x81a4);
                Number(4, 10, (uint)host.GetHashCode());
                Number(8, 5, 1);
                Number(20, 10, directory ? 8192 : ((FileInfo)info).Length);
                var seconds = (decimal)new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds();
                Number(24, 10, (decimal)new DateTimeOffset(info.LastAccessTimeUtc).ToUnixTimeSeconds());
                Number(28, 10, seconds);
                Number(32, 10, seconds);
                Number(40, 10, 4096);
                storage.Put(at + 48, (directory ? "*DIR" : "*STMF").PadRight(11));
                Number(62, 5, 1208);
                return 0m;
            }
            case "opendir":
            {
                var host = Host(Path(0));
                if (host is null) return Pointer.Null.Also(() => Fail(EAcces));
                if (!Directory.Exists(host)) return Pointer.Null.Also(() => Fail(File.Exists(host) ? ENotDir : ENoEnt));
                var handle = new Storage(16);
                var names = new[] { ".", ".." }.Concat(Directory.EnumerateFileSystemEntries(host).Select(System.IO.Path.GetFileName).OfType<string>()).ToList();
                _directories[handle] = (names, 0, new Storage(696));
                return new Pointer(handle, 0);
            }
            case "readdir":
            {
                if (Ptr(0).Target is not { } handle || !_directories.TryGetValue(handle, out var dir)) return Pointer.Null.Also(() => Fail(EBadF));
                if (dir.Next >= dir.Names.Count) return Pointer.Null;
                var entry = dir.Entry;
                var entryName = dir.Names[dir.Next];
                _directories[handle] = dir with { Next = dir.Next + 1 };
                // struct dirent: d_namelen at 52 and d_name at 56, with the CCSID in d_nlsinfo at 40.
                entry.Put(0, new string('\0', entry.Bytes.Length));
                Bytes.Encode(new RpgType(RpgKind.Int, 10), 1208m, entry, 40);
                Bytes.Encode(new RpgType(RpgKind.Int, 10, Unsigned: true), (decimal)entryName.Length, entry, 52);
                entry.Put(56, entryName);
                return new Pointer(entry, 0);
            }
            case "closedir":
                return Ptr(0).Target is { } closing && _directories.Remove(closing) ? 0m : (decimal)Fail(EBadF);
            default:
                throw new RpgError(100, $"the procedure {name} isn't there");
        }
    }

    private readonly Dictionary<Storage, (List<string> Names, int Next, Storage Entry)> _directories = [];
}

internal static class PointerExtensions
{
    /// <summary>The pointer, after doing something (setting errno).</summary>
    public static Pointer Also(this Pointer pointer, Func<int> action)
    {
        action();
        return pointer;
    }
}
