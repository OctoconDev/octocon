namespace Interfold.Contracts.Secrets;

public sealed record SecretEntry(
    SecretsStoreKey Key,
    string Value,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ExpiresAt,
    string? RotatedFrom);

public interface ISecretsStore
{
    Task<string?> GetAsync(SecretsStoreKey key, CancellationToken cancellationToken = default);
    Task<string> GetRequiredAsync(SecretsStoreKey key, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SecretEntry>> ListAsync(CancellationToken cancellationToken = default);
}
