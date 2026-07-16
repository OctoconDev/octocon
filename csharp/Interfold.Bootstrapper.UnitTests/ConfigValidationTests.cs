using System.Diagnostics;
using System.Text.Json;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Phases;
using Interfold.Contracts.Enums;
using TUnit.Core;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>
/// Unit tests for <see cref="ConfigPhase.Validate"/>. Each test stages a single invariant violation
/// against an otherwise-valid <see cref="BootstrapConfig"/> and asserts the validator throws with a
/// message naming the offending field — the human-readable error text matters because the operator
/// sees it inline when bootstrap fails.
/// </summary>
public sealed class ConfigValidationTests
{
    /// <summary>Default-construct a config that already satisfies every invariant.</summary>
    private static BootstrapConfig MakeValid() => new()
    {
        Deployment =
        {
            OutputDir = "./deploy",
            Hosts = ["api.example.com"],
            RootCaName = "Interfold Root CA",
            CertYears = 5,
            TrustStoreInstall = true,
        },
        Ports =
        {
            ApiHttp = 5000,
            ApiHttps = 5001,
            WebHttp = 8080,
            WebHttps = 8081,
        },
        DatabaseMode = DatabaseMode.Single,
    };

    [Test]
    public async Task ValidConfigPasses()
    {
        // Sanity check: the helper itself must satisfy the validator, otherwise every other test
        // here is testing the wrong thing.
        ConfigPhase.Validate(MakeValid());
        await Task.CompletedTask;
    }

    [Test]
    public async Task EmptyHostsListFailsValidation()
    {
        var cfg = MakeValid();
        cfg.Deployment.Hosts = [];

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("hosts");
    }

    [Test]
    public async Task ZeroCertYearsFailsValidation()
    {
        var cfg = MakeValid();
        cfg.Deployment.CertYears = 0;

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("certYears");
    }

    [Test]
    public async Task NegativeCertYearsFailsValidation()
    {
        var cfg = MakeValid();
        cfg.Deployment.CertYears = -1;

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("certYears");
    }

    [Test]
    public async Task CertYearsOverThirtyFailsValidation()
    {
        var cfg = MakeValid();
        cfg.Deployment.CertYears = 31;

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("certYears");
    }

    [Test]
    public async Task ApiHttpEqualsApiHttpsFailsValidation()
    {
        var cfg = MakeValid();
        // Same host port can't bind two listeners; the validator must surface this before publish.
        cfg.Ports.ApiHttp = 5000;
        cfg.Ports.ApiHttps = 5000;

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("apiHttp");
        await Assert.That(ex.Message).Contains("apiHttps");
    }

    [Test]
    public async Task DomainWithSpaceFailsValidation()
    {
        var cfg = MakeValid();
        cfg.Deployment.Hosts = ["api example.com"];

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("whitespace");
    }

    [Test]
    public async Task Ipv4HostPassesValidation()
    {
        // Self-hoster on a LAN box without any DNS - just the box's static IP. Must pass validation
        // and produce a working stack with an IP-SAN'd leaf cert. The derived CallbackBaseUrl is
        // always `https` + `:{Ports.ApiHttps}` (the API container always terminates HTTPS in
        // self-host independent of WebHttps; the default ApiHttps is 5001 — see
        // ConfigPhase.ResolveDerivedDefaults for the rationale).
        var cfg = MakeValid();
        cfg.Deployment.Hosts = ["192.168.1.42"];

        ConfigPhase.Validate(cfg);
        await Assert.That(cfg.ApiRuntime.CallbackBaseUrl).IsEqualTo("https://192.168.1.42:5001");
    }

    [Test]
    public async Task Ipv6HostPassesValidation()
    {
        // IPv6 literal as the only host. Validate must accept it, and the derived URL must
        // bracket-wrap the address per RFC 3986 §3.2.2 — un-bracketed IPv6 in a URL authority
        // is malformed and would break every downstream Uri.Parse caller. The :5001 port
        // suffix follows the closing bracket so the colons inside the bracketed authority
        // can't be confused with the port separator.
        var cfg = MakeValid();
        cfg.Deployment.Hosts = ["fe80::1"];

        ConfigPhase.Validate(cfg);
        await Assert.That(cfg.ApiRuntime.CallbackBaseUrl).IsEqualTo("https://[fe80::1]:5001");
    }

    [Test]
    public async Task Ipv4CidrPlusDnsHostPassesValidation()
    {
        // CIDR alone fails (no primary host), but CIDR alongside a DNS / IP entry is valid:
        // the DNS / IP entry serves as the primary, the CIDR widens the root-CA Name
        // Constraints scope.
        var cfg = MakeValid();
        cfg.Deployment.Hosts = ["api.example.com", "10.0.0.0/8"];

        ConfigPhase.Validate(cfg);
        await Assert.That(cfg.ApiRuntime.CallbackBaseUrl).IsEqualTo("https://api.example.com:5001");
    }

    [Test]
    public async Task Ipv6CidrPlusIpv4HostPassesValidation()
    {
        var cfg = MakeValid();
        cfg.Deployment.Hosts = ["192.168.1.42", "fe80::/64"];

        ConfigPhase.Validate(cfg);
        await Assert.That(cfg.ApiRuntime.CallbackBaseUrl).IsEqualTo("https://192.168.1.42:5001");
    }

    [Test]
    public async Task AllCidrHostsFailValidation()
    {
        // A CIDR-only host list has no leaf-eligible primary, so the leaf cert can't be issued
        // (it'd have no CN / no SANs). Validate must reject upfront.
        var cfg = MakeValid();
        cfg.Deployment.Hosts = ["192.168.1.0/24", "fe80::/64"];

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("non-CIDR");
    }

    [Test]
    public async Task CidrWithHostBitsSetFailsValidation()
    {
        // Operator typo: meant /32 (single host) or /24 (network) but typed the host address
        // with /24. Surface the fix-it from HostParser rather than silently normalising.
        var cfg = MakeValid();
        cfg.Deployment.Hosts = ["192.168.1.42/24"];

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("host bits");
        await Assert.That(ex.Message).Contains("192.168.1.0/24");
        await Assert.That(ex.Message).Contains("192.168.1.42/32");
    }

    [Test]
    public async Task DefaultConstructedDeploymentHasNoHosts()
    {
        // Pins the "no placeholder" contract from BootstrapConfig.Deployment.Hosts. A future
        // refactor that silently re-introduces ["api.example.com"] (or any other default) would
        // brick the fail-fast guarantee documented in DeploymentSection.Hosts' xmldoc.
        var deployment = new DeploymentSection();
        await Assert.That(deployment.Hosts.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DefaultConstructedBootstrapConfigFailsValidation()
    {
        // The flip side of DefaultConstructedDeploymentHasNoHosts: a non-interactive caller that
        // ships a config file omitting `deployment.hosts` (or just calls `new BootstrapConfig()`)
        // must hit a precise validation error rather than silently issuing a cert for some
        // placeholder.
        var cfg = new BootstrapConfig();

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("hosts");
        await Assert.That(ex.Message).Contains("at least one");
    }

    [Test]
    public async Task PortAboveMaxFailsValidation()
    {
        var cfg = MakeValid();
        cfg.Ports.ApiHttp = 70000;

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("ApiHttp");
    }

    [Test]
    public async Task InvalidDatabaseModeInJsonFailsDeserialization()
    {
        // DatabaseMode is now a strongly-typed enum with a snake-case JSON converter, so an
        // unrecognised wire value is rejected at parse time by the source-generated context
        // (not by ConfigPhase.Validate). Assert we still fail loudly on operator typos.
        const string badJson = """
        {
            "databaseMode": "quadruple-redundant"
        }
        """;
        var ex = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(badJson, BootstrapJsonContext.Default.BootstrapConfig));
        await Assert.That(ex.Message).Contains("DatabaseMode");
        await Assert.That(ex.Message).Contains("databaseMode");
    }

    [Test]
    public async Task DefaultPostgresDatabasePasses()
    {
        // The shipping default value must always pass validation - any drift here would brick
        // every operator who relies on the out-of-the-box configuration.
        var cfg = MakeValid();
        cfg.PostgresDatabase = "interfold";

        ConfigPhase.Validate(cfg);
        await Task.CompletedTask;
    }

    [Test]
    public async Task CustomSafePostgresDatabasePasses()
    {
        // Any identifier matching ^[A-Za-z_][A-Za-z0-9_]{0,62}$ must round-trip cleanly. We
        // pick a value that exercises underscores + digits because operators on shared clusters
        // commonly suffix the database name with an env tag (e.g. acme_prod).
        var cfg = MakeValid();
        cfg.PostgresDatabase = "acme_prod_42";

        ConfigPhase.Validate(cfg);
        await Task.CompletedTask;
    }

    [Test]
    public async Task EmptyPostgresDatabaseFailsValidation()
    {
        var cfg = MakeValid();
        cfg.PostgresDatabase = string.Empty;

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("postgresDatabase");
    }

    [Test]
    public async Task WhitespacePostgresDatabaseFailsValidation()
    {
        var cfg = MakeValid();
        cfg.PostgresDatabase = "   ";

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("postgresDatabase");
    }

    [Test]
    public async Task PostgresDatabaseStartingWithDigitFailsValidation()
    {
        // Postgres tolerates a leading digit only inside double quotes; rejecting it up front
        // keeps the seeder's CREATE DATABASE "<name>" emission unsurprising and avoids
        // identifier-handling drift between quoted and unquoted call sites.
        var cfg = MakeValid();
        cfg.PostgresDatabase = "1interfold";

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("postgresDatabase");
    }

    [Test]
    public async Task PostgresDatabaseWithDashFailsValidation()
    {
        // A dash is a valid character inside double-quoted Postgres identifiers but is forbidden
        // by our pattern so the value also works as a default role / schema prefix downstream
        // without further quoting gymnastics.
        var cfg = MakeValid();
        cfg.PostgresDatabase = "inter-fold";

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("postgresDatabase");
    }

    [Test]
    public async Task DefaultClusterNamePasses()
    {
        // The shipping default must always pass validation, exactly like postgresDatabase.
        var cfg = MakeValid();
        cfg.ClusterName = "InterfoldCluster";

        ConfigPhase.Validate(cfg);
        await Task.CompletedTask;
    }

    [Test]
    public async Task CustomClusterNameWithSpacesPasses()
    {
        // Cluster names are advertised in gossip metadata and shown in DESCRIBE CLUSTER output;
        // operators routinely put a human-readable name with spaces here (e.g. "Acme Prod"),
        // so we must accept spaces (unlike postgresDatabase).
        var cfg = MakeValid();
        cfg.ClusterName = "Acme Prod 1.0";

        ConfigPhase.Validate(cfg);
        await Task.CompletedTask;
    }

    [Test]
    public async Task EmptyClusterNameFailsValidation()
    {
        var cfg = MakeValid();
        cfg.ClusterName = string.Empty;

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("clusterName");
    }

    [Test]
    public async Task ClusterNameWithSingleQuoteFailsValidation()
    {
        // Single quotes are the highest-risk character because Cassandra's entrypoint rewrites
        // cassandra.yaml with the value pasted in; an unescaped quote would corrupt the YAML.
        var cfg = MakeValid();
        cfg.ClusterName = "Acme'Prod";

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("clusterName");
    }

    [Test]
    public async Task ClusterNameWithNewlineFailsValidation()
    {
        var cfg = MakeValid();
        cfg.ClusterName = "Acme\nProd";

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("clusterName");
    }

    [Test]
    public async Task OverlyLongClusterNameFailsValidation()
    {
        // 64 chars is the published Cassandra limit; anything past it must fail.
        var cfg = MakeValid();
        cfg.ClusterName = new string('A', 65);

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("clusterName");
    }

    [Test]
    public async Task InvalidScyllaKeyspaceInJsonFailsDeserialization()
    {
        // The seven valid values are now enforced by the ScyllaKeyspace enum + its snake-case
        // JSON converter, so an operator typo lands on the JSON deserializer rather than on
        // ConfigPhase.Validate. Pin the failure mode so a converter regression surfaces here.
        const string badJson = """
        {
            "scyllaKeyspace": "antarctica"
        }
        """;
        var ex = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(badJson, BootstrapJsonContext.Default.BootstrapConfig));
        await Assert.That(ex.Message).Contains("ScyllaKeyspace");
        await Assert.That(ex.Message).Contains("scyllaKeyspace");
    }

    [Test]
    public async Task EachValidScyllaKeyspacePasses()
    {
        // Smoke test that all seven canonical region values are accepted. The single test body
        // iterates so a future addition to the region list will fail loudly here first.
        foreach (var keyspace in Enum.GetValues<ScyllaKeyspace>())
        {
            var cfg = MakeValid();
            cfg.ScyllaKeyspace = keyspace;
            ConfigPhase.Validate(cfg);
        }
        await Task.CompletedTask;
    }

    [Test]
    public async Task NonHttpCallbackBaseUrlFailsValidation()
    {
        // The shared ValidateAbsoluteHttpUri helper rejects anything that doesn't parse as an
        // absolute http(s) URL. Even valid URIs with a different scheme (file://, ftp://, …)
        // must fail so the operator catches typos before the API tries to use the value in
        // the OAuth redirect-URL stitching.
        var cfg = MakeValid();
        cfg.ApiRuntime.CallbackBaseUrl = "ftp://api.example.com";

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("callbackBaseUrl");
    }

    [Test]
    public async Task EmptyJwtAudienceFailsValidation()
    {
        // jwtAudience has a property-initialiser default ("octocon") so it's never empty in
        // practice — but a hand-edited JSON with `"jwtAudience": ""` must reject upfront
        // rather than silently writing an empty value into OCTOCON_JWT_AUDIENCE.
        var cfg = MakeValid();
        cfg.ApiRuntime.JwtAudience = "";

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("jwtAudience");
    }

    [Test]
    public async Task NonHttpCorsOriginFailsValidation()
    {
        // Each CORS allow-list entry must parse as an absolute http(s) origin — bare hostnames,
        // wildcards, or non-http schemes would never match the request's Origin header at
        // runtime and are therefore a bootstrapper-time error.
        var cfg = MakeValid();
        cfg.ApiRuntime.CorsAllowedOrigins = ["https://app.example.com", "not-a-url"];

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("corsAllowedOrigins");
    }

    [Test]
    public async Task ValidateFillsDerivedApiRuntimeDefaults()
    {
        // Validate is the bootstrapper's single materialisation point for derived defaults: a
        // non-interactive caller (loading a JSON file that omits apiRuntime.*) must end up
        // with the same post-derivation values the interactive form would produce. This pins
        // the side-effect contract — Validate doesn't just check, it mutates.
        var cfg = MakeValid();
        cfg.Deployment.Hosts = ["api.example.com", "admin.example.com"];
        cfg.Deployment.WebHttps = true;
        // Force the apiRuntime fields back to their "empty" state (MakeValid leaves them at
        // their property-initialiser defaults which are already empty, but be explicit).
        cfg.ApiRuntime.CallbackBaseUrl = string.Empty;
        cfg.ApiRuntime.JwtAuthority = string.Empty;
        cfg.ApiRuntime.CorsAllowedOrigins = [];

        ConfigPhase.Validate(cfg);

        // API URL carries the default Ports.ApiHttps=5001 suffix (always https in self-host).
        // CORS scheme tracks WebHttps=true with the default Ports.WebHttps=8081 suffix.
        await Assert.That(cfg.ApiRuntime.CallbackBaseUrl).IsEqualTo("https://api.example.com:5001");
        await Assert.That(cfg.ApiRuntime.JwtAuthority).IsEqualTo("https://api.example.com:5001");
        await Assert.That(cfg.ApiRuntime.CorsAllowedOrigins.Count).IsEqualTo(2);
        await Assert.That(cfg.ApiRuntime.CorsAllowedOrigins).Contains("https://api.example.com:8081");
        await Assert.That(cfg.ApiRuntime.CorsAllowedOrigins).Contains("https://admin.example.com:8081");
    }

    // --- Cluster / Storage / Observability / Socket / Persistence tuning validation ---

    [Test]
    public async Task InvalidNodeGroupInJsonFailsDeserialization()
    {
        // Cluster.NodeGroup is now a strongly-typed enum + snake-case JSON converter, so an
        // unrecognised value fails at deserialization rather than at Validate. Preserves the
        // spirit of the old "fail fast on operator typo" contract.
        const string badJson = """
        {
            "cluster": { "nodeGroup": "guardian" }
        }
        """;
        var ex = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(badJson, BootstrapJsonContext.Default.BootstrapConfig));
        await Assert.That(ex.Message).Contains("NodeGroup");
        await Assert.That(ex.Message).Contains("nodeGroup");
    }

    [Test]
    public async Task EachValidNodeGroupPasses()
    {
        // Smoke test all three canonical values. Same shape as EachValidScyllaKeyspacePasses
        // — drives a future allow-list extension to fail loudly here first.
        foreach (var nodeGroup in Enum.GetValues<NodeGroup>())
        {
            var cfg = MakeValid();
            cfg.Cluster.NodeGroup = nodeGroup;
            ConfigPhase.Validate(cfg);
        }
        await Task.CompletedTask;
    }

    [Test]
    public async Task EmptyOptionalStringsPass()
    {
        // The four "disabled when empty" string fields (Avatar* + OtlpEndpoint) must accept
        // the empty default. This pins the contract that leaving those rows blank in the
        // form (or omitting the JSON section entirely) is a supported "feature disabled"
        // signal rather than a validation failure.
        var cfg = MakeValid();
        cfg.Storage.AvatarStorageRoot = string.Empty;
        cfg.Storage.AvatarPublicBase = string.Empty;
        cfg.Observability.OtlpEndpoint = string.Empty;
        cfg.Socket.BatchBytesThreshold = null;

        ConfigPhase.Validate(cfg);
        await Task.CompletedTask;
    }

    [Test]
    public async Task NonHttpAvatarPublicBaseFailsValidation()
    {
        // The optional URL fields reuse the same absolute http(s) check as the apiRuntime URL
        // fields — non-http schemes still fail, even when the field is optional overall.
        var cfg = MakeValid();
        cfg.Storage.AvatarPublicBase = "ftp://cdn.example.com/avatars/";

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("avatarPublicBase");
    }

    [Test]
    public async Task RelativeAvatarStorageRootFailsValidation()
    {
        // The avatar storage root lives inside the API container; relative paths would resolve
        // against the container's CWD (whatever Aspire baked into the image) and silently
        // break the avatar-write code path. Reject upfront.
        var cfg = MakeValid();
        cfg.Storage.AvatarStorageRoot = "avatars";

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("avatarStorageRoot");
    }

    [Test]
    public async Task NonHttpOtlpEndpointFailsValidation()
    {
        var cfg = MakeValid();
        cfg.Observability.OtlpEndpoint = "grpc://otel-collector:4317";

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("otlpEndpoint");
    }

    [Test]
    public async Task ZeroDbRetryAttemptsFailsValidation()
    {
        var cfg = MakeValid();
        cfg.Persistence.DbRetryAttempts = 0;

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("dbRetryAttempts");
    }

    [Test]
    public async Task DbRetryAttemptsAboveCapFailsValidation()
    {
        var cfg = MakeValid();
        cfg.Persistence.DbRetryAttempts = 9999;

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("dbRetryAttempts");
    }

    [Test]
    public async Task DbRetryMaxBelowInitialFailsValidation()
    {
        // The cross-check catches the easy swap mistake (initial=1500, max=100) which would
        // make the exponential backoff cap below the starting delay — a guaranteed source of
        // confused operators reading retry logs.
        var cfg = MakeValid();
        cfg.Persistence.DbRetryInitialDelayMs = 500;
        cfg.Persistence.DbRetryMaxDelayMs = 100;

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("dbRetryMaxDelayMs");
        await Assert.That(ex.Message).Contains("dbRetryInitialDelayMs");
    }

    [Test]
    public async Task HydrationConcurrencyAboveCapFailsValidation()
    {
        var cfg = MakeValid();
        cfg.Persistence.HydrationMaxConcurrency = 9999;

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("hydrationMaxConcurrency");
    }

    [Test]
    public async Task SocketBatchThresholdOutOfRangeFailsValidation()
    {
        // The nullable field is bounded only when set — null still passes (see
        // EmptyOptionalStringsPass above). Once supplied, the 1..16 MiB range applies.
        var cfg = MakeValid();
        cfg.Socket.BatchBytesThreshold = 0;

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("batchBytesThreshold");
    }

    [Test]
    public async Task DefaultBackupSectionPasses()
    {
        // The shipped defaults (enabled=false, schedule="daily", retain=14, dir="", autostart=false)
        // are the "no-op" stance — every operator's stock bootstrap.json includes them, so they
        // must satisfy the validator on day one.
        var cfg = MakeValid();
        // MakeValid leaves the section at its default; this assertion just pins the contract.
        await Assert.That(cfg.Backup.RetainCount).IsEqualTo(14);
        await Assert.That(cfg.Backup.Schedule).IsEqualTo("daily");
        ConfigPhase.Validate(cfg);
        await Task.CompletedTask;
    }

    [Test]
    public async Task ZeroBackupRetainCountFailsValidation()
    {
        // retainCount=0 would delete every backup as soon as it's written — almost certainly
        // a typo for the "disable scheduled backups" semantics (which is `enabled=false`).
        // Validator rejects to surface the misconfiguration at bootstrap time.
        var cfg = MakeValid();
        cfg.Backup.RetainCount = 0;

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("retainCount");
    }

    [Test]
    public async Task NegativeBackupRetainCountFailsValidation()
    {
        var cfg = MakeValid();
        cfg.Backup.RetainCount = -5;

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("retainCount");
    }

    [Test]
    public async Task BackupRetainCountAboveCapFailsValidation()
    {
        // 1000 is the documented cap; anything larger is almost certainly a units error
        // (operator confused minutes-to-keep with files-to-keep, or similar).
        var cfg = MakeValid();
        cfg.Backup.RetainCount = 5000;

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("retainCount");
    }

    [Test]
    public async Task EmptyBackupScheduleFailsValidation()
    {
        // An empty schedule renders into `OnCalendar=` (no value), which systemd-analyze
        // rejects — but rejecting it at config-load time gives the operator a faster
        // feedback loop and a clearer message.
        var cfg = MakeValid();
        cfg.Backup.Schedule = string.Empty;

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("schedule");
    }

    [Test]
    public async Task WhitespaceBackupScheduleFailsValidation()
    {
        var cfg = MakeValid();
        cfg.Backup.Schedule = "   ";

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("schedule");
    }

    [Test]
    public async Task BackupScheduleWithShellMetacharactersFailsValidation()
    {
        // Catches an operator trying to embed a `;` or `&` to chain a second command — the
        // schedule string ends up on the OnCalendar= line of a unit file, but a paranoid
        // upfront reject also defends against future code paths that might shell out with it.
        var cfg = MakeValid();
        cfg.Backup.Schedule = "daily;rm -rf /";

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("schedule");
    }

    [Test]
    public async Task WellKnownBackupScheduleShortcutsPass()
    {
        // systemd's OnCalendar= shortcuts. Every value here must satisfy the validator's
        // permissive pattern.
        foreach (var schedule in new[] { "hourly", "daily", "weekly", "monthly", "*-*-* 03:00", "Mon..Fri 03:30", "Sat *-*-* 04,16:00:00" })
        {
            var cfg = MakeValid();
            cfg.Backup.Schedule = schedule;
            ConfigPhase.Validate(cfg);
        }
        await Task.CompletedTask;
    }

    [Test]
    public async Task RelativeBackupDirectoryFailsValidation()
    {
        // Relative paths resolve against systemd-timer-driven invocations' unpredictable
        // CWD — we'd silently write to /run, /home/root, or wherever depending on the
        // distro's systemd unit defaults. Reject upfront.
        var cfg = MakeValid();
        cfg.Backup.Directory = "backups/interfold";

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("directory");
    }

    [Test]
    public async Task EmptyBackupDirectoryPasses()
    {
        // Empty == "use {outputDir}/backups". This is the default and most-common state.
        var cfg = MakeValid();
        cfg.Backup.Directory = string.Empty;

        ConfigPhase.Validate(cfg);
        await Task.CompletedTask;
    }

    [Test]
    public async Task AutostartTogglePassesIndependently()
    {
        // The Enabled / AutostartServer toggles are orthogonal — operators can enable
        // either independently of the other. Validator must accept all four combinations.
        foreach (var enabled in new[] { true, false })
        {
            foreach (var autostart in new[] { true, false })
            {
                var cfg = MakeValid();
                cfg.Backup.Enabled = enabled;
                cfg.Backup.AutostartServer = autostart;
                ConfigPhase.Validate(cfg);
            }
        }
        await Task.CompletedTask;
    }

    [Test]
    public async Task UpdateHealthCheckTimeoutZeroFailsValidation()
    {
        // Lower bound is 1s; a zero timeout would starve every probe on start. The
        // validator error names the field so the operator can find the offending key
        // in interfold.bootstrap.json.
        var cfg = MakeValid();
        cfg.Update.HealthCheckTimeoutSeconds = 0;

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("healthCheckTimeoutSeconds");
    }

    [Test]
    public async Task UpdateHealthCheckTimeoutAboveMaxFailsValidation()
    {
        // Upper bound is 3600s (1h). Anything higher signals a bug; the operator should
        // instead investigate WHY the stack takes over an hour to come up.
        var cfg = MakeValid();
        cfg.Update.HealthCheckTimeoutSeconds = 3601;

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("healthCheckTimeoutSeconds");
    }

    [Test]
    public async Task UpdateHealthCheckTimeoutInRangePasses()
    {
        // Boundary values are inclusive; 1s and 3600s are both accepted.
        var cfg = MakeValid();
        cfg.Update.HealthCheckTimeoutSeconds = 1;
        ConfigPhase.Validate(cfg);

        cfg.Update.HealthCheckTimeoutSeconds = 3600;
        ConfigPhase.Validate(cfg);

        await Task.CompletedTask;
    }

    [Test]
    public async Task UpdateServicesUnknownEntryFailsValidation()
    {
        // The whitelist is the source of truth for compose service names. Typos here
        // would surface later as a docker compose "no such service" error, which is
        // harder to diagnose than "config.update.services entry 'foo' is not a known
        // compose service".
        var cfg = MakeValid();
        cfg.Update.Services = ["msg-database"];

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("msg-database");
        await Assert.That(ex.Message).Contains("msg-db");
    }

    [Test]
    public async Task UpdateServicesBlankEntryFailsValidation()
    {
        // A stray empty string in the array probably means the JSON was hand-edited
        // to something like `["msg-db", ""]`; reject it so the operator notices.
        var cfg = MakeValid();
        cfg.Update.Services = ["msg-db", ""];

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("blank entry");
    }

    [Test]
    public async Task UpdateServicesKnownEntriesPassValidation()
    {
        // Every whitelisted service must be accepted individually.
        foreach (var svc in ConfigPhase.ValidUpdateServices)
        {
            var cfg = MakeValid();
            cfg.Update.Services = [svc];
            ConfigPhase.Validate(cfg);
        }
        await Task.CompletedTask;
    }

    [Test]
    public async Task UpdateServicesEmptyPasses()
    {
        // The default: "every service". Represented on the wire as an empty array; the
        // validator must not turn this into an error since it's the most common state.
        var cfg = MakeValid();
        cfg.Update.Services = [];

        ConfigPhase.Validate(cfg);
        await Task.CompletedTask;
    }

    [Test]
    public async Task UpdateTogglePassesIndependently()
    {
        // The Enabled / AutoRestoreOnFailure / RecreateOnUpdate toggles are orthogonal;
        // the validator accepts every combination so the operator can opt into each
        // behaviour independently.
        foreach (var enabled in new[] { true, false })
        {
            foreach (var autoRestore in new[] { true, false })
            {
                foreach (var recreate in new[] { true, false })
                {
                    var cfg = MakeValid();
                    cfg.Update.Enabled = enabled;
                    cfg.Update.AutoRestoreOnFailure = autoRestore;
                    cfg.Update.RecreateOnUpdate = recreate;
                    ConfigPhase.Validate(cfg);
                }
            }
        }
        await Task.CompletedTask;
    }

    [Test]
    public async Task MalformedJsonReturnsClearError()
    {
        // ConfigPhase.RunAsync uses JsonSerializer.Deserialize, which surfaces a JsonException
        // when the file is malformed; we exercise that path here so future regressions in error
        // handling are caught at unit speed instead of via a full DinD spin-up. We drive the
        // `publish` command (not `bootstrap`) so the prereqs phase is skipped — this keeps the
        // test runnable on Windows / macOS as well as Linux.
        var tmpDir = Path.Combine(Path.GetTempPath(), "interfold-cfg-malformed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try
        {
            var configPath = Path.Combine(tmpDir, "interfold.bootstrap.json");
            await File.WriteAllTextAsync(configPath, "{ this is not valid json");

            // Shell out to the binary so we exercise the real exit-code path the operator sees.
            var result = await RunBootstrapperAsync("publish", "--config", configPath,
                "--output-dir", tmpDir, "--non-interactive");

            await Assert.That(result.ExitCode).IsNotEqualTo(0)
                .Because("malformed JSON must abort the bootstrap");
            // The thrown JsonException's message varies across SDK versions but always names the
            // JSON parse failure mode in some form.
            var combined = result.Stdout + result.Stderr;
            await Assert.That(combined).Contains("JSON")
                .Or.Contains("json")
                .Or.Contains("parse");
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* best effort cleanup */ }
        }
    }

    [Test]
    public async Task MissingFileWithNonInteractiveExitsWithMessage()
    {
        // Drives `publish` (not `bootstrap`) so the prereqs phase is bypassed on non-Linux hosts.
        var tmpDir = Path.Combine(Path.GetTempPath(), "interfold-cfg-missing-ni-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try
        {
            var configPath = Path.Combine(tmpDir, "interfold.bootstrap.json");
            await Assert.That(File.Exists(configPath)).IsFalse();

            var result = await RunBootstrapperAsync("publish", "--config", configPath,
                "--output-dir", tmpDir, "--non-interactive");

            await Assert.That(result.ExitCode).IsNotEqualTo(0);
            await Assert.That(result.Stdout + result.Stderr)
                .Contains("Config file not found")
                .Or.Contains("non-interactive");
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* best effort cleanup */ }
        }
    }

    /// <summary>
    /// Shell out to the compiled bootstrapper so we exercise the exact same dispatch path the
    /// operator hits. We rely on TestSupport.BootstrapperPath to locate the Debug build; if it's
    /// absent the test is skipped (TUnit will surface this on the first call).
    /// </summary>
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunBootstrapperAsync(params string[] args)
    {
        var path = TestSupport.BootstrapperBinaryOrSkip();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Strip TTY so the "interactive" branch never triggers on hosts that happen to have one.
            // The --non-interactive flag also covers this in the bootstrapper, but redirecting
            // stdin is what *really* eliminates the prompt path.
            RedirectStandardInput = true,
        };
        psi.ArgumentList.Add(path);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start dotnet");
        proc.StandardInput.Close();
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, stdout, stderr);
    }
}
