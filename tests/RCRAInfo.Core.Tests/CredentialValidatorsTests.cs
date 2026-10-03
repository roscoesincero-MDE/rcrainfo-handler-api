using RCRAInfo.Core.Credentials;

namespace RCRAInfo.Core.Tests;

/// <summary>The <c>All</c> combinator: the order it runs in, where it stops, and what it refuses.</summary>
public sealed class CredentialValidatorsTests
{
    private static readonly ApplicationCredentials Credentials =
        new(Secrets.SqlPassword, Secrets.ApiId, Secrets.ApiKey);

    [Fact]
    public async Task EveryValidatorRunsWhenEveryOneAccepts()
    {
        RecordingValidator first = RecordingValidator.Accepts();
        RecordingValidator second = RecordingValidator.Accepts();

        CredentialValidation result =
            await CredentialValidators.All(first.Delegate, second.Delegate)(Credentials, default);

        Assert.True(result.IsValid);
        Assert.Equal(1, first.Calls);
        Assert.Equal(1, second.Calls);
    }

    [Fact]
    public async Task TheFirstRejectionEndsItAndTheRestAreNotRun()
    {
        // Short-circuiting is a decision about the message, not about speed. All the secrets in a file
        // are sealed together or not at all, so the first rejection already settles the outcome; a second
        // failure message about a credential that is fine leaves an operator guessing which to fix.
        RecordingValidator rejects = RecordingValidator.Rejects("Login failed for user 'RCRAInfoLoader'.");
        RecordingValidator never = RecordingValidator.Accepts();

        CredentialValidation result =
            await CredentialValidators.All(rejects.Delegate, never.Delegate)(Credentials, default);

        Assert.False(result.IsValid);
        Assert.Equal("Login failed for user 'RCRAInfoLoader'.", result.FailureMessage);
        Assert.Equal(0, never.Calls);
    }

    [Fact]
    public async Task ValidatorsRunInTheOrderTheyWereGiven()
    {
        // The order the loader composes in is SQL first, then the API credential, so that an unreachable
        // SQL Server is never reported as a problem with an API Key nothing tried.
        List<string> order = [];

        CredentialValidator Record(string name) =>
            (_, _) =>
            {
                order.Add(name);
                return Task.FromResult(CredentialValidation.Valid);
            };

        await CredentialValidators.All(Record("sql"), Record("api"))(Credentials, default);

        string[] expected = ["sql", "api"];
        Assert.Equal(expected, order);
    }

    [Fact]
    public async Task AThrownExceptionIsNotSwallowedIntoARejection()
    {
        // Deliberately left to the bootstrapper. It is the component that knows a thrown exception's
        // message may name a credential -- EPA's auth endpoint puts the API Key in the request URI -- and
        // reports the type name only. Catching here would produce a second, more helpful-looking message
        // built from text nobody had checked.
        RecordingValidator throws = RecordingValidator.Throws(new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CredentialValidators.All(throws.Delegate)(Credentials, default));
    }

    [Fact]
    public void ValidatingAgainstNothingIsRefused()
    {
        // An empty composition accepts everything, which would seal every secret in the file without
        // checking any of them. That is the precise failure AR4's validation step exists to prevent, so
        // it is an exception at composition time rather than a quiet pass at 2am.
        ArgumentException error = Assert.Throws<ArgumentException>(() => CredentialValidators.All());

        Assert.Contains("At least one validator", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANullValidatorIsRefused()
    {
        Assert.Throws<ArgumentException>(() => CredentialValidators.All(null!, RecordingValidator.Accepts().Delegate));
        Assert.Throws<ArgumentNullException>(() => CredentialValidators.All(null!));
    }

    [Fact]
    public async Task TheCompositionIsNotAffectedByLaterChangesToTheArrayItWasGiven()
    {
        // The array is copied, so a caller reusing a buffer cannot change what a composed validator does
        // after the fact -- which for this delegate would mean changing what gets sealed.
        RecordingValidator accepts = RecordingValidator.Accepts();
        CredentialValidator[] validators = [accepts.Delegate];

        CredentialValidator composed = CredentialValidators.All(validators);
        validators[0] = RecordingValidator.Rejects("replaced").Delegate;

        Assert.True((await composed(Credentials, default)).IsValid);
        Assert.Equal(1, accepts.Calls);
    }
}
