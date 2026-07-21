using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;

namespace Interfold.Contracts.Models;

/// <summary>
/// Read-shape marker for any DTO / read model that carries the persisted
/// <c>(avatar_url, avatar_source)</c> pair — i.e. every wire model that has to route
/// through the API-side <c>AvatarUrlQualifier</c> to have a Local URL rewritten to an
/// absolute server-origin one before it goes back to a client.
///
/// <para>
/// The pair is always read together: the stored <see cref="AvatarSource"/> is the
/// authoritative discriminator that decides whether <see cref="AvatarUrl"/> is a
/// relative Local path (needs the request origin prepended) or an absolute External
/// URL (verbatim). Threading the two properties separately through every callsite —
/// <c>QualifyAvatar(x.AvatarUrl, x.AvatarSource, ...)</c> — is the pattern this
/// interface exists to collapse: callers pass the bearing itself and the qualifier
/// reads both properties off it.
/// </para>
///
/// <para>
/// Implementations today: <c>BareAlter</c> / <c>AlterReadModel</c>,
/// <c>PublicSystemReadModel</c>, <c>AccountPublicProfileReadModel</c>,
/// <c>FriendProfileReadModel</c>, <c>FriendFrontingAlterReadModel</c>, and
/// <c>SocketSelfReadModel</c>. Positional records satisfy <see cref="AvatarUrl"/>
/// and <see cref="AvatarSource"/> automatically because their generated
/// <c>{ get; init; }</c> properties are covariant with the interface's <c>{ get; }</c>
/// declarations.
/// </para>
/// </summary>
public interface IAvatarBearing
{
    AvatarUrl? AvatarUrl { get; }
    AvatarSource? AvatarSource { get; }
}
