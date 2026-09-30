using Cassandra;
using Interfold.IntegrationTests.Shared.TestServices;
using Interfold.StackMove;
using Npgsql;
using TUnit.Core.Exceptions;

namespace Interfold.Infrastructure.IntegrationTests.Services.StackMove;

[ClassDataSource<ScyllaWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel("stack-move-cql")]
public sealed class StackMoveRoundTripTests(ScyllaWebFactoryFixture fixture)
{
    [Test]
    public async Task SqliteRoundTripsThroughTheSharedCqlCopier()
    {
        var shared = fixture.Aspire;
        if (shared.ScyllaPort is not int port)
        {
            throw new SkipTestException("Scylla is not available.");
        }

        // The app role only has SELECT on internal.secrets (001_grant_secrets_access.sql).
        var postgres = await AdminPostgresConnectionStringAsync(shared.PostgresConnectionString);
        var endpoint = new CqlEndpoint(
            postgres,
            "127.0.0.1",
            port,
            TestDbCredentials.ScyllaAppUser,
            TestDbCredentials.ScyllaAppPassword,
            "nam",
            "nam");

        // The suite already owns rows in nam and global.user_registry. StackMover refuses
        // that mix; PopulationGuard covers the refusal. This test drives the copiers.
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var userA = "smva" + suffix;
        var userB = "smvb" + suffix;
        var nameA = "smv-user-" + suffix;
        var nameB = "smv-friend-" + suffix;
        var existingSecrets = await PostgresSecrets.ReadAsync(postgres, CancellationToken.None);
        var originalPepper = existingSecrets.Single(secret => secret.Key == "encryption:pepper");
        var directory = Path.Combine(Path.GetTempPath(), $"interfold-stack-move-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var sourcePath = Path.Combine(directory, "source.db");
        var backPath = Path.Combine(directory, "back.db");
        const long createdAt = 1_700_000_000_000;
        var fieldId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var cql = new CqlStackStore(endpoint);

        try
        {
            var sample = Sample(userA, userB, nameA, nameB, fieldId, createdAt, originalPepper);
            await new SqliteStackStore(sourcePath).WriteAsync(sample, sample.Secrets, CancellationToken.None);
            var fromSqlite = await new SqliteStackStore(sourcePath).ReadAsync(CancellationToken.None);
            await cql.WriteAsync(fromSqlite, fromSqlite.Secrets, CancellationToken.None);

            var fromCql = Slice(await cql.ReadAsync(CancellationToken.None), userA, userB);
            await new SqliteStackStore(backPath).WriteAsync(fromCql, fromCql.Secrets, CancellationToken.None);

            var read = await new SqliteStackStore(backPath).ReadAsync(CancellationToken.None);
            var account = read.Accounts.Single(row => row.SystemId == userA);
            await Assert.That(account.Fields.Single().Type).IsEqualTo((short)4);
            await Assert.That(account.Fields.Single().InsertedAtUnixMs).IsEqualTo(createdAt);
            await Assert.That(account.Encryption!.Salt).IsEqualTo("salt");
            await Assert.That(read.Alters.Single(alter => alter.SystemId == userA).Name).IsEqualTo("Ada");
            await Assert.That(read.Friendships.Count(row => row.UserId == userA && row.FriendId == userB)).IsEqualTo(1);
            await Assert.That(read.Friendships.Count(row => row.UserId == userB && row.FriendId == userA)).IsEqualTo(1);
            await Assert.That(read.Secrets.Single(secret => secret.Key == "encryption:pepper").Value).IsEqualTo(originalPepper.Value);
        }
        finally
        {
            await DeleteRowsAsync(endpoint, userA, userB, nameA, nameB);
            await PostgresSecrets.UpsertAsync(postgres, [originalPepper], CancellationToken.None);

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static StackSnapshot Slice(StackSnapshot source, string userA, string userB) => new()
    {
        Accounts = source.Accounts.Where(row => row.SystemId == userA || row.SystemId == userB).ToList(),
        Alters = source.Alters.Where(row => row.SystemId == userA || row.SystemId == userB).ToList(),
        Friendships = source.Friendships.Where(row =>
            (row.UserId == userA && row.FriendId == userB) ||
            (row.UserId == userB && row.FriendId == userA)).ToList(),
        Secrets = source.Secrets.Where(secret => secret.Key == "encryption:pepper").ToList(),
    };

    private static StackSnapshot Sample(
        string userA,
        string userB,
        string nameA,
        string nameB,
        Guid fieldId,
        long createdAt,
        SecretRow pepper) => new()
    {
        Accounts =
        [
            Account(userA, nameA, fieldId, createdAt),
            new AccountRow
            {
                SystemId = userB,
                Username = nameB,
                CreatedAtUnixMs = createdAt,
                UpdatedAtUnixMs = createdAt,
            },
        ],
        Alters =
        [
            new AlterRow
            {
                SystemId = userA,
                Id = 1,
                Name = "Ada",
                SecurityLevel = 1,
                InsertedAtUnixMs = createdAt,
                UpdatedAtUnixMs = createdAt,
            },
        ],
        Friendships =
        [
            new FriendshipRow { UserId = userA, FriendId = userB, Level = 2, SinceUnixMs = createdAt },
            new FriendshipRow { UserId = userB, FriendId = userA, Level = 2, SinceUnixMs = createdAt },
        ],
        Secrets = [pepper],
    };

    private static AccountRow Account(string id, string username, Guid fieldId, long createdAt) => new()
    {
        SystemId = id,
        Username = username,
        CreatedAtUnixMs = createdAt,
        UpdatedAtUnixMs = createdAt,
        Encryption = new EncryptionRow { Initialized = true, Salt = "salt", UpdatedAtUnixMs = createdAt },
        Fields =
        [
            new SettingsFieldRow
            {
                Id = fieldId,
                Name = "note",
                Type = 4,
                SecurityLevel = 1,
                Index = 0,
                InsertedAtUnixMs = createdAt,
                UpdatedAtUnixMs = createdAt,
            },
        ],
    };

    private static async Task<string> AdminPostgresConnectionStringAsync(string appConnectionString)
    {
        var secrets = await PostgresSecrets.ReadAsync(appConnectionString, CancellationToken.None);
        var builder = new NpgsqlConnectionStringBuilder(appConnectionString)
        {
            Username = secrets.Single(secret => secret.Key == "postgres:admin_username").Value,
            Password = secrets.Single(secret => secret.Key == "postgres:admin_password").Value,
        };
        return builder.ConnectionString;
    }

    private static async Task DeleteRowsAsync(
        CqlEndpoint endpoint,
        string userA,
        string userB,
        string nameA,
        string nameB)
    {
        var builder = Cluster.Builder()
            .AddContactPoint("127.0.0.1")
            .WithPort(endpoint.Port)
            .WithCredentials(endpoint.Username, endpoint.Password);
        using var cluster = builder.Build();
        using var session = await cluster.ConnectAsync();
        foreach (var id in new[] { userA, userB })
        {
            await session.ExecuteAsync(new SimpleStatement("DELETE FROM nam.users WHERE id = ?", id));
            await session.ExecuteAsync(new SimpleStatement("DELETE FROM nam.alters WHERE user_id = ?", id));
            await session.ExecuteAsync(new SimpleStatement("DELETE FROM global.user_registry WHERE user_id = ?", id));
            await session.ExecuteAsync(new SimpleStatement("DELETE FROM global.friendships WHERE user_id = ?", id));
            await session.ExecuteAsync(new SimpleStatement(
                "DELETE FROM global.friendships_by_friend_id WHERE friend_id = ?", id));
        }

        foreach (var username in new[] { nameA, nameB })
        {
            await session.ExecuteAsync(new SimpleStatement(
                "DELETE FROM nam.users_by_username WHERE username = ?", username));
            await session.ExecuteAsync(new SimpleStatement(
                "DELETE FROM global.user_registry_by_username WHERE username = ?", username));
        }
    }
}
