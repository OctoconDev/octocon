using Interfold.Contracts.Ids;

namespace Interfold.IntegrationTests.TestServices;

/// <summary>
/// Test-side helpers for minting deterministic-shape-but-unique identifiers. Consolidates
/// the scattered <c>$"{prefix}-{Guid.NewGuid():N}"[..N]</c> shape that was open-coded in
/// nearly every integration test — each site picked its own length cap (14, 16, 18, 20, 24,
/// 32), which made drift hard to catch. This helper keeps the length explicit at the call
/// site while sharing the prefix + GUID layout.
///
/// <para>
/// Length note: SystemId's on-wire cap is 32 characters (see
/// <see cref="SystemId"/> validation). The default <c>maxLen: 32</c> matches that; call
/// sites that need a shorter id (e.g. avatar tests that want a stable short principal for
/// filename readability) pass their own length explicitly, so the intent stays visible
/// where it matters.
/// </para>
/// </summary>
internal static class TestIds
{
    /// <summary>
    /// Build a raw system-id string of the form <c>"{prefix}-{Guid:N}"</c>, truncated to
    /// <paramref name="maxLen"/> characters. The GUID guarantees uniqueness; the prefix
    /// keeps leaked rows greppable back to the test that produced them.
    /// </summary>
    public static string NewSystemId(string prefix, int maxLen = 32)
    {
        var raw = $"{prefix}-{Guid.NewGuid():N}";
        return raw.Length <= maxLen ? raw : raw[..maxLen];
    }

    /// <summary>
    /// Convenience overload returning a strongly-typed <see cref="SystemId"/>. Prefer this
    /// at call sites that immediately wrap the raw string.
    /// </summary>
    public static SystemId NewTypedSystemId(string prefix, int maxLen = 32)
        => new(NewSystemId(prefix, maxLen));
}
