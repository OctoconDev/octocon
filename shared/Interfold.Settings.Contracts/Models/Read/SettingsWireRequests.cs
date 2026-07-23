using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;

namespace Interfold.Contracts.Models.Read;

public sealed record SettingsUsernameRequest(
    Username Username
);

public sealed record SettingsDescriptionRequest(
    string Description
);

public sealed record SettingsPushTokenRequest(
    PushToken? Token = null
);

public sealed record SettingsEncryptionRequest(
    RecoveryCode RecoveryCode
);

public sealed record SettingsImportRequest(
    ImportToken Token,
    RecoveryCode? RecoveryCode = null
);

public sealed record SettingsCreateFieldRequest(
    string Name,
    FieldType? Type,
    VisibilityLevel? SecurityLevel,
    bool? Locked
);

public sealed record SettingsUpdateFieldRequest(
    string? Name,
    VisibilityLevel? SecurityLevel,
    bool? Locked
);

public sealed record SettingsRelocateFieldRequest(
    int Index
);
