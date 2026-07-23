using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;

namespace Interfold.Contracts.Models.Commands;

public sealed record CreateAlterCommand(string Name, DateTimeOffset CreatedAt);

// Property-initialiser record shape (rather than positional) so callers only spell out
// the fields they actually want to set — the alter-avatar handlers in AltersController
// were previously passing 13-14 nulls positionally which drowned the meaningful arguments.
// Every remaining positional field is either always required (AlterId, UpdatedAt) or has
// a natural null / false default; nothing forces callers to spell out fields they aren't
// mutating. See PR #12 review comments #7, #8, #9.
public sealed record UpdateAlterCommand
{
    public required AlterId AlterId { get; init; }
    public string? Name { get; init; }
    public string? Description { get; init; }
    public AvatarUrl? AvatarUrl { get; init; }
    public AvatarSource? AvatarSource { get; init; }
    public HexColor? Color { get; init; }
    public string? Pronouns { get; init; }
    public VisibilityLevel? SecurityLevel { get; init; }
    public IReadOnlyList<AlterFieldCommand>? Fields { get; init; }
    public string? ProxyName { get; init; }
    public string? Alias { get; init; }
    public bool? Untracked { get; init; }
    public bool? Archived { get; init; }
    public bool? Pinned { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public bool ClearAvatar { get; init; }
}

public sealed record DeleteAlterCommand(AlterId AlterId);

// Id references the settings field definition (FieldId). Property name is frozen:
// it serializes as "id" in persisted command JSON and idempotency hashes.
public sealed record AlterFieldCommand(FieldId Id, string? Value);
