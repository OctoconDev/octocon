using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Infrastructure.InMemory;

namespace Interfold.Api.UnitTests.Infrastructure;

// InMemoryRegionContext strip-before-hash invariant: scoped {region}:{rawId} and raw
// {rawId} shapes of the same principal must resolve to the same region, otherwise
// InMemory reads through a raw URL segment 404 against writes seeded through a JWT-
// derived scoped principal. Persistent-backend equivalent lives in RegionContextCachingTests.
public sealed class InMemoryRegionContextTests
{
    private const string RawId = "sys-region-context-test";

    [Test]
    public async Task ScopedAndRawFormsOfSamePrincipal_ResolveToSameRegion()
    {
        var ctx = new InMemoryRegionContext();

        var scoped = ctx.ResolveUserRegion(new($"nam:{RawId}"));
        var raw = ctx.ResolveUserRegion(new(RawId));

        await Assert.That(scoped).IsEqualTo(raw)
            .Because("A scoped nam:{rawId} JWT-derived principal and a raw {rawId} route-bound principal must land in the same region — otherwise InMemoryStorageKeys.ForSystem yields two different partition keys for the same user and public reads 404 with system_not_found.");
    }

    [Test]
    public async Task CrossRegionSameRawId_ResolveToSameRegion()
    {
        var ctx = new InMemoryRegionContext();

        var namScoped = ctx.ResolveUserRegion(new($"nam:{RawId}"));
        var eurScoped = ctx.ResolveUserRegion(new($"eur:{RawId}"));

        await Assert.That(namScoped).IsEqualTo(eurScoped)
            .Because("The region prefix is stripped, not gated on — a caller that happens to hold the id under a different region wire tag (rare but possible across the seven canonical regions) must not partition into a different physical bucket.");
    }

    // Large sample so per-process hash randomisation can't produce an all-one-region run
    // (P ≈ 4e-42 across 7 regions × 50 samples).
    [Test]
    public async Task DifferentPrincipals_ResolveAcrossMultipleRegions()
    {
        var ctx = new InMemoryRegionContext();

        var observed = new HashSet<ScyllaKeyspace>();
        for (var i = 0; i < 50; i++)
        {
            observed.Add(ctx.ResolveUserRegion(new($"sys-discriminator-{i:D4}")));
        }

        await Assert.That(observed.Count).IsGreaterThan(1)
            .Because("The resolver must still discriminate between principals — if it degenerates to a single region for every id (e.g. by hashing a constant, or by falling through to CurrentRegion for non-empty inputs), the InMemory backend loses its multi-region routing and every test sharing a WebApplicationFactory sees a single-partition collision surface.");
    }

    // Blank inputs must short-circuit to CurrentRegion — ScyllaKeyspaceResolver relies on
    // this default rather than throwing. All three IsNullOrWhiteSpace shapes are pinned so
    // a refactor to a single guard clause can't drop one.
    [Test]
    public async Task DefaultSystemId_FallsBackToCurrentRegion()
    {
        var ctx = new InMemoryRegionContext(currentRegion: ScyllaKeyspace.Eur);

        var region = ctx.ResolveUserRegion(default);

        await Assert.That(region).IsEqualTo(ScyllaKeyspace.Eur)
            .Because("default(SystemId).Value is null — this branch must fall through to the constructor-supplied CurrentRegion. The ScyllaKeyspaceResolver default-keyspace path depends on it and would NRE otherwise.");
    }

    [Test]
    public async Task EmptyInput_FallsBackToCurrentRegion()
    {
        var ctx = new InMemoryRegionContext(currentRegion: ScyllaKeyspace.Sam);

        var region = ctx.ResolveUserRegion(new(string.Empty));

        await Assert.That(region).IsEqualTo(ScyllaKeyspace.Sam)
            .Because("Empty id is the second IsNullOrWhiteSpace shape — pin all three (null-via-default, empty, whitespace) so a refactor to a single guard clause can't silently drop one branch.");
    }

    [Test]
    public async Task WhitespaceInput_FallsBackToCurrentRegion()
    {
        var ctx = new InMemoryRegionContext(currentRegion: ScyllaKeyspace.Sas);

        var region = ctx.ResolveUserRegion(new("   "));

        await Assert.That(region).IsEqualTo(ScyllaKeyspace.Sas)
            .Because("Whitespace-only id completes the IsNullOrWhiteSpace matrix.");
    }
}
