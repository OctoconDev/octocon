using Interfold.Contracts.Ids;

namespace Interfold.Contracts;

public sealed record PollCommandResult(SystemId SystemId, PollId PollId, bool Replay) : ICommandResult<PollCommandResult>
{
    public PollCommandResult WithReplay() => this with { Replay = true };
}
