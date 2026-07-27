namespace Interfold.Shared.Api.Uploads;

public sealed record AvatarUploadPayload(Stream? Stream, bool EmptyFilePart = false);
