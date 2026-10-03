namespace RCRAInfo.Data.Tests;

/// <summary>
/// Locates the repository from the test binary, so a test can read a file that is not a build output.
/// </summary>
/// <remarks>
/// Several tests in this project assert that a C# artifact still agrees with a file that is not
/// compiled — <c>BannedSymbols.txt</c>, and the SQL scripts under
/// <c>src/RCRAInfo.Database/Scripts</c>. Copying those into the test project would make the tests pass
/// against a stale copy, which is the failure the tests exist to prevent, so they are read from the
/// working tree instead.
/// </remarks>
internal static class TestPaths
{
    /// <summary>The directory holding <c>RCRAInfo.sln</c>.</summary>
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    /// <summary>The directory holding the numbered SQL deployment scripts.</summary>
    public static string SqlScripts { get; } =
        Path.Combine(RepositoryRoot, "src", "RCRAInfo.Database", "Scripts");

    /// <summary>Reads a SQL script by its file name.</summary>
    /// <param name="fileName">File name within <see cref="SqlScripts"/>.</param>
    /// <returns>The script text.</returns>
    public static string ReadSqlScript(string fileName) =>
        File.ReadAllText(Path.Combine(SqlScripts, fileName));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RCRAInfo.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate RCRAInfo.sln above {AppContext.BaseDirectory}.");
    }
}
