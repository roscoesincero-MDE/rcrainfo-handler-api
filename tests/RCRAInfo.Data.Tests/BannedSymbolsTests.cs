using System.Collections.Immutable;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RCRAInfo.Data.Tests;

/// <summary>
/// Guards BannedSymbols.txt against its silent failure modes.
/// </summary>
/// <remarks>
/// <para>
/// BannedApiAnalyzers is a deliberately quiet analyzer. An entry naming a symbol it cannot resolve is
/// not reported: it simply never matches, and the ban is gone. The file still reads correctly, the
/// build is still green, and nothing anywhere says the rule stopped working.
/// </para>
/// <para>
/// This is not hypothetical. Two forms that look obviously correct resolve to nothing, and both were
/// in this file before these tests existed:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///     A method entry with no parameter list does NOT ban every overload. It resolves to the
///     PARAMETERLESS overload and nothing else. <c>M:System.String.Trim</c> bans <c>Trim()</c> and
///     leaves <c>Trim(char)</c> alone; <c>M:System.String.Concat</c>, which has no parameterless
///     overload, bans nothing at all.
///     </description>
///   </item>
///   <item>
///     <description>
///     A generic method needs its full documentation-comment ID: the double-backtick arity suffix
///     AND the parameter list, with method type parameters written <c>``0</c>. Neither
///     <c>M:System.Linq.Enumerable.Count</c> nor <c>M:System.Linq.Enumerable.Count``1</c> resolves;
///     <c>M:System.Linq.Enumerable.Count``1(System.Collections.Generic.IEnumerable{``0})</c> does.
///     </description>
///   </item>
/// </list>
/// <para>
/// So the tests resolve each entry through <see cref="DocumentationCommentId"/> -- the same Roslyn API
/// the analyzer itself uses, pinned to the same package generation -- rather than approximating it
/// with reflection. <see cref="EveryOverloadOfATotallyBannedApiIsListed"/> then goes the other way and
/// fails when EF Core adds an overload that no entry covers, printing the doc IDs to paste in.
/// </para>
/// <para>
/// This project owns the tests because it is the one that references EF Core, so the banned EF Core
/// types are in the compilation.
/// </para>
/// </remarks>
public sealed class BannedSymbolsTests
{
    /// <summary>
    /// APIs where every single overload must be banned, not just the ones someone happened to think
    /// of. Each is a rule about the shape of the whole codebase, so a surviving overload is a hole.
    /// </summary>
    private static readonly (string TypeMetadataName, string MethodName, string Why)[] TotallyBanned =
    [
        ("Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions", "ExecuteDelete",
            "AR7: no hard deletes anywhere in this database."),
        ("Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions", "ExecuteDeleteAsync",
            "AR7: no hard deletes anywhere in this database."),
        ("Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions", "ExecuteUpdate",
            "Revision 6: procedures own all writes and audit stamping."),
        ("Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions", "ExecuteUpdateAsync",
            "Revision 6: procedures own all writes and audit stamping."),
        ("Microsoft.EntityFrameworkCore.DbContext", "SaveChanges",
            "Revision 6: nothing writes through the change tracker."),
        ("Microsoft.EntityFrameworkCore.DbContext", "SaveChangesAsync",
            "Revision 6: nothing writes through the change tracker."),
        ("Microsoft.EntityFrameworkCore.RelationalEntityTypeBuilderExtensions", "InsertUsingStoredProcedure",
            "Revision 6: procedures are set-based; this maps one procedure per entity."),
        ("Microsoft.EntityFrameworkCore.RelationalEntityTypeBuilderExtensions", "UpdateUsingStoredProcedure",
            "Revision 6: procedures are set-based; this maps one procedure per entity."),
        ("Microsoft.EntityFrameworkCore.RelationalEntityTypeBuilderExtensions", "DeleteUsingStoredProcedure",
            "Revision 6: procedures are set-based; this maps one procedure per entity."),

        // R12. Every procedure in this database opens its own transaction, and T-SQL has no nested
        // rollback: the procedure's CATCH rolls back to @@TRANCOUNT = 0, discarding the outer
        // transaction the C# code still believes it owns, which then fails on Commit with a DIFFERENT
        // error than the one that actually happened. A genuine multi-procedure unit of work needs one
        // procedure wrapping the others.
        //
        // It is also what EnableRetryOnFailure cannot coexist with: EF Core throws when a retrying
        // execution strategy meets an explicit transaction, and it throws on the first retry -- that
        // is, in UAT, not in development.
        //
        // Two types, because BeginTransaction is declared on DatabaseFacade AND overloaded by the
        // relational extension methods. Banning one and not the other leaves the isolation-level
        // overload legal, which is the overload someone reaching for a transaction is most likely to
        // reach for.
        ("Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade", "BeginTransaction",
            "R12: procedures own their transactions; T-SQL has no nested rollback."),
        ("Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade", "BeginTransactionAsync",
            "R12: procedures own their transactions; T-SQL has no nested rollback."),
        ("Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions", "BeginTransaction",
            "R12: procedures own their transactions; T-SQL has no nested rollback."),
        ("Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions", "BeginTransactionAsync",
            "R12: procedures own their transactions; T-SQL has no nested rollback."),
        ("Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions", "UseTransaction",
            "R12: procedures own their transactions; an enlisted transaction has the same problem."),
        ("Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions", "UseTransactionAsync",
            "R12: procedures own their transactions; an enlisted transaction has the same problem."),
    ];

    /// <summary>Every banned-symbol entry in the file, as written.</summary>
    public static TheoryData<string> Entries
    {
        get
        {
            TheoryData<string> data = [];

            foreach (string entry in ReadEntries())
            {
                data.Add(entry);
            }

            return data;
        }
    }

    /// <summary>
    /// The file is not empty. A bad merge that leaves only the comment header would otherwise turn
    /// every other test in this class into a vacuous pass.
    /// </summary>
    [Fact]
    public void TheListIsNotEmpty()
    {
        IReadOnlyList<string> entries = ReadEntries();

        Assert.True(
            entries.Count >= 50,
            $"BannedSymbols.txt has {entries.Count} entries; it had 54 when this test was written. " +
            "Entries are removed only by a deliberate decision, which should also update this test.");
    }

    /// <summary>
    /// Each entry resolves to at least one real symbol, using the analyzer's own resolution path. An
    /// entry that resolves to nothing is ignored without a word, so this is the only thing standing
    /// between a plausible-looking ID and a rule that has quietly stopped being enforced.
    /// </summary>
    [Theory]
    [MemberData(nameof(Entries))]
    public void EveryEntryResolvesToARealSymbol(string entry)
    {
        // "M:Namespace.Type.Method(System.String); message" -> id, message
        int separator = entry.IndexOf(';', StringComparison.Ordinal);

        Assert.True(
            separator > 0,
            $"Entry '{entry}' has no '; message' part. Without a message the analyzer reports the " +
            "ban with no explanation, and the next person deletes the entry instead of the call.");

        string id = entry[..separator].Trim();
        string message = entry[(separator + 1)..].Trim();

        Assert.False(string.IsNullOrWhiteSpace(message), $"Entry '{id}' has an empty message.");

        ImmutableArray<ISymbol> symbols =
            DocumentationCommentId.GetSymbolsForDeclarationId(id, ProbeCompilation);

        Assert.False(
            symbols.IsDefaultOrEmpty,
            $"'{id}' resolves to no symbol, so BannedApiAnalyzers ignores it and the ban does " +
            "nothing. Check the documentation-comment ID: a method needs its full parameter list " +
            "unless it is parameterless, and a generic method also needs its ``N arity suffix.");
    }

    /// <summary>
    /// Every public overload of the APIs in <see cref="TotallyBanned"/> has an entry. This is the test
    /// that survives EF Core upgrades: a new overload of ExecuteDelete or InsertUsingStoredProcedure
    /// would otherwise be a legal way around a rule the rest of the codebase is built on.
    /// </summary>
    [Fact]
    public void EveryOverloadOfATotallyBannedApiIsListed()
    {
        HashSet<string> banned = [.. ReadEntries().Select(e => e[..e.IndexOf(';', StringComparison.Ordinal)].Trim())];
        List<string> missing = [];

        foreach ((string typeName, string methodName, string why) in TotallyBanned)
        {
            INamedTypeSymbol? type = ProbeCompilation.GetTypeByMetadataName(typeName);

            Assert.True(
                type is not null,
                $"'{typeName}' is not in the compilation. The type may have been renamed or moved " +
                "to another assembly, in which case every entry naming it has stopped working.");

            IMethodSymbol[] overloads =
            [
                .. type!.GetMembers(methodName)
                        .OfType<IMethodSymbol>()
                        .Where(m => m.DeclaredAccessibility == Accessibility.Public)
            ];

            Assert.True(
                overloads.Length > 0,
                $"'{typeName}' has no public method named '{methodName}'. The API was renamed; the " +
                "entries naming it are resolving to nothing.");

            foreach (IMethodSymbol overload in overloads)
            {
                string? docId = overload.GetDocumentationCommentId();

                if (docId is not null && !banned.Contains(docId))
                {
                    missing.Add($"{docId}; {why}");
                }
            }
        }

        Assert.True(
            missing.Count == 0,
            $"{missing.Count} overload(s) of a totally-banned API have no entry in BannedSymbols.txt. " +
            "Paste these lines in verbatim:\n" + string.Join("\n", missing.Distinct().Order()));
    }

    /// <summary>
    /// The list is handed to the analyzer and RS0030 is an error. A correct list that the build never
    /// reads enforces nothing, and that failure is invisible from inside the file itself.
    /// </summary>
    [Fact]
    public void TheListIsWiredIntoTheBuild()
    {
        string props =
            File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, "Directory.Build.props"));

        Assert.Contains("BannedSymbols.txt", props, StringComparison.Ordinal);
        Assert.Contains("AdditionalFiles", props, StringComparison.Ordinal);
        Assert.Contains("Microsoft.CodeAnalysis.BannedApiAnalyzers", props, StringComparison.Ordinal);
        Assert.Contains("RS0030", props, StringComparison.Ordinal);
    }

    /// <summary>
    /// An empty compilation over every assembly the test host has loaded, which is the whole
    /// dependency closure of this test project and therefore includes EF Core. No source is needed:
    /// symbol resolution only reads metadata.
    /// </summary>
    private static Compilation ProbeCompilation { get; } = CreateProbeCompilation();

    private static CSharpCompilation CreateProbeCompilation()
    {
        // TRUSTED_PLATFORM_ASSEMBLIES is the full list of assemblies the host resolved, which is a
        // superset of the referenced ones and saves guessing at paths.
        string paths = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException("TRUSTED_PLATFORM_ASSEMBLIES is not available.");

        IEnumerable<MetadataReference> references = paths
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path));

        return CSharpCompilation.Create("RCRAInfo.BannedSymbolsProbe", references: references);
    }

    /// <summary>Reads the non-comment, non-blank lines of BannedSymbols.txt.</summary>
    private static IReadOnlyList<string> ReadEntries()
    {
        string path = Path.Combine(TestPaths.RepositoryRoot, "BannedSymbols.txt");

        return
        [
            .. File.ReadLines(path)
                   .Select(line => line.Trim())
                   // A leading ';' is a comment. Inside an entry, ';' separates ID from message.
                   .Where(line => line.Length > 0 && !line.StartsWith(';'))
        ];
    }
}
