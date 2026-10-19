using Interfold.Shared.Contracts.Ids;

namespace Interfold.Settings.Api.Services.Export;

public interface IExportService
{
    Task<PkExportPayload> BuildPkAsync(SystemId systemId, CancellationToken cancellationToken = default);
    Task<OctoconExportPayload> BuildOctoconAsync(SystemId systemId, CancellationToken cancellationToken = default);
}
