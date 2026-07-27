using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Shared.Contracts.Operations;

public static class SettingsActionEntityRefExtensions
{
    /// <summary>Invariant-violation ref for a failed settings command,
    /// e.g. <c>"settings:avatar_uploaded_failed"</c>.</summary>
    public static EntityRef ToFailedEntityRef(this SettingsAction action)
        => new($"settings:{action.ToWire()}_failed");
}
