namespace SimI.Tests;

/// <summary>Settings for tests that sign on as the test user, which Sim i makes only when the settings name it.</summary>
internal static class TestUsers
{
    /// <summary>SIM_I_USER as PRTUSER, with its default password (Secret12).</summary>
    public static Dictionary<string, string> Settings() => new() { ["SIM_I_USER"] = Users.DefaultUser };
}
