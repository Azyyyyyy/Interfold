namespace Interfold.Bootstrapper.Configuration;

/// <summary>V1 public endpoint facts captured before <see cref="ConfigSchemaMigrator"/> rewrites the file.</summary>
internal sealed record V1EndpointSnapshot(
    bool WebEnabled,
    int ApiHttps,
    int WebHttps,
    IReadOnlyList<string> Hosts);
