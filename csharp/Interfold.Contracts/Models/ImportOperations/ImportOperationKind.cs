using System.Text.Json.Serialization;
using Interfold.Contracts.Enums;

namespace Interfold.Contracts.Models.ImportOperations;

/// <summary>
/// The third-party platform an import operation is pulling data from. Persisted as its
/// lowercase enum-name string in the <c>import_operations.kind</c> column and emitted as
/// the same value in socket completion frames + HTTP dispatch responses. A future
/// integration adds a value here (and a matching socket-frame constant in
/// <c>SocketEventNames.Imports</c>).
/// </summary>
/// <remarks>
/// Wire values are locked to <c>"sp"</c> / <c>"pk"</c> via <see cref="JsonStringEnumMemberNameAttribute"/>
/// so the enum names can evolve without breaking the persisted / on-wire representation.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<ImportOperationKind>))]
public enum ImportOperationKind
{
    /// <summary>Simply Plural — handled by <c>SimplyPluralImportService</c>. Wire value: <c>"sp"</c>.</summary>
    [JsonStringEnumMemberName("sp")]
    SimplyPlural,

    /// <summary>PluralKit — handler is currently a stub but inherits the same dispatch model. Wire value: <c>"pk"</c>.</summary>
    [JsonStringEnumMemberName("pk")]
    PluralKit,
}
