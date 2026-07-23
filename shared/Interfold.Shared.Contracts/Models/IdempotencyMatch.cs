namespace Interfold.Shared.Contracts.Models;

public sealed record IdempotencyMatch(string PayloadHash, string OutcomeHash, string? OutcomePayload);