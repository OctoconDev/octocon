using Interfold.Api.Host.Services.Secrets;

namespace Interfold.IntegrationTests.Shared.TestServices;

public interface IWebFactoryFixture
{
    /// <summary>
    /// Session-shared factory. Every non-mutating test resolves via this property so the
    /// underlying <see cref="InterfoldWebApplicationFactory"/> (and any backend containers it
    /// depends on) is amortised across the whole session.
    /// </summary>
    InterfoldWebApplicationFactory Factory { get; }

    /// <summary>
    /// Construct a fresh, private <see cref="InterfoldWebApplicationFactory"/> with the exact
    /// same backend wiring as <see cref="Factory"/>, owned by the caller. Use when a test needs
    /// to mutate global-scope factory configuration (<c>WithConfiguration</c>,
    /// <c>UseSetting</c>, hosted-service replacements) that would otherwise poison the shared
    /// factory for every subsequent test in the same session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Ownership.</b> Callers own the returned factory and must dispose it (<c>await using</c>
    /// in an async test method). Reuse across tests is not supported — build one per test that
    /// needs isolation.
    /// </para>
    /// <para>
    /// <b>Cost.</b> Each call performs a full host build (including
    /// <see cref="SecretsPreBuildLoader"/>'s pre-Build fetch and
    /// the migration-strip DI rewrite for DB-backed runs). Reserve for tests that actually need
    /// isolation; do not use as a default.
    /// </para>
    /// <para>
    /// <b>Regression this closes.</b> Before this method existed, the avatar-multipart tests
    /// (<c>AvatarSourceTests.Api_SettingsAvatarMultipart_ReportsLocalSource</c>,
    /// <c>SettingsControllerTests.Api_SettingsAvatarMultipart_PersistsAndServesAvatar</c>,
    /// <c>SettingsControllerTests.Api_AlterAvatarMultipart_PersistsAndReflectsOnPublicAlter</c>)
    /// wrote a relative <c>OCTOCON_AVATAR_PUBLIC_BASE</c> value onto the session-shared factory
    /// via <c>WithConfiguration</c>. That value fails <c>[AbsoluteHttpUri]</c> validation on
    /// <c>StorageConfiguration.AvatarPublicBase</c>, so every subsequent request that resolved
    /// <c>IOptionsMonitor&lt;StorageConfiguration&gt;.Get()</c> (via <c>InterfoldPrincipalMiddleware</c>)
    /// threw <c>OptionsValidationException</c> and 500'd — cascading into ~50 downstream test
    /// failures that were hard to attribute to the actual cause. Isolating those three tests to
    /// their own factory removes the vector entirely.
    /// </para>
    /// </remarks>
    InterfoldWebApplicationFactory CreatePrivateFactory();
}
