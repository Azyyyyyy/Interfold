using Microsoft.Data.Sqlite;

namespace Interfold.StackMove.UnitTests;

public sealed class MappingTests
{
    [Test]
    public async Task BareIdStripsOneRegionPrefix()
    {
        await Assert.That(SystemIds.Bare("nam:abcdefg")).IsEqualTo("abcdefg");
        await Assert.That(SystemIds.Bare("abcdefg")).IsEqualTo("abcdefg");
    }

    [Test]
    public async Task GuidTextUsesNFormat()
    {
        var id = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var text = GuidText.Format(id);

        await Assert.That(text).IsEqualTo("00112233445566778899aabbccddeeff");
        await Assert.That(GuidText.Parse(text)).IsEqualTo(id);
    }

    [Test]
    public async Task UnixMsRoundTrips()
    {
        const long unixMs = 1_700_000_000_000;
        await Assert.That(UnixMs.From(UnixMs.ToOffset(unixMs))).IsEqualTo(unixMs);
    }

    [Test]
    public async Task EnumOrdinalSurvivesSqliteRoundTrip()
    {
        var fieldId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var snapshot = Sample(fieldId, type: 2, createdAt: 1_700_000_000_000);
        var path = TempDatabase();
        try
        {
            var store = new SqliteStackStore(path);
            await store.WriteAsync(snapshot, snapshot.Secrets, CancellationToken.None);
            var read = await store.ReadAsync(CancellationToken.None);
            var field = read.Accounts.Single().Fields.Single();

            await Assert.That(field.Type).IsEqualTo((short)2);
            await Assert.That(field.InsertedAtUnixMs).IsEqualTo(1_700_000_000_000);
            await Assert.That(read.Accounts.Single().SystemId).IsEqualTo("abcdefg");
            await Assert.That(GuidText.Format(field.Id)).IsEqualTo(GuidText.Format(fieldId));
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public async Task FriendshipDirectionsRoundTrip()
    {
        var snapshot = Sample(Guid.NewGuid(), type: 0, createdAt: 10);
        snapshot.Friendships.Add(new FriendshipRow
        {
            UserId = "nam:abcdefg",
            FriendId = "hhhhhhh",
            Level = 3,
            SinceUnixMs = 50,
        });
        snapshot.Friendships.Add(new FriendshipRow
        {
            UserId = "hhhhhhh",
            FriendId = "abcdefg",
            Level = 3,
            SinceUnixMs = 50,
        });

        var companion = FriendshipCompanion.For(snapshot.Friendships[0]);
        await Assert.That(companion.FriendId).IsEqualTo("hhhhhhh");
        await Assert.That(companion.UserId).IsEqualTo("abcdefg");
        await Assert.That(companion.Level).IsEqualTo((short)3);

        var path = TempDatabase();
        try
        {
            var store = new SqliteStackStore(path);
            await store.WriteAsync(snapshot, [], CancellationToken.None);
            var read = await store.ReadAsync(CancellationToken.None);
            await Assert.That(read.Friendships.Count).IsEqualTo(2);
            await Assert.That(read.Friendships.Any(row => row.UserId == "abcdefg" && row.FriendId == "hhhhhhh")).IsTrue();
            await Assert.That(read.Friendships.Any(row => row.UserId == "hhhhhhh" && row.FriendId == "abcdefg")).IsTrue();
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public async Task OccupiedSqliteFileIsNotEmpty()
    {
        var path = TempDatabase();
        try
        {
            var store = new SqliteStackStore(path);
            await store.WriteAsync(Sample(Guid.NewGuid(), 1, 10), [], CancellationToken.None);
            var count = await store.CountAccountsAsync(CancellationToken.None);
            await Assert.That(count).IsEqualTo(1);
            await Assert.That(() => PopulationGuard.EnsureEmpty(count, "SQLite database")).ThrowsExactly<MoveRefusedException>();
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    private static StackSnapshot Sample(Guid fieldId, short type, long createdAt) => new()
    {
        Accounts =
        [
            new AccountRow
            {
                SystemId = "nam:abcdefg",
                Username = "ada",
                CreatedAtUnixMs = createdAt,
                UpdatedAtUnixMs = createdAt,
                Fields =
                [
                    new SettingsFieldRow
                    {
                        Id = fieldId,
                        Name = "color",
                        Type = type,
                        SecurityLevel = 1,
                        Index = 0,
                        InsertedAtUnixMs = createdAt,
                        UpdatedAtUnixMs = createdAt,
                    },
                ],
                Encryption = new EncryptionRow
                {
                    Initialized = true,
                    Salt = "salt",
                    UpdatedAtUnixMs = createdAt,
                },
            },
        ],
        Secrets =
        [
            new SecretRow
            {
                Key = "encryption:pepper",
                Value = "pepper",
                CreatedAtUnixMs = createdAt,
                UpdatedAtUnixMs = createdAt,
            },
        ],
    };

    private static string TempDatabase() =>
        Path.Combine(Path.GetTempPath(), $"interfold-stack-move-{Guid.NewGuid():N}", "interfold.db");

    private static void DeleteDatabase(string path)
    {
        SqliteConnection.ClearAllPools();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
