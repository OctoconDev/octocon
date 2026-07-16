using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Domain.Abstractions;
using Interfold.Infrastructure.InMemory;
using Interfold.Infrastructure.InMemory.Repository;
using Microsoft.Extensions.Time.Testing;

namespace Interfold.Api.UnitTests;

/// <summary>
/// Regression suite for <see cref="InMemoryAccountRepository"/>'s link-token map. Each
/// test targets one previously-latent failure mode:
///
///   * Bug A — no TTL on the reverse-map; a stale entry stayed resolvable indefinitely
///     even though the Scylla adapter enforced a 5-minute expiry.
///   * Bug B — <c>ResolveSystemIdByLinkTokenAsync</c> missed the scrub on miss, so a
///     re-issued deterministic-hash token would silently adopt a dangling pointer.
///   * Bug C — the reverse-map value was built via hand-concat
///     (<c>region + ":" + systemId.Value</c>), which double-prefixed any already-scoped
///     input to <c>"nam:nam:abcdefg"</c> and broke <c>ClearLinkTokenAsync</c>.
///
/// All three were invisible when exercised through the controller layer because the
/// controller normalised the id first. Direct-repository coverage is the guard against
/// reintroduction.
/// </summary>
public sealed class InMemoryAccountRepositoryLinkTokenRegressionTests
{
    private static readonly SystemId RawSystemId = new("nam:abcdefg");
    private static readonly SystemId AlreadyScopedSystemId = new("nam:abcdefg");

    /// <summary>
    /// Fixed-region stub so the tests don't depend on <see cref="InMemoryRegionContext"/>'s
    /// hash-routing, which would otherwise land <c>"nam:abcdefg"</c> in some non-NAM
    /// region and defeat the byte-form assertion below. The bug fix under test is about
    /// how the repository composes ids given a region — the routing algorithm is a
    /// different concern.
    /// </summary>
    private sealed class FixedRegionContext(ScyllaKeyspace region) : IRegionContext
    {
        public ScyllaKeyspace CurrentRegion { get; } = region;
        public ScyllaKeyspace ResolveUserRegion(SystemId systemId) => CurrentRegion;
    }

    // ---------------- Bug A — TTL honoured on Resolve --------------------

    /// <summary>
    /// A link token issued at T+0 must not resolve after the TTL window has elapsed.
    /// The InMemory adapter needs the same 5-minute expiry as Scylla; without it the
    /// test would silently return the systemId at T+1h.
    /// </summary>
    [Test]
    public async Task ResolveSystemIdByLinkTokenAsync_TokenPastTtl_ReturnsNull()
    {
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(start);
        var repo = new InMemoryAccountRepository(new FixedRegionContext(ScyllaKeyspace.Nam), timeProvider: clock);

        var token = await repo.GetOrCreateLinkTokenAsync(RawSystemId);

        // Advance to just past the 5-minute TTL window.
        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        var resolved = await repo.ResolveSystemIdByLinkTokenAsync(token);
        await Assert.That(resolved).IsNull()
            .Because("Bug A regression: a stale token past the 5-minute TTL must not resolve — otherwise the InMemory adapter diverges from Scylla's expiry contract.");
    }

    /// <summary>
    /// Symmetric TTL: the same expiry applies to <c>GetLinkTokenAsync</c>, which would
    /// otherwise advertise a token that <c>ResolveSystemIdByLinkTokenAsync</c> then
    /// refuses.
    /// </summary>
    [Test]
    public async Task GetLinkTokenAsync_TokenPastTtl_ReturnsNull()
    {
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(start);
        var repo = new InMemoryAccountRepository(new FixedRegionContext(ScyllaKeyspace.Nam), timeProvider: clock);

        await repo.GetOrCreateLinkTokenAsync(RawSystemId);
        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        var read = await repo.GetLinkTokenAsync(RawSystemId);
        await Assert.That(read).IsNull()
            .Because("Bug A regression: GetLinkTokenAsync must respect the same 5-minute TTL as ResolveSystemIdByLinkTokenAsync so a client-side check cannot diverge from the server-side gate.");
    }

    // ---------------- Bug B — miss scrubs the reverse map ----------------

    /// <summary>
    /// The deterministic-hash token derivation means the same systemId always produces
    /// the same reverse-map key. A miss on the reverse map (nonexistent or expired) must
    /// scrub the forward pointer so the next <c>GetOrCreateLinkTokenAsync</c> re-issues
    /// rather than silently adopting a dangling reverse-map pointer.
    /// </summary>
    [Test]
    public async Task ResolveSystemIdByLinkTokenAsync_ExpiredMiss_ScrubsForwardPointer()
    {
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(start);
        var repo = new InMemoryAccountRepository(new FixedRegionContext(ScyllaKeyspace.Nam), timeProvider: clock);

        var originalToken = await repo.GetOrCreateLinkTokenAsync(RawSystemId);
        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        // Miss on the expired token — this is the scrub trigger.
        var resolvedAfterExpiry = await repo.ResolveSystemIdByLinkTokenAsync(originalToken);
        await Assert.That(resolvedAfterExpiry).IsNull()
            .Because("Setup for bug B: the expired token must first be rejected so the scrub branch fires.");

        // A subsequent GetLinkTokenAsync must not report the stale forward pointer.
        var stalePointer = await repo.GetLinkTokenAsync(RawSystemId);
        await Assert.That(stalePointer).IsNull()
            .Because("Bug B regression: a resolve-side miss must scrub the forward pointer so GetLinkTokenAsync doesn't hand out a token whose reverse-map entry was just refused.");
    }

    // ---------------- Bug C — no double-prefix, ClearLinkToken works ----

    /// <summary>
    /// The reverse-map key must go through <c>ScopedSystemId.Compose</c>, not
    /// <c>region + ":" + systemId.Value</c>. Hand-concatenation would double-prefix an
    /// already-scoped input to <c>"nam:nam:abcdefg"</c> and break the round-trip through
    /// <c>ClearLinkTokenAsync</c>, which looks up the reverse-map by the forward-map
    /// token and expects to find the record it wrote.
    /// </summary>
    [Test]
    public async Task GetOrCreateLinkTokenAsync_AlreadyScopedInput_NoDoublePrefix_AndClearRoundTrips()
    {
        var repo = new InMemoryAccountRepository(new FixedRegionContext(ScyllaKeyspace.Nam));

        var issuedToken = await repo.GetOrCreateLinkTokenAsync(AlreadyScopedSystemId);
        var resolved = await repo.ResolveSystemIdByLinkTokenAsync(issuedToken);

        await Assert.That(resolved).IsNotNull()
            .Because("Bug C regression: a scoped-input token must resolve to the same single-prefixed id.");
        await Assert.That(resolved!.Value.Value).IsEqualTo("nam:abcdefg")
            .Because("The resolved id must be single-prefixed. Any double-prefix (e.g. \"nam:nam:abcdefg\") indicates the hand-concat pattern crept back in.");

        var cleared = await repo.ClearLinkTokenAsync(AlreadyScopedSystemId);
        await Assert.That(cleared).IsTrue()
            .Because("ClearLinkTokenAsync must succeed for the same scoped input that GetOrCreate accepted.");

        var postClear = await repo.ResolveSystemIdByLinkTokenAsync(issuedToken);
        await Assert.That(postClear).IsNull()
            .Because("Bug C regression: the clear path and the write path must use the same Compose call, otherwise a double-prefixed key survives ClearLinkTokenAsync because the two sides disagree on the key shape.");
    }
}
