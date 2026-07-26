using Interfold.Polls.Contracts.Ids;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Polls.Contracts;

public sealed record PollCommandResult(SystemId SystemId, PollId PollId, bool Replay) : ICommandResult<PollCommandResult>
{
    public PollCommandResult WithReplay() => this with { Replay = true };
}
