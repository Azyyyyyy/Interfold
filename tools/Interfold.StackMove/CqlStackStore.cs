using Cassandra;
using Interfold.Shared.Contracts.Enums;

namespace Interfold.StackMove;

internal sealed class CqlStackStore(CqlEndpoint endpoint)
{
    private readonly string _keyspace = ValidateKeyspace(endpoint.Keyspace);

    public async Task<long> CountPopulationAsync(CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var session = connection.Session;
        var users = await CountAsync(session, $"SELECT COUNT(*) FROM {_keyspace}.users", cancellationToken)
            .ConfigureAwait(false);
        var registry = await CountAsync(session, "SELECT COUNT(*) FROM global.user_registry", cancellationToken)
            .ConfigureAwait(false);
        return users + registry;
    }

    public async Task<StackSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var session = connection.Session;
        var snapshot = new StackSnapshot();
        snapshot.Regions.AddRange(await ReadRegionsAsync(session, cancellationToken).ConfigureAwait(false));
        snapshot.Accounts.AddRange(await ReadAccountsAsync(session, cancellationToken).ConfigureAwait(false));
        snapshot.Alters.AddRange(await ReadAltersAsync(session, cancellationToken).ConfigureAwait(false));
        snapshot.Tags.AddRange(await ReadTagsAsync(session, cancellationToken).ConfigureAwait(false));
        snapshot.AlterTags.AddRange(await ReadAlterTagsAsync(session, cancellationToken).ConfigureAwait(false));
        snapshot.Friendships.AddRange(await ReadFriendshipsAsync(session, cancellationToken).ConfigureAwait(false));
        snapshot.FriendRequests.AddRange(await ReadFriendRequestsAsync(session, cancellationToken).ConfigureAwait(false));
        snapshot.NotificationTokens.AddRange(await ReadTokensAsync(session, cancellationToken).ConfigureAwait(false));
        snapshot.Fronts.AddRange(await ReadFrontsAsync(session, cancellationToken).ConfigureAwait(false));
        snapshot.Journals.AddRange(await ReadJournalsAsync(session, cancellationToken).ConfigureAwait(false));
        snapshot.Polls.AddRange(await ReadPollsAsync(session, cancellationToken).ConfigureAwait(false));
        snapshot.Secrets.AddRange(await PostgresSecrets.ReadAsync(endpoint.PostgresConnectionString, cancellationToken)
            .ConfigureAwait(false));
        return snapshot;
    }

    public async Task WriteAsync(StackSnapshot snapshot, IReadOnlyList<SecretRow> secrets, CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var session = connection.Session;
        foreach (var account in snapshot.Accounts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WriteAccountAsync(session, account, cancellationToken).ConfigureAwait(false);
        }

        foreach (var alter in snapshot.Alters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WriteAlterAsync(session, alter).ConfigureAwait(false);
        }

        foreach (var tag in snapshot.Tags)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WriteTagAsync(session, tag).ConfigureAwait(false);
        }

        foreach (var membership in snapshot.AlterTags)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WriteAlterTagAsync(session, membership).ConfigureAwait(false);
        }

        foreach (var friendship in snapshot.Friendships)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WriteFriendshipAsync(session, friendship).ConfigureAwait(false);
        }

        foreach (var request in snapshot.FriendRequests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WriteFriendRequestAsync(session, request).ConfigureAwait(false);
        }

        foreach (var token in snapshot.NotificationTokens)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WriteTokenAsync(session, token).ConfigureAwait(false);
        }

        foreach (var front in snapshot.Fronts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WriteFrontAsync(session, front).ConfigureAwait(false);
        }

        foreach (var journal in snapshot.Journals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WriteJournalAsync(session, journal).ConfigureAwait(false);
        }

        foreach (var poll in snapshot.Polls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WritePollAsync(session, poll).ConfigureAwait(false);
        }

        await PostgresSecrets.UpsertAsync(endpoint.PostgresConnectionString, secrets, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task WriteAccountAsync(ISession session, AccountRow account, CancellationToken cancellationToken)
    {
        var id = SystemIds.Bare(account.SystemId);
        var fields = account.Fields
            .OrderBy(field => field.Index)
            .Select(field => new FieldUdt
            {
                Id = field.Id,
                Name = field.Name,
                Type = field.Type,
                Locked = field.Locked,
                SecurityLevel = field.SecurityLevel,
                InsertedAt = UnixMs.ToOffset(field.InsertedAtUnixMs),
                UpdatedAt = UnixMs.ToOffset(field.UpdatedAtUnixMs),
            })
            .ToList();
        var created = UnixMs.ToOffset(account.CreatedAtUnixMs);
        var updated = UnixMs.ToOffset(account.UpdatedAtUnixMs);
        var legacyPrimary = account.LegacyPrimaryFront ?? account.PrimaryFrontAlter;

        await session.ExecuteAsync(new SimpleStatement(
            $"""
            INSERT INTO {_keyspace}.users (
                id, email, discord_id, apple_id, google_id, username, description, avatar_url, avatar_source,
                lifetime_alter_count, primary_front, primary_front_alter, last_proxy_id, discord_settings, fields,
                salt, encryption_initialized, encryption_key_checksum, inserted_at, updated_at)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            id,
            BlankToNull(account.Email),
            BlankToNull(account.DiscordId),
            BlankToNull(account.AppleId),
            BlankToNull(account.GoogleId),
            BlankToNull(account.Username),
            account.Description,
            account.AvatarUrl,
            account.AvatarSource,
            account.LifetimeAlterCount,
            legacyPrimary,
            account.PrimaryFrontAlter,
            account.LastProxyId,
            ToDiscordUdt(account.DiscordSettings),
            fields,
            account.Encryption?.Salt,
            account.Encryption?.Initialized ?? false,
            account.Encryption?.KeyChecksum,
            created,
            updated)).ConfigureAwait(false);

        await session.ExecuteAsync(new SimpleStatement(
            """
            INSERT INTO global.user_registry (
                user_id, discord_id, email, username, apple_id, google_id, region, inserted_at, updated_at)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            id,
            BlankToNull(account.DiscordId),
            BlankToNull(account.Email),
            BlankToNull(account.Username),
            BlankToNull(account.AppleId),
            BlankToNull(account.GoogleId),
            _keyspace,
            created,
            updated)).ConfigureAwait(false);

        await InsertLookupAsync(session, "users_by_discord_id", "discord_id", account.DiscordId, id, registry: true)
            .ConfigureAwait(false);
        await InsertLookupAsync(session, "users_by_email", "email", account.Email, id, registry: true)
            .ConfigureAwait(false);
        await InsertLookupAsync(session, "users_by_username", "username", account.Username, id, registry: true)
            .ConfigureAwait(false);
        await InsertLookupAsync(session, "users_by_apple_id", "apple_id", account.AppleId, id, registry: true)
            .ConfigureAwait(false);
        await InsertLookupAsync(session, "users_by_google_id", "google_id", account.GoogleId, id, registry: true)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task InsertLookupAsync(
        ISession session,
        string table,
        string column,
        string? value,
        string userId,
        bool registry)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        await session.ExecuteAsync(new SimpleStatement(
            $"INSERT INTO {_keyspace}.{table} ({column}, user_id) VALUES (?, ?)",
            value,
            userId)).ConfigureAwait(false);

        if (!registry)
        {
            return;
        }

        await session.ExecuteAsync(new SimpleStatement(
            $"INSERT INTO global.user_registry_by_{column} ({column}, user_id, region) VALUES (?, ?, ?)",
            value,
            userId,
            _keyspace)).ConfigureAwait(false);
    }

    private async Task WriteAlterAsync(ISession session, AlterRow alter)
    {
        var userId = SystemIds.Bare(alter.SystemId);
        var fields = alter.Fields.Select(field => new AlterFieldUdt { Id = field.Id, Value = field.Value }).ToList();
        await session.ExecuteAsync(new SimpleStatement(
            $"""
            INSERT INTO {_keyspace}.alters (
                user_id, id, alias, name, pronouns, description, avatar_url, avatar_source, security_level,
                extra_images, color, discord_proxies, proxy_name, fields, untracked, archived, pinned,
                last_fronted, inserted_at, updated_at)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            userId,
            alter.Id,
            BlankToNull(alter.Alias),
            alter.Name,
            alter.Pronouns,
            alter.Description,
            alter.AvatarUrl,
            alter.AvatarSource,
            alter.SecurityLevel,
            alter.ExtraImages.Count == 0 ? null : alter.ExtraImages,
            alter.Color,
            alter.DiscordProxies.Count == 0 ? null : alter.DiscordProxies,
            alter.ProxyName,
            fields,
            alter.Untracked,
            alter.Archived,
            alter.Pinned,
            UnixMs.ToOffset(alter.LastFrontedUnixMs),
            UnixMs.ToOffset(alter.InsertedAtUnixMs),
            UnixMs.ToOffset(alter.UpdatedAtUnixMs))).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(alter.Alias))
        {
            await session.ExecuteAsync(new SimpleStatement(
                $"INSERT INTO {_keyspace}.alters_by_alias (user_id, alias, alter_id) VALUES (?, ?, ?)",
                userId,
                alter.Alias,
                alter.Id)).ConfigureAwait(false);
        }
    }

    private async Task WriteTagAsync(ISession session, TagRow tag)
    {
        await session.ExecuteAsync(new SimpleStatement(
            $"""
            INSERT INTO {_keyspace}.tags (
                user_id, id, parent_tag_id, name, description, color, security_level, inserted_at, updated_at)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            SystemIds.Bare(tag.SystemId),
            tag.Id,
            tag.ParentTagId,
            tag.Name,
            tag.Description,
            tag.Color,
            tag.SecurityLevel,
            UnixMs.ToOffset(tag.InsertedAtUnixMs),
            UnixMs.ToOffset(tag.UpdatedAtUnixMs))).ConfigureAwait(false);
    }

    private async Task WriteAlterTagAsync(ISession session, AlterTagRow membership)
    {
        var userId = SystemIds.Bare(membership.SystemId);
        var inserted = UnixMs.ToOffset(membership.InsertedAtUnixMs);
        var updated = UnixMs.ToOffset(membership.UpdatedAtUnixMs);
        await session.ExecuteAsync(new SimpleStatement(
            $"""
            INSERT INTO {_keyspace}.alter_tags (user_id, tag_id, alter_id, inserted_at, updated_at)
            VALUES (?, ?, ?, ?, ?)
            """,
            userId,
            membership.TagId,
            membership.AlterId,
            inserted,
            updated)).ConfigureAwait(false);
        await session.ExecuteAsync(new SimpleStatement(
            $"""
            INSERT INTO {_keyspace}.alter_tags_by_alter (user_id, alter_id, tag_id, inserted_at, updated_at)
            VALUES (?, ?, ?, ?, ?)
            """,
            userId,
            membership.AlterId,
            membership.TagId,
            inserted,
            updated)).ConfigureAwait(false);
    }

    private async Task WriteFriendshipAsync(ISession session, FriendshipRow friendship)
    {
        var userId = SystemIds.Bare(friendship.UserId);
        var companion = FriendshipCompanion.For(friendship);
        var since = UnixMs.ToOffset(friendship.SinceUnixMs);
        await session.ExecuteAsync(new SimpleStatement(
            """
            INSERT INTO global.friendships (user_id, friend_id, level, since, inserted_at, updated_at)
            VALUES (?, ?, ?, ?, ?, ?)
            """,
            userId,
            companion.FriendId,
            friendship.Level,
            since,
            since,
            since)).ConfigureAwait(false);
        await session.ExecuteAsync(new SimpleStatement(
            """
            INSERT INTO global.friendships_by_friend_id (friend_id, user_id, level, since, inserted_at, updated_at)
            VALUES (?, ?, ?, ?, ?, ?)
            """,
            companion.FriendId,
            companion.UserId,
            companion.Level,
            since,
            since,
            since)).ConfigureAwait(false);
    }

    private async Task WriteFriendRequestAsync(ISession session, FriendRequestRow request)
    {
        var fromId = SystemIds.Bare(request.FromUserId);
        var toId = SystemIds.Bare(request.ToUserId);
        var sent = UnixMs.ToOffset(request.DateSentUnixMs);
        await session.ExecuteAsync(new SimpleStatement(
            """
            INSERT INTO global.friend_requests (from_id, to_id, date_sent, inserted_at, updated_at)
            VALUES (?, ?, ?, ?, ?)
            """,
            fromId,
            toId,
            sent,
            sent,
            sent)).ConfigureAwait(false);
        await session.ExecuteAsync(new SimpleStatement(
            """
            INSERT INTO global.friend_requests_by_to_id (to_id, from_id, date_sent, inserted_at, updated_at)
            VALUES (?, ?, ?, ?, ?)
            """,
            toId,
            fromId,
            sent,
            sent,
            sent)).ConfigureAwait(false);
    }

    private async Task WriteTokenAsync(ISession session, NotificationTokenRow token)
    {
        var userId = SystemIds.Bare(token.SystemId);
        var inserted = UnixMs.ToOffset(token.InsertedAtUnixMs);
        var updated = UnixMs.ToOffset(token.UpdatedAtUnixMs);
        await session.ExecuteAsync(new SimpleStatement(
            """
            INSERT INTO global.notification_tokens (user_id, push_token, inserted_at, updated_at)
            VALUES (?, ?, ?, ?)
            """,
            userId,
            token.PushToken,
            inserted,
            updated)).ConfigureAwait(false);
        await session.ExecuteAsync(new SimpleStatement(
            """
            INSERT INTO global.notification_tokens_by_push_token (push_token, user_id)
            VALUES (?, ?)
            """,
            token.PushToken,
            userId)).ConfigureAwait(false);
    }

    private async Task WriteFrontAsync(ISession session, FrontRow front)
    {
        var userId = SystemIds.Bare(front.SystemId);
        var started = UnixMs.ToOffset(front.TimeStartUnixMs);
        if (front.Current)
        {
            await session.ExecuteAsync(new SimpleStatement(
                $"""
                INSERT INTO {_keyspace}.current_fronts (
                    user_id, alter_id, id, comment, time_start, inserted_at, updated_at)
                VALUES (?, ?, ?, ?, ?, ?, ?)
                """,
                userId,
                front.AlterId,
                front.Id,
                front.Comment,
                started,
                started,
                started)).ConfigureAwait(false);
            return;
        }

        var ended = UnixMs.ToOffset(front.TimeEndUnixMs);
        await session.ExecuteAsync(new SimpleStatement(
            $"""
            INSERT INTO {_keyspace}.fronts (
                id, user_id, alter_id, comment, time_start, time_end, inserted_at, updated_at)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?)
            """,
            front.Id,
            userId,
            front.AlterId,
            front.Comment,
            started,
            ended,
            started,
            started)).ConfigureAwait(false);
        await session.ExecuteAsync(new SimpleStatement(
            $"""
            INSERT INTO {_keyspace}.fronts_by_alter (
                user_id, alter_id, id, comment, time_start, time_end, inserted_at, updated_at)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?)
            """,
            userId,
            front.AlterId,
            front.Id,
            front.Comment,
            started,
            ended,
            started,
            started)).ConfigureAwait(false);

        if (ended is null)
        {
            return;
        }

        await session.ExecuteAsync(new SimpleStatement(
            $"""
            INSERT INTO {_keyspace}.fronts_by_time (
                user_id, time_start, time_end, id, alter_id, comment, inserted_at, updated_at)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?)
            """,
            userId,
            started,
            ended,
            front.Id,
            front.AlterId,
            front.Comment,
            started,
            started)).ConfigureAwait(false);
        await session.ExecuteAsync(new SimpleStatement(
            $"""
            INSERT INTO {_keyspace}.fronts_by_end_time (
                user_id, time_end, time_start, id, alter_id, comment, inserted_at, updated_at)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?)
            """,
            userId,
            ended,
            started,
            front.Id,
            front.AlterId,
            front.Comment,
            started,
            started)).ConfigureAwait(false);
    }

    private async Task WriteJournalAsync(ISession session, JournalRow journal)
    {
        var userId = SystemIds.Bare(journal.SystemId);
        var inserted = UnixMs.ToOffset(journal.InsertedAtUnixMs);
        var updated = UnixMs.ToOffset(journal.UpdatedAtUnixMs);
        if (journal.AlterId is { } alterId)
        {
            await session.ExecuteAsync(new SimpleStatement(
                $"""
                INSERT INTO {_keyspace}.alter_journals (
                    user_id, id, alter_id, title, content, color, pinned, locked, inserted_at, updated_at)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                """,
                userId,
                journal.Id,
                alterId,
                journal.Title,
                journal.Content,
                journal.Color,
                journal.Pinned,
                journal.Locked,
                inserted,
                updated)).ConfigureAwait(false);
            await session.ExecuteAsync(new SimpleStatement(
                $"""
                INSERT INTO {_keyspace}.alter_journals_by_alter (
                    user_id, alter_id, id, title, content, color, pinned, locked, inserted_at, updated_at)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                """,
                userId,
                alterId,
                journal.Id,
                journal.Title,
                journal.Content,
                journal.Color,
                journal.Pinned,
                journal.Locked,
                inserted,
                updated)).ConfigureAwait(false);
            return;
        }

        await session.ExecuteAsync(new SimpleStatement(
            $"""
            INSERT INTO {_keyspace}.global_journals (
                user_id, id, title, content, color, pinned, locked, inserted_at, updated_at)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            userId,
            journal.Id,
            journal.Title,
            journal.Content,
            journal.Color,
            journal.Pinned,
            journal.Locked,
            inserted,
            updated)).ConfigureAwait(false);

        foreach (var attached in journal.AlterIds)
        {
            await session.ExecuteAsync(new SimpleStatement(
                $"""
                INSERT INTO {_keyspace}.global_journal_alters (
                    user_id, global_journal_id, alter_id, inserted_at, updated_at)
                VALUES (?, ?, ?, ?, ?)
                """,
                userId,
                journal.Id,
                attached,
                inserted,
                updated)).ConfigureAwait(false);
        }
    }

    private async Task WritePollAsync(ISession session, PollRow poll)
    {
        await session.ExecuteAsync(new SimpleStatement(
            $"""
            INSERT INTO {_keyspace}.polls (
                user_id, id, title, description, type, data, time_end, inserted_at, updated_at)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            SystemIds.Bare(poll.SystemId),
            poll.Id,
            poll.Title,
            poll.Description,
            poll.Type,
            string.IsNullOrEmpty(poll.Data) ? "{}" : poll.Data,
            UnixMs.ToOffset(poll.TimeEndUnixMs),
            UnixMs.ToOffset(poll.InsertedAtUnixMs),
            UnixMs.ToOffset(poll.UpdatedAtUnixMs))).ConfigureAwait(false);
    }

    private async Task<List<string>> ReadRegionsAsync(ISession session, CancellationToken cancellationToken)
    {
        var regions = new List<string>();
        var rows = await session.ExecuteAsync(Page("SELECT region FROM global.user_registry")).ConfigureAwait(false);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!row.IsNull("region"))
            {
                regions.Add(row.GetValue<string>("region"));
            }
        }

        return regions;
    }

    private async Task<List<AccountRow>> ReadAccountsAsync(ISession session, CancellationToken cancellationToken)
    {
        var rows = new List<AccountRow>();
        var result = await session.ExecuteAsync(Page(
            $"""
            SELECT id, email, discord_id, apple_id, google_id, username, description, avatar_url, avatar_source,
                   lifetime_alter_count, primary_front, primary_front_alter, last_proxy_id, discord_settings, fields,
                   salt, encryption_initialized, encryption_key_checksum, inserted_at, updated_at
            FROM {_keyspace}.users
            """)).ConfigureAwait(false);
        foreach (var row in result)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var primary = ReadShort(row, "primary_front_alter");
            var legacy = ReadInt(row, "primary_front");
            if (primary is null && legacy is >= short.MinValue and <= short.MaxValue)
            {
                primary = (short)legacy;
            }

            var salt = ReadText(row, "salt");
            var checksum = ReadText(row, "encryption_key_checksum");
            var initialized = !row.IsNull("encryption_initialized") && row.GetValue<bool>("encryption_initialized");
            EncryptionRow? encryption = salt is null && checksum is null && !initialized
                ? null
                : new EncryptionRow
                {
                    Initialized = initialized,
                    KeyChecksum = checksum,
                    Salt = salt,
                    UpdatedAtUnixMs = ReadUnix(row, "updated_at") ?? 0,
                };

            rows.Add(new AccountRow
            {
                SystemId = SystemIds.Bare(row.GetValue<string>("id")),
                Email = ReadText(row, "email"),
                DiscordId = ReadText(row, "discord_id"),
                AppleId = ReadText(row, "apple_id"),
                GoogleId = ReadText(row, "google_id"),
                Username = ReadText(row, "username"),
                Description = ReadText(row, "description"),
                AvatarUrl = ReadText(row, "avatar_url"),
                AvatarSource = ReadShort(row, "avatar_source"),
                LifetimeAlterCount = ReadInt(row, "lifetime_alter_count"),
                LegacyPrimaryFront = legacy,
                PrimaryFrontAlter = primary,
                LastProxyId = ReadShort(row, "last_proxy_id"),
                DiscordSettings = ReadDiscord(row),
                Fields = ReadFields(row),
                Encryption = encryption,
                CreatedAtUnixMs = ReadUnix(row, "inserted_at") ?? 0,
                UpdatedAtUnixMs = ReadUnix(row, "updated_at") ?? 0,
            });
        }

        return rows;
    }

    private async Task<List<AlterRow>> ReadAltersAsync(ISession session, CancellationToken cancellationToken)
    {
        var rows = new List<AlterRow>();
        var result = await session.ExecuteAsync(Page(
            $"""
            SELECT user_id, id, alias, name, pronouns, description, avatar_url, avatar_source, security_level,
                   extra_images, color, discord_proxies, proxy_name, fields, untracked, archived, pinned,
                   last_fronted, inserted_at, updated_at
            FROM {_keyspace}.alters
            """)).ConfigureAwait(false);
        foreach (var row in result)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add(new AlterRow
            {
                SystemId = SystemIds.Bare(row.GetValue<string>("user_id")),
                Id = row.GetValue<short>("id"),
                Alias = ReadText(row, "alias"),
                Name = ReadText(row, "name") ?? "",
                Pronouns = ReadText(row, "pronouns"),
                Description = ReadText(row, "description"),
                AvatarUrl = ReadText(row, "avatar_url"),
                AvatarSource = ReadShort(row, "avatar_source"),
                SecurityLevel = ReadShort(row, "security_level") ?? 0,
                ExtraImages = ReadTextList(row, "extra_images"),
                Color = ReadText(row, "color"),
                DiscordProxies = ReadTextList(row, "discord_proxies"),
                ProxyName = ReadText(row, "proxy_name"),
                Fields = ReadAlterFields(row),
                Untracked = ReadBool(row, "untracked"),
                Archived = ReadBool(row, "archived"),
                Pinned = ReadBool(row, "pinned"),
                LastFrontedUnixMs = ReadUnix(row, "last_fronted"),
                InsertedAtUnixMs = ReadUnix(row, "inserted_at") ?? 0,
                UpdatedAtUnixMs = ReadUnix(row, "updated_at") ?? 0,
            });
        }

        return rows;
    }

    private async Task<List<TagRow>> ReadTagsAsync(ISession session, CancellationToken cancellationToken)
    {
        var rows = new List<TagRow>();
        var result = await session.ExecuteAsync(Page(
            $"""
            SELECT user_id, id, parent_tag_id, name, description, color, security_level, inserted_at, updated_at
            FROM {_keyspace}.tags
            """)).ConfigureAwait(false);
        foreach (var row in result)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add(new TagRow
            {
                SystemId = SystemIds.Bare(row.GetValue<string>("user_id")),
                Id = row.GetValue<Guid>("id"),
                ParentTagId = row.IsNull("parent_tag_id") ? null : row.GetValue<Guid>("parent_tag_id"),
                Name = ReadText(row, "name") ?? "",
                Description = ReadText(row, "description"),
                Color = ReadText(row, "color"),
                SecurityLevel = ReadShort(row, "security_level") ?? 0,
                InsertedAtUnixMs = ReadUnix(row, "inserted_at") ?? 0,
                UpdatedAtUnixMs = ReadUnix(row, "updated_at") ?? 0,
            });
        }

        return rows;
    }

    private async Task<List<AlterTagRow>> ReadAlterTagsAsync(ISession session, CancellationToken cancellationToken)
    {
        var rows = new List<AlterTagRow>();
        var result = await session.ExecuteAsync(Page(
            $"SELECT user_id, tag_id, alter_id, inserted_at, updated_at FROM {_keyspace}.alter_tags"))
            .ConfigureAwait(false);
        foreach (var row in result)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add(new AlterTagRow
            {
                SystemId = SystemIds.Bare(row.GetValue<string>("user_id")),
                TagId = row.GetValue<Guid>("tag_id"),
                AlterId = row.GetValue<short>("alter_id"),
                InsertedAtUnixMs = ReadUnix(row, "inserted_at") ?? 0,
                UpdatedAtUnixMs = ReadUnix(row, "updated_at") ?? 0,
            });
        }

        return rows;
    }

    private static async Task<List<FriendshipRow>> ReadFriendshipsAsync(ISession session, CancellationToken cancellationToken)
    {
        var rows = new List<FriendshipRow>();
        var result = await session.ExecuteAsync(Page(
            "SELECT user_id, friend_id, level, since FROM global.friendships")).ConfigureAwait(false);
        foreach (var row in result)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add(new FriendshipRow
            {
                UserId = SystemIds.Bare(row.GetValue<string>("user_id")),
                FriendId = SystemIds.Bare(row.GetValue<string>("friend_id")),
                Level = ReadShort(row, "level") ?? 0,
                SinceUnixMs = ReadUnix(row, "since") ?? 0,
            });
        }

        return rows;
    }

    private static async Task<List<FriendRequestRow>> ReadFriendRequestsAsync(
        ISession session,
        CancellationToken cancellationToken)
    {
        var rows = new List<FriendRequestRow>();
        var result = await session.ExecuteAsync(Page(
            "SELECT from_id, to_id, date_sent FROM global.friend_requests")).ConfigureAwait(false);
        foreach (var row in result)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add(new FriendRequestRow
            {
                FromUserId = SystemIds.Bare(row.GetValue<string>("from_id")),
                ToUserId = SystemIds.Bare(row.GetValue<string>("to_id")),
                DateSentUnixMs = ReadUnix(row, "date_sent") ?? 0,
            });
        }

        return rows;
    }

    private static async Task<List<NotificationTokenRow>> ReadTokensAsync(
        ISession session,
        CancellationToken cancellationToken)
    {
        var rows = new List<NotificationTokenRow>();
        var result = await session.ExecuteAsync(Page(
            "SELECT user_id, push_token, inserted_at, updated_at FROM global.notification_tokens"))
            .ConfigureAwait(false);
        foreach (var row in result)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add(new NotificationTokenRow
            {
                SystemId = SystemIds.Bare(row.GetValue<string>("user_id")),
                PushToken = row.GetValue<string>("push_token"),
                InsertedAtUnixMs = ReadUnix(row, "inserted_at") ?? 0,
                UpdatedAtUnixMs = ReadUnix(row, "updated_at") ?? 0,
            });
        }

        return rows;
    }

    private async Task<List<FrontRow>> ReadFrontsAsync(ISession session, CancellationToken cancellationToken)
    {
        var rows = new List<FrontRow>();
        var current = await session.ExecuteAsync(Page(
            $"SELECT user_id, alter_id, id, comment, time_start FROM {_keyspace}.current_fronts"))
            .ConfigureAwait(false);
        foreach (var row in current)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add(new FrontRow
            {
                SystemId = SystemIds.Bare(row.GetValue<string>("user_id")),
                AlterId = row.GetValue<short>("alter_id"),
                Id = row.GetValue<Guid>("id"),
                Comment = ReadText(row, "comment"),
                TimeStartUnixMs = ReadUnix(row, "time_start") ?? 0,
                Current = true,
            });
        }

        var history = await session.ExecuteAsync(Page(
            $"SELECT user_id, id, alter_id, comment, time_start, time_end FROM {_keyspace}.fronts"))
            .ConfigureAwait(false);
        foreach (var row in history)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add(new FrontRow
            {
                SystemId = SystemIds.Bare(row.GetValue<string>("user_id")),
                Id = row.GetValue<Guid>("id"),
                AlterId = row.GetValue<short>("alter_id"),
                Comment = ReadText(row, "comment"),
                TimeStartUnixMs = ReadUnix(row, "time_start") ?? 0,
                TimeEndUnixMs = ReadUnix(row, "time_end"),
                Current = false,
            });
        }

        return rows;
    }

    private async Task<List<JournalRow>> ReadJournalsAsync(ISession session, CancellationToken cancellationToken)
    {
        var attachments = new Dictionary<(string, Guid), List<short>>();
        var links = await session.ExecuteAsync(Page(
            $"SELECT user_id, global_journal_id, alter_id FROM {_keyspace}.global_journal_alters"))
            .ConfigureAwait(false);
        foreach (var row in links)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = (SystemIds.Bare(row.GetValue<string>("user_id")), row.GetValue<Guid>("global_journal_id"));
            if (!attachments.TryGetValue(key, out var list))
            {
                list = [];
                attachments[key] = list;
            }

            list.Add(row.GetValue<short>("alter_id"));
        }

        var rows = new List<JournalRow>();
        var globals = await session.ExecuteAsync(Page(
            $"""
            SELECT user_id, id, title, content, color, pinned, locked, inserted_at, updated_at
            FROM {_keyspace}.global_journals
            """)).ConfigureAwait(false);
        foreach (var row in globals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var systemId = SystemIds.Bare(row.GetValue<string>("user_id"));
            var id = row.GetValue<Guid>("id");
            var journal = new JournalRow
            {
                SystemId = systemId,
                Id = id,
                Title = ReadText(row, "title") ?? "",
                Content = ReadText(row, "content"),
                Color = ReadText(row, "color"),
                Pinned = ReadBool(row, "pinned"),
                Locked = ReadBool(row, "locked"),
                InsertedAtUnixMs = ReadUnix(row, "inserted_at") ?? 0,
                UpdatedAtUnixMs = ReadUnix(row, "updated_at") ?? 0,
            };
            if (attachments.TryGetValue((systemId, id), out var alters))
            {
                journal.AlterIds.AddRange(alters);
            }

            rows.Add(journal);
        }

        var alterJournals = await session.ExecuteAsync(Page(
            $"""
            SELECT user_id, id, alter_id, title, content, color, pinned, locked, inserted_at, updated_at
            FROM {_keyspace}.alter_journals
            """)).ConfigureAwait(false);
        foreach (var row in alterJournals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add(new JournalRow
            {
                SystemId = SystemIds.Bare(row.GetValue<string>("user_id")),
                Id = row.GetValue<Guid>("id"),
                AlterId = row.GetValue<short>("alter_id"),
                Title = ReadText(row, "title") ?? "",
                Content = ReadText(row, "content"),
                Color = ReadText(row, "color"),
                Pinned = ReadBool(row, "pinned"),
                Locked = ReadBool(row, "locked"),
                InsertedAtUnixMs = ReadUnix(row, "inserted_at") ?? 0,
                UpdatedAtUnixMs = ReadUnix(row, "updated_at") ?? 0,
            });
        }

        return rows;
    }

    private async Task<List<PollRow>> ReadPollsAsync(ISession session, CancellationToken cancellationToken)
    {
        var rows = new List<PollRow>();
        var result = await session.ExecuteAsync(Page(
            $"""
            SELECT user_id, id, title, description, type, data, time_end, inserted_at, updated_at
            FROM {_keyspace}.polls
            """)).ConfigureAwait(false);
        foreach (var row in result)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add(new PollRow
            {
                SystemId = SystemIds.Bare(row.GetValue<string>("user_id")),
                Id = row.GetValue<Guid>("id"),
                Title = ReadText(row, "title") ?? "",
                Description = ReadText(row, "description"),
                Type = ReadShort(row, "type") ?? 0,
                Data = ReadText(row, "data") ?? "{}",
                TimeEndUnixMs = ReadUnix(row, "time_end"),
                InsertedAtUnixMs = ReadUnix(row, "inserted_at") ?? 0,
                UpdatedAtUnixMs = ReadUnix(row, "updated_at") ?? 0,
            });
        }

        return rows;
    }

    private async Task<CqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var builder = Cluster.Builder()
            .AddContactPoints(endpoint.ContactPoints.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .WithPort(endpoint.Port)
            .WithLoadBalancingPolicy(new TokenAwarePolicy(new DCAwareRoundRobinPolicy(endpoint.LocalDatacenter)))
            .WithSocketOptions(new SocketOptions().SetConnectTimeoutMillis(10_000));
        if (!string.IsNullOrWhiteSpace(endpoint.Username))
        {
            builder = builder.WithCredentials(endpoint.Username, endpoint.Password ?? "");
        }

        var cluster = builder.Build();
        try
        {
            var session = await cluster.ConnectAsync().ConfigureAwait(false);
            DefineUdts(session);
            return new CqlConnection(session, cluster);
        }
        catch (NoHostAvailableException exception)
        {
            cluster.Dispose();
            throw new MoveRefusedException(
                $"Could not reach CQL at {endpoint.ContactPoints}:{endpoint.Port} (local dc {endpoint.LocalDatacenter}). {exception.Message}");
        }
        catch
        {
            cluster.Dispose();
            throw;
        }
    }

    private void DefineUdts(ISession session)
    {
        try
        {
            session.UserDefinedTypes.Define(
                UdtMap.For<FieldUdt>("field", _keyspace)
                    .Map(field => field.Id, "id")
                    .Map(field => field.Name, "name")
                    .Map(field => field.Type, "type")
                    .Map(field => field.Locked, "locked")
                    .Map(field => field.SecurityLevel, "security_level")
                    .Map(field => field.InsertedAt, "inserted_at")
                    .Map(field => field.UpdatedAt, "updated_at"),
                UdtMap.For<AlterFieldUdt>("alter_field", _keyspace)
                    .Map(field => field.Id, "id")
                    .Map(field => field.Value, "value"),
                UdtMap.For<DiscordServerSettingsUdt>("discord_server_settings", _keyspace)
                    .Map(settings => settings.GuildId, "guild_id")
                    .Map(settings => settings.ProxyingDisabled, "proxying_disabled")
                    .Map(settings => settings.AutoproxyMode, "autoproxy_mode")
                    .Map(settings => settings.LatchedAlter, "latched_alter"),
                UdtMap.For<DiscordSettingsUdt>("discord_settings", _keyspace)
                    .Map(settings => settings.SystemTag, "system_tag")
                    .Map(settings => settings.ShowSystemTag, "show_system_tag")
                    .Map(settings => settings.CaseInsensitiveProxies, "case_insensitive_proxies")
                    .Map(settings => settings.ShowPronouns, "show_pronouns")
                    .Map(settings => settings.IdsAsProxies, "ids_as_proxies")
                    .Map(settings => settings.SilentProxying, "silent_proxying")
                    .Map(settings => settings.UseProxyDelay, "use_proxy_delay")
                    .Map(settings => settings.GlobalAutoproxyMode, "global_autoproxy_mode")
                    .Map(settings => settings.GlobalLatchedAlter, "global_latched_alter")
                    .Map(settings => settings.ServerSettings, "server_settings"));
        }
        catch (InvalidQueryException exception)
        {
            throw new MoveRefusedException(
                $"CQL schema in keyspace '{_keyspace}' is not ready. Bring the stack up so migrations have run. {exception.Message}");
        }
    }

    private static async Task<long> CountAsync(ISession session, string cql, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var rows = await session.ExecuteAsync(new SimpleStatement(cql)).ConfigureAwait(false);
            return rows.First().GetValue<long>("count");
        }
        catch (InvalidQueryException exception)
        {
            throw new MoveRefusedException(
                $"CQL schema is not ready ({cql}). Bring the stack up so migrations have run. {exception.Message}");
        }
    }

    private static SimpleStatement Page(string cql)
    {
        var statement = new SimpleStatement(cql);
        statement.SetPageSize(500);
        return statement;
    }

    private static string ValidateKeyspace(string keyspace)
    {
        if (string.IsNullOrWhiteSpace(keyspace) || !keyspace.Trim().TryParseWire(out ScyllaKeyspace _))
        {
            throw new MoveRefusedException(
                $"Unrecognised keyspace '{keyspace}'. Expected one of nam, eur, sam, sas, eas, ocn, gdpr.");
        }

        return keyspace.Trim();
    }

    private static string? BlankToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? ReadText(Row row, string name) =>
        row.IsNull(name) ? null : row.GetValue<string>(name);

    private static short? ReadShort(Row row, string name) =>
        row.IsNull(name) ? null : row.GetValue<short>(name);

    private static int? ReadInt(Row row, string name) =>
        row.IsNull(name) ? null : row.GetValue<int>(name);

    private static bool ReadBool(Row row, string name) =>
        !row.IsNull(name) && row.GetValue<bool>(name);

    private static long? ReadUnix(Row row, string name) =>
        row.IsNull(name) ? null : row.GetValue<DateTimeOffset>(name).ToUnixTimeMilliseconds();

    private static List<string> ReadTextList(Row row, string name)
    {
        if (row.IsNull(name))
        {
            return [];
        }

        return row.GetValue<IEnumerable<string>>(name)?.Where(item => !string.IsNullOrEmpty(item)).ToList() ?? [];
    }

    private static List<SettingsFieldRow> ReadFields(Row row)
    {
        if (row.IsNull("fields"))
        {
            return [];
        }

        var fields = row.GetValue<IEnumerable<FieldUdt>>("fields")?.ToList() ?? [];
        return fields.Select((field, index) => new SettingsFieldRow
        {
            Id = field.Id,
            Name = field.Name ?? "",
            Type = field.Type,
            SecurityLevel = field.SecurityLevel,
            Locked = field.Locked,
            Index = index,
            InsertedAtUnixMs = UnixMs.From(field.InsertedAt),
            UpdatedAtUnixMs = UnixMs.From(field.UpdatedAt),
        }).ToList();
    }

    private static List<AlterFieldRow> ReadAlterFields(Row row)
    {
        if (row.IsNull("fields"))
        {
            return [];
        }

        return row.GetValue<IEnumerable<AlterFieldUdt>>("fields")?
            .Select(field => new AlterFieldRow { Id = field.Id, Value = field.Value })
            .ToList() ?? [];
    }

    private static DiscordSettingsRow? ReadDiscord(Row row)
    {
        if (row.IsNull("discord_settings"))
        {
            return null;
        }

        var settings = row.GetValue<DiscordSettingsUdt>("discord_settings");
        return new DiscordSettingsRow
        {
            SystemTag = settings.SystemTag,
            ShowSystemTag = settings.ShowSystemTag,
            CaseInsensitiveProxies = settings.CaseInsensitiveProxies,
            ShowPronouns = settings.ShowPronouns,
            IdsAsProxies = settings.IdsAsProxies,
            SilentProxying = settings.SilentProxying,
            UseProxyDelay = settings.UseProxyDelay,
            GlobalAutoproxyMode = settings.GlobalAutoproxyMode,
            GlobalLatchedAlter = settings.GlobalLatchedAlter,
            ServerSettings = settings.ServerSettings?.Select(server => new DiscordServerSettingsRow
            {
                GuildId = server.GuildId,
                ProxyingDisabled = server.ProxyingDisabled,
                AutoproxyMode = server.AutoproxyMode,
                LatchedAlter = server.LatchedAlter,
            }).ToList() ?? [],
        };
    }

    private static DiscordSettingsUdt? ToDiscordUdt(DiscordSettingsRow? settings)
    {
        if (settings is null)
        {
            return null;
        }

        return new DiscordSettingsUdt
        {
            SystemTag = settings.SystemTag,
            ShowSystemTag = settings.ShowSystemTag,
            CaseInsensitiveProxies = settings.CaseInsensitiveProxies,
            ShowPronouns = settings.ShowPronouns,
            IdsAsProxies = settings.IdsAsProxies,
            SilentProxying = settings.SilentProxying,
            UseProxyDelay = settings.UseProxyDelay,
            GlobalAutoproxyMode = settings.GlobalAutoproxyMode,
            GlobalLatchedAlter = settings.GlobalLatchedAlter,
            ServerSettings = settings.ServerSettings.Select(server => new DiscordServerSettingsUdt
            {
                GuildId = server.GuildId,
                ProxyingDisabled = server.ProxyingDisabled,
                AutoproxyMode = server.AutoproxyMode,
                LatchedAlter = server.LatchedAlter,
            }).ToList(),
        };
    }

    private sealed class CqlConnection : IDisposable
    {
        private readonly ISession _session;
        private readonly Cluster _cluster;

        public CqlConnection(ISession session, Cluster cluster)
        {
            _session = session;
            _cluster = cluster;
        }

        public ISession Session => _session;

        public void Dispose()
        {
            _session.Dispose();
            _cluster.Dispose();
        }
    }
}

internal sealed class FieldUdt
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public short Type { get; set; }
    public bool Locked { get; set; }
    public short SecurityLevel { get; set; }
    public DateTimeOffset? InsertedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

internal sealed class AlterFieldUdt
{
    public Guid Id { get; set; }
    public string? Value { get; set; }
}

internal sealed class DiscordServerSettingsUdt
{
    public string? GuildId { get; set; }
    public bool ProxyingDisabled { get; set; }
    public short AutoproxyMode { get; set; }
    public int? LatchedAlter { get; set; }
}

internal sealed class DiscordSettingsUdt
{
    public string? SystemTag { get; set; }
    public bool ShowSystemTag { get; set; }
    public bool CaseInsensitiveProxies { get; set; }
    public bool ShowPronouns { get; set; }
    public bool IdsAsProxies { get; set; }
    public bool SilentProxying { get; set; }
    public bool UseProxyDelay { get; set; }
    public short GlobalAutoproxyMode { get; set; }
    public int? GlobalLatchedAlter { get; set; }
    public List<DiscordServerSettingsUdt>? ServerSettings { get; set; }
}
