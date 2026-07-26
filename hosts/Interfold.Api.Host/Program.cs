using Interfold.Alters.Api.DependencyInjection;
using Interfold.Api.Host.Services.Secrets;
using Interfold.Auth.Api.DependencyInjection;
using Interfold.Friendships.Api.DependencyInjection;
using Interfold.Fronting.Api.DependencyInjection;
using Interfold.Infrastructure.DependencyInjection;
using Interfold.Infrastructure.InMemory;
using Interfold.Infrastructure.Postgres;
using Interfold.Infrastructure.Scylla;
using Interfold.Journals.Api.DependencyInjection;
using Interfold.Ops.Api.DependencyInjection;
using Interfold.Ops.Api.Helpers;
using Interfold.Polls.Api.DependencyInjection;
using Interfold.ServiceDefaults;
using Interfold.Settings.Api.DependencyInjection;
using Interfold.Shared.Api.DependencyInjection;
using Interfold.Shared.Api.Helpers;
using Interfold.Shared.Api.Middleware;
using Interfold.Shared.Api.Services;
using Interfold.Shared.Api.Services.Secrets;
using Interfold.Shared.Api.Swagger;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Socket.Api.DependencyInjection;
using Interfold.Systems.Api.DependencyInjection;
using Interfold.Tags.Api.DependencyInjection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;

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
builder.Services.AddInterfoldOptions();

// Auth feature module owns AuthenticationConfiguration binding + PostConfigure + JWT bearer
// + OAuth challenge schemes + the four auth handler singletons + the ES256 startup log.
// Must run BEFORE AddInterfoldPersistence — persistence ValidateOnStart reads
// AuthenticationConfiguration.EncryptionPepper.
builder.Services.AddAuthModule(builder.Configuration);

// Alters feature module owns the three alter command-handler singletons.
// Order-independent — no options binding, no config reads.
builder.Services.AddAltersModule();

// Friendships feature module owns the six friendship command-handler singletons.
// Order-independent — no options binding, no config reads.
builder.Services.AddFriendshipsModule();

// Fronting feature module owns the seven fronting command-handler singletons.
// Order-independent — no options binding, no config reads.
builder.Services.AddFrontingModule();

// Journals feature module owns the twelve journal command-handler singletons
// (seven global + five alter). Order-independent — no options binding, no config reads.
builder.Services.AddJournalsModule();

// Tags feature module owns the seven tag command-handler singletons.
// Order-independent — no options binding, no config reads.
builder.Services.AddTagsModule();

// Polls feature module owns the three poll command-handler singletons.
// Order-independent — no options binding, no config reads.
builder.Services.AddPollsModule();

// Settings feature module owns twenty-two settings/encryption/avatar/import/field
// command-handler singletons, the two IImportJobRunner implementations + the
// ImportJobBackgroundService drain loop, the SimplyPlural HttpClient + import service,
// and the two Firebase IPostConfigureOptions bindings. Must run BEFORE
// AddInterfoldPersistence when the runtime depends on the FirebaseClient/Fcm secrets
// snapshot — the PostConfigure patchers read from ISecretsSnapshot registered further
// down, and the ordering matches AddAuthModule's rationale above.
builder.Services.AddSettingsModule();

// Socket feature module owns the Phoenix-protocol WebSocket transport, per-feature socket
// event handlers, SocketJoinRateLimiter, the LoopbackHttpClient named HttpClient, and the
// /api/socket/websocket route wiring (MapSocketModule below).
builder.Services.AddSocketModule();

// Systems feature module is a pure read facade (no owned handlers, no .Domain project);
// AddSystemsModule is a no-op today and exists for wiring symmetry with the other modules.
builder.Services.AddSystemsModule();

// Ops feature module hosts NodeRoleController + TrustController; no owned handlers,
// no .Domain project, no .Contracts project (nothing cross-module to share).
// AddOpsModule is a no-op today and exists for wiring symmetry with the other modules.
builder.Services.AddOpsModule();

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
// The two IPostConfigureOptions bindings that patched FirebaseClientConfiguration +
// FcmConfiguration from this snapshot moved into AddSettingsModule with the Firebase +
// FCM types they patch.
builder.Services.AddSingleton<ISecretsSnapshot>(secretsSnapshot);
builder.Services.AddSingleton(secretsSnapshot);

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

// HttpLoggingHandler + AddSimplyPluralImport + the two IImportJobRunner singletons +
// ImportJobBackgroundService all moved into AddSettingsModule (Phase-3 Settings slice).
// SocketJoinRateLimiter + LoopbackHttpClient named client both moved into AddSocketModule
// (Phase-3 Socket slice #10) — see apis/Interfold.Socket.Api/DependencyInjection.

// Cross-cutting authorization policy applies to every module's controllers, so it stays on
// the composition host rather than moving into AddAuthModule.
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

// AddSharedApi configures JsonOptions (snake_case + UTC), inserts UnixSecondsModelBinderProvider,
// wires the ErrorResponse InvalidModelStateResponseFactory, and registers the ExceptionHandler.
builder.Services.AddSharedApi();
builder.Services.AddControllers();

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

var app = builder.Build();

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

app.UseMiddleware<AuthTokenRevocationMiddleware>();

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

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    // /.well-known stays plain HTTP so clients can fetch the root CA before trusting HTTPS.
    app.UseWhen(
        static ctx => !ctx.Request.Path.StartsWithSegments("/.well-known"),
        static branch => branch.UseHttpsRedirection());
}
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

app.MapSocketModule();
app.MapControllers();

app.Run();
return 0;