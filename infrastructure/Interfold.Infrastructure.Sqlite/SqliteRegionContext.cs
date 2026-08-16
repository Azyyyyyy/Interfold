using Interfold.Shared.Contracts.Enums;

using Interfold.Shared.Contracts.Ids;

using Interfold.Shared.Domain.Abstractions;



namespace Interfold.Infrastructure.Sqlite;



/// <summary>Region routing for Sqlite mode — same hash-bucket behaviour as InMemory.</summary>

public sealed class SqliteRegionContext : IRegionContext

{

    private static readonly ScyllaKeyspace[] Regions = Enum.GetValues<ScyllaKeyspace>();



    public ScyllaKeyspace CurrentRegion { get; }



    public SqliteRegionContext(ScyllaKeyspace currentRegion = ScyllaKeyspace.Nam)

    {

        CurrentRegion = currentRegion;

    }



    public ScyllaKeyspace ResolveUserRegion(SystemId systemId)

    {

        if (string.IsNullOrWhiteSpace(systemId))

        {

            return CurrentRegion;

        }



        var normalized = ScopedSystemId.StripRegionPrefix(systemId);

        var index = Math.Abs(normalized.GetHashCode(StringComparison.Ordinal)) % Regions.Length;

        return Regions[index];

    }

}

