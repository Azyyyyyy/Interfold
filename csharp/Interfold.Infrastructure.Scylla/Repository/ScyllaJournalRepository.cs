using Cassandra;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Models.Read;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace Interfold.Infrastructure.Scylla.Repository;

public sealed class ScyllaJournalRepository : IJournalRepository
{
    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IScyllaScopeResolver _scopeResolver;
    private readonly IScyllaKeyspaceResolver _keyspaceResolver;
    private readonly PersistenceConfiguration _options;

    public ScyllaJournalRepository(
        IScyllaSessionProvider sessionProvider,
        IScyllaScopeResolver scopeResolver,
        IScyllaKeyspaceResolver keyspaceResolver,
        IOptions<PersistenceConfiguration> options)
    {
        _sessionProvider = sessionProvider;
        _scopeResolver = scopeResolver;
        _keyspaceResolver = keyspaceResolver;
        _options = options.Value;
    }

    public async Task<EntryId?> CreateGlobalAsync(SystemId systemId, CreateGlobalJournalEntryCommand command, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync<EntryId?>(systemId, async scope =>
        {
            var session = scope.Session;
            var normalizedSystemId = scope.NormalizedSystemId;
            var keyspace = scope.Keyspace;
            var entryId = Guid.NewGuid();

            var insert = new SimpleStatement(
                $"INSERT INTO {keyspace}.global_journals (user_id, id, title, content, color, pinned, locked, inserted_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?, toTimestamp(now()), toTimestamp(now()))",
                normalizedSystemId,
                entryId,
                command.Title,
                null,
                null,
                false,
                false
            );

            await session.ExecuteAsync(insert);
            return new(entryId);
        }, cancellationToken);
    }

    public async Task<bool> ExistsGlobalAsync(SystemId systemId, EntryId entryId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var session = scope.Session;
            var normalizedSystemId = scope.NormalizedSystemId;
            var keyspace = scope.Keyspace;

            var query = new SimpleStatement(
                $"SELECT id FROM {keyspace}.global_journals WHERE user_id = ? AND id = ? LIMIT 1",
                normalizedSystemId,
                entryId.Value
            );

            var rows = await session.ExecuteAsync(query);
            return rows.Any();
        }, cancellationToken);
    }

    public async Task<bool> UpdateGlobalAsync(SystemId systemId, UpdateGlobalJournalEntryCommand command, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var session = scope.Session;
            var normalizedSystemId = scope.NormalizedSystemId;
            var keyspace = scope.Keyspace;

            var exists = await ExistsGlobalAsync(systemId, command.EntryId, cancellationToken);
            if (!exists)
                return false;

            var updateBatch = new BatchStatement();

            if (command.Title is not null)
            {
                var q = new SimpleStatement(
                    $"UPDATE {keyspace}.global_journals SET title = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ?",
                    command.Title,
                    normalizedSystemId,
                    command.EntryId.Value
                );
                updateBatch.Add(q);
            }

            if (command.Content is not null)
            {
                var q = new SimpleStatement(
                    $"UPDATE {keyspace}.global_journals SET content = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ?",
                    command.Content,
                    normalizedSystemId,
                    command.EntryId.Value
                );
                updateBatch.Add(q);
            }

            if (command.Color is { } color)
            {
                var q = new SimpleStatement(
                    $"UPDATE {keyspace}.global_journals SET color = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ?",
                    color.Value,
                    normalizedSystemId,
                    command.EntryId.Value
                );
                updateBatch.Add(q);
            }

            if (!updateBatch.IsEmpty)
            {
                await session.ExecuteAsync(updateBatch);
            }

            return true;
        }, cancellationToken);
    }

    public async Task<bool> DeleteGlobalAsync(SystemId systemId, EntryId entryId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var session = scope.Session;
            var normalizedSystemId = scope.NormalizedSystemId;
            var keyspace = scope.Keyspace;

            var exists = await ExistsGlobalAsync(systemId, entryId, cancellationToken);
            if (!exists)
                return false;

            var deleteBatch = new BatchStatement();
            deleteBatch.Add(new SimpleStatement(
                $"DELETE FROM {keyspace}.global_journals WHERE user_id = ? AND id = ?",
                normalizedSystemId,
                entryId.Value
            ));
            deleteBatch.Add(new SimpleStatement(
                $"DELETE FROM {keyspace}.global_journal_alters WHERE user_id = ? AND global_journal_id = ?",
                normalizedSystemId,
                entryId.Value
            ));
            await session.ExecuteAsync(deleteBatch);

            return true;
        }, cancellationToken);
    }

    public async Task<bool> SetGlobalLockedAsync(SystemId systemId, EntryId entryId, bool locked, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var session = scope.Session;
            var normalizedSystemId = scope.NormalizedSystemId;
            var keyspace = scope.Keyspace;

            var exists = await ExistsGlobalAsync(systemId, entryId, cancellationToken);
            if (!exists)
                return false;

            var upsert = new SimpleStatement(
                $"UPDATE {keyspace}.global_journals SET locked = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ?",
                locked,
                normalizedSystemId,
                entryId.Value
            );
            await session.ExecuteAsync(upsert);

            return true;
        }, cancellationToken);
    }

    public async Task<bool> SetGlobalPinnedAsync(SystemId systemId, EntryId entryId, bool pinned, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var session = scope.Session;
            var normalizedSystemId = scope.NormalizedSystemId;
            var keyspace = scope.Keyspace;

            var exists = await ExistsGlobalAsync(systemId, entryId, cancellationToken);
            if (!exists)
                return false;

            var upsert = new SimpleStatement(
                $"UPDATE {keyspace}.global_journals SET pinned = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ?",
                pinned,
                normalizedSystemId,
                entryId.Value
            );
            await session.ExecuteAsync(upsert);

            return true;
        }, cancellationToken);
    }

    public async Task<bool> AttachGlobalAlterAsync(SystemId systemId, EntryId entryId, AlterId alterId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var session = scope.Session;
            var normalizedSystemId = scope.NormalizedSystemId;
            var keyspace = scope.Keyspace;

            var exists = await ExistsGlobalAsync(systemId, entryId, cancellationToken);
            if (!exists)
                return false;

            var insert = new SimpleStatement(
                $"INSERT INTO {keyspace}.global_journal_alters (user_id, global_journal_id, alter_id, inserted_at, updated_at) VALUES (?, ?, ?, toTimestamp(now()), toTimestamp(now()))",
                normalizedSystemId,
                entryId.Value,
                alterId.Value
            );
            await session.ExecuteAsync(insert);

            return true;
        }, cancellationToken);
    }

    public async Task<bool> DetachGlobalAlterAsync(SystemId systemId, EntryId entryId, AlterId alterId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var session = scope.Session;
            var normalizedSystemId = scope.NormalizedSystemId;
            var keyspace = scope.Keyspace;

            var edgeExistsQuery = new SimpleStatement(
                $"SELECT alter_id FROM {keyspace}.global_journal_alters WHERE user_id = ? AND global_journal_id = ? AND alter_id = ? LIMIT 1",
                normalizedSystemId,
                entryId.Value,
                alterId.Value
            );

            var edgeRows = await session.ExecuteAsync(edgeExistsQuery);
            if (!edgeRows.Any())
                return false;

            var delete = new SimpleStatement(
                $"DELETE FROM {keyspace}.global_journal_alters WHERE user_id = ? AND global_journal_id = ? AND alter_id = ?",
                normalizedSystemId,
                entryId.Value,
                alterId.Value
            );
            await session.ExecuteAsync(delete);

            return true;
        }, cancellationToken);
    }

    public async Task<EntryId?> CreateAlterAsync(SystemId systemId, CreateAlterJournalEntryCommand command, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync<EntryId?>(systemId, async scope =>
        {
            var session = scope.Session;
            var normalizedSystemId = scope.NormalizedSystemId;
            var keyspace = scope.Keyspace;
            var entryId = Guid.NewGuid();

            var timestamp = command.CreatedAt.ToUniversalTime();

            var insert = new SimpleStatement(
                $"INSERT INTO {keyspace}.alter_journals (user_id, id, alter_id, title, content, color, pinned, locked, inserted_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
                normalizedSystemId,
                entryId,
                command.AlterId.Value,
                command.Title,
                null,
                null,
                false,
                false,
                timestamp,
                timestamp
            );

            var insertLookup = new SimpleStatement(
                $"INSERT INTO {keyspace}.alter_journals_by_alter (user_id, alter_id, id, title, content, color, pinned, locked, inserted_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
                normalizedSystemId,
                command.AlterId.Value,
                entryId,
                command.Title,
                null,
                null,
                false,
                false,
                timestamp,
                timestamp
            );

            var batch = new BatchStatement();
            batch.Add(insert);
            batch.Add(insertLookup);
            await session.ExecuteAsync(batch);
            return new(entryId);
        }, cancellationToken);
    }

    public async Task<AlterJournalRef?> GetAlterRefAsync(SystemId systemId, EntryId entryId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
            await GetAlterRefCoreAsync(scope, entryId, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Reads the alter-journal ref within an already-open scope. Used by the alter-scoped
    /// writers to avoid a nested <see cref="IScyllaScopeResolver"/> call
    /// (which would create a second retry envelope around the outer one).
    /// </summary>
    private static async Task<AlterJournalRef?> GetAlterRefCoreAsync(
        ScyllaScope scope, EntryId entryId, CancellationToken cancellationToken)
    {
        var query = new SimpleStatement(
            $"SELECT id, alter_id FROM {scope.Keyspace}.alter_journals WHERE user_id = ? AND id = ? ALLOW FILTERING",
            scope.NormalizedSystemId,
            entryId.Value
        );

        var row = (await scope.Session.ExecuteAsync(query)).FirstOrDefault();
        return row is null
            ? null
            : new AlterJournalRef(new(row.GetValue<Guid>("id")), new(row.GetValue<short>("alter_id")));
    }

    public async Task<bool> UpdateAlterAsync(SystemId systemId, UpdateAlterJournalEntryCommand command, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var reference = await GetAlterRefCoreAsync(scope, command.EntryId, cancellationToken);
            if (reference is null)
                return false;

            var timestamp = command.UpdatedAt.ToUniversalTime();
            var updateBatch = new BatchStatement();

            if (command.Title is not null)
            {
                updateBatch.Add(new SimpleStatement(
                    $"UPDATE {scope.Keyspace}.alter_journals SET title = ?, updated_at = ? WHERE user_id = ? AND id = ? AND alter_id = ?",
                    command.Title, timestamp, scope.NormalizedSystemId, reference.EntryId.Value, reference.AlterId.Value));
                updateBatch.Add(new SimpleStatement(
                    $"UPDATE {scope.Keyspace}.alter_journals_by_alter SET title = ?, updated_at = ? WHERE user_id = ? AND alter_id = ? AND id = ?",
                    command.Title, timestamp, scope.NormalizedSystemId, reference.AlterId.Value, reference.EntryId.Value));
            }

            if (command.Content is not null)
            {
                updateBatch.Add(new SimpleStatement(
                    $"UPDATE {scope.Keyspace}.alter_journals SET content = ?, updated_at = ? WHERE user_id = ? AND id = ? AND alter_id = ?",
                    command.Content, timestamp, scope.NormalizedSystemId, reference.EntryId.Value, reference.AlterId.Value));
                updateBatch.Add(new SimpleStatement(
                    $"UPDATE {scope.Keyspace}.alter_journals_by_alter SET content = ?, updated_at = ? WHERE user_id = ? AND alter_id = ? AND id = ?",
                    command.Content, timestamp, scope.NormalizedSystemId, reference.AlterId.Value, reference.EntryId.Value));
            }

            if (command.Color is not null)
            {
                updateBatch.Add(new SimpleStatement(
                    $"UPDATE {scope.Keyspace}.alter_journals SET color = ?, updated_at = ? WHERE user_id = ? AND id = ? AND alter_id = ?",
                    command.Color?.Value, timestamp, scope.NormalizedSystemId, reference.EntryId.Value, reference.AlterId.Value));
                updateBatch.Add(new SimpleStatement(
                    $"UPDATE {scope.Keyspace}.alter_journals_by_alter SET color = ?, updated_at = ? WHERE user_id = ? AND alter_id = ? AND id = ?",
                    command.Color?.Value, timestamp, scope.NormalizedSystemId, reference.AlterId.Value, reference.EntryId.Value));
            }

            if (!updateBatch.IsEmpty)
                await scope.Session.ExecuteAsync(updateBatch);

            return true;
        }, cancellationToken);
    }

    public async Task<bool> DeleteAlterAsync(SystemId systemId, EntryId entryId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var reference = await GetAlterRefCoreAsync(scope, entryId, cancellationToken);
            if (reference is null)
                return false;

            var delete = new BatchStatement();
            delete.Add(new SimpleStatement(
                $"DELETE FROM {scope.Keyspace}.alter_journals WHERE user_id = ? AND id = ? AND alter_id = ?",
                scope.NormalizedSystemId,
                reference.EntryId.Value,
                reference.AlterId.Value
            ));
            delete.Add(new SimpleStatement(
                $"DELETE FROM {scope.Keyspace}.alter_journals_by_alter WHERE user_id = ? AND alter_id = ? AND id = ?",
                scope.NormalizedSystemId,
                reference.AlterId.Value,
                reference.EntryId.Value
            ));
            await scope.Session.ExecuteAsync(delete);

            return true;
        }, cancellationToken);
    }

    public async Task<bool> SetAlterLockedAsync(SystemId systemId, EntryId entryId, bool locked, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var reference = await GetAlterRefCoreAsync(scope, entryId, cancellationToken);
            if (reference is null)
                return false;

            var batch = new BatchStatement();
            batch.Add(new SimpleStatement(
                $"UPDATE {scope.Keyspace}.alter_journals SET locked = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ? AND alter_id = ?",
                locked, scope.NormalizedSystemId, reference.EntryId.Value, reference.AlterId.Value));
            batch.Add(new SimpleStatement(
                $"UPDATE {scope.Keyspace}.alter_journals_by_alter SET locked = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND alter_id = ? AND id = ?",
                locked, scope.NormalizedSystemId, reference.AlterId.Value, reference.EntryId.Value));
            await scope.Session.ExecuteAsync(batch);

            return true;
        }, cancellationToken);
    }

    public async Task<bool> SetAlterPinnedAsync(SystemId systemId, EntryId entryId, bool pinned, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var reference = await GetAlterRefCoreAsync(scope, entryId, cancellationToken);
            if (reference is null)
                return false;

            var batch = new BatchStatement();
            batch.Add(new SimpleStatement(
                $"UPDATE {scope.Keyspace}.alter_journals SET pinned = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ? AND alter_id = ?",
                pinned, scope.NormalizedSystemId, reference.EntryId.Value, reference.AlterId.Value));
            batch.Add(new SimpleStatement(
                $"UPDATE {scope.Keyspace}.alter_journals_by_alter SET pinned = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND alter_id = ? AND id = ?",
                pinned, scope.NormalizedSystemId, reference.AlterId.Value, reference.EntryId.Value));
            await scope.Session.ExecuteAsync(batch);

            return true;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<AlterJournalReadModel>> ListAlterAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var session = scope.Session;
            var normalizedSystemId = scope.NormalizedSystemId;
            var keyspace = scope.Keyspace;

            var query = new SimpleStatement(
                $"SELECT id, user_id, alter_id, title, content, color, pinned, locked, inserted_at, updated_at FROM {keyspace}.alter_journals_by_alter WHERE user_id = ? AND alter_id = ?",
                normalizedSystemId,
                alterId.Value
            );

            var rows = await session.ExecuteAsync(query);
            return rows
                .Select(row => new AlterJournalReadModel(
                    new(row.GetValue<Guid>("id")),
                    new(row.GetValue<string>("user_id")),
                    new(row.GetValue<short>("alter_id")),
                    row.GetValue<string>("title"),
                    row.GetValue<string?>("content"),
                    HexColor.FromNullable(row.GetValue<string?>("color")),
                    row.GetValue<bool>("locked"),
                    row.GetValue<bool>("pinned"),
                    row.GetValue<DateTime>("inserted_at"),
                    row.GetValue<DateTime>("updated_at")))
                .OrderByDescending(e => e.InsertedAt)
                .ToArray();
        }, cancellationToken);
    }

    public async Task<AlterJournalReadModel?> GetAlterAsync(SystemId systemId, EntryId entryId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var session = scope.Session;
            var normalizedSystemId = scope.NormalizedSystemId;
            var keyspace = scope.Keyspace;

            var query = new SimpleStatement(
                $"SELECT id, user_id, alter_id, title, content, color, pinned, locked, inserted_at, updated_at FROM {keyspace}.alter_journals WHERE user_id = ? AND id = ? ALLOW FILTERING",
                normalizedSystemId,
                entryId.Value
            );

            var row = (await session.ExecuteAsync(query)).FirstOrDefault();
            return row is null
                ? null
                : new AlterJournalReadModel(
                    new(row.GetValue<Guid>("id")),
                    new(row.GetValue<string>("user_id")),
                    new(row.GetValue<short>("alter_id")),
                    row.GetValue<string>("title"),
                    row.GetValue<string?>("content"),
                    HexColor.FromNullable(row.GetValue<string?>("color")),
                    row.GetValue<bool>("locked"),
                    row.GetValue<bool>("pinned"),
                    row.GetValue<DateTime>("inserted_at"),
                    row.GetValue<DateTime>("updated_at"));
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<JournalReadModel>> ListGlobalAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var session = scope.Session;
            var normalizedSystemId = scope.NormalizedSystemId;
            var keyspace = scope.Keyspace;

            var entriesQuery = new SimpleStatement(
                $"SELECT id, user_id, title, content, color, pinned, locked, inserted_at, updated_at FROM {keyspace}.global_journals WHERE user_id = ?",
                normalizedSystemId
            );
            var entryRows = await session.ExecuteAsync(entriesQuery);

            var result = new List<JournalReadModel>();
            foreach (var row in entryRows)
            {
                var id = row.GetValue<Guid>("id");
                var altersQuery = new SimpleStatement(
                    $"SELECT alter_id FROM {keyspace}.global_journal_alters WHERE user_id = ? AND global_journal_id = ?",
                    normalizedSystemId,
                    id
                );
                var alterRows = await session.ExecuteAsync(altersQuery);
                var alterIds = alterRows.Select(r => new AlterId(r.GetValue<short>("alter_id"))).ToArray();

                result.Add(new JournalReadModel(
                    new(id),
                    new(row.GetValue<string>("user_id")),
                    row.GetValue<string>("title"),
                    row.GetValue<string?>("content"),
                    HexColor.FromNullable(row.GetValue<string?>("color")),
                    row.GetValue<bool>("locked"),
                    row.GetValue<bool>("pinned"),
                    row.GetValue<DateTime>("inserted_at"),
                    row.GetValue<DateTime>("updated_at"),
                    alterIds));
            }

            // Sort key is the wire form (lowercase "N" hex) to keep list ordering byte-identical
            // to the historic string-backed EntryId — Guid.CompareTo bytewise reorders differently.
            return (IReadOnlyList<JournalReadModel>)result
                .OrderByDescending(e => e.Id.Value.ToString("N"), StringComparer.Ordinal)
                .ToArray();
        }, cancellationToken);
    }

    public async Task<JournalReadModel?> GetGlobalAsync(SystemId systemId, EntryId entryId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var session = scope.Session;
            var normalizedSystemId = scope.NormalizedSystemId;
            var keyspace = scope.Keyspace;

            var entryQuery = new SimpleStatement(
                $"SELECT id, user_id, title, content, color, pinned, locked, inserted_at, updated_at FROM {keyspace}.global_journals WHERE user_id = ? AND id = ? LIMIT 1",
                normalizedSystemId,
                entryId.Value
            );
            var entryRow = (await session.ExecuteAsync(entryQuery)).FirstOrDefault();
            if (entryRow is null)
                return null;

            var altersQuery = new SimpleStatement(
                $"SELECT alter_id FROM {keyspace}.global_journal_alters WHERE user_id = ? AND global_journal_id = ?",
                normalizedSystemId,
                entryId.Value
            );
            var alterIds = (await session.ExecuteAsync(altersQuery))
                .Select(r => new AlterId(r.GetValue<short>("alter_id")))
                .ToArray();

            return new JournalReadModel(
                new(entryRow.GetValue<Guid>("id")),
                new(entryRow.GetValue<string>("user_id")),
                entryRow.GetValue<string>("title"),
                entryRow.GetValue<string?>("content"),
                HexColor.FromNullable(entryRow.GetValue<string?>("color")),
                entryRow.GetValue<bool>("locked"),
                entryRow.GetValue<bool>("pinned"),
                entryRow.GetValue<DateTime>("inserted_at"),
                entryRow.GetValue<DateTime>("updated_at"),
                alterIds);
        }, cancellationToken);
    }

    public async Task<int> DeleteAllForAlterAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var session = scope.Session;
            var normalizedSystemId = scope.NormalizedSystemId;
            var keyspace = scope.Keyspace;

            // Step 1: list every per-alter journal entry id for this alter via the by-alter
            // view (the partition key is (user_id, alter_id), so this is one single-partition
            // read - no cross-partition scan). We capture both id and alter_id even though
            // alter_id is constant per partition because the alter_journals (main view) row
            // key is (user_id, id, alter_id).
            var listQuery = new SimpleStatement(
                $"SELECT id FROM {keyspace}.alter_journals_by_alter WHERE user_id = ? AND alter_id = ?",
                normalizedSystemId,
                alterId.Value);
            var entryIds = (await session.ExecuteAsync(listQuery))
                .Select(r => r.GetValue<Guid>("id"))
                .ToArray();

            // Step 2: detach the alter from any global journals. We scan the user's
            // global_journal_alters partition once and filter for the matching alter_id in
            // C# (ALLOW FILTERING would be equivalent at the server but burns a server-side
            // filter; the partition is small enough that client-side filter is cheap and we
            // already pay for the row reads either way).
            var attachmentsQuery = new SimpleStatement(
                $"SELECT global_journal_id, alter_id FROM {keyspace}.global_journal_alters WHERE user_id = ?",
                normalizedSystemId);
            var attachedGlobals = (await session.ExecuteAsync(attachmentsQuery))
                .Where(r => new AlterId(r.GetValue<short>("alter_id")) == alterId)
                .Select(r => r.GetValue<Guid>("global_journal_id"))
                .ToArray();

            if (entryIds.Length == 0 && attachedGlobals.Length == 0)
            {
                return 0;
            }

            // Step 3: single batch for all deletes so we either land the full cascade or
            // none of it. The deletes are all to the same partition key prefix (user_id)
            // so this is a single-coordinator batch in Scylla terms.
            var batch = new BatchStatement();

            foreach (var entryId in entryIds)
            {
                batch.Add(new SimpleStatement(
                    $"DELETE FROM {keyspace}.alter_journals WHERE user_id = ? AND id = ? AND alter_id = ?",
                    normalizedSystemId,
                    entryId,
                    alterId.Value));
                batch.Add(new SimpleStatement(
                    $"DELETE FROM {keyspace}.alter_journals_by_alter WHERE user_id = ? AND alter_id = ? AND id = ?",
                    normalizedSystemId,
                    alterId.Value,
                    entryId));
            }

            foreach (var globalJournalId in attachedGlobals)
            {
                batch.Add(new SimpleStatement(
                    $"DELETE FROM {keyspace}.global_journal_alters WHERE user_id = ? AND global_journal_id = ? AND alter_id = ?",
                    normalizedSystemId,
                    globalJournalId,
                    alterId.Value));
            }

            await session.ExecuteAsync(batch);
            return entryIds.Length;
        }, cancellationToken);
    }
}
