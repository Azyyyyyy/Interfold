using System.Data;
using System.Diagnostics;
using Dapper;
using Interfold.Alters.Contracts.Models;
using Interfold.Alters.Domain.Abstractions.Repository;
using Interfold.Friendships.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Domain.Observability;
using Interfold.Tags.Contracts.Ids;
using Interfold.Tags.Contracts.Models.Commands;
using Interfold.Tags.Contracts.Models.Read;
using Interfold.Tags.Domain.Abstractions.Repository;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Interfold.Infrastructure.Sqlite.Repository;

public sealed class SqliteTagRepository : ITagRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly IFriendshipRepository? _friendships;
    private readonly IAlterRepository _alterRepository;
    private readonly ILogger<SqliteTagRepository> _logger;
    private readonly TimeProvider _timeProvider;

    public SqliteTagRepository(
        ISqliteConnectionFactory connectionFactory,
        IFriendshipRepository friendships,
        IAlterRepository alterRepository,
        ILogger<SqliteTagRepository> logger,
        TimeProvider? timeProvider = null)
    {
        _connectionFactory = connectionFactory;
        _friendships = friendships;
        _alterRepository = alterRepository;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<TagId?> CreateAsync(
        SystemId systemId,
        CreateTagCommand command,
        CancellationToken cancellationToken = default)
    {
        var systemKey = SqliteStorageKeys.Persist(systemId);
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var insertedAtMs = new DateTimeOffset(DateTime.SpecifyKind(command.InsertedAtUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        string? parentId = null;
        if (command.ParentTagId is { } parent && parent != TagId.Empty)
        {
            if (!await TagExistsAsync(connection, systemKey, parent.Value.ToString("N")))
            {
                return null;
            }

            parentId = parent.Value.ToString("N");
        }

        var tagGuid = Guid.NewGuid();
        var tagIdHex = tagGuid.ToString("N");

        await connection.ExecuteAsync(
            """
            INSERT INTO tags (
                system_id, id, parent_tag_id, name, description, color,
                security_level, inserted_at, updated_at)
            VALUES (
                @system_id, @id, @parent_tag_id, @name, NULL, NULL,
                @security_level, @inserted_at, @updated_at)
            """,
            new
            {
                system_id = systemKey,
                id = tagIdHex,
                parent_tag_id = parentId,
                name = command.Name,
                security_level = (short)VisibilityLevel.Private,
                inserted_at = insertedAtMs,
                updated_at = nowMs,
            });

        return new TagId(tagGuid);
    }

    public async Task<bool> ExistsAsync(
        SystemId systemId,
        TagId tagId,
        CancellationToken cancellationToken = default)
    {
        var systemKey = SqliteStorageKeys.Persist(systemId);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        return await TagExistsAsync(connection, systemKey, tagId.Value.ToString("N"));
    }

    public async Task<bool> UpdateAsync(
        SystemId systemId,
        UpdateTagCommand command,
        CancellationToken cancellationToken = default)
    {
        var systemKey = SqliteStorageKeys.Persist(systemId);
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var tagHex = command.TagId.Value.ToString("N");

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        if (!await TagExistsAsync(connection, systemKey, tagHex))
        {
            return false;
        }

        var sets = new List<string> { "updated_at = @updated_at" };
        var parameters = new DynamicParameters();
        parameters.Add("updated_at", nowMs);
        parameters.Add("system_id", systemKey);
        parameters.Add("id", tagHex);

        if (command.Name is not null)
        {
            sets.Add("name = @name");
            parameters.Add("name", command.Name);
        }

        if (command.Color is { } color)
        {
            sets.Add("color = @color");
            parameters.Add("color", color.Value);
        }

        if (command.Description is not null)
        {
            sets.Add("description = @description");
            parameters.Add("description", command.Description);
        }

        if (command.SecurityLevel is not null)
        {
            sets.Add("security_level = @security_level");
            parameters.Add("security_level", (short)command.SecurityLevel.Value);
        }

        if (sets.Count == 1)
        {
            return true;
        }

        await connection.ExecuteAsync(
            $"""
            UPDATE tags
            SET {string.Join(", ", sets)}
            WHERE system_id = @system_id AND id = @id
            """,
            parameters);
        return true;
    }

    public async Task<bool> DeleteAsync(
        SystemId systemId,
        TagId tagId,
        CancellationToken cancellationToken = default)
    {
        var systemKey = SqliteStorageKeys.Persist(systemId);
        var tagHex = tagId.Value.ToString("N");

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        if (!await TagExistsAsync(connection, tx, systemKey, tagHex))
        {
            return false;
        }

        await connection.ExecuteAsync(
            """
            DELETE FROM alter_tags
            WHERE system_id = @system_id AND tag_id = @tag_id
            """,
            new { system_id = systemKey, tag_id = tagHex },
            tx);

        await connection.ExecuteAsync(
            """
            DELETE FROM tags
            WHERE system_id = @system_id AND id = @id
            """,
            new { system_id = systemKey, id = tagHex },
            tx);

        await tx.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> AttachAlterAsync(
        SystemId systemId,
        TagId tagId,
        AlterId alterId,
        CancellationToken cancellationToken = default)
    {
        var systemKey = SqliteStorageKeys.Persist(systemId);
        var tagHex = tagId.Value.ToString("N");
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        if (!await TagExistsAsync(connection, systemKey, tagHex))
        {
            return false;
        }

        await connection.ExecuteAsync(
            """
            INSERT INTO alter_tags (system_id, tag_id, alter_id, inserted_at, updated_at)
            VALUES (@system_id, @tag_id, @alter_id, @inserted_at, @updated_at)
            ON CONFLICT(system_id, tag_id, alter_id) DO NOTHING
            """,
            new
            {
                system_id = systemKey,
                tag_id = tagHex,
                alter_id = alterId.Value,
                inserted_at = nowMs,
                updated_at = nowMs,
            });
        return true;
    }

    public async Task<bool> DetachAlterAsync(
        SystemId systemId,
        TagId tagId,
        AlterId alterId,
        CancellationToken cancellationToken = default)
    {
        var systemKey = SqliteStorageKeys.Persist(systemId);
        var tagHex = tagId.Value.ToString("N");

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(
            """
            DELETE FROM alter_tags
            WHERE system_id = @system_id AND tag_id = @tag_id AND alter_id = @alter_id
            """,
            new
            {
                system_id = systemKey,
                tag_id = tagHex,
                alter_id = alterId.Value,
            });
        return affected > 0;
    }

    public async Task<TagId?> GetParentIdAsync(
        SystemId systemId,
        TagId tagId,
        CancellationToken cancellationToken = default)
    {
        var systemKey = SqliteStorageKeys.Persist(systemId);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var parentHex = await connection.QueryFirstOrDefaultAsync<string?>(
            """
            SELECT parent_tag_id FROM tags
            WHERE system_id = @system_id AND id = @id
            LIMIT 1
            """,
            new { system_id = systemKey, id = tagId.Value.ToString("N") });

        return parentHex is null ? null : new TagId(Guid.Parse(parentHex));
    }

    public async Task<bool> SetParentAsync(
        SystemId systemId,
        TagId tagId,
        TagId parentTagId,
        CancellationToken cancellationToken = default)
    {
        var systemKey = SqliteStorageKeys.Persist(systemId);
        var tagHex = tagId.Value.ToString("N");
        var parentHex = parentTagId.Value.ToString("N");
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        if (!await TagExistsAsync(connection, systemKey, tagHex) ||
            !await TagExistsAsync(connection, systemKey, parentHex))
        {
            return false;
        }

        await connection.ExecuteAsync(
            """
            UPDATE tags
            SET parent_tag_id = @parent_tag_id, updated_at = @updated_at
            WHERE system_id = @system_id AND id = @id
            """,
            new
            {
                parent_tag_id = parentHex,
                updated_at = nowMs,
                system_id = systemKey,
                id = tagHex,
            });
        return true;
    }

    public async Task<bool> RemoveParentAsync(
        SystemId systemId,
        TagId tagId,
        CancellationToken cancellationToken = default)
    {
        var systemKey = SqliteStorageKeys.Persist(systemId);
        var tagHex = tagId.Value.ToString("N");
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        if (!await TagExistsAsync(connection, systemKey, tagHex))
        {
            return false;
        }

        await connection.ExecuteAsync(
            """
            UPDATE tags
            SET parent_tag_id = NULL, updated_at = @updated_at
            WHERE system_id = @system_id AND id = @id
            """,
            new { updated_at = nowMs, system_id = systemKey, id = tagHex });
        return true;
    }

    public async Task<IReadOnlyList<TagReadModel>> ListAsync(
        SystemId systemId,
        CancellationToken cancellationToken = default)
    {
        var systemKey = SqliteStorageKeys.Persist(systemId);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await LoadTagRowsAsync(connection, systemKey, tagIdHex: null);
        var memberships = await LoadMembershipsAsync(connection, systemKey);

        return rows
            .OrderBy(r => r.IdHex, StringComparer.Ordinal)
            .Select(r => MapTagReadModel(r, memberships.GetValueOrDefault(r.IdHex) ?? [], systemId))
            .ToArray();
    }

    public async Task<IReadOnlyList<TagPublicReadModel>> ListGuardedAsync(
        SystemId systemId,
        SystemId? viewerSystemId,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var ownerId = SqliteStorageKeys.Normalize(systemId).Value;
        var friendshipLevel = await SqliteStorageKeys.ResolveFriendshipLevelAsync(
            systemId, viewerSystemId, _friendships, cancellationToken);
        var systemKey = SqliteStorageKeys.Persist(systemId);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await LoadTagRowsAsync(connection, systemKey, tagIdHex: null);
        var memberships = await LoadMembershipsAsync(connection, systemKey);

        var visible = new List<TagPublicReadModel>();
        foreach (var row in rows.Where(r => r.SecurityLevel.CanBeViewedBy(friendshipLevel))
                     .OrderBy(r => r.IdHex, StringComparer.Ordinal))
        {
            var alterIds = memberships.GetValueOrDefault(row.IdHex) ?? [];
            var alters = new List<BareAlter>();
            foreach (var alterId in alterIds.OrderBy(x => x.Value))
            {
                var alter = await _alterRepository.GetGuardedAsync(systemId, alterId, viewerSystemId, cancellationToken);
                if (alter is not null)
                {
                    alters.Add(alter);
                }
            }

            visible.Add(MapTagPublicReadModel(row, alters, systemId));
        }

        GuardedInstrumentation.RecordList(
            _logger, "tag", nameof(ListGuardedAsync), viewerSystemId, ownerId,
            totalCount: rows.Count, visibleCount: visible.Count, sw.Elapsed.TotalMilliseconds);
        return visible;
    }

    public async Task<TagReadModel?> GetAsync(
        SystemId systemId,
        TagId tagId,
        CancellationToken cancellationToken = default)
    {
        var systemKey = SqliteStorageKeys.Persist(systemId);
        var tagHex = tagId.Value.ToString("N");

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await LoadTagRowsAsync(connection, systemKey, tagHex);
        if (rows.Count == 0)
        {
            return null;
        }

        var alterIds = await LoadAlterIdsForTagAsync(connection, systemKey, tagHex);
        return MapTagReadModel(rows[0], alterIds, systemId);
    }

    public async Task<TagPublicReadModel?> GetGuardedAsync(
        SystemId systemId,
        TagId tagId,
        SystemId? viewerSystemId,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var ownerId = SqliteStorageKeys.Normalize(systemId).Value;
        var friendshipLevel = await SqliteStorageKeys.ResolveFriendshipLevelAsync(
            systemId, viewerSystemId, _friendships, cancellationToken);
        var systemKey = SqliteStorageKeys.Persist(systemId);
        var tagHex = tagId.Value.ToString("N");

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await LoadTagRowsAsync(connection, systemKey, tagHex);
        if (rows.Count == 0)
        {
            GuardedInstrumentation.RecordGet(
                _logger, "tag", nameof(GetGuardedAsync), viewerSystemId, ownerId,
                tagHex, found: false, filtered: false, sw.Elapsed.TotalMilliseconds);
            return null;
        }

        var row = rows[0];
        if (!row.SecurityLevel.CanBeViewedBy(friendshipLevel))
        {
            GuardedInstrumentation.RecordGet(
                _logger, "tag", nameof(GetGuardedAsync), viewerSystemId, ownerId,
                tagHex, found: false, filtered: true, sw.Elapsed.TotalMilliseconds);
            return null;
        }

        var alterIds = await LoadAlterIdsForTagAsync(connection, systemKey, tagHex);
        var alters = new List<BareAlter>();
        foreach (var alterId in alterIds.OrderBy(x => x.Value))
        {
            var alter = await _alterRepository.GetGuardedAsync(systemId, alterId, viewerSystemId, cancellationToken);
            if (alter is not null)
            {
                alters.Add(alter);
            }
        }

        var result = MapTagPublicReadModel(row, alters, systemId);
        GuardedInstrumentation.RecordGet(
            _logger, "tag", nameof(GetGuardedAsync), viewerSystemId, ownerId,
            tagHex, found: true, filtered: false, sw.Elapsed.TotalMilliseconds);
        return result;
    }

    private static async Task<bool> TagExistsAsync(
        SqliteConnection connection,
        string systemKey,
        string tagHex)
        => await TagExistsAsync(connection, tx: null, systemKey, tagHex);

    private static async Task<bool> TagExistsAsync(
        SqliteConnection connection,
        IDbTransaction? tx,
        string systemKey,
        string tagHex)
    {
        var found = await connection.ExecuteScalarAsync<long?>(
            """
            SELECT 1 FROM tags
            WHERE system_id = @system_id AND id = @id
            LIMIT 1
            """,
            new { system_id = systemKey, id = tagHex },
            tx);
        return found is not null;
    }

    private static async Task<List<TagRow>> LoadTagRowsAsync(
        SqliteConnection connection,
        string systemKey,
        string? tagIdHex)
    {
        var sql = tagIdHex is null
            ? """
                SELECT id AS IdHex, name AS Name, color AS Color, description AS Description,
                       parent_tag_id AS ParentTagIdHex, security_level AS SecurityLevel,
                       inserted_at AS InsertedAt, updated_at AS UpdatedAt
                FROM tags
                WHERE system_id = @system_id
                """
            : """
                SELECT id AS IdHex, name AS Name, color AS Color, description AS Description,
                       parent_tag_id AS ParentTagIdHex, security_level AS SecurityLevel,
                       inserted_at AS InsertedAt, updated_at AS UpdatedAt
                FROM tags
                WHERE system_id = @system_id AND id = @id
                """;

        var dtos = await connection.QueryAsync<TagRowDto>(
            sql,
            tagIdHex is null
                ? new { system_id = systemKey }
                : new { system_id = systemKey, id = tagIdHex });

        return dtos.Select(MapTagRow).ToList();
    }

    private static TagRow MapTagRow(TagRowDto dto)
        => new(
            dto.IdHex,
            dto.Name,
            HexColor.FromNullable(dto.Color),
            dto.Description,
            dto.ParentTagIdHex is null ? null : new TagId(Guid.Parse(dto.ParentTagIdHex)),
            ((short)dto.SecurityLevel).FromCode<VisibilityLevel>(),
            DateTimeOffset.FromUnixTimeMilliseconds(dto.InsertedAt).UtcDateTime,
            DateTimeOffset.FromUnixTimeMilliseconds(dto.UpdatedAt).UtcDateTime);

    private static async Task<Dictionary<string, List<AlterId>>> LoadMembershipsAsync(
        SqliteConnection connection,
        string systemKey)
    {
        var rows = await connection.QueryAsync<AlterTagRow>(
            """
            SELECT tag_id AS TagIdHex, alter_id AS AlterId
            FROM alter_tags
            WHERE system_id = @system_id
            """,
            new { system_id = systemKey });

        var result = new Dictionary<string, List<AlterId>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!result.TryGetValue(row.TagIdHex, out var list))
            {
                list = [];
                result[row.TagIdHex] = list;
            }

            list.Add(new AlterId((short)row.AlterId));
        }

        return result;
    }

    private static async Task<IReadOnlyList<AlterId>> LoadAlterIdsForTagAsync(
        SqliteConnection connection,
        string systemKey,
        string tagHex)
    {
        var alterIds = await connection.QueryAsync<long>(
            """
            SELECT alter_id
            FROM alter_tags
            WHERE system_id = @system_id AND tag_id = @tag_id
            ORDER BY alter_id
            """,
            new { system_id = systemKey, tag_id = tagHex });

        return alterIds.Select(id => new AlterId((short)id)).ToArray();
    }

    private static TagReadModel MapTagReadModel(TagRow row, IReadOnlyList<AlterId> alterIds, SystemId systemId)
        => new(
            new TagId(Guid.Parse(row.IdHex)),
            row.Name,
            row.Color,
            row.Description,
            row.ParentTagId,
            alterIds,
            row.InsertedAt,
            row.UpdatedAt,
            row.SecurityLevel,
            systemId);

    private static TagPublicReadModel MapTagPublicReadModel(TagRow row, IReadOnlyList<BareAlter> alters, SystemId systemId)
        => new(
            new TagId(Guid.Parse(row.IdHex)),
            row.Name,
            row.Color,
            row.Description,
            row.ParentTagId,
            alters,
            row.InsertedAt,
            row.UpdatedAt,
            row.SecurityLevel,
            systemId);

    private sealed record TagRowDto(
        string IdHex,
        string Name,
        string? Color,
        string? Description,
        string? ParentTagIdHex,
        long SecurityLevel,
        long InsertedAt,
        long UpdatedAt);

    private sealed record AlterTagRow(string TagIdHex, long AlterId);

    private sealed record TagRow(
        string IdHex,
        string Name,
        HexColor? Color,
        string? Description,
        TagId? ParentTagId,
        VisibilityLevel SecurityLevel,
        DateTime InsertedAt,
        DateTime UpdatedAt);
}
