using Interfold.Api.Services.Secrets;
using Interfold.Contracts.Secrets;
using Microsoft.Extensions.Options;
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

    /// <summary>
    /// Materialises <typeparamref name="TPost"/> from <paramref name="mock"/><c>.Object</c>,
    /// constructs a fresh <typeparamref name="TOptions"/>, runs
    /// <see cref="IPostConfigureOptions{TOptions}.PostConfigure(string?, TOptions)"/> against
    /// the requested bucket, and returns the patched options.
    /// </summary>
    /// <remarks>
    /// Collapses the 3-line "<c>new patcher; new options; patcher.PostConfigure(...)</c>"
    /// triad the three PostConfigure test files under this folder repeat verbatim. The
    /// <paramref name="name"/> parameter defaults to <see cref="Options.DefaultName"/> so
    /// the happy-path and missing-row tests stay one call; the named-instance guard tests
    /// pass an explicit bucket name (e.g. <c>"other-name"</c>) to exercise the
    /// <c>if (name != DefaultName) return;</c> branch inside each PostConfigure.
    /// <para>
    /// Not applicable to tests that call <see cref="IPostConfigureOptions{TOptions}.PostConfigure"/>
    /// twice on the same instance (idempotency guards), tests that expect PostConfigure to
    /// throw (malformed-seed guards), or tests that resolve the options via a full
    /// <see cref="Microsoft.Extensions.DependencyInjection.ServiceCollection"/> +
    /// <see cref="Microsoft.Extensions.Options.IOptionsMonitor{TOptions}"/> pipeline
    /// (missing-mandatory-row validators). Those keep the manual pattern because each of
    /// them needs a hand at the PostConfigure boundary rather than a return value.
    /// </para>
    /// </remarks>
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
