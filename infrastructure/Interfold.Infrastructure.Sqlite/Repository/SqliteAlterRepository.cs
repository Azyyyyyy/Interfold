using System.Diagnostics;
using Interfold.Alters.Contracts.Abstractions;
using Interfold.Alters.Contracts.Models;
using Interfold.Alters.Contracts.Models.Commands;
using Interfold.Alters.Domain;
using Interfold.Alters.Domain.Abstractions.Repository;
using Interfold.Friendships.Domain.Abstractions.Repository;
using Interfold.Polls.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Read;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Shared.Domain.Abstractions.Repository;
using Interfold.Shared.Domain.Observability;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Interfold.Infrastructure.Sqlite.Repository;

public sealed class SqliteAlterRepository : IAlterRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly IRegionContext _regionContext;
    private readonly IFriendshipRepository _friendships;
    private readonly ISettingsFieldRepository _settingsFields;
    private readonly IAlterFieldDefinitions _alterFieldDefinitions;
    private readonly IPollRepository _polls;
    private readonly ILogger<SqliteAlterRepository> _logger;

    public SqliteAlterRepository(
        ISqliteConnectionFactory connectionFactory,
        IRegionContext regionContext,
        IFriendshipRepository friendships,
        ISettingsFieldRepository settingsFields,
        IAlterFieldDefinitions alterFieldDefinitions,
        IPollRepository polls,
        ILogger<SqliteAlterRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _regionContext = regionContext;
        _friendships = friendships;
        _settingsFields = settingsFields;
        _alterFieldDefinitions = alterFieldDefinitions;
        _polls = polls;
        _logger = logger;
    }

    public async Task<AlterId?> CreateAsync(
        SystemId systemId,
        CreateAlterCommand command,
        CancellationToken cancellationToken = default)
    {
        var systemKey = SqliteStorageKeys.ForSystem(_regionContext, systemId).Value;
        var createdAtMs = command.CreatedAt.ToUnixTimeMilliseconds();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        short nextId;
        await using (var maxCmd = connection.CreateCommand())
        {
            maxCmd.Transaction = tx;
            maxCmd.CommandText = "SELECT COALESCE(MAX(id), 0) FROM alters WHERE system_id = $system_id";
            maxCmd.Parameters.AddWithValue("$system_id", systemKey);
            var max = Convert.ToInt16(await maxCmd.ExecuteScalarAsync(cancellationToken) ?? 0);
            nextId = checked((short)(max + 1));
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO alters (
                    system_id, id, name, alias, security_level,
                    untracked, archived, pinned, inserted_at, updated_at)
                VALUES (
                    $system_id, $id, $name, NULL, $security_level,
                    0, 0, 0, $inserted_at, $updated_at)
                """;
            insert.Parameters.AddWithValue("$system_id", systemKey);
            insert.Parameters.AddWithValue("$id", nextId);
            insert.Parameters.AddWithValue("$name", command.Name);
            insert.Parameters.AddWithValue("$security_level", VisibilityLevel.Private.ToWire());
            insert.Parameters.AddWithValue("$inserted_at", createdAtMs);
            insert.Parameters.AddWithValue("$updated_at", createdAtMs);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return new AlterId(nextId);
    }

    public async Task<bool> ExistsAsync(
        SystemId systemId,
        AlterId alterId,
        CancellationToken cancellationToken = default)
    {
        var systemKey = SqliteStorageKeys.ForSystem(_regionContext, systemId).Value;
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1 FROM alters
            WHERE system_id = $system_id AND id = $id
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$system_id", systemKey);
        command.Parameters.AddWithValue("$id", alterId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken);
    }

    public async Task<bool> UpdateAsync(
        SystemId systemId,
        UpdateAlterCommand command,
        CancellationToken cancellationToken = default)
    {
        var systemKey = SqliteStorageKeys.ForSystem(_regionContext, systemId).Value;
        var updatedAtMs = command.UpdatedAt.ToUnixTimeMilliseconds();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        if (!await RowExistsAsync(connection, tx, systemKey, command.AlterId.Value, cancellationToken))
        {
            return false;
        }

        var sets = new List<string> { "updated_at = $updated_at" };
        await using var update = connection.CreateCommand();
        update.Transaction = tx;
        update.Parameters.AddWithValue("$updated_at", updatedAtMs);
        update.Parameters.AddWithValue("$system_id", systemKey);
        update.Parameters.AddWithValue("$id", command.AlterId.Value);

        void Set(string column, string param, object? value)
        {
            sets.Add($"{column} = {param}");
            update.Parameters.AddWithValue(param, value ?? DBNull.Value);
        }

        if (!string.IsNullOrWhiteSpace(command.Name))
            Set("name", "$name", command.Name);
        if (command.Description is not null)
            Set("description", "$description", command.Description);
        if (command.Color is not null)
            Set("color", "$color", command.Color.Value.Value);
        if (command.Pronouns is not null)
            Set("pronouns", "$pronouns", command.Pronouns);
        if (command.ProxyName is not null)
            Set("proxy_name", "$proxy_name", command.ProxyName);
        if (command.SecurityLevel is not null)
            Set("security_level", "$security_level", command.SecurityLevel.Value.ToWire());
        if (command.Untracked is not null)
            Set("untracked", "$untracked", command.Untracked.Value ? 1 : 0);
        if (command.Archived is not null)
            Set("archived", "$archived", command.Archived.Value ? 1 : 0);
        if (command.Pinned is not null)
            Set("pinned", "$pinned", command.Pinned.Value ? 1 : 0);

        if (command.ClearAvatar)
        {
            Set("avatar_url", "$avatar_url", null);
            Set("avatar_source", "$avatar_source", null);
        }
        else if (command.AvatarUrl is not null)
        {
            Set("avatar_url", "$avatar_url", command.AvatarUrl.Value.Value);
            Set("avatar_source", "$avatar_source", (command.AvatarSource ?? AvatarSource.Local).ToWire());
        }

        if (!string.IsNullOrWhiteSpace(command.Alias))
            Set("alias", "$alias", command.Alias);

        update.CommandText = $"""
            UPDATE alters
            SET {string.Join(", ", sets)}
            WHERE system_id = $system_id AND id = $id
            """;
        await update.ExecuteNonQueryAsync(cancellationToken);

        if (command.Fields is not null)
        {
            foreach (var field in command.Fields)
            {
                await using var upsert = connection.CreateCommand();
                upsert.Transaction = tx;
                upsert.CommandText = """
                    INSERT INTO alter_fields (system_id, alter_id, field_id, value)
                    VALUES ($system_id, $alter_id, $field_id, $value)
                    ON CONFLICT(system_id, alter_id, field_id) DO UPDATE SET
                        value = excluded.value
                    """;
                upsert.Parameters.AddWithValue("$system_id", systemKey);
                upsert.Parameters.AddWithValue("$alter_id", command.AlterId.Value);
                upsert.Parameters.AddWithValue("$field_id", field.Id.Value.ToString("N"));
                upsert.Parameters.AddWithValue("$value", (object?)field.Value ?? DBNull.Value);
                await upsert.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await tx.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteAsync(
        SystemId systemId,
        AlterId alterId,
        CancellationToken cancellationToken = default)
    {
        var systemKey = SqliteStorageKeys.ForSystem(_regionContext, systemId).Value;

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        if (!await RowExistsAsync(connection, tx, systemKey, alterId.Value, cancellationToken))
        {
            return false;
        }

        await using (var deleteFields = connection.CreateCommand())
        {
            deleteFields.Transaction = tx;
            deleteFields.CommandText = """
                DELETE FROM alter_fields
                WHERE system_id = $system_id AND alter_id = $alter_id
                """;
            deleteFields.Parameters.AddWithValue("$system_id", systemKey);
            deleteFields.Parameters.AddWithValue("$alter_id", alterId.Value);
            await deleteFields.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteTags = connection.CreateCommand())
        {
            deleteTags.Transaction = tx;
            deleteTags.CommandText = """
                DELETE FROM alter_tags
                WHERE system_id = $system_id AND alter_id = $alter_id
                """;
            deleteTags.Parameters.AddWithValue("$system_id", systemKey);
            deleteTags.Parameters.AddWithValue("$alter_id", alterId.Value);
            await deleteTags.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteAlter = connection.CreateCommand())
        {
            deleteAlter.Transaction = tx;
            deleteAlter.CommandText = """
                DELETE FROM alters
                WHERE system_id = $system_id AND id = $id
                """;
            deleteAlter.Parameters.AddWithValue("$system_id", systemKey);
            deleteAlter.Parameters.AddWithValue("$id", alterId.Value);
            await deleteAlter.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        await _polls.RemoveAlterFromPollsAsync(systemId, alterId, cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<AlterReadModel>> ListAsync(
        SystemId systemId,
        CancellationToken cancellationToken = default)
    {
        var systemKey = SqliteStorageKeys.ForSystem(_regionContext, systemId).Value;
        var definitions = await _settingsFields.ListAsync(systemId, cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await LoadAlterRowsAsync(connection, systemKey, alterId: null, cancellationToken);
        var fieldsByAlter = await LoadFieldsByAlterAsync(connection, systemKey, cancellationToken);

        return rows
            .OrderBy(r => r.Id)
            .Select(r => MapAlterReadModel(r, fieldsByAlter.GetValueOrDefault(r.Id), definitions))
            .ToArray();
    }

    public async Task<IReadOnlyList<BareAlter>> ListGuardedAsync(
        SystemId systemId,
        SystemId? viewerSystemId,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var friendshipLevel = await SqliteStorageKeys.ResolveFriendshipLevelAsync(
            systemId, viewerSystemId, _friendships, cancellationToken);
        var definitions = await _alterFieldDefinitions.ListVisibleAsync(systemId, friendshipLevel, cancellationToken);
        var ownerId = SqliteStorageKeys.Normalize(systemId).Value;
        var systemKey = SqliteStorageKeys.ForSystem(_regionContext, systemId).Value;

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await LoadAlterRowsAsync(connection, systemKey, alterId: null, cancellationToken);
        var fieldsByAlter = await LoadFieldsByAlterAsync(connection, systemKey, cancellationToken);

        var visible = rows
            .Where(r => r.SecurityLevel.CanBeViewedBy(friendshipLevel))
            .OrderBy(r => r.Id)
            .Select(r => MapBareAlter(r, fieldsByAlter.GetValueOrDefault(r.Id), definitions))
            .ToArray();

        GuardedInstrumentation.RecordList(
            _logger, "alter", nameof(ListGuardedAsync), viewerSystemId, ownerId,
            totalCount: rows.Count, visibleCount: visible.Length, sw.Elapsed.TotalMilliseconds);
        return visible;
    }

    public async Task<AlterReadModel?> GetAsync(
        SystemId systemId,
        AlterId alterId,
        CancellationToken cancellationToken = default)
    {
        var systemKey = SqliteStorageKeys.ForSystem(_regionContext, systemId).Value;
        var definitions = await _settingsFields.ListAsync(systemId, cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await LoadAlterRowsAsync(connection, systemKey, alterId.Value, cancellationToken);
        if (rows.Count == 0)
        {
            return null;
        }

        var fields = await LoadFieldsForAlterAsync(connection, systemKey, alterId.Value, cancellationToken);
        return MapAlterReadModel(rows[0], fields, definitions);
    }

    public async Task<BareAlter?> GetGuardedAsync(
        SystemId systemId,
        AlterId alterId,
        SystemId? viewerSystemId,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var ownerId = SqliteStorageKeys.Normalize(systemId).Value;
        var friendshipLevel = await SqliteStorageKeys.ResolveFriendshipLevelAsync(
            systemId, viewerSystemId, _friendships, cancellationToken);
        var definitions = await _alterFieldDefinitions.ListVisibleAsync(systemId, friendshipLevel, cancellationToken);
        var systemKey = SqliteStorageKeys.ForSystem(_regionContext, systemId).Value;

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await LoadAlterRowsAsync(connection, systemKey, alterId.Value, cancellationToken);
        if (rows.Count == 0)
        {
            GuardedInstrumentation.RecordGet(
                _logger, "alter", nameof(GetGuardedAsync), viewerSystemId, ownerId,
                alterId.Value.ToString(), found: false, filtered: false, sw.Elapsed.TotalMilliseconds);
            return null;
        }

        var row = rows[0];
        if (!row.SecurityLevel.CanBeViewedBy(friendshipLevel))
        {
            GuardedInstrumentation.RecordGet(
                _logger, "alter", nameof(GetGuardedAsync), viewerSystemId, ownerId,
                alterId.Value.ToString(), found: false, filtered: true, sw.Elapsed.TotalMilliseconds);
            return null;
        }

        var fields = await LoadFieldsForAlterAsync(connection, systemKey, alterId.Value, cancellationToken);
        var result = MapBareAlter(row, fields, definitions);
        GuardedInstrumentation.RecordGet(
            _logger, "alter", nameof(GetGuardedAsync), viewerSystemId, ownerId,
            alterId.Value.ToString(), found: true, filtered: false, sw.Elapsed.TotalMilliseconds);
        return result;
    }

    public async Task<bool> AliasTakenByOtherAsync(
        SystemId systemId,
        AlterId alterId,
        string alias,
        CancellationToken cancellationToken = default)
    {
        var systemKey = SqliteStorageKeys.ForSystem(_regionContext, systemId).Value;
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1 FROM alters
            WHERE system_id = $system_id
              AND id != $id
              AND alias IS NOT NULL
              AND alias = $alias COLLATE NOCASE
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$system_id", systemKey);
        command.Parameters.AddWithValue("$id", alterId.Value);
        command.Parameters.AddWithValue("$alias", alias);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken);
    }

    internal static async Task RemoveFieldValuesForSystemAsync(
        ISqliteConnectionFactory connectionFactory,
        string systemKey,
        Guid fieldId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM alter_fields
            WHERE system_id = $system_id AND field_id = $field_id
            """;
        command.Parameters.AddWithValue("$system_id", systemKey);
        command.Parameters.AddWithValue("$field_id", fieldId.ToString("N"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> RowExistsAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string systemKey,
        short alterId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT 1 FROM alters
            WHERE system_id = $system_id AND id = $id
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$system_id", systemKey);
        command.Parameters.AddWithValue("$id", alterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken);
    }

    private static async Task<List<AlterRow>> LoadAlterRowsAsync(
        SqliteConnection connection,
        string systemKey,
        short? alterId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = alterId is null
            ? """
                SELECT id, name, alias, description, avatar_url, avatar_source, color, pronouns,
                       security_level, proxy_name, untracked, archived, pinned, inserted_at, updated_at
                FROM alters
                WHERE system_id = $system_id
                """
            : """
                SELECT id, name, alias, description, avatar_url, avatar_source, color, pronouns,
                       security_level, proxy_name, untracked, archived, pinned, inserted_at, updated_at
                FROM alters
                WHERE system_id = $system_id AND id = $id
                """;
        command.Parameters.AddWithValue("$system_id", systemKey);
        if (alterId is not null)
        {
            command.Parameters.AddWithValue("$id", alterId.Value);
        }

        var rows = new List<AlterRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadAlterRow(reader));
        }

        return rows;
    }

    private static async Task<Dictionary<short, Dictionary<FieldId, string?>>> LoadFieldsByAlterAsync(
        SqliteConnection connection,
        string systemKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT alter_id, field_id, value
            FROM alter_fields
            WHERE system_id = $system_id
            """;
        command.Parameters.AddWithValue("$system_id", systemKey);

        var result = new Dictionary<short, Dictionary<FieldId, string?>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var alterId = (short)reader.GetInt64(0);
            if (!result.TryGetValue(alterId, out var map))
            {
                map = new Dictionary<FieldId, string?>();
                result[alterId] = map;
            }

            map[new FieldId(Guid.Parse(reader.GetString(1)))] =
                reader.IsDBNull(2) ? null : reader.GetString(2);
        }

        return result;
    }

    private static async Task<Dictionary<FieldId, string?>> LoadFieldsForAlterAsync(
        SqliteConnection connection,
        string systemKey,
        short alterId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT field_id, value
            FROM alter_fields
            WHERE system_id = $system_id AND alter_id = $alter_id
            """;
        command.Parameters.AddWithValue("$system_id", systemKey);
        command.Parameters.AddWithValue("$alter_id", alterId);

        var map = new Dictionary<FieldId, string?>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            map[new FieldId(Guid.Parse(reader.GetString(0)))] =
                reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        return map;
    }

    private static AlterRow ReadAlterRow(SqliteDataReader reader)
    {
        var securityWire = reader.GetString(reader.GetOrdinal("security_level"));
        if (!securityWire.TryParseWire(out VisibilityLevel securityLevel))
        {
            throw new InvalidOperationException($"Corrupt alter security_level wire value '{securityWire}'.");
        }

        var avatarSourceOrdinal = reader.GetOrdinal("avatar_source");
        AvatarSource? avatarSource = null;
        if (!reader.IsDBNull(avatarSourceOrdinal))
        {
            var wire = reader.GetString(avatarSourceOrdinal);
            if (!wire.TryParseWire(out AvatarSource parsed))
            {
                throw new InvalidOperationException($"Corrupt alter avatar_source wire value '{wire}'.");
            }

            avatarSource = parsed;
        }

        return new AlterRow(
            Id: (short)reader.GetInt64(reader.GetOrdinal("id")),
            Name: reader.GetString(reader.GetOrdinal("name")),
            Alias: reader.IsDBNull(reader.GetOrdinal("alias")) ? null : reader.GetString(reader.GetOrdinal("alias")),
            Description: reader.IsDBNull(reader.GetOrdinal("description")) ? null : reader.GetString(reader.GetOrdinal("description")),
            AvatarUrl: AvatarUrl.FromNullable(
                reader.IsDBNull(reader.GetOrdinal("avatar_url")) ? null : reader.GetString(reader.GetOrdinal("avatar_url"))),
            AvatarSource: avatarSource,
            Color: HexColor.FromNullable(
                reader.IsDBNull(reader.GetOrdinal("color")) ? null : reader.GetString(reader.GetOrdinal("color"))),
            Pronouns: reader.IsDBNull(reader.GetOrdinal("pronouns")) ? null : reader.GetString(reader.GetOrdinal("pronouns")),
            SecurityLevel: securityLevel,
            ProxyName: reader.IsDBNull(reader.GetOrdinal("proxy_name")) ? null : reader.GetString(reader.GetOrdinal("proxy_name")),
            Untracked: reader.GetInt64(reader.GetOrdinal("untracked")) != 0,
            Archived: reader.GetInt64(reader.GetOrdinal("archived")) != 0,
            Pinned: reader.GetInt64(reader.GetOrdinal("pinned")) != 0,
            InsertedAt: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(reader.GetOrdinal("inserted_at"))).UtcDateTime,
            UpdatedAt: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(reader.GetOrdinal("updated_at"))).UtcDateTime);
    }

    private static AlterReadModel MapAlterReadModel(
        AlterRow row,
        Dictionary<FieldId, string?>? fieldValues,
        IReadOnlyList<SettingsFieldReadModel> definitions)
        => new(
            new AlterId(row.Id),
            row.Name,
            row.Description,
            row.AvatarUrl,
            row.AvatarSource,
            row.Color,
            row.Pronouns,
            row.SecurityLevel,
            AlterFieldProjection.ResolveOwnerFields(fieldValues, definitions),
            row.ProxyName,
            row.Alias,
            row.Untracked,
            row.Archived,
            row.Pinned,
            Array.Empty<string>(),
            row.InsertedAt,
            row.UpdatedAt);

    private BareAlter MapBareAlter(
        AlterRow row,
        Dictionary<FieldId, string?>? fieldValues,
        IReadOnlyList<SettingsFieldReadModel> definitions)
        => new(
            new AlterId(row.Id),
            row.Name,
            row.AvatarUrl,
            row.AvatarSource,
            row.Color,
            row.Pronouns,
            row.Description,
            AlterFieldProjection.ResolveGuardedFields(fieldValues, definitions, _logger));

    private sealed record AlterRow(
        short Id,
        string Name,
        string? Alias,
        string? Description,
        AvatarUrl? AvatarUrl,
        AvatarSource? AvatarSource,
        HexColor? Color,
        string? Pronouns,
        VisibilityLevel SecurityLevel,
        string? ProxyName,
        bool Untracked,
        bool Archived,
        bool Pinned,
        DateTime InsertedAt,
        DateTime UpdatedAt);
}
