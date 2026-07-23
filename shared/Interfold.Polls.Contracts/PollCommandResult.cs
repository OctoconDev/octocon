using Interfold.Shared.Contracts.Ids;

namespace Interfold.Shared.Contracts;

public sealed record PollCommandResult(SystemId SystemId, PollId PollId, bool Replay) : ICommandResult<PollCommandResult>
{
    public PollCommandResult WithReplay() => this with { Replay = true };
}
