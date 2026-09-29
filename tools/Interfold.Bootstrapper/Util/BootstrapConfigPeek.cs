using System.Text.Json;
using System.Text.Json.Serialization;
using Interfold.Bootstrapper.Configuration;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Enums;

namespace Interfold.Bootstrapper.Util;

/// <summary>Tolerant pre-validation reads of <c>interfold.bootstrap.json</c>
/// for AIO sizing — missing/unreadable files fall back to safe defaults.</summary>
internal static class BootstrapConfigPeek
{
    public static async Task<PersistenceMode> PeekPersistenceModeAsync(
        string? configPath,
        CancellationToken cancellationToken = default)
    {
        var peek = await TryReadAsync(configPath, cancellationToken).ConfigureAwait(false);
        if (peek?.Datastores is { } datastores)
        {
            return datastores.Persistence;
        }

        if (peek?.DatabaseMode is { } wire
            && wire.TryParseWire<DatabaseMode>(out var mode)
            && mode == DatabaseMode.Sqlite)
        {
            return PersistenceMode.Sqlite;
        }

        return PersistenceMode.Sqlite;
    }

    public static async Task<DatabaseMode> PeekCqlDatabaseModeAsync(
        string? configPath,
        CancellationToken cancellationToken = default)
    {
        var peek = await TryReadAsync(configPath, cancellationToken).ConfigureAwait(false);
        if (peek?.Datastores?.Cql is { } cql)
        {
            return CqlBackendMapping.ToDatabaseMode(cql.Backend);
        }

        if (peek?.DatabaseMode is { } wire
            && wire.TryParseWire<DatabaseMode>(out var mode))
        {
            return mode;
        }

        return DatabaseMode.Single;
    }

    private static async Task<BootstrapDatastoresPeek?> TryReadAsync(
        string? configPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(configPath) || !File.Exists(configPath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(configPath);
            return await JsonSerializer.DeserializeAsync(
                stream,
                BootstrapJsonContext.Default.BootstrapDatastoresPeek,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Slim deserialize shape for pre-validation peeks — avoids full
/// <see cref="BootstrapConfig"/> validate while still using typed STJ.</summary>
public sealed class BootstrapDatastoresPeek
{
    [JsonPropertyName("datastores")]
    public DatastoresSection? Datastores { get; set; }

    [JsonPropertyName("databaseMode")]
    public string? DatabaseMode { get; set; }
}
