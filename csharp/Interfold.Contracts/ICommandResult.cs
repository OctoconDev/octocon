namespace Interfold.Contracts;

/// <summary>
/// Marker interface for all command result records that carry a replay flag.
/// Used by the API layer to record metrics and structured log outcomes uniformly.
/// </summary>
public interface ICommandResult
{
    bool Replay { get; }
}

/// <summary>
/// Self-typed refinement so <c>IdempotentCommandHandler&lt;TPayload, TResult&gt;</c> can
/// synthesise the "same record but with <c>Replay = true</c>" for a stored idempotent
/// outcome without every result record hand-rolling its own <c>CreateReplayResult</c>
/// override on every handler subclass. Producing a fresh instance (rather than mutating a
/// stored one) keeps the immutability invariant every result record depends on.
///
/// <para>
/// The type parameter is the record's own type (F-bounded pattern): each result record
/// declares <c>ICommandResult&lt;MyResult&gt;</c> so <c>WithReplay()</c> returns the
/// concrete type back to the caller without a cast. That in turn lets the base handler
/// call <c>originalResult.WithReplay()</c> generically with full type safety, and 46
/// per-handler overrides collapse to one default virtual on the base.
/// </para>
/// </summary>
public interface ICommandResult<TSelf> : ICommandResult
    where TSelf : ICommandResult<TSelf>
{
    /// <summary>
    /// Returns a copy of this result with the <see cref="ICommandResult.Replay"/> flag set.
    /// Canonical implementation on each record is <c>this with { Replay = true }</c>; the
    /// only reason to hand-roll a different body is if a record has cross-field invariants
    /// that need to move together with the flag (none today).
    /// </summary>
    TSelf WithReplay();
}