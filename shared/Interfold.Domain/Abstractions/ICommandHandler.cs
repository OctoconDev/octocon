using Interfold.Contracts.Models;
using Interfold.Contracts.Operations;

namespace Interfold.Domain.Abstractions;

public interface ICommandHandler<TPayload, TResult>
{
    Task<CommandExecutionResult<TResult>> HandleAsync(
        CommandEnvelope<TPayload> command,
        CancellationToken cancellationToken = default
    );
}