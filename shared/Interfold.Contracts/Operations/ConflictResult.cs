using Interfold.Contracts.Ids;

namespace Interfold.Contracts.Operations;

public enum ConflictCode
{
    ConflictDuplicate,
    ConflictInvariant
}

public sealed record ConflictResult(
    ConflictCode Code,
    OperationId OperationId,
    EntityRef EntityRef,
    ResolutionHint ResolutionHint
);
