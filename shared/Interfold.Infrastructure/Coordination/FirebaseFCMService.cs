using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Secrets;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using Interfold.Contracts;

namespace Interfold.Infrastructure.Coordination;

/// <summary>Real FCM v1 sender for the fronting-changed push flow. DI wires this only
/// when the primary role is active and <c>fcm:service_account_json</c> is present;
/// otherwise <see cref="NullFCMService"/> takes over.</summary>
public sealed class FirebaseFCMService : IFCMService, IDisposable
{
    // Process-local registry key for FirebaseApp.Create/GetInstance.
    private const string FirebaseAppName = "interfold-fcm";

    // FCM v1 multicast cap: https://firebase.google.com/docs/reference/fcm/rest/v1/projects.messages/send
    private const int FcmMulticastMax = 500;

    private readonly IOptions<FcmConfiguration> _options;
    private readonly INotificationTokenRepository _tokens;
    private readonly IAccountRepository _accounts;
    private readonly IAlterRepository _alters;
    private readonly ILogger<FirebaseFCMService> _logger;
    private readonly ResiliencePipeline _resilience;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private FirebaseMessaging? _messaging;
    private FirebaseApp? _app;

    public FirebaseFCMService(
        IOptions<FcmConfiguration> options,
        INotificationTokenRepository tokens,
        IAccountRepository accounts,
        IAlterRepository alters,
        ILogger<FirebaseFCMService> logger)
    {
        _options = options;
        _tokens = tokens;
        _accounts = accounts;
        _alters = alters;
        _logger = logger;
        _resilience = BuildResiliencePipeline(logger);
    }

    public async Task NotifyFrontingChangedAsync(
        SystemId systemId,
        IReadOnlyList<AlterId> currentAlterIds,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(systemId.Value))
            return;

        var friendGroups = await _tokens.ListTokensForFriendsOfAsync(systemId, cancellationToken).ConfigureAwait(false);
        if (friendGroups.Count == 0)
        {
            _logger.LogDebug("[fcm] system={SystemId} has no friend tokens to notify.", systemId);
            return;
        }

        var messaging = await EnsureMessagingAsync(cancellationToken).ConfigureAwait(false);
        if (messaging is null)
            return;

        var profile = await _accounts.GetPublicProfileAsync(systemId, cancellationToken).ConfigureAwait(false);
        var frontingDisplayName = profile?.Username is { } username && !string.IsNullOrWhiteSpace(username.Value)
            ? username.Value
            : "A friend";

        // Relative path — each client resolves against its own origin.
        var deepLink = $"/system/{Uri.EscapeDataString(systemId.Value)}";
        var frontEnded = currentAlterIds.Count == 0;
        var currentAlterSet = frontEnded ? null : new HashSet<AlterId>(currentAlterIds);

        foreach (var friendGroup in friendGroups)
        {
            IReadOnlyList<AlterId> visibleIds;
            string body;
            if (frontEnded)
            {
                visibleIds = Array.Empty<AlterId>();
                body = "No one is fronting";
            }
            else
            {
                var guarded = await _alters
                    .ListGuardedAsync(systemId, friendGroup.FriendSystemId, cancellationToken)
                    .ConfigureAwait(false);
                var visibleAlters = guarded
                    .Where(a => currentAlterSet!.Contains(a.Id))
                    .ToArray();

                if (visibleAlters.Length == 0)
                {
                    // Skip rather than leak "something changed" via an empty payload.
                    _logger.LogDebug(
                        "[fcm] Skipping friend={FriendSystemId} for system={SystemId}: no visible fronting alters.",
                        friendGroup.FriendSystemId, systemId);
                    continue;
                }

                visibleIds = visibleAlters.Select(a => a.Id).ToArray();
                body = string.Join(", ", visibleAlters.Select(a => a.Name));
            }

            var alterCsv = string.Join(",", visibleIds);
            var data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [FcmPayloadKeys.Type] = FcmNotificationTypes.FrontingChanged,
                [FcmPayloadKeys.SystemId] = systemId.Value,
                [FcmPayloadKeys.AlterIds] = alterCsv,
                [FcmPayloadKeys.DeepLink] = deepLink,
            };

            foreach (var chunk in friendGroup.Tokens.Chunk(FcmMulticastMax))
            {
                var message = new MulticastMessage
                {
                    Tokens = chunk.Select(t => t.Value).ToArray(),
                    Notification = new Notification
                    {
                        Title = $"Front update: {frontingDisplayName}",
                        Body = body,
                    },
                    Data = data,
                    Webpush = new WebpushConfig
                    {
                        FcmOptions = new WebpushFcmOptions { Link = deepLink },
                    },
                };

                BatchResponse? response;
                try
                {
                    response = await _resilience.ExecuteAsync(
                        async ct => await messaging.SendEachForMulticastAsync(message, ct).ConfigureAwait(false),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "[fcm] Multicast send failed for system={SystemId} friend={FriendSystemId} tokens={TokenCount}",
                        systemId, friendGroup.FriendSystemId, chunk.Length);
                    continue;
                }

                await PruneInvalidTokensAsync(chunk, response, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // Prune tokens flagged as permanently invalid (Unregistered / InvalidArgument); other
    // failures are logged and left in place so the next flush retries.
    private async Task PruneInvalidTokensAsync(
        IReadOnlyList<PushToken> tokens,
        BatchResponse response,
        CancellationToken cancellationToken)
    {
        if (response.FailureCount == 0)
            return;

        for (var i = 0; i < response.Responses.Count; i++)
        {
            var single = response.Responses[i];
            if (single.IsSuccess) continue;

            var token = tokens[i];

            if (single.Exception is FirebaseMessagingException fmEx &&
                (fmEx.MessagingErrorCode == MessagingErrorCode.Unregistered ||
                 fmEx.MessagingErrorCode == MessagingErrorCode.InvalidArgument))
            {
                try
                {
                    await _tokens.RemoveAsync(token, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation(
                        "[fcm] Pruned invalid token ({Code}). token_prefix={TokenPrefix}",
                        fmEx.MessagingErrorCode, TokenPrefix(token.Value));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex,
                        "[fcm] Failed to prune invalid token. token_prefix={TokenPrefix}",
                        TokenPrefix(token.Value));
                }
            }
            else
            {
                _logger.LogWarning(single.Exception,
                    "[fcm] Per-token delivery failure. token_prefix={TokenPrefix} code={Code}",
                    TokenPrefix(token.Value),
                    (single.Exception as FirebaseMessagingException)?.MessagingErrorCode);
            }
        }
    }

    private static string TokenPrefix(string token) =>
        string.IsNullOrEmpty(token) ? "***"
            : token.Length <= 6 ? "***"
            : token[..6];

    // FirebaseApp.Create throws on duplicate name — gate first-use and treat
    // "already exists" as a race we lost.
    private async Task<FirebaseMessaging?> EnsureMessagingAsync(CancellationToken ct)
    {
        var existing = Volatile.Read(ref _messaging);
        if (existing is not null) return existing;

        await _initGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_messaging is not null) return _messaging;

            var serviceAccountJson = _options.Value.ServiceAccountJson;
            if (string.IsNullOrWhiteSpace(serviceAccountJson))
            {
                // Defensive: the snapshot is startup-populated and shouldn't change mid-process.
                _logger.LogError(
                    "[fcm] {Key} disappeared between DI-graph build and first send. " +
                    "Restart the API after re-seeding the row, or drop it and let the factory pick NullFCMService.",
                    SecretsStoreKeys.FcmServiceAccountJson);
                return null;
            }

            var options = new AppOptions
            {
                Credential = CredentialFactory
                    .FromJson<ServiceAccountCredential>(serviceAccountJson)
                    .ToGoogleCredential(),
            };

            try
            {
                _app = FirebaseApp.Create(options, FirebaseAppName);
            }
            catch (ArgumentException)
            {
                _app = FirebaseApp.GetInstance(FirebaseAppName);
            }

            _messaging = FirebaseMessaging.GetMessaging(_app);
            _logger.LogInformation("[fcm] Firebase Admin initialised for FCM v1 send (app={AppName}).", FirebaseAppName);
            return _messaging;
        }
        finally
        {
            _initGate.Release();
        }
    }

    // Hand-rolled — FirebaseAdmin owns its HttpClient so Microsoft.Extensions.Http.Resilience
    // can't wrap it. Retries only the two transient FCM codes + transport failures.
    private static ResiliencePipeline BuildResiliencePipeline(ILogger logger) =>
        new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<FirebaseMessagingException>(ex =>
                        ex.MessagingErrorCode == MessagingErrorCode.Unavailable ||
                        ex.MessagingErrorCode == MessagingErrorCode.Internal)
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>(),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromMilliseconds(500),
                MaxDelay = TimeSpan.FromSeconds(5),
                OnRetry = args =>
                {
                    logger.LogWarning(args.Outcome.Exception,
                        "[fcm] Transient send failure, retrying attempt {Attempt} in {Delay}ms.",
                        args.AttemptNumber + 1, args.RetryDelay.TotalMilliseconds);
                    return ValueTask.CompletedTask;
                },
            })
            .Build();

    public void Dispose()
    {
        _initGate.Dispose();
        try
        {
            _app?.Delete();
        }
        catch
        {
            // Double-dispose race — harmless at shutdown.
        }
    }
}
