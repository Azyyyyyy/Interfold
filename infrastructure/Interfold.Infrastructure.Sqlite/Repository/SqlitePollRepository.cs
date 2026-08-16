using System.Text.Json;
using Interfold.Polls.Contracts.Ids;
using Interfold.Polls.Contracts.Models;
using Interfold.Polls.Contracts.Models.Commands;
using Interfold.Polls.Contracts.Models.Read;
using Interfold.Polls.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Domain.Abstractions;
using Microsoft.Data.Sqlite;

namespace Interfold.Infrastructure.Sqlite.Repository;

public sealed class SqlitePollRepository(
    ISqliteConnectionFactory connectionFactory,
    IRegionContext regionContext) : IPollRepository
{
    public async Task<IReadOnlyList<PollReadModel>> ListAsync(
        SystemId systemId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var userId = SqliteStorageKeys.ForSystem(regionContext, systemId).Value;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, title, description, type, data, time_end, inserted_at, updated_at
            FROM polls
            WHERE user_id = $user_id
            """;
        command.Parameters.AddWithValue("$user_id", userId);

        var list = new List<PollReadModel>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(ReadPoll(reader, systemId));
        }

        return list
            .OrderBy(p => p.Id.Value.ToString("N"), StringComparer.Ordinal)
            .ToList();
    }

    public async Task<PollReadModel?> GetAsync(
        SystemId systemId,
        PollId pollId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var userId = SqliteStorageKeys.ForSystem(regionContext, systemId).Value;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, title, description, type, data, time_end, inserted_at, updated_at
            FROM polls
            WHERE user_id = $user_id AND id = $id
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$user_id", userId);
        command.Parameters.AddWithValue("$id", pollId.Value.ToString("N"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadPoll(reader, systemId);
    }

    public async Task<PollId?> CreateAsync(
        SystemId systemId,
        CreatePollCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var userId = SqliteStorageKeys.ForSystem(regionContext, systemId).Value;
        var pollGuid = Guid.NewGuid();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var insertedMs = ToUnixMs(command.InsertedAtUtc);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO polls
                (user_id, id, title, description, type, data, time_end, inserted_at, updated_at)
            VALUES
                ($user_id, $id, $title, $description, $type, '{}', $time_end, $inserted_at, $updated_at)
            """;
        insert.Parameters.AddWithValue("$user_id", userId);
        insert.Parameters.AddWithValue("$id", pollGuid.ToString("N"));
        insert.Parameters.AddWithValue("$title", command.Title);
        insert.Parameters.AddWithValue("$description", (object?)command.Description ?? DBNull.Value);
        insert.Parameters.AddWithValue("$type", command.Type.ToWire());
        insert.Parameters.AddWithValue(
            "$time_end",
            command.TimeEnd is { } end ? ToUnixMs(end) : DBNull.Value);
        insert.Parameters.AddWithValue("$inserted_at", insertedMs);
        insert.Parameters.AddWithValue("$updated_at", nowMs);
        await insert.ExecuteNonQueryAsync(cancellationToken);

        return new PollId(pollGuid);
    }

    public async Task<bool> ExistsAsync(
        SystemId systemId,
        PollId pollId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var userId = SqliteStorageKeys.ForSystem(regionContext, systemId).Value;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1 FROM polls
            WHERE user_id = $user_id AND id = $id
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$user_id", userId);
        command.Parameters.AddWithValue("$id", pollId.Value.ToString("N"));
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<bool> UpdateAsync(
        SystemId systemId,
        UpdatePollCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var userId = SqliteStorageKeys.ForSystem(regionContext, systemId).Value;
        var pollIdText = command.Id.Value.ToString("N");

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        if (!await ExistsOnConnectionAsync(connection, userId, pollIdText, cancellationToken))
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
        await using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE polls SET
                title = COALESCE($title, title),
                description = CASE WHEN $has_description = 1 THEN $description ELSE description END,
                time_end = CASE WHEN $has_time_end = 1 THEN $time_end ELSE time_end END,
                data = COALESCE($data, data),
                updated_at = $updated_at
            WHERE user_id = $user_id AND id = $id
            """;
        update.Parameters.AddWithValue("$title", (object?)command.Title ?? DBNull.Value);
        update.Parameters.AddWithValue("$has_description", command.Description is not null ? 1 : 0);
        update.Parameters.AddWithValue("$description", (object?)command.Description ?? DBNull.Value);
        update.Parameters.AddWithValue("$has_time_end", command.HasTimeEnd ? 1 : 0);
        update.Parameters.AddWithValue(
            "$time_end",
            command.HasTimeEnd
                ? (command.TimeEnd is { } end ? ToUnixMs(end) : DBNull.Value)
                : DBNull.Value);
        update.Parameters.AddWithValue(
            "$data",
            command.Data is { } data ? data.GetRawText() : DBNull.Value);
        update.Parameters.AddWithValue("$updated_at", nowMs);
        update.Parameters.AddWithValue("$user_id", userId);
        update.Parameters.AddWithValue("$id", pollIdText);
        await update.ExecuteNonQueryAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteAsync(
        SystemId systemId,
        PollId pollId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var userId = SqliteStorageKeys.ForSystem(regionContext, systemId).Value;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM polls WHERE user_id = $user_id AND id = $id";
        command.Parameters.AddWithValue("$user_id", userId);
        command.Parameters.AddWithValue("$id", pollId.Value.ToString("N"));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
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
        string pollId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1 FROM polls
            WHERE user_id = $user_id AND id = $id
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$user_id", userId);
        command.Parameters.AddWithValue("$id", pollId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static PollReadModel ReadPoll(SqliteDataReader reader, SystemId systemId)
    {
        var id = PollId.Parse(reader.GetString(0), provider: null);
        var title = reader.GetString(1);
        var description = reader.IsDBNull(2) ? null : reader.GetString(2);
        var typeWire = reader.GetString(3);
        if (!typeWire.TryParseWire<PollType>(out var type))
        {
            throw new InvalidOperationException($"Corrupt poll type '{typeWire}'.");
        }

        var dataRaw = reader.IsDBNull(4) ? "{}" : reader.GetString(4);
        var data = JsonDocument.Parse(string.IsNullOrWhiteSpace(dataRaw) ? "{}" : dataRaw).RootElement.Clone();
        DateTime? timeEnd = reader.IsDBNull(5) ? null : FromUnixMs(reader.GetInt64(5));
        var insertedAt = FromUnixMs(reader.GetInt64(6));
        var updatedAt = FromUnixMs(reader.GetInt64(7));

        return new PollReadModel(
            id,
            SqliteStorageKeys.Normalize(systemId),
            title,
            description,
            type,
            data,
            timeEnd,
            insertedAt,
            updatedAt);
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
}
