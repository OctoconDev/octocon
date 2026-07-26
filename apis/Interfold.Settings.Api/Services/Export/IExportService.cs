using Interfold.Shared.Contracts.Ids;

namespace Interfold.Settings.Api.Services.Export;

public interface IExportService
{
    Task<PkExportPayload> BuildPkAsync(SystemId systemId, CancellationToken cancellationToken = default);
    Task<FullExportPayload> BuildFullAsync(SystemId systemId, CancellationToken cancellationToken = default);
}
