namespace SimI.Tests;

/// <summary>The repository's examples: examples/source, a folder for each library, found from where the tests run.</summary>
internal static class Examples
{
    /// <summary>examples/source.</summary>
    public static string Source()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "examples", "source");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("examples/source");
    }

    private static readonly Lazy<string> Crm = new(() => Only("CRMDEMO"));
    private static readonly Lazy<string> Customers = new(() => Only("DEMOCUST"));

    /// <summary>A folder holding only the CRM's library, CRMDEMO, as a source folder: made once per test run.</summary>
    public static string CrmSource() => Crm.Value;

    /// <summary>A folder holding only the customer maintenance app's library, DEMOCUST: made once per test run.</summary>
    public static string CustomersSource() => Customers.Value;

    /// <summary>A copy of one library of examples/source, alone in a source folder of its own.</summary>
    private static string Only(string library)
    {
        var folder = Directory.CreateTempSubdirectory("sim-i-example-").FullName;
        var from = Path.Combine(Source(), library);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(folder, library, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }
        return folder;
    }
}
