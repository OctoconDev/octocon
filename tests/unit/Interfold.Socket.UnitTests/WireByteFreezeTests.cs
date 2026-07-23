using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Interfold.Api.Socket;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Events;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Domain;
using Interfold.Infrastructure;
using Interfold.Infrastructure.Coordination;

namespace Interfold.Api.UnitTests;

// Golden-byte guardrail on the scoped-id wire boundaries. Each fact below pins one
// wire boundary a stray ScopedSystemId.RawId swap would silently corrupt. If any
// fail, treat as a real wire-format break, do not "adjust the expected value".
public sealed class WireByteFreezeTests
{
    private const string CanonicalScoped = "nam:abcdefg";
    private const string CanonicalRaw = "abcdefg";

    [Test]
    public async Task DeriveKey_IsDeterministic_AndScopeSensitive()
    {
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

    [Test]
    public async Task SystemTopic_ToWireString_EmitsScopedComposite()
    {
        var scoped = ScopedSystemId.Compose(ScyllaKeyspace.Nam, CanonicalRaw);
        var topic = new SystemTopic(scoped.AsSystemId());

        await Assert.That(topic.ToWireString()).IsEqualTo($"system:{CanonicalScoped}")
            .Because("SystemTopic emits the scoped composite verbatim; any change to that prefix breaks Phoenix topic matching for existing sockets.");
    }

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

        var moved = await enumerator.MoveNextAsync();
        await Assert.That(moved).IsFalse()
            .Because("A NAM-scoped subscriber must not receive an EUR-scoped publish even when the raw ids match — a strip-then-compare shape would deliver this cross-region false positive.");

        await enumerator.DisposeAsync();
    }

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
