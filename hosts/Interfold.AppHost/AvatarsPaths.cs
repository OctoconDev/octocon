using Interfold.Shared.Contracts.Configuration;

namespace Interfold.AppHost;

/// <summary>Filesystem wire contract for uploaded avatars. The bootstrapper stages the
/// host-side directory and fills the bind-mount source from
/// <c>BootstrapConfig.storage.avatarStorageRoot</c>; the emitted compose always mounts
/// that host path at <see cref="ContainerDir"/>.</summary>
internal static class AvatarsPaths
{
    /// <summary>Host-side avatars directory, relative to the emitted compose file.</summary>
    public const string HostDir = "../../data/avatars";

    /// <summary>In-container mount point for <see cref="HostDir"/>.</summary>
    public const string ContainerDir = ContainerMountPaths.InterfoldAvatars;
}
