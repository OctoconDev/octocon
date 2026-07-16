using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Infrastructure.InMemory;

/// <summary>
/// Shared key/normalization helpers for the InMemory repositories, replacing the
/// per-repository copies. The formats are frozen: normalization strips a legacy
/// <c>"region:"</c> prefix (mirroring <c>ScyllaKeyspaceResolver.NormalizeSystemId</c>) and
/// system partition keys are <c>"{region}:{systemId}"</c>.
/// </summary>
internal static class InMemoryStorageKeys
{
    /// <summary>
    /// Region-strip a <see cref="SystemId"/> and return a fresh <see cref="SystemId"/> so
    /// the typed key can be used directly as a dictionary key without unwrapping to
    /// <see cref="string"/>. Preferred entry point for the InMemory repositories that key
    /// by a normalized system id (friendship, encryption, notification, auth-revocation, ...).
    /// Callers that specifically need the raw <see cref="string"/> can call
    /// <see cref="ScopedSystemId.StripRegionPrefix(string)"/> directly.
    /// </summary>
    public static SystemId Normalize(SystemId systemId)
        => new(ScopedSystemId.StripRegionPrefix(systemId.Value));

    /// <summary>
    /// The per-system dictionary partition key: <c>"{region}:{systemId}"</c>. Routed through
    /// <see cref="ScopedSystemId.Compose(ScyllaKeyspace, SystemId)"/> so the composition is
    /// idempotent — an incoming <see cref="SystemId"/> that already carries a region prefix
    /// does not produce a double-prefixed key like <c>"nam:nam:abcdefg"</c>. Returns the
    /// typed <see cref="ScopedSystemId"/> directly so repositories can key their per-system
    /// dictionaries on the wrapper (ordinal equality on the underlying string).
    /// </summary>
    public static ScopedSystemId ForSystem(IRegionContext regionContext, SystemId systemId)
    {
        var region = regionContext.ResolveUserRegion(systemId);
        return ScopedSystemId.Compose(region, systemId);
    }

    /// <summary>
    /// Resolve the friendship level between the <paramref name="systemId"/> owner and the
    /// (optional) <paramref name="viewerSystemId"/> viewer for the InMemory port. Returns
    /// <see langword="null"/> when there is no viewer (public read), returns
    /// <see cref="FriendshipLevel.TrustedFriend"/> when the viewer is the owner (self-read),
    /// and otherwise defers to the <paramref name="friendships"/> repository if one was
    /// wired for this InMemory instance. When <paramref name="friendships"/> is
    /// <see langword="null"/> (the AppHost bootstraps some InMemory instances with a null
    /// friendship repository) the method returns <see langword="null"/> — the caller-side
    /// "non-owner viewer without a friendship graph" branch that every InMemory alter /
    /// tag / fronting read model relies on.
    ///
    /// <para>
    /// The self-check normalises through <see cref="ScopedSystemId.StripRegionPrefix(SystemId)"/>
    /// on both sides so a viewer arriving scoped (<c>"nam:abcdefg"</c>) still matches an
    /// owner arriving raw (<c>"abcdefg"</c>) and vice versa. Byte-identical to the
    /// equivalent Scylla shared-query self-check.
    /// </para>
    /// </summary>
    public static async Task<FriendshipLevel?> ResolveFriendshipLevelAsync(
        SystemId systemId,
        SystemId? viewerSystemId,
        IFriendshipRepository? friendships,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(viewerSystemId?.Value))
        {
            return null;
        }

        if (ScopedSystemId.StripRegionPrefix(systemId) ==
            ScopedSystemId.StripRegionPrefix(viewerSystemId.Value))
        {
            return FriendshipLevel.TrustedFriend;
        }

        if (friendships is null)
        {
            return null;
        }

        return await friendships.GetFriendshipLevelAsync(systemId, viewerSystemId, cancellationToken);
    }
}
