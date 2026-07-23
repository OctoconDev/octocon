namespace Interfold.DatabaseBootstrap;

/// <summary>Options for <c>ScyllaReadinessProbe.WaitAsync</c>. Mirrors
/// <see cref="PostgresReadinessOptions"/>, but drives CQL via <see cref="IScyllaExecutor"/>
/// instead of shelling to <c>pg_isready</c>.</summary>
/// <param name="Timeout">Absolute deadline for the probe loop (DatabaseInitPhase uses 5 min).</param>
/// <param name="RequiredConsecutiveSuccesses">Back-to-back CQL successes required
/// before "ready". Defaults to 1; scylla's auth-ready gate already implies bootstrap
/// finished by the time <c>DESCRIBE CLUSTER</c> returns.</param>
public record ScyllaReadinessOptions(
    TimeSpan Timeout,
    int RequiredConsecutiveSuccesses = 1
);
