using Interfold.Contracts.Configuration;
using Microsoft.Extensions.Options;

namespace Interfold.Auth.Api.DependencyInjection;

/// <summary>Hosted-service replacement for the ApplicationStarted lambda that used to sit
/// at the bottom of <c>Program.cs</c>. Subscribes to <see cref="IHostApplicationLifetime.ApplicationStarted"/>
/// so ValidateOnStart has patched [Required] secret fields before the log line fires.</summary>
internal sealed class AuthStartupLogHostedService : IHostedService
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IOptionsMonitor<AuthenticationConfiguration> _authMonitor;
    private readonly ILogger<AuthStartupLogHostedService> _logger;

    public AuthStartupLogHostedService(
        IHostApplicationLifetime lifetime,
        IOptionsMonitor<AuthenticationConfiguration> authMonitor,
        ILogger<AuthStartupLogHostedService> logger)
    {
        _lifetime = lifetime;
        _authMonitor = authMonitor;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _lifetime.ApplicationStarted.Register(() =>
        {
            var effectiveAuthConfig = _authMonitor.CurrentValue;
            var verificationKeyCount = effectiveAuthConfig.JwtEs256VerificationKeyPems?.Length ?? 0;
            _logger.LogInformation(
                "ES256 token issuance is enabled. Verification key count: {VerificationKeyCount}.",
                verificationKeyCount);
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
