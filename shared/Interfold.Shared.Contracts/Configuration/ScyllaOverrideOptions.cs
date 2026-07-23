namespace Interfold.Shared.Contracts.Configuration;

/// <summary>Test/dev-only overrides for Scylla contact points and port. Production values
/// live in <c>internal.secrets</c>; a null property here means "no override — use the
/// secrets-store row". Env: OCTOCON_SCYLLA_CONTACT_POINTS, OCTOCON_SCYLLA_PORT.</summary>
public sealed class ScyllaOverrideOptions
{
    public const string SectionName = "Octocon:Scylla";

    public IReadOnlyList<string>? ContactPoints { get; set; }

    public int? Port { get; set; }
}
