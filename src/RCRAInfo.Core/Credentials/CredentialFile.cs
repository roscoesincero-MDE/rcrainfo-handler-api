using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RCRAInfo.Core.Credentials;

/// <summary>
/// The contents of one credential file: the <c>Encrypted</c> flag, the secrets, and everything else
/// the file happens to contain.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not a deserialized POCO.</b> The file is hand-written by whoever seeds the
/// environment (plan §4.2 step 1) and rewritten in place by the application. Round-tripping it through
/// a class with four properties would silently delete every property the class does not know about —
/// an operator's own note, a key added by a later revision, a setting someone put here rather than in
/// <c>appsettings.json</c>. There is no backup of this file and no source control copy of it, by
/// design. So it is read as a <see cref="JsonObject"/>, exactly four properties are touched, and the
/// rest is written back unchanged.
/// </para>
/// <para>
/// <b>Key lookup is case-insensitive, and a case collision is an error.</b> Operators type
/// <c>"encrypted"</c>. Reading one spelling and writing another would leave a file that says both, one
/// of them stale — and the stale one being <c>Encrypted: false</c> over a sealed value is a file that
/// re-seals its own ciphertext on the next run. So a file containing two spellings of the same key is
/// refused rather than guessed at.
/// </para>
/// <para>
/// <b>What is lost on a rewrite:</b> comments and formatting. Both are accepted on the way in —
/// hand-edited JSON has trailing commas and <c>//</c> notes in it — and neither survives
/// <see cref="Seal"/>, because <see cref="JsonNode"/> does not carry them. Stated in the runbook rather
/// than worked around: the file is normalized the first time the application seals it.
/// </para>
/// </remarks>
public sealed class CredentialFile
{
    /// <summary>
    /// The file name this class expects, and the reason <c>reloadOnChange</c> never comes up.
    /// </summary>
    /// <remarks>
    /// Analysis §6.1 requires <c>reloadOnChange: false</c> on the file the application rewrites, or the
    /// web application recycles itself the moment it encrypts its own configuration. This design goes a
    /// step further and takes the file out of the configuration system altogether: it is read by this
    /// class, not by <c>IConfiguration</c>, so there is no file watcher to disable and no way for a
    /// later `AddJsonFile` call to reintroduce one by omitting an argument. The name is deliberately
    /// not <c>appsettings*.json</c>, which is what the default host configuration globs.
    /// <para>
    /// <c>.gitignore</c> already excludes this exact name, and
    /// <c>build/check_gitignore_secrets.py</c> asserts that it does.
    /// </para>
    /// </remarks>
    public const string DefaultFileName = "secrets.json";

    private const string EncryptedKey = "Encrypted";
    private const string SqlPasswordKey = "SqlPassword";
    private const string ApiIdKey = "ApiId";
    private const string ApiKeyKey = "ApiKey";

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        // Tolerant on the way in, because a human types this file.
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly JsonObject root;
    private readonly string encryptedKey;
    private readonly string sqlPasswordKey;
    private readonly string? apiIdKey;
    private readonly string? apiKeyKey;
    private readonly string sqlPasswordValue;
    private readonly string? apiIdValue;
    private readonly string? apiKeyValue;

    private CredentialFile(
        JsonObject root,
        bool encrypted,
        string encryptedKey,
        string sqlPasswordKey,
        string sqlPasswordValue,
        string? apiIdKey,
        string? apiIdValue,
        string? apiKeyKey,
        string? apiKeyValue)
    {
        this.root = root;
        Encrypted = encrypted;
        this.encryptedKey = encryptedKey;
        this.sqlPasswordKey = sqlPasswordKey;
        this.sqlPasswordValue = sqlPasswordValue;
        this.apiIdKey = apiIdKey;
        this.apiIdValue = apiIdValue;
        this.apiKeyKey = apiKeyKey;
        this.apiKeyValue = apiKeyValue;
    }

    /// <summary>Whether the secrets in this file are sealed. AR4's <c>Encrypted</c> flag.</summary>
    public bool Encrypted { get; }

    /// <summary>Whether this file carries RCRAInfo API credentials as well as a SQL password.</summary>
    public bool HasApiCredentials => apiIdValue is not null;

    /// <summary>Reads a credential file.</summary>
    /// <param name="json">The file's text.</param>
    /// <returns>The parsed file, ready to be opened or sealed.</returns>
    /// <exception cref="CredentialFileFormatException">
    /// The text is not JSON, is not an object, is missing a required key, spells one key two ways, or
    /// holds a secret that is not a string.
    /// </exception>
    public static CredentialFile Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        if (string.IsNullOrWhiteSpace(json))
        {
            throw new CredentialFileFormatException(
                "The credential file is empty. If a previous run was interrupted while sealing it, "
                + "re-seed it from secrets.Template.json and the password manager entry; there is "
                + "nothing to recover from the file itself.");
        }

        JsonNode? parsed;

        try
        {
            parsed = JsonNode.Parse(json, nodeOptions: null, documentOptions: ReadOptions);
        }
        catch (JsonException error)
        {
            // The line, the position and the JSON path -- and deliberately NOT error.Message, which
            // quotes the character that broke the parse. On a file whose values are passwords, the
            // character that broke the parse is a character of a password, and this message is logged.
            // Path names a property rather than a value, so it is safe and it is the useful half.
            throw new CredentialFileFormatException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "The credential file is not valid JSON: line {0}, position {1}{2}. The value "
                    + "itself is not reported here, because in this file the values are secrets.",
                    error.LineNumber?.ToString(CultureInfo.InvariantCulture) ?? "unknown",
                    error.BytePositionInLine?.ToString(CultureInfo.InvariantCulture) ?? "unknown",
                    string.IsNullOrEmpty(error.Path) ? string.Empty : $", at {error.Path}"),
                error);
        }

        if (parsed is not JsonObject root)
        {
            throw new CredentialFileFormatException(
                "The credential file must contain a JSON object with an Encrypted flag and the "
                + "application's secrets.");
        }

        string encryptedKey = RequireKey(root, EncryptedKey);
        bool encrypted = ReadFlag(root, encryptedKey);

        string sqlPasswordKey = RequireKey(root, SqlPasswordKey);
        string sqlPassword = ReadSecret(root, sqlPasswordKey)
            ?? throw new CredentialFileFormatException(
                   $"'{sqlPasswordKey}' is empty. Every environment authenticates to SQL Server with "
                   + "a password (AR3), so an empty one is a file that was seeded incompletely rather "
                   + "than an application that connects some other way.");

        string? apiIdKey = FindKey(root, ApiIdKey);
        string? apiKeyKey = FindKey(root, ApiKeyKey);
        string? apiId = apiIdKey is null ? null : ReadSecret(root, apiIdKey);
        string? apiKey = apiKeyKey is null ? null : ReadSecret(root, apiKeyKey);

        // The ID and the Key are one credential. Half of it is not a partial capability, it is an auth
        // call that fails at 2am for a reason the message will not explain.
        if ((apiId is null) != (apiKey is null))
        {
            throw new CredentialFileFormatException(
                $"'{ApiIdKey}' and '{ApiKeyKey}' are one credential and must be supplied together; "
                + $"this file has {(apiId is null ? ApiKeyKey : ApiIdKey)} only. The monitoring "
                + "application legitimately has neither (Analysis §6.1) — omit both, or leave both "
                + "empty.");
        }

        return new CredentialFile(
            root, encrypted, encryptedKey, sqlPasswordKey, sqlPassword,
            apiIdKey, apiId, apiKeyKey, apiKey);
    }

    /// <summary>Reads a credential file from disk. Convenience over <see cref="Parse"/>.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The parsed file.</returns>
    public static CredentialFile Read(string path) => Parse(File.ReadAllText(path));

    /// <summary>
    /// The secrets of an unsealed file, as they are.
    /// </summary>
    /// <returns>The plaintext credentials.</returns>
    /// <exception cref="InvalidOperationException">This file is sealed; use <see cref="Unseal"/>.</exception>
    public ApplicationCredentials ReadPlaintext()
    {
        if (Encrypted)
        {
            throw new InvalidOperationException(
                "This file records Encrypted: true, so its values are ciphertext. Call Unseal.");
        }

        return new ApplicationCredentials(sqlPasswordValue, apiIdValue, apiKeyValue);
    }

    /// <summary>Recovers the secrets of a sealed file.</summary>
    /// <param name="protector">The protector for the application this file belongs to.</param>
    /// <returns>The plaintext credentials.</returns>
    /// <exception cref="InvalidOperationException">This file is not sealed.</exception>
    /// <exception cref="System.Security.Cryptography.CryptographicException">
    /// A value could not be recovered. G5 row 3.
    /// </exception>
    public ApplicationCredentials Unseal(ISecretProtector protector)
    {
        ArgumentNullException.ThrowIfNull(protector);

        if (!Encrypted)
        {
            throw new InvalidOperationException(
                "This file records Encrypted: false, so its values are plaintext. Call ReadPlaintext.");
        }

        return new ApplicationCredentials(
            protector.Unprotect(sqlPasswordValue),
            apiIdValue is null ? null : protector.Unprotect(apiIdValue),
            apiKeyValue is null ? null : protector.Unprotect(apiKeyValue));
    }

    /// <summary>
    /// The text of this file with every secret sealed and the flag set to <c>true</c>. Everything else
    /// in the file — including properties this class does not know about — is preserved.
    /// </summary>
    /// <param name="protector">The protector for the application this file belongs to.</param>
    /// <returns>The new file text, ending in a newline.</returns>
    /// <exception cref="InvalidOperationException">This file is already sealed.</exception>
    /// <remarks>
    /// Returns text rather than writing it. The caller holds the file open exclusively and writes
    /// through that one handle, and every byte is produced before the handle is touched — so the only
    /// way the write can fail is the disk itself, rather than a protector throwing halfway through a
    /// file that has already been truncated.
    /// </remarks>
    public string Seal(ISecretProtector protector)
    {
        ArgumentNullException.ThrowIfNull(protector);

        if (Encrypted)
        {
            throw new InvalidOperationException("This file is already sealed.");
        }

        // A clone, so a failure part-way through leaves this instance describing the file that is still
        // on disk rather than a half-sealed object nobody can interpret.
        JsonObject resealed = root.DeepClone().AsObject();

        resealed[sqlPasswordKey] = protector.Protect(sqlPasswordValue);

        if (apiIdValue is not null && apiIdKey is not null)
        {
            resealed[apiIdKey] = protector.Protect(apiIdValue);
        }

        if (apiKeyValue is not null && apiKeyKey is not null)
        {
            resealed[apiKeyKey] = protector.Protect(apiKeyValue);
        }

        resealed[encryptedKey] = true;

        return resealed.ToJsonString(WriteOptions) + Environment.NewLine;
    }

    /// <summary>
    /// The text of a fresh, unsealed credential file for one application: the shape an operator fills
    /// in at plan §4.2 step 1.
    /// </summary>
    /// <param name="application">Which application's file. The monitor gets no API credentials.</param>
    /// <returns>The template text.</returns>
    /// <remarks>
    /// Generated rather than hand-written so that the template committed to the repository cannot drift
    /// from the parser that has to accept it; a test asserts the committed files are exactly this.
    /// </remarks>
    public static string SeedTemplate(ApplicationIdentity application)
    {
        JsonObject template = new() { [EncryptedKey] = false, [SqlPasswordKey] = string.Empty };

        if (application == ApplicationIdentity.Loader)
        {
            template[ApiIdKey] = string.Empty;
            template[ApiKeyKey] = string.Empty;
        }

        return template.ToJsonString(WriteOptions) + Environment.NewLine;
    }

    private static string RequireKey(JsonObject root, string name) =>
        FindKey(root, name)
        ?? throw new CredentialFileFormatException(
               $"The credential file has no '{name}' property. Seed it from secrets.Template.json "
               + "rather than adding the key by hand, so the whole shape is present.");

    private static string? FindKey(JsonObject root, string name)
    {
        string[] matches =
            [.. root.Select(property => property.Key)
                    .Where(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                    .Order(StringComparer.Ordinal)];

        if (matches.Length > 1)
        {
            throw new CredentialFileFormatException(
                $"The credential file spells '{name}' {matches.Length} ways "
                + $"({string.Join(", ", matches.Select(match => $"'{match}'"))}). Which one this "
                + "application would read is not something to leave to chance: on the Encrypted flag "
                + "a stale second spelling means the file re-seals its own ciphertext. Remove the "
                + "duplicates.");
        }

        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool ReadFlag(JsonObject root, string key)
    {
        JsonNode? node = root[key];

        if (node is JsonValue value)
        {
            if (value.TryGetValue(out bool flag))
            {
                return flag;
            }

            // "false" as a string is a common hand-edit and unambiguous; anything else is refused
            // rather than interpreted, because guessing wrong on this one key re-encrypts ciphertext.
            if (value.TryGetValue(out string? text))
            {
                if (bool.TryParse(text, out flag))
                {
                    return flag;
                }
            }
        }

        throw new CredentialFileFormatException(
            $"'{key}' must be true or false. It decides whether this application treats the stored "
            + "secrets as ciphertext, so it is not defaulted.");
    }

    private static string? ReadSecret(JsonObject root, string key)
    {
        JsonNode? node = root[key];

        if (node is null)
        {
            return null;
        }

        if (node is not JsonValue value || !value.TryGetValue(out string? text))
        {
            throw new CredentialFileFormatException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "'{0}' must be a JSON string. A secret written as a number or a boolean is a "
                    + "quoting mistake, and reading it as text anyway would seal whatever JSON "
                    + "happened to produce.",
                    key));
        }

        // Whitespace-only is absence. An operator who leaves a key in place with nothing in it means
        // "not supplied", and the alternative is sealing a space and failing to authenticate with it.
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
