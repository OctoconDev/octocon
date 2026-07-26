using Interfold.Shared.Contracts.Ids;

namespace Interfold.Api.Services.Export;

public interface IExportService
{
    Task<PkExportPayload> BuildPkAsync(SystemId systemId, CancellationToken cancellationToken = default);
    Task<FullExportPayload> BuildFullAsync(SystemId systemId, CancellationToken cancellationToken = default);
}
