using Cassandra;
using Interfold.Alters.Domain.Abstractions.Repository;
using Interfold.Infrastructure.Scylla.Repository;
using Interfold.IntegrationTests.Shared;
using Interfold.IntegrationTests.Shared.TestServices;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Domain.Abstractions.Repository;
using Microsoft.Extensions.DependencyInjection;

namespace Interfold.Alters.IntegrationTests.Services.Scylla;

/// <summary>
/// Direct-to-repository coverage for <see cref="ScyllaAlterRepository"/>'s handling of
/// nullable UDT / column shapes. The integration suite covers happy-path CRUD end-to-end
/// through the HTTP pipeline (<c>AltersControllerTests</c> / <c>PublicSystemsControllerTests</c>),
/// but corrupt or partially-null on-disk rows can't be produced through the public API —
/// the wire boundary rejects them before they reach Scylla. Both scenarios below insert
/// the pathological row directly via the cluster session, then drive the production
/// <see cref="IAlterRepository"/> against it.
///
/// <para>
/// Companion to <see cref="ScyllaFrontingRepositoryNullTimeStartTests"/>: same fixture,
/// same "direct-CQL synthesise, then production-repo read" template, same rationale
/// (things that can't reach the DB via the wire but must not blow up the read path).
/// </para>
/// </summary>
[ClassDataSource<ScyllaWebFactoryFixture>(Shared = SharedType.PerTestSession)]
public sealed class ScyllaAlterRepositoryUdtNullTests(ScyllaWebFactoryFixture fixture) : BaseEndpointTest
{
    /// <summary>
    /// A row with <c>security_level = NULL</c> maps to <see cref="VisibilityLevel.Private"/>:
    /// non-friend viewers are filtered out; the owner still sees the alter.
    /// </summary>
    [Test]
    public async Task GetGuardedAsync_NullSecurityLevelOnRow_TreatsAsPrivate()
    {
        var factory = fixture.Factory;
        using var client = factory.CreateClient();

        var rawSystemId = TestIds.NewSystemId("sys-null-sec");
        var systemId = new SystemId(rawSystemId);
        var seededAlterId = await CreateAlterAsync(client, rawSystemId, "seed-alter");

        var alterRepo = factory.Services.GetRequiredService<IAlterRepository>();
        var (session, keyspace, normalizedSystemId) = await ScyllaDirectHarness.ResolveAsync(fixture, systemId);

        await session.ExecuteAsync(new SimpleStatement(
            $"INSERT INTO {keyspace}.alters (user_id, id, name, security_level) VALUES (?, ?, ?, ?)",
            normalizedSystemId,
            seededAlterId.Value,
            "seed-alter-nulled",
            (short?)null));

        await Assert.That(await alterRepo.GetGuardedAsync(systemId, seededAlterId, viewerSystemId: null))
            .IsNull()
            .Because("Null security_level must fail closed as Private, so a non-friend viewer does not see the alter.");

        var owner = await alterRepo.GetAsync(systemId, seededAlterId);
        await Assert.That(owner).IsNotNull();
        await Assert.That(owner!.SecurityLevel).IsEqualTo(VisibilityLevel.Private)
            .Because("The owner list/join path must still return the row; Private is the fail-closed stand-in for a missing code.");
    }

    /// <summary>
    /// The UDT list on <c>alters.fields</c> permits null values per column
    /// (<c>alter_field (id uuid, value text)</c>), so a partial-migration or manual write
    /// can produce a UDT entry with <c>value = NULL</c>. The projection must surface such
    /// entries with a null <see cref="AlterPublicFieldReadModel.Value"/> rather than
    /// throwing or silently dropping the whole entry — the field is still projection-visible
    /// (the alter has "opinions" about it), it just doesn't carry a value.
    /// </summary>
    [Test]
    public async Task GetGuardedAsync_PartiallyNullFieldUdt_EmitsFieldWithNullValue()
    {
        var factory = fixture.Factory;
        using var client = factory.CreateClient();

        var rawSystemId = TestIds.NewSystemId("sys-null-udt");
        var systemId = new SystemId(rawSystemId);
        var seededAlterId = await CreateAlterAsync(client, rawSystemId, "field-udt-alter");

        var settingsFields = factory.Services.GetRequiredService<ISettingsFieldRepository>();
        var alterRepo = factory.Services.GetRequiredService<IAlterRepository>();
        var (session, keyspace, normalizedSystemId) = await ScyllaDirectHarness.ResolveAsync(fixture, systemId);

        // Field definition drives the projection filter — without a matching definition,
        // ResolveGuardedFields skips the UDT entry entirely (that's the unrelated path).
        var fieldId = (await settingsFields.CreateAsync(
            systemId,
            "NullableField",
            FieldType.Text,
            VisibilityLevel.Public,
            locked: false,
            DateTime.UtcNow))!.Value;

        // Ensure the alter-field UDT mapping is registered on the cluster session
        // (production reads/writes register it lazily on first hit; the direct-CQL
        // write below binds a strongly-typed AlterFieldUdt and would otherwise hit
        // "No UDT descriptor found").
        ScyllaAlterRepository.EnsureAlterFieldUdtMapping(session, keyspace);

        var udtWithNullValue = new List<AlterFieldUdt>
        {
            new() { Id = fieldId.Value, Value = null },
        };

        await session.ExecuteAsync(new SimpleStatement(
            $"UPDATE {keyspace}.alters SET fields = ? WHERE user_id = ? AND id = ?",
            udtWithNullValue,
            normalizedSystemId,
            seededAlterId.Value));

        var view = await alterRepo.GetGuardedAsync(systemId, seededAlterId, viewerSystemId: null);

        using (Assert.Multiple())
        {
            await Assert.That(view).IsNotNull()
                .Because("A public-visibility alter with a null-valued field UDT still resolves for an anonymous viewer — the null value doesn't break the row's readability.");
            await Assert.That(view!.Fields.Count).IsEqualTo(1)
                .Because("The projection must surface exactly the one field the alter has an opinion about — the null Value is a stored opinion, not a missing definition.");
            await Assert.That(view.Fields[0].Id).IsEqualTo(fieldId);
            await Assert.That(view.Fields[0].Value).IsNull()
                .Because("The UDT's null Value must round-trip as a null AlterPublicFieldReadModel.Value — silently coercing to empty string would misrepresent \"no data\" as \"blank string\".");
        }
    }
}
