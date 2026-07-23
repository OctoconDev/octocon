using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Operations;

namespace Interfold.Shared.Domain.Abstractions;

public interface ICommandHandler<TPayload, TResult>
{
    Task<CommandExecutionResult<TResult>> HandleAsync(
        CommandEnvelope<TPayload> command,
        CancellationToken cancellationToken = default
    );
}