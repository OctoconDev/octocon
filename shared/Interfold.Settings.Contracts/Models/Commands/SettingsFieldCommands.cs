using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;

namespace Interfold.Shared.Contracts.Models.Commands;

/// <summary>
/// Public-API callers send <c>InsertedAtUtc: default(DateTime)</c>. The handler stamps the
/// real value from the envelope's <c>OccurredAt</c> after the idempotency hash is taken, so
/// the hashed payload stays stable across retries (otherwise every replay would carry a fresh
/// <c>DateTime.UtcNow</c> and trip <c>ConflictDuplicate</c>). The Simply Plural importer
/// constructs the command directly with the decoded ObjectId timestamp.
/// </summary>
public sealed record CreateFieldCommand(string Name, FieldType Type, VisibilityLevel SecurityLevel, bool Locked, DateTime InsertedAtUtc);

public sealed record UpdateFieldCommand(FieldId FieldId, string? Name, VisibilityLevel? SecurityLevel, bool? Locked);

public sealed record DeleteFieldCommand(FieldId FieldId);

public sealed record RelocateFieldCommand(FieldId FieldId, int Index);
