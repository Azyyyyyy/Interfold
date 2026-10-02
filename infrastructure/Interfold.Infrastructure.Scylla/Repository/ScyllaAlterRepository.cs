using System.Collections.Concurrent;
using System.Diagnostics;
using Cassandra;
using Interfold.Alters.Contracts.Abstractions;
using Interfold.Alters.Contracts.Models;
using Interfold.Alters.Contracts.Models.Commands;
using Interfold.Alters.Domain;
using Interfold.Alters.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Domain.Abstractions.Repository;
using Interfold.Shared.Domain.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Interfold.Infrastructure.Scylla.Repository;

public sealed class ScyllaAlterRepository : IAlterRepository
{
    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IScyllaScopeResolver _scopeResolver;
    private readonly IScyllaKeyspaceResolver _keyspaceResolver;
    private readonly ISettingsFieldRepository _settingsFields;
    private readonly IAlterFieldDefinitions _alterFieldDefinitions;
    private readonly PersistenceConfiguration _options;
    private readonly ILogger<ScyllaAlterRepository> _logger;
    private static readonly ConcurrentDictionary<(int ClusterId, string Keyspace), byte> UdtMappings = new();

    public ScyllaAlterRepository(
        IScyllaSessionProvider sessionProvider,
        IScyllaScopeResolver scopeResolver,
        IScyllaKeyspaceResolver keyspaceResolver,
        ISettingsFieldRepository settingsFields,
        IAlterFieldDefinitions alterFieldDefinitions,
        IOptions<PersistenceConfiguration> options,
        ILogger<ScyllaAlterRepository> logger
    )
    {
        _sessionProvider = sessionProvider;
        _scopeResolver = scopeResolver;
        _keyspaceResolver = keyspaceResolver;
        _settingsFields = settingsFields;
        _alterFieldDefinitions = alterFieldDefinitions;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<AlterId?> CreateAsync(SystemId systemId, CreateAlterCommand command, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync<AlterId?>(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;

            var nextIdQuery = new SimpleStatement(
                $"SELECT id FROM {keyspace}.alters WHERE user_id = ? ORDER BY id DESC LIMIT 1",
                normalizedSystemId
            );

            var rows = await session.ExecuteAsync(nextIdQuery);
            var current = rows.FirstOrDefault()?.GetValue<short>("id") ?? (short)0;
            var next = (short)(current + 1);
            var createdAt = command.CreatedAt.ToUniversalTime();

            // Always stamp security_level — the strict read path rejects null.
            var insert = new SimpleStatement(
                $"INSERT INTO {keyspace}.alters (user_id, id, name, alias, security_level, inserted_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?)",
                normalizedSystemId,
                next,
                command.Name,
                null,
                (short)VisibilityLevel.Private,
                createdAt,
                createdAt
            );

            await session.ExecuteAsync(insert);
            return new(next);
        }, cancellationToken);
    }

    public async Task<bool> ExistsAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, scope => ExistsAsync(scope, alterId), cancellationToken);
    }

    private static Task<bool> ExistsAsync(ScyllaScope scope, AlterId alterId)
        => ScyllaExistsQueries.RowExistsAsync(scope.Session, scope.Keyspace, "alters", "id", scope.NormalizedSystemId, alterId.Value);

    public async Task<bool> UpdateAsync(SystemId systemId, UpdateAlterCommand command, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;
            var updatedAt = command.UpdatedAt.ToUniversalTime();

            var batch = new BatchStatement();

            var exists = await ExistsAsync(scope, command.AlterId);
            if (!exists)
            {
                return false;
            }

            UpdateIfNotNull(batch, keyspace, command, "name", command.Name, normalizedSystemId, updatedAt);
            UpdateIfNotNull(batch, keyspace, command, "description", command.Description, normalizedSystemId, updatedAt);
            if (command.ClearAvatar)
            {
                batch.Add(new SimpleStatement(
                    $"UPDATE {keyspace}.alters SET avatar_url = ?, avatar_source = ?, updated_at = ? WHERE user_id = ? AND id = ?",
                    null,
                    null,
                    updatedAt,
                    normalizedSystemId,
                    command.AlterId.Value
                ));
            }
            else if (command.AvatarUrl is { } avatarUrl)
            {
                // avatar_url + avatar_source move together; domain handler rejects half-set.
                var sourceShort = (short)(command.AvatarSource ?? AvatarSource.Local);
                batch.Add(new SimpleStatement(
                    $"UPDATE {keyspace}.alters SET avatar_url = ?, avatar_source = ?, updated_at = ? WHERE user_id = ? AND id = ?",
                    avatarUrl.Value,
                    sourceShort,
                    updatedAt,
                    normalizedSystemId,
                    command.AlterId.Value
                ));
            }
            UpdateIfNotNull(batch, keyspace, command, "color", command.Color?.Value, normalizedSystemId, updatedAt);
            UpdateIfNotNull(batch, keyspace, command, "pronouns", command.Pronouns, normalizedSystemId, updatedAt);
            UpdateIfNotNull(batch, keyspace, command, "security_level", (short?)command.SecurityLevel, normalizedSystemId, updatedAt);

            if (command.Fields is not null)
            {
                EnsureAlterFieldUdtMapping(session, keyspace);

                var currentRow = (await session.ExecuteAsync(new SimpleStatement(
                    $"SELECT fields FROM {keyspace}.alters WHERE user_id = ? AND id = ? LIMIT 1",
                    normalizedSystemId,
                    command.AlterId.Value
                ))).FirstOrDefault();

                var merged = (currentRow?.GetValue<IEnumerable<AlterFieldUdt>?>("fields") ?? [])
                    .ToDictionary(x => x.Id, x => x.Value);

                foreach (var f in command.Fields)
                {
                    merged[f.Id] = f.Value;
                }

                var udts = merged
                    .Select(kvp => new AlterFieldUdt { Id = kvp.Key, Value = kvp.Value })
                    .ToList();

                batch.Add(new SimpleStatement(
                    $"UPDATE {keyspace}.alters SET fields = ?, updated_at = ? WHERE user_id = ? AND id = ?",
                    udts,
                    updatedAt,
                    normalizedSystemId,
                    command.AlterId.Value
                ));
            }

            UpdateIfNotNull(batch, keyspace, command, "proxy_name", command.ProxyName, normalizedSystemId, updatedAt);
            UpdateIfNotNull(batch, keyspace, command, "untracked", command.Untracked, normalizedSystemId, updatedAt);
            UpdateIfNotNull(batch, keyspace, command, "archived", command.Archived, normalizedSystemId, updatedAt);
            UpdateIfNotNull(batch, keyspace, command, "pinned", command.Pinned, normalizedSystemId, updatedAt);

            if (command.Alias is not null)
            {
                var oldAliasRow = (await session.ExecuteAsync(new SimpleStatement(
                    $"SELECT alias FROM {keyspace}.alters WHERE user_id = ? AND id = ? LIMIT 1",
                    normalizedSystemId, command.AlterId.Value))).FirstOrDefault();
                var oldAlias = oldAliasRow?.GetValue<string?>("alias");

                batch.Add(new SimpleStatement(
                    $"UPDATE {keyspace}.alters SET alias = ?, updated_at = ? WHERE user_id = ? AND id = ?",
                    command.Alias, updatedAt, normalizedSystemId, command.AlterId.Value));

                if (!string.IsNullOrWhiteSpace(oldAlias))
                {
                    batch.Add(new SimpleStatement(
                        $"DELETE FROM {keyspace}.alters_by_alias WHERE user_id = ? AND alias = ?",
                        normalizedSystemId, oldAlias));
                }

                var newAlias = command.Alias as string;
                if (!string.IsNullOrWhiteSpace(newAlias))
                {
                    batch.Add(new SimpleStatement(
                        $"INSERT INTO {keyspace}.alters_by_alias (user_id, alias, alter_id) VALUES (?, ?, ?)",
                        normalizedSystemId, newAlias, command.AlterId.Value));
                }
            }

            await session.ExecuteAsync(batch);

            return true;
        }, cancellationToken);
    }

    private void UpdateIfNotNull(BatchStatement batch, string keyspace, UpdateAlterCommand command, string field, object? value, string normalizedSystemId, DateTimeOffset updatedAt)
    {
        if (value is not null)
        {
            batch.Add(new SimpleStatement(
                    $"UPDATE {keyspace}.alters SET {field} = ?, updated_at = ? WHERE user_id = ? AND id = ?",
                    value,
                    updatedAt,
                    normalizedSystemId,
                    command.AlterId.Value
                ));
        }
    }

    public async Task<bool> DeleteAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;
            var alterIdShort = alterId.Value;

            if (!await ExistsAsync(scope, alterId))
                return false;

            var aliasRow = (await session.ExecuteAsync(new SimpleStatement(
                $"SELECT alias FROM {keyspace}.alters WHERE user_id = ? AND id = ? LIMIT 1",
                normalizedSystemId,
                alterIdShort))).FirstOrDefault();
            var alias = aliasRow?.GetValue<string?>("alias");

            await session.ExecuteAsync(new SimpleStatement(
                $"DELETE FROM {keyspace}.alters WHERE user_id = ? AND id = ?",
                normalizedSystemId,
                alterIdShort));

            if (!string.IsNullOrWhiteSpace(alias))
            {
                await session.ExecuteAsync(new SimpleStatement(
                    $"DELETE FROM {keyspace}.alters_by_alias WHERE user_id = ? AND alias = ?",
                    normalizedSystemId,
                    alias));
            }

            return true;
        }, cancellationToken);
    }

    public async Task RemoveFieldValuesAsync(SystemId systemId, FieldId fieldId, CancellationToken cancellationToken = default)
    {
        await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;
            EnsureAlterFieldUdtMapping(session, keyspace);

            var rows = await session.ExecuteAsync(new SimpleStatement(
                $"SELECT id, fields FROM {keyspace}.alters WHERE user_id = ?",
                normalizedSystemId));

            var batch = new BatchStatement();
            var any = false;
            foreach (var row in rows)
            {
                var alterIdShort = row.GetValue<short>("id");
                var currentFields = row.GetValue<IEnumerable<AlterFieldUdt>?>("fields")?.ToList();
                if (currentFields is null || currentFields.Count == 0)
                    continue;

                if (currentFields.RemoveAll(x => x.Id == fieldId.Value) == 0)
                    continue;

                any = true;
                batch.Add(new SimpleStatement(
                    $"UPDATE {keyspace}.alters SET fields = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ?",
                    currentFields,
                    normalizedSystemId,
                    alterIdShort));
            }

            if (any)
                await session.ExecuteAsync(batch);

            return true;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<AlterReadModel>> ListAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;

            var definitions = await _settingsFields.ListAsync(systemId, cancellationToken);
            EnsureAlterFieldUdtMapping(session, keyspace);

            var query = new SimpleStatement(
                $"SELECT id, name, alias, fields, security_level, color, pronouns, avatar_url, avatar_source, pinned, archived, untracked, description, proxy_name, discord_proxies, inserted_at, updated_at FROM {keyspace}.alters WHERE user_id = ?",
                normalizedSystemId
            );

            var rows = await session.ExecuteAsync(query);
            return rows
                .Select(row => AlterRowMappers.MapAlterReadModel(row, definitions))
                .OrderBy(x => x.Id.Value)
                .ToArray();
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<BareAlter>> ListGuardedAsync(
        SystemId systemId,
        SystemId? viewerSystemId,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var result = await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;
            var friendshipLevel = await ScyllaSharedQueries.ResolveFriendshipLevelAsync(session, _keyspaceResolver, new(normalizedSystemId), viewerSystemId, _logger);
            EnsureAlterFieldUdtMapping(session, keyspace);
            var definitions = await _alterFieldDefinitions.ListVisibleAsync(systemId, friendshipLevel, cancellationToken);

            var query = new SimpleStatement(
                $"SELECT id, name, avatar_url, avatar_source, color, description, pronouns, pinned, security_level, fields FROM {keyspace}.alters WHERE user_id = ?",
                normalizedSystemId
            );

            var rows = (await session.ExecuteAsync(query)).ToArray();
            var visible = rows
                .Where(row => row.GetValue<short?>("security_level").FromStorage().CanBeViewedBy(friendshipLevel))
                .Select(row => AlterRowMappers.MapBareAlter(row, definitions))
                .OrderBy(x => x.Id.Value)
                .ToArray();

            return (Total: rows.Length, Visible: (IReadOnlyList<BareAlter>)visible, NormalizedSystemId: normalizedSystemId);
        }, cancellationToken);

        GuardedInstrumentation.RecordList(_logger, "alter", nameof(ListGuardedAsync), viewerSystemId, result.NormalizedSystemId, result.Total, result.Visible.Count, sw.Elapsed.TotalMilliseconds);
        return result.Visible;
    }

    public async Task<AlterReadModel?> GetAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;

            var definitions = await _settingsFields.ListAsync(systemId, cancellationToken);
            EnsureAlterFieldUdtMapping(session, keyspace);

            var query = new SimpleStatement(
                $"SELECT id, name, alias, fields, security_level, color, pronouns, avatar_url, avatar_source, pinned, archived, untracked, description, proxy_name, discord_proxies, inserted_at, updated_at FROM {keyspace}.alters WHERE user_id = ? AND id = ? LIMIT 1",
                normalizedSystemId,
                alterId.Value
            );

            var row = (await session.ExecuteAsync(query)).FirstOrDefault();
            return row is null
                ? null
                : AlterRowMappers.MapAlterReadModel(row, definitions);
        }, cancellationToken);
    }

    public async Task<BareAlter?> GetGuardedAsync(
        SystemId systemId,
        AlterId alterId,
        SystemId? viewerSystemId,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var result = await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;
            var friendshipLevel = await ScyllaSharedQueries.ResolveFriendshipLevelAsync(session, _keyspaceResolver, new(normalizedSystemId), viewerSystemId, _logger);
            EnsureAlterFieldUdtMapping(session, keyspace);
            var definitions = await _alterFieldDefinitions.ListVisibleAsync(systemId, friendshipLevel, cancellationToken);

            var query = new SimpleStatement(
                $"SELECT id, name, avatar_url, avatar_source, description, color, pronouns, pinned, security_level, fields FROM {keyspace}.alters WHERE user_id = ? AND id = ? LIMIT 1",
                normalizedSystemId,
                alterId.Value
            );

            var row = (await session.ExecuteAsync(query)).FirstOrDefault();
            if (row is null)
            {
                return (Alter: (BareAlter?)null, Filtered: false, NormalizedSystemId: normalizedSystemId);
            }

            var securityLevel = row.GetValue<short?>("security_level").FromStorage();
            if (!securityLevel.CanBeViewedBy(friendshipLevel))
            {
                return (Alter: (BareAlter?)null, Filtered: true, NormalizedSystemId: normalizedSystemId);
            }

            return (Alter: (BareAlter?)AlterRowMappers.MapBareAlter(row, definitions), Filtered: false, NormalizedSystemId: normalizedSystemId);
        }, cancellationToken);

        GuardedInstrumentation.RecordGet(_logger, "alter", nameof(GetGuardedAsync), viewerSystemId, result.NormalizedSystemId, alterId.Value.ToString(), found: result.Alter is not null, filtered: result.Filtered, sw.Elapsed.TotalMilliseconds);
        return result.Alter;
    }

    public async Task<bool> AliasTakenByOtherAsync(
        SystemId systemId,
        AlterId alterId,
        string alias,
        CancellationToken cancellationToken = default
    )
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;

            var query = new SimpleStatement(
                $"SELECT id, alias FROM {keyspace}.alters WHERE user_id = ? AND alias = ?",
                normalizedSystemId,
                alias
            );

            var rows = await session.ExecuteAsync(query);
            return rows.Any(row => row.GetValue<short>("id") != alterId.Value);
        }, cancellationToken);
    }

    public static void EnsureAlterFieldUdtMapping(ISession session, string keyspace)
    {
        var key = (System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(session.Cluster), keyspace);
        if (UdtMappings.ContainsKey(key))
        {
            return;
        }

        session.UserDefinedTypes.Define(
            UdtMap.For<AlterFieldUdt>("alter_field", keyspace)
                .Map(f => f.Id, "id")
                .Map(f => f.Value, "value"));

        UdtMappings.TryAdd(key, 0);
    }

}
