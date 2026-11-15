using System.Text;
using FakeIbmI.Remote;
using Xunit.Abstractions;

namespace FakeIbmI.Tests;

/// <summary>
/// A test against a real IBM i, run only when it's named: FAKE_IBMI_TEST_REMOTE_HOST, _USER and
/// _PASSWORD, and FAKE_IBMI_TEST_REMOTE_OBJECTS as LIB/OBJ LIB/OBJ ... (objects that user can read)
/// or FAKE_IBMI_TEST_REMOTE_LIBRARY, a whole library (with FAKE_IBMI_TEST_REMOTE_DATA=*NO, without its records).
/// FAKE_IBMI_TEST_REMOTE_DATABASE (a connection string) keeps what's received.
/// </summary>
public sealed class RemoteFactAttribute : FactAttribute
{
    public RemoteFactAttribute(string what)
    {
        if (new[] { "HOST", "USER", "PASSWORD", what }.Any(v => string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FAKE_IBMI_TEST_REMOTE_" + v))))
            Skip = $"needs a real IBM i: FAKE_IBMI_TEST_REMOTE_HOST, _USER, _PASSWORD and _{what}";
    }
}

/// <summary>SAVRSTLIB and SAVRSTOBJ: reading a real IBM i's records and descriptions, and making its files here.</summary>
public class SaveRestoreTests(ITestOutputHelper output)
{
    private static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", ""));

    private static string Value(string hex, RemoteField field)
    {
        long bad = 0;
        var value = RemoteRecords.Value(Hex(hex), field, ref bad);
        Assert.Equal(0, bad);
        return value;
    }

    [Theory]
    [InlineData("12345F", 2, "123.45")]
    [InlineData("12345D", 2, "-123.45")]
    [InlineData("00000F", 0, "0")]
    [InlineData("00005C", 5, "0.00005")]
    [InlineData("01000B", 0, "-1000")]
    public void PackedNumbers(string hex, int decimals, string expected) =>
        Assert.Equal(expected, Value(hex, new RemoteField("N", 'P', 0, 3, 5, decimals)));

    [Theory]
    [InlineData("F1F2F3", 1, "12.3")]
    [InlineData("F1F2D3", 0, "-123")]
    [InlineData("404040", 0, "0")]
    [InlineData("F0F0C7", 3, "0.007")]
    public void ZonedNumbers(string hex, int decimals, string expected) =>
        Assert.Equal(expected, Value(hex, new RemoteField("N", 'S', 0, 3, 3, decimals)));

    [Fact]
    public void ANumberThatIsNotOneIsZeroAndCounted()
    {
        long bad = 0;
        Assert.Equal("0", RemoteRecords.Value(Hex("404040"), new RemoteField("N", 'P', 0, 3, 5, 0), ref bad));
        Assert.Equal(1, bad);
    }

    [Fact]
    public void BinaryFloatAndCharacters()
    {
        Assert.Equal("-2", Value("FFFE", new RemoteField("N", 'B', 0, 2, 4)));
        Assert.Equal("70000", Value("00011170", new RemoteField("N", 'B', 0, 4, 9)));
        Assert.Equal("1.5", Value("00000096", new RemoteField("N", 'B', 0, 4, 9, 2)));
        Assert.Equal("1.5", Value("3FF8000000000000", new RemoteField("N", 'F', 0, 8)));
        // Trailing blanks go: PostgreSQL's CHAR has them back.
        Assert.Equal("Acme", Value("C1839485 4040", new RemoteField("C", 'A', 0, 6)));
        // A varying field: two bytes of length, then that many characters.
        Assert.Equal("Ac ", Value("0003 C18340 FFFF", new RemoteField("C", 'A', 0, 7, Varying: true)));
        // UTF-8 and UTF-16 by their CCSIDs.
        Assert.Equal("é", Value("C3A9 20", new RemoteField("C", 'A', 0, 3, Ccsid: 1208)));
        Assert.Equal("Hi", Value("0002 0048 0069 0000", new RemoteField("C", 'G', 0, 8, Varying: true, Ccsid: 1200)));
    }

    [Fact]
    public void DatesTimesAndTimestamps()
    {
        static byte[] E(string text) => Ebcdic.Encode(text);
        string Read(string text, char type, string format = "") => Value(Convert.ToHexString(E(text)), new RemoteField("D", type, 0, text.Length, Format: format));
        Assert.Equal("2026-11-15", Read("2026-11-15", 'L', "*ISO"));
        Assert.Equal("2026-11-15", Read("11/15/2026", 'L', "*USA"));
        Assert.Equal("2026-11-15", Read("15.11.2026", 'L', "*EUR"));
        Assert.Equal("1999-12-31", Read("12/31/99", 'L', "*MDY"));
        Assert.Equal("2026-11-15", Read("26/11/15", 'L', "*YMD"));
        Assert.Equal("2024-02-29", Read("24/060", 'L', "*JUL"));
        Assert.Equal("13:05:09", Read("13.05.09", 'T', "*ISO"));
        Assert.Equal("13:05:00", Read("01:05 PM", 'T', "*USA"));
        Assert.Equal("2026-11-15 13:05:09.123456", Read("2026-11-15-13.05.09.123456", 'Z'));
        long bad = 0;
        Assert.Equal("0001-01-01", RemoteRecords.Value(E("          "), new RemoteField("D", 'L', 0, 10, Format: "*ISO"), ref bad));
        Assert.Equal(1, bad);
    }

    [Fact]
    public void CopyTextEscapesWhatSeparatesRows()
    {
        var line = new StringBuilder();
        RemoteRecords.AppendCopy(line, "a\tb\\c\nd\0");
        Assert.Equal(@"a\tb\\c\nd ", line.ToString());
    }

    [Fact]
    public void ALongStatementIsWrappedBetweenWordsNotInStrings()
    {
        var lines = RemoteSystem.Wrapped("SELECT 'a b c d e f g h' AS X, \"LONG NAME\" FROM T WHERE Y = 1;", 20);
        Assert.True(lines.Count > 2);
        Assert.Contains(lines, l => l.Contains("'a b c d e f g h'"));
        Assert.Contains(lines, l => l.Contains("\"LONG NAME\""));
        Assert.Equal("SELECT 'a b c d e f g h' AS X, \"LONG NAME\" FROM T WHERE Y = 1;", string.Join(' ', lines));
    }

    [Fact]
    public void NamesGenericNamesAndAll()
    {
        Assert.True(Machine.NameMatches("CUSTPF", "*ALL"));
        Assert.True(Machine.NameMatches("CUSTPF", "CUST*"));
        Assert.True(Machine.NameMatches("CUSTPF", "CUSTPF"));
        Assert.False(Machine.NameMatches("CUSTPF", "CUST"));
        Assert.False(Machine.NameMatches("ACUSTPF", "CUST*"));
    }

    private static readonly RemoteField[] CustomerFields =
    [
        new("CUSTNO", 'P', 0, 3, 5, 0), new("NAME", 'A', 3, 20) { Text = "Customer's name" }, new("NOTE", 'A', 23, 52, Varying: true, Nullable: true),
        new("BAL", 'S', 75, 9, 9, 2), new("SEQ", 'B', 84, 4, 9), new("SINCE", 'L', 88, 10, Format: "*ISO"), new("CHANGED", 'Z', 98, 26),
    ];

    [Fact]
    public void APhysicalFilesDdsMakesTheFileItDescribes()
    {
        var dds = Machine.PhysicalDds("CUSTREC", CustomerFields, [("CUSTNO", false)], unique: true);
        output.WriteLine(dds);
        Assert.Contains("     A          R CUSTREC", dds);
        Assert.Contains("     A            CUSTNO         5P 0", dds);
        Assert.Contains("     A            NOTE          50A         VARLEN ALWNULL", dds);
        Assert.Contains("     A          K CUSTNO", dds);
        using var machine = Machine.Open();
        var job = new BatchJob("PRTUSER", CancellationToken.None);
        machine.Save(new Member("QGPL", "QDDSSRC", "CUSTPF", "PF", dds));
        var made = machine.Run("CRTPF FILE(QGPL/CUSTPF) SRCFILE(QGPL/QDDSSRC)", job);
        Assert.True(made.Ok, made.Message);
        Assert.Equal(
            [("CUSTNO", "DECIMAL(5, 0)"), ("NAME", "CHAR(20)"), ("NOTE", "VARCHAR(50)"), ("BAL", "DECIMAL(9, 2)"), ("SEQ", "INTEGER"), ("SINCE", "DATE"), ("CHANGED", "TIMESTAMP")],
            machine.Store.Columns("CUSTPF", "QGPL"));
    }

    [Fact]
    public void ALogicalFilesDdsKeepsItsKeyAndItsRules()
    {
        using var machine = Machine.Open();
        var job = new BatchJob("PRTUSER", CancellationToken.None);
        machine.Save(new Member("QGPL", "QDDSSRC", "CUSTPF", "PF", Machine.PhysicalDds("CUSTREC", CustomerFields, [("CUSTNO", false)], unique: true)));
        Assert.True(machine.Run("CRTPF FILE(QGPL/CUSTPF) SRCFILE(QGPL/QDDSSRC)", job).Ok);
        var dds = Machine.LogicalDds([new Machine.LogicalFormat("CUSTREC", "CUSTPF", [], [("NAME", false), ("CUSTNO", false)],
        [
            // Select customers 2 to 4 not named Bolt; omit the rest.
            new Machine.RemoteRule('S', "CUSTNO", "RG", ["2", "4"]), new Machine.RemoteRule('A', "NAME", "NE", ["'Bolt'"]),
            new Machine.RemoteRule('O', "", "AL", []),
        ])], unique: false);
        output.WriteLine(dds);
        machine.Save(new Member("QGPL", "QDDSSRC", "CUSTBYNAME", "LF", dds));
        var made = machine.Run("CRTLF FILE(QGPL/CUSTBYNAME) SRCFILE(QGPL/QDDSSRC)", job);
        Assert.True(made.Ok, made.Message);
        var run = machine.Run("RUNSQL SQL('INSERT INTO QGPL.CUSTPF (CUSTNO, NAME) VALUES (1, ''Acme''), (2, ''Bolt''), (3, ''Cog''), (4, ''Dent''), (5, ''Eve'')')", job);
        Assert.True(run.Ok, run.Message);
        var (shown, error) = RpgTests.Run(machine, """
            dcl-f custbyname keyed;
            read custbyname;
            dow not %eof(custbyname);
              dsply (%trim(name));
              read custbyname;
            enddo;
            *inlr = *on;
            """);
        Assert.Null(error);
        Assert.Equal(["Cog", "Dent"], shown);
    }

    [Fact]
    public void TheCommandsNeedALibraryASystemAndAUser()
    {
        using var machine = Machine.Open();
        var job = new BatchJob("PRTUSER", CancellationToken.None);
        Assert.Equal("CPD0071", machine.Run("SAVRSTLIB", job).MessageId);
        Assert.Equal("CPD0071", machine.Run("SAVRSTLIB LIB(MYLIB)", job).MessageId);
        var noUser = machine.Run("SAVRSTLIB LIB(MYLIB) RMTLOCNAME(SOMEHOST)", job);
        Assert.Equal("CPD0071", noUser.MessageId);
        Assert.Contains("RMTUSER", noUser.Message);
        Assert.Equal("CPD0071", machine.Run("SAVRSTOBJ LIB(MYLIB) RMTLOCNAME(SOMEHOST) RMTUSER(ME) RMTPWD(secret)", job).MessageId);
        // Nothing listens there: the command fails with the reason, and no library is made.
        var unreachable = machine.Run("SAVRSTLIB LIB(MYLIB) RMTLOCNAME(127.0.0.1) PORT(1) RMTUSER(ME) RMTPWD(secret)", job);
        Assert.False(unreachable.Ok);
        Assert.Equal("CPF3770", unreachable.MessageId);
        Assert.DoesNotContain("secret", unreachable.Message);
        Assert.False(machine.Store.HasLibrary("MYLIB"));
    }

    /// <summary>Receives the objects FAKE_IBMI_TEST_REMOTE_OBJECTS names from a real IBM i, and says what came.</summary>
    private static string Setting(string name) => Environment.GetEnvironmentVariable("FAKE_IBMI_TEST_REMOTE_" + name)!;

    /// <summary>Receives the library FAKE_IBMI_TEST_REMOTE_LIBRARY names, whole, and says what came. Objects that can't be made here don't fail it.</summary>
    [RemoteFact("LIBRARY")]
    public void ReceivesALibraryFromARealSystem()
    {
        using var machine = Machine.Open(Environment.GetEnvironmentVariable("FAKE_IBMI_TEST_REMOTE_DATABASE") is { Length: > 0 } keep ? keep : null);
        machine.RemoteUser = Setting("USER");
        machine.RemotePassword = Setting("PASSWORD");
        machine.Progress = output.WriteLine;
        var library = Setting("LIBRARY").ToUpperInvariant();
        var result = machine.Run($"SAVRSTLIB LIB({library}) RMTLOCNAME({Setting("HOST")}) DATA({Environment.GetEnvironmentVariable("FAKE_IBMI_TEST_REMOTE_DATA") ?? "*YES"})",
            new BatchJob("PRTUSER", CancellationToken.None));
        output.WriteLine(result.Message);
        output.WriteLine(machine.Store.Spools()[^1].Text);
        Assert.True(result.Ok || result.MessageId == "CPF3773", result.Message);
        var (members, objects, tables) = machine.Store.Counts(library);
        output.WriteLine($"{library} here: {members} members, {objects} objects, {tables} tables and views");
        Assert.True(members + objects + tables > 0);
    }

    [RemoteFact("OBJECTS")]
    public void ReceivesObjectsFromARealSystem()
    {
        // FAKE_IBMI_TEST_REMOTE_DATABASE names a database to keep what's received in; without it, it goes when the test ends.
        using var machine = Machine.Open(Environment.GetEnvironmentVariable("FAKE_IBMI_TEST_REMOTE_DATABASE") is { Length: > 0 } keep ? keep : null);
        machine.RemoteUser = Setting("USER");
        machine.RemotePassword = Setting("PASSWORD");
        machine.Progress = output.WriteLine;
        var job = new BatchJob("PRTUSER", CancellationToken.None);
        foreach (var library in Setting("OBJECTS").ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).GroupBy(o => o.Split('/')[0], o => o.Split('/')[1]))
        {
            var result = machine.Run($"SAVRSTOBJ OBJ({string.Join(' ', library)}) LIB({library.Key}) RMTLOCNAME({Setting("HOST")}) DATA({Environment.GetEnvironmentVariable("FAKE_IBMI_TEST_REMOTE_DATA") ?? "*YES"})", job);
            output.WriteLine(result.Message);
            output.WriteLine(machine.Store.Spools()[^1].Text);
            Assert.True(result.Ok, result.Message);
            var (members, objects, tables) = machine.Store.Counts(library.Key);
            output.WriteLine($"{library.Key} here: {members} members, {objects} objects, {tables} tables and views");
            Assert.True(members + objects + tables > 0);
        }
    }
}
