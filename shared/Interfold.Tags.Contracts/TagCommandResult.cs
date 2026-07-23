using Interfold.Shared.Contracts.Ids;

namespace Interfold.Shared.Contracts;

// Extracted from OperationResults.cs during Phase-3 Tags migration. The other per-feature
// command results (Alter, Poll, etc.) will migrate with their respective features.

public sealed record TagCommandResult(SystemId SystemId, TagId TagId, bool Replay) : ICommandResult<TagCommandResult>
{
    public TagCommandResult WithReplay() => this with { Replay = true };
}
