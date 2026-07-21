using System.Collections.Concurrent;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Models.Read;
using Interfold.Api.UnitTests.Support;
using Interfold.Infrastructure.InMemory.Repository;
using Microsoft.Extensions.DependencyInjection;

namespace Interfold.Api.UnitTests;

/// <summary>
/// Direct-to-repository coverage for <see cref="InMemoryAlterRepository"/>. The
/// integration suite exercises alter behaviour end-to-end through the HTTP pipeline
/// (see <c>AltersControllerTests</c> / <c>PublicSystemsControllerTests</c>), but any
/// regression in the repository's projection logic that is masked by controller or
/// handler code slips through those tests. Option D's P4 gap: no direct repo unit
/// tests existed for the alter repositories — this file closes that gap on the
/// InMemory backend. The Scylla counterpart lives in <c>Interfold.IntegrationTests/
/// Services/Scylla/ScyllaAlterRepositoryUdtNullTests.cs</c> because it requires the
/// live Scylla fixture.
/// </summary>
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
        // Fixed region so the tests don't depend on InMemoryRegionContext's hash-routing
        // (which would land each principal in a different keyspace and break the
        // "everything talks to the same store" precondition the visibility tests need).
        var region = new FixedRegionContext(ScyllaKeyspace.Nam);
        var friendships = new InMemoryFriendshipRepository();
        // The SettingsField repo takes an IServiceProvider used only for the cascade
        // delete of alter field values. Our tests never delete field definitions, so
        // an empty provider is fine — no InMemoryAlterRepository is resolved from it.
        var fields = new InMemorySettingsFieldRepository(region, new ServiceCollection().BuildServiceProvider());
        var polls = new InMemoryPollRepository(region);
        var alters = new InMemoryAlterRepository(region, friendships, fields, polls);
        return new TestHarness(alters, friendships, fields);
    }

    /// <summary>
    /// Establishes a mutual friendship between <paramref name="a"/> and <paramref name="b"/>
    /// via the auto-accept branch of <c>SendRequestAsync</c> (mutual pending → linked).
    /// The InMemory friendship graph has no public "seed friends directly" affordance,
    /// so this goes through the same wire-facing surface production uses.
    /// </summary>
    private static async Task SeedFriendshipAsync(InMemoryFriendshipRepository friendships, SystemId a, SystemId b, bool trusted = false)
    {
        await friendships.SendRequestAsync(a, b);
        var outcome = await friendships.SendRequestAsync(b, a);
        await Assert.That(outcome).IsEqualTo(SendFriendRequestOutcome.Accepted)
            .Because("Mutual-request auto-accept is the only public seam that produces a linked friendship in the InMemory port.");

        if (trusted)
        {
            // SetTrustedAsync flips one side of the pair to TrustedFriend. The gate we
            // exercise reads owner→viewer, so tighten the owner→viewer entry specifically.
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

    // -------------------- Visibility matrix (list) ------------------------

    /// <summary>
    /// The full 4-visibility x 3-viewer matrix at the repository layer. Pins the
    /// same behaviour that <c>PublicSystemsControllerTests.Visibility_NonFriendFriendTrusted_*</c>
    /// asserts at the HTTP layer — the repo copy catches any drift caused by controller-
    /// or handler-layer filtering that would otherwise hide a repo-side regression.
    /// </summary>
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
            // Stranger and anonymous see the same slice (public-only).
            await Assert.That(strangerView.Select(a => a.Id).ToArray()).IsEquivalentTo(new[] { publicId });
            await Assert.That(anonymousView.Select(a => a.Id).ToArray()).IsEquivalentTo(new[] { publicId });

            // Friend sees Public + FriendsOnly.
            await Assert.That(friendView.Select(a => a.Id).ToArray()).IsEquivalentTo(new[] { publicId, friendsId });

            // Trusted friend sees Public + FriendsOnly + TrustedOnly.
            await Assert.That(trustedView.Select(a => a.Id).ToArray()).IsEquivalentTo(new[] { publicId, friendsId, trustedOnlyId });

            // Self-view is TrustedFriend-equivalent per InMemoryStorageKeys.ResolveFriendshipLevelAsync's
            // self-check: everything except Private.
            await Assert.That(selfView.Select(a => a.Id).ToArray()).IsEquivalentTo(new[] { publicId, friendsId, trustedOnlyId });
        }
    }

    // -------------------- Visibility matrix (get) -------------------------

    /// <summary>
    /// The single-alter counterpart to the list matrix. Same 4 x 3 grid — GetGuarded
    /// returns null (not the alter) when the viewer isn't allowed to see it, so we
    /// assert null-vs-non-null per cell.
    /// </summary>
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
            // Stranger: sees none.
            await Assert.That(await harness.Alters.GetGuardedAsync(OwnerId, friendsId, StrangerId)).IsNull();
            await Assert.That(await harness.Alters.GetGuardedAsync(OwnerId, trustedOnlyId, StrangerId)).IsNull();
            await Assert.That(await harness.Alters.GetGuardedAsync(OwnerId, privateId, StrangerId)).IsNull();

            // Friend: sees friends-only, not trusted-only or private.
            await Assert.That(await harness.Alters.GetGuardedAsync(OwnerId, friendsId, FriendId)).IsNotNull();
            await Assert.That(await harness.Alters.GetGuardedAsync(OwnerId, trustedOnlyId, FriendId)).IsNull();
            await Assert.That(await harness.Alters.GetGuardedAsync(OwnerId, privateId, FriendId)).IsNull();

            // Trusted: sees friends-only and trusted-only, not private.
            await Assert.That(await harness.Alters.GetGuardedAsync(OwnerId, friendsId, TrustedId)).IsNotNull();
            await Assert.That(await harness.Alters.GetGuardedAsync(OwnerId, trustedOnlyId, TrustedId)).IsNotNull();
            await Assert.That(await harness.Alters.GetGuardedAsync(OwnerId, privateId, TrustedId)).IsNull();

            // Anonymous viewer: sees none of the non-public ones.
            await Assert.That(await harness.Alters.GetGuardedAsync(OwnerId, friendsId, viewerSystemId: null)).IsNull();
            await Assert.That(await harness.Alters.GetGuardedAsync(OwnerId, privateId, viewerSystemId: null)).IsNull();
        }
    }

    // -------------------- Field projection matrix ------------------------

    /// <summary>
    /// Field-level security matrix at the repo layer. Fields with a higher security
    /// level than the viewer's friendship level must be projected out of the returned
    /// <see cref="AlterPublicFieldReadModel"/> list — never redacted, never present
    /// with a null value, just absent.
    /// </summary>
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

            // Stranger: public field only.
            await Assert.That(strangerView!.Fields.Select(f => f.Id).ToArray()).IsEquivalentTo(new[] { publicField });
            // Friend: public + friends fields.
            await Assert.That(friendView!.Fields.Select(f => f.Id).ToArray()).IsEquivalentTo(new[] { publicField, friendsField });
            // Trusted: all three fields.
            await Assert.That(trustedView!.Fields.Select(f => f.Id).ToArray()).IsEquivalentTo(new[] { publicField, friendsField, trustedField });
        }
    }

    /// <summary>
    /// If a definition exists but the alter has never populated a value for it, the
    /// guarded projection omits the definition entirely — see
    /// <see cref="Interfold.Domain.Alters.AlterFieldProjection.ResolveGuardedFields"/>.
    /// The unguarded read path is more permissive (emits one row per definition, null
    /// value when unset); the guarded path is intentionally stricter.
    /// </summary>
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

    // -------------------- Concurrency --------------------------------------

    /// <summary>
    /// <see cref="InMemoryAlterRepository.CreateAsync"/> hands out ids via
    /// <c>ConcurrentDictionary.AddOrUpdate</c> plus a <c>TryAdd</c> into the per-system
    /// store. Neither operation blocks the other, so N parallel writers should observe
    /// N distinct alters after the fan-in — no lost writes, no duplicate ids,
    /// counter never overflows the smallint range under normal loads. This test caps
    /// N at 200 to stay well below the <c>short</c> ceiling but far enough above any
    /// small race window that a regression would surface deterministically.
    /// </summary>
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
