using Interfold.Fronting.Contracts.Ids;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Fronting.Contracts;

public sealed record FrontCommandResult(SystemId SystemId, AlterId? AlterId, FrontId? FrontId, bool Replay) : ICommandResult<FrontCommandResult>
{
    public FrontCommandResult WithReplay() => this with { Replay = true };
}
