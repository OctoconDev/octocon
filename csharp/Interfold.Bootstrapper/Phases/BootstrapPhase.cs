namespace Interfold.Bootstrapper.Phases;

/// <summary>
/// The orchestrated bootstrap phases, in execution order, as used by the hidden
/// <c>--fault-inject=after-&lt;phase&gt;</c> testability hook. The wire names are frozen —
/// integration tests and operator muscle memory pass the exact tokens.
/// </summary>
internal enum BootstrapPhase
{
    Prereqs,
    Config,
    Secrets,
    Certs,
    Publish,
    Firebase,
    DbInit,
    Launch,

    /// <summary>Sub-phase hook inside db-init: halts after Postgres init, before Scylla init.</summary>
    DbPostgres,
}

internal static class BootstrapPhaseExtensions
{
    public static string ToWireName(this BootstrapPhase phase) => phase switch
    {
        BootstrapPhase.Prereqs => "prereqs",
        BootstrapPhase.Config => "config",
        BootstrapPhase.Secrets => "secrets",
        BootstrapPhase.Certs => "certs",
        BootstrapPhase.Publish => "publish",
        BootstrapPhase.Firebase => "firebase",
        BootstrapPhase.DbInit => "db-init",
        BootstrapPhase.Launch => "launch",
        BootstrapPhase.DbPostgres => "db-postgres",
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "Unhandled BootstrapPhase."),
    };

    /// <summary>The <c>--fault-inject</c> token that halts execution after this phase.</summary>
    public static string ToFaultInjectToken(this BootstrapPhase phase) => $"after-{phase.ToWireName()}";
}
