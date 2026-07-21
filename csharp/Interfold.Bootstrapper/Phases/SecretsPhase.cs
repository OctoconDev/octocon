using System.Security.Cryptography;
using System.Text.Json;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Util;

namespace Interfold.Bootstrapper.Phases;

/// <summary>
/// Phase 3 — generates DB/admin passwords, an RSA-2048 encryption keypair (base64-encoded PEM),
/// a server-side encryption pepper, JWT signing keypairs (RSA-2048 + ES256), and the
/// deep-link HMAC secret. Persists everything to <c>deploy/secrets/secrets.json</c> with
/// mode 0600. Idempotent: a second run reuses the existing file unless
/// <c>--rotate-secrets</c> is specified.
/// </summary>
/// <remarks>
/// The JWT keypairs and the deep-link secret live exclusively inside the on-disk
/// <c>secrets.json</c> file until <see cref="DatabaseInitPhase"/> seeds them into
/// <c>internal.secrets</c>. They are NEVER written to disk as standalone PEMs and
/// NEVER bind-mounted into the API container — the API reads them from the secrets
/// store at startup.
/// </remarks>
internal static partial class SecretsPhase
{
    // Alphabet for generated passwords. Drops the easily-confused chars (0/O, 1/l/I) plus shell-tricky
    // punctuation. 32 characters from this alphabet still gives ~190 bits of entropy.
    private const string PasswordAlphabet =
        "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";

    private const int PasswordLength = 32;

    // Deep-link HMAC secrets are sized larger than passwords (60 chars from the same alphabet ≈
    // 357 bits) to comfortably exceed the 256-bit working budget of HMAC-SHA256 with margin for
    // truncation in any downstream usage.
    private const int DeepLinkSecretLength = 60;

    private const int RsaKeyBits = 2048;

    public static async Task<GeneratedSecrets> RunAsync(
        BootstrapOptions options,
        BootstrapConfig config,
        PhaseLogger logger,
        CancellationToken ct)
    {
        const string Phase = "secrets";
        logger.PhaseStart(Phase);

        var secretsPath = Path.Combine(options.OutputDir, BootstrapArtifactPaths.SecretsRelativePath);
        if (File.Exists(secretsPath) && !options.RotateSecrets)
        {
            logger.PhaseSkip(Phase, PhaseFailureReasons.Skip.AlreadyPresent);
            var existing = await LoadAsync(secretsPath, ct).ConfigureAwait(false);
            // Backfill admin / init passwords for secrets.json files that predate DatabaseInitPhase.
            // Without this, a rerun against an older deployment would set an empty admin
            // password inside the cluster - psql ALTER ROLE happily accepts ''.
            var mutated = false;
            if (string.IsNullOrEmpty(existing.PostgresInitPassword))
            {
                existing.PostgresInitPassword = RandomPassword();
                mutated = true;
            }
            if (string.IsNullOrEmpty(existing.PostgresAdminPassword))
            {
                existing.PostgresAdminPassword = RandomPassword();
                mutated = true;
            }
            if (string.IsNullOrEmpty(existing.ScyllaAdminPassword))
            {
                existing.ScyllaAdminPassword = RandomPassword();
                mutated = true;
            }
            // Deep-link secret + JWT keypairs are post-store-migration additions. Backfill on
            // rerun so deployments that predate the migration keep working without forcing the
            // operator into `--rotate-secrets`.
            if (string.IsNullOrEmpty(existing.DeepLinkSecret))
            {
                existing.DeepLinkSecret = RandomDeepLinkSecret();
                mutated = true;
            }
            if (string.IsNullOrEmpty(existing.JwtRsa256PrivateKeyPem))
            {
                (existing.JwtRsa256PublicKeyPem, existing.JwtRsa256PrivateKeyPem) = GenerateJwtRsaKeypair();
                mutated = true;
            }
            if (string.IsNullOrEmpty(existing.JwtEs256PrivateKeyPem))
            {
                (existing.JwtEs256PublicKeyPem, existing.JwtEs256PrivateKeyPem) = GenerateJwtEs256Keypair();
                mutated = true;
            }
            if (mutated)
            {
                logger.Info("    backfilled missing fields into existing secrets.json");
                await PersistAsync(existing, secretsPath, ct).ConfigureAwait(false);
                UnixFilePermissions.SetOwnerOnly(secretsPath, logger);
            }
            return existing;
        }

        // On rotate-secrets we deliberately preserve a couple of credentials that wrap on-disk
        // material we are NOT regenerating in the same pass:
        //   * LeafPfxPassword: the PKCS#12 password used to bundle leaf.crt + leaf.key into
        //     leaf.pfx. The cert / key bytes themselves don't change on rotate-secrets (only
        //     rotate-certs touches them), so re-encrypting the PFX wrapper with a fresh
        //     password would invalidate Kestrel's load without delivering any real security
        //     benefit - the rotated password would protect the exact same cert + key. The
        //     legitimate path to rotate this is rotate-certs, which regenerates the wrapped
        //     material too.
        string? preservedLeafPfxPassword = null;
        if (File.Exists(secretsPath))
        {
            logger.Info($"    --rotate-secrets set: regenerating {secretsPath}");
            try
            {
                var prior = await LoadAsync(secretsPath, ct).ConfigureAwait(false);
                preservedLeafPfxPassword = prior.LeafPfxPassword;
            }
            catch (Exception ex)
            {
                logger.Warn($"    couldn't load existing secrets to preserve PFX password ({ex.GetType().Name}); regenerating");
            }
        }

        var secrets = Generate();
        if (!string.IsNullOrEmpty(preservedLeafPfxPassword))
        {
            secrets.LeafPfxPassword = preservedLeafPfxPassword;
        }
        await PersistAsync(secrets, secretsPath, ct).ConfigureAwait(false);
        UnixFilePermissions.SetOwnerOnly(secretsPath, logger);

        logger.PhaseDone(Phase);
        return secrets;
    }

    /// <summary>
    /// Reads the existing secrets file without regenerating. Used by the <c>rotate-certs</c>
    /// command, which needs the leaf PFX password but must not rotate DB credentials.
    /// </summary>
    public static GeneratedSecrets LoadExisting(BootstrapOptions options)
    {
        var secretsPath = Path.Combine(options.OutputDir, BootstrapArtifactPaths.SecretsRelativePath);
        if (!File.Exists(secretsPath))
        {
            throw new InvalidOperationException(
                $"Cannot rotate certs without an existing secrets file at {secretsPath}. " +
                "Run `bootstrap` first.");
        }
        var json = File.ReadAllText(secretsPath);
        return JsonSerializer.Deserialize(json, BootstrapJsonContext.Default.GeneratedSecrets)
               ?? throw new InvalidOperationException($"Failed to parse {secretsPath}.");
    }

    /// <summary>
    /// Pure factory for a fresh <see cref="GeneratedSecrets"/> with random passwords, an RSA-2048
    /// encryption keypair, and JWT signing material. Internal so the unit-test project can drive
    /// the generator without staging output directories on disk.
    /// </summary>
    internal static GeneratedSecrets Generate()
    {
        // RSA-2048 for two purposes:
        //   1. Application-level data encryption (EncryptionPrivateKeyB64) - existing.
        //   2. JWT bearer-token signing (JwtRsa256*) - required by the API at startup, otherwise
        //      ApplyAuthentication() throws "No RSA public PEM or file was provided".
        // We generate independent keys so a rotate of the JWT keys doesn't invalidate at-rest
        // ciphertext (and vice-versa).
        using var dataRsa = RSA.Create(RsaKeyBits);
        var dataPrivatePem = dataRsa.ExportRSAPrivateKeyPem();
        var dataPrivateB64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(dataPrivatePem));

        // PKCS#8 + SPKI - the formats Microsoft.IdentityModel and System.Security.Cryptography
        // accept directly via RSA.ImportFromPem in the API.
        var (jwtRsaPublicPem, jwtRsaPrivatePem) = GenerateJwtRsaKeypair();
        var (jwtEsPublicPem, jwtEsPrivatePem) = GenerateJwtEs256Keypair();

        return new GeneratedSecrets
        {
            PostgresUser = "interfold",
            PostgresPassword = RandomPassword(),
            // Transient init credential used only during initdb / DatabaseInitPhase. Scrambled
            // in-cluster after first-boot bootstrap; the .env value becomes stale by design.
            PostgresInitPassword = RandomPassword(),
            // Independent admin password - lives inside the cluster (internal.secrets) and is
            // never injected into compose or AppHost parameters.
            PostgresAdminPassword = RandomPassword(),
            ScyllaUser = "interfold",
            ScyllaPassword = RandomPassword(),
            ScyllaAdminPassword = RandomPassword(),
            EncryptionPrivateKeyB64 = dataPrivateB64,
            EncryptionPepper = RandomPassword(),
            DeepLinkSecret = RandomDeepLinkSecret(),
            LeafPfxPassword = RandomPassword(),
            JwtRsa256PublicKeyPem = jwtRsaPublicPem,
            JwtRsa256PrivateKeyPem = jwtRsaPrivatePem,
            JwtEs256PublicKeyPem = jwtEsPublicPem,
            JwtEs256PrivateKeyPem = jwtEsPrivatePem,
        };
    }

    /// <summary>
    /// Cryptographically-random password drawn from <see cref="PasswordAlphabet"/>. Internal so
    /// unit tests can assert the alphabet/length contract without going through full secret generation.
    /// </summary>
    internal static string RandomPassword()
    {
        return RandomNumberGenerator.GetString(PasswordAlphabet, PasswordLength);
    }

    /// <summary>
    /// Cryptographically-random HMAC signing secret for the phase-F deep-link exchange.
    /// Sized to <see cref="DeepLinkSecretLength"/> chars (~357 bits) so HMAC-SHA256 has full
    /// working entropy with margin.
    /// </summary>
    internal static string RandomDeepLinkSecret()
    {
        return RandomNumberGenerator.GetString(PasswordAlphabet, DeepLinkSecretLength);
    }

    /// <summary>Internal so unit tests can round-trip a persisted secrets file in isolation.</summary>
    internal static async Task<GeneratedSecrets> LoadAsync(string path, CancellationToken ct)
    {
        var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, BootstrapJsonContext.Default.GeneratedSecrets)
               ?? throw new InvalidOperationException($"Failed to parse {path}.");
    }

    /// <summary>Internal so unit tests can stage old-format secrets files for backfill testing.</summary>
    internal static async Task PersistAsync(GeneratedSecrets secrets, string path, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(secrets, BootstrapJsonContext.Default.GeneratedSecrets);
        await File.WriteAllTextAsync(path, json, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// RSA-2048 JWT signing keypair emitted as PKCS#8 (private) + SubjectPublicKeyInfo (public) PEM.
    /// Internal so unit tests can pin the emitted PEM header/format contract that the API's
    /// <c>RSA.ImportFromPem</c> depends on. Both the fresh-generation path (<see cref="Generate"/>)
    /// and the second-run backfill path in <see cref="RunAsync"/> route through here so any format
    /// change moves in lockstep across the auth boundary.
    /// </summary>
    internal static (string PublicPem, string PrivatePem) GenerateJwtRsaKeypair()
    {
        using var rsa = RSA.Create(RsaKeyBits);
        return (rsa.ExportSubjectPublicKeyInfoPem(), rsa.ExportPkcs8PrivateKeyPem());
    }

    /// <summary>
    /// ES256 (NIST P-256) JWT signing keypair emitted as SEC1 (private) + SubjectPublicKeyInfo
    /// (public) PEM. Internal so unit tests can pin the emitted PEM header/format contract that
    /// the API's <c>ECDsa.ImportFromPem</c> depends on. Both generation paths route through here
    /// for the same lockstep reason as <see cref="GenerateJwtRsaKeypair"/>.
    /// </summary>
    internal static (string PublicPem, string PrivatePem) GenerateJwtEs256Keypair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (ecdsa.ExportSubjectPublicKeyInfoPem(), ecdsa.ExportECPrivateKeyPem());
    }
}
