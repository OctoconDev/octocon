using System.Security.Cryptography;
using System.Text.Json;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Util;

namespace Interfold.Bootstrapper.Phases;

/// <summary>Phase 3 — generates DB/admin passwords, an RSA-2048 encryption keypair, encryption
/// pepper, JWT signing keypairs (RSA-2048 + ES256), and the deep-link HMAC secret. Persists to
/// <c>deploy/secrets/secrets.json</c> with mode 0600. Idempotent: reruns reuse the existing
/// file unless <c>--rotate-secrets</c> is passed. JWT keys + deep-link secret live only in
/// <c>secrets.json</c> until <see cref="DatabaseInitPhase"/> seeds them into
/// <c>internal.secrets</c>; they are never written as standalone PEMs or bind-mounted.</summary>
internal static partial class SecretsPhase
{
    // Drops 0/O, 1/l/I and shell-tricky punctuation; 32 chars ≈ 190 bits of entropy.
    private const string PasswordAlphabet =
        "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";

    private const int PasswordLength = 32;

    // 60 chars ≈ 357 bits — comfortably above the 256-bit HMAC-SHA256 working budget.
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
            // Backfill for secrets.json files that predate DatabaseInitPhase; without this a
            // rerun would set an empty admin password (psql ALTER ROLE accepts '').
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
            // Post-store-migration additions; backfill on rerun so pre-migration deployments
            // keep working without forcing `--rotate-secrets`.
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

        // Preserve LeafPfxPassword on rotate-secrets: it wraps leaf.crt/leaf.key, and rotate-secrets
        // doesn't touch those bytes. Rotating just the PFX password would break Kestrel's load
        // without any security benefit. Use rotate-certs to rotate the wrapped material together.
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

    /// <summary>Reads secrets without regenerating. Used by <c>rotate-certs</c>, which needs
    /// the leaf PFX password but must not touch DB credentials.</summary>
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

    /// <summary>Pure factory for a fresh <see cref="GeneratedSecrets"/>.</summary>
    internal static GeneratedSecrets Generate()
    {
        // Two independent RSA keys: EncryptionPrivateKeyB64 for at-rest data, JwtRsa256* for
        // bearer signing. Independent so rotating one doesn't invalidate the other.
        using var dataRsa = RSA.Create(RsaKeyBits);
        var dataPrivatePem = dataRsa.ExportRSAPrivateKeyPem();
        var dataPrivateB64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(dataPrivatePem));

        var (jwtRsaPublicPem, jwtRsaPrivatePem) = GenerateJwtRsaKeypair();
        var (jwtEsPublicPem, jwtEsPrivatePem) = GenerateJwtEs256Keypair();

        return new GeneratedSecrets
        {
            PostgresUser = "interfold",
            PostgresPassword = RandomPassword(),
            // Transient — scrambled in-cluster after first-boot bootstrap; .env goes stale by design.
            PostgresInitPassword = RandomPassword(),
            // Lives in internal.secrets; never injected into compose or AppHost parameters.
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

    internal static string RandomPassword()
        => RandomNumberGenerator.GetString(PasswordAlphabet, PasswordLength);

    internal static string RandomDeepLinkSecret()
        => RandomNumberGenerator.GetString(PasswordAlphabet, DeepLinkSecretLength);

    internal static async Task<GeneratedSecrets> LoadAsync(string path, CancellationToken ct)
    {
        var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, BootstrapJsonContext.Default.GeneratedSecrets)
               ?? throw new InvalidOperationException($"Failed to parse {path}.");
    }

    internal static async Task PersistAsync(GeneratedSecrets secrets, string path, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(secrets, BootstrapJsonContext.Default.GeneratedSecrets);
        await File.WriteAllTextAsync(path, json, ct).ConfigureAwait(false);
    }

    /// <summary>PKCS#8 private + SPKI public PEM — the shapes <c>RSA.ImportFromPem</c> accepts.</summary>
    internal static (string PublicPem, string PrivatePem) GenerateJwtRsaKeypair()
    {
        using var rsa = RSA.Create(RsaKeyBits);
        return (rsa.ExportSubjectPublicKeyInfoPem(), rsa.ExportPkcs8PrivateKeyPem());
    }

    /// <summary>SEC1 private + SPKI public PEM (NIST P-256) — the shapes <c>ECDsa.ImportFromPem</c>
    /// accepts.</summary>
    internal static (string PublicPem, string PrivatePem) GenerateJwtEs256Keypair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (ecdsa.ExportSubjectPublicKeyInfoPem(), ecdsa.ExportECPrivateKeyPem());
    }
}
