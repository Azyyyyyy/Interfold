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
    public static IResourceBuilder<DevSeedResource> AddSqliteDevSeedPipeline(
        this IDistributedApplicationBuilder builder,
        string hostDbPath)
    {
        var encryptionPepper = builder.AddParameter(
            DevSeedParameterNames.EncryptionPepper,
            new GenerateParameterDefault { MinLength = 32 },
            secret: true, persist: true);
        var deepLinkSecret = builder.AddParameter(
            DevSeedParameterNames.DeepLinkSecret,
            new GenerateParameterDefault { MinLength = 32 },
            secret: true, persist: true);

        var seedResourceBuilder = builder.AddResource(new DevSeedResource("db-seed"));

        var context = new SqliteDevSeedContext(
            seedResource: seedResourceBuilder.Resource,
            hostDbPath: hostDbPath,
            encryptionPepper: encryptionPepper.Resource,
            deepLinkSecret: deepLinkSecret.Resource);


        builder.Services.AddSingleton(context);
        builder.Services.AddHostedService<SqliteDevSeedHostedService>();
        // Same concurrent-startup requirement as the Postgres DevSeed path — WaitFor(db-seed)
        // deadlocks if the hosted service cannot start in parallel with the orchestrator.
        builder.Services.Configure<HostOptions>(o => o.ServicesStartConcurrently = true);

        return seedResourceBuilder;
    }
}
