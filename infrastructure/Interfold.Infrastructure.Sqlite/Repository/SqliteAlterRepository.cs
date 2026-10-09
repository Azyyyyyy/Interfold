using System.Data;
using System.Diagnostics;
using Dapper;
using Interfold.Alters.Contracts.Abstractions;
using Interfold.Alters.Contracts.Models;
using Interfold.Alters.Contracts.Models.Commands;
using Interfold.Alters.Domain;
using Interfold.Alters.Domain.Abstractions.Repository;
using Interfold.Friendships.Domain.Abstractions.Repository;
using Interfold.Infrastructure.Sqlite;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Read;
using Interfold.Shared.Domain.Abstractions.Repository;
using Interfold.Shared.Domain.Observability;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Interfold.Infrastructure.Sqlite.Repository;

public sealed class SqliteAlterRepository : IAlterRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly IFriendshipRepository _friendships;
    private readonly ISettingsFieldRepository _settingsFields;
    private readonly IAlterFieldDefinitions _alterFieldDefinitions;
    private readonly ILogger<SqliteAlterRepository> _logger;

    public SqliteAlterRepository(
        ISqliteConnectionFactory connectionFactory,
        IFriendshipRepository friendships,
        ISettingsFieldRepository settingsFields,
        IAlterFieldDefinitions alterFieldDefinitions,
        ILogger<SqliteAlterRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _friendships = friendships;
        _settingsFields = settingsFields;
        _alterFieldDefinitions = alterFieldDefinitions;
        _logger = logger;
    }

    public async Task<AlterId?> CreateAsync(
        SystemId systemId,
        CreateAlterCommand command,
        CancellationToken cancellationToken = default)
    {
        var createdAtMs = command.CreatedAt.ToUnixTimeMilliseconds();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        var max = await connection.ExecuteScalarAsync<long>(
            "SELECT COALESCE(MAX(id), 0) FROM alters WHERE system_id = @system_id",
            new { system_id = systemId },
            tx);
        var nextId = checked((short)(max + 1));

        await connection.ExecuteAsync(
            """
            INSERT INTO alters (
                system_id, id, name, alias, security_level,
                untracked, archived, pinned, inserted_at, updated_at)
            VALUES (
                @system_id, @id, @name, NULL, @security_level,
                0, 0, 0, @inserted_at, @updated_at)
            """,
            new
            {
                system_id = systemId,
                id = nextId,
                name = command.Name,
                security_level = (short)VisibilityLevel.Private,
                inserted_at = createdAtMs,
                updated_at = createdAtMs,
            },
            tx);

        await tx.CommitAsync(cancellationToken);
        return new AlterId(nextId);
    }

    public async Task<bool> ExistsAsync(
        SystemId systemId,
        AlterId alterId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        return await RowExistsAsync(connection, tx: null, systemId, alterId.Value);
    }

    public async Task<bool> UpdateAsync(
        SystemId systemId,
        UpdateAlterCommand command,
        CancellationToken cancellationToken = default)
    {
        var updatedAtMs = command.UpdatedAt.ToUnixTimeMilliseconds();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        if (!await RowExistsAsync(connection, tx, systemId, command.AlterId.Value))
        {
            return false;
        }

        var sets = new List<string> { "updated_at = @updated_at" };
        var parameters = new DynamicParameters();
        parameters.Add("updated_at", updatedAtMs);
        parameters.Add("system_id", systemId);
        parameters.Add("id", command.AlterId.Value);

        void Set(string column, string param, object? value)
        {
            sets.Add($"{column} = @{param}");
            parameters.Add(param, value);
        }

        if (!string.IsNullOrWhiteSpace(command.Name))
            Set("name", "name", command.Name);
        if (command.Description is not null)
            Set("description", "description", command.Description);
        if (command.Color is not null)
            Set("color", "color", command.Color.Value.Value);
        if (command.Pronouns is not null)
            Set("pronouns", "pronouns", command.Pronouns);
        if (command.ProxyName is not null)
            Set("proxy_name", "proxy_name", command.ProxyName);
        if (command.SecurityLevel is not null)
            Set("security_level", "security_level", (short)command.SecurityLevel.Value);
        if (command.Untracked is not null)
            Set("untracked", "untracked", command.Untracked.Value ? 1 : 0);
        if (command.Archived is not null)
            Set("archived", "archived", command.Archived.Value ? 1 : 0);
        if (command.Pinned is not null)
            Set("pinned", "pinned", command.Pinned.Value ? 1 : 0);

        if (command.ClearAvatar)
        {
            Set("avatar_url", "avatar_url", null);
            Set("avatar_source", "avatar_source", null);
        }
        else if (command.AvatarUrl is not null)
        {
            Set("avatar_url", "avatar_url", command.AvatarUrl.Value.Value);
            Set("avatar_source", "avatar_source", (short)(command.AvatarSource ?? AvatarSource.Local));
        }

        if (!string.IsNullOrWhiteSpace(command.Alias))
            Set("alias", "alias", command.Alias);

        var updated = await connection.ExecuteAsync(
            $"""
            UPDATE alters
            SET {string.Join(", ", sets)}
            WHERE system_id = @system_id AND id = @id
            """,
            parameters,
            tx);

        if (command.Fields is not null)
        {
            foreach (var field in command.Fields)
            {
                await connection.ExecuteAsync(
                    """
                    INSERT INTO alter_fields (system_id, alter_id, field_id, value)
                    VALUES (@system_id, @alter_id, @field_id, @value)
                    ON CONFLICT(system_id, alter_id, field_id) DO UPDATE SET
                        value = excluded.value
                    """,
                    new
                    {
                        system_id = systemId,
                        alter_id = command.AlterId.Value,
                        field_id = field.Id.Value.ToString("N"),
                        value = field.Value,
                    },
                    tx);
            }
        }

        await tx.CommitAsync(cancellationToken);
        return updated > 0;
    }

    public async Task<bool> DeleteAsync(
        SystemId systemId,
        AlterId alterId,
        CancellationToken cancellationToken = default)
    {
        await using var work = await SqliteWork.OpenAsync(_connectionFactory, cancellationToken);
        if (!await RowExistsAsync(work.Connection, work.Transaction, systemId, alterId.Value))
            return false;

        await work.Connection.ExecuteAsync(
            """
            DELETE FROM alter_fields
            WHERE system_id = @system_id AND alter_id = @alter_id
            """,
            new { system_id = systemId, alter_id = alterId.Value },
            work.Transaction);

        var removed = await work.Connection.ExecuteAsync(
            """
            DELETE FROM alters
            WHERE system_id = @system_id AND id = @id
            """,
            new { system_id = systemId, id = alterId.Value },
            work.Transaction);
        if (removed == 0)
            return false;

        await work.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<AlterReadModel>> ListAsync(
        SystemId systemId,
        CancellationToken cancellationToken = default)
    {
        var definitions = await _settingsFields.ListAsync(systemId, cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await LoadAlterRowsAsync(connection, systemId, alterId: null);
        var fieldsByAlter = await LoadFieldsByAlterAsync(connection, systemId);

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
        var friendshipLevel = await _friendships.GetFriendshipLevelAsync(
            systemId, viewerSystemId, cancellationToken);
        var definitions = await _alterFieldDefinitions.ListVisibleAsync(systemId, friendshipLevel, cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await LoadAlterRowsAsync(connection, systemId, alterId: null);
        var fieldsByAlter = await LoadFieldsByAlterAsync(connection, systemId);

        var visible = rows
            .Where(r => r.SecurityLevel.CanBeViewedBy(friendshipLevel))
            .OrderBy(r => r.Id)
            .Select(r => MapBareAlter(r, fieldsByAlter.GetValueOrDefault(r.Id), definitions))
            .ToArray();

        GuardedInstrumentation.RecordList(
            _logger, "alter", nameof(ListGuardedAsync), viewerSystemId, systemId,
            totalCount: rows.Count, visibleCount: visible.Length, sw.Elapsed.TotalMilliseconds);
        return visible;
    }

    public async Task<AlterReadModel?> GetAsync(
        SystemId systemId,
        AlterId alterId,
        CancellationToken cancellationToken = default)
    {
        var definitions = await _settingsFields.ListAsync(systemId, cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await LoadAlterRowsAsync(connection, systemId, alterId.Value);
        if (rows.Count == 0)
        {
            return null;
        }

        var fields = await LoadFieldsForAlterAsync(connection, systemId, alterId.Value);
        return MapAlterReadModel(rows[0], fields, definitions);
    }

    public async Task<BareAlter?> GetGuardedAsync(
        SystemId systemId,
        AlterId alterId,
        SystemId? viewerSystemId,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var friendshipLevel = await _friendships.GetFriendshipLevelAsync(
            systemId, viewerSystemId, cancellationToken);
        var definitions = await _alterFieldDefinitions.ListVisibleAsync(systemId, friendshipLevel, cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await LoadAlterRowsAsync(connection, systemId, alterId.Value);
        if (rows.Count == 0)
        {
            GuardedInstrumentation.RecordGet(
                _logger, "alter", nameof(GetGuardedAsync), viewerSystemId, systemId,
                alterId.Value.ToString(), found: false, filtered: false, sw.Elapsed.TotalMilliseconds);
            return null;
        }

        var row = rows[0];
        if (!row.SecurityLevel.CanBeViewedBy(friendshipLevel))
        {
            GuardedInstrumentation.RecordGet(
                _logger, "alter", nameof(GetGuardedAsync), viewerSystemId, systemId,
                alterId.Value.ToString(), found: false, filtered: true, sw.Elapsed.TotalMilliseconds);
            return null;
        }

        var fields = await LoadFieldsForAlterAsync(connection, systemId, alterId.Value);
        var result = MapBareAlter(row, fields, definitions);
        GuardedInstrumentation.RecordGet(
            _logger, "alter", nameof(GetGuardedAsync), viewerSystemId, systemId,
            alterId.Value.ToString(), found: true, filtered: false, sw.Elapsed.TotalMilliseconds);
        return result;
    }

    public async Task<bool> AliasTakenByOtherAsync(
        SystemId systemId,
        AlterId alterId,
        string alias,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var found = await connection.ExecuteScalarAsync<long?>(
            """
            SELECT 1 FROM alters
            WHERE system_id = @system_id
              AND id != @id
              AND alias IS NOT NULL
              AND alias = @alias COLLATE NOCASE
            LIMIT 1
            """,
            new { system_id = systemId, id = alterId.Value, alias });
        return found is not null;
    }

    public async Task RemoveFieldValuesAsync(
        SystemId systemId,
        FieldId fieldId,
        CancellationToken cancellationToken = default)
    {
        await using var work = await SqliteWork.OpenAsync(_connectionFactory, cancellationToken);
        await work.Connection.ExecuteAsync(
            """
            DELETE FROM alter_fields
            WHERE system_id = @system_id AND field_id = @field_id
            """,
            new { system_id = systemId, field_id = fieldId.Value.ToString("N") },
            work.Transaction);
        await work.CommitAsync(cancellationToken);
    }

    private static async Task<bool> RowExistsAsync(
        SqliteConnection connection,
        IDbTransaction? tx,
        SystemId systemId,
        short alterId)
    {
        var found = await connection.ExecuteScalarAsync<long?>(
            """
            SELECT 1 FROM alters
            WHERE system_id = @system_id AND id = @id
            LIMIT 1
            """,
            new { system_id = systemId, id = alterId },
            tx);
        return found is not null;
    }

    private static async Task<List<AlterRow>> LoadAlterRowsAsync(
        SqliteConnection connection,
        SystemId systemId,
        short? alterId)
    {
        const string columns = """
            id AS Id, name AS Name, alias AS Alias, description AS Description,
            avatar_url AS AvatarUrl, avatar_source AS AvatarSource, color AS Color, pronouns AS Pronouns,
            security_level AS SecurityLevel, proxy_name AS ProxyName, untracked AS Untracked,
            archived AS Archived, pinned AS Pinned, inserted_at AS InsertedAt, updated_at AS UpdatedAt
            """;

        var sql = alterId is null
            ? $"""
                SELECT {columns}
                FROM alters
                WHERE system_id = @system_id
                """
            : $"""
                SELECT {columns}
                FROM alters
                WHERE system_id = @system_id AND id = @id
                """;

        var dtos = await connection.QueryAsync<AlterRowDto>(
            sql,
            alterId is null
                ? new { system_id = systemId }
                : new { system_id = systemId, id = alterId.Value });

        return dtos.Select(MapAlterRow).ToList();
    }

    private static AlterRow MapAlterRow(AlterRowDto dto)
        => new(
            (short)dto.Id,
            dto.Name,
            dto.Alias,
            dto.Description,
            AvatarUrl.FromNullable(dto.AvatarUrl),
            dto.AvatarSource is { } src ? ((short)src).FromCodeOrNull<AvatarSource>() : null,
            HexColor.FromNullable(dto.Color),
            dto.Pronouns,
            ((short)dto.SecurityLevel).FromCode<VisibilityLevel>(),
            dto.ProxyName,
            dto.Untracked != 0,
            dto.Archived != 0,
            dto.Pinned != 0,
            DateTimeOffset.FromUnixTimeMilliseconds(dto.InsertedAt).UtcDateTime,
            DateTimeOffset.FromUnixTimeMilliseconds(dto.UpdatedAt).UtcDateTime);

    private static async Task<Dictionary<short, Dictionary<FieldId, string?>>> LoadFieldsByAlterAsync(
        SqliteConnection connection,
        SystemId systemId)
    {
        var rows = await connection.QueryAsync<AlterFieldRow>(
            """
            SELECT alter_id AS AlterId, field_id AS FieldId, value AS Value
            FROM alter_fields
            WHERE system_id = @system_id
            """,
            new { system_id = systemId });

        var result = new Dictionary<short, Dictionary<FieldId, string?>>();
        foreach (var row in rows)
        {
            var alterId = (short)row.AlterId;
            if (!result.TryGetValue(alterId, out var map))
            {
                map = new Dictionary<FieldId, string?>();
                result[alterId] = map;
            }

            map[new FieldId(Guid.Parse(row.FieldId))] = row.Value;
        }

        return result;
    }

    private static async Task<Dictionary<FieldId, string?>> LoadFieldsForAlterAsync(
        SqliteConnection connection,
        SystemId systemId,
        short alterId)
    {
        var rows = await connection.QueryAsync<AlterFieldValueRow>(
            """
            SELECT field_id AS FieldId, value AS Value
            FROM alter_fields
            WHERE system_id = @system_id AND alter_id = @alter_id
            """,
            new { system_id = systemId, alter_id = alterId });

        var map = new Dictionary<FieldId, string?>();
        foreach (var row in rows)
        {
            map[new FieldId(Guid.Parse(row.FieldId))] = row.Value;
        }

        return map;
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

    private sealed record AlterRowDto(
        long Id,
        string Name,
        string? Alias,
        string? Description,
        string? AvatarUrl,
        long? AvatarSource,
        string? Color,
        string? Pronouns,
        long SecurityLevel,
        string? ProxyName,
        long Untracked,
        long Archived,
        long Pinned,
        long InsertedAt,
        long UpdatedAt);

    private sealed record AlterFieldRow(long AlterId, string FieldId, string? Value);

    private sealed record AlterFieldValueRow(string FieldId, string? Value);

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
