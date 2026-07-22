using Interfold.Contracts.Ids;

namespace Interfold.Domain.Abstractions.Repository;

/// <summary>Per-JTI JWT revocation store. Records issued tokens and answers "still valid?"
/// on every authenticated request (must be sub-ms PK-lookup fast). Also drives logout,
/// incident response, and background expiry cleanup.</summary>
public interface IAuthTokenRevocationRepository
{
    Task RecordTokenAsync(
        Jti jti,
        SystemId systemId,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default);

    /// <summary>Called on every authenticated request during JWT validation. Must be fast.</summary>
    Task<bool> ValidateTokenNotRevokedAsync(
        Jti jti,
        CancellationToken cancellationToken = default);

    /// <summary>Idempotent — revoking an already-revoked token is safe.</summary>
    Task RevokeTokenAsync(
        Jti jti,
        CancellationToken cancellationToken = default);
}
