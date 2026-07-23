using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Operations;

namespace Interfold.Domain.Tags;

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
