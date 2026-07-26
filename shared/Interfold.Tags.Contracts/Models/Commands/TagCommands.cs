using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Tags.Contracts.Ids;

namespace Interfold.Tags.Contracts.Models.Commands;

/// <summary>
/// Public-API callers send <c>InsertedAtUtc: default(DateTime)</c>. The handler stamps the
/// real value from the envelope's <c>OccurredAt</c> after the idempotency hash is taken, so
/// the hashed payload stays stable across retries (otherwise every replay would carry a fresh
/// <c>DateTime.UtcNow</c> and trip <c>ConflictDuplicate</c>). The Simply Plural importer
/// constructs the command directly with the decoded ObjectId timestamp.
/// </summary>
public sealed record CreateTagCommand(string Name, TagId? ParentTagId, DateTime InsertedAtUtc);

public sealed record UpdateTagCommand(
	TagId TagId,
	string? Name,
	HexColor? Color,
	string? Description,
	VisibilityLevel? SecurityLevel
);

public sealed record DeleteTagCommand(TagId TagId);

public sealed record AttachAlterToTagCommand(TagId TagId, AlterId AlterId);

public sealed record DetachAlterFromTagCommand(TagId TagId, AlterId AlterId);

public sealed record SetParentTagCommand(TagId TagId, TagId ParentTagId);

public sealed record RemoveParentTagCommand(TagId TagId);
