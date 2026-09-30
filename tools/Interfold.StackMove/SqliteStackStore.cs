using Interfold.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Interfold.StackMove;

internal sealed class SqliteStackStore(string databasePath)
{
    private string ConnectionString => $"Data Source={databasePath};Pooling=False";

    public async Task<long> CountAccountsAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(databasePath))
        {
            return 0;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        if (!await TableExistsAsync(connection, "accounts", cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM accounts";
        var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(scalar);
    }

    public async Task<StackSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = new StackSnapshot();
        snapshot.Accounts.AddRange(await ReadAccountsAsync(connection, cancellationToken).ConfigureAwait(false));
        var encryption = await ReadEncryptionAsync(connection, cancellationToken).ConfigureAwait(false);
        var fields = await ReadSettingsFieldsAsync(connection, cancellationToken).ConfigureAwait(false);
        var primary = await ReadPrimaryFrontsAsync(connection, cancellationToken).ConfigureAwait(false);
        foreach (var account in snapshot.Accounts)
        {
            if (encryption.TryGetValue(account.SystemId, out var state))
            {
                account.Encryption = state;
            }

            if (fields.TryGetValue(account.SystemId, out var accountFields))
            {
                account.Fields.AddRange(accountFields);
            }

            if (primary.TryGetValue(account.SystemId, out var alterId))
            {
                account.PrimaryFrontAlter = alterId;
            }
        }

        snapshot.Alters.AddRange(await ReadAltersAsync(connection, cancellationToken).ConfigureAwait(false));
        snapshot.Tags.AddRange(await ReadTagsAsync(connection, cancellationToken).ConfigureAwait(false));
        snapshot.AlterTags.AddRange(await ReadAlterTagsAsync(connection, cancellationToken).ConfigureAwait(false));
        snapshot.Friendships.AddRange(await ReadFriendshipsAsync(connection, cancellationToken).ConfigureAwait(false));
        snapshot.FriendRequests.AddRange(await ReadFriendRequestsAsync(connection, cancellationToken).ConfigureAwait(false));
        snapshot.NotificationTokens.AddRange(await ReadNotificationTokensAsync(connection, cancellationToken).ConfigureAwait(false));
        snapshot.Fronts.AddRange(await ReadFrontsAsync(connection, cancellationToken).ConfigureAwait(false));
        snapshot.Journals.AddRange(await ReadJournalsAsync(connection, cancellationToken).ConfigureAwait(false));
        snapshot.Polls.AddRange(await ReadPollsAsync(connection, cancellationToken).ConfigureAwait(false));
        snapshot.Secrets.AddRange(await ReadSecretsAsync(connection, cancellationToken).ConfigureAwait(false));
        return snapshot;
    }

    public async Task WriteAsync(StackSnapshot snapshot, IReadOnlyList<SecretRow> secrets, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await SqliteMigrationService.MigrateAsync(ConnectionString, NullLogger.Instance, cancellationToken)
            .ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var account in snapshot.Accounts)
        {
            await InsertAccountAsync(connection, transaction, account, cancellationToken).ConfigureAwait(false);
        }

        foreach (var alter in snapshot.Alters)
        {
            await InsertAlterAsync(connection, transaction, alter, cancellationToken).ConfigureAwait(false);
        }

        foreach (var tag in snapshot.Tags)
        {
            await InsertTagAsync(connection, transaction, tag, cancellationToken).ConfigureAwait(false);
        }

        foreach (var membership in snapshot.AlterTags)
        {
            await InsertAlterTagAsync(connection, transaction, membership, cancellationToken).ConfigureAwait(false);
        }

        foreach (var friendship in snapshot.Friendships)
        {
            await InsertFriendshipAsync(connection, transaction, friendship, cancellationToken).ConfigureAwait(false);
        }

        foreach (var request in snapshot.FriendRequests)
        {
            await InsertFriendRequestAsync(connection, transaction, request, cancellationToken).ConfigureAwait(false);
        }

        foreach (var token in snapshot.NotificationTokens)
        {
            await InsertNotificationTokenAsync(connection, transaction, token, cancellationToken).ConfigureAwait(false);
        }

        foreach (var front in snapshot.Fronts)
        {
            await InsertFrontAsync(connection, transaction, front, cancellationToken).ConfigureAwait(false);
        }

        foreach (var journal in snapshot.Journals)
        {
            await InsertJournalAsync(connection, transaction, journal, cancellationToken).ConfigureAwait(false);
        }

        foreach (var poll in snapshot.Polls)
        {
            await InsertPollAsync(connection, transaction, poll, cancellationToken).ConfigureAwait(false);
        }

        foreach (var secret in secrets)
        {
            await UpsertSecretAsync(connection, transaction, secret, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await new SqliteConnectionFactory(ConnectionString).OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            throw new MoveRefusedException(
                $"SQLite database is locked ({databasePath}). Stop the API and run the move again.");
        }
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", table);
        var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return scalar is not null;
    }

    private static async Task<List<AccountRow>> ReadAccountsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var rows = new List<AccountRow>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT system_id, username, description, avatar_url, avatar_source, discord_id, email, apple_id,
                   link_token, link_token_expires_at, created_at, updated_at
            FROM accounts
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new AccountRow
            {
                SystemId = SystemIds.Bare(reader.GetString(0)),
                Username = ReadString(reader, 1),
                Description = ReadString(reader, 2),
                AvatarUrl = ReadString(reader, 3),
                AvatarSource = ReadInt16(reader, 4),
                DiscordId = ReadString(reader, 5),
                Email = ReadString(reader, 6),
                AppleId = ReadString(reader, 7),
                LinkToken = ReadString(reader, 8),
                LinkTokenExpiresAtUnixMs = ReadInt64(reader, 9),
                CreatedAtUnixMs = reader.GetInt64(10),
                UpdatedAtUnixMs = reader.GetInt64(11),
            });
        }

        return rows;
    }

    private static async Task<Dictionary<string, EncryptionRow>> ReadEncryptionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var rows = new Dictionary<string, EncryptionRow>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT system_id, encryption_initialized, encryption_key_checksum, salt, updated_at
            FROM encryption_states
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows[SystemIds.Bare(reader.GetString(0))] = new EncryptionRow
            {
                Initialized = reader.GetInt64(1) != 0,
                KeyChecksum = ReadString(reader, 2),
                Salt = ReadString(reader, 3),
                UpdatedAtUnixMs = reader.GetInt64(4),
            };
        }

        return rows;
    }

    private static async Task<Dictionary<string, List<SettingsFieldRow>>> ReadSettingsFieldsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var rows = new Dictionary<string, List<SettingsFieldRow>>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT system_id, id, name, type, security_level, locked, idx, inserted_at, updated_at
            FROM settings_fields
            ORDER BY system_id, idx
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var systemId = SystemIds.Bare(reader.GetString(0));
            if (!rows.TryGetValue(systemId, out var list))
            {
                list = [];
                rows[systemId] = list;
            }

            list.Add(new SettingsFieldRow
            {
                Id = GuidText.Parse(reader.GetString(1)),
                Name = reader.GetString(2),
                Type = (short)reader.GetInt64(3),
                SecurityLevel = (short)reader.GetInt64(4),
                Locked = reader.GetInt64(5) != 0,
                Index = (int)reader.GetInt64(6),
                InsertedAtUnixMs = reader.GetInt64(7),
                UpdatedAtUnixMs = reader.GetInt64(8),
            });
        }

        return rows;
    }

    private static async Task<Dictionary<string, short>> ReadPrimaryFrontsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var rows = new Dictionary<string, short>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT user_id, alter_id FROM front_primary WHERE alter_id IS NOT NULL";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows[SystemIds.Bare(reader.GetString(0))] = (short)reader.GetInt64(1);
        }

        return rows;
    }

    private static async Task<List<AlterRow>> ReadAltersAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var fields = await ReadAlterFieldsAsync(connection, cancellationToken).ConfigureAwait(false);
        var rows = new List<AlterRow>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT system_id, id, alias, name, pronouns, description, avatar_url, avatar_source, security_level,
                   color, proxy_name, untracked, archived, pinned, inserted_at, updated_at
            FROM alters
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var systemId = SystemIds.Bare(reader.GetString(0));
            var id = (short)reader.GetInt64(1);
            var alter = new AlterRow
            {
                SystemId = systemId,
                Id = id,
                Alias = ReadString(reader, 2),
                Name = reader.GetString(3),
                Pronouns = ReadString(reader, 4),
                Description = ReadString(reader, 5),
                AvatarUrl = ReadString(reader, 6),
                AvatarSource = ReadInt16(reader, 7),
                SecurityLevel = (short)reader.GetInt64(8),
                Color = ReadString(reader, 9),
                ProxyName = ReadString(reader, 10),
                Untracked = reader.GetInt64(11) != 0,
                Archived = reader.GetInt64(12) != 0,
                Pinned = reader.GetInt64(13) != 0,
                InsertedAtUnixMs = reader.GetInt64(14),
                UpdatedAtUnixMs = reader.GetInt64(15),
            };
            if (fields.TryGetValue((systemId, id), out var values))
            {
                alter.Fields.AddRange(values);
            }

            rows.Add(alter);
        }

        return rows;
    }

    private static async Task<Dictionary<(string SystemId, short AlterId), List<AlterFieldRow>>> ReadAlterFieldsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var rows = new Dictionary<(string, short), List<AlterFieldRow>>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT system_id, alter_id, field_id, value FROM alter_fields";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = (SystemIds.Bare(reader.GetString(0)), (short)reader.GetInt64(1));
            if (!rows.TryGetValue(key, out var list))
            {
                list = [];
                rows[key] = list;
            }

            list.Add(new AlterFieldRow
            {
                Id = GuidText.Parse(reader.GetString(2)),
                Value = ReadString(reader, 3),
            });
        }

        return rows;
    }

    private static async Task<List<TagRow>> ReadTagsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var rows = new List<TagRow>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT system_id, id, parent_tag_id, name, description, color, security_level, inserted_at, updated_at
            FROM tags
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new TagRow
            {
                SystemId = SystemIds.Bare(reader.GetString(0)),
                Id = GuidText.Parse(reader.GetString(1)),
                ParentTagId = reader.IsDBNull(2) ? null : GuidText.Parse(reader.GetString(2)),
                Name = reader.GetString(3),
                Description = ReadString(reader, 4),
                Color = ReadString(reader, 5),
                SecurityLevel = (short)reader.GetInt64(6),
                InsertedAtUnixMs = reader.GetInt64(7),
                UpdatedAtUnixMs = reader.GetInt64(8),
            });
        }

        return rows;
    }

    private static async Task<List<AlterTagRow>> ReadAlterTagsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var rows = new List<AlterTagRow>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT system_id, tag_id, alter_id, inserted_at, updated_at FROM alter_tags";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new AlterTagRow
            {
                SystemId = SystemIds.Bare(reader.GetString(0)),
                TagId = GuidText.Parse(reader.GetString(1)),
                AlterId = (short)reader.GetInt64(2),
                InsertedAtUnixMs = reader.GetInt64(3),
                UpdatedAtUnixMs = reader.GetInt64(4),
            });
        }

        return rows;
    }

    private static async Task<List<FriendshipRow>> ReadFriendshipsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var rows = new List<FriendshipRow>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT user_id, friend_id, level, since FROM friendships";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new FriendshipRow
            {
                UserId = SystemIds.Bare(reader.GetString(0)),
                FriendId = SystemIds.Bare(reader.GetString(1)),
                Level = (short)reader.GetInt64(2),
                SinceUnixMs = reader.GetInt64(3),
            });
        }

        return rows;
    }

    private static async Task<List<FriendRequestRow>> ReadFriendRequestsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var rows = new List<FriendRequestRow>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT from_user_id, to_user_id, date_sent FROM friend_requests";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new FriendRequestRow
            {
                FromUserId = SystemIds.Bare(reader.GetString(0)),
                ToUserId = SystemIds.Bare(reader.GetString(1)),
                DateSentUnixMs = reader.GetInt64(2),
            });
        }

        return rows;
    }

    private static async Task<List<NotificationTokenRow>> ReadNotificationTokensAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var rows = new List<NotificationTokenRow>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT system_id, push_token, inserted_at, updated_at FROM notification_tokens";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new NotificationTokenRow
            {
                SystemId = SystemIds.Bare(reader.GetString(0)),
                PushToken = reader.GetString(1),
                InsertedAtUnixMs = reader.GetInt64(2),
                UpdatedAtUnixMs = reader.GetInt64(3),
            });
        }

        return rows;
    }

    private static async Task<List<FrontRow>> ReadFrontsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var rows = new List<FrontRow>();
        await using (var current = connection.CreateCommand())
        {
            current.CommandText = "SELECT user_id, id, alter_id, comment, time_start FROM current_fronts";
            await using var reader = await current.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new FrontRow
                {
                    SystemId = SystemIds.Bare(reader.GetString(0)),
                    Id = GuidText.Parse(reader.GetString(1)),
                    AlterId = (short)reader.GetInt64(2),
                    Comment = ReadString(reader, 3),
                    TimeStartUnixMs = reader.GetInt64(4),
                    Current = true,
                });
            }
        }

        await using (var history = connection.CreateCommand())
        {
            history.CommandText = "SELECT user_id, id, alter_id, comment, time_start, time_end FROM fronts";
            await using var reader = await history.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new FrontRow
                {
                    SystemId = SystemIds.Bare(reader.GetString(0)),
                    Id = GuidText.Parse(reader.GetString(1)),
                    AlterId = (short)reader.GetInt64(2),
                    Comment = ReadString(reader, 3),
                    TimeStartUnixMs = reader.GetInt64(4),
                    TimeEndUnixMs = ReadInt64(reader, 5),
                    Current = false,
                });
            }
        }

        return rows;
    }

    private static async Task<List<JournalRow>> ReadJournalsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var attachments = new Dictionary<(string, Guid), List<short>>();
        await using (var link = connection.CreateCommand())
        {
            link.CommandText = "SELECT user_id, global_journal_id, alter_id FROM global_journal_alters";
            await using var reader = await link.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var key = (SystemIds.Bare(reader.GetString(0)), GuidText.Parse(reader.GetString(1)));
                if (!attachments.TryGetValue(key, out var list))
                {
                    list = [];
                    attachments[key] = list;
                }

                list.Add((short)reader.GetInt64(2));
            }
        }

        var rows = new List<JournalRow>();
        await using (var global = connection.CreateCommand())
        {
            global.CommandText = """
                SELECT user_id, id, title, content, color, pinned, locked, inserted_at, updated_at
                FROM global_journals
                """;
            await using var reader = await global.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var systemId = SystemIds.Bare(reader.GetString(0));
                var id = GuidText.Parse(reader.GetString(1));
                var journal = new JournalRow
                {
                    SystemId = systemId,
                    Id = id,
                    Title = reader.GetString(2),
                    Content = ReadString(reader, 3),
                    Color = ReadString(reader, 4),
                    Pinned = reader.GetInt64(5) != 0,
                    Locked = reader.GetInt64(6) != 0,
                    InsertedAtUnixMs = reader.GetInt64(7),
                    UpdatedAtUnixMs = reader.GetInt64(8),
                };
                if (attachments.TryGetValue((systemId, id), out var alters))
                {
                    journal.AlterIds.AddRange(alters);
                }

                rows.Add(journal);
            }
        }

        await using (var alterJournal = connection.CreateCommand())
        {
            alterJournal.CommandText = """
                SELECT user_id, id, alter_id, title, content, color, pinned, locked, inserted_at, updated_at
                FROM alter_journals
                """;
            await using var reader = await alterJournal.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new JournalRow
                {
                    SystemId = SystemIds.Bare(reader.GetString(0)),
                    Id = GuidText.Parse(reader.GetString(1)),
                    AlterId = (short)reader.GetInt64(2),
                    Title = reader.GetString(3),
                    Content = ReadString(reader, 4),
                    Color = ReadString(reader, 5),
                    Pinned = reader.GetInt64(6) != 0,
                    Locked = reader.GetInt64(7) != 0,
                    InsertedAtUnixMs = reader.GetInt64(8),
                    UpdatedAtUnixMs = reader.GetInt64(9),
                });
            }
        }

        return rows;
    }

    private static async Task<List<PollRow>> ReadPollsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var rows = new List<PollRow>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT user_id, id, title, description, type, data, time_end, inserted_at, updated_at
            FROM polls
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new PollRow
            {
                SystemId = SystemIds.Bare(reader.GetString(0)),
                Id = GuidText.Parse(reader.GetString(1)),
                Title = reader.GetString(2),
                Description = ReadString(reader, 3),
                Type = (short)reader.GetInt64(4),
                Data = reader.IsDBNull(5) ? "{}" : reader.GetString(5),
                TimeEndUnixMs = ReadInt64(reader, 6),
                InsertedAtUnixMs = reader.GetInt64(7),
                UpdatedAtUnixMs = reader.GetInt64(8),
            });
        }

        return rows;
    }

    private static async Task<List<SecretRow>> ReadSecretsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var rows = new List<SecretRow>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT key, value, created_by, created_at, updated_at, expires_at, rotated_from
            FROM secrets
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new SecretRow
            {
                Key = reader.GetString(0),
                Value = reader.GetString(1),
                CreatedBy = reader.GetString(2),
                CreatedAtUnixMs = reader.GetInt64(3),
                UpdatedAtUnixMs = reader.GetInt64(4),
                ExpiresAtUnixMs = ReadInt64(reader, 5),
                RotatedFrom = ReadString(reader, 6),
            });
        }

        return rows;
    }

    private static async Task InsertAccountAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AccountRow account,
        CancellationToken cancellationToken)
    {
        var id = SystemIds.Bare(account.SystemId);
        await ExecuteAsync(connection, transaction, """
            INSERT INTO accounts (
                system_id, username, description, avatar_url, avatar_source, discord_id, email, apple_id,
                link_token, link_token_expires_at, created_at, updated_at)
            VALUES (
                $system_id, $username, $description, $avatar_url, $avatar_source, $discord_id, $email, $apple_id,
                $link_token, $link_token_expires_at, $created_at, $updated_at)
            """,
            cancellationToken,
            ("$system_id", id),
            ("$username", account.Username),
            ("$description", account.Description),
            ("$avatar_url", account.AvatarUrl),
            ("$avatar_source", account.AvatarSource),
            ("$discord_id", account.DiscordId),
            ("$email", account.Email),
            ("$apple_id", account.AppleId),
            ("$link_token", account.LinkToken),
            ("$link_token_expires_at", account.LinkTokenExpiresAtUnixMs),
            ("$created_at", account.CreatedAtUnixMs),
            ("$updated_at", account.UpdatedAtUnixMs)).ConfigureAwait(false);

        if (account.Encryption is { } encryption)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO encryption_states (
                    system_id, encryption_initialized, encryption_key_checksum, salt, updated_at)
                VALUES ($system_id, $initialized, $checksum, $salt, $updated_at)
                """,
                cancellationToken,
                ("$system_id", id),
                ("$initialized", encryption.Initialized ? 1 : 0),
                ("$checksum", encryption.KeyChecksum),
                ("$salt", encryption.Salt),
                ("$updated_at", encryption.UpdatedAtUnixMs)).ConfigureAwait(false);
        }

        var index = 0;
        foreach (var field in account.Fields.OrderBy(field => field.Index))
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO settings_fields (
                    system_id, id, name, type, security_level, locked, idx, inserted_at, updated_at)
                VALUES (
                    $system_id, $id, $name, $type, $security_level, $locked, $idx, $inserted_at, $updated_at)
                """,
                cancellationToken,
                ("$system_id", id),
                ("$id", GuidText.Format(field.Id)),
                ("$name", field.Name),
                ("$type", field.Type),
                ("$security_level", field.SecurityLevel),
                ("$locked", field.Locked ? 1 : 0),
                ("$idx", index),
                ("$inserted_at", field.InsertedAtUnixMs ?? account.CreatedAtUnixMs),
                ("$updated_at", field.UpdatedAtUnixMs ?? account.UpdatedAtUnixMs)).ConfigureAwait(false);
            index++;
        }

        if (account.PrimaryFrontAlter is { } alterId)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO front_primary (user_id, alter_id) VALUES ($user_id, $alter_id)
                """,
                cancellationToken,
                ("$user_id", id),
                ("$alter_id", alterId)).ConfigureAwait(false);
        }
    }

    private static async Task InsertAlterAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AlterRow alter,
        CancellationToken cancellationToken)
    {
        var systemId = SystemIds.Bare(alter.SystemId);
        await ExecuteAsync(connection, transaction, """
            INSERT INTO alters (
                system_id, id, alias, name, pronouns, description, avatar_url, avatar_source, security_level,
                color, proxy_name, untracked, archived, pinned, inserted_at, updated_at)
            VALUES (
                $system_id, $id, $alias, $name, $pronouns, $description, $avatar_url, $avatar_source, $security_level,
                $color, $proxy_name, $untracked, $archived, $pinned, $inserted_at, $updated_at)
            """,
            cancellationToken,
            ("$system_id", systemId),
            ("$id", alter.Id),
            ("$alias", alter.Alias),
            ("$name", alter.Name),
            ("$pronouns", alter.Pronouns),
            ("$description", alter.Description),
            ("$avatar_url", alter.AvatarUrl),
            ("$avatar_source", alter.AvatarSource),
            ("$security_level", alter.SecurityLevel),
            ("$color", alter.Color),
            ("$proxy_name", alter.ProxyName),
            ("$untracked", alter.Untracked ? 1 : 0),
            ("$archived", alter.Archived ? 1 : 0),
            ("$pinned", alter.Pinned ? 1 : 0),
            ("$inserted_at", alter.InsertedAtUnixMs),
            ("$updated_at", alter.UpdatedAtUnixMs)).ConfigureAwait(false);

        foreach (var field in alter.Fields)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO alter_fields (system_id, alter_id, field_id, value)
                VALUES ($system_id, $alter_id, $field_id, $value)
                """,
                cancellationToken,
                ("$system_id", systemId),
                ("$alter_id", alter.Id),
                ("$field_id", GuidText.Format(field.Id)),
                ("$value", field.Value)).ConfigureAwait(false);
        }
    }

    private static Task InsertTagAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TagRow tag,
        CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, """
            INSERT INTO tags (
                system_id, id, parent_tag_id, name, description, color, security_level, inserted_at, updated_at)
            VALUES (
                $system_id, $id, $parent_tag_id, $name, $description, $color, $security_level, $inserted_at, $updated_at)
            """,
            cancellationToken,
            ("$system_id", SystemIds.Bare(tag.SystemId)),
            ("$id", GuidText.Format(tag.Id)),
            ("$parent_tag_id", tag.ParentTagId is { } parent ? GuidText.Format(parent) : null),
            ("$name", tag.Name),
            ("$description", tag.Description),
            ("$color", tag.Color),
            ("$security_level", tag.SecurityLevel),
            ("$inserted_at", tag.InsertedAtUnixMs),
            ("$updated_at", tag.UpdatedAtUnixMs));

    private static Task InsertAlterTagAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AlterTagRow membership,
        CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, """
            INSERT INTO alter_tags (system_id, tag_id, alter_id, inserted_at, updated_at)
            VALUES ($system_id, $tag_id, $alter_id, $inserted_at, $updated_at)
            """,
            cancellationToken,
            ("$system_id", SystemIds.Bare(membership.SystemId)),
            ("$tag_id", GuidText.Format(membership.TagId)),
            ("$alter_id", membership.AlterId),
            ("$inserted_at", membership.InsertedAtUnixMs),
            ("$updated_at", membership.UpdatedAtUnixMs));

    private static Task InsertFriendshipAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        FriendshipRow friendship,
        CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, """
            INSERT INTO friendships (user_id, friend_id, level, since)
            VALUES ($user_id, $friend_id, $level, $since)
            """,
            cancellationToken,
            ("$user_id", SystemIds.Bare(friendship.UserId)),
            ("$friend_id", SystemIds.Bare(friendship.FriendId)),
            ("$level", friendship.Level),
            ("$since", friendship.SinceUnixMs));

    private static Task InsertFriendRequestAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        FriendRequestRow request,
        CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, """
            INSERT INTO friend_requests (from_user_id, to_user_id, date_sent)
            VALUES ($from_user_id, $to_user_id, $date_sent)
            """,
            cancellationToken,
            ("$from_user_id", SystemIds.Bare(request.FromUserId)),
            ("$to_user_id", SystemIds.Bare(request.ToUserId)),
            ("$date_sent", request.DateSentUnixMs));

    private static Task InsertNotificationTokenAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        NotificationTokenRow token,
        CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, """
            INSERT INTO notification_tokens (system_id, push_token, inserted_at, updated_at)
            VALUES ($system_id, $push_token, $inserted_at, $updated_at)
            """,
            cancellationToken,
            ("$system_id", SystemIds.Bare(token.SystemId)),
            ("$push_token", token.PushToken),
            ("$inserted_at", token.InsertedAtUnixMs),
            ("$updated_at", token.UpdatedAtUnixMs));

    private static Task InsertFrontAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        FrontRow front,
        CancellationToken cancellationToken)
    {
        var systemId = SystemIds.Bare(front.SystemId);
        if (front.Current)
        {
            return ExecuteAsync(connection, transaction, """
                INSERT INTO current_fronts (user_id, alter_id, id, comment, time_start)
                VALUES ($user_id, $alter_id, $id, $comment, $time_start)
                """,
                cancellationToken,
                ("$user_id", systemId),
                ("$alter_id", front.AlterId),
                ("$id", GuidText.Format(front.Id)),
                ("$comment", front.Comment),
                ("$time_start", front.TimeStartUnixMs));
        }

        return ExecuteAsync(connection, transaction, """
            INSERT INTO fronts (user_id, id, alter_id, comment, time_start, time_end)
            VALUES ($user_id, $id, $alter_id, $comment, $time_start, $time_end)
            """,
            cancellationToken,
            ("$user_id", systemId),
            ("$id", GuidText.Format(front.Id)),
            ("$alter_id", front.AlterId),
            ("$comment", front.Comment),
            ("$time_start", front.TimeStartUnixMs),
            ("$time_end", front.TimeEndUnixMs));
    }

    private static async Task InsertJournalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        JournalRow journal,
        CancellationToken cancellationToken)
    {
        var systemId = SystemIds.Bare(journal.SystemId);
        if (journal.AlterId is { } alterId)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO alter_journals (
                    user_id, id, alter_id, title, content, color, pinned, locked, inserted_at, updated_at)
                VALUES (
                    $user_id, $id, $alter_id, $title, $content, $color, $pinned, $locked, $inserted_at, $updated_at)
                """,
                cancellationToken,
                ("$user_id", systemId),
                ("$id", GuidText.Format(journal.Id)),
                ("$alter_id", alterId),
                ("$title", journal.Title),
                ("$content", journal.Content),
                ("$color", journal.Color),
                ("$pinned", journal.Pinned ? 1 : 0),
                ("$locked", journal.Locked ? 1 : 0),
                ("$inserted_at", journal.InsertedAtUnixMs),
                ("$updated_at", journal.UpdatedAtUnixMs)).ConfigureAwait(false);
            return;
        }

        await ExecuteAsync(connection, transaction, """
            INSERT INTO global_journals (
                user_id, id, title, content, color, pinned, locked, inserted_at, updated_at)
            VALUES (
                $user_id, $id, $title, $content, $color, $pinned, $locked, $inserted_at, $updated_at)
            """,
            cancellationToken,
            ("$user_id", systemId),
            ("$id", GuidText.Format(journal.Id)),
            ("$title", journal.Title),
            ("$content", journal.Content),
            ("$color", journal.Color),
            ("$pinned", journal.Pinned ? 1 : 0),
            ("$locked", journal.Locked ? 1 : 0),
            ("$inserted_at", journal.InsertedAtUnixMs),
            ("$updated_at", journal.UpdatedAtUnixMs)).ConfigureAwait(false);

        foreach (var attached in journal.AlterIds)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO global_journal_alters (
                    user_id, global_journal_id, alter_id, inserted_at, updated_at)
                VALUES ($user_id, $global_journal_id, $alter_id, $inserted_at, $updated_at)
                """,
                cancellationToken,
                ("$user_id", systemId),
                ("$global_journal_id", GuidText.Format(journal.Id)),
                ("$alter_id", attached),
                ("$inserted_at", journal.InsertedAtUnixMs),
                ("$updated_at", journal.UpdatedAtUnixMs)).ConfigureAwait(false);
        }
    }

    private static Task InsertPollAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PollRow poll,
        CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, """
            INSERT INTO polls (
                user_id, id, title, description, type, data, time_end, inserted_at, updated_at)
            VALUES (
                $user_id, $id, $title, $description, $type, $data, $time_end, $inserted_at, $updated_at)
            """,
            cancellationToken,
            ("$user_id", SystemIds.Bare(poll.SystemId)),
            ("$id", GuidText.Format(poll.Id)),
            ("$title", poll.Title),
            ("$description", poll.Description),
            ("$type", poll.Type),
            ("$data", string.IsNullOrEmpty(poll.Data) ? "{}" : poll.Data),
            ("$time_end", poll.TimeEndUnixMs),
            ("$inserted_at", poll.InsertedAtUnixMs),
            ("$updated_at", poll.UpdatedAtUnixMs));

    private static Task UpsertSecretAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SecretRow secret,
        CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, """
            INSERT INTO secrets (key, value, created_by, created_at, updated_at, expires_at, rotated_from)
            VALUES ($key, $value, $created_by, $created_at, $updated_at, $expires_at, $rotated_from)
            ON CONFLICT(key) DO UPDATE SET
                value = excluded.value,
                created_by = excluded.created_by,
                updated_at = excluded.updated_at,
                expires_at = excluded.expires_at,
                rotated_from = excluded.rotated_from
            """,
            cancellationToken,
            ("$key", secret.Key),
            ("$value", secret.Value),
            ("$created_by", secret.CreatedBy),
            ("$created_at", secret.CreatedAtUnixMs),
            ("$updated_at", secret.UpdatedAtUnixMs),
            ("$expires_at", secret.ExpiresAtUnixMs),
            ("$rotated_from", secret.RotatedFrom));

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string? ReadString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static short? ReadInt16(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : (short)reader.GetInt64(ordinal);

    private static long? ReadInt64(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
}
