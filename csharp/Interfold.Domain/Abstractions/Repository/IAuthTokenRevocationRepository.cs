using Interfold.Contracts.Ids;

namespace Interfold.Domain.Abstractions.Repository;

/// <summary>
/// Contract for managing issued JWT token revocation.
/// 
/// Tokens are tracked by their JTI (JWT ID) claim to enable per-token revocation
/// without using refresh tokens. When a token is issued, it is recorded with an expiration time.
/// On validation, the system checks if the token has been explicitly revoked.
/// 
/// This supports the following scenarios:
/// - Logout: client calls POST /auth/revoke to invalidate their current token
/// - Security incident: admin can invalidate a specific token by JTI
/// - Cleanup: background job removes expired entries to prevent table bloat
/// </summary>
public interface IAuthTokenRevocationRepository
{
    /// <summary>
    /// Record a newly issued deep-link token for revocation tracking.
    /// </summary>
    /// <param name="jti">The JWT ID (jti) claim from the token being issued.</param>
    /// <param name="systemId">The system ID (subject) this token was issued to.</param>
    /// <param name="expiresAt">The UTC timestamp when this token expires naturally (from the exp claim).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task completing when the token is recorded.</returns>
    Task RecordTokenAsync(
        Jti jti,
        SystemId systemId,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if a token is valid (not revoked and not expired).
    /// </summary>
    /// <param name="jti">The JWT ID to validate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the token exists, is not revoked, and has not yet expired; false otherwise.</returns>
    /// <remarks>
    /// This method is called on every authenticated request during JWT validation.
    /// It should be fast (ideally < 1ms for a PK lookup on indexed JTI).
    /// </remarks>
    Task<bool> ValidateTokenNotRevokedAsync(
        Jti jti,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Explicitly revoke a token (logout, incident response, or admin action).
    /// </summary>
    /// <param name="jti">The JWT ID to revoke.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task completing when the token is marked as revoked.</returns>
    /// <remarks>
    /// This is idempotent: revoking an already-revoked token is safe and returns success.
    /// </remarks>
    Task RevokeTokenAsync(
        Jti jti,
        CancellationToken cancellationToken = default);
}
