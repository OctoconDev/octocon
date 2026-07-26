using Interfold.Shared.Contracts.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Polly;

namespace Interfold.ServiceDefaults;

public static class Extensions
{
    private const string OtlpEndpointEnvName = "OTEL_EXPORTER_OTLP_ENDPOINT";

    // Applied by ConfigureResilienceForGetOnly. Sampling duration satisfies the
    // framework constraint SamplingDuration >= 2 * AttemptTimeout.
    private static readonly TimeSpan ResilienceAttemptTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ResilienceTotalRequestTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CircuitBreakerSamplingDuration = TimeSpan.FromMinutes(1);

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddStandardResilienceHandler().Configure(ConfigureResilienceForGetOnly);
            http.AddServiceDiscovery();
        });

        return builder;
    }

    /// <summary>Retries and the circuit breaker only fire for idempotent GET. Non-GET
    /// verbs are unsafe to replay after a per-attempt timeout (the origin can't tell the
    /// retry from a fresh call — see the Pi 4 SP-import triple-run in
    /// <c>scripts/diagnostics/raspi_dump.txt</c>). AttemptTimeout / TotalRequestTimeout
    /// still apply universally (no <c>ShouldHandle</c> hook), so keep them long enough for
    /// legitimate long-running mutations but bounded.</summary>
    public static void ConfigureResilienceForGetOnly(HttpStandardResilienceOptions options)
    {
        var defaultRetryShould = options.Retry.ShouldHandle;
        options.Retry.ShouldHandle = args =>
        {
            var request = args.Context.GetRequestMessage();
            if (request is null || request.Method != HttpMethod.Get)
            {
                return new ValueTask<bool>(false);
            }
            return defaultRetryShould(args);
        };

        var defaultBreakerShould = options.CircuitBreaker.ShouldHandle;
        options.CircuitBreaker.ShouldHandle = args =>
        {
            var request = args.Context.GetRequestMessage();
            if (request is null || request.Method != HttpMethod.Get)
            {
                return new ValueTask<bool>(false);
            }
            return defaultBreakerShould(args);
        };

        options.AttemptTimeout.Timeout = ResilienceAttemptTimeout;
        options.TotalRequestTimeout.Timeout = ResilienceTotalRequestTimeout;
        options.CircuitBreaker.SamplingDuration = CircuitBreakerSamplingDuration;
    }

    /// <summary>OTel logging, metrics, and tracing. Kept <c>public</c> to match the Aspire
    /// ServiceDefaults template — operators wrap their own composition around this call.</summary>
    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddAspNetCoreInstrumentation(tracing =>
                        tracing.Filter = context =>
                            !context.Request.Path.StartsWithSegments(HealthEndpoints.PathPrefix)
                    )
                    .AddHttpClientInstrumentation();
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration[OtlpEndpointEnvName]);

        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        return builder;
    }

    /// <summary>Built-in <c>self</c> liveness check tagged <see cref="HealthCheckTags.Live"/>.
    /// Kept <c>public</c> for the Aspire template convention.</summary>
    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            .AddCheck(HealthCheckNames.Self, () => HealthCheckResult.Healthy(), [HealthCheckTags.Live]);

        return builder;
    }

    public static IEndpointRouteBuilder MapDefaultEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks(HealthEndpoints.Live, new HealthCheckOptions
        {
            Predicate = _ => false
        }).AllowAnonymous().ShortCircuit();

        endpoints.MapHealthChecks(HealthEndpoints.Ready, new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(HealthCheckTags.Ready)
        }).AllowAnonymous().ShortCircuit();

        endpoints.MapHealthChecks(HealthEndpoints.Startup, new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(HealthCheckTags.Startup)
        }).AllowAnonymous().ShortCircuit();

        return endpoints;
    }
}
