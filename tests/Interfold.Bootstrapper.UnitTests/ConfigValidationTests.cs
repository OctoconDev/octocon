using System.Diagnostics;
using System.Text.Json;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Phases;
using Interfold.Contracts.Enums;
using TUnit.Core;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>Each test stages one invariant violation and asserts the validator throws
/// with a message naming the offending field — operators see it inline on failure.</summary>
public sealed class ConfigValidationTests
{
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

    /// <summary>Mutates a valid config, invokes Validate, asserts every fragment appears in
    /// the thrown message. Bespoke-config / non-throwing tests stay inline.</summary>
    private static async Task AssertInvalidAsync(
        Action<BootstrapConfig> mutate,
        params string[] messageContains)
    {
        var cfg = MakeValid();
        mutate(cfg);
        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        foreach (var frag in messageContains)
        {
            await Assert.That(ex.Message).Contains(frag);
        }
    }

    [Test]
    public async Task ValidConfigPasses()
    {
        ConfigPhase.Validate(MakeValid());
        await Task.CompletedTask;
    }

    [Test]
    public Task EmptyHostsListFailsValidation()
        => AssertInvalidAsync(c => c.Deployment.Hosts = [], "hosts");

    [Test]
    public Task ZeroCertYearsFailsValidation()
        => AssertInvalidAsync(c => c.Deployment.CertYears = 0, "certYears");

    [Test]
    public Task NegativeCertYearsFailsValidation()
        => AssertInvalidAsync(c => c.Deployment.CertYears = -1, "certYears");

    [Test]
    public Task CertYearsOverThirtyFailsValidation()
        => AssertInvalidAsync(c => c.Deployment.CertYears = 31, "certYears");

    [Test]
    public Task ApiHttpEqualsApiHttpsFailsValidation()
        // Same host port can't bind two listeners; the validator must surface this before publish.
        => AssertInvalidAsync(c =>
        {
            c.Ports.ApiHttp = 5000;
            c.Ports.ApiHttps = 5000;
        }, "apiHttp", "apiHttps");

    [Test]
    public Task DomainWithSpaceFailsValidation()
        => AssertInvalidAsync(c => c.Deployment.Hosts = ["api example.com"], "whitespace");

    [Test]
    public async Task Ipv4HostPassesValidation()
    {
        // LAN-only self-host: IP-SAN'd leaf; derived URL is always https + :ApiHttps (5001).
        var cfg = MakeValid();
        cfg.Deployment.Hosts = ["192.168.1.42"];

        ConfigPhase.Validate(cfg);
        await Assert.That(cfg.ApiRuntime.CallbackBaseUrl).IsEqualTo("https://192.168.1.42:5001");
    }

    [Test]
    public async Task Ipv6HostPassesValidation()
    {
        // Derived URL must bracket-wrap the address per RFC 3986 §3.2.2.
        var cfg = MakeValid();
        cfg.Deployment.Hosts = ["fe80::1"];

        ConfigPhase.Validate(cfg);
        await Assert.That(cfg.ApiRuntime.CallbackBaseUrl).IsEqualTo("https://[fe80::1]:5001");
    }

    [Test]
    public async Task Ipv4CidrPlusDnsHostPassesValidation()
    {
        // DNS/IP acts as primary; CIDR widens root-CA Name Constraints scope.
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
    // CIDR-only has no leaf-eligible primary.
    public Task AllCidrHostsFailValidation()
        => AssertInvalidAsync(c => c.Deployment.Hosts = ["192.168.1.0/24", "fe80::/64"], "non-CIDR");

    [Test]
    // Surface HostParser's fix-it rather than silently normalising.
    public Task CidrWithHostBitsSetFailsValidation()
        => AssertInvalidAsync(c => c.Deployment.Hosts = ["192.168.1.42/24"],
            "host bits", "192.168.1.0/24", "192.168.1.42/32");

    [Test]
    public async Task DefaultConstructedDeploymentHasNoHosts()
    {
        // Pins the "no placeholder" contract on Deployment.Hosts.
        var deployment = new DeploymentSection();
        await Assert.That(deployment.Hosts.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DefaultConstructedBootstrapConfigFailsValidation()
    {
        // Non-interactive callers omitting deployment.hosts must fail precisely, not silently.
        var cfg = new BootstrapConfig();

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("hosts");
        await Assert.That(ex.Message).Contains("at least one");
    }

    [Test]
    public Task PortAboveMaxFailsValidation()
        => AssertInvalidAsync(c => c.Ports.ApiHttp = 70000, "ApiHttp");

    [Test]
    public async Task InvalidDatabaseModeInJsonFailsDeserialization()
    {
        // Rejection lives in the source-generated context, not Validate.
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
        var cfg = MakeValid();
        cfg.PostgresDatabase = "interfold";

        ConfigPhase.Validate(cfg);
        await Task.CompletedTask;
    }

    [Test]
    public async Task CustomSafePostgresDatabasePasses()
    {
        // Exercise underscores + digits (typical env-suffixed name).
        var cfg = MakeValid();
        cfg.PostgresDatabase = "acme_prod_42";

        ConfigPhase.Validate(cfg);
        await Task.CompletedTask;
    }

    [Test]
    public Task EmptyPostgresDatabaseFailsValidation()
        => AssertInvalidAsync(c => c.PostgresDatabase = string.Empty, "postgresDatabase");

    [Test]
    public Task WhitespacePostgresDatabaseFailsValidation()
        => AssertInvalidAsync(c => c.PostgresDatabase = "   ", "postgresDatabase");

    [Test]
    // Postgres tolerates a leading digit only inside quotes; forbid up front to avoid drift.
    public Task PostgresDatabaseStartingWithDigitFailsValidation()
        => AssertInvalidAsync(c => c.PostgresDatabase = "1interfold", "postgresDatabase");

    [Test]
    // Dashes need quoting; forbidding them keeps the name reusable as a role/schema prefix.
    public Task PostgresDatabaseWithDashFailsValidation()
        => AssertInvalidAsync(c => c.PostgresDatabase = "inter-fold", "postgresDatabase");

    [Test]
    public async Task DefaultClusterNamePasses()
    {
        var cfg = MakeValid();
        cfg.ClusterName = "InterfoldCluster";

        ConfigPhase.Validate(cfg);
        await Task.CompletedTask;
    }

    [Test]
    public async Task CustomClusterNameWithSpacesPasses()
    {
        // Spaces are legitimate here (advertised in gossip / DESCRIBE CLUSTER).
        var cfg = MakeValid();
        cfg.ClusterName = "Acme Prod 1.0";

        ConfigPhase.Validate(cfg);
        await Task.CompletedTask;
    }

    [Test]
    public Task EmptyClusterNameFailsValidation()
        => AssertInvalidAsync(c => c.ClusterName = string.Empty, "clusterName");

    [Test]
    // A raw quote would corrupt the Cassandra entrypoint's cassandra.yaml rewrite.
    public Task ClusterNameWithSingleQuoteFailsValidation()
        => AssertInvalidAsync(c => c.ClusterName = "Acme'Prod", "clusterName");

    [Test]
    public Task ClusterNameWithNewlineFailsValidation()
        => AssertInvalidAsync(c => c.ClusterName = "Acme\nProd", "clusterName");

    [Test]
    // 64 chars is the published Cassandra limit.
    public Task OverlyLongClusterNameFailsValidation()
        => AssertInvalidAsync(c => c.ClusterName = new string('A', 65), "clusterName");

    [Test]
    public async Task InvalidScyllaKeyspaceInJsonFailsDeserialization()
    {
        // Rejection lives in the JSON converter, not Validate.
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
        // Iterates so future additions fail here first.
        foreach (var keyspace in Enum.GetValues<ScyllaKeyspace>())
        {
            var cfg = MakeValid();
            cfg.ScyllaKeyspace = keyspace;
            ConfigPhase.Validate(cfg);
        }
        await Task.CompletedTask;
    }

    [Test]
    public Task NonHttpCallbackBaseUrlFailsValidation()
        => AssertInvalidAsync(c => c.ApiRuntime.CallbackBaseUrl = "ftp://api.example.com", "callbackBaseUrl");

    [Test]
    // Even with a property-initialiser default, `"": ""` from hand-edited JSON must reject.
    public Task EmptyJwtAudienceFailsValidation()
        => AssertInvalidAsync(c => c.ApiRuntime.JwtAudience = "", "jwtAudience");

    [Test]
    public Task NonHttpCorsOriginFailsValidation()
        => AssertInvalidAsync(c => c.ApiRuntime.CorsAllowedOrigins = ["https://app.example.com", "not-a-url"],
            "corsAllowedOrigins");

    [Test]
    public async Task ValidateFillsDerivedApiRuntimeDefaults()
    {
        // Validate is a mutating check — it materialises apiRuntime defaults for JSON callers.
        var cfg = MakeValid();
        cfg.Deployment.Hosts = ["api.example.com", "admin.example.com"];
        cfg.Deployment.WebHttps = true;
        cfg.ApiRuntime.CallbackBaseUrl = string.Empty;
        cfg.ApiRuntime.JwtAuthority = string.Empty;
        cfg.ApiRuntime.CorsAllowedOrigins = [];

        ConfigPhase.Validate(cfg);

        // API URL is always https + ApiHttps (5001); CORS scheme follows WebHttps + WebHttps port.
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
        // Rejection lives in the JSON converter, not Validate.
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
        // Drives future allow-list extensions to fail here first.
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
        // Empty Avatar* / OtlpEndpoint = "feature disabled"; must not fail validation.
        var cfg = MakeValid();
        cfg.Storage.AvatarStorageRoot = string.Empty;
        cfg.Storage.AvatarPublicBase = string.Empty;
        cfg.Observability.OtlpEndpoint = string.Empty;
        cfg.Socket.BatchBytesThreshold = null;

        ConfigPhase.Validate(cfg);
        await Task.CompletedTask;
    }

    [Test]
    public Task NonHttpAvatarPublicBaseFailsValidation()
        => AssertInvalidAsync(c => c.Storage.AvatarPublicBase = "ftp://cdn.example.com/avatars/", "avatarPublicBase");

    [Test]
    // Relative paths resolve against the API container CWD → silent breakage.
    public Task RelativeAvatarStorageRootFailsValidation()
        => AssertInvalidAsync(c => c.Storage.AvatarStorageRoot = "avatars", "avatarStorageRoot");

    [Test]
    public Task NonHttpOtlpEndpointFailsValidation()
        => AssertInvalidAsync(c => c.Observability.OtlpEndpoint = "grpc://otel-collector:4317", "otlpEndpoint");

    [Test]
    public Task ZeroDbRetryAttemptsFailsValidation()
        => AssertInvalidAsync(c => c.Persistence.DbRetryAttempts = 0, "dbRetryAttempts");

    [Test]
    public Task DbRetryAttemptsAboveCapFailsValidation()
        => AssertInvalidAsync(c => c.Persistence.DbRetryAttempts = 9999, "dbRetryAttempts");

    [Test]
    // Cross-check for the easy swap mistake (initial > max makes the cap below the start).
    public Task DbRetryMaxBelowInitialFailsValidation()
        => AssertInvalidAsync(c =>
        {
            c.Persistence.DbRetryInitialDelayMs = 500;
            c.Persistence.DbRetryMaxDelayMs = 100;
        }, "dbRetryMaxDelayMs", "dbRetryInitialDelayMs");

    [Test]
    public Task HydrationConcurrencyAboveCapFailsValidation()
        => AssertInvalidAsync(c => c.Persistence.HydrationMaxConcurrency = 9999, "hydrationMaxConcurrency");

    [Test]
    // Nullable → null passes, but 1..16 MiB range enforced when supplied.
    public Task SocketBatchThresholdOutOfRangeFailsValidation()
        => AssertInvalidAsync(c => c.Socket.BatchBytesThreshold = 0, "batchBytesThreshold");

    [Test]
    public async Task DefaultBackupSectionPasses()
    {
        // Shipped defaults are the "no-op" stance and must pass day one.
        var cfg = MakeValid();
        await Assert.That(cfg.Backup.RetainCount).IsEqualTo(14);
        await Assert.That(cfg.Backup.Schedule).IsEqualTo("daily");
        ConfigPhase.Validate(cfg);
        await Task.CompletedTask;
    }

    [Test]
    // 0 would delete every backup as it's written — likely a typo for `enabled=false`.
    public Task ZeroBackupRetainCountFailsValidation()
        => AssertInvalidAsync(c => c.Backup.RetainCount = 0, "retainCount");

    [Test]
    public Task NegativeBackupRetainCountFailsValidation()
        => AssertInvalidAsync(c => c.Backup.RetainCount = -5, "retainCount");

    [Test]
    // 1000 is the documented cap; larger values are almost always a units mistake.
    public Task BackupRetainCountAboveCapFailsValidation()
        => AssertInvalidAsync(c => c.Backup.RetainCount = 5000, "retainCount");

    [Test]
    public Task EmptyBackupScheduleFailsValidation()
        => AssertInvalidAsync(c => c.Backup.Schedule = string.Empty, "schedule");

    [Test]
    public Task WhitespaceBackupScheduleFailsValidation()
        => AssertInvalidAsync(c => c.Backup.Schedule = "   ", "schedule");

    [Test]
    // Belt-and-braces: string lands on OnCalendar=, but reject shell chaining upfront.
    public Task BackupScheduleWithShellMetacharactersFailsValidation()
        => AssertInvalidAsync(c => c.Backup.Schedule = "daily;rm -rf /", "schedule");

    [Test]
    public async Task WellKnownBackupScheduleShortcutsPass()
    {
        foreach (var schedule in new[] { "hourly", "daily", "weekly", "monthly", "*-*-* 03:00", "Mon..Fri 03:30", "Sat *-*-* 04,16:00:00" })
        {
            var cfg = MakeValid();
            cfg.Backup.Schedule = schedule;
            ConfigPhase.Validate(cfg);
        }
        await Task.CompletedTask;
    }

    [Test]
    // Relative paths resolve against systemd's unpredictable CWD.
    public Task RelativeBackupDirectoryFailsValidation()
        => AssertInvalidAsync(c => c.Backup.Directory = "backups/interfold", "directory");

    [Test]
    public async Task EmptyBackupDirectoryPasses()
    {
        // Empty = default ({outputDir}/backups).
        var cfg = MakeValid();
        cfg.Backup.Directory = string.Empty;

        ConfigPhase.Validate(cfg);
        await Task.CompletedTask;
    }

    [Test]
    public async Task AutostartTogglePassesIndependently()
    {
        // Toggles are orthogonal.
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
    public Task UpdateHealthCheckTimeoutZeroFailsValidation()
        => AssertInvalidAsync(c => c.Update.HealthCheckTimeoutSeconds = 0, "healthCheckTimeoutSeconds");

    [Test]
    public Task UpdateHealthCheckTimeoutAboveMaxFailsValidation()
        => AssertInvalidAsync(c => c.Update.HealthCheckTimeoutSeconds = 3601, "healthCheckTimeoutSeconds");

    [Test]
    public async Task UpdateHealthCheckTimeoutInRangePasses()
    {
        // 1s and 3600s are the inclusive bounds.
        var cfg = MakeValid();
        cfg.Update.HealthCheckTimeoutSeconds = 1;
        ConfigPhase.Validate(cfg);

        cfg.Update.HealthCheckTimeoutSeconds = 3600;
        ConfigPhase.Validate(cfg);

        await Task.CompletedTask;
    }

    [Test]
    // Fail here with a clear name instead of "no such service" from docker compose.
    public Task UpdateServicesUnknownEntryFailsValidation()
        => AssertInvalidAsync(c => c.Update.Services = ["msg-database"], "msg-database", "msg-db");

    [Test]
    public Task UpdateServicesBlankEntryFailsValidation()
        => AssertInvalidAsync(c => c.Update.Services = ["msg-db", ""], "blank entry");

    [Test]
    public async Task UpdateServicesKnownEntriesPassValidation()
    {
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
        // Empty = "every service" (the default).
        var cfg = MakeValid();
        cfg.Update.Services = [];

        ConfigPhase.Validate(cfg);
        await Task.CompletedTask;
    }

    [Test]
    public async Task UpdateTogglePassesIndependently()
    {
        // All three toggles are orthogonal.
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
        // Drive `publish` so prereqs are skipped (Windows/macOS-friendly).
        using var scratch = TestSupport.NewScratchDir("interfold-cfg-malformed");
        var tmpDir = scratch.Path;
        var configPath = Path.Combine(tmpDir, "interfold.bootstrap.json");
        await File.WriteAllTextAsync(configPath, "{ this is not valid json");

        var result = await RunBootstrapperAsync("publish", "--config", configPath,
            "--output-dir", tmpDir, "--non-interactive");

        await Assert.That(result.ExitCode).IsNotEqualTo(0)
            .Because("malformed JSON must abort the bootstrap");
        // JsonException message varies by SDK version but always mentions parsing.
        var combined = result.Stdout + result.Stderr;
        await Assert.That(combined).Contains("JSON")
            .Or.Contains("json")
            .Or.Contains("parse");
    }

    [Test]
    public async Task MissingFileWithNonInteractiveExitsWithMessage()
    {
        // Drive `publish` so prereqs are skipped on non-Linux hosts.
        using var scratch = TestSupport.NewScratchDir("interfold-cfg-missing-ni");
        var tmpDir = scratch.Path;
        var configPath = Path.Combine(tmpDir, "interfold.bootstrap.json");
        await Assert.That(File.Exists(configPath)).IsFalse();

        var result = await RunBootstrapperAsync("publish", "--config", configPath,
            "--output-dir", tmpDir, "--non-interactive");

        await Assert.That(result.ExitCode).IsNotEqualTo(0);
        await Assert.That(result.Stdout + result.Stderr)
            .Contains("Config file not found")
            .Or.Contains("non-interactive");
    }

    /// <summary>Shells out to the compiled binary so tests exercise the operator dispatch
    /// path; TestSupport.BootstrapperBinaryOrSkip locates the Debug build.</summary>
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
            // Redirecting stdin eliminates the prompt path even if the host has a TTY.
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
