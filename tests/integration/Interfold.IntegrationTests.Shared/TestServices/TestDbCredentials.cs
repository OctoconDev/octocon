using System.Security.Cryptography;

namespace Interfold.IntegrationTests.Shared.TestServices;

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
///
/// The JWT keypair PEMs below are DETERMINISTIC — baked in as constants rather than
/// generated at process start — because under the centralised test-bench every leaf
/// integration project shares one Postgres, and <see cref="Interfold.DatabaseBootstrap.PostgresSeeder.BootstrapAsync"/>
/// short-circuits step 4 (<c>internal.secrets</c> upsert) once the app role is provisioned.
/// A per-process <c>Lazy&lt;ECDsa.Create&gt;</c> would leave the first cold-booter's public
/// key authoritative in the DB — every subsequent test-host would sign tokens with a
/// different key and the API would reject them with <c>Invalid JWT signature: tested 1
/// verification key(s) but none matched</c>. Baking the PEM in guarantees signer / verifier
/// agreement across processes.
///
/// Regenerate via a one-off snippet if the shape ever needs to change:
/// <code>
/// using var rsa = RSA.Create(2048);
/// Console.WriteLine(rsa.ExportPkcs8PrivateKeyPem());
/// using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
/// Console.WriteLine(ec.ExportECPrivateKeyPem());
/// </code>
/// </remarks>
public static class TestDbCredentials
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

    public const string JwtRsa256PrivateKeyPem = """
        -----BEGIN PRIVATE KEY-----
        MIIEvAIBADANBgkqhkiG9w0BAQEFAASCBKYwggSiAgEAAoIBAQCi/EMiHUj5rcmb
        F5QkZ5FBghIezaPBUhVVfPrXn26KqR9QXVYYXWLrZNTuxdxazNt8gM6br6yrlnW9
        W8C659Dz0G1j1mZGQVAVKxxfkfncdIOqYk9VPeD27JN+ysqTUvy//N9LaG/LD3gx
        3BL/nyS0j1WCoGmtBNRKKypQt5pJkujvaEEcTUD4ZcCNd7HLjWL+rnpjU1mXQfJH
        dAUuo9qCObfsacGN1W4Y0DVrtWb6+wI/v5WzWIa9PR19UpS50n+H/gKuAdCv5v2f
        gzH69wFe+CwKZF4q7Z9Axkd3CDzP+Nhuc9Vxncm+DoA7JEiKHCCe6N3F3bNeBawv
        j7EVrhi7AgMBAAECggEABTx4hayOLyNTsk1gH4lKQ+IDZgTySWAzOkNhJbNEEGVd
        ka3l+bNqXaioJKGrsikZthbHTH4o3HxhfPtpJjgTYPnlwcreD0zC32M6rqFYdyox
        HrS42sK5qwUvueeW+YO1hg+ANwJ8cPgmwudQnvDEc56VPzSbIIlirp1TZzN22/00
        naNBpdYcsxrb4+/esIRPdYkrV9uTzXgGkZSieLSEQzBoeyoBXl7lZ9+Hhei/Rw2M
        GNFFPth3awLd0Ricbt0Btstjj+uEoelwLNKRIox8Uq8tKKvazhzfWKIvGRaO8Vgg
        42M85qbloWZHwfWQBH4SZwBby1lpwqsvgzd75MnoyQKBgQDePQ4kD1ZpHYZcjXAt
        LaEgtWZOF+xDZzqHNegNcYuMFIjSXUUlKI07Y65wsapSLssP9e1q9igsF95yTdo3
        krEF5LwSLEB7FyPsnz8CtWMHEUB4/l9wzljPM5g6v0T6T5Vq1jYsUnSbe2g+YSbp
        RXHLUn3/X0XDET2Kr3ur9/icswKBgQC7vtSEJQG4hSWrCAW9WcYVcInnt3EZGZ3/
        ksSucQtM8cYXw/lSfHkKy2Pc/ZzHnVZJ6GK5td4wwx+1OImBBiCdWPSwVR+OsKUl
        RjW4lueVfOSlBbHyiHPOl3sL5PlNF2PTTLhtfz0WJnPHoM4jgHmXAcri0DoPiaRs
        WTtW+KYn2QKBgGnFFzDUV+TpV2Q/MI9f8xrVGt66BglCXRmy70FOtAK0VzX/jAQW
        W7lTRNd/xzcb7CspeZh5lT7/ETDHmr7uQvAyH8xqYTn0FnXsiJmqaHoZnNy4/AV9
        P8lFngL/uz2CmPNjBh9sEvFY95EQvesx0Ona1fqvhk1DrE2QHUUIXLt/AoGAXKgZ
        if4Zk09i/729121u7TXVWZ6XGqQh7fgpSU5RHXBVK3V0ntj/g+xNJMuljH6CD4e4
        8Z5oQfiKtY1pj1vOzNkSKdRY9rsHRhYYfTk8ofI5hZgB8oiVXugzufMPdpSMl8PN
        YXziUQQ5L9SU58CIQZaI4teRUAnGNBL1zj55AkECgYBNiW9yo6LPCkx1fzbIuQD4
        akE4cZcE9NWZ4sd1qc4BDp75q+hqbayFOfT66j0pGPjLR25c48E5Waaez9+msokM
        jBtkjyCA8wJPwMoRW/04himva1xsH9327GKfs/+b7wSupJ7WnRy2/vZOWsGsbQmn
        M3WS7LMNe0kWrpy+/njL+Q==
        -----END PRIVATE KEY-----
        """;

    public const string JwtEs256PrivateKeyPem = """
        -----BEGIN EC PRIVATE KEY-----
        MHcCAQEEIF3B7fhYJm2Au2j1Oyiz8QTrOhFJB3KliuIhcCWpugTVoAoGCCqGSM49
        AwEHoUQDQgAEgc8fRNFLhhK+GNX2GHv97z5PMgZFqRcg8Gs+A7ogntzi8gc5+Hre
        74GuDZ8Xv3vtxQ4xEPw1CAAEEj86f4srnQ==
        -----END EC PRIVATE KEY-----
        """;

    private static readonly Lazy<string> _rsaPublicPem =
        new(() => DerivePublicPem(JwtRsa256PrivateKeyPem, isEc: false));
    private static readonly Lazy<string> _esPublicPem =
        new(() => DerivePublicPem(JwtEs256PrivateKeyPem, isEc: true));

    public static string JwtRsa256PublicKeyPem => _rsaPublicPem.Value;
    public static string JwtEs256PublicKeyPem  => _esPublicPem.Value;

    private static string DerivePublicPem(string privatePem, bool isEc)
    {
        if (isEc)
        {
            using var ec = ECDsa.Create();
            ec.ImportFromPem(privatePem);
            return ec.ExportSubjectPublicKeyInfoPem();
        }
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privatePem);
        return rsa.ExportSubjectPublicKeyInfoPem();
    }
}
