using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Read;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Domain.Alters;

// Backend-agnostic helpers for guarded alter-field projection. Kept in Interfold.Domain
// so InMemory + Scylla repos share the visibility rules and can't drift.
public static class AlterFieldProjection
{
    /// <summary>Guarded join: emits one <see cref="AlterPublicFieldReadModel"/> per
    /// definition the alter has populated. Empty-value definitions are skipped — the
    /// guarded path is stricter than the unguarded one.</summary>
    public static IReadOnlyList<AlterPublicFieldReadModel> ResolveGuardedFields(
        IReadOnlyDictionary<FieldId, string?>? alterFieldValues,
        IReadOnlyList<SettingsFieldReadModel> definitions)
    {
        if (alterFieldValues is null || alterFieldValues.Count == 0 || definitions.Count == 0)
        {
            return Array.Empty<AlterPublicFieldReadModel>();
        }

        return definitions
            .Where(def => alterFieldValues.ContainsKey(def.Id))
            .Select(def => new AlterPublicFieldReadModel(
                def.Id,
                def.Name,
                def.Type,
                alterFieldValues[def.Id]))
            .ToArray();
    }

    /// <summary>Field-definition subset visible to the viewer at
    /// <paramref name="friendshipLevel"/> (null = anonymous, public-only).</summary>
    public static async Task<IReadOnlyList<SettingsFieldReadModel>> ResolveVisibleDefinitionsAsync(
        ISettingsFieldRepository settingsFields,
        SystemId systemId,
        FriendshipLevel? friendshipLevel,
        CancellationToken cancellationToken = default)
    {
        var definitions = await settingsFields.ListAsync(systemId, cancellationToken).ConfigureAwait(false);
        return definitions
            .Where(def => def.SecurityLevel.CanBeViewedBy(friendshipLevel))
            .ToArray();
    }
}
