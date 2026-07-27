using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models.Read;

namespace Interfold.Alters.Contracts.Abstractions;

/// <summary>Viewer-visibility-filtered field-definition set consumed by the Alters read path.
/// Adapter over <c>ISettingsFieldRepository</c> in <c>Interfold.Settings.Domain</c>, registered
/// by <c>AddSettingsModule</c>.</summary>
public interface IAlterFieldDefinitions
{
    Task<IReadOnlyList<SettingsFieldReadModel>> ListVisibleAsync(
        SystemId systemId,
        FriendshipLevel? friendshipLevel,
        CancellationToken cancellationToken = default);
}
