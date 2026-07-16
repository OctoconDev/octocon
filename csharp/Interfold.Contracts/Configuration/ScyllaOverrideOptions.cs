namespace Interfold.Contracts.Configuration;

/// <summary>
/// Test/dev overrides for the Scylla client's contact-points and port. The production
/// values live in <c>internal.secrets</c> (rows <c>scylla:contact_points</c> and
/// <c>scylla:port</c>) and are the authoritative source in every non-test deployment;
/// these env-bound overrides exist purely so integration-test containers can point the
/// client at a host-published port that differs from the store-supplied cluster address.
///
/// Env bindings:
/// <list type="bullet">
///   <item><c>OCTOCON_SCYLLA_CONTACT_POINTS</c> — optional comma-separated host list</item>
///   <item><c>OCTOCON_SCYLLA_PORT</c> — optional integer port override</item>
/// </list>
///
/// A <c>null</c> value on either property means "no override — use the secrets-store row".
/// </summary>
public sealed class ScyllaOverrideOptions
{
    public const string SectionName = "Octocon:Scylla";

    /// <summary>
    /// Optional comma-separated Scylla contact points (already split by the binder into
    /// a de-duplicated, whitespace-trimmed array). <c>null</c> or empty means "no override".
    /// </summary>
    public IReadOnlyList<string>? ContactPoints { get; set; }

    /// <summary>
    /// Optional Scylla port override for host-published test containers. <c>null</c> means
    /// "no override — fall through to the store-supplied port (default 9042)".
    /// </summary>
    public int? Port { get; set; }
}
