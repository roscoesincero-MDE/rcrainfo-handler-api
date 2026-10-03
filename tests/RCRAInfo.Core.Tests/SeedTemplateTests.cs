using System.Text.Json;
using System.Text.Json.Nodes;
using RCRAInfo.Core.Credentials;

namespace RCRAInfo.Core.Tests;

/// <summary>
/// The two committed <c>secrets.Template.json</c> files: that they hold no secret, and that they still
/// match the shape the parser demands.
/// </summary>
/// <remarks>
/// Every "file not found" message this feature produces names <c>secrets.Template.json</c> and tells an
/// operator to seed from it. That instruction is only worth giving if the file exists and is right, and
/// neither is checked by anything else: the templates are not compiled, not deployed by the SQL scripts,
/// and not referenced by any code path. So a template that drifted from the parser — a key renamed, an
/// <c>ApiId</c> left in the monitor's copy — would be discovered by whoever was seeding production at
/// the time.
/// </remarks>
public sealed class SeedTemplateTests
{
    /// <summary>The same tolerance <see cref="CredentialFile"/> reads with, so a template may be commented.</summary>
    private static readonly JsonDocumentOptions Tolerant = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public static TheoryData<ApplicationIdentity, string> Templates =>
        new()
        {
            { ApplicationIdentity.Loader, "RCRAInfo.Loader" },
            { ApplicationIdentity.Monitor, "RCRAInfo.Monitor" },
        };

    private static string RepositoryRoot { get; } = FindRepositoryRoot();

    [Theory]
    [MemberData(nameof(Templates))]
    public void TheCommittedTemplateExistsWhereTheErrorMessageSaysItDoes(
        ApplicationIdentity application, string project)
    {
        _ = application;

        Assert.True(
            File.Exists(TemplatePath(project)),
            $"{TemplatePath(project)} does not exist, but the NotSeeded message tells the operator to "
            + "seed from it.");
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public void TheCommittedTemplateIsTheShapeThisCodeGenerates(
        ApplicationIdentity application, string project)
    {
        // DeepEquals rather than a text comparison: the committed file carries the comments that tell an
        // operator what to fill in and how, and the generated one cannot -- JsonNode does not write
        // comments. Line endings would be the other difference, and this is insensitive to both.
        JsonNode committed = Parse(File.ReadAllText(TemplatePath(project)));
        JsonNode generated = Parse(CredentialFile.SeedTemplate(application));

        Assert.True(
            JsonNode.DeepEquals(committed, generated),
            $"{project}'s secrets.Template.json no longer matches "
            + $"CredentialFile.SeedTemplate({application}). Committed: {Describe(committed)}. "
            + $"Generated: {Describe(generated)}.");
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public void NoCommittedTemplateHoldsAValue(ApplicationIdentity application, string project)
    {
        _ = application;

        // The whole reason this file is committed and secrets.json is not. A template that shipped with
        // a real password in it would be in source control forever, and the .gitignore rule that catches
        // secrets.json by name would not have caught it.
        foreach (KeyValuePair<string, JsonNode?> property in
                 Parse(File.ReadAllText(TemplatePath(project))).AsObject())
        {
            if (string.Equals(property.Key, "Encrypted", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Assert.True(
                string.IsNullOrEmpty(property.Value?.GetValue<string>()),
                $"{project}'s secrets.Template.json has a value in '{property.Key}'. A template holds "
                + "the shape, never a credential.");
        }
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public void ATemplateIsNotASeededFileAndTheParserSaysSo(
        ApplicationIdentity application, string project)
    {
        _ = application;

        // Worth pinning because it looks like a bug the first time it is seen. Parse rejects the
        // template -- SqlPassword is empty, and an empty SQL password is an incompletely seeded file
        // (AR3) rather than an application that connects some other way. Copying the template to
        // secrets.json and running before filling it in gives that message, which is the right one.
        CredentialFileFormatException error = Assert.Throws<CredentialFileFormatException>(
            () => CredentialFile.Parse(File.ReadAllText(TemplatePath(project))));

        Assert.Contains("SqlPassword", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheLoaderTemplateCarriesApiCredentials()
    {
        // The monitoring application never calls EPA (AR2), so an ApiKey key in its template is an
        // invitation to put the API Key on a machine whose identity is denied read access to the
        // loader's copy of it -- Analysis §6.1.
        JsonObject monitor = Parse(File.ReadAllText(TemplatePath("RCRAInfo.Monitor"))).AsObject();
        JsonObject loader = Parse(File.ReadAllText(TemplatePath("RCRAInfo.Loader"))).AsObject();

        Assert.False(monitor.ContainsKey("ApiId"));
        Assert.False(monitor.ContainsKey("ApiKey"));
        Assert.True(loader.ContainsKey("ApiId"));
        Assert.True(loader.ContainsKey("ApiKey"));
    }

    private static string TemplatePath(string project) =>
        Path.Combine(RepositoryRoot, "src", project, "secrets.Template.json");

    private static JsonNode Parse(string json) =>
        JsonNode.Parse(json, nodeOptions: null, documentOptions: Tolerant)
        ?? throw new InvalidOperationException("A credential template must not be JSON null.");

    private static string Describe(JsonNode node) =>
        string.Join(", ", node.AsObject().Select(property => property.Key));

    /// <summary>
    /// Locates the repository from the test binary, so the templates are read from the working tree.
    /// </summary>
    /// <remarks>
    /// Copying them into the test project as content would let this pass against a stale copy — which is
    /// the exact failure the drift assertion exists to prevent.
    /// </remarks>
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
