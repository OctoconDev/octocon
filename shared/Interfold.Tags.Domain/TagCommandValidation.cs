using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Operations;

namespace Interfold.Shared.Domain.Tags;

internal static class TagCommandValidation
{
    public static EntityRef? GetNameValidationError(string? name)
    {
        if (name is not null && name.Length > 50)
            return EntityRefs.TagNameTooLong;

        return null;
    }

    public static bool HasNoMutableFields(UpdateTagCommand payload)
        => payload.Name is null
           && payload.Color is null
           && payload.Description is null
           && payload.SecurityLevel is null;
}
