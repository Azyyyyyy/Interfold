using System.Diagnostics;
using Interfold.Alters.Contracts.Models;
using Interfold.Alters.Domain.Abstractions.Repository;
using Interfold.Friendships.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Domain.Abstractions;
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
    private readonly IRegionContext _regionContext;
    private readonly IFriendshipRepository? _friendships;
    private readonly IAlterRepository _alterRepository;
    private readonly ILogger<SqliteTagRepository> _logger;
    private readonly TimeProvider _timeProvider;

    public SqliteTagRepository(
        ISqliteConnectionFactory connectionFactory,
        IRegionContext regionContext,
        IFriendshipRepository friendships,
        IAlterRepository alterRepository,
        ILogger<SqliteTagRepository> logger,
        TimeProvider? timeProvider = null)
    {
        _connectionFactory = connectionFactory;
        _regionContext = regionContext;
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
        var systemKey = SqliteStorageKeys.ForSystem(_regionContext, systemId).Value;
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var insertedAtMs = new DateTimeOffset(DateTime.SpecifyKind(command.InsertedAtUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        string? parentId = null;
        if (command.ParentTagId is { } parent && parent != TagId.Empty)
        {
            if (!await TagExistsAsync(connection, systemKey, parent.Value.ToString("N"), cancellationToken))
            {
                return null;
            }

            parentId = parent.Value.ToString("N");
        }

        var tagGuid = Guid.NewGuid();
        var tagIdHex = tagGuid.ToString("N");

        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO tags (
                system_id, id, parent_tag_id, name, description, color,
                security_level, inserted_at, updated_at)
            VALUES (
                $system_id, $id, $parent_tag_id, $name, NULL, NULL,
                $security_level, $inserted_at, $updated_at)
            """;
        insert.Parameters.AddWithValue("$system_id", systemKey);
        insert.Parameters.AddWithValue("$id", tagIdHex);
        insert.Parameters.AddWithValue("$parent_tag_id", (object?)parentId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$name", command.Name);
        insert.Parameters.AddWithValue("$security_level", VisibilityLevel.Private.ToWire());
        insert.Parameters.AddWithValue("$inserted_at", insertedAtMs);
        insert.Parameters.AddWithValue("$updated_at", nowMs);
        await insert.ExecuteNonQueryAsync(cancellationToken);

        return new TagId(tagGuid);
    }

    public async Task<bool> ExistsAsync(
        SystemId systemId,
        TagId tagId,
        CancellationToken cancellationToken = default)
    {
        var systemKey = SqliteStorageKeys.ForSystem(_regionContext, systemId).Value;
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        return await TagExistsAsync(connection, systemKey, tagId.Value.ToString("N"), cancellationToken);
    }

    public async Task<bool> UpdateAsync(
        SystemId systemId,
        UpdateTagCommand command,
        CancellationToken cancellationToken = default)
    {
        var systemKey = SqliteStorageKeys.ForSystem(_regionContext, systemId).Value;
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var tagHex = command.TagId.Value.ToString("N");

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        if (!await TagExistsAsync(connection, systemKey, tagHex, cancellationToken))
        {
            return false;
        }

        var sets = new List<string> { "updated_at = $updated_at" };
        await using var update = connection.CreateCommand();
        update.Parameters.AddWithValue("$updated_at", nowMs);
        update.Parameters.AddWithValue("$system_id", systemKey);
        update.Parameters.AddWithValue("$id", tagHex);

        if (command.Name is not null)
        {
            sets.Add("name = $name");
            update.Parameters.AddWithValue("$name", command.Name);
        }

        if (command.Color is { } color)
        {
            sets.Add("color = $color");
            update.Parameters.AddWithValue("$color", color.Value);
        }

        if (command.Description is not null)
        {
            sets.Add("description = $description");
            update.Parameters.AddWithValue("$description", command.Description);
        }

        if (command.SecurityLevel is not null)
        {
            sets.Add("security_level = $security_level");
            update.Parameters.AddWithValue("$security_level", command.SecurityLevel.Value.ToWire());
        }

        if (sets.Count == 1)
        {
            return true;
        }

        update.CommandText = $"""
            UPDATE tags
            SET {string.Join(", ", sets)}
            WHERE system_id = $system_id AND id = $id
            """;
        await update.ExecuteNonQueryAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteAsync(
        SystemId systemId,
        TagId tagId,
        CancellationToken cancellationToken = default)
    {
        var systemKey = SqliteStorageKeys.ForSystem(_regionContext, systemId).Value;
        var tagHex = tagId.Value.ToString("N");

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        if (!await TagExistsAsync(connection, tx, systemKey, tagHex, cancellationToken))
        {
            return false;
        }

        await using (var deleteMembership = connection.CreateCommand())
        {
            deleteMembership.Transaction = tx;
            deleteMembership.CommandText = """
                DELETE FROM alter_tags
                WHERE system_id = $system_id AND tag_id = $tag_id
                """;
            deleteMembership.Parameters.AddWithValue("$system_id", systemKey);
            deleteMembership.Parameters.AddWithValue("$tag_id", tagHex);
            await deleteMembership.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteTag = connection.CreateCommand())
        {
            deleteTag.Transaction = tx;
            deleteTag.CommandText = """
                DELETE FROM tags
                WHERE system_id = $system_id AND id = $id
                """;
            deleteTag.Parameters.AddWithValue("$system_id", systemKey);
            deleteTag.Parameters.AddWithValue("$id", tagHex);
            await deleteTag.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> AttachAlterAsync(
        SystemId systemId,
        TagId tagId,
        AlterId alterId,
        CancellationToken cancellationToken = default)
    {
        var systemKey = SqliteStorageKeys.ForSystem(_regionContext, systemId).Value;
        var tagHex = tagId.Value.ToString("N");
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        if (!await TagExistsAsync(connection, systemKey, tagHex, cancellationToken))
        {
            return false;
        }

        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO alter_tags (system_id, tag_id, alter_id, inserted_at, updated_at)
            VALUES ($system_id, $tag_id, $alter_id, $inserted_at, $updated_at)
            ON CONFLICT(system_id, tag_id, alter_id) DO NOTHING
            """;
        insert.Parameters.AddWithValue("$system_id", systemKey);
        insert.Parameters.AddWithValue("$tag_id", tagHex);
        insert.Parameters.AddWithValue("$alter_id", alterId.Value);
        insert.Parameters.AddWithValue("$inserted_at", nowMs);
        insert.Parameters.AddWithValue("$updated_at", nowMs);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DetachAlterAsync(
        SystemId systemId,
        TagId tagId,
        AlterId alterId,
        CancellationToken cancellationToken = default)
    {
        var systemKey = SqliteStorageKeys.ForSystem(_regionContext, systemId).Value;
        var tagHex = tagId.Value.ToString("N");

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM alter_tags
            WHERE system_id = $system_id AND tag_id = $tag_id AND alter_id = $alter_id
            """;
        command.Parameters.AddWithValue("$system_id", systemKey);
        command.Parameters.AddWithValue("$tag_id", tagHex);
        command.Parameters.AddWithValue("$alter_id", alterId.Value);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return affected > 0;
    }

    public async Task<TagId?> GetParentIdAsync(
        SystemId systemId,
        TagId tagId,
        CancellationToken cancellationToken = default)
    {
        var systemKey = SqliteStorageKeys.ForSystem(_regionContext, systemId).Value;
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT parent_tag_id FROM tags
            WHERE system_id = $system_id AND id = $id
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$system_id", systemKey);
        command.Parameters.AddWithValue("$id", tagId.Value.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0))
        {
            return null;
        }

        return new TagId(Guid.Parse(reader.GetString(0)));
    }

    public async Task<bool> SetParentAsync(
        SystemId systemId,
        TagId tagId,
        TagId parentTagId,
        CancellationToken cancellationToken = default)
    {
        var systemKey = SqliteStorageKeys.ForSystem(_regionContext, systemId).Value;
        var tagHex = tagId.Value.ToString("N");
        var parentHex = parentTagId.Value.ToString("N");
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        if (!await TagExistsAsync(connection, systemKey, tagHex, cancellationToken) ||
            !await TagExistsAsync(connection, systemKey, parentHex, cancellationToken))
        {
            return false;
        }

        await using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE tags
            SET parent_tag_id = $parent_tag_id, updated_at = $updated_at
            WHERE system_id = $system_id AND id = $id
            """;
        update.Parameters.AddWithValue("$parent_tag_id", parentHex);
        update.Parameters.AddWithValue("$updated_at", nowMs);
        update.Parameters.AddWithValue("$system_id", systemKey);
        update.Parameters.AddWithValue("$id", tagHex);
        await update.ExecuteNonQueryAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RemoveParentAsync(
        SystemId systemId,
        TagId tagId,
        CancellationToken cancellationToken = default)
    {
        var systemKey = SqliteStorageKeys.ForSystem(_regionContext, systemId).Value;
        var tagHex = tagId.Value.ToString("N");
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        if (!await TagExistsAsync(connection, systemKey, tagHex, cancellationToken))
        {
            return false;
        }

        await using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE tags
            SET parent_tag_id = NULL, updated_at = $updated_at
            WHERE system_id = $system_id AND id = $id
            """;
        update.Parameters.AddWithValue("$updated_at", nowMs);
        update.Parameters.AddWithValue("$system_id", systemKey);
        update.Parameters.AddWithValue("$id", tagHex);
        await update.ExecuteNonQueryAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<TagReadModel>> ListAsync(
        SystemId systemId,
        CancellationToken cancellationToken = default)
    {
        var systemKey = SqliteStorageKeys.ForSystem(_regionContext, systemId).Value;
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await LoadTagRowsAsync(connection, systemKey, tagIdHex: null, cancellationToken);
        var memberships = await LoadMembershipsAsync(connection, systemKey, cancellationToken);

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
        var systemKey = SqliteStorageKeys.ForSystem(_regionContext, systemId).Value;

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await LoadTagRowsAsync(connection, systemKey, tagIdHex: null, cancellationToken);
        var memberships = await LoadMembershipsAsync(connection, systemKey, cancellationToken);

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
        var systemKey = SqliteStorageKeys.ForSystem(_regionContext, systemId).Value;
        var tagHex = tagId.Value.ToString("N");

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await LoadTagRowsAsync(connection, systemKey, tagHex, cancellationToken);
        if (rows.Count == 0)
        {
            return null;
        }

        var alterIds = await LoadAlterIdsForTagAsync(connection, systemKey, tagHex, cancellationToken);
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
        var systemKey = SqliteStorageKeys.ForSystem(_regionContext, systemId).Value;
        var tagHex = tagId.Value.ToString("N");

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await LoadTagRowsAsync(connection, systemKey, tagHex, cancellationToken);
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

        var alterIds = await LoadAlterIdsForTagAsync(connection, systemKey, tagHex, cancellationToken);
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
        string tagHex,
        CancellationToken cancellationToken)
        => await TagExistsAsync(connection, tx: null, systemKey, tagHex, cancellationToken);

    private static async Task<bool> TagExistsAsync(
        SqliteConnection connection,
        SqliteTransaction? tx,
        string systemKey,
        string tagHex,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT 1 FROM tags
            WHERE system_id = $system_id AND id = $id
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$system_id", systemKey);
        command.Parameters.AddWithValue("$id", tagHex);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken);
    }

    private static async Task<List<TagRow>> LoadTagRowsAsync(
        SqliteConnection connection,
        string systemKey,
        string? tagIdHex,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = tagIdHex is null
            ? """
                SELECT id, name, color, description, parent_tag_id, security_level, inserted_at, updated_at
                FROM tags
                WHERE system_id = $system_id
                """
            : """
                SELECT id, name, color, description, parent_tag_id, security_level, inserted_at, updated_at
                FROM tags
                WHERE system_id = $system_id AND id = $id
                """;
        command.Parameters.AddWithValue("$system_id", systemKey);
        if (tagIdHex is not null)
        {
            command.Parameters.AddWithValue("$id", tagIdHex);
        }

        var rows = new List<TagRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadTagRow(reader));
        }

        return rows;
    }

    private static async Task<Dictionary<string, List<AlterId>>> LoadMembershipsAsync(
        SqliteConnection connection,
        string systemKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT tag_id, alter_id
            FROM alter_tags
            WHERE system_id = $system_id
            """;
        command.Parameters.AddWithValue("$system_id", systemKey);

        var result = new Dictionary<string, List<AlterId>>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var tagHex = reader.GetString(0);
            if (!result.TryGetValue(tagHex, out var list))
            {
                list = [];
                result[tagHex] = list;
            }

            list.Add(new AlterId((short)reader.GetInt64(1)));
        }

        return result;
    }

    private static async Task<IReadOnlyList<AlterId>> LoadAlterIdsForTagAsync(
        SqliteConnection connection,
        string systemKey,
        string tagHex,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT alter_id
            FROM alter_tags
            WHERE system_id = $system_id AND tag_id = $tag_id
            ORDER BY alter_id
            """;
        command.Parameters.AddWithValue("$system_id", systemKey);
        command.Parameters.AddWithValue("$tag_id", tagHex);

        var list = new List<AlterId>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new AlterId((short)reader.GetInt64(0)));
        }

        return list;
    }

    private static TagRow ReadTagRow(SqliteDataReader reader)
    {
        var securityWire = reader.GetString(reader.GetOrdinal("security_level"));
        if (!securityWire.TryParseWire(out VisibilityLevel securityLevel))
        {
            throw new InvalidOperationException($"Corrupt tag security_level wire value '{securityWire}'.");
        }

        var parentOrdinal = reader.GetOrdinal("parent_tag_id");
        return new TagRow(
            IdHex: reader.GetString(reader.GetOrdinal("id")),
            Name: reader.GetString(reader.GetOrdinal("name")),
            Color: HexColor.FromNullable(
                reader.IsDBNull(reader.GetOrdinal("color")) ? null : reader.GetString(reader.GetOrdinal("color"))),
            Description: reader.IsDBNull(reader.GetOrdinal("description"))
                ? null
                : reader.GetString(reader.GetOrdinal("description")),
            ParentTagId: reader.IsDBNull(parentOrdinal)
                ? null
                : new TagId(Guid.Parse(reader.GetString(parentOrdinal))),
            SecurityLevel: securityLevel,
            InsertedAt: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(reader.GetOrdinal("inserted_at"))).UtcDateTime,
            UpdatedAt: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(reader.GetOrdinal("updated_at"))).UtcDateTime);
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
