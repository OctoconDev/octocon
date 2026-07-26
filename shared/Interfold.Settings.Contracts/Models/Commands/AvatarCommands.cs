using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Settings.Contracts.Models.Commands;

public sealed record UploadAvatarCommand(AvatarUrl AvatarUrl, AvatarSource Source);

public sealed record DeleteAvatarCommand();
