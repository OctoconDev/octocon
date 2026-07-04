using Interfold.Contracts.Enums;
using Interfold.Domain.Abstractions;
using Interfold.Infrastructure.Coordination;
using Interfold.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace Interfold.Api.UnitTests.Coordination;

/// <summary>
/// Exercises the <see cref="ServiceCollectionExtensions.AddInterfoldCluster(IServiceCollection, NodeGroup)"/>
/// factory for <see cref="IFCMService"/>. The factory encodes three routing rules:
/// <list type="number">
///   <item>Auxiliary / sidecar nodes always get <see cref="NullFCMService"/> regardless of secrets state.</item>
///   <item>Primary nodes with no <c>fcm:service_account_json</c> seeded get <see cref="NullFCMService"/> — the deployment hasn't wired Firebase.</item>
///   <item>Primary nodes with a non-blank <c>fcm:service_account_json</c> get <see cref="FirebaseFCMService"/>.</item>
/// </list>
/// If any of these rules break, unconfigured deployments will crash at the first
/// fronting change (or, worse, start silently dropping notifications).
/// </summary>
/// <remarks>
/// DI composition lives in <see cref="FCMServiceFactoryFixture"/>, injected via the
/// TUnit <see cref="ClassDataSourceAttribute{T}"/> pattern used across
/// <c>Interfold.IntegrationTests</c>. Tests just ask the fixture for a scenario scope,
/// resolve the service under test, and assert — no <c>ServiceCollection</c> plumbing
/// leaks into the test bodies.
/// </remarks>
[ClassDataSource<FCMServiceFactoryFixture>(Shared = SharedType.PerTestSession)]
public sealed class FCMServiceFactoryTests(FCMServiceFactoryFixture fixture)
{
    [Test]
    [Arguments(NodeGroup.Auxiliary)]
    [Arguments(NodeGroup.Sidecar)]
    public async Task NonPrimaryNode_AlwaysResolvesNullFCMService(NodeGroup role)
    {
        // Auxiliary/sidecar nodes never send push, so their secrets state shouldn't matter.
        // Even with a fully-seeded credential the factory should pin them to the no-op.
        await using var scope = fixture.CreateScope(role, seedServiceAccount: true);

        var resolved = scope.Services.GetRequiredService<IFCMService>();

        await Assert.That(resolved).IsTypeOf<NullFCMService>()
            .Because("Only primary nodes are meant to hold the send responsibility; auxiliaries would double-send if promoted.");
    }

    [Test]
    public async Task PrimaryNode_WithBlankServiceAccount_ResolvesNullFCMService()
    {
        // The dev / self-hosted "no Firebase wired" shape. If this branch regresses, every
        // fresh deployment crashes on first fronting change because FirebaseFCMService's
        // lazy init tries to build a GoogleCredential from an empty string.
        await using var scope = fixture.CreateScope(NodeGroup.Primary, seedServiceAccount: false);

        var resolved = scope.Services.GetRequiredService<IFCMService>();

        await Assert.That(resolved).IsTypeOf<NullFCMService>()
            .Because("The fcm:service_account_json row is absent — factory must fall back to the no-op rather than trying to construct FirebaseFCMService.");
    }

    [Test]
    public async Task PrimaryNode_WithSeededServiceAccount_ResolvesFirebaseFCMService()
    {
        await using var scope = fixture.CreateScope(NodeGroup.Primary, seedServiceAccount: true);

        var resolved = scope.Services.GetRequiredService<IFCMService>();

        await Assert.That(resolved).IsTypeOf<FirebaseFCMService>()
            .Because("The credential is present and the node owns the send responsibility — the factory must promote to the real sender.");
    }

    [Test]
    public async Task ResolutionIsStable_AcrossRepeatedRequests()
    {
        // IFCMService is registered as a singleton, so the factory should only run once
        // and subsequent resolutions must return the same instance. If someone changes the
        // registration lifetime, tests like FrontNotifierBackgroundService's would silently
        // hold onto a different instance than the one the request path resolves.
        await using var scope = fixture.CreateScope(NodeGroup.Primary, seedServiceAccount: true);

        var first = scope.Services.GetRequiredService<IFCMService>();
        var second = scope.Services.GetRequiredService<IFCMService>();

        await Assert.That(ReferenceEquals(first, second)).IsTrue()
            .Because("IFCMService is a singleton — a fresh instance per request would mean the factory's blocking secrets read runs on every notification flush.");
    }
}
