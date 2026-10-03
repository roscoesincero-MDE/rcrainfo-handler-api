using System.Text;

using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace RCRAInfo.SqlCheck;

/// <summary>
/// Parses every deployment script with ScriptDom's SQL Server 2022 grammar (<c>TSql160Parser</c>)
/// and fails if any of them does not parse.
/// </summary>
/// <remarks>
/// <para>
/// Why this exists: the developer workstation runs SQL Server 2025 and UAT/Production run 2022.
/// Compatibility level 160 is not a feature gate -- it happily executes 2025-only syntax -- so
/// without this check a script can be written, run successfully on the workstation, committed, and
/// then fail at the UAT deployment. This moves that failure to the build.
/// </para>
/// <para>
/// WHAT IT DOES NOT CATCH, stated plainly because relying on it for more than it does would be
/// worse than not having it: this is a <em>syntax</em> gate, not a <em>semantic</em> one. A column
/// declared with the 2025-only native <c>json</c> type parses cleanly here, because to the parser
/// <c>json</c> is just a type name -- it would fail only on the 2022 server. The forbidden-feature
/// list (native <c>json</c>, the <c>REGEXP_*</c> functions, <c>JSON_OBJECTAGG</c>,
/// <c>JSON_ARRAYAGG</c>, <c>JSON_CONTAINS</c>, <c>VECTOR</c>, <c>OPTIMIZED_LOCKING</c>) is enforced
/// by .claude/hooks/validate-sql.py, and the last word belongs to F3's real deployment to a 2022
/// instance. Three layers, none of which is sufficient alone.
/// </para>
/// </remarks>
internal static class Program
{
    /// <summary>Scripts live here by default, relative to the repository root.</summary>
    private const string DefaultScriptRoot = @"src\RCRAInfo.Database";

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        List<string> files;
        try
        {
            files = ResolveFiles(args);
        }
        catch (DirectoryNotFoundException ex)
        {
            Console.Error.WriteLine($"FAIL  SqlCheck: {ex.Message}");
            return 2;
        }

        if (files.Count == 0)
        {
            // An empty run is a failure, not a pass. A check that silently examines nothing is the
            // most dangerous state this tool can be in: green, and blind.
            Console.Error.WriteLine("FAIL  SqlCheck found no .sql files to parse. Check the path.");
            return 2;
        }

        int errorCount = 0;

        foreach (string file in files)
        {
            errorCount += ParseOne(file);
        }

        if (errorCount > 0)
        {
            Console.Error.WriteLine(
                $"FAIL  SQL Server 2022 parse (TSql160Parser): {errorCount} error(s) in {files.Count} file(s).");
            return 1;
        }

        Console.WriteLine($"PASS  SQL Server 2022 parse (TSql160Parser): {files.Count} file(s), no errors.");
        return 0;
    }

    /// <summary>
    /// Parses one file and writes any parse errors to stderr in a file:line:column form that both
    /// a terminal and a CI log annotator can follow.
    /// </summary>
    /// <returns>The number of parse errors found.</returns>
    private static int ParseOne(string file)
    {
        string sql = File.ReadAllText(file);

        // initialQuotedIdentifier: true matches the QUOTED_IDENTIFIER ON that 010_Database.sql sets
        // on the database and that every script assumes.
        TSql160Parser parser = new(initialQuotedIdentifiers: true);

        using StringReader reader = new(sql);
        _ = parser.Parse(reader, out IList<ParseError> errors);

        if (errors.Count == 0)
        {
            return 0;
        }

        foreach (ParseError error in errors)
        {
            Console.Error.WriteLine(
                $"{file}({error.Line},{error.Column}): error SQL{error.Number}: {error.Message}");
        }

        return errors.Count;
    }

    /// <summary>
    /// Expands the command-line arguments into a sorted list of .sql files. Arguments may be files
    /// or directories; with no arguments, the default script root under the repository is used.
    /// </summary>
    private static List<string> ResolveFiles(string[] args)
    {
        IEnumerable<string> roots = args.Length > 0
            ? args
            : [Path.Combine(FindRepositoryRoot(), DefaultScriptRoot)];

        List<string> files = [];

        foreach (string root in roots)
        {
            if (File.Exists(root))
            {
                files.Add(Path.GetFullPath(root));
            }
            else if (Directory.Exists(root))
            {
                files.AddRange(Directory.EnumerateFiles(root, "*.sql", SearchOption.AllDirectories));
            }
            else
            {
                throw new DirectoryNotFoundException($"'{root}' is neither a file nor a directory.");
            }
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    /// <summary>
    /// Walks up from the executing assembly looking for RCRAInfo.sln, so the tool can be run from
    /// bin\Debug without the caller having to know how deep that is.
    /// </summary>
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
            "Could not locate RCRAInfo.sln above " + AppContext.BaseDirectory +
            ". Pass the script directory explicitly.");
    }
}
