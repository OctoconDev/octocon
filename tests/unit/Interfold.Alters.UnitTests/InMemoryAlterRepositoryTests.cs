using System.Collections.Concurrent;
using Interfold.Alters.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Api.UnitTests.Support;
using Interfold.Friendships.Contracts.Models.Read;
using Interfold.Infrastructure.InMemory.Repository;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Interfold.Api.UnitTests;

// Direct-to-repository coverage for InMemoryAlterRepository — the visibility matrix
// and field-projection paths that HTTP-layer tests can mask.
public sealed class InMemoryAlterRepositoryTests
{
    private static readonly SystemId OwnerId = new("owner01");
    private static readonly SystemId FriendId = new("frnd001");
    private static readonly SystemId TrustedId = new("trstd01");
    private static readonly SystemId StrangerId = new("stngr01");

    private sealed record TestHarness(
        InMemoryAlterRepository Alters,
        InMemoryFriendshipRepository Friendships,
        InMemorySettingsFieldRepository Fields);

    private static TestHarness BuildHarness()
    {
        // Fixed region so hash-routing doesn't land principals in different keyspaces
        // and break the "everything talks to the same store" precondition.
        var region = new FixedRegionContext(ScyllaKeyspace.Nam);
        var friendships = new InMemoryFriendshipRepository();
        // Empty provider is safe: only used for cascade-delete of field definitions,
        // which these tests never trigger.
        var fields = new InMemorySettingsFieldRepository(region, new ServiceCollection().BuildServiceProvider());
        var polls = new InMemoryPollRepository(region);
        var alters = new InMemoryAlterRepository(region, friendships, fields, polls, NullLogger<InMemoryAlterRepository>.Instance);
        return new TestHarness(alters, friendships, fields);
    }

    // Mutual auto-accept is the only public seam that produces a linked friendship
    // in the InMemory port; SetTrustedAsync tightens owner→viewer specifically.
    private static async Task SeedFriendshipAsync(InMemoryFriendshipRepository friendships, SystemId a, SystemId b, bool trusted = false)
    {
        await friendships.SendRequestAsync(a, b);
        var outcome = await friendships.SendRequestAsync(b, a);
        await Assert.That(outcome).IsEqualTo(SendFriendRequestOutcome.Accepted)
            .Because("Mutual-request auto-accept is the only public seam that produces a linked friendship in the InMemory port.");

        if (trusted)
        {
            await friendships.SetTrustedAsync(a, b, trusted: true);
        }
    }

    private static Task<AlterId> CreateAlterAsync(InMemoryAlterRepository alters, SystemId systemId, string name)
        => alters.CreateAsync(systemId, new CreateAlterCommand(name, DateTimeOffset.UtcNow))
            .ContinueWith(t => t.Result ?? throw new InvalidOperationException($"CreateAsync returned null for '{name}'."));

    private static Task<bool> SetVisibilityAsync(InMemoryAlterRepository alters, SystemId systemId, AlterId alterId, VisibilityLevel level)
        => alters.UpdateAsync(systemId, new UpdateAlterCommand
        {
            AlterId = alterId,
            SecurityLevel = level,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

    [Test]
    public async Task ListGuardedAsync_VisibilityMatrix_FiltersByViewer()
    {
        var harness = BuildHarness();

        var publicId = await CreateAlterAsync(harness.Alters, OwnerId, "Publicly Visible");
        var friendsId = await CreateAlterAsync(harness.Alters, OwnerId, "Friends Only");
        var trustedOnlyId = await CreateAlterAsync(harness.Alters, OwnerId, "Trusted Only");
        var privateId = await CreateAlterAsync(harness.Alters, OwnerId, "Private");

        await SetVisibilityAsync(harness.Alters, OwnerId, publicId, VisibilityLevel.Public);
        await SetVisibilityAsync(harness.Alters, OwnerId, friendsId, VisibilityLevel.FriendsOnly);
        await SetVisibilityAsync(harness.Alters, OwnerId, trustedOnlyId, VisibilityLevel.TrustedOnly);
        await SetVisibilityAsync(harness.Alters, OwnerId, privateId, VisibilityLevel.Private);

        await SeedFriendshipAsync(harness.Friendships, OwnerId, FriendId);
        await SeedFriendshipAsync(harness.Friendships, OwnerId, TrustedId, trusted: true);

        var strangerView = await harness.Alters.ListGuardedAsync(OwnerId, StrangerId);
        var anonymousView = await harness.Alters.ListGuardedAsync(OwnerId, viewerSystemId: null);
        var friendView = await harness.Alters.ListGuardedAsync(OwnerId, FriendId);
        var trustedView = await harness.Alters.ListGuardedAsync(OwnerId, TrustedId);
        var selfView = await harness.Alters.ListGuardedAsync(OwnerId, OwnerId);

        using (Assert.Multiple())
        {
            await Assert.That(strangerView.Select(a => a.Id).ToArray()).IsEquivalentTo(new[] { publicId });
            await Assert.That(anonymousView.Select(a => a.Id).ToArray()).IsEquivalentTo(new[] { publicId });
            await Assert.That(friendView.Select(a => a.Id).ToArray()).IsEquivalentTo(new[] { publicId, friendsId });
            await Assert.That(trustedView.Select(a => a.Id).ToArray()).IsEquivalentTo(new[] { publicId, friendsId, trustedOnlyId });
            // Self-view is TrustedFriend-equivalent per ResolveFriendshipLevelAsync's self-check.
            await Assert.That(selfView.Select(a => a.Id).ToArray()).IsEquivalentTo(new[] { publicId, friendsId, trustedOnlyId });
        }
    }

    [Test]
    public async Task GetGuardedAsync_VisibilityMatrix_FiltersByViewer()
    {
        var harness = BuildHarness();

        var friendsId = await CreateAlterAsync(harness.Alters, OwnerId, "Friends Only");
        var trustedOnlyId = await CreateAlterAsync(harness.Alters, OwnerId, "Trusted Only");
        var privateId = await CreateAlterAsync(harness.Alters, OwnerId, "Private");

        await SetVisibilityAsync(harness.Alters, OwnerId, friendsId, VisibilityLevel.FriendsOnly);
        await SetVisibilityAsync(harness.Alters, OwnerId, trustedOnlyId, VisibilityLevel.TrustedOnly);
        await SetVisibilityAsync(harness.Alters, OwnerId, privateId, VisibilityLevel.Private);

        await SeedFriendshipAsync(harness.Friendships, OwnerId, FriendId);
        await SeedFriendshipAsync(harness.Friendships, OwnerId, TrustedId, trusted: true);

        using (Assert.Multiple())
        {
            await Assert.That(await harness.Alters.GetGuardedAsync(OwnerId, friendsId, StrangerId)).IsNull();
            await Assert.That(await harness.Alters.GetGuardedAsync(OwnerId, trustedOnlyId, StrangerId)).IsNull();
            await Assert.That(await harness.Alters.GetGuardedAsync(OwnerId, privateId, StrangerId)).IsNull();

            await Assert.That(await harness.Alters.GetGuardedAsync(OwnerId, friendsId, FriendId)).IsNotNull();
            await Assert.That(await harness.Alters.GetGuardedAsync(OwnerId, trustedOnlyId, FriendId)).IsNull();
            await Assert.That(await harness.Alters.GetGuardedAsync(OwnerId, privateId, FriendId)).IsNull();

            await Assert.That(await harness.Alters.GetGuardedAsync(OwnerId, friendsId, TrustedId)).IsNotNull();
            await Assert.That(await harness.Alters.GetGuardedAsync(OwnerId, trustedOnlyId, TrustedId)).IsNotNull();
            await Assert.That(await harness.Alters.GetGuardedAsync(OwnerId, privateId, TrustedId)).IsNull();

            await Assert.That(await harness.Alters.GetGuardedAsync(OwnerId, friendsId, viewerSystemId: null)).IsNull();
            await Assert.That(await harness.Alters.GetGuardedAsync(OwnerId, privateId, viewerSystemId: null)).IsNull();
        }
    }

    // Field with a higher security level than the viewer's friendship level is projected
    // out entirely — never redacted, never present with a null value.
    [Test]
    public async Task GetGuardedAsync_FieldSecurityLevel_FiltersDefinitionsByViewer()
    {
        var harness = BuildHarness();

        var publicField = (await harness.Fields.CreateAsync(OwnerId, "PublicField", FieldType.Text, VisibilityLevel.Public, locked: false, DateTime.UtcNow))!.Value;
        var friendsField = (await harness.Fields.CreateAsync(OwnerId, "FriendsField", FieldType.Text, VisibilityLevel.FriendsOnly, locked: false, DateTime.UtcNow))!.Value;
        var trustedField = (await harness.Fields.CreateAsync(OwnerId, "TrustedField", FieldType.Text, VisibilityLevel.TrustedOnly, locked: false, DateTime.UtcNow))!.Value;

        var alterId = await CreateAlterAsync(harness.Alters, OwnerId, "Alter With Fields");
        await SetVisibilityAsync(harness.Alters, OwnerId, alterId, VisibilityLevel.Public);
        var updated = await harness.Alters.UpdateAsync(OwnerId, new UpdateAlterCommand
        {
            AlterId = alterId,
            Fields = new[]
            {
                new AlterFieldCommand(publicField, "public-value"),
                new AlterFieldCommand(friendsField, "friends-value"),
                new AlterFieldCommand(trustedField, "trusted-value"),
            },
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await Assert.That(updated).IsTrue();

        await SeedFriendshipAsync(harness.Friendships, OwnerId, FriendId);
        await SeedFriendshipAsync(harness.Friendships, OwnerId, TrustedId, trusted: true);

        var strangerView = await harness.Alters.GetGuardedAsync(OwnerId, alterId, StrangerId);
        var friendView = await harness.Alters.GetGuardedAsync(OwnerId, alterId, FriendId);
        var trustedView = await harness.Alters.GetGuardedAsync(OwnerId, alterId, TrustedId);

        using (Assert.Multiple())
        {
            await Assert.That(strangerView).IsNotNull();
            await Assert.That(friendView).IsNotNull();
            await Assert.That(trustedView).IsNotNull();

            await Assert.That(strangerView!.Fields.Select(f => f.Id).ToArray()).IsEquivalentTo(new[] { publicField });
            await Assert.That(friendView!.Fields.Select(f => f.Id).ToArray()).IsEquivalentTo(new[] { publicField, friendsField });
            await Assert.That(trustedView!.Fields.Select(f => f.Id).ToArray()).IsEquivalentTo(new[] { publicField, friendsField, trustedField });
        }
    }

    // Guarded projection drops definitions the alter hasn't populated; unguarded reads
    // are permissive and emit one row per definition with a null value when unset.
    [Test]
    public async Task GetGuardedAsync_MissingFieldValues_AreOmittedFromProjection()
    {
        var harness = BuildHarness();

        var populated = (await harness.Fields.CreateAsync(OwnerId, "Populated", FieldType.Text, VisibilityLevel.Public, locked: false, DateTime.UtcNow))!.Value;
        var unpopulated = (await harness.Fields.CreateAsync(OwnerId, "Unpopulated", FieldType.Text, VisibilityLevel.Public, locked: false, DateTime.UtcNow))!.Value;

        var alterId = await CreateAlterAsync(harness.Alters, OwnerId, "Sparse Alter");
        await SetVisibilityAsync(harness.Alters, OwnerId, alterId, VisibilityLevel.Public);
        await harness.Alters.UpdateAsync(OwnerId, new UpdateAlterCommand
        {
            AlterId = alterId,
            Fields = new[] { new AlterFieldCommand(populated, "the-only-value") },
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        var view = await harness.Alters.GetGuardedAsync(OwnerId, alterId, StrangerId);

        using (Assert.Multiple())
        {
            await Assert.That(view).IsNotNull();
            await Assert.That(view!.Fields.Count).IsEqualTo(1)
                .Because("The unpopulated definition must be projected out — the guarded path drops definitions the alter hasn't filled in.");
            await Assert.That(view.Fields[0].Id).IsEqualTo(populated);
            await Assert.That(view.Fields[0].Value).IsEqualTo("the-only-value");
            await Assert.That(view.Fields.Any(f => f.Id == unpopulated)).IsFalse();
        }
    }

    [Test]
    public async Task CreateAsync_UnderParallelWriters_PreservesAllAlters()
    {
        var harness = BuildHarness();
        const int writerCount = 200;

        var createdIds = new ConcurrentBag<AlterId>();

        await Parallel.ForEachAsync(
            Enumerable.Range(0, writerCount),
            new ParallelOptions { MaxDegreeOfParallelism = 32 },
            async (i, ct) =>
            {
                var id = await harness.Alters.CreateAsync(
                    OwnerId,
                    new CreateAlterCommand($"parallel-{i}", DateTimeOffset.UtcNow),
                    ct);
                if (id is not null)
                {
                    createdIds.Add(id.Value);
                }
            });

        var listed = await harness.Alters.ListAsync(OwnerId);

        using (Assert.Multiple())
        {
            await Assert.That(createdIds.Count).IsEqualTo(writerCount)
                .Because("Every CreateAsync must return a non-null AlterId under concurrent writers.");
            await Assert.That(createdIds.Distinct().Count()).IsEqualTo(writerCount)
                .Because("The AddOrUpdate counter must issue a distinct id per writer — a collision here would prove the increment isn't atomic.");
            await Assert.That(listed.Count).IsEqualTo(writerCount)
                .Because("The per-system store must contain every created alter after the fan-in.");
        }
    }
}
