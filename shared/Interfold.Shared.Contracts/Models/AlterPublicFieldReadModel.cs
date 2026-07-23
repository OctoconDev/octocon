using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Shared.Contracts.Models;

// The Id here is the settings field definition's FieldId — alter field values are
// (field definition id → value) pairs joined against SettingsFieldReadModel.Id.
// Stays on the spine (rather than moving with BareAlter/AlterReadModel to
// Interfold.Alters.Contracts) because Interfold.Friendships.Contracts binds
// IReadOnlyList<AlterPublicFieldReadModel> on FriendFrontingAlterReadModel; hoisting
// this DTO to a feature Contracts would form a Friendships.Contracts -> Alters.Contracts
// cross-feature reference.
public sealed record AlterPublicFieldReadModel(FieldId Id, string Name, FieldType Type, string? Value);
