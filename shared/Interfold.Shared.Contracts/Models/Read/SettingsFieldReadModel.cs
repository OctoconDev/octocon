using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Shared.Contracts.Models.Read;

// SettingsFieldReadModel stays on spine — pinned by Interfold.Alters.Domain.AlterFieldProjection.
// Eight settings-scoped wire request records migrated to
// Interfold.Settings.Contracts/Models/Read/SettingsWireRequests.cs (Phase-3 Settings slice).
// AvatarUrlUploadRequest + AvatarUploadPayload stay on spine (in-slice deviation from the
// plan): the first is bound by both AltersController and SettingsController, the second
// is constructed inside InterfoldControllerBase.ResolveMultipartUploadAsync
// (Interfold.Shared.Api) — moving either would introduce Api.Shared → Settings.Contracts
// or Alters.Api → Settings.Contracts arrows that the "pinned by cross-feature binding"
// rule in the plan's section 1b already tells us to avoid.
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

public sealed record AvatarUploadPayload(Stream? Stream, bool EmptyFilePart = false);
