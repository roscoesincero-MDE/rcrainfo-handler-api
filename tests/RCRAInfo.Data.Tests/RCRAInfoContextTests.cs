using System.Reflection;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Options;

using RCRAInfo.Data.Results;

namespace RCRAInfo.Data.Tests;

/// <summary>
/// Asserts the model <see cref="RCRAInfoContext"/> builds, without connecting to anything.
/// </summary>
/// <remarks>
/// <para>
/// EF Core builds its model from metadata alone, so every test here runs offline against a connection
/// string that is never opened. That matters, because the three things being checked are exactly the
/// ones whose failure is invisible at run time:
/// </para>
/// <list type="number">
///   <item><description>
///   <b>Column names.</b> EF Core binds a <c>FromSqlRaw</c> result by column NAME. A property mapped to
///   a name the projection does not return stays at its type default on every row — null, 0, false —
///   and a grid drawing a blank column looks like a handler with no data rather than a broken mapping.
///   </description></item>
///   <item><description>
///   <b>The <c>Audit*</c> rename.</b> It is applied by a rule over every property whose name starts
///   with "Audit", which is safe only for as long as that prefix means the audit columns and nothing
///   else. A future <c>AuditorName</c> would be silently mapped to <c>auditorName</c>.
///   </description></item>
///   <item><description>
///   <b>The <see cref="DateTimeOffset"/> conversion.</b> Registered as a convention rather than per
///   property, so the check is that the convention reached all of them.
///   </description></item>
/// </list>
/// <para>
/// What is NOT here: anything that needs a server. Whether a procedure's projection matches the shape
/// is <c>build/check_result_shapes.py</c>, which asks the catalog.
/// </para>
/// </remarks>
public sealed class RCRAInfoContextTests
{
    /// <summary>The prefix on a C# property that maps to a camelCase audit column.</summary>
    private const string AuditPrefix = "Audit";

    /// <summary>The audit columns this database defines, as they are spelled in SQL.</summary>
    /// <remarks>
    /// Only the four modify/create columns appear in a projection. <c>IsDeleted</c>,
    /// <c>auditDeletedBy</c> and <c>auditDeletedDateUtc</c> exist on every table and are never
    /// projected: a read path that could return a soft-deleted row would be the more serious finding.
    /// </remarks>
    private static readonly string[] AuditColumns =
    [
        "auditCreatedBy",
        "auditCreatedDateUtc",
        "auditModifiedBy",
        "auditModifiedDateUtc",
    ];

    /// <summary>Every result type registered on the model.</summary>
    public static TheoryData<Type> ResultTypes
    {
        get
        {
            TheoryData<Type> data = [];

            foreach (IEntityType entityType in Model.GetEntityTypes())
            {
                data.Add(entityType.ClrType);
            }

            return data;
        }
    }

    /// <summary>
    /// Every class in the Results namespace is registered, and nothing else is. An unregistered result
    /// type throws only when a method that uses it is first called, which in a scheduled console app is
    /// at 2am in production.
    /// </summary>
    [Fact]
    public void EveryResultClassIsRegisteredAndNothingElseIs()
    {
        string[] declared =
        [
            .. typeof(HandlerSourceGridRow).Assembly
                   .GetTypes()
                   .Where(t => t.IsClass
                               && t.IsPublic
                               && t.Namespace == typeof(HandlerSourceGridRow).Namespace)
                   .Select(t => t.Name)
                   .Order(StringComparer.Ordinal)
        ];

        string[] registered =
        [
            .. Model.GetEntityTypes().Select(e => e.ClrType.Name).Order(StringComparer.Ordinal)
        ];

        Assert.Equal(declared, registered);
    }

    /// <summary>
    /// Every result type is keyless. A key would put EF Core's identity resolution in front of the
    /// rows, which for a projection that repeats a handler across versions means the second version
    /// silently becoming a reference to the first.
    /// </summary>
    /// <param name="type">A registered result type.</param>
    [Theory]
    [MemberData(nameof(ResultTypes))]
    public void EveryResultTypeIsKeyless(Type type)
    {
        IEntityType entityType = Model.FindEntityType(type)!;

        Assert.Null(entityType.FindPrimaryKey());
    }

    /// <summary>
    /// Every property maps to a column named exactly as the property, except the audit columns, which
    /// map to their camelCase SQL spelling.
    /// </summary>
    /// <param name="type">A registered result type.</param>
    [Theory]
    [MemberData(nameof(ResultTypes))]
    public void EveryPropertyMapsToItsOwnName(Type type)
    {
        IEntityType entityType = Model.FindEntityType(type)!;
        List<string> wrong = [];

        foreach (IProperty property in entityType.GetProperties())
        {
            string expected = property.Name.StartsWith(AuditPrefix, StringComparison.Ordinal)
                ? "audit" + property.Name[AuditPrefix.Length..]
                : property.Name;

            string actual = property.GetColumnName();

            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                wrong.Add($"{property.Name} -> {actual}, expected {expected}");
            }
        }

        Assert.True(
            wrong.Count == 0,
            $"{type.Name} has {wrong.Count} property/column mismatch(es): {string.Join("; ", wrong)}. " +
            "EF Core binds a FromSqlRaw result by column name, so each of these stays at its type " +
            "default on every row.");
    }

    /// <summary>
    /// The only properties the <c>Audit</c> prefix catches are the audit columns. This is the assertion
    /// <c>RCRAInfoContext.OnModelCreating</c> names: the rename is a rule over a prefix rather than
    /// thirty-two explicit calls, and a rule is only safe while the prefix means what it meant when the
    /// rule was written.
    /// </summary>
    [Fact]
    public void ThePrefixRuleCatchesOnlyTheAuditColumns()
    {
        List<string> unexpected = [];

        foreach (IEntityType entityType in Model.GetEntityTypes())
        {
            foreach (IProperty property in entityType.GetProperties())
            {
                if (!property.Name.StartsWith(AuditPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                string column = property.GetColumnName();

                if (!AuditColumns.Contains(column, StringComparer.Ordinal))
                {
                    unexpected.Add($"{entityType.ClrType.Name}.{property.Name} -> {column}");
                }
            }
        }

        Assert.True(
            unexpected.Count == 0,
            $"{unexpected.Count} property/properties start with '{AuditPrefix}' but do not map to one " +
            $"of this database's audit columns ({string.Join(", ", AuditColumns)}): " +
            $"{string.Join("; ", unexpected)}. The rename in OnModelCreating lowercases the prefix on " +
            "everything it matches, so a property that merely begins with those five letters is mapped " +
            "to a column that does not exist and reads as null on every row.");
    }

    /// <summary>
    /// At least one audit column is actually mapped. Without this the previous test passes just as
    /// happily when the rename has been deleted and there is nothing left for it to examine.
    /// </summary>
    [Fact]
    public void TheAuditRenameIsReachingProperties()
    {
        string[] mapped =
        [
            .. Model.GetEntityTypes()
                    .SelectMany(e => e.GetProperties())
                    .Select(p => p.GetColumnName())
                    .Where(c => AuditColumns.Contains(c, StringComparer.Ordinal))
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
        ];

        Assert.NotEmpty(mapped);

        // No property is spelled with the SQL casing, which would be a style violation the analyzers
        // reject -- so every one of these names got here through the rename.
        Assert.DoesNotContain(
            typeof(HandlerSourceGridRow).Assembly
                .GetTypes()
                .Where(t => t.Namespace == typeof(HandlerSourceGridRow).Namespace)
                .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                .Select(p => p.Name),
            name => name.StartsWith("audit", StringComparison.Ordinal));
    }

    /// <summary>
    /// Every <see cref="DateTimeOffset"/> property carries the UTC value converter, and it round-trips
    /// an offset instant to the same instant.
    /// </summary>
    /// <remarks>
    /// The converter is registered by convention over the type, so the risk is not that one property
    /// was forgotten but that the convention was never applied at all — in which case EF Core would
    /// fall back to its own <c>datetimeoffset</c> mapping and read a <c>DATETIME2</c> column at whatever
    /// offset the provider chose. Twenty-two properties across nine shapes, checked as a set.
    /// </remarks>
    [Fact]
    public void EveryDateTimeOffsetPropertyRoundTripsThroughUtc()
    {
        IProperty[] instants =
        [
            .. Model.GetEntityTypes()
                    .SelectMany(e => e.GetProperties())
                    .Where(p => (Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType)
                                == typeof(DateTimeOffset))
        ];

        Assert.NotEmpty(instants);

        List<string> unconverted = [];

        foreach (IProperty property in instants)
        {
            var converter = property.GetValueConverter();

            if (converter is null)
            {
                unconverted.Add($"{property.DeclaringType.ClrType.Name}.{property.Name}");
                continue;
            }

            Assert.Equal(typeof(DateTime), converter.ProviderClrType);

            // 09:30 at -05:00 is 14:30Z going down, and comes back as the same instant.
            var original = new DateTimeOffset(2026, 9, 5, 9, 30, 0, TimeSpan.FromHours(-5));
            object stored = converter.ConvertToProvider(original)!;

            Assert.Equal(new DateTime(2026, 9, 5, 14, 30, 0, DateTimeKind.Unspecified), stored);
            Assert.Equal(original, (DateTimeOffset)converter.ConvertFromProvider(stored)!);
        }

        Assert.True(
            unconverted.Count == 0,
            $"{unconverted.Count} DateTimeOffset property/properties have no value converter, so the " +
            "convention in ConfigureConventions is not reaching them: " +
            $"{string.Join(", ", unconverted)}.");
    }

    /// <summary>
    /// No result property is a bare <see cref="DateTime"/>. A <c>DATETIME2</c> read into one arrives as
    /// <see cref="DateTimeKind.Unspecified"/>, so the monitoring app calling <c>ToLocalTime ()</c> on a
    /// column named <c>StartedDateUtc</c> shifts a value that was already UTC and displays the wrong
    /// time, with nothing failing and no way to notice but arithmetic.
    /// </summary>
    [Fact]
    public void NoResultPropertyIsABareDateTime()
    {
        string[] offenders =
        [
            .. typeof(HandlerSourceGridRow).Assembly
                   .GetTypes()
                   .Where(t => t.Namespace == typeof(HandlerSourceGridRow).Namespace)
                   .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                     .Select(p => (Type: t, Property: p)))
                   .Where(x => (Nullable.GetUnderlyingType(x.Property.PropertyType)
                                ?? x.Property.PropertyType) == typeof(DateTime))
                   .Select(x => $"{x.Type.Name}.{x.Property.Name}")
                   .Order(StringComparer.Ordinal)
        ];

        Assert.True(
            offenders.Length == 0,
            $"{offenders.Length} result property/properties are DateTime rather than DateTimeOffset: " +
            $"{string.Join(", ", offenders)}. Use DateTimeOffset for datetime2 and DateOnly for date. " +
            "build/check_result_shapes.py enforces the same mapping from the catalog side.");
    }

    /// <summary>
    /// The context exposes no public <see cref="DbSet{TEntity}"/>. One would let a caller write LINQ
    /// against a shape that has no table behind it and get an exception naming an object that does not
    /// exist in the database.
    /// </summary>
    [Fact]
    public void TheContextExposesNoPublicDbSet()
    {
        string[] sets =
        [
            .. typeof(RCRAInfoContext)
                   .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                   .Where(p => p.PropertyType.IsGenericType
                               && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
                   .Select(p => p.Name)
        ];

        Assert.True(
            sets.Length == 0,
            $"RCRAInfoContext exposes {string.Join(", ", sets)} as a DbSet. Every result shape is " +
            "reached through Set<T> inside the class; a public DbSet is a writable-looking handle on a " +
            "projection.");
    }

    /// <summary>
    /// The model, built once. Building it opens no connection, which is why the connection string below
    /// can be a placeholder — and has to be, since a test that needed a server would not be a unit test.
    /// </summary>
    private static IModel Model { get; } = BuildModel();

    private static IModel BuildModel()
    {
        DbContextOptions<RCRAInfoContext> options =
            new DbContextOptionsBuilder<RCRAInfoContext>()
                .UseSqlServer(
                    "Server=(model-only);Database=RCRAInfo;Integrated Security=true",
                    sqlServer => sqlServer.UseCompatibilityLevel(160))
                .Options;

        var dataOptions = Options.Create(new RCRAInfoDataOptions
        {
            ConnectionString = "Server=(model-only);Database=RCRAInfo;Integrated Security=true",
        });

        using var context = new RCRAInfoContext(options, dataOptions);
        return context.Model;
    }
}
