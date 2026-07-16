using Interfold.Api.Services.Secrets;
using Interfold.Contracts.Secrets;
using TUnit.Mocks;

namespace Interfold.Api.UnitTests.Options;

/// <summary>
/// Shared <see cref="ISecretsSnapshot"/> mock builder for the PostConfigure test files
/// under this folder. Replaces three near-identical hand-rolled <c>StubSecretsSnapshot</c>
/// nested classes (one per test file) with a single fluent shape that reads the same as
/// the old <c>new StubSecretsSnapshot().With(key, value)</c> chain.
///
/// <para>
/// Loose-mode semantics carry the "missing row returns null" contract verbatim: the
/// generator's default for an unconfigured <c>string?</c>-returning method is <c>null</c>,
/// which is exactly what the pre-existing dictionary-backed stub did on a
/// <c>TryGetValue</c> miss. The "missing mandatory row trips validation" tests in
/// <see cref="AuthenticationSecretsPostConfigureTests"/> depend on that null-on-miss
/// behaviour, and continue to pass because loose-mode defaults preserve it.
/// </para>
/// </summary>
internal static class SecretsSnapshotMock
{
    /// <summary>
    /// A loose <see cref="ISecretsSnapshot"/> mock with <see cref="ISecretsSnapshot.IsPopulated"/>
    /// preconfigured to <see langword="true"/> — mirrors the "loader has run" invariant every
    /// PostConfigure test assumed on the old hand-rolled stub.
    /// </summary>
    public static Mock<ISecretsSnapshot> Empty()
    {
        var mock = ISecretsSnapshot.Mock();
        mock.IsPopulated.Returns(true);
        return mock;
    }

    /// <summary>
    /// Fluent per-row setup so the call sites keep their prior
    /// <c>new StubSecretsSnapshot().With(key, value)</c> shape verbatim.
    /// </summary>
    public static Mock<ISecretsSnapshot> With(this Mock<ISecretsSnapshot> mock, SecretsStoreKey key, string value)
    {
        mock.Get(key).Returns(value);
        return mock;
    }
}
