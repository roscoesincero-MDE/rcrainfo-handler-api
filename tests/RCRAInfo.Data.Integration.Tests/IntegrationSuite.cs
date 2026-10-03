namespace RCRAInfo.Data.Integration.Tests;

/// <summary>
/// The one collection every class in this suite belongs to, which makes the whole suite run serially.
/// </summary>
/// <remarks>
/// <para>
/// xUnit parallelises across classes by default, and that default is wrong here for two reasons that
/// have nothing to do with speed.
/// </para>
/// <para>
/// The first is that these tests share one permanent corpus. Two classes writing the same reserved
/// handler identifier at the same time would each see the other's row, and the merge procedure's
/// change detection would report an outcome neither test asked for — a failure that appears in
/// whichever test lost the race and moves when the machine gets faster.
/// </para>
/// <para>
/// The second is <c>logs.uspStartLoadRun</c>. It refuses a start while another run for the same
/// activity location is still <c>Running</c>, which is exactly the right behaviour for the loader and
/// exactly the wrong thing to race against. <see cref="Runs"/> passes <c>@AllowConcurrent = 1</c> so
/// the refusal is not what serialises the suite; this attribute is, because relying on the override
/// would make the suite depend on a flag whose whole purpose is to be used sparingly.
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class IntegrationSuite
{
    /// <summary>The collection name. Every test class in this assembly carries it.</summary>
    public const string Name = "RCRAInfo database";
}
