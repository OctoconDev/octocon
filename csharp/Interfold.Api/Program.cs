using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Interfold.Api;
using Interfold.Api.Auth;
using Interfold.Api.Helpers;
using Interfold.Api.Middleware;
using Interfold.Api.ModelBinding;
using Interfold.Api.Models;
using Interfold.Api.Services;
using Interfold.Api.Services.Http;
using Interfold.Api.Services.ImportJobs;
using Interfold.Api.Services.Secrets;
using Interfold.Api.Socket;
using Interfold.Api.Swagger;
using Interfold.Api.SimplyPlural;
using Interfold.Contracts;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Ids;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.ImportJobs;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Infrastructure.DependencyInjection;
using Interfold.Infrastructure.InMemory;
using Interfold.Infrastructure.Postgres;
using Interfold.Infrastructure.Scylla;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// --- Aspire ServiceDefaults (OTel, resilience, service discovery) ---
builder.AddServiceDefaults();

// Fetches every internal.secrets row the API's PostConfigure patchers need before host build.
var secretsSnapshot = SecretsPreBuildLoader.Load(builder.Configuration);

ScyllaServiceCollectionExtensions.Register();
InMemoryServiceCollectionExtensions.Register();
PostgresServiceCollectionExtensions.Register();

// Register typed options BEFORE the startup snapshots below so tests can override them
// via the FactoryConfigurationProvider without a bespoke Bind*() helper.
IOptionsMonitor<AuthenticationConfiguration>? authOptionsMonitor = null;
builder.Services.AddInterfoldOptions();

// AuthenticationConfiguration is deliberately absent from this probe: its patchers pull
// from the pre-Build secrets snapshot; probing would trip [Required] before the patches
// land. ASP0000 is intentional — the alternative is hand-maintained Bind*() helpers that
// reintroduce the drift the options pipeline exists to eliminate.
// The probe SP swaps in a non-owning IConfiguration proxy so disposing it doesn't dispose
// the shared ConfigurationManager (which WebApplicationFactory<T> reuses in tests).
PersistenceConfiguration persistenceConfig;
CorsOptions corsOptions;
ClusterConfiguration clusterConfig;
var probeServices = StartupProbeConfigurationProxy.SwapInto(builder.Services, builder.Configuration);
#pragma warning disable ASP0000
using (var probeProvider = probeServices.BuildServiceProvider(validateScopes: false))
#pragma warning restore ASP0000
{
    persistenceConfig = probeProvider.GetRequiredService<IOptions<PersistenceConfiguration>>().Value;
    corsOptions = probeProvider.GetRequiredService<IOptions<CorsOptions>>().Value;
    clusterConfig = probeProvider.GetRequiredService<IOptions<ClusterConfiguration>>().Value;
}

// Allow-list from OCTOCON_CORS_ALLOWED_ORIGINS; blank = allow-any (dev-only).
var configuredCorsOrigins = corsOptions.AllowedOrigins.ToArray();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (configuredCorsOrigins.Length > 0)
        {
            policy.WithOrigins(configuredCorsOrigins)
                .AllowAnyHeader()
                .AllowAnyMethod();
            return;
        }

        policy.SetIsOriginAllowed(_ => true)
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

// Register BEFORE persistence so the pre-Build secrets snapshot is visible when migration
// services and ValidationHostedService (via [Required] + ValidateOnStart) read secrets.
builder.Services.AddSingleton<ISecretsSnapshot>(secretsSnapshot);
builder.Services.AddSingleton(secretsSnapshot);
builder.Services.AddSingleton<IPostConfigureOptions<AuthenticationConfiguration>, AuthenticationSecretsPostConfigure>();
builder.Services.AddSingleton<IPostConfigureOptions<FirebaseClientConfiguration>, FirebaseClientSecretsPostConfigure>();
builder.Services.AddSingleton<IPostConfigureOptions<FcmConfiguration>, FcmSecretsPostConfigure>();

builder.Services.AddInterfoldCluster(clusterConfig.NodeGroup);
builder.Services.AddInterfoldPersistence(persistenceConfig.Mode, persistenceConfig);
builder.Services.AddInterfoldDomainHandlers();

// Readiness fails fast; startup allows longer for cold-start migrations; naming convention
// lives in HealthCheckExtensions.AddReadyAndStartup.
var healthChecks = builder.Services.AddHealthChecks();

if (persistenceConfig.Mode == PersistenceMode.ScyllaPostgres)
{
    healthChecks
        .AddReadyAndStartup<ScyllaHealthChecker>("scylla")
        .AddReadyAndStartup<PostgresHealthChecker>("postgres");
}
builder.Services.AddSingleton<IAvatarStorage, LocalAvatarStorage>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SocketJoinRateLimiter>();

builder.Services.AddTransient<HttpLoggingHandler>();

builder.Services.AddHttpClient<GoogleOAuthService>();
builder.Services.AddHttpClient<DiscordOAuthService>();
builder.Services.AddHttpClient<AppleOAuthService>();
builder.Services.AddSimplyPluralImport();

// Async-import queue lives in AddInterfoldCluster; this hosts the drain loop.
builder.Services.AddSingleton<IImportJobRunner, PkImportJobRunner>();
builder.Services.AddHostedService<ImportJobBackgroundService>();

// Loopback-only named client for the WebSocket relay's self-call (see LoopbackHttpClient).
// AllowAutoRedirect off — relay targets HTTPS directly, any redirect is a bug.
builder.Services.AddHttpClient(LoopbackHttpClient.Name)
    .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        SslOptions = new System.Net.Security.SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, _, _, _) => true,
        },
    });

// JWTs are self-issued post-OAuth; no external OIDC issuer to validate, so aud + lifetime only.
// Read straight off builder.Configuration to avoid triggering .ValidateOnStart() before
// AuthenticationSecretsPostConfigure patches in the [Required] secret fields.
var jwtAudienceAtBoot = builder.Configuration[OctoconEnvKeys.JwtAudience] ?? "octocon";
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            NameClaimType = "sub",
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidAudience = jwtAudienceAtBoot,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
            ValidateIssuerSigningKey = false,
            RequireSignedTokens = true,
            // authOptionsMonitor is assigned right after app.Build(); this closure only fires
            // at request time so null-forgive is safe.
            SignatureValidator = (token, _) =>
                ValidateJwtTokenSignatureForBearer(
                    token,
                    authOptionsMonitor!.CurrentValue)
        };
    });

// Same builder.Configuration read as jwtAudienceAtBoot for the same PostConfigure reason.
builder.Services.AddInterfoldAuthChallengeSchemes(
    discordOAuthClientId: builder.Configuration[OctoconEnvKeys.DiscordOAuthClientId],
    googleOAuthClientId: builder.Configuration[OctoconEnvKeys.GoogleOAuthClientId],
    appleOAuthClientId: builder.Configuration[OctoconEnvKeys.AppleOAuthClientId]);

builder.Services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build());

builder.Services.AddHsts(options =>
{
    options.ExcludedHosts.Add("api.octocon.app");
});

builder.Services.AddHttpsRedirection(options => 
{
    options.RedirectStatusCode = 308; // Permanent Redirect
});

// --- OpenTelemetry ---
// The InterfoldMetrics custom meter is registered on top of ServiceDefaults OTel config.
builder.Services
    .AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics.AddMeter(InterfoldMetrics.MeterName);
    });

// UnixSecondsModelBinderProvider at position 0 takes precedence over MVC's SimpleType /
// ComplexObject providers for UnixSeconds parameters.
builder.Services.AddControllers(mvcOptions =>
    {
        mvcOptions.ModelBinderProviders.Insert(0, new UnixSecondsModelBinderProvider());
    })
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        options.JsonSerializerOptions.Converters.Add(new UtcDateTimeConverter());
        options.JsonSerializerOptions.Converters.Add(new UtcDateTimeOffsetConverter());
    });

// Route DataAnnotations 400s through ErrorResponse so the Kotlin client can decode them
// (the default ValidationProblemDetails isn't supported).
builder.Services.Configure<Microsoft.AspNetCore.Mvc.ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var firstBadField = context.ModelState
            .FirstOrDefault(kv => kv.Value?.Errors.Count > 0
                && !string.IsNullOrWhiteSpace(kv.Value.Errors[0].ErrorMessage));

        var firstError = firstBadField.Value?.Errors[0].ErrorMessage
            ?? "The request payload was invalid.";

        // Prefer the binder's stashed ErrorCode so invalid_end_anchor / invalid_anchor
        // survive verbatim without needing globally unique messages.
        var stashedCode = context.HttpContext.Items[
            UnixSecondsBindingAttribute.ItemsKey(firstBadField.Key ?? string.Empty)] as string;

        var code = stashedCode is not null
            ? new ErrorCode(stashedCode)
            : ValidationErrorCodeRegistry.LookupOrDefault(firstError);

        var body = new ErrorResponse(firstError, code, System.Net.HttpStatusCode.BadRequest);
        return new Microsoft.AspNetCore.Mvc.BadRequestObjectResult(body);
    };
});

// --- Swagger/OpenAPI ---
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Interfold API",
        Version = "v1",
        Description = $"Interfold API - Contract Version: {InterfoldContractVersions.Current}"
    });

    options.ResolveConflictingActions(apiDescriptions =>
    {
        var first = apiDescriptions.First();
        var route = first.RelativePath?.ToLowerInvariant();

        // Sentinelise route params so renaming (e.g. {id}→{alterId}) doesn't silently drop
        // routes off the allow-list and crash Swagger doc generation.
        var normalisedRoute = route is null
            ? null
            : System.Text.RegularExpressions.Regex.Replace(route, @"\{[^/{}]+\}", "{*}");

        // Only allow conflicts for avatar upload endpoints (multipart vs JSON siblings).
        var allowedConflicts = new[]
        {
            "api/settings/avatar",
            "api/systems/me/alters/{*}/avatar"
        };

        if (allowedConflicts.All(allowed => normalisedRoute?.Contains(allowed) != true))
        {
            var actionNames = string.Join(", ", apiDescriptions.Select(d => $"{d.ActionDescriptor.DisplayName}"));
            throw new NotSupportedException(
                $"Conflicting actions detected for route '{route}': {actionNames}. " +
                "If this is intentional, add the route to the allowedConflicts list in Program.cs.");
        }

        return first;
    });

    options.DocumentFilter<MultipleContentTypeOperationFilter>();
    options.DocumentFilter<HealthCheckDocumentFilter>();

    // Add JWT Bearer authentication to Swagger UI
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme. Enter 'Bearer' [space] and then your token in the text input below.",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

builder.Services.AddExceptionHandler<ExceptionHandler>();

var app = builder.Build();

// Stable handle for the JWT SignatureValidator closure; safe pre-request-time since
// CurrentValue isn't dereferenced yet.
authOptionsMonitor = app.Services.GetRequiredService<IOptionsMonitor<AuthenticationConfiguration>>();

// Log on ApplicationStarted so ValidateOnStart has patched [Required] secret fields first.
var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AuthStartup");
app.Lifetime.ApplicationStarted.Register(() =>
{
    var effectiveAuthConfig = authOptionsMonitor.CurrentValue;
    var verificationKeyCount = effectiveAuthConfig.JwtEs256VerificationKeyPems?.Length ?? 0;
    startupLogger.LogInformation(
        "ES256 token issuance is enabled. Verification key count: {VerificationKeyCount}.",
        verificationKeyCount);
});

app.UseExceptionHandler("/error");

// Buffer avatar multipart PUTs so source-validation can re-read Request.Body post-bind;
// scoped to the two avatar routes only.
app.Use(async (context, next) =>
{
    if (HttpMethods.IsPut(context.Request.Method)
        && context.Request.HasFormContentType)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (path.Equals("/api/settings/avatar", StringComparison.OrdinalIgnoreCase)
            || (path.StartsWith("/api/systems/me/alters/", StringComparison.OrdinalIgnoreCase)
                && path.EndsWith("/avatar", StringComparison.OrdinalIgnoreCase)))
        {
            context.Request.EnableBuffering();
        }
    }

    await next();
});

// Post-authentication JTI-revocation gate.
app.Use(async (context, next) =>
{
    if (context.User?.Identity?.IsAuthenticated == true)
    {
        if (context.User.FindFirst(JwtClaimNames.Jti)?.Value is { } jti && !string.IsNullOrWhiteSpace(jti))
        {
            var revocationRepository = context.RequestServices.GetRequiredService<IAuthTokenRevocationRepository>();
            var isTokenValid = await revocationRepository.ValidateTokenNotRevokedAsync(new Jti(jti), context.RequestAborted);
            
            if (!isTokenValid)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json";
                var error = new ErrorResponse("Token has been revoked.", ErrorCodes.TokenRevoked);
                var json = JsonSerializer.Serialize(error, JsonSerializerOptions.Web);
                await context.Response.WriteAsync(json, context.RequestAborted);
                return;
            }
        }
    }

    await next();
});

// Phase N: correlation ID propagation and structured request logging.
app.UseMiddleware<RequestCorrelationMiddleware>();

// --- Swagger UI ---
app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "Interfold API v1");
    options.RoutePrefix = "swagger";
});

app.Use(async (ctx, next) =>
{
    ctx.Response.OnStarting(() =>
    {
        ctx.Response.Headers[InterfoldHeaders.Contract] = InterfoldContractVersions.Current;
        return Task.CompletedTask;
    });
    await next();
});

app.UseHsts();
// /.well-known stays plain HTTP so clients can fetch the root CA before trusting HTTPS.
app.UseWhen(
    static ctx => !ctx.Request.Path.StartsWithSegments("/.well-known"),
    static branch => branch.UseHttpsRedirection());
app.UseCors();
app.UseAuthentication();
app.UseMiddleware<InterfoldPrincipalMiddleware>();
app.UseStaticFiles();

// Serve avatars per AvatarServingPolicy. Hand-rolled (not a second UseStaticFiles) so the
// serve-side and LocalAvatarStorage's URL-stamp side read the same IOptionsMonitor snapshot.
var avatarContentTypeProvider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
app.Use(async (context, next) =>
{
    var storageMonitor = context.RequestServices.GetRequiredService<IOptionsMonitor<StorageConfiguration>>();
    var current = storageMonitor.CurrentValue;
    var (shouldServe, physicalRoot, requestPath) = AvatarServingPolicy.Resolve(
        current.AvatarStorageRoot, current.AvatarPublicBase);

    if (!shouldServe
        || !context.Request.Path.StartsWithSegments(requestPath, StringComparison.Ordinal, out var remainingPath)
        || !(HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)))
    {
        await next();
        return;
    }

    var relative = remainingPath.Value?.TrimStart('/') ?? string.Empty;
    if (relative.Length == 0 || relative.Contains("..", StringComparison.Ordinal))
    {
        await next();
        return;
    }

    var rootFull = Path.GetFullPath(physicalRoot);
    var rootWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar)
        ? rootFull
        : rootFull + Path.DirectorySeparatorChar;
    var fullPath = Path.GetFullPath(Path.Combine(rootFull, relative));
    if (!fullPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
    {
        await next();
        return;
    }

    if (!File.Exists(fullPath))
    {
        await next();
        return;
    }

    if (!avatarContentTypeProvider.TryGetContentType(fullPath, out var contentType))
    {
        await next();
        return;
    }

    var fi = new FileInfo(fullPath);
    context.Response.ContentType = contentType;
    context.Response.ContentLength = fi.Length;
    // Safe long-term cache: LocalAvatarStorage embeds a fresh timestamp+Guid on every write.
    context.Response.Headers.CacheControl = "public, max-age=86400";
    if (HttpMethods.IsHead(context.Request.Method))
    {
        return;
    }
    await context.Response.SendFileAsync(fullPath, context.RequestAborted);
});

app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

app.UseAuthorization();

app.MapDefaultEndpoints();

app.MapMethods("/api/socket/websocket", ["GET", "CONNECT"], WebSocketHandler.HandleUserSocketAsync).AllowAnonymous();
app.MapControllers();

app.Run();
return 0;

static SecurityToken ValidateJwtTokenSignatureForBearer(
    string token,
    AuthenticationConfiguration config)
{
    return Interfold.Api.Auth.JwtEs256Validator.ValidateSignature(token, config.JwtEs256VerificationKeyPems ?? [], useJsonWebToken: true);
}
