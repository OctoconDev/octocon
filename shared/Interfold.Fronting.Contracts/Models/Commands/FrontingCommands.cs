using Interfold.Shared.Contracts.Ids;

namespace Interfold.Shared.Contracts.Models.Commands;

public sealed record StartFrontCommand(AlterId AlterId, string? Comment);

public sealed record EndFrontCommand(AlterId AlterId);

public sealed record SetPrimaryFrontCommand(AlterId? AlterId);

public sealed record FrontStartItem(AlterId AlterId, string? Comment);

public sealed record BulkUpdateFrontCommand(IReadOnlyList<FrontStartItem> Start, IReadOnlyList<AlterId> End);

public sealed record SetFrontCommand(AlterId AlterId, string? Comment);

public sealed record DeleteFrontByIdCommand(FrontId FrontId);

public sealed record UpdateFrontCommentCommand(FrontId FrontId, string Comment);
