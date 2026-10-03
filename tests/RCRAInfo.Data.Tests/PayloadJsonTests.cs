using System.Text.Json;
using System.Text.RegularExpressions;

using RCRAInfo.Data.Payloads;

namespace RCRAInfo.Data.Tests;

/// <summary>
/// Pins the one serializer configuration that every set parameter goes through, against the JSON paths
/// the procedures actually read.
/// </summary>
/// <remarks>
/// <para>
/// This is the test the class remarks on <see cref="PayloadJson"/> promise. It exists because the
/// failure it guards has no symptom. <b>SQL Server matches JSON property names case-sensitively,
/// regardless of database collation, and <c>OPENJSON</c> over a path that matches nothing does not
/// error — it shreds to <c>NULL</c>.</b> So a serializer that emitted <c>HandlerId</c> where the
/// procedure reads <c>'$.handlerId'</c> would merge a batch of empty columns, return a row count that
/// looked right, and log a successful run.
/// </para>
/// <para>
/// Both sides are read from the working tree rather than restated here: the C# side by serialising a
/// fully-populated element, the SQL side by extracting the paths from the deployment scripts. There is
/// no list of property names in this file to fall out of step with either.
/// </para>
/// <para>
/// The comparison is deliberately BIDIRECTIONAL. A path the procedure reads and the payload does not
/// emit shreds to null; a property the payload emits and the procedure does not read is silently
/// dropped. The first is a data-loss bug and the second is either a dead property or a column somebody
/// meant to populate, and neither produces an error message.
/// </para>
/// </remarks>
public sealed class PayloadJsonTests
{
    /// <summary>
    /// Every payload-taking procedure, the script it lives in, and a fully-populated element of the
    /// type bound to it.
    /// </summary>
    /// <remarks>
    /// The elements set EVERY property, including the optional ones. A partially-populated element
    /// would make this test agree with the scripts by omission — <c>WhenWritingNull</c> drops a null
    /// property, so an unset property is indistinguishable from one the serializer names wrongly.
    /// </remarks>
    public static TheoryData<string, string, object> Payloads =>
        new()
        {
            {
                "400_dbo.uspMergeHandlerSourceBatch.sql",
                nameof(HandlerEnvelope),
                new HandlerEnvelope
                {
                    RetrievedDateUtc = new DateTimeOffset(2026, 9, 5, 14, 30, 0, TimeSpan.Zero),

                    // EPA's own JSON, forwarded verbatim. The 212 '$.handler.*' paths in script 400
                    // were generated from the same pinned swagger spec that generated
                    // dbo.HandlerSource, so they are checked by build/generate_schema.py --check
                    // rather than here; what this test covers is the envelope around it.
                    Handler = JsonDocument.Parse("""{"handlerId":"MDD000000001"}""").RootElement,
                }
            },
            {
                "520_logs.uspUpsertHandlerLoadStatusSet.sql",
                nameof(HandlerLoadStatusElement),
                new HandlerLoadStatusElement
                {
                    HandlerId = "MDD000000001",
                    ActivityLocation = "MD",
                    SourceType = "P",
                    Sequence = 1,
                    AttemptNumber = 2,
                    HttpStatusCode = 503,
                    ApiErrorCode = "E_SERVICE",
                    ApiErrorMessage = "Service unavailable.",
                    ApiErrorId = "00000000-0000-0000-0000-000000000000",
                    ApiErrorDate = new DateTimeOffset(2026, 9, 5, 14, 30, 0, TimeSpan.Zero),
                }
            },
            {
                "521_dbo.uspReconcileCurrentRecord.sql",
                nameof(HandlerVersionElement),
                new HandlerVersionElement
                {
                    HandlerId = "MDD000000001",
                    SourceType = "P",
                    Sequence = 1,
                    CurrentRecord = true,

                    // [R43] Nullable, and set here because this test's contract is that a FULLY
                    // populated element emits every path its procedure reads. Leaving it null passes
                    // the procedure a payload with no receivedDate at all, which is a case script 521
                    // accepts on purpose but is not the case this test is asking about.
                    ReceivedDate = new DateOnly(2026, 5, 11),
                }
            },
            {
                "522_dbo.uspSoftDeleteHandlerSourceSet.sql",
                nameof(HandlerKeyElement),
                new HandlerKeyElement
                {
                    HandlerId = "MDD000000001",
                    SourceType = "P",
                    Sequence = 1,
                }
            },
            {
                "523_dbo.uspRefreshLookupSet.sql",
                nameof(LookupElement),
                new LookupElement
                {
                    ActivityLocation = "MD",
                    Code = "1",
                    Description = "Large Quantity Generator",
                    Active = true,
                    SortOrder = 10L,
                    CodeType = "FED",
                    Acute = false,
                    IndustryApp = false,
                    BrLoadActive = true,

                    // Both nested collections are LookupElement again, so the property names inside
                    // them are the same names this test already checks at the root. Set here only so
                    // that the root object emits them at all.
                    EpisodicType = new LookupElement { Code = "EP" },
                    Counties = [new LookupElement { Code = "003" }],
                }
            },
            {
                "524_logs.uspRecordHandlerLoadAttemptSet.sql",
                nameof(HandlerLoadAttemptElement),
                new HandlerLoadAttemptElement
                {
                    HandlerId = "MDD000000001",
                    SourceType = "P",
                    Sequence = 1,
                    AttemptNumber = 2,
                    StartedDateUtc = new DateTimeOffset(2026, 9, 5, 14, 30, 0, TimeSpan.Zero),
                    CompletedDateUtc = new DateTimeOffset(2026, 9, 5, 14, 30, 2, TimeSpan.Zero),
                    DurationMs = 2000,
                    Outcome = "Throttled",
                    HttpStatusCode = 429,

                    // A bare path, which is also the only shape the procedure will store unchanged.
                    RequestPath = "/api/v1/hd/sources/MDD000000001/P/1",
                    ResponseBytes = 214,
                    RetryAfterSeconds = 30,
                    ApiErrorCode = "E_RateLimitExceeded",
                    ApiErrorMessage = "Too many requests.",
                    ApiErrorId = "00000000-0000-0000-0000-000000000000",
                    ApiErrorDate = new DateTimeOffset(2026, 9, 5, 14, 30, 0, TimeSpan.Zero),
                    FailureMessage = "Throttled by RCRAInfo; retrying after the interval it asked for.",
                }
            },
        };

    /// <summary>
    /// A fully-populated payload element emits exactly the top-level property names that its procedure
    /// reads, with the same spelling and the same case.
    /// </summary>
    /// <param name="scriptName">The deployment script holding the procedure.</param>
    /// <param name="typeName">The payload type, for the failure message.</param>
    /// <param name="element">A fully-populated element of that type.</param>
    [Theory]
    [MemberData(nameof(Payloads))]
    public void EveryPathAProcedureReadsIsEmittedWithTheSameCase(
        string scriptName, string typeName, object element)
    {
        HashSet<string> read = TopLevelJsonPaths(TestPaths.ReadSqlScript(scriptName));

        Assert.NotEmpty(read);

        HashSet<string> emitted = TopLevelPropertyNames(element);

        string[] neverEmitted = [.. read.Except(emitted, StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        string[] neverRead = [.. emitted.Except(read, StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        Assert.True(
            neverEmitted.Length == 0,
            $"{scriptName} reads {string.Join(", ", neverEmitted.Select(p => $"'$.{p}'"))}, which " +
            $"{typeName} does not emit under that exact name. OPENJSON matches case-sensitively and " +
            "a path that matches nothing shreds to NULL, so this batch would merge empty columns and " +
            "report success. Compare against the emitted names: " +
            string.Join(", ", emitted.Order(StringComparer.Ordinal)));

        Assert.True(
            neverRead.Length == 0,
            $"{typeName} emits {string.Join(", ", neverRead)}, which {scriptName} never reads. The " +
            "value crosses the wire in an NVARCHAR (MAX) parameter and is then discarded without a " +
            "word. Either the procedure is missing a column or the property is dead.");
    }

    /// <summary>
    /// The naming policy is camelCase, asserted on a name where the two policies differ by more than
    /// the first letter. <c>HandlerId</c> would pass a test that only checked the first character even
    /// under a policy that lowercased everything.
    /// </summary>
    [Fact]
    public void TheNamingPolicyIsCamelCaseAndNotMerelyLowercase()
    {
        string json = PayloadJson.Serialize([new HandlerKeyElement
        {
            HandlerId = "MDD000000001",
            SourceType = "P",
            Sequence = 1,
        }], maxElements: 1).Json;

        Assert.Contains("\"handlerId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"sourceType\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"handlerid\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"HandlerId\"", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <see cref="DateTimeOffset"/> at a non-zero offset is written as the equivalent UTC instant.
    /// </summary>
    /// <remarks>
    /// The measured behaviour this guards: <c>TRY_CAST</c> to <c>DATETIME2</c> accepts an ISO 8601
    /// string with an offset and <b>discards</b> the offset rather than applying it. So an unconverted
    /// <c>-05:00</c> would land in a column named <c>…DateUtc</c> five hours wrong, with nothing
    /// failing anywhere.
    /// </remarks>
    [Fact]
    public void ADateTimeOffsetIsWrittenAsTheEquivalentUtcInstant()
    {
        // 09:30 at -05:00 is 14:30Z.
        string json = PayloadJson.Serialize([new HandlerLoadStatusElement
        {
            HandlerId = "MDD000000001",
            SourceType = "P",
            Sequence = 1,
            ApiErrorDate = new DateTimeOffset(2026, 9, 5, 9, 30, 0, TimeSpan.FromHours(-5)),
        }], maxElements: 1).Json;

        Assert.Contains("\"apiErrorDate\":\"2026-09-05T14:30:00.0000000Z\"", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// Seven fractional digits, matching <c>DATETIME2</c>'s default scale. Fewer would lose precision
    /// the column can hold, and a digest comparison over a round-tripped value would then never match
    /// — which reads as "this handler changed" on every run.
    /// </summary>
    [Fact]
    public void SevenFractionalDigitsAreWritten()
    {
        var instant = new DateTimeOffset(2026, 9, 5, 14, 30, 0, TimeSpan.Zero).AddTicks(1234567);

        string json = PayloadJson.Serialize([new HandlerEnvelope
        {
            RetrievedDateUtc = instant,
            Handler = JsonDocument.Parse("{}").RootElement,
        }], maxElements: 1).Json;

        Assert.Contains("\"retrievedDateUtc\":\"2026-09-05T14:30:00.1234567Z\"", json, StringComparison.Ordinal);
    }

    /// <summary>An empty batch serialises to an empty JSON array, which every procedure treats as a
    /// documented no-op rather than an error.</summary>
    [Fact]
    public void AnEmptyBatchIsAnEmptyArray()
    {
        PayloadBatch batch = PayloadJson.Serialize(Array.Empty<HandlerKeyElement>(), maxElements: 10);

        Assert.Equal("[]", batch.Json);
        Assert.Equal(0, batch.ElementCount);
        PayloadJson.AssertJsonArray(batch.Json);
    }

    /// <summary>A batch over the configured maximum is refused before anything is sent.</summary>
    [Fact]
    public void ABatchOverTheMaximumIsRefused()
    {
        HandlerKeyElement[] elements =
        [
            .. Enumerable.Range(0, 3).Select(i => new HandlerKeyElement
            {
                HandlerId = "MDD00000000" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                SourceType = "P",
                Sequence = 1,
            })
        ];

        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => PayloadJson.Serialize(elements, maxElements: 2));

        Assert.Equal(3, exception.ActualValue);
    }

    /// <summary>A non-positive maximum is a caller error, not a way to disable the limit.</summary>
    [Fact]
    public void ANonPositiveMaximumIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PayloadJson.Serialize(Array.Empty<HandlerKeyElement>(), maxElements: 0));

    /// <summary>
    /// <see cref="PayloadJson.AssertJsonArray"/> accepts an array and rejects everything else,
    /// including the JSON that is well-formed but wrongly shaped.
    /// </summary>
    /// <param name="json">Candidate payload text.</param>
    /// <param name="valid">Whether it is an acceptable payload.</param>
    /// <remarks>
    /// The bare-object case is the one worth having a test for. It is valid JSON, so <c>ISJSON</c>
    /// passes it, but <c>OPENJSON</c> over an object enumerates its PROPERTIES rather than its
    /// elements — so every path misses, the batch merges nulls, and the procedure reports success.
    /// </remarks>
    [Theory]
    [InlineData("[]", true)]
    [InlineData("[{\"handlerId\":\"MDD000000001\"}]", true)]
    [InlineData("{\"handlerId\":\"MDD000000001\"}", false)]
    [InlineData("\"MDD000000001\"", false)]
    [InlineData("null", false)]
    [InlineData("123", false)]
    [InlineData("", false)]
    [InlineData("[{\"handlerId\":}]", false)]
    public void AssertJsonArrayAcceptsOnlyAJsonArray(string json, bool valid)
    {
        if (valid)
        {
            PayloadJson.AssertJsonArray(json);
            return;
        }

        Assert.Throws<InvalidOperationException>(() => PayloadJson.AssertJsonArray(json));
    }

    /// <summary>
    /// The digest is 64 lowercase hex characters and is stable for identical text. The column is
    /// <c>NVARCHAR (64)</c> and every "has this handler changed" comparison is an equality test against
    /// it, so a second implementation differing only in case would make every comparison miss — which
    /// reads as "changed" on every single run.
    /// </summary>
    [Fact]
    public void TheDigestIs64LowercaseHexCharactersAndIsStable()
    {
        string digest = PayloadJson.Sha256("MDD000000001");

        Assert.Equal(64, digest.Length);
        Assert.Equal(digest.ToLowerInvariant(), digest, StringComparer.Ordinal);
        Assert.Matches("^[0-9a-f]{64}$", digest);
        Assert.Equal(digest, PayloadJson.Sha256("MDD000000001"), StringComparer.Ordinal);
        Assert.NotEqual(digest, PayloadJson.Sha256("MDD000000002"), StringComparer.Ordinal);
    }

    /// <summary>The digest a batch carries is the digest of the JSON it carries.</summary>
    [Fact]
    public void TheBatchDigestMatchesItsOwnJson()
    {
        PayloadBatch batch = PayloadJson.Serialize([new HandlerKeyElement
        {
            HandlerId = "MDD000000001",
            SourceType = "P",
            Sequence = 1,
        }], maxElements: 1);

        Assert.Equal(PayloadJson.Sha256(batch.Json), batch.Sha256, StringComparer.Ordinal);
        Assert.Equal(1, batch.ElementCount);
    }

    /// <summary>The top-level property names a payload element serialises to.</summary>
    /// <remarks>
    /// Serialised through <see cref="PayloadJson.Serialize"/> rather than
    /// <see cref="JsonSerializer"/> directly, so the test exercises the configuration the loader uses
    /// rather than a second copy of it — a test that built its own options would pass while the real
    /// path was misconfigured, which is the whole failure being guarded here.
    /// </remarks>
    private static HashSet<string> TopLevelPropertyNames(object element)
    {
        // T is object on purpose: System.Text.Json resolves a declared type of object to the value's
        // runtime type, so this needs no reflection over the generic method.
        PayloadBatch batch = PayloadJson.Serialize<object>([element], maxElements: 1);

        using JsonDocument document = JsonDocument.Parse(batch.Json);

        return [.. document.RootElement[0].EnumerateObject().Select(p => p.Name)];
    }

    /// <summary>
    /// The distinct first segments of the <c>'$.…'</c> JSON paths a script reads <b>relative to an
    /// element of the payload array</b>, with comments and nested shreds removed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Comments have to go first, and not as a tidiness measure: script 521's header block explains
    /// that <c>dbo.uspMergeHandlerSourceBatch</c> takes the flag from <c>'$.handler.currentRecord'</c>,
    /// and reading that sentence as a path this procedure uses would have the test demand a
    /// <c>handler</c> property on a payload type that correctly has none.
    /// </para>
    /// <para>
    /// <b>Nested shreds have to go too, and that is the harder half.</b> A path is only evidence about
    /// the payload TYPE when it is anchored at an element of the payload array. Script 400 shreds the
    /// child collections with <c>CROSS APPLY OPENJSON (arr.[value]) WITH (… '$.firstName' …)</c>, whose
    /// paths are relative to one owner, one contact, one waste code — not to the envelope. Scraping
    /// every <c>'$.…'</c> in the file used to be adequate and stopped being so the moment 400 grew
    /// those: it then demanded 44 properties of <see cref="HandlerEnvelope"/> that no envelope has ever
    /// carried, which is a guardrail failing on its own model of the script rather than on the script.
    /// </para>
    /// <para>
    /// So the paths are COLLECTED from the two places that are anchored at a payload element, rather
    /// than scraped and then filtered. The payload array is always shredded from a PARAMETER
    /// (<c>OPENJSON (@Payload)</c>, <c>OPENJSON (@Elements)</c>, <c>OPENJSON (@Summaries)</c>), so
    /// (a) the <c>WITH</c> clause of a parameter-sourced shred is envelope-relative, and (b) so is any
    /// <c>'$.…'</c> passed alongside the <c>value</c> column of the alias that shred binds — which is
    /// what puts <c>OPENJSON (e.[value], '$.counties')</c>, <c>JSON_PATH_EXISTS (e.[value], '$.…')</c>
    /// and <c>JSON_VALUE (e.[value], '$.…')</c> inside the check. A nested collection is always
    /// shredded from a CHILD alias's <c>value</c> column instead, and everything reached through one —
    /// <c>arr.[value]</c>, <c>pe.[value]</c> — is left out, because it describes an owner, a contact or
    /// a project rather than the envelope.
    /// </para>
    /// <para>
    /// Anchoring on the alias and not merely on "is it a column" is what the second attempt got wrong.
    /// Script 400 reads <c>OPENJSON (pe.[value], '$.wasteCodes')</c>, where <c>pe</c> is one episodic
    /// PROJECT: keeping every collection path regardless of alias made the test demand a
    /// <c>wasteCodes</c> property of the envelope, which is the same class of false failure in a
    /// different disguise.
    /// </para>
    /// </remarks>
    private static HashSet<string> TopLevelJsonPaths(string script)
    {
        string code = StripSqlComments(script);
        HashSet<string> paths = new(StringComparer.Ordinal);

        foreach (Match shred in ParameterSourcedShreds(code))
        {
            int open = code.IndexOf('(', shred.Index);
            int after = CloseOf(code, open) + 1;
            Match with = Regex.Match(
                code[after..], @"\A\s*WITH\s*\(",
                RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));

            // No WITH clause means the shred takes the default key/value/type projection and reads no
            // property names of its own; its alias is what the paths hang off instead.
            if (with.Success)
            {
                int withOpen = after + with.Length - 1;
                AddPaths(paths, code[withOpen..(CloseOf(code, withOpen) + 1)]);
            }

            // The alias bound here is the payload element. Every path handed to a JSON function
            // alongside its value column is relative to one element of the array.
            string? alias = AliasAfter(code, with.Success ? CloseOf(code, after + with.Length - 1) + 1 : after);

            if (alias is not null)
            {
                foreach (Match read in Regex.Matches(
                    code,
                    $@"\b{Regex.Escape(alias)}\s*\.\s*(?:\[value\]|value)\s*,\s*(?<path>'\$\.[^']*')",
                    RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5)))
                {
                    AddPaths(paths, read.Groups["path"].Value);
                }
            }
        }

        return paths;
    }

    /// <summary>Every <c>OPENJSON</c> whose source is a parameter — the shreds over the payload array
    /// itself.</summary>
    private static MatchCollection ParameterSourcedShreds(string code) =>
        Regex.Matches(code, @"OPENJSON\s*\(\s*@", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));

    private static void AddPaths(HashSet<string> paths, string fragment)
    {
        foreach (Match match in Regex.Matches(
            fragment, @"'\$\.(?<path>[A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)*)'",
            RegexOptions.None, TimeSpan.FromSeconds(5)))
        {
            paths.Add(match.Groups["path"].Value.Split('.', 2)[0]);
        }
    }

    /// <summary>The table alias introduced at <paramref name="at"/>, with or without <c>AS</c>.</summary>
    private static string? AliasAfter(string code, int at)
    {
        Match alias = Regex.Match(
            code[at..], @"\A\s*(?:AS\s+)?(?<alias>[A-Za-z_][A-Za-z0-9_]*)",
            RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));

        // A keyword here means the shred was not aliased at all -- there is nothing to hang a path off,
        // and treating one as an alias would match nothing rather than match wrongly.
        return alias.Success && !Keywords.Contains(alias.Groups["alias"].Value) ? alias.Groups["alias"].Value : null;
    }

    private static readonly HashSet<string> Keywords =
        new(StringComparer.OrdinalIgnoreCase) { "WHERE", "GROUP", "ORDER", "HAVING", "UNION", "CROSS", "OUTER", "INNER", "JOIN", "ON", "OPTION", "FOR" };

    /// <summary>
    /// The index of the parenthesis closing the one at <paramref name="open"/>, skipping string
    /// literals so that a bracket inside a path or a description cannot unbalance the count.
    /// </summary>
    private static int CloseOf(string code, int open)
    {
        int depth = 0;
        bool inString = false;

        for (int i = open; i < code.Length; i++)
        {
            if (inString)
            {
                inString = code[i] != '\'';
                continue;
            }

            switch (code[i])
            {
                case '\'':
                    inString = true;
                    break;

                case '(':
                    depth++;
                    break;

                case ')' when --depth == 0:
                    return i;
            }
        }

        return code.Length - 1;
    }

    /// <summary>Removes T-SQL block and line comments.</summary>
    /// <remarks>
    /// Adequate for these five scripts and no more than that: it does not model a <c>--</c> inside a
    /// string literal, and it does not need to, because none of them contains one. If that changes the
    /// test over-strips and fails loudly rather than quietly passing, which is the right direction for
    /// a helper this small.
    /// </remarks>
    private static string StripSqlComments(string script)
    {
        string withoutBlocks = Regex.Replace(
            script, @"/\*.*?\*/", " ",
            RegexOptions.Singleline, TimeSpan.FromSeconds(5));

        return Regex.Replace(
            withoutBlocks, @"--[^\r\n]*", " ",
            RegexOptions.None, TimeSpan.FromSeconds(5));
    }
}
