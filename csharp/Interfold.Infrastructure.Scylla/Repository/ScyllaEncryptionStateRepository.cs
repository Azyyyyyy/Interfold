using Cassandra;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace Interfold.Infrastructure.Scylla.Repository;

public sealed class ScyllaEncryptionStateRepository : IEncryptionStateRepository
{
    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IScyllaKeyspaceResolver _keyspaceResolver;
    private readonly PersistenceConfiguration _options;

    public ScyllaEncryptionStateRepository(
        IScyllaSessionProvider sessionProvider,
        IScyllaKeyspaceResolver keyspaceResolver,
        IOptions<PersistenceConfiguration> options)
    {
        _sessionProvider = sessionProvider;
        _keyspaceResolver = keyspaceResolver;
        _options = options.Value;
    }

    public async Task<EncryptionState?> GetAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            // Bind SystemId.Value (the raw string), not the SystemId struct itself. The Cassandra
            // C# driver's params-object[] path has no serializer for SystemId; passing the struct
            // relies on Object.ToString() being called by the driver's fallback path, which today
            // happens to equal SystemId.Value but is not contractually guaranteed. Sibling repos
            // (ScyllaNotificationTokenRepository:37, ScyllaSharedQueries:40) already use .Value —
            // this call site (and its two UPSERT siblings below) was drift.
            var query = new SimpleStatement(
                $"SELECT encryption_initialized, encryption_key_checksum, salt FROM {keyspace}.users WHERE id = ? LIMIT 1",
                normalizedSystemId
            );

            var row = (await session.ExecuteAsync(query)).FirstOrDefault();
            return row is null
                ? null
                : new EncryptionState(
                    row.GetValue<bool?>("encryption_initialized") ?? false,
                    KeyChecksum.FromNullable(row.GetValue<string?>("encryption_key_checksum")),
                    EncryptionSalt.FromNullable(row.GetValue<string?>("salt")));
        }, _options, cancellationToken);
    }

    public async Task<bool> UpsertAsync(SystemId systemId, bool initialized, KeyChecksum? keyChecksum, EncryptionSalt? salt, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            SimpleStatement statement;
            if (salt is not { } newSalt)
            {
                statement = new SimpleStatement(
                    $"UPDATE {keyspace}.users SET encryption_initialized = ?, encryption_key_checksum = ?, updated_at = toTimestamp(now()) WHERE id = ?",
                    initialized,
                    keyChecksum?.Value,
                    normalizedSystemId
                );
            }
            else
            {
                statement = new SimpleStatement(
                    $"UPDATE {keyspace}.users SET encryption_initialized = ?, encryption_key_checksum = ?, salt = ?, updated_at = toTimestamp(now()) WHERE id = ?",
                    initialized,
                    keyChecksum?.Value,
                    newSalt.Value,
                    normalizedSystemId
                );
            }

            await session.ExecuteAsync(statement);
            return true;
        }, _options, cancellationToken);
    }
}
