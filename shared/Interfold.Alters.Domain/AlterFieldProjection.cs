using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Read;
using Interfold.Shared.Domain.Abstractions.Repository;
using Interfold.Shared.Domain.Observability;
using Microsoft.Extensions.Logging;

namespace Interfold.Alters.Domain;

// Backend-agnostic helpers for guarded alter-field projection. Kept in Interfold.Alters.Domain
// so InMemory + Scylla repos share the visibility rules and can't drift.
public static class AlterFieldProjection
{
    /// <summary>Guarded join: emits one <see cref="AlterPublicFieldReadModel"/> per
    /// definition the alter has populated. Empty-value definitions are skipped — the
    /// guarded path is stricter than the unguarded one.</summary>
    public static IReadOnlyList<AlterPublicFieldReadModel> ResolveGuardedFields(
        IReadOnlyDictionary<FieldId, string?>? alterFieldValues,
        IReadOnlyList<SettingsFieldReadModel> definitions,
        ILogger? logger = null)
    {
        if (alterFieldValues is null || alterFieldValues.Count == 0 || definitions.Count == 0)
        {
            return Array.Empty<AlterPublicFieldReadModel>();
        }

        try
        {
            return definitions
                .Where(def => alterFieldValues.ContainsKey(def.Id))
                .Select(def => new AlterPublicFieldReadModel(
                    def.Id,
                    def.Name,
                    def.Type,
                    alterFieldValues[def.Id]))
                .ToArray();
        }
        catch (Exception ex)
        {
            GuardedMetrics.ErrorsTotal.Add(1,
                new KeyValuePair<string, object?>("entity_type", "field"),
                new KeyValuePair<string, object?>("exception_type", ex.GetType().Name));
            logger?.LogError(ex,
                "Guarded field projection failed: definitions={DefinitionCount}, values={ValueCount}",
                definitions.Count, alterFieldValues.Count);
            throw;
        }
    }

    /// <summary>Field-definition subset visible to the viewer at
    /// <paramref name="friendshipLevel"/> (null = anonymous, public-only).</summary>
    public static async Task<IReadOnlyList<SettingsFieldReadModel>> ResolveVisibleDefinitionsAsync(
        ISettingsFieldRepository settingsFields,
        SystemId systemId,
        FriendshipLevel? friendshipLevel,
        CancellationToken cancellationToken = default,
        ILogger? logger = null)
    {
        try
        {
            var definitions = await settingsFields.ListAsync(systemId, cancellationToken).ConfigureAwait(false);
            return definitions
                .Where(def => def.SecurityLevel.CanBeViewedBy(friendshipLevel))
                .ToArray();
        }
        catch (Exception ex)
        {
            GuardedMetrics.ErrorsTotal.Add(1,
                new KeyValuePair<string, object?>("entity_type", "field"),
                new KeyValuePair<string, object?>("exception_type", ex.GetType().Name));
            logger?.LogError(ex,
                "Guarded field-definition load failed: system={SystemId}, friendship_level={FriendshipLevel}",
                systemId.Value, friendshipLevel);
            throw;
        }
    }
}
