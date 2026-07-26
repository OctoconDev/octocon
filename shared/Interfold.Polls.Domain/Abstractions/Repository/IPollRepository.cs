using Interfold.Polls.Contracts.Ids;
using Interfold.Polls.Contracts.Models.Commands;
using Interfold.Polls.Contracts.Models.Read;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Polls.Domain.Abstractions.Repository;

public interface IPollRepository
{
    Task<IReadOnlyList<PollReadModel>> ListAsync(SystemId systemId, CancellationToken cancellationToken = default);

    Task<PollReadModel?> GetAsync(SystemId systemId, PollId pollId, CancellationToken cancellationToken = default);

    Task<PollId?> CreateAsync(SystemId systemId, CreatePollCommand command, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(SystemId systemId, PollId pollId, CancellationToken cancellationToken = default);

    Task<bool> UpdateAsync(SystemId systemId, UpdatePollCommand command, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(SystemId systemId, PollId pollId, CancellationToken cancellationToken = default);
    Task RemoveAlterFromPollsAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default);
}
