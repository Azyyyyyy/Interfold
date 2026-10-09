using Aspire.Hosting.ApplicationModel;

namespace Interfold.AppHost.DevSeed;

/// <summary>Marker resource the API waits on for SQLite dev seed completion.</summary>
public sealed class SqliteDevSeedResource(string name) : Resource(name), IResourceWithWaitSupport;
