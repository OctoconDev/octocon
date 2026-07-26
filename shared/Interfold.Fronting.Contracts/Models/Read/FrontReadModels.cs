using Interfold.Alters.Contracts.Models;
using Interfold.Fronting.Contracts.Ids;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Fronting.Contracts.Models.Read;

public sealed record FrontActiveReadModel(
    BareAlter Alter,
    FrontHistoryReadModel Front,
    bool Primary
);

public sealed record FrontHistoryReadModel(
    FrontId Id,
    AlterId AlterId,
    string? Comment,
    DateTimeOffset TimeStart,
    DateTimeOffset? TimeEnd,
    SystemId UserId
);

// Front*Request + FrontStartEntry + FrontStartedResponse live in Interfold.Fronting.Contracts (Phase-3 migration).
