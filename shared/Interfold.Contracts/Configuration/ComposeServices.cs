using Interfold.Contracts.Enums;

namespace Interfold.Contracts.Configuration;

/// <summary>Docker Compose service names emitted by <c>InterfoldAppHost</c>. Shared with
/// the bootstrapper's ConfigPhase whitelist; any addition MUST also be added to
/// <c>InterfoldAppHost.Configure</c>. Regional Scylla names mirror
/// <see cref="Interfold.Contracts.Enums.ScyllaKeyspace"/> 1:1.</summary>
public static class ComposeServices
{
    /// <summary>Postgres / TimescaleDB service (application state + <c>internal.secrets</c>).</summary>
    public const string Postgres = "msg-db";

    /// <summary>Single-node Scylla service (used in <c>single</c> DatabaseMode).</summary>
    public const string ScyllaSingle = "scylla";

    /// <summary>North America seed / node for multi-region Scylla deployments.</summary>
    public const string ScyllaNam = "scylla-nam";

    /// <summary>Europe node for multi-region Scylla deployments.</summary>
    public const string ScyllaEur = "scylla-eur";

    /// <summary>South America node for multi-region Scylla deployments.</summary>
    public const string ScyllaSam = "scylla-sam";

    /// <summary>South Asia node for multi-region Scylla deployments.</summary>
    public const string ScyllaSas = "scylla-sas";

    /// <summary>East Asia node for multi-region Scylla deployments.</summary>
    public const string ScyllaEas = "scylla-eas";

    /// <summary>Oceania node for multi-region Scylla deployments.</summary>
    public const string ScyllaOcn = "scylla-ocn";

    /// <summary>GDPR (EU data residency) node for multi-region Scylla deployments.</summary>
    public const string ScyllaGdpr = "scylla-gdpr";

    /// <summary>Cassandra service (used in <c>cassandra</c> DatabaseMode as the Scylla fallback).</summary>
    public const string Cassandra = "cassandra";

    /// <summary>API container. Emits OCTOCON_* env, terminates TLS, hosts the WebSocket endpoint.</summary>
    public const string InterfoldApi = "interfold-api";

    /// <summary>Static/octocon web tier proxy in front of the API when web-https is enabled.</summary>
    public const string OctoconWeb = "octocon-web";

    /// <summary>Seven regional Scylla nodes in ScyllaKeyspace declaration order.</summary>
    public static readonly string[] ScyllaRegionalNodes =
    [
        ScyllaNam, ScyllaEur, ScyllaSam, ScyllaSas,
        ScyllaEas, ScyllaOcn, ScyllaGdpr,
    ];

    /// <summary>All compose services the bootstrapper will accept as an <c>--service</c> filter.</summary>
    public static readonly string[] AllValidUpdateServices =
    [
        Postgres,
        ScyllaSingle,
        ScyllaNam, ScyllaEur, ScyllaSam, ScyllaSas,
        ScyllaEas, ScyllaOcn, ScyllaGdpr,
        Cassandra,
        InterfoldApi,
        OctoconWeb,
    ];

    /// <summary>Single naming rule for Scylla node services: multi-node → <c>scylla-{region}</c>,
    /// single → <c>scylla</c>. Shared with the AppHost graph and bind-mount renderer.</summary>
    public static string ToScyllaNodeName(string regionWireValue, bool multiNode)
        => multiNode ? $"scylla-{regionWireValue}" : ScyllaSingle;

    public static string ToScyllaNodeName(Enums.ScyllaKeyspace keyspace, bool multiNode)
        => ToScyllaNodeName(keyspace.ToWire(), multiNode);
}
