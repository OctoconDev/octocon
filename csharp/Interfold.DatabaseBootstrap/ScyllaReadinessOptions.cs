namespace Interfold.DatabaseBootstrap;

/// <summary>
/// Options for <c>Interfold.Bootstrapper.Util.ScyllaReadinessProbe.WaitAsync</c>. Shaped as
/// a near-mirror of <see cref="PostgresReadinessOptions"/> — same
/// <c>Timeout</c>/<c>RequiredConsecutiveSuccesses</c> semantics — but where the postgres
/// probe polls <c>pg_isready</c> from inside the compose service, the scylla probe drives
/// a CQL round-trip via an injected <see cref="IScyllaExecutor"/> so both app-user and
/// cassandra-default fallbacks stay in scope.
/// </summary>
/// <param name="Timeout">Absolute deadline for the probe loop. Scylla's gossip/bootstrap
/// pass historically dominates first-boot time; the default caller in
/// <c>DatabaseInitPhase</c> uses 5 minutes.</param>
/// <param name="RequiredConsecutiveSuccesses">Number of back-to-back successful CQL
/// responses needed before the node is declared ready. Defaults to <c>1</c> because
/// scylla's own auth ready-check already ensures the node has finished bootstrap by
/// the time a CQL <c>DESCRIBE CLUSTER</c> succeeds — callers with a stricter policy
/// (mirroring the postgres <c>3</c>) can raise it.</param>
public record ScyllaReadinessOptions(
    TimeSpan Timeout,
    int RequiredConsecutiveSuccesses = 1
);
