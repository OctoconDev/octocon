using System.Text.Json.Serialization;
using Interfold.Fronting.Contracts.Ids;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Validation;

namespace Interfold.Fronting.Contracts.Models.Read;

public sealed record FrontBulkUpdateRequest(
    IReadOnlyList<FrontStartEntry> Start,
    IReadOnlyList<AlterId> End
);

public sealed record FrontStartEntry(AlterId AlterId, string? Comment = null);

// Front start/end/set/primary requests take the target alter as `id` — the only wire
// name the Kotlin client (and the replay fixtures) ever send. The former `alter_id`
// alias was exercised only by the server's own tests and has been removed.
//
// Attribute targets on record positional parameters:
// * JsonPropertyName has to live on the compiler-generated property so
//   System.Text.Json's property-based deserialiser sees it (the `property:` target).
// * ValidAlterId has to live on the *constructor parameter*, not the property,
//   because ASP.NET Core MVC's ObjectModelValidator calls
//   ThrowIfRecordTypeHasValidationOnProperties() on records and hard-fails with a
//   500 the moment it finds validation metadata on a record property. Attributes
//   without an explicit target on a record positional parameter default to the
//   parameter target, which is exactly where MVC's parameter-binding validator
//   picks them up.
public sealed record FrontStartRequest(
    [property: JsonPropertyName("id")][ValidAlterId] AlterId Id = default,
    string? Comment = null
);

public sealed record FrontEndRequest(
    [property: JsonPropertyName("id")][ValidAlterId] AlterId Id = default
);

// FrontPrimaryRequest.Id stays nullable — `null` legitimately clears the primary front
// — so ValidAlterId is applied with AllowNull = true; a caller who sends `id: 0`
// still gets a 400 with the same shape as every other AlterId payload.
public sealed record FrontPrimaryRequest(
    [property: JsonPropertyName("id")][ValidAlterId(AllowNull = true)] AlterId? Id = null
);

public sealed record FrontSetRequest(
    [property: JsonPropertyName("id")][ValidAlterId] AlterId Id = default,
    string? Comment = null
);

public sealed record FrontCommentRequest(
    string Comment
);

public sealed record FrontStartedResponse(FrontId FrontId);
