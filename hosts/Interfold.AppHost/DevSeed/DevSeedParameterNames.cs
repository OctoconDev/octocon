namespace Interfold.AppHost.DevSeed;

/// <summary>
/// Aspire parameter names for dev-mode-only secrets consumed by <see cref="DevSeedHostedService"/>.
/// These are AppHost-private (never crossed to <c>Interfold.Bootstrapper.PublishPhase</c>) —
/// self-hosted deployments generate the equivalent values inside <c>SecretsPhase</c> and
/// seed them straight into <c>internal.secrets</c>. Constants live here (not on
/// <see cref="Interfold.Shared.Contracts.Configuration.AppHostParameterKeys"/>) precisely
/// because they must never appear in the emitted compose <c>.env</c>.
/// </summary>
internal static class DevSeedParameterNames
{
    // Aspire's AddParameter takes the bare name (no "Parameters:" prefix). GenerateParameterDefault
    // writes the value into `dotnet user-secrets` under the AppHost's UserSecretsId on first Build().
    public const string EncryptionPepper = "dev-encryption-pepper";
    public const string DeepLinkSecret = "dev-deep-link-secret";
    public const string PostgresAdminPassword = "dev-postgres-admin-password";
    public const string ScyllaAdminPassword = "dev-scylla-admin-password";
}
