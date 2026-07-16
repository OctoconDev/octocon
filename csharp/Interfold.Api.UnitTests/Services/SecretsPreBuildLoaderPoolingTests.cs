using Interfold.Api.Services.Secrets;
using Npgsql;

namespace Interfold.Api.UnitTests.Services;

/// <summary>
/// Pins the pool-isolation contract for <see cref="SecretsPreBuildLoader.WithDedicatedPoolIdentity"/>.
/// If any of these break, the loader will silently re-enter shared-pool territory (or the
/// bounded-pool guarantee will be lost) and one of two integration-suite regressions will come
/// back:
/// <list type="bullet">
///   <item>
///   Losing the dedicated pool identity → the loader rejoins the app's 5-slot pool
///   (<c>SharedDbFixture</c> deliberately pins <c>Maximum Pool Size=5</c>) and parallel factory
///   builds fail with <c>NpgsqlException: connection pool has been exhausted, either raise
///   'Max Pool Size' (currently 5) or 'Timeout' (currently 15 seconds)</c>.
///   </item>
///   <item>
///   Losing the bounded ceiling → each parallel factory build opens fresh physical connections
///   without bound, and once we hit Postgres's <c>max_connections</c> (default 100) the suite
///   fails with <c>Npgsql.PostgresException: 53300: sorry, too many clients already</c>.
///   </item>
/// </list>
///
/// Every test round-trips through the real <see cref="NpgsqlConnectionStringBuilder"/> so
/// keyword aliases and casing quirks are covered by the same parser Npgsql uses at
/// connection-open time.
/// </summary>
public sealed class SecretsPreBuildLoaderPoolingTests
{
    /// <summary>
    /// Core invariant: the loader stamps its distinctive Application Name onto the output.
    /// Npgsql keys its pool identity on the full canonical connection string, so this is what
    /// gives the loader its own pool separate from the app's. If this ever gets dropped, the
    /// loader silently re-joins the app pool and the "pool exhausted" regression returns.
    /// </summary>
    [Test]
    public async Task WithDedicatedPoolIdentity_StampsLoaderApplicationName()
    {
        const string input = "Host=localhost;Port=5432;Username=app;Password=pw;Database=octocon";

        var output = SecretsPreBuildLoader.WithDedicatedPoolIdentity(input);

        var parsed = new NpgsqlConnectionStringBuilder(output);
        await Assert.That(parsed.ApplicationName).IsEqualTo(SecretsPreBuildLoader.LoaderApplicationName);
    }

    /// <summary>
    /// Regression pin against a subtle failure mode: an operator (or upstream config) that
    /// stamps its own Application Name would previously have been honoured, leaving the loader
    /// sharing that consumer's pool identity. This test proves we override rather than defer.
    /// </summary>
    [Test]
    public async Task WithDedicatedPoolIdentity_OverridesPreExistingApplicationName()
    {
        const string input =
            "Host=localhost;Port=5432;Username=app;Password=pw;Database=octocon;Application Name=some-other-app";

        var output = SecretsPreBuildLoader.WithDedicatedPoolIdentity(input);

        var parsed = new NpgsqlConnectionStringBuilder(output);
        await Assert.That(parsed.ApplicationName).IsEqualTo(SecretsPreBuildLoader.LoaderApplicationName);
    }

    /// <summary>
    /// Bounded-ceiling invariant: MaxPoolSize on the output must match the loader's constant.
    /// This is what caps our Postgres client footprint at a known value. Without it, unbounded
    /// pool growth under high parallelism trips Postgres's <c>max_connections</c> and the
    /// "too many clients already" regression returns.
    /// </summary>
    [Test]
    public async Task WithDedicatedPoolIdentity_StampsBoundedMaxPoolSize()
    {
        const string input = "Host=localhost;Port=5432;Username=app;Password=pw;Database=octocon";

        var output = SecretsPreBuildLoader.WithDedicatedPoolIdentity(input);

        var parsed = new NpgsqlConnectionStringBuilder(output);
        await Assert.That(parsed.MaxPoolSize).IsEqualTo(SecretsPreBuildLoader.LoaderMaxPoolSize);
    }

    /// <summary>
    /// The specific shape the integration-test fixture uses (<c>SharedDbFixture</c>
    /// pins <c>Maximum Pool Size=5</c>). Pinned here so the regression is visible
    /// without re-running the whole integration suite: the loader must carry ITS OWN
    /// ceiling, not the fixture's 5-slot value.
    /// </summary>
    [Test]
    public async Task WithDedicatedPoolIdentity_OverridesFixtureMaxPoolSize()
    {
        const string input =
            "Host=localhost;Port=5432;Username=app;Password=pw;Database=octocon;Maximum Pool Size=5";

        var output = SecretsPreBuildLoader.WithDedicatedPoolIdentity(input);

        var parsed = new NpgsqlConnectionStringBuilder(output);
        await Assert.That(parsed.MaxPoolSize).IsEqualTo(SecretsPreBuildLoader.LoaderMaxPoolSize);
    }

    /// <summary>
    /// Pooling must stay enabled (the Npgsql default) so subsequent factory builds reuse
    /// physical connections instead of paying full TCP+auth handshake on every startup.
    /// Explicitly asserted here because an earlier iteration of this loader forced
    /// <c>Pooling=false</c>, which pushed the exhaustion problem down to Postgres itself
    /// (<c>53300: sorry, too many clients already</c>) — that regression must not return.
    /// </summary>
    [Test]
    public async Task WithDedicatedPoolIdentity_KeepsPoolingEnabled()
    {
        const string input =
            "Host=localhost;Port=5432;Username=app;Password=pw;Database=octocon;Pooling=false";

        var output = SecretsPreBuildLoader.WithDedicatedPoolIdentity(input);

        var parsed = new NpgsqlConnectionStringBuilder(output);
        await Assert.That(parsed.Pooling).IsTrue();
    }

    /// <summary>
    /// Round-trip invariant: setting our loader keywords must not clobber any other keyword
    /// the operator supplied (credentials, TLS mode, timeouts, etc.). If this breaks, a
    /// production deployment could silently lose critical settings on startup — no thanks.
    /// </summary>
    [Test]
    public async Task WithDedicatedPoolIdentity_PreservesAllOtherKeywords()
    {
        const string input =
            "Host=db.internal;Port=6543;Username=app;Password=super-secret;Database=octocon;" +
            "SSL Mode=Require;Timeout=30;Command Timeout=60";

        var output = SecretsPreBuildLoader.WithDedicatedPoolIdentity(input);

        var parsed = new NpgsqlConnectionStringBuilder(output);
        using (Assert.Multiple())
        {
            await Assert.That(parsed.Host).IsEqualTo("db.internal");
            await Assert.That(parsed.Port).IsEqualTo(6543);
            await Assert.That(parsed.Username).IsEqualTo("app");
            await Assert.That(parsed.Password).IsEqualTo("super-secret");
            await Assert.That(parsed.Database).IsEqualTo("octocon");
            await Assert.That(parsed.SslMode).IsEqualTo(SslMode.Require);
            await Assert.That(parsed.Timeout).IsEqualTo(30);
            await Assert.That(parsed.CommandTimeout).IsEqualTo(60);
            await Assert.That(parsed.ApplicationName).IsEqualTo(SecretsPreBuildLoader.LoaderApplicationName);
            await Assert.That(parsed.MaxPoolSize).IsEqualTo(SecretsPreBuildLoader.LoaderMaxPoolSize);
            await Assert.That(parsed.Pooling).IsTrue();
        }
    }

    /// <summary>
    /// Idempotence: applying the transform twice must produce a string that still parses
    /// with the loader's identity + ceiling. Guards against a future refactor that
    /// accidentally double-appends keywords or produces a string Npgsql refuses to parse.
    /// </summary>
    [Test]
    public async Task WithDedicatedPoolIdentity_IsIdempotent()
    {
        const string input = "Host=localhost;Port=5432;Username=app;Password=pw;Database=octocon";

        var once  = SecretsPreBuildLoader.WithDedicatedPoolIdentity(input);
        var twice = SecretsPreBuildLoader.WithDedicatedPoolIdentity(once);

        var parsed = new NpgsqlConnectionStringBuilder(twice);
        using (Assert.Multiple())
        {
            await Assert.That(parsed.ApplicationName).IsEqualTo(SecretsPreBuildLoader.LoaderApplicationName);
            await Assert.That(parsed.MaxPoolSize).IsEqualTo(SecretsPreBuildLoader.LoaderMaxPoolSize);
            await Assert.That(parsed.Pooling).IsTrue();
        }
    }
}
