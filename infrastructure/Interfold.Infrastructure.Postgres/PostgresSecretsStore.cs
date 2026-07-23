using Interfold.Shared.Contracts.Secrets;
using Npgsql;

namespace Interfold.Infrastructure.Postgres;

public sealed class PostgresSecretsStore(IPostgresConnectionFactory connectionFactory) : ISecretsStore
{
    public async Task<string?> GetAsync(SecretsStoreKey key, CancellationToken cancellationToken = default)
    {
        await using var conn = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand("SELECT value FROM internal.secrets WHERE key = @key", conn);
        cmd.Parameters.AddWithValue("key", key.Value);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result as string;
    }

    public async Task<string> GetRequiredAsync(SecretsStoreKey key, CancellationToken cancellationToken = default)
    {
        var value = await GetAsync(key, cancellationToken);
        return value ?? throw new InvalidOperationException($"Required secret '{key.Value}' not found in secrets store.");
    }

    public async Task<IReadOnlyList<SecretEntry>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            "SELECT key, value, created_by, created_at, updated_at, expires_at, rotated_from FROM internal.secrets ORDER BY key",
            conn);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        var results = new List<SecretEntry>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new SecretEntry(
                Key: new SecretsStoreKey(reader.GetString(0)),
                Value: reader.GetString(1),
                CreatedBy: reader.GetString(2),
                CreatedAt: reader.GetDateTime(3),
                UpdatedAt: reader.GetDateTime(4),
                ExpiresAt: reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                RotatedFrom: reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return results;
    }
}
