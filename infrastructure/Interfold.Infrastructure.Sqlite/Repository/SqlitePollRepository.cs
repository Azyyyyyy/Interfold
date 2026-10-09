using System.Text.Json;
using Dapper;
using Interfold.Polls.Contracts.Ids;
using Interfold.Polls.Contracts.Models;
using Interfold.Polls.Contracts.Models.Commands;
using Interfold.Polls.Contracts.Models.Read;
using Interfold.Polls.Domain.Abstractions.Repository;
using Interfold.Infrastructure.Sqlite;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Microsoft.Data.Sqlite;

namespace Interfold.Infrastructure.Sqlite.Repository;

public sealed class SqlitePollRepository : IPollRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly TimeProvider _timeProvider;

    public SqlitePollRepository(ISqliteConnectionFactory connectionFactory, TimeProvider timeProvider)
    {
        _connectionFactory = connectionFactory;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<PollReadModel>> ListAsync(
        SystemId systemId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<PollRow>(
            """
            SELECT id AS Id, title AS Title, description AS Description, type AS Type,
                   data AS Data, time_end AS TimeEnd, inserted_at AS InsertedAt, updated_at AS UpdatedAt
            FROM polls
            WHERE user_id = @user_id
            """,
            new { user_id = systemId });

        return rows
            .Select(r => MapPoll(r, systemId))
            .OrderBy(p => p.Id.Value.ToString("N"), StringComparer.Ordinal)
            .ToList();
    }

    public async Task<PollReadModel?> GetAsync(
        SystemId systemId,
        PollId pollId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var row = await connection.QueryFirstOrDefaultAsync<PollRow>(
            """
            SELECT id AS Id, title AS Title, description AS Description, type AS Type,
                   data AS Data, time_end AS TimeEnd, inserted_at AS InsertedAt, updated_at AS UpdatedAt
            FROM polls
            WHERE user_id = @user_id AND id = @id
            LIMIT 1
            """,
            new { user_id = systemId, id = pollId.Value.ToString("N") });

        return row is null ? null : MapPoll(row, systemId);
    }

    public async Task<PollId?> CreateAsync(
        SystemId systemId,
        CreatePollCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pollGuid = Guid.NewGuid();
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var insertedMs = ToUnixMs(command.InsertedAtUtc);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(
            """
            INSERT INTO polls
                (user_id, id, title, description, type, data, time_end, inserted_at, updated_at)
            VALUES
                (@user_id, @id, @title, @description, @type, '{}', @time_end, @inserted_at, @updated_at)
            """,
            new
            {
                user_id = systemId,
                id = pollGuid.ToString("N"),
                title = command.Title,
                description = command.Description,
                type = (short)command.Type,
                time_end = command.TimeEnd is { } end ? ToUnixMs(end) : (long?)null,
                inserted_at = insertedMs,
                updated_at = nowMs,
            });

        return new PollId(pollGuid);
    }

    public async Task<bool> ExistsAsync(
        SystemId systemId,
        PollId pollId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var found = await connection.ExecuteScalarAsync<long?>(
            """
            SELECT 1 FROM polls
            WHERE user_id = @user_id AND id = @id
            LIMIT 1
            """,
            new { user_id = systemId, id = pollId.Value.ToString("N") });
        return found is not null;
    }

    public async Task<bool> UpdateAsync(
        SystemId systemId,
        UpdatePollCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pollIdText = command.Id.Value.ToString("N");

        await using var work = await SqliteWork.OpenAsync(_connectionFactory, cancellationToken);
        if (!await ExistsOnConnectionAsync(work.Connection, work.Transaction, systemId, pollIdText))
            return false;

        if (command.Title is null
            && command.Description is null
            && !command.HasTimeEnd
            && command.Data is null)
        {
            await work.CommitAsync(cancellationToken);
            return true;
        }

        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var updated = await work.Connection.ExecuteAsync(
            """
            UPDATE polls SET
                title = COALESCE(@title, title),
                description = CASE WHEN @has_description = 1 THEN @description ELSE description END,
                time_end = CASE WHEN @has_time_end = 1 THEN @time_end ELSE time_end END,
                data = COALESCE(@data, data),
                updated_at = @updated_at
            WHERE user_id = @user_id AND id = @id
            """,
            new
            {
                title = command.Title,
                has_description = command.Description is not null ? 1 : 0,
                description = command.Description,
                has_time_end = command.HasTimeEnd ? 1 : 0,
                time_end = command.HasTimeEnd
                    ? (command.TimeEnd is { } end ? ToUnixMs(end) : (long?)null)
                    : null,
                data = command.Data?.GetRawText(),
                updated_at = nowMs,
                user_id = systemId,
                id = pollIdText,
            },
            work.Transaction);
        await work.CommitAsync(cancellationToken);
        return updated > 0;
    }

    public async Task<bool> DeleteAsync(
        SystemId systemId,
        PollId pollId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var work = await SqliteWork.OpenAsync(_connectionFactory, cancellationToken);
        var affected = await work.Connection.ExecuteAsync(
            "DELETE FROM polls WHERE user_id = @user_id AND id = @id",
            new { user_id = systemId, id = pollId.Value.ToString("N") },
            work.Transaction);
        await work.CommitAsync(cancellationToken);
        return affected > 0;
    }

    public async Task<IReadOnlyList<PollId>> RemoveAlterFromPollsAsync(
        SystemId systemId,
        AlterId alterId,
        CancellationToken cancellationToken = default)
    {
        var updated = new List<PollId>();
        var polls = await ListAsync(systemId, cancellationToken);
        foreach (var poll in polls)
        {
            if (!PollDataJson.TryRemoveAlterResponses(poll.Data, alterId, out var newData))
            {
                continue;
            }

            await UpdateAsync(
                systemId,
                new UpdatePollCommand(poll.Id, null, null, null, false, newData),
                cancellationToken);
            updated.Add(poll.Id);
        }

        return updated;
    }

    private static async Task<bool> ExistsOnConnectionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SystemId systemId,
        string pollId)
    {
        var found = await connection.ExecuteScalarAsync<long?>(
            """
            SELECT 1 FROM polls
            WHERE user_id = @user_id AND id = @id
            LIMIT 1
            """,
            new { user_id = systemId, id = pollId },
            transaction);
        return found is not null;
    }

    private static PollReadModel MapPoll(PollRow row, SystemId systemId)
    {
        var id = PollId.Parse(row.Id, provider: null);
        var dataRaw = string.IsNullOrWhiteSpace(row.Data) ? "{}" : row.Data;
        var data = JsonDocument.Parse(dataRaw).RootElement.Clone();
        DateTime? timeEnd = row.TimeEnd is null ? null : FromUnixMs(row.TimeEnd.Value);

        return new PollReadModel(
            id,
            systemId,
            row.Title,
            row.Description,
            ((short)row.Type).FromCode<PollType>(),
            data,
            timeEnd,
            FromUnixMs(row.InsertedAt),
            FromUnixMs(row.UpdatedAt));
    }

    // Repo stores unix ms as UTC; Unspecified Kind is treated as UTC (not local).
    private static long ToUnixMs(DateTime value)
        => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private static DateTime FromUnixMs(long ms)
        => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    private sealed record PollRow(
        string Id,
        string Title,
        string? Description,
        long Type,
        string? Data,
        long? TimeEnd,
        long InsertedAt,
        long UpdatedAt);
}
