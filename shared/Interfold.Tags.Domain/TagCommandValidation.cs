using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Operations;
using Interfold.Tags.Contracts.Models.Commands;

namespace Interfold.Tags.Domain;

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
