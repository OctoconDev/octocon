using Interfold.Api.Services.Secrets;
using Interfold.Shared.Contracts.Secrets;
using Microsoft.Extensions.Options;
using TUnit.Mocks;

namespace Interfold.Api.UnitTests.Options;

// Shared ISecretsSnapshot mock builder for the PostConfigure test files under this
// folder. Loose-mode default of null on unconfigured Get(...) preserves the
// "missing row returns null" contract every PostConfigure test relies on.
internal static class SecretsSnapshotMock
{
    // IsPopulated preset to true mirrors the "loader has run" invariant every
    // PostConfigure test assumes.
    public static Mock<ISecretsSnapshot> Empty()
    {
        var mock = ISecretsSnapshot.Mock();
        mock.IsPopulated.Returns(true);
        return mock;
    }

    public static Mock<ISecretsSnapshot> With(this Mock<ISecretsSnapshot> mock, SecretsStoreKey key, string value)
    {
        mock.Get(key).Returns(value);
        return mock;
    }

    // Not applicable to tests that call PostConfigure twice (idempotency guards),
    // tests that expect it to throw (malformed-seed guards), or tests that resolve
    // options via a full DI + IOptionsMonitor pipeline (missing-mandatory-row).
    public static TOptions ApplyPostConfigure<TPost, TOptions>(
        this Mock<ISecretsSnapshot> mock,
        string? name = null)
        where TPost : IPostConfigureOptions<TOptions>
        where TOptions : class, new()
    {
        var patcher = (TPost)Activator.CreateInstance(typeof(TPost), mock.Object)!;
        var options = new TOptions();
        patcher.PostConfigure(name ?? Microsoft.Extensions.Options.Options.DefaultName, options);
        return options;
    }
}
