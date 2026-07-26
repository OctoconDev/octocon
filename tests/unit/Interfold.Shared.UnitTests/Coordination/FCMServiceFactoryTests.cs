using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Infrastructure.Coordination;
using Microsoft.Extensions.DependencyInjection;

namespace Interfold.Api.UnitTests.Coordination;

// AddInterfoldCluster's IFCMService factory encodes three routing rules: non-primary
// nodes always get NullFCMService; primary nodes without fcm:service_account_json get
// NullFCMService; primary nodes with the credential get FirebaseFCMService. A regression
// silently drops notifications or crashes at the first fronting change.
[ClassDataSource<FCMServiceFactoryFixture>(Shared = SharedType.PerTestSession)]
public sealed class FCMServiceFactoryTests(FCMServiceFactoryFixture fixture)
{
    [Test]
    [Arguments(NodeGroup.Auxiliary)]
    [Arguments(NodeGroup.Sidecar)]
    public async Task NonPrimaryNode_AlwaysResolvesNullFCMService(NodeGroup role)
    {
        await using var scope = fixture.CreateScope(role, seedServiceAccount: true);

        var resolved = scope.Services.GetRequiredService<IFCMService>();

        await Assert.That(resolved).IsTypeOf<NullFCMService>()
            .Because("Only primary nodes are meant to hold the send responsibility; auxiliaries would double-send if promoted.");
    }

    [Test]
    public async Task PrimaryNode_WithBlankServiceAccount_ResolvesNullFCMService()
    {
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
        await using var scope = fixture.CreateScope(NodeGroup.Primary, seedServiceAccount: true);

        var first = scope.Services.GetRequiredService<IFCMService>();
        var second = scope.Services.GetRequiredService<IFCMService>();

        await Assert.That(ReferenceEquals(first, second)).IsTrue()
            .Because("IFCMService is a singleton — a fresh instance per request would mean the factory's blocking secrets read runs on every notification flush.");
    }
}
