using Aspire.Hosting.ApplicationModel;

namespace Interfold.AppHost.DevSeed;

/// <summary>
/// Host-side SQLite path + Aspire parameter refs for <see cref="SqliteDevSeedHostedService"/>.
/// </summary>
internal sealed class SqliteDevSeedContext(
    SqliteDevSeedResource seedResource,
    string hostDbPath,
    ParameterResource encryptionPepper,
    ParameterResource deepLinkSecret)
{
    public SqliteDevSeedResource SeedResource { get; } = seedResource;
    public string HostDbPath { get; } = hostDbPath;
    public ParameterResource EncryptionPepper { get; } = encryptionPepper;
    public ParameterResource DeepLinkSecret { get; } = deepLinkSecret;
}
