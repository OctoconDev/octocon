using System.Collections.Concurrent;
using Interfold.Contracts.Models;
using Interfold.Domain.Abstractions;
using Interfold.Contracts.Ids;

namespace Interfold.Infrastructure.InMemory;

public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private readonly ConcurrentDictionary<string, IdempotencyMatch> _store = new();

    public Task<IdempotencyMatch?> FindAsync(
        SystemId principalId,
        OperationId operationId,
        IdempotencyKey idempotencyKey,
        CancellationToken cancellationToken = default
    )
    {
        var key = BuildKey(principalId, operationId, idempotencyKey);
        _store.TryGetValue(key, out var value);
        return Task.FromResult<IdempotencyMatch?>(value);
    }

    public Task SaveAsync(
        SystemId principalId,
        OperationId operationId,
        IdempotencyKey idempotencyKey,
        string payloadHash,
        string outcomeHash,
        string? outcomePayload,
        CancellationToken cancellationToken = default
    )
    {
        var key = BuildKey(principalId, operationId, idempotencyKey);
        _store[key] = new IdempotencyMatch(payloadHash, outcomeHash, outcomePayload);
        return Task.CompletedTask;
    }

    private static string BuildKey(SystemId principalId, OperationId operationId, IdempotencyKey idempotencyKey) =>
        $"{principalId.Value}:{operationId.Value}:{idempotencyKey.Value}";
}