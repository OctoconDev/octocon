using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;

namespace Interfold.Contracts.Models.Commands;

public sealed record UploadAvatarCommand(AvatarUrl AvatarUrl, AvatarSource Source);

public sealed record DeleteAvatarCommand();
