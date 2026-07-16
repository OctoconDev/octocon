using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Interfold.Api.Socket;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Events;
using Interfold.Contracts.Ids;
using Interfold.Domain;
using Interfold.Infrastructure;
using Interfold.Infrastructure.Coordination;

namespace Interfold.Api.UnitTests;

/// <summary>
/// Golden-byte guardrail on the scoped-id wire boundaries. Each fact below pins one
/// wire boundary that a stray <see cref="ScopedSystemId"/>.<c>RawId</c> swap (or any
/// regression that de-scopes an id before it hits persistence, a JWT, an event bus
/// filter, or a socket topic) would silently corrupt. If any of these fail, do NOT
/// "adjust the expected value" — treat it as a real wire-format break and audit the
/// diff for the offending site.
/// </summary>
public sealed class WireByteFreezeTests
{
    // Canonical shape: "nam:abcdefg" is the byte-form every wire boundary emits for a
    // NAM-region principal. Every test in this file constructs its id from these two
    // constants so a search for the literal points every reviewer at the same spot.
    private const string CanonicalScoped = "nam:abcdefg";
    private const string CanonicalRaw = "abcdefg";

    // ---------------- 1. EncryptionKey.DeriveKey — golden path ---------------------

    /// <summary>
    /// DeriveKey is a KDF: same inputs must produce the same output, and any accidental
    /// change to the wire form of <c>systemId</c> (e.g. handing the raw id to the KDF
    /// instead of the scoped one) would orphan every existing recovery code. This test
    /// pins:
    ///   * determinism across two calls with the same inputs, and
    ///   * scoped-vs-raw sensitivity — dropping the region prefix changes the derived key.
    /// The exact bytes are re-derived each run rather than hard-coded so a Konscious
    /// upstream tweak can't lock us into a stale expectation; the scoped-vs-raw
    /// inequality below is the wire-form freeze.
    /// </summary>
    [Test]
    public async Task DeriveKey_IsDeterministic_AndScopeSensitive()
    {
        // DeriveKey speaks wrappers end-to-end. The wire-form freeze is enforced by the
        // wrapper types at the call boundary — a caller who constructs
        // `new SystemId(CanonicalRaw)` gets a different derived key than
        // `new SystemId(CanonicalScoped)`, which is exactly the scoped-vs-raw sensitivity
        // this test pins.
        const string pepper = "test-pepper";
        RecoveryCode recoveryCode = new("test-code");
        EncryptionSalt salt = new(Convert.ToBase64String(Encoding.UTF8.GetBytes("known-salt-16b!!")));

        var first = EncryptionKey.DeriveKey(pepper, new(CanonicalScoped), recoveryCode, salt);
        var second = EncryptionKey.DeriveKey(pepper, new(CanonicalScoped), recoveryCode, salt);
        var rawOnly = EncryptionKey.DeriveKey(pepper, new(CanonicalRaw), recoveryCode, salt);

        await Assert.That(first).IsEqualTo(second)
            .Because("DeriveKey must be deterministic — a diff between two calls with identical inputs means Argon2 params or salt handling drifted.");
        await Assert.That(first).IsNotEqualTo(rawOnly)
            .Because("Dropping the region prefix from the systemId argument must change the derived key — otherwise a caller could silently swap scoped for raw and orphan the recovery flow.");
        await Assert.That(Convert.FromBase64String(first.Value).Length).IsEqualTo(32)
            .Because("Argon2id hash_len is pinned at 32 in EncryptionKey.DeriveKey.");
    }

    // ---------------- 2. AuthHelper.CreateToken — JWT sub emission -----------------

    /// <summary>
    /// The JWT <c>sub</c> claim is the one place a scoped-id string crosses the trust
    /// boundary out of the process; <c>InterfoldPrincipalMiddleware</c> requires it to
    /// arrive back as a scoped composite on the inbound side. This test pins that
    /// <c>AuthHelper.CreateToken</c> emits the composite verbatim when handed a
    /// <see cref="SystemId"/> whose <c>Value</c> is the scoped form.
    /// </summary>
    [Test]
    public async Task CreateToken_EmitsScopedSubClaim()
    {
        var (privatePem, _) = GenerateEs256Pem();
        var authConfig = new AuthenticationConfiguration
        {
            JwtAuthority = "https://test.interfold.local",
            JwtEs256PrivateKeyPem = privatePem,
        };
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var expiresAt = now.AddDays(1);
        Jti jti = new("golden-jti");
        var scoped = ScopedSystemId.Compose(ScyllaKeyspace.Nam, CanonicalRaw);

        var token = AuthHelper.CreateToken(authConfig, expiresAt, now, jti, scoped.AsSystemId());

        var payload = DecodeJwtPayload(token);
        await Assert.That(payload.GetProperty("sub").GetString()).IsEqualTo(CanonicalScoped)
            .Because("The JWT sub claim is a wire boundary; the middleware ParseScoped requires the region prefix and would 401 an unscoped emission.");
    }

    // ---------------- 3. SystemTopic.ToWireString — Phoenix topic ------------------

    /// <summary>
    /// Phoenix topics are matched as opaque strings on the socket. If ToWireString drops
    /// the region prefix (e.g. because someone swapped <c>Value</c> for <c>RawId</c>), the
    /// subscriber joined on the scoped topic never matches the publisher's payload and
    /// every downstream projection event goes silently unread.
    /// </summary>
    [Test]
    public async Task SystemTopic_ToWireString_EmitsScopedComposite()
    {
        var scoped = ScopedSystemId.Compose(ScyllaKeyspace.Nam, CanonicalRaw);
        var topic = new SystemTopic(scoped.AsSystemId());

        await Assert.That(topic.ToWireString()).IsEqualTo($"system:{CanonicalScoped}")
            .Because("SystemTopic emits the scoped composite verbatim; any change to that prefix breaks Phoenix topic matching for existing sockets.");
    }

    // ---------------- 4. InProcessEventBus — idempotent routing --------------------

    /// <summary>
    /// The bus's <c>Subscription.TargetSystemId</c> is <see cref="ScopedSystemId"/>? and
    /// the publisher-side <see cref="ITargetedClusterEvent.TargetSystemId"/> is
    /// <see cref="ScopedSystemId"/>; the filter compares scoped-to-scoped by
    /// record-struct equality. This test pins the scoped-to-scoped match — the
    /// load-bearing single-region happy path every WebSocket push takes. The subscriber
    /// is always well-formed because <c>WebSocketHandler</c> composes the scoped
    /// composite from the JWT sub before it reaches the bus.
    /// </summary>
    [Test]
    public async Task InProcessEventBus_ScopedToScopedMatchDelivers()
    {
        using var bus = new InProcessEventBus();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var scopedTarget = ScopedSystemId.Compose(ScyllaKeyspace.Nam, CanonicalRaw);
        var subscriber = bus.SubscribeAsync<AlterCreatedEvent>(
            targetSystemId: scopedTarget,
            ct: cts.Token);

        var enumerator = subscriber.GetAsyncEnumerator(cts.Token);

        await bus.PublishAsync(new AlterCreatedEvent(scopedTarget, new(42)), cts.Token);

        var moved = await enumerator.MoveNextAsync();
        await Assert.That(moved).IsTrue()
            .Because("A scoped-to-scoped compare must deliver — this is the single-region happy path every socket push takes, so a false-negative here would silently break every WebSocket push in the codebase.");
        await Assert.That(enumerator.Current.TargetSystemId.Value).IsEqualTo(CanonicalScoped)
            .Because("The delivered event must carry the scoped composite verbatim — the filter is match-only, not lossy on payload.");

        await enumerator.DisposeAsync();
    }

    /// <summary>
    /// Cross-region regression pin: a subscriber joined on one region's scoped composite
    /// must NOT receive events published under a different region's scoped composite,
    /// even when the raw id is identical. A strip-then-compare shape would deliver this
    /// false positive because both sides normalise to the same raw id.
    /// </summary>
    [Test]
    public async Task InProcessEventBus_CrossRegionScopedTargetsDoNotBleedAcross()
    {
        using var bus = new InProcessEventBus();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var namScoped = ScopedSystemId.Compose(ScyllaKeyspace.Nam, CanonicalRaw);
        var eurScoped = ScopedSystemId.Compose(ScyllaKeyspace.Eur, CanonicalRaw);

        var subscriber = bus.SubscribeAsync<AlterCreatedEvent>(
            targetSystemId: namScoped,
            ct: cts.Token);

        var enumerator = subscriber.GetAsyncEnumerator(cts.Token);

        await bus.PublishAsync(new AlterCreatedEvent(eurScoped, new(42)), cts.Token);

        // The cross-region publish must NOT wake this subscriber. Wait until the cts
        // fires (i.e. no delivery in 2 s); MoveNextAsync will observe the cancellation
        // and return false without ever surfacing the eur-target event to the nam sub.
        var moved = await enumerator.MoveNextAsync();
        await Assert.That(moved).IsFalse()
            .Because("A NAM-scoped subscriber must not receive an EUR-scoped publish even when the raw ids match — a strip-then-compare shape would deliver this cross-region false positive.");

        await enumerator.DisposeAsync();
    }

    // ---------------- 5. ScopedSystemId JSON — verbatim wire form ------------------

    /// <summary>
    /// The JSON converter must emit exactly <c>Value</c> (no object wrapper). Any change
    /// here re-serialises every scoped-id payload in the wire contract set (command
    /// envelopes, event payloads) and would break clients that don't decode the wrapped
    /// form. This is the byte-freeze on the JSON boundary.
    /// </summary>
    [Test]
    public async Task ScopedSystemId_JsonRoundTrip_IsByteExact()
    {
        var scoped = ScopedSystemId.Compose(ScyllaKeyspace.Nam, CanonicalRaw);

        var json = JsonSerializer.Serialize(scoped);
        var roundTripped = JsonSerializer.Deserialize<ScopedSystemId>(json);

        await Assert.That(json).IsEqualTo($"\"{CanonicalScoped}\"")
            .Because("The converter must emit Value verbatim so the wire bytes match the plain SystemId serialisation.");
        await Assert.That(roundTripped.Value).IsEqualTo(CanonicalScoped)
            .Because("A round-trip must preserve Value exactly; a divergence here means the converter reserialised through RawId or an object shape.");
    }

    // ---------------- helpers ------------------------------------------------------

    private static (string PrivatePem, string PublicPem) GenerateEs256Pem()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (ecdsa.ExportECPrivateKeyPem(), ecdsa.ExportSubjectPublicKeyInfoPem());
    }

    private static JsonElement DecodeJwtPayload(string token)
    {
        var parts = token.Split('.');
        var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
        using var doc = JsonDocument.Parse(payloadJson);
        return doc.RootElement.Clone();
    }

    private static byte[] Base64UrlDecode(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }
}
