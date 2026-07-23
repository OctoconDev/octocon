namespace Interfold.Shared.Contracts;

/// <summary>
/// The API contract version stamped on every response via
/// <see cref="InterfoldHeaders.Contract"/>. Wire-frozen per version — clients pin against
/// the exact spelling; bump by adding a new constant and moving <see cref="Current"/>.
/// </summary>
public static class InterfoldContractVersions
{
    public const string V2026_03_V1 = "2026-03-v1";

    public const string Current = V2026_03_V1;
}
