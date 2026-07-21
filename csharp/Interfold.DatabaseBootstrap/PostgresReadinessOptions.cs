namespace Interfold.DatabaseBootstrap;

public record PostgresReadinessOptions(
    TimeSpan Timeout,
    int RequiredConsecutiveSuccesses = 3,
    string? RunAsRole = null
);
