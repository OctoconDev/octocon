namespace Interfold.IntegrationTests.Shared.TestServices;

/// <summary>Fixed Aspire resource-service / OTLP / Kestrel endpoints for concurrent AppHost
/// processes. <c>launchSettings.json</c> pins <c>22223</c>/<c>21246</c> for interactive
/// <c>aspire run</c>; every automated host must claim a distinct triple so parallel test
/// assemblies (bench launcher + Infrastructure legacy fixture + MultiNode) never collide.</summary>
public static class AspireProcessEndpoints
{
    public const string BenchResourceService = "https://127.0.0.1:23223";
    public const string BenchOtlp = "https://127.0.0.1:23246";
    public const string BenchAppUrls = "http://127.0.0.1:23277";

    public const string LegacySharedDbResourceService = "https://127.0.0.1:24223";
    public const string LegacySharedDbOtlp = "https://127.0.0.1:24246";
    public const string LegacySharedDbAppUrls = "http://127.0.0.1:24277";

    public const string MultiNodeResourceService = "https://127.0.0.1:25223";
    public const string MultiNodeOtlp = "https://127.0.0.1:25246";
    public const string MultiNodeAppUrls = "http://127.0.0.1:25277";

    /// <summary>Legacy <see cref="SharedDbFixture"/> host ports — isolated from the shared
    /// test-bench defaults (<c>14200</c>/<c>19042</c>/<c>19043</c>) so Infrastructure's
    /// opted-out Aspire host can run beside the bench without Docker bind conflicts.</summary>
    public const int LegacyPostgresPort = 24200;
    public const int LegacyScyllaPort = 29042;
    public const int LegacyCassandraPort = 29043;

    public static void ApplyTo(System.Diagnostics.ProcessStartInfo psi, string resourceService, string otlp, string appUrls)
    {
        // `--no-launch-profile` skips launchSettings' https applicationUrl; Aspire refuses
        // plain http unless this escape hatch is set (aka.ms/aspire/allowunsecuredtransport).
        psi.Environment["ASPIRE_ALLOW_UNSECURED_TRANSPORT"] = "true";
        psi.Environment["ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL"] = resourceService;
        psi.Environment["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"] = otlp;
        psi.Environment["ASPNETCORE_URLS"] = appUrls;
        psi.Environment["DOTNET_URLS"] = appUrls;
    }

    public static void ApplyToCurrentProcess(string resourceService, string otlp, string appUrls)
    {
        Environment.SetEnvironmentVariable("ASPIRE_ALLOW_UNSECURED_TRANSPORT", "true");
        Environment.SetEnvironmentVariable("ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL", resourceService);
        Environment.SetEnvironmentVariable("ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL", otlp);
        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", appUrls);
        Environment.SetEnvironmentVariable("DOTNET_URLS", appUrls);
    }
}
