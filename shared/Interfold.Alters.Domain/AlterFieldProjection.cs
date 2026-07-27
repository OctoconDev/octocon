using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Read;
using Interfold.Shared.Domain.Observability;
using Microsoft.Extensions.Logging;

namespace Interfold.Alters.Domain;

// Backend-agnostic helpers for guarded alter-field projection so InMemory + Scylla repos
// share the visibility rules and can't drift.
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

    /// <summary>Owner-view join: emits one <see cref="AlterPublicFieldReadModel"/> per
    /// definition, <c>Value = null</c> when the alter didn't populate it. Not viewer-filtered —
    /// contrast with <see cref="ResolveGuardedFields"/> which drops unpopulated defs and expects
    /// visibility-filtered definitions.</summary>
    public static IReadOnlyList<AlterPublicFieldReadModel> ResolveOwnerFields(
        IReadOnlyDictionary<FieldId, string?>? alterFieldValues,
        IReadOnlyList<SettingsFieldReadModel> definitions)
    {
        if (definitions.Count == 0)
        {
            return Array.Empty<AlterPublicFieldReadModel>();
        }

        return definitions
            .Select(def => new AlterPublicFieldReadModel(
                def.Id,
                def.Name,
                def.Type,
                alterFieldValues is not null && alterFieldValues.TryGetValue(def.Id, out var value) ? value : null))
            .ToArray();
    }
}
