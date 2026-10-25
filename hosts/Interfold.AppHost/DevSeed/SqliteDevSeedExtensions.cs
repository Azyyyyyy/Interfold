using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Interfold.AppHost.DevSeed;

internal static class SqliteDevSeedExtensions
{
    /// <summary>
    /// Registers migrate + secrets seed for SQLite RunMode. The marker resource is what the
    /// API <c>WaitFor</c>s so secrets exist before <c>SecretsPreBuildLoader</c> runs.
    /// </summary>
    public static IResourceBuilder<SqliteDevSeedResource> AddSqliteDevSeedPipeline(
        this IDistributedApplicationBuilder builder,
        string hostDbPath)
    {
        var encryptionPepper = builder.AddParameter(
            "encryption-pepper",
            new GenerateParameterDefault { MinLength = 32 },
            secret: true, persist: true);
        var deepLinkSecret = builder.AddParameter(
            "deep-link-secret",
            new GenerateParameterDefault { MinLength = 32 },
            secret: true, persist: true);

        var seedResourceBuilder = builder.AddResource(new SqliteDevSeedResource("db-seed"));

        var context = new SqliteDevSeedContext(
            seedResource: seedResourceBuilder.Resource,
            hostDbPath: hostDbPath,
            encryptionPepper: encryptionPepper.Resource,
            deepLinkSecret: deepLinkSecret.Resource);

        builder.Services.AddSingleton(context);
        builder.Services.AddHostedService<SqliteDevSeedHostedService>();
        builder.Services.Configure<HostOptions>(o => o.ServicesStartConcurrently = true);

        return seedResourceBuilder;
    }
}
