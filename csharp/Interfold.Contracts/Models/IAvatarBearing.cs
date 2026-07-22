using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;

namespace Interfold.Contracts.Models;

/// <summary>Marker for wire models carrying the persisted <c>(avatar_url, avatar_source)</c>
/// pair. AvatarUrlQualifier reads both off the bearing to rewrite Local URLs to
/// absolute server-origin ones before responding.</summary>
public interface IAvatarBearing
{
    AvatarUrl? AvatarUrl { get; }
    AvatarSource? AvatarSource { get; }
}
