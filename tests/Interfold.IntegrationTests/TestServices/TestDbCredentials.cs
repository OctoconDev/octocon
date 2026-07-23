using System.Security.Cryptography;

namespace Interfold.IntegrationTests.TestServices;

/// <summary>
/// Deterministic credentials shared by the consolidated <see cref="SharedDbFixture"/> and
/// the multi-DC topology fixture (<see cref="MultiNodeScyllaFixture"/>). Hard-coded so the
/// AppHost <c>Args</c>, the in-process seeder, and any assertions further downstream agree
/// on the exact values without having to read the auto-generated parameter defaults back
/// out of the service provider.
/// </summary>
/// <remarks>
/// These match the legacy per-fixture <c>private const</c> blocks one-for-one — extracted
/// here so a future credential rotation only touches one place. None of these values are
/// real secrets; they exist purely to keep the in-test cluster authenticatable.
/// The JWT keypair fields below are generated once per process — they need to be valid
/// PEM-encoded keys because the API reads them straight out of <c>internal.secrets</c> on
/// startup, but they don't need to be deterministic across runs.
/// </remarks>
internal static class TestDbCredentials
{
    public const string PostgresInitPassword  = "test_init_pw_789!Bootstrap";
    public const string PostgresAppUser       = "test_pg_user";
    public const string PostgresAppPassword   = "test_pg_pw_789!Secure";
    public const string PostgresAdminUser     = "test_pg_user_admin";
    public const string PostgresAdminPassword = "test_pg_admin_pw_456!Strong";
    public const string ScyllaAppUser         = "test_app_user";
    public const string ScyllaAppPassword     = "test_secure_pw_123!Safe";
    public const string ScyllaAdminUser       = "test_app_user_admin";
    public const string ScyllaAdminPassword   = "test_admin_pw_456!Strong";

    public const string DeepLinkSecret  = "test_deep_link_secret_0123456789abcdefghijklmnopqrstuv";
    public const string LeafPfxPassword = "test_leaf_pfx_pw_0123!Strong";

    private static readonly Lazy<(string RsaPrivatePem, string RsaPublicPem)> _rsaKeyPair
        = new(GenerateRsaKeypair);
    private static readonly Lazy<(string EsPrivatePem, string EsPublicPem)> _esKeyPair
        = new(GenerateEs256Keypair);

    public static string JwtRsa256PrivateKeyPem => _rsaKeyPair.Value.RsaPrivatePem;
    public static string JwtRsa256PublicKeyPem  => _rsaKeyPair.Value.RsaPublicPem;
    public static string JwtEs256PrivateKeyPem  => _esKeyPair.Value.EsPrivatePem;
    public static string JwtEs256PublicKeyPem   => _esKeyPair.Value.EsPublicPem;

    private static (string PrivatePem, string PublicPem) GenerateRsaKeypair()
    {
        using var rsa = RSA.Create(2048);
        return (rsa.ExportPkcs8PrivateKeyPem(), rsa.ExportSubjectPublicKeyInfoPem());
    }

    private static (string PrivatePem, string PublicPem) GenerateEs256Keypair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (ecdsa.ExportECPrivateKeyPem(), ecdsa.ExportSubjectPublicKeyInfoPem());
    }
}
