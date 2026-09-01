using System.Text.Json.Serialization;
using Interfold.Shared.Contracts.Enums;

namespace Interfold.Bootstrapper.Configuration;

/// <summary>CQL datastore backend under <c>datastores.cql.backend</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CqlBackend>))]
public enum CqlBackend
{
    [JsonStringEnumMemberName("scylla-single")]
    ScyllaSingle,

    [JsonStringEnumMemberName("scylla-multi")]
    ScyllaMulti,

    [JsonStringEnumMemberName("cassandra")]
    Cassandra,
}

internal static class CqlBackendMapping
{
    internal static DatabaseMode ToDatabaseMode(CqlBackend backend) => backend switch
    {
        CqlBackend.ScyllaSingle => DatabaseMode.Single,
        CqlBackend.ScyllaMulti => DatabaseMode.Multi,
        CqlBackend.Cassandra => DatabaseMode.Cassandra,
        _ => DatabaseMode.Single,
    };

    internal static CqlBackend FromDatabaseMode(DatabaseMode mode) => mode switch
    {
        DatabaseMode.Single => CqlBackend.ScyllaSingle,
        DatabaseMode.Multi => CqlBackend.ScyllaMulti,
        DatabaseMode.Cassandra => CqlBackend.Cassandra,
        _ => CqlBackend.ScyllaSingle,
    };

    internal static CqlBackend ParseWire(string? wire)
    {
        if (string.IsNullOrWhiteSpace(wire))
        {
            return CqlBackend.ScyllaSingle;
        }

        return wire.Trim().ToLowerInvariant() switch
        {
            "scylla-single" or "single" => CqlBackend.ScyllaSingle,
            "scylla-multi" or "multi" => CqlBackend.ScyllaMulti,
            "cassandra" => CqlBackend.Cassandra,
            _ => throw new InvalidOperationException(
                $"Unknown datastores.cql.backend '{wire}'. Expected scylla-single, scylla-multi, or cassandra."),
        };
    }

    internal static string ToWire(this CqlBackend backend) => backend switch
    {
        CqlBackend.ScyllaSingle => "scylla-single",
        CqlBackend.ScyllaMulti => "scylla-multi",
        CqlBackend.Cassandra => "cassandra",
        _ => "scylla-single",
    };
}
