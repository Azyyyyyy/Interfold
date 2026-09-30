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
    private static readonly string[] UserIds = ["smvtest1", "smvtest2"];

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

        if (await new CqlStackStore(endpoint).CountPopulationAsync(CancellationToken.None) > 0)
        {
            throw new SkipTestException("The shared nam keyspace already has rows.");
        }

        var existingSecrets = await PostgresSecrets.ReadAsync(postgres, CancellationToken.None);
        var originalPepper = existingSecrets.FirstOrDefault(secret => secret.Key == "encryption:pepper");
        var directory = Path.Combine(Path.GetTempPath(), $"interfold-stack-move-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var sourcePath = Path.Combine(directory, "source.db");
        var backPath = Path.Combine(directory, "back.db");
        const long createdAt = 1_700_000_000_000;
        var fieldId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");

        try
        {
            await new SqliteStackStore(sourcePath).WriteAsync(Sample(fieldId, createdAt), Sample(fieldId, createdAt).Secrets, CancellationToken.None);

            var toCql = await StackMover.ExecuteAsync(new MoveRequest
            {
                From = StackKind.Sqlite,
                To = StackKind.Scylla,
                SourceSqlitePath = sourcePath,
                TargetCql = endpoint,
            }, new StringWriter(), CancellationToken.None);
            await Assert.That(toCql).IsEqualTo(0);

            var toSqlite = await StackMover.ExecuteAsync(new MoveRequest
            {
                From = StackKind.Scylla,
                To = StackKind.Sqlite,
                SourceCql = endpoint,
                TargetSqlitePath = backPath,
            }, new StringWriter(), CancellationToken.None);
            await Assert.That(toSqlite).IsEqualTo(0);

            var read = await new SqliteStackStore(backPath).ReadAsync(CancellationToken.None);
            var account = read.Accounts.Single(row => row.SystemId == "smvtest1");
            await Assert.That(account.Fields.Single().Type).IsEqualTo((short)4);
            await Assert.That(account.Fields.Single().InsertedAtUnixMs).IsEqualTo(createdAt);
            await Assert.That(account.Encryption!.Salt).IsEqualTo("salt");
            await Assert.That(read.Alters.Single(alter => alter.SystemId == "smvtest1").Name).IsEqualTo("Ada");
            await Assert.That(read.Friendships.Count(row => row.UserId == "smvtest1" && row.FriendId == "smvtest2")).IsEqualTo(1);
            await Assert.That(read.Friendships.Count(row => row.UserId == "smvtest2" && row.FriendId == "smvtest1")).IsEqualTo(1);
            await Assert.That(read.Secrets.Single(secret => secret.Key == "encryption:pepper").Value).IsEqualTo("moved-pepper");
        }
        finally
        {
            await DeleteRowsAsync(endpoint);
            if (originalPepper is not null)
            {
                await PostgresSecrets.UpsertAsync(postgres, [originalPepper], CancellationToken.None);
            }

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static StackSnapshot Sample(Guid fieldId, long createdAt) => new()
    {
        Accounts =
        [
            Account("smvtest1", "smv-user", fieldId, createdAt),
            new AccountRow
            {
                SystemId = "smvtest2",
                Username = "smv-friend",
                CreatedAtUnixMs = createdAt,
                UpdatedAtUnixMs = createdAt,
            },
        ],
        Alters =
        [
            new AlterRow
            {
                SystemId = "smvtest1",
                Id = 1,
                Name = "Ada",
                SecurityLevel = 1,
                InsertedAtUnixMs = createdAt,
                UpdatedAtUnixMs = createdAt,
            },
        ],
        Friendships =
        [
            new FriendshipRow { UserId = "smvtest1", FriendId = "smvtest2", Level = 2, SinceUnixMs = createdAt },
            new FriendshipRow { UserId = "smvtest2", FriendId = "smvtest1", Level = 2, SinceUnixMs = createdAt },
        ],
        Secrets =
        [
            new SecretRow
            {
                Key = "encryption:pepper",
                Value = "moved-pepper",
                CreatedAtUnixMs = createdAt,
                UpdatedAtUnixMs = createdAt,
            },
        ],
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

    private static async Task DeleteRowsAsync(CqlEndpoint endpoint)
    {
        var builder = Cluster.Builder()
            .AddContactPoint("127.0.0.1")
            .WithPort(endpoint.Port)
            .WithCredentials(endpoint.Username, endpoint.Password);
        using var cluster = builder.Build();
        using var session = await cluster.ConnectAsync();
        foreach (var id in UserIds)
        {
            await session.ExecuteAsync(new SimpleStatement("DELETE FROM nam.users WHERE id = ?", id));
            await session.ExecuteAsync(new SimpleStatement("DELETE FROM nam.alters WHERE user_id = ?", id));
            await session.ExecuteAsync(new SimpleStatement("DELETE FROM global.user_registry WHERE user_id = ?", id));
            await session.ExecuteAsync(new SimpleStatement("DELETE FROM global.friendships WHERE user_id = ?", id));
            await session.ExecuteAsync(new SimpleStatement(
                "DELETE FROM global.friendships_by_friend_id WHERE friend_id = ?", id));
        }

        await session.ExecuteAsync(new SimpleStatement(
            "DELETE FROM nam.users_by_username WHERE username = ?", "smv-user"));
        await session.ExecuteAsync(new SimpleStatement(
            "DELETE FROM nam.users_by_username WHERE username = ?", "smv-friend"));
        await session.ExecuteAsync(new SimpleStatement(
            "DELETE FROM global.user_registry_by_username WHERE username = ?", "smv-user"));
        await session.ExecuteAsync(new SimpleStatement(
            "DELETE FROM global.user_registry_by_username WHERE username = ?", "smv-friend"));
    }
}
