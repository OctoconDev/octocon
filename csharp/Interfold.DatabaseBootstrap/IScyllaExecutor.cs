namespace Interfold.DatabaseBootstrap;

/// <summary>Transport surface driven by <see cref="ScyllaSeeder"/>. Two methods:
/// <c>ExecCqlAsync</c> throws on failure, <c>TryExecCqlAsync</c> returns success+output
/// so probes can distinguish "auth denied while bootstrapping" from "fatal".</summary>
public interface IScyllaExecutor
{
    Task ExecCqlAsync(string user, string password, string cql, CancellationToken ct);

    Task<ScyllaExecResult> TryExecCqlAsync(string user, string password, string cql, CancellationToken ct);
}

/// <summary>Result of <see cref="IScyllaExecutor.TryExecCqlAsync"/>. <paramref name="Output"/>
/// is <c>cqlsh</c> stdout for compose-exec, a flattened row dump for the DataStax driver.</summary>
public readonly record struct ScyllaExecResult(bool Succeeded, string Output);
