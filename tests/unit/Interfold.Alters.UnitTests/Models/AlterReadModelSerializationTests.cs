using System.Text.Json;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;

namespace Interfold.Api.UnitTests.Models;

// PluralKit round-trip prerequisite: `discord_proxies` is the ONLY source for the
// PK v2 importer's `proxy_tags`, and `inserted_at` / `updated_at` are surfaced by
// the full-export path. All three live on the Scylla `alters` row (see
// infrastructure/Interfold.Infrastructure.Scylla/Migrations/002_create_interfold_schema.templated.cql:118-129)
// but are dropped by the current AlterReadModel — this test locks in the widening.
public sealed class AlterReadModelSerializationTests
{
    [Test]
    public async Task RoundTrip_PreservesDiscordProxiesAndTimestamps()
    {
        var original = new AlterReadModel(
            id: new AlterId(1),
            name: "Alpha",
            description: "d",
            avatarUrl: null,
            avatarSource: null,
            color: null,
            pronouns: null,
            securityLevel: VisibilityLevel.Public,
            fields: System.Array.Empty<AlterPublicFieldReadModel>(),
            proxyName: null,
            alias: null,
            untracked: false,
            archived: false,
            pinned: false,
            discordProxies: new[] { "heart;text moon" },
            insertedAt: new System.DateTime(2024, 1, 2, 3, 4, 5, System.DateTimeKind.Utc),
            updatedAt: new System.DateTime(2024, 6, 7, 8, 9, 10, System.DateTimeKind.Utc));

        var json = JsonSerializer.Serialize(original);
        var deserialised = JsonSerializer.Deserialize<AlterReadModel>(json)
            ?? throw new System.InvalidOperationException("AlterReadModel deserialised to null.");

        using (Assert.Multiple())
        {
            await Assert.That(deserialised.DiscordProxies).IsEquivalentTo(new[] { "heart;text moon" });
            await Assert.That(deserialised.InsertedAt).IsEqualTo(original.InsertedAt);
            await Assert.That(deserialised.UpdatedAt).IsEqualTo(original.UpdatedAt);
        }
    }
}
