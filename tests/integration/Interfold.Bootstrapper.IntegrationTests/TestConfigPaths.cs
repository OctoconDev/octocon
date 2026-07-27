namespace Interfold.Bootstrapper.IntegrationTests;

public static class TestConfigPaths
{
    public static string DefaultConfig => Path.Combine(AppContext.BaseDirectory, "Fixtures", "interfold.bootstrap.test.json");
    public static string TrustInstallConfig => Path.Combine(AppContext.BaseDirectory, "Fixtures", "interfold.bootstrap.test.trust-install.json");
    public static string CassandraConfig => Path.Combine(AppContext.BaseDirectory, "Fixtures", "interfold.bootstrap.test.cassandra.json");
    public static string BadImageConfig => Path.Combine(AppContext.BaseDirectory, "Fixtures", "interfold.bootstrap.test.bad-image.json");
    public static string WebHttpOnlyConfig => Path.Combine(AppContext.BaseDirectory, "Fixtures", "interfold.bootstrap.test.web-http-only.json");
    public static string MdnsGateConfig => Path.Combine(AppContext.BaseDirectory, "Fixtures", "interfold.bootstrap.test.mdns-gate.json");
    public static string WebHttpsConfig => Path.Combine(AppContext.BaseDirectory, "Fixtures", "interfold.bootstrap.test.web-https.json");
}

