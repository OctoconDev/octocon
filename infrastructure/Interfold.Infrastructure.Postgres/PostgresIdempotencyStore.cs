using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Interfold.Infrastructure.Postgres;

public sealed class PostgresIdempotencyStore : IIdempotencyStore
{
    private readonly IPostgresConnectionFactory _connectionFactory;
    private readonly PersistenceConfiguration _options;

    public PostgresIdempotencyStore(
        IPostgresConnectionFactory connectionFactory,
        IOptions<PersistenceConfiguration> options
    )
    {
        _connectionFactory = connectionFactory;
        _options = options.Value;
    }

    public async Task<IdempotencyMatch?> FindAsync(
        SystemId principalId,
        OperationId operationId,
        IdempotencyKey idempotencyKey,
        CancellationToken cancellationToken = default
    )
    {
        return await DatabaseTransientRetry.ExecutePostgresAsync(async () =>
        {
            await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(@"
            SELECT payload_hash, outcome_hash, outcome_payload
            FROM octocon_idempotency
            WHERE principal_id = @principal_id
              AND operation_id = @operation_id
              AND idempotency_key = @idempotency_key", connection);

            command.Parameters.AddWithValue("principal_id", principalId.Value);
            command.Parameters.AddWithValue("operation_id", operationId.Value);
            command.Parameters.AddWithValue("idempotency_key", idempotencyKey.Value);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            var payloadHash = reader.GetString(0);
            var outcomeHash = reader.GetString(1);
            var outcomePayload = await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2);

            return new IdempotencyMatch(payloadHash, outcomeHash, outcomePayload);
        }, _options, cancellationToken);
    }

    public async Task SaveAsync(
        SystemId principalId,
        OperationId operationId,
        IdempotencyKey idempotencyKey,
        string payloadHash,
        string outcomeHash,
        string? outcomePayload,
        CancellationToken cancellationToken = default
    )
    {
        await DatabaseTransientRetry.ExecutePostgresAsync(async () =>
        {
            await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(@"
            INSERT INTO octocon_idempotency (
                principal_id,
                operation_id,
                idempotency_key,
                payload_hash,
                outcome_hash,
                outcome_payload,
                created_at
            ) VALUES (
                @principal_id,
                @operation_id,
                @idempotency_key,
                @payload_hash,
                @outcome_hash,
                @outcome_payload,
                NOW()
            )
            ON CONFLICT (principal_id, operation_id, idempotency_key)
            DO UPDATE
              SET payload_hash = EXCLUDED.payload_hash,
                  outcome_hash = EXCLUDED.outcome_hash,
                  outcome_payload = EXCLUDED.outcome_payload", connection);

            command.Parameters.AddWithValue("principal_id", principalId.Value);
            command.Parameters.AddWithValue("operation_id", operationId.Value);
            command.Parameters.AddWithValue("idempotency_key", idempotencyKey.Value);
            command.Parameters.AddWithValue("payload_hash", payloadHash);
            command.Parameters.AddWithValue("outcome_hash", outcomeHash);
            command.Parameters.AddWithValue("outcome_payload", (object?)outcomePayload ?? DBNull.Value);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }, _options, cancellationToken);
    }
}
