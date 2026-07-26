using Interfold.Api.Host.Services.Secrets;
using Npgsql;

namespace Interfold.Api.UnitTests.Services;

// Pins the pool-isolation contract for SecretsPreBuildLoader.WithDedicatedPoolIdentity.
// Regressions here re-expose either the "pool exhausted" (loader joined the app's 5-slot
// pool) or "too many clients already" (loader pool unbounded) integration-suite failures.
// Every test round-trips through NpgsqlConnectionStringBuilder so keyword aliases and
// casing quirks are covered by the same parser Npgsql uses at open-time.
public sealed class SecretsPreBuildLoaderPoolingTests
{
    [Test]
    public async Task WithDedicatedPoolIdentity_StampsLoaderApplicationName()
    {
        const string input = "Host=localhost;Port=5432;Username=app;Password=pw;Database=octocon";

        var output = SecretsPreBuildLoader.WithDedicatedPoolIdentity(input);

        var parsed = new NpgsqlConnectionStringBuilder(output);
        await Assert.That(parsed.ApplicationName).IsEqualTo(SecretsPreBuildLoader.LoaderApplicationName);
    }

    [Test]
    public async Task WithDedicatedPoolIdentity_OverridesPreExistingApplicationName()
    {
        const string input =
            "Host=localhost;Port=5432;Username=app;Password=pw;Database=octocon;Application Name=some-other-app";

        var output = SecretsPreBuildLoader.WithDedicatedPoolIdentity(input);

        var parsed = new NpgsqlConnectionStringBuilder(output);
        await Assert.That(parsed.ApplicationName).IsEqualTo(SecretsPreBuildLoader.LoaderApplicationName);
    }

    [Test]
    public async Task WithDedicatedPoolIdentity_StampsBoundedMaxPoolSize()
    {
        const string input = "Host=localhost;Port=5432;Username=app;Password=pw;Database=octocon";

        var output = SecretsPreBuildLoader.WithDedicatedPoolIdentity(input);

        var parsed = new NpgsqlConnectionStringBuilder(output);
        await Assert.That(parsed.MaxPoolSize).IsEqualTo(SecretsPreBuildLoader.LoaderMaxPoolSize);
    }

    [Test]
    public async Task WithDedicatedPoolIdentity_OverridesFixtureMaxPoolSize()
    {
        const string input =
            "Host=localhost;Port=5432;Username=app;Password=pw;Database=octocon;Maximum Pool Size=5";

        var output = SecretsPreBuildLoader.WithDedicatedPoolIdentity(input);

        var parsed = new NpgsqlConnectionStringBuilder(output);
        await Assert.That(parsed.MaxPoolSize).IsEqualTo(SecretsPreBuildLoader.LoaderMaxPoolSize);
    }

    // An earlier iteration forced Pooling=false which pushed the exhaustion problem
    // down to Postgres's max_connections; must stay on.
    [Test]
    public async Task WithDedicatedPoolIdentity_KeepsPoolingEnabled()
    {
        const string input =
            "Host=localhost;Port=5432;Username=app;Password=pw;Database=octocon;Pooling=false";

        var output = SecretsPreBuildLoader.WithDedicatedPoolIdentity(input);

        var parsed = new NpgsqlConnectionStringBuilder(output);
        await Assert.That(parsed.Pooling).IsTrue();
    }

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
