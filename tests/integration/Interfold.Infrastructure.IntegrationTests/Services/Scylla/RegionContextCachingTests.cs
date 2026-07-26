using Cassandra;
using Interfold.Infrastructure.Scylla;
using Interfold.IntegrationTests.Shared;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Interfold.Infrastructure.IntegrationTests.Services.Scylla;

public sealed class RegionContextCachingTests : BaseEndpointTest
{
    // A stub that throws if the session is ever accessed, proving the cache is always used.
    private sealed class ThrowingSessionProvider : IScyllaSessionProvider
    {
        public Task<ISession> GetSessionAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("DB must not be reached when cache is warm.");
    }

    private static ScyllaUserRegistryRegionContext BuildContext(string defaultRegion = "nam")
    {
        var persistenceOptions = Options.Create(new PersistenceConfiguration
        {
            ScyllaKeyspace = EnumWireExtensions.ParseScyllaKeyspace(defaultRegion),
        });
        return new(new ThrowingSessionProvider(),
            persistenceOptions,
            NullLogger<ScyllaUserRegistryRegionContext>.Instance);
    }

    [Test]
    public async Task ResolveUserRegion_FallsBackToDefault_WhenSessionThrowsAndCacheEmpty()
    {
        // ThrowingSessionProvider will throw; the catch block should return CurrentRegion.
        var ctx = BuildContext("eur");
        var result = ctx.ResolveUserRegion(new SystemId("user-123"));
        await Assert.That(result).IsEqualTo(ScyllaKeyspace.Eur);
    }

    [Test]
    public async Task ResolveUserRegion_UsesCachedRegion_AfterRegisterRegion()
    {
        var ctx = BuildContext("nam");
        ctx.RegisterRegion("user-abc", ScyllaKeyspace.Eur);

        // ThrowingSessionProvider must NOT be called; cache is warm.
        var result = ctx.ResolveUserRegion(new SystemId("user-abc"));
        await Assert.That(result).IsEqualTo(ScyllaKeyspace.Eur);
    }

    [Test]
    public async Task ResolveUserRegion_StripsLegacyPrefix_BeforeCacheLookup()
    {
        var ctx = BuildContext("nam");
        ctx.RegisterRegion("eas", ScyllaKeyspace.Nam);  // unrelated key must not interfere

        // The prefixed form should resolve via the same stripped key.
        ctx.RegisterRegion("eas:user-xyz", ScyllaKeyspace.Sam);
        var result = ctx.ResolveUserRegion(new SystemId("eas:user-xyz"));
        await Assert.That(result).IsEqualTo(ScyllaKeyspace.Sam);

        // Plain key lookup after prefix strip should also be cache-warm.
        var result2 = ctx.ResolveUserRegion(new SystemId("eas:user-xyz"));
        await Assert.That(result2).IsEqualTo(ScyllaKeyspace.Sam);
    }

    [Test]
    public async Task RegisterRegion_EmptyKey_DoesNotCorruptCache()
    {
        var ctx = BuildContext("nam");
        ctx.RegisterRegion("", ScyllaKeyspace.Eur);   // empty key — should be no-op

        // Fallback should still apply because nothing was cached ("user-1" resolution hits
        // the throwing session and falls back to the default region).
        var result = ctx.ResolveUserRegion(new SystemId("user-1"));
        await Assert.That(result).IsEqualTo(ScyllaKeyspace.Nam);
    }

    // ---------------- UserRegistryLookup-driven cache-key routing --------
    //
    // Pins the (cacheKey, UserRegistryLookup?) contract inside HandleForLookup — the
    // read-side helper that routes region-context cache reads by handle kind. The
    // invariants being locked in:
    //
    // - Bare id / "id:<raw>" / "<region>:<raw>" all cache under the same key (they all
    //   identify the same user_registry row).
    // - "username:X" / "discord:X" keep their full prefixed form as the cache key so a
    //   username "abcdefg" cannot spuriously alias the bare id "abcdefg".
    // - Unknown non-region prefixes ("xxx:abcdefg") don't get silently stripped — the
    //   whole input is the cache key AND the query value, matching the strict-rejection
    //   contract in UserRegistryLookup.TryParse's xml-doc.

    [Test]
    public async Task ResolveUserRegion_ExplicitIdPrefix_SharesCacheKeyWithBareId()
    {
        var ctx = BuildContext("nam");
        ctx.RegisterRegion("abcdefg", ScyllaKeyspace.Eur);

        var fromBare    = ctx.ResolveUserRegion(new SystemId("abcdefg"));
        var fromIdPrefix = ctx.ResolveUserRegion(new SystemId("id:abcdefg"));

        using (Assert.Multiple())
        {
            await Assert.That(fromBare).IsEqualTo(ScyllaKeyspace.Eur);
            await Assert.That(fromIdPrefix).IsEqualTo(ScyllaKeyspace.Eur)
                .Because("'id:abcdefg' and 'abcdefg' must resolve to the same cached entry — both are the strict 'this is a system id' assertion routing to user_registry.user_id.");
        }
    }

    [Test]
    public async Task ResolveUserRegion_UsernamePrefix_KeepsFullFormAsCacheKey()
    {
        // Register two entries whose after-colon halves collide: an "abcdefg" system id
        // in Eur, and a "username:abcdefg" (someone whose username happens to match a
        // 7-char alphanumeric shape) in Sam. If the cache key stripped the "username:"
        // prefix, the second RegisterRegion would overwrite the first and the bare
        // lookup would return the wrong region.
        var ctx = BuildContext("nam");
        ctx.RegisterRegion("abcdefg", ScyllaKeyspace.Eur);
        ctx.RegisterRegion("username:abcdefg", ScyllaKeyspace.Sam);

        using (Assert.Multiple())
        {
            await Assert.That(ctx.ResolveUserRegion(new SystemId("abcdefg"))).IsEqualTo(ScyllaKeyspace.Eur)
                .Because("The bare id must remain cached under its own key even after a username handle with the same after-colon shape was registered.");
            await Assert.That(ctx.ResolveUserRegion(new SystemId("username:abcdefg"))).IsEqualTo(ScyllaKeyspace.Sam)
                .Because("Username handles must round-trip through their prefixed cache key so username 'abcdefg' doesn't alias the bare id 'abcdefg'.");
        }
    }

    [Test]
    public async Task ResolveUserRegion_DiscordPrefix_KeepsFullFormAsCacheKey()
    {
        // Same anti-aliasing invariant as the username case: a Discord snowflake ("1234")
        // cached under its prefixed shape must not collide with a bare id "1234". These
        // aren't likely to overlap in production (Discord snowflakes are much longer
        // than the 7-char system-id alphabet), but the invariant is worth pinning so a
        // future test-fixture id shape can't silently poison the cache.
        var ctx = BuildContext("nam");
        ctx.RegisterRegion("1234", ScyllaKeyspace.Eur);
        ctx.RegisterRegion("discord:1234", ScyllaKeyspace.Sam);

        using (Assert.Multiple())
        {
            await Assert.That(ctx.ResolveUserRegion(new SystemId("1234"))).IsEqualTo(ScyllaKeyspace.Eur);
            await Assert.That(ctx.ResolveUserRegion(new SystemId("discord:1234"))).IsEqualTo(ScyllaKeyspace.Sam);
        }
    }

    [Test]
    public async Task ResolveUserRegion_UnknownPrefix_KeepsFullFormAsCacheKey()
    {
        // 'xxx' is not a region tag and not a discriminator prefix — UserRegistryLookup.TryParse
        // rejects it. The cache key must be the WHOLE original input so a rejected handle
        // is deterministic and round-trippable, matching the fallback branch in
        // LookupAsync that queries user_registry.user_id with the same whole input.
        var ctx = BuildContext("nam");
        ctx.RegisterRegion("xxx:abcdefg", ScyllaKeyspace.Sam);

        var result = ctx.ResolveUserRegion(new SystemId("xxx:abcdefg"));
        await Assert.That(result).IsEqualTo(ScyllaKeyspace.Sam)
            .Because("Unknown non-region prefix must NOT be silently stripped — the whole input is both the cache key and the eventual user_id query value.");
    }

    [Test]
    public async Task ResolveUserRegion_ExplicitIdPrefix_UsesCachedRegionSetViaBareRegister()
    {
        // The complementary direction of the "shared key" invariant: register with the
        // bare form, resolve via "id:" prefix, must not miss the cache. This is the
        // shape a controller sees when a client asks for /users/id:abcdefg but the
        // account layer registered the user under just "abcdefg".
        var ctx = BuildContext("nam");
        ctx.RegisterRegion("abcdefg", ScyllaKeyspace.Ocn);

        var result = ctx.ResolveUserRegion(new SystemId("id:abcdefg"));
        await Assert.That(result).IsEqualTo(ScyllaKeyspace.Ocn);
    }
}
