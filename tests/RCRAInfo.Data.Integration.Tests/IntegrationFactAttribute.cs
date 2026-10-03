using System.Diagnostics.CodeAnalysis;

namespace RCRAInfo.Data.Integration.Tests;

/// <summary>A <see cref="FactAttribute"/> that skips itself unless the suite has been enabled.</summary>
/// <remarks>
/// <para>
/// <b>Why an attribute and not <c>Assert.Skip</c>:</b> because dynamic skip does not work in this
/// configuration, which was established by trying it rather than by reading about it. xunit 2.9.3 has no
/// <c>Assert.Skip</c> at all — it arrived on <c>Assert</c> in v3 — and its <c>SkipException.ForSkip</c>,
/// which does exist and does prefix the message with the <c>$XunitDynamicSkip$</c> token, produces a
/// <b>failed</b> test under xunit.runner.visualstudio 3.1.4: the token reaches the output verbatim and
/// the run reports one failure and zero skips. A suite whose opt-in mechanism reports every test as
/// failing when it is merely switched off would be indistinguishable from a broken suite.
/// </para>
/// <para>
/// <c>FactAttribute.Skip</c> is settable and is read at discovery, so a subclass that sets it in its
/// constructor is the mechanism xunit v2 actually supports. The cost is that the decision is made once
/// per test-assembly load rather than per test, which is correct here anyway: whether these tests may
/// write to a database is not a per-test question.
/// </para>
/// </remarks>
[SuppressMessage(
    "Design",
    "CA1813:Avoid unsealed attributes",
    Justification = "Sealed. Suppressed only because the analyzer also flags the file-scoped pair below.")]
public sealed class IntegrationFactAttribute : FactAttribute
{
    /// <summary>Initialises the attribute, skipping the test when the suite is disabled.</summary>
    public IntegrationFactAttribute() => Skip = IntegrationServer.SkipReason;
}

/// <summary>A <see cref="TheoryAttribute"/> that skips itself unless the suite has been enabled.</summary>
/// <remarks>
/// The counterpart to <see cref="IntegrationFactAttribute"/>, and skipped for the same reason. Note that
/// the <c>MemberData</c> feeding a theory is still enumerated at discovery even when the theory is
/// skipped, so a theory source must not need a server. The one in this suite reads the generated fixture
/// from the working tree, which is a file.
/// </remarks>
public sealed class IntegrationTheoryAttribute : TheoryAttribute
{
    /// <summary>Initialises the attribute, skipping the theory when the suite is disabled.</summary>
    public IntegrationTheoryAttribute() => Skip = IntegrationServer.SkipReason;
}

/// <summary>
/// An <see cref="IntegrationFactAttribute"/> that additionally needs the loader's real SQL password.
/// </summary>
/// <remarks>
/// <para>
/// AR4's accepting direction cannot be arranged from anything in this repository: proving that a password
/// works means having the password 050_Roles_and_Users.sql generated, and that value lives in the
/// password manager because it must live nowhere else. The runbook's paste step puts it in
/// <c>LoaderPassword</c> for the length of a session — the same variable
/// <c>build/check_permission_posture.py</c> reads, and read from the environment for the same reason it
/// is there: a command line ends up in the process list.
/// </para>
/// <para>
/// A <b>skip</b> rather than a green test with a comment. The obvious alternative is an early return
/// guarded by <c>Assert.True (true, "not set")</c>, and it is worse than useless: it reports a pass, so
/// the run says the accepting direction was verified when nothing ran. A skip with this reason attached
/// says which variable is missing, in the run output, where the person who can set it will see it.
/// </para>
/// </remarks>
public sealed class LoaderPasswordFactAttribute : FactAttribute
{
    /// <summary>The environment variable carrying the loader login's password.</summary>
    public const string Variable = "LoaderPassword";

    /// <summary>Initialises the attribute, skipping unless both the suite and the password are available.</summary>
    public LoaderPasswordFactAttribute() =>
        Skip = IntegrationServer.SkipReason
            ?? (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable))
                ? $"Set {Variable} to the RCRAInfoLoader password to exercise AR4's ACCEPTING " +
                  "direction. Every other test in this class proves a rejection, and a validator only " +
                  "ever shown to reject is indistinguishable from one that rejects everything. The " +
                  "value is in the password manager; the runbook's paste step sets this variable."
                : null);
}
