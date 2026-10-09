using System.Text.Json.Serialization;

namespace Interfold.Shared.Contracts.Enums;

/// <summary>Database stack the bootstrapper deploys. Wire value: sqlite (API-only,
/// no Postgres/CQL).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DatabaseMode>))]
public enum DatabaseMode
{
    [JsonStringEnumMemberName("sqlite")]
    Sqlite,
}
