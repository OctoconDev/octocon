using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Shared.Contracts.Models.Read;

// SettingsFieldReadModel stays on spine — pinned by Interfold.Alters.Domain.AlterFieldProjection.
// AvatarUrlUploadRequest stays on spine — bound by both AltersController and SettingsController;
// hoisting to either feature Contracts would introduce a cross-feature reference.
public sealed record SettingsFieldReadModel(
    FieldId Id,
    string Name,
    FieldType Type,
    VisibilityLevel SecurityLevel,
    bool Locked,
    int Index,
    DateTime? InsertedAt);

public sealed record AvatarUrlUploadRequest(
    AvatarUrl Url
);
