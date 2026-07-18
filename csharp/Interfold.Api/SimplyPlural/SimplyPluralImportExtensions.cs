using Interfold.Api.Services;
using Interfold.Api.Services.Http;
using Interfold.Api.Services.ImportJobs;
using Interfold.Domain;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.ImportJobs;
using Interfold.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Interfold.Api.SimplyPlural;

public static class SimplyPluralImportExtensions
{
    public static IServiceCollection AddSimplyPluralImport(this IServiceCollection services)
    {
        services.AddHttpClient(HttpClientNames.SimplyPlural).AddHttpMessageHandler<HttpLoggingHandler>();
        services.AddSingleton<ISimplyPluralImportService, SimplyPluralImportService>();
        services.AddSingleton<IImportJobRunner, SpImportJobRunner>();
        return services;
    }
}
