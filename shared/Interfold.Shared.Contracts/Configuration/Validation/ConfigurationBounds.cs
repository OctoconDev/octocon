namespace Interfold.Shared.Contracts.Configuration.Validation;

/// <summary>Single source of truth for numeric bounds on operator-tunable OCTOCON_* env vars.
/// Referenced by both <c>ConfigPhase.Validate()</c> (bootstrapper) and <c>[Range]</c>
/// attributes on IOptions classes (ValidateOnStart) so a tune in one place lands in both.</summary>
public static class ConfigurationBounds
{
    public const int DbRetryAttemptsMin = 1;
    public const int DbRetryAttemptsMax = 100;

    public const int DbRetryInitialDelayMsMin = 1;
    public const int DbRetryInitialDelayMsMax = 60_000;

    public const int DbRetryMaxDelayMsMin = 1;
    public const int DbRetryMaxDelayMsMax = 600_000;

    public const int HydrationMaxConcurrencyMin = 1;
    public const int HydrationMaxConcurrencyMax = 1024;

    public const int SocketBatchBytesThresholdMin = 1;
    public const int SocketBatchBytesThresholdMax = 16 * 1024 * 1024;
}
