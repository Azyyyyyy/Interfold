using System.Text.Json;
using Dapper;
using Interfold.Polls.Contracts.Ids;
using Interfold.Polls.Contracts.Models;
using Interfold.Polls.Contracts.Models.Commands;
using Interfold.Polls.Contracts.Models.Read;
using Interfold.Polls.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Microsoft.Data.Sqlite;

namespace Interfold.Infrastructure.Sqlite.Repository;

public sealed class SqlitePollRepository(ISqliteConnectionFactory connectionFactory) : IPollRepository
{
    public async Task<IReadOnlyList<PollReadModel>> ListAsync(
        SystemId systemId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var userId = SqliteStorageKeys.Persist(systemId);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<PollRow>(
            """
            SELECT id AS Id, title AS Title, description AS Description, type AS Type,
                   data AS Data, time_end AS TimeEnd, inserted_at AS InsertedAt, updated_at AS UpdatedAt
            FROM polls
            WHERE user_id = @user_id
            """,
            new { user_id = userId });

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
        var userId = SqliteStorageKeys.Persist(systemId);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var row = await connection.QueryFirstOrDefaultAsync<PollRow>(
            """
            SELECT id AS Id, title AS Title, description AS Description, type AS Type,
                   data AS Data, time_end AS TimeEnd, inserted_at AS InsertedAt, updated_at AS UpdatedAt
            FROM polls
            WHERE user_id = @user_id AND id = @id
            LIMIT 1
            """,
            new { user_id = userId, id = pollId.Value.ToString("N") });

        return row is null ? null : MapPoll(row, systemId);
    }

    public async Task<PollId?> CreateAsync(
        SystemId systemId,
        CreatePollCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var userId = SqliteStorageKeys.Persist(systemId);
        var pollGuid = Guid.NewGuid();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var insertedMs = ToUnixMs(command.InsertedAtUtc);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(
            """
            INSERT INTO polls
                (user_id, id, title, description, type, data, time_end, inserted_at, updated_at)
            VALUES
                (@user_id, @id, @title, @description, @type, '{}', @time_end, @inserted_at, @updated_at)
            """,
            new
            {
                user_id = userId,
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
        var userId = SqliteStorageKeys.Persist(systemId);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var found = await connection.ExecuteScalarAsync<long?>(
            """
            SELECT 1 FROM polls
            WHERE user_id = @user_id AND id = @id
            LIMIT 1
            """,
            new { user_id = userId, id = pollId.Value.ToString("N") });
        return found is not null;
    }

    public async Task<bool> UpdateAsync(
        SystemId systemId,
        UpdatePollCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var userId = SqliteStorageKeys.Persist(systemId);
        var pollIdText = command.Id.Value.ToString("N");

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        if (!await ExistsOnConnectionAsync(connection, userId, pollIdText))
        {
            return false;
        }

        if (command.Title is null
            && command.Description is null
            && !command.HasTimeEnd
            && command.Data is null)
        {
            return true;
        }

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await connection.ExecuteAsync(
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
                user_id = userId,
                id = pollIdText,
            });
        return true;
    }

    public async Task<bool> DeleteAsync(
        SystemId systemId,
        PollId pollId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var userId = SqliteStorageKeys.Persist(systemId);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(
            "DELETE FROM polls WHERE user_id = @user_id AND id = @id",
            new { user_id = userId, id = pollId.Value.ToString("N") });
        return affected > 0;
    }

    public async Task RemoveAlterFromPollsAsync(
        SystemId systemId,
        AlterId alterId,
        CancellationToken cancellationToken = default)
    {
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
        }
    }

    private static async Task<bool> ExistsOnConnectionAsync(
        SqliteConnection connection,
        string userId,
        string pollId)
    {
        var found = await connection.ExecuteScalarAsync<long?>(
            """
            SELECT 1 FROM polls
            WHERE user_id = @user_id AND id = @id
            LIMIT 1
            """,
            new { user_id = userId, id = pollId });
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

    private static long ToUnixMs(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();
        return new DateTimeOffset(utc).ToUnixTimeMilliseconds();
    }

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
