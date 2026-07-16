using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Infrastructure.InMemory.Repository;

/// <summary>
/// In-memory implementation of JWT token revocation tracking for testing and development.
/// Stores tokens in a static dictionary keyed by JTI so that tokens remain accessible
/// even when the DI container is rebuilt (e.g., WebApplicationFactory host recreation
/// during parallel test execution).
/// </summary>
public sealed class InMemoryAuthTokenRevocationRepository : IAuthTokenRevocationRepository
{
    private static readonly Lock s_lock = new();
    private static readonly Dictionary<Jti, TokenRecord> s_tokens = new();

    private sealed record TokenRecord(
        Jti Jti,
        SystemId SystemId,
        DateTimeOffset IssuedAt,
        DateTimeOffset ExpiresAt,
        DateTimeOffset? RevokedAt = null
    );

    public Task RecordTokenAsync(
        Jti jti,
        SystemId systemId,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jti.Value, nameof(jti));
        ArgumentException.ThrowIfNullOrWhiteSpace(systemId, nameof(systemId));

        lock (s_lock)
        {
            s_tokens[jti] = new TokenRecord(
                Jti: jti,
                SystemId: InMemoryStorageKeys.Normalize(systemId),
                IssuedAt: DateTimeOffset.UtcNow,
                ExpiresAt: expiresAt,
                RevokedAt: null
            );
        }

        return Task.CompletedTask;
    }

    public Task<bool> ValidateTokenNotRevokedAsync(
        Jti jti,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jti.Value, nameof(jti));

        lock (s_lock)
        {
            if (!s_tokens.TryGetValue(jti, out var record))
            {
                return Task.FromResult(false);
            }

            // Token is valid if: not revoked AND not expired
            var isValid = record.RevokedAt is null && record.ExpiresAt > DateTimeOffset.UtcNow;
            return Task.FromResult(isValid);
        }
    }

    public Task RevokeTokenAsync(
        Jti jti,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jti.Value, nameof(jti));

        lock (s_lock)
        {
            if (s_tokens.TryGetValue(jti, out var record) && record.RevokedAt is null)
            {
                s_tokens[jti] = record with { RevokedAt = DateTimeOffset.UtcNow };
            }
        }

        return Task.CompletedTask;
    }
}
