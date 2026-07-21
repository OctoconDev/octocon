using System.ComponentModel.DataAnnotations;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Phases;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Configuration.Validation;
using Interfold.Contracts.Enums;
using TUnit.Core;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>
/// Unit tests for the shared validation surface (<see cref="ConfigurationBounds"/> +
/// <see cref="AbsolutePathAttribute"/> + <see cref="AbsoluteHttpUriAttribute"/>). Two
/// responsibilities:
///
/// <list type="number">
///   <item>Pure attribute / bounds behaviour — the attributes and constants stand on their own.</item>
///   <item>Bootstrapper-vs-API parity — a value that trips the bootstrapper's
///     <see cref="ConfigPhase.Validate"/> must also trip <see cref="ValidateOnStart"/>-style
///     DataAnnotations on the matching options class, and vice versa. Drift here means an
///     operator sees a green config gate followed by a red API boot.</item>
/// </list>
/// </summary>
public sealed class SharedValidationBoundsTests
{
    // ---------------------------------------------------------------------------------
    // ConfigurationBounds — sanity checks that no bound has been swapped or zeroed.
    // ---------------------------------------------------------------------------------

    [Test]
    public async Task BoundsAreMonotonicAndPositive()
    {
        // Guards against the "someone accidentally set Min > Max" refactor mistake. Every
        // pair the API's [Range] attributes lean on is asserted here so a bad edit trips one
        // fast unit test rather than a mysterious boot-time DataAnnotations failure. The
        // (min, max, name) tuple form runs the check at runtime so the TUnit analyzer sees
        // dynamic operands instead of const-folding the comparison away.
        (int Min, int Max, string Name)[] bounds =
        [
            (ConfigurationBounds.DbRetryAttemptsMin, ConfigurationBounds.DbRetryAttemptsMax, nameof(ConfigurationBounds.DbRetryAttemptsMin)),
            (ConfigurationBounds.DbRetryInitialDelayMsMin, ConfigurationBounds.DbRetryInitialDelayMsMax, nameof(ConfigurationBounds.DbRetryInitialDelayMsMin)),
            (ConfigurationBounds.DbRetryMaxDelayMsMin, ConfigurationBounds.DbRetryMaxDelayMsMax, nameof(ConfigurationBounds.DbRetryMaxDelayMsMin)),
            (ConfigurationBounds.HydrationMaxConcurrencyMin, ConfigurationBounds.HydrationMaxConcurrencyMax, nameof(ConfigurationBounds.HydrationMaxConcurrencyMin)),
            (ConfigurationBounds.SocketBatchBytesThresholdMin, ConfigurationBounds.SocketBatchBytesThresholdMax, nameof(ConfigurationBounds.SocketBatchBytesThresholdMin)),
        ];

        foreach (var (min, max, name) in bounds)
        {
            await Assert.That(min < max).IsTrue().Because($"{name}: min ({min}) must be strictly less than max ({max}).");
            await Assert.That(min >= 1).IsTrue().Because($"{name}: min ({min}) must be at least 1 to reject the zero-attempts / zero-delay footgun.");
        }
    }

    [Test]
    public async Task SocketBatchBytesThresholdMaxIs16MiB()
    {
        // The 16 MiB ceiling is a wire contract with the WebSocket batcher — this pins it so
        // a future edit that widens the bound without adjusting the batcher's back-pressure
        // budget fails a test instead of only surfacing under real socket load. Materialising
        // both sides into locals dodges the TUnit const-compare analyzer.
        var actualMax = ConfigurationBounds.SocketBatchBytesThresholdMax;
        const int expected16MiB = 16 * 1024 * 1024;
        await Assert.That(actualMax == expected16MiB).IsTrue().Because($"SocketBatchBytesThresholdMax was {actualMax}, expected 16 MiB ({expected16MiB}).");
    }

    // ---------------------------------------------------------------------------------
    // AbsolutePathAttribute — pure attribute behaviour.
    // ---------------------------------------------------------------------------------

    [Test]
    public async Task AbsolutePathIsAbsolutePath_AcceptsRootedPath()
    {
        // Path.IsPathRooted returns true for both Unix ("/foo") and Windows ("C:\foo") shapes,
        // but the bootstrapper only runs on Unix + Windows hosts — accept both to avoid
        // asymmetry between CI hosts.
        await Assert.That(AbsolutePathAttribute.IsAbsolutePath("/var/lib/interfold")).IsTrue();
    }

    [Test]
    public async Task AbsolutePathIsAbsolutePath_RejectsRelativeAndBlank()
    {
        await Assert.That(AbsolutePathAttribute.IsAbsolutePath("relative/path")).IsFalse();
        await Assert.That(AbsolutePathAttribute.IsAbsolutePath(null)).IsFalse();
        await Assert.That(AbsolutePathAttribute.IsAbsolutePath("")).IsFalse();
        await Assert.That(AbsolutePathAttribute.IsAbsolutePath("   ")).IsFalse();
    }

    [Test]
    public async Task AbsolutePathAttribute_AllowEmpty_PassesForBlank()
    {
        // AllowEmpty defaults to true so an unset OCTOCON_AVATAR_STORAGE_ROOT (which the
        // env-var provider surfaces as "") isn't a boot failure — the storage-disabled branch
        // in LocalAvatarStorage handles null/blank internally.
        var attr = new AbsolutePathAttribute();
        var ctx = new ValidationContext(new object()) { DisplayName = "AvatarStorageRoot", MemberName = "AvatarStorageRoot" };
        await Assert.That(attr.GetValidationResult("", ctx)).IsEqualTo(ValidationResult.Success);
        await Assert.That(attr.GetValidationResult(null, ctx)).IsEqualTo(ValidationResult.Success);
    }

    [Test]
    public async Task AbsolutePathAttribute_RejectsRelativePath()
    {
        var attr = new AbsolutePathAttribute();
        var ctx = new ValidationContext(new object()) { DisplayName = "AvatarStorageRoot", MemberName = "AvatarStorageRoot" };
        var result = attr.GetValidationResult("etc/passwd", ctx);
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.ErrorMessage).Contains("absolute path");
    }

    // ---------------------------------------------------------------------------------
    // AbsoluteHttpUriAttribute — pure attribute behaviour.
    // ---------------------------------------------------------------------------------

    [Test]
    public async Task AbsoluteHttpUri_AcceptsHttpAndHttps()
    {
        await Assert.That(AbsoluteHttpUriAttribute.IsAbsoluteHttpUri("http://otel:4317")).IsTrue();
        await Assert.That(AbsoluteHttpUriAttribute.IsAbsoluteHttpUri("https://cdn.example.com/avatars/")).IsTrue();
    }

    [Test]
    public async Task AbsoluteHttpUri_RejectsNonHttpSchemes()
    {
        // The bootstrapper accepts only http(s) URIs for OTLP + AvatarPublicBase — gRPC schemes
        // and relative URIs must be rejected here so a hand-edited env doesn't sneak past the
        // API's ValidateOnStart.
        await Assert.That(AbsoluteHttpUriAttribute.IsAbsoluteHttpUri("grpc://otel:4317")).IsFalse();
        await Assert.That(AbsoluteHttpUriAttribute.IsAbsoluteHttpUri("file:///tmp/root.crt")).IsFalse();
        await Assert.That(AbsoluteHttpUriAttribute.IsAbsoluteHttpUri("/relative/url")).IsFalse();
        await Assert.That(AbsoluteHttpUriAttribute.IsAbsoluteHttpUri("not a url")).IsFalse();
    }

    [Test]
    public async Task AbsoluteHttpUri_RejectsBlankOnCoreCheck()
    {
        // Core boolean check treats blank as "not a URL" — the attribute wrapper handles the
        // AllowEmpty short-circuit for optional properties separately.
        await Assert.That(AbsoluteHttpUriAttribute.IsAbsoluteHttpUri(null)).IsFalse();
        await Assert.That(AbsoluteHttpUriAttribute.IsAbsoluteHttpUri("")).IsFalse();
        await Assert.That(AbsoluteHttpUriAttribute.IsAbsoluteHttpUri("   ")).IsFalse();
    }

    [Test]
    public async Task AbsoluteHttpUriAttribute_AllowEmpty_PassesForBlank()
    {
        var attr = new AbsoluteHttpUriAttribute();
        var ctx = new ValidationContext(new object()) { DisplayName = "OtlpEndpoint", MemberName = "OtlpEndpoint" };
        await Assert.That(attr.GetValidationResult("", ctx)).IsEqualTo(ValidationResult.Success);
        await Assert.That(attr.GetValidationResult(null, ctx)).IsEqualTo(ValidationResult.Success);
    }

    // ---------------------------------------------------------------------------------
    // Bootstrapper ↔ API parity — the whole point of the shared-bounds layer.
    // Every case tests one out-of-range value against both validators and asserts the
    // *same* value fails both. If a future refactor loosens one side, the parity test
    // fails and prevents the operator from seeing a green bootstrap gate followed by a
    // red API boot.
    // ---------------------------------------------------------------------------------

    [Test]
    public Task Parity_DbRetryAttemptsAboveMax_FailsBothSides()
    {
        // Bootstrapper side — ConfigPhase.Validate throws with dbRetryAttempts in the message.
        // API side — DataAnnotations validation on PersistenceConfiguration.DbRetryAttempts
        // fires the same [Range] check. Same input, same rejection.
        var overMax = ConfigurationBounds.DbRetryAttemptsMax + 1;
        var apiOpts = MakePersistenceValid();
        apiOpts.DbRetryAttempts = overMax;
        return AssertParityRejectionAsync(
            bootstrapMutate: c => c.Persistence.DbRetryAttempts = overMax,
            bootstrapMessageContains: "dbRetryAttempts",
            apiOpts: apiOpts,
            apiMemberName: nameof(PersistenceConfiguration.DbRetryAttempts));
    }

    [Test]
    public Task Parity_SocketBatchBytesThresholdAboveMax_FailsBothSides()
    {
        var overMax = ConfigurationBounds.SocketBatchBytesThresholdMax + 1;
        return AssertParityRejectionAsync(
            bootstrapMutate: c => c.Socket.BatchBytesThreshold = overMax,
            bootstrapMessageContains: "batchBytesThreshold",
            apiOpts: new SocketConfiguration { BatchBytesThreshold = overMax });
    }

    [Test]
    public Task Parity_AbsolutePathRejection_FailsBothSides()
        // Bootstrapper side — the config phase rejects a relative avatarStorageRoot.
        // API side — StorageConfiguration.AvatarStorageRoot carries [AbsolutePath] which
        // trips on the same relative input.
        => AssertParityRejectionAsync(
            bootstrapMutate: c => c.Storage.AvatarStorageRoot = "relative/avatars",
            bootstrapMessageContains: "avatarStorageRoot",
            apiOpts: new StorageConfiguration { AvatarStorageRoot = "relative/avatars" });

    [Test]
    public Task Parity_AbsoluteHttpUriRejection_FailsBothSides()
        // Bootstrapper side — non-http OtlpEndpoint blows up in ConfigPhase.Validate.
        // API side — same input fails [AbsoluteHttpUri] on ObservabilityConfiguration.
        => AssertParityRejectionAsync(
            bootstrapMutate: c => c.Observability.OtlpEndpoint = "grpc://otel:4317",
            bootstrapMessageContains: "otlpEndpoint",
            apiOpts: new ObservabilityConfiguration { OtlpEndpoint = "grpc://otel:4317" });

    /// <summary>
    /// Two-sided parity assertion for the shared-bounds contract: the same out-of-range value
    /// must be rejected by <see cref="ConfigPhase.Validate"/> AND by the DataAnnotations
    /// validation the API's <c>ValidateOnStart</c> pipeline runs. If one side drifts, an
    /// operator sees a green bootstrap gate followed by a red API boot — this helper's four
    /// call sites make sure both surfaces stay in lockstep on every bound. Pass
    /// <paramref name="apiMemberName"/> when the API-side test additionally pins which member
    /// name the failing <see cref="ValidationResult"/> reports.
    /// </summary>
    private static async Task AssertParityRejectionAsync(
        Action<BootstrapConfig> bootstrapMutate,
        string bootstrapMessageContains,
        object apiOpts,
        string? apiMemberName = null)
    {
        var cfg = MakeBootstrapValid();
        bootstrapMutate(cfg);
        var bootstrapperEx = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(bootstrapperEx.Message).Contains(bootstrapMessageContains);

        var apiResults = ValidateOptions(apiOpts);
        await Assert.That(apiResults.Count).IsGreaterThan(0);
        if (apiMemberName is not null)
        {
            await Assert.That(string.Join(";", apiResults.Select(r => r.MemberNames.FirstOrDefault()))).Contains(apiMemberName);
        }
    }

    // ---------------------------------------------------------------------------------
    // Fixture builders.
    // ---------------------------------------------------------------------------------

    /// <summary>Default-construct a bootstrap config that already satisfies every invariant.</summary>
    private static BootstrapConfig MakeBootstrapValid() => new()
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

    /// <summary>Default-construct a <see cref="PersistenceConfiguration"/> that satisfies every attribute.</summary>
    private static PersistenceConfiguration MakePersistenceValid() => new()
    {
        DbRetryAttempts = 3,
        DbRetryInitialDelay = TimeSpan.FromMilliseconds(100),
        DbRetryMaxDelay = TimeSpan.FromMilliseconds(1500),
        HydrationMaxConcurrency = 8,
        PostgresConnectionString = "Host=localhost;Port=5432;Database=interfold;Username=interfold;Password=interfold",
    };

    /// <summary>
    /// Runs both the property-level DataAnnotations attributes and the
    /// <see cref="IValidatableObject.Validate"/> hook — matches the strictness the
    /// <c>Microsoft.Extensions.Options</c> pipeline applies when
    /// <c>.ValidateDataAnnotations().ValidateOnStart()</c> fires at container boot.
    /// </summary>
    private static List<ValidationResult> ValidateOptions(object opts)
    {
        var ctx = new ValidationContext(opts);
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(opts, ctx, results, validateAllProperties: true);
        if (opts is IValidatableObject vo)
        {
            results.AddRange(vo.Validate(ctx));
        }
        return results;
    }
}
