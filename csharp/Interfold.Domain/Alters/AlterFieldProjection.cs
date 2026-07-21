using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Read;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Domain.Alters;

/// <summary>
/// Backend-agnostic helpers for guarded alter-field projection. Both
/// <c>ScyllaAlterRepository</c> and <c>InMemoryAlterRepository</c> previously carried
/// byte-identical private copies of <c>ResolveGuardedFields</c> and
/// <c>ResolveVisibleDefinitionsAsync</c> — the former joined an alter's stored field
/// values against the caller-visible definition set, the latter filtered the
/// system-wide definitions by the viewer's friendship level. Consolidated here so a
/// visibility-rule tweak lands in one place and can't drift between backends.
/// <para>
/// Kept in <c>Interfold.Domain</c> (not either infrastructure project) so both backends
/// can consume it via the domain project reference they already carry. No DI service —
/// static methods match the shape of the existing <c>ScyllaSharedQueries</c> seam and
/// avoid constructor-signature churn on every repo that needs the helpers.
/// </para>
/// </summary>
public static class AlterFieldProjection
{
    /// <summary>
    /// Joins an alter's stored field values against the caller-visible definitions,
    /// emitting one <see cref="AlterPublicFieldReadModel"/> per definition that the
    /// alter has actually populated. Definitions without a value are skipped — the
    /// unguarded read path (<c>ResolveAlterFields</c> in <c>ScyllaSharedQueries</c>)
    /// emits one entry per definition including nulls; the guarded path is stricter
    /// because "field exists but empty" is not a leak the viewer needs to see.
    /// </summary>
    /// <param name="alterFieldValues">
    /// Field-id to raw-value map from the alter's row/state. Both keys and values may
    /// be sparse: keys missing from <paramref name="definitions"/> are ignored, and
    /// definitions missing from this map are omitted from the output.
    /// </param>
    /// <param name="definitions">
    /// The set of field definitions the caller is allowed to see, already filtered by
    /// <see cref="ResolveVisibleDefinitionsAsync"/>. Passed as a plain list so the
    /// helper can iterate definition-first (preserving definition order) rather than
    /// value-first.
    /// </param>
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

    /// <summary>
    /// Returns the subset of the system's field definitions that the viewer at
    /// <paramref name="friendshipLevel"/> is allowed to see, per the
    /// <c>def.SecurityLevel.CanBeViewedBy(friendshipLevel)</c> gate.
    /// <paramref name="friendshipLevel"/> is null for anonymous viewers (public-only).
    /// </summary>
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
