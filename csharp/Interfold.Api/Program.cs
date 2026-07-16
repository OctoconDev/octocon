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

// Unified secrets snapshot: fetches every internal.secrets row the API's PostConfigure
// patchers need (auth, Firebase client, FCM) plus the leaf PFX password, all before the
// host builds. See SecretsPreBuildLoader for the Postgres-vs-InMemory branch + ordering
// rationale. The returned snapshot is registered as a singleton instance below.
var secretsSnapshot = SecretsPreBuildLoader.Load(builder.Configuration);

//Database connections which have been implemented
ScyllaServiceCollectionExtensions.Register();
InMemoryServiceCollectionExtensions.Register();
PostgresServiceCollectionExtensions.Register();

// --- Configuration ---
// Register every typed option (bound via AddInterfoldOptions) BEFORE we take any startup
// snapshots — the CORS + persistence + cluster wiring below all read one-shot values that
// must go through the IOptions pipeline so tests can override them via the
// FactoryConfigurationProvider without a bespoke Bind*() helper.
IOptionsMonitor<AuthenticationConfiguration>? authOptionsMonitor = null;
builder.Services.AddInterfoldOptions();

// Startup snapshots for the three purely-env-bound options. AuthenticationConfiguration is
// deliberately absent: it goes through the AuthenticationSecretsPostConfigure pipeline which
// pulls from an ISecretsSnapshot populated pre-Build by SecretsPreBuildLoader — probing it
// here would resolve validation before the secret-store-sourced fields are patched in and
// trip [Required] on the mandatory secret fields. The two probe consumers (JWT bearer
// ValidAudience and AddInterfoldAuthChallengeSchemes) read directly from builder.Configuration
// for the four env-bound values they need, below.
//
// ASP0000: BuildServiceProvider inside application code duplicates singleton graphs — that
// is the intended cost here. The alternative (hand-maintained Bind*(IConfiguration)
// helpers) reintroduces the drift that the options pipeline exists to eliminate.
//
// The probe SP is built off a CLONE of builder.Services in which the framework's factory
// IConfiguration registration ("services.AddSingleton(_ => appConfiguration)", intentionally
// set up so the SP owns configuration disposal) is swapped for a non-owning proxy. Without
// that swap, disposing the probe SP would also dispose the shared ConfigurationManager, and
// any subsequent ConfigureAppConfiguration callback — notably the one WebApplicationFactory<T>
// registers during integration tests — would throw ObjectDisposedException at builder.Build()
// when it tries to Add() a source to the disposed manager. See
// StartupProbeConfigurationProxy for the full rationale.
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

// Comma-separated allow-list from OCTOCON_CORS_ALLOWED_ORIGINS via IOptions<CorsOptions>;
// blank falls back to allow-any (dev-only — production stacks must set it explicitly).
// Trailing-slash trimming and de-dup live inside ApplyCors for parity with the ASP.NET Core
// CORS matcher.
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

// Registered BEFORE persistence services so the snapshot (already populated pre-Build by
// SecretsPreBuildLoader) is visible before migration services try to read admin creds /
// OAuth secrets out of IConfiguration, and before ValidationHostedService dereferences
// AuthenticationConfiguration / FirebaseClientConfiguration / FcmConfiguration to enforce
// [Required] via .ValidateOnStart().
builder.Services.AddSingleton<ISecretsSnapshot>(secretsSnapshot);
builder.Services.AddSingleton(secretsSnapshot);
builder.Services.AddSingleton<IPostConfigureOptions<AuthenticationConfiguration>, AuthenticationSecretsPostConfigure>();
builder.Services.AddSingleton<IPostConfigureOptions<FirebaseClientConfiguration>, FirebaseClientSecretsPostConfigure>();
builder.Services.AddSingleton<IPostConfigureOptions<FcmConfiguration>, FcmSecretsPostConfigure>();

// --- Dependency Injection ---
// The snapshots above already reflect the env-bound IOptions<T> values; passing them into
// the mode/role-scoped extension methods layers them onto the mode-registration lambdas
// (which capture PersistenceConfiguration synchronously) while every other consumer still
// resolves IOptions<PersistenceConfiguration> from the DI container.
builder.Services.AddInterfoldCluster(clusterConfig.NodeGroup);
builder.Services.AddInterfoldPersistence(persistenceConfig.Mode, persistenceConfig);
builder.Services.AddInterfoldDomainHandlers();

// --- Health Checks ---
// Readiness checks use a short timeout (5s) — fail fast if a dependency drops.
// Startup checks use a longer timeout (30s) — databases may still be initializing at boot.
var healthChecks = builder.Services.AddHealthChecks();

if (persistenceConfig.Mode == PersistenceMode.ScyllaPostgres)
{
    healthChecks.AddCheck<ScyllaHealthChecker>(
        "scylla-ready", tags: [HealthCheckTags.Ready], timeout: TimeSpan.FromSeconds(5));
    healthChecks.AddCheck<ScyllaHealthChecker>(
        "scylla-startup", tags: [HealthCheckTags.Startup], timeout: TimeSpan.FromSeconds(30));
    healthChecks.AddCheck<PostgresHealthChecker>(
        "postgres-ready", tags: [HealthCheckTags.Ready], timeout: TimeSpan.FromSeconds(5));
    healthChecks.AddCheck<PostgresHealthChecker>(
        "postgres-startup", tags: [HealthCheckTags.Startup], timeout: TimeSpan.FromSeconds(30));
}
builder.Services.AddSingleton<IAvatarStorage, LocalAvatarStorage>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SocketJoinRateLimiter>();

builder.Services.AddTransient<HttpLoggingHandler>();

// --- HTTP Client Factory (for OAuth token exchange, etc.) ---
builder.Services.AddHttpClient<GoogleOAuthService>();
builder.Services.AddHttpClient<DiscordOAuthService>();
builder.Services.AddHttpClient<AppleOAuthService>();
builder.Services.AddHttpClient(HttpClientNames.SimplyPlural).AddHttpMessageHandler<HttpLoggingHandler>();
builder.Services.AddSingleton<ISimplyPluralImportService, SimplyPluralImportService>();

// Async-import worker stack. The queue itself is registered in
// AddInterfoldCluster (it's a coordination primitive). Runners are per-kind and
// resolve their concrete importer dependency from this graph. The hosted service
// drains the queue, drives operation-row transitions, and publishes the
// sp_import_complete / pk_import_complete events that the existing socket pump
// relays to the WebSocket client.
builder.Services.AddSingleton<IImportJobRunner, SpImportJobRunner>();
builder.Services.AddSingleton<IImportJobRunner, PkImportJobRunner>();
builder.Services.AddHostedService<ImportJobBackgroundService>();

// Permissive-TLS named client for the WebSocket endpoint relay's self-call. The call
// site (WebSocketHandler.HandleEndpointProxyAsync + ResolveLoopbackBaseUri) guarantees
// a loopback destination; LoopbackHttpClient's XML doc covers why permissive validation
// is the right call there. AllowAutoRedirect off because the relay targets the HTTPS
// listener directly, so any redirect would be a bug to surface, not follow.
builder.Services.AddHttpClient(LoopbackHttpClient.Name)
    .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        SslOptions = new System.Net.Security.SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, _, _, _) => true,
        },
    });

// --- Auth ---
// JWTs are self-issued post-OAuth (the provider only identifies the user); no external OIDC
// authority to validate iss against, so issuer validation is off and we rely on aud + lifetime.
// TODO: Look into how we can make this better WITHOUT breaking existing clients
//
// ValidAudience and the OAuth client IDs are env-bound values that must be read at
// registration time to wire into the JWT handler / challenge schemes. We deliberately do
// not resolve IOptions<AuthenticationConfiguration>.Value here — that would trigger
// .ValidateOnStart() before AuthenticationSecretsPostConfigure patches in the [Required]
// secret fields (already populated in the snapshot pre-Build by SecretsPreBuildLoader).
// builder.Configuration is the same source ApplyAuthentication reads from, so this is
// byte-identical to the options-pipeline probe for the four public fields we still need at
// boot.
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
            ValidAudience = jwtAudienceAtBoot, //Has to be done at startup to wire into the JWT handler
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
            ValidateIssuerSigningKey = false,
            RequireSignedTokens = true,
            // authOptionsMonitor is assigned right after app.Build() and this closure only
            // fires at request time (long after startup completes and PostConfigure has
            // patched the ES256 verification key from the snapshot), so a null-forgive is
            // safe here.
            SignatureValidator = (token, _) =>
                ValidateJwtTokenSignatureForBearer(
                    token,
                    authOptionsMonitor!.CurrentValue)
        };
        // JTI revocation check is wired after app.Build() to access IAuthTokenRevocationRepository
    });

// OAuth challenge schemes are registered once at startup; only the client_id per provider is
// per-deployment, and each ClientId is env-bound (ApplyAuthentication:275/277/279). Reading
// builder.Configuration directly here mirrors that binding without materialising an
// AuthenticationConfiguration snapshot — same rationale as jwtAudienceAtBoot above.
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

// --- MVC ---
// The UnixSecondsModelBinderProvider is inserted at position 0 so it takes precedence
// over MVC's built-in SimpleType / ComplexObject providers for UnixSeconds parameters.
// Without the front-of-queue insert, MVC would try to shape UnixSeconds as a complex
// object (looking for a `Value` constructor arg on the query string) instead of using
// the string TryParse path our custom binder owns.
builder.Services.AddControllers(mvcOptions =>
    {
        mvcOptions.ModelBinderProviders.Insert(0, new UnixSecondsModelBinderProvider());
    })
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        // Ensure DateTime / DateTimeOffset are consistently emitted as UTC (single trailing 'Z')
        options.JsonSerializerOptions.Converters.Add(new UtcDateTimeConverter());
        options.JsonSerializerOptions.Converters.Add(new UtcDateTimeOffsetConverter());
    });

// Route DataAnnotations-driven 400s (e.g. `[ValidAlterId]` on request records) through
// the same `ErrorResponse` shape the rest of the API returns, using
// `ValidationErrorCodeRegistry` to preserve stable wire codes (`invalid_alter_id`,
// falling back to `bad_request`). Without this the framework default is
// `ValidationProblemDetails`, which the Kotlin client does not decode.
builder.Services.Configure<Microsoft.AspNetCore.Mvc.ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        // Prefer the first field with an error so we can pair its message with the
        // matching UnixSecondsBinding stash on HttpContext.Items. Falling back to a
        // synthetic entry keeps the payload shape stable when ModelState is empty
        // (defensive — the factory only runs when at least one error is present).
        var firstBadField = context.ModelState
            .FirstOrDefault(kv => kv.Value?.Errors.Count > 0
                && !string.IsNullOrWhiteSpace(kv.Value.Errors[0].ErrorMessage));

        var firstError = firstBadField.Value?.Errors[0].ErrorMessage
            ?? "The request payload was invalid.";

        // UnixSecondsModelBinder stashes the intended ErrorCode string on HttpContext.Items
        // under a well-known per-field key. Preferring it over the message-keyed registry
        // preserves the invalid_end_anchor / invalid_anchor wire codes verbatim without
        // requiring the human-readable message to be globally unique.
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

        // Normalise any route-parameter placeholder to a fixed sentinel so the allow-list is
        // token-agnostic. Otherwise renaming a route parameter (e.g. `{id}` → `{alterId}` on
        // AltersController.UploadAvatar*) silently pushes the route out of the allow-list and
        // Swagger throws NotSupportedException on every doc generation — which propagates as
        // an unhandled 500 through the ExceptionHandler pipeline on any request.
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

// Capture the monitor once so the JWT SignatureValidator closure has a stable handle. The
// closure only fires at request time (after startup completes and PostConfigure has run),
// so this assignment is safe even though the monitor's CurrentValue isn't dereferenced yet.
authOptionsMonitor = app.Services.GetRequiredService<IOptionsMonitor<AuthenticationConfiguration>>();

// Defer the ES256 verification-key log to ApplicationStarted so we don't dereference
// IOptionsMonitor<AuthenticationConfiguration>.CurrentValue between app.Build() and
// app.Run(). .ValidateOnStart() + [Required] on the secret fields means an early
// CurrentValue resolution would trip validation before the ValidateOnStart hosted service
// has run — the log fires after that hosted service completes, so the count reflects the
// fully-patched configuration (the snapshot itself is already populated pre-Build by
// SecretsPreBuildLoader, well before this point).
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

// Buffer avatar multipart PUTs so source-validation can re-read Request.Body after MVC
// model-binds the form. Scoped to the two avatar routes to keep the cost off everything else.
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

// JWT token revocation check middleware.
// This runs after authentication, checking if the authenticated token's JTI has been revoked.
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
                // Web options keep the historical lowercase member names ("error"/"code").
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

// X-Interfold-Contract response header on every response
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
// Carve /.well-known out of HTTPS-redirect so TrustController can serve the root CA over
// plain HTTP — clients can't trust HTTPS until they've fetched and installed that root.
app.UseWhen(
    static ctx => !ctx.Request.Path.StartsWithSegments("/.well-known"),
    static branch => branch.UseHttpsRedirection());
app.UseCors();
app.UseAuthentication();
app.UseMiddleware<InterfoldPrincipalMiddleware>();
app.UseStaticFiles();

// Serve avatars per AvatarServingPolicy (see its XML doc for the config matrix). Hand-rolled
// rather than a secondary UseStaticFiles because the policy reads IOptionsMonitor per request
// — LocalAvatarStorage stamps URLs from the same monitor, so write-side and read-side must
// agree on current values across config reloads. Unknown extensions fall through (matches
// StaticFileMiddleware's ServeUnknownFileTypes=false); the upload controller already gates
// content-type at write time.
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
    // Safe to cache long-term: LocalAvatarStorage embeds a timestamp+Guid in the filename,
    // so any overwrite produces a fresh URL.
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

// --- Health Check Endpoints (from ServiceDefaults) ---
app.MapDefaultEndpoints();

app.MapMethods("/api/socket/websocket", ["GET", "CONNECT"], WebSocketHandler.HandleUserSocketAsync).AllowAnonymous();
app.MapControllers();

app.Run();
return 0;

// ES256 JWT token validation using ECDSA P-256 public keys.
static SecurityToken ValidateJwtTokenSignatureForBearer(
    string token,
    AuthenticationConfiguration config)
{
    if (string.IsNullOrWhiteSpace(token))
    {
        throw new SecurityTokenInvalidSignatureException("Token is empty.");
    }

    var parts = token.Split('.');
    if (parts.Length != 3)
    {
        throw new SecurityTokenInvalidSignatureException("Token is not a valid JWS compact token.");
    }

    var headerJson = Encoding.UTF8.GetString(parts[0].Base64UrlDecode());
    var header = JsonSerializer.Deserialize<JwsHeader>(headerJson);
    if (header is null || string.IsNullOrWhiteSpace(header.Alg))
    {
        throw new SecurityTokenInvalidSignatureException("Missing JWT algorithm.");
    }

    var signingInput = Encoding.UTF8.GetBytes(parts[0] + "." + parts[1]);
    var signatureBytes = parts[2].Base64UrlDecode();

    // ES256 (ECDSA P-256 with SHA-256) validation
    if (!string.Equals(header.Alg, JwsHeader.Es256, StringComparison.Ordinal))
    {
        throw new SecurityTokenInvalidSignatureException("Only ES256 algorithm is supported.");
    }

    var pems = config.JwtEs256VerificationKeyPems ?? [];
    if (pems.Length == 0)
    {
        throw new SecurityTokenInvalidSignatureException("No ES256 verification keys configured.");
    }

    foreach (var rawPem in pems)
    {
        using var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportFromPem(NormalizePem(rawPem).AsSpan());
        }
        catch (CryptographicException)
        {
            continue;
        }

        if (ecdsa.VerifyData(
            signingInput,
            signatureBytes,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        {
            return new JsonWebToken(token);
        }
    }

    throw new SecurityTokenInvalidSignatureException("Invalid JWT signature: no verification key matched.");
}

static string NormalizePem(string pem)
{
    if (string.IsNullOrWhiteSpace(pem))
        return pem;

    // PEMs arrive from env vars / DB with both real and escaped line endings; collapse all
    // of them to '\n' so ECDsa.ImportFromPem accepts the result.
    var normalized = pem
        .Replace(@"\r\n", "\n", StringComparison.Ordinal)
        .Replace("\\r", "\n", StringComparison.Ordinal)
        .Replace("\\n", "\n", StringComparison.Ordinal)
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace("\r", "\n", StringComparison.Ordinal);

    return normalized;
}
