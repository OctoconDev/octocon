namespace Interfold.Contracts;

/// <summary>Marker for command result records carrying a replay flag.</summary>
public interface ICommandResult
{
    bool Replay { get; }
}

/// <summary>F-bounded self-type so <c>IdempotentCommandHandler</c> can synthesise a
/// "same record with Replay = true" copy from a stored outcome without a per-handler
/// override. Canonical <c>WithReplay()</c> body: <c>this with { Replay = true }</c>.</summary>
public interface ICommandResult<TSelf> : ICommandResult
    where TSelf : ICommandResult<TSelf>
{
    TSelf WithReplay();
}
