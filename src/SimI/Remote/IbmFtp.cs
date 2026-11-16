// An FTP client for one job: talking to a real IBM i's FTP server, which runs CL commands (RCMD)
// and sends a file member's records as they are (binary: EBCDIC, packed and zoned numbers). It's
// how SAVRSTLIB and SAVRSTOBJ receive a library: the save files a real restore reads can't be
// read here, but the records and their descriptions can.

using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace SimI.Remote;

/// <summary>The remote system refused something, or can't be reached: its own words.</summary>
public sealed class RemoteException(string message) : Exception(message);

/// <summary>Whether the FTP session is encrypted: when the server offers it (*DFT), always (*SSL) or never (*NONE).</summary>
internal enum RemoteSecurity
{
    Default,
    Required,
    None,
}

internal sealed partial class IbmFtp : IDisposable
{
    /// <summary>The longest line an IBM i's FTP server takes.</summary>
    public const int MaxLine = 4096;

    private readonly string _host;
    private readonly TcpClient _control;
    private Stream _stream;
    private readonly byte[] _buffer = new byte[8192];
    private int _start, _end;

    private IbmFtp(string host, TcpClient control)
    {
        _host = host;
        _control = control;
        _stream = control.GetStream();
    }

    /// <summary>Whether the session, and so the password, is encrypted.</summary>
    public bool Encrypted { get; private set; }

    /// <summary>Signs on to the system's FTP server. The names of files are then paths (/QSYS.LIB/LIB.LIB/FILE.FILE/MBR.MBR), and transfers binary.</summary>
    public static IbmFtp Connect(string host, int port, string user, string password, RemoteSecurity security)
    {
        TcpClient control;
        try
        {
            control = new TcpClient { ReceiveTimeout = 30 * 60 * 1000, SendTimeout = 60 * 1000 };
            if (!control.ConnectAsync(host, port).Wait(TimeSpan.FromSeconds(20))) throw new RemoteException($"System {host} doesn't answer on port {port}.");
        }
        catch (Exception e) when (e is SocketException or AggregateException)
        {
            throw new RemoteException($"System {host} can't be reached on port {port}: {(e.InnerException ?? e).Message}");
        }
        var ftp = new IbmFtp(host, control);
        try
        {
            ftp.Expect(ftp.Read(), 220);
            if (security != RemoteSecurity.None)
            {
                var (code, text) = ftp.Send("AUTH TLS");
                if (code == 234)
                {
                    ftp._stream = ftp.Secure(ftp._stream);
                    ftp.Encrypted = true;
                }
                else if (security == RemoteSecurity.Required)
                {
                    throw new RemoteException($"System {host} doesn't take a secure connection (SECCNN(*SSL)): {text}");
                }
            }
            var (asked, why) = ftp.Send("USER " + user);
            if (asked == 331) (asked, why) = ftp.Send("PASS " + password);
            if (asked != 230) throw new RemoteException($"System {host} didn't sign {user} on: {why}");
            if (ftp.Encrypted)
            {
                ftp.Expect(ftp.Send("PBSZ 0"), 200);
                ftp.Expect(ftp.Send("PROT P"), 200);
            }
            ftp.Expect(ftp.Send("SITE NAMEFMT 1"), 250);
            ftp.Expect(ftp.Send("TYPE I"), 200);
            return ftp;
        }
        catch (Exception e) when (e is IOException or SocketException or System.Security.Authentication.AuthenticationException)
        {
            ftp.Dispose();
            throw new RemoteException($"The connection to {host} failed: {e.Message}");
        }
        catch
        {
            ftp.Dispose();
            throw;
        }
    }

    /// <summary>A member's path, as the FTP server names it.</summary>
    public static string Path(string library, string file, string member) => $"/QSYS.LIB/{library}.LIB/{file}.FILE/{member}.MBR";

    // The certificate isn't checked: a system's own is usually self-signed, and the choice is this or no encryption.
    private SslStream Secure(Stream stream)
    {
        var secure = new SslStream(stream, false, (_, _, _, _) => true);
        secure.AuthenticateAsClient(_host);
        return secure;
    }

    /// <summary>Runs a CL command in the server's job. Throws what the system said when it fails.</summary>
    public void Command(string command)
    {
        if (TryCommand(command) is { } problem) throw new RemoteException(problem);
    }

    /// <summary>Runs a CL command in the server's job: null when it worked, else what the system said.</summary>
    public string? TryCommand(string command)
    {
        if (Encoding.UTF8.GetByteCount(command) + 7 > MaxLine) return $"The command is longer than the {MaxLine} bytes FTP takes: {command[..60]}...";
        var (code, text) = Send("RCMD " + command);
        return code == 250 ? null : text;
    }

    /// <summary>Receives a member as it is, giving its bytes to <paramref name="read"/> as they come.</summary>
    public void Retrieve(string path, Action<Stream> read)
    {
        using var data = Passive();
        var (code, text) = Send("RETR " + path);
        if (code is not (125 or 150)) throw new RemoteException(text);
        try
        {
            using var stream = Encrypted ? Secure(data.GetStream()) : (Stream)data.GetStream();
            read(stream);
        }
        finally
        {
            data.Close();
            // The server's last word on the transfer has to be read whatever happened to the data.
            var (done, said) = Read();
            if (done is not (226 or 250)) throw new RemoteException(said);
        }
    }

    /// <summary>Sends text lines to a (source) member, replacing it.</summary>
    public void StoreText(string path, IEnumerable<string> lines)
    {
        Expect(Send("TYPE A"), 200);
        try
        {
            using var data = Passive();
            var (code, text) = Send("STOR " + path);
            if (code is not (125 or 150)) throw new RemoteException(text);
            using (var stream = Encrypted ? Secure(data.GetStream()) : (Stream)data.GetStream())
            {
                foreach (var line in lines) stream.Write(Encoding.ASCII.GetBytes(line + "\r\n"));
            }
            data.Close();
            var (done, said) = Read();
            if (done is not (226 or 250)) throw new RemoteException(said);
        }
        finally
        {
            Expect(Send("TYPE I"), 200);
        }
    }

    [GeneratedRegex(@"\((\d+),(\d+),(\d+),(\d+),(\d+),(\d+)\)")]
    private static partial Regex PassiveReply();

    /// <summary>A data connection, to the port the server names (at the address the session is with: its own may be a private one).</summary>
    private TcpClient Passive()
    {
        var (code, text) = Send("PASV");
        if (code != 227 || PassiveReply().Match(text) is not { Success: true } m) throw new RemoteException($"No data connection: {text}");
        var port = int.Parse(m.Groups[5].Value) * 256 + int.Parse(m.Groups[6].Value);
        var data = new TcpClient { ReceiveTimeout = 10 * 60 * 1000, SendTimeout = 60 * 1000 };
        data.Connect(_host, port);
        return data;
    }

    private void Expect((int Code, string Text) reply, int code)
    {
        if (reply.Code != code) throw new RemoteException(reply.Text);
    }

    private (int Code, string Text) Send(string line)
    {
        _stream.Write(Encoding.UTF8.GetBytes(line + "\r\n"));
        _stream.Flush();
        return Read();
    }

    /// <summary>A reply: its code, and its text without the codes (a reply of several lines ends with "code text").</summary>
    private (int Code, string Text) Read()
    {
        var text = new StringBuilder();
        while (true)
        {
            var line = Line();
            var coded = line.Length >= 3 && char.IsDigit(line[0]) && char.IsDigit(line[1]) && char.IsDigit(line[2]);
            var rest = coded ? line[Math.Min(4, line.Length)..] : line;
            if (rest.Trim().Length > 0) text.Append(text.Length > 0 ? " " : "").Append(rest.Trim());
            if (coded && (line.Length == 3 || line[3] == ' ')) return (int.Parse(line[..3]), text.ToString());
        }
    }

    private string Line()
    {
        var line = new List<byte>();
        while (true)
        {
            if (_start == _end)
            {
                _start = 0;
                _end = _stream.Read(_buffer, 0, _buffer.Length);
                if (_end <= 0) throw new IOException("the system closed the connection");
            }
            var b = _buffer[_start++];
            if (b == '\n') return Encoding.Latin1.GetString(line.ToArray()).TrimEnd('\r');
            line.Add(b);
        }
    }

    public void Dispose()
    {
        try
        {
            if (_control.Connected)
            {
                _control.ReceiveTimeout = 3000;
                _stream.Write("QUIT\r\n"u8);
            }
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
        }
        _stream.Dispose();
        _control.Dispose();
    }
}
