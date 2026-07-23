using Interfold.Contracts.Ids;

namespace Interfold.Contracts;

public sealed record FrontCommandResult(SystemId SystemId, AlterId? AlterId, FrontId? FrontId, bool Replay) : ICommandResult<FrontCommandResult>
{
    public FrontCommandResult WithReplay() => this with { Replay = true };
}
