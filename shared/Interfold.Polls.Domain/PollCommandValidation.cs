using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Operations;

namespace Interfold.Domain.Polls;

internal static class PollCommandValidation
{
    public static EntityRef? GetTitleDescriptionValidationError(string? title, string? description)
    {
        if (title is not null && title.Length > 100)
            return EntityRefs.PollTitleTooLong;

        if (description is not null && description.Length > 2000)
            return EntityRefs.PollDescriptionTooLong;

        return null;
    }

    public static bool HasNoMutableFields(UpdatePollCommand payload)
        => payload.Title is null
           && payload.Description is null
           && !payload.HasTimeEnd
           && payload.Data is null;
}
