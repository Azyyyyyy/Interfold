using Interfold.Infrastructure.Sqlite;
using Interfold.Settings.Domain;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Infrastructure.Sqlite.Repository;
using Microsoft.Extensions.Logging.Abstractions;

namespace Interfold.Api.UnitTests;

// Coverage for the unbounded fronts read the export path needs — reference exports
// every front that has a time_start (accounts.ex:1215). The two existing reads on
// IFrontingRepository (ListActiveAsync + ListHistoryBetweenAsync) cannot cover that
// contract: ListActiveAsync drops closed rows, ListHistoryBetweenAsync stitches
// fronts_by_time which is only populated on close.
public sealed class SqliteFrontingRepositoryListAllTests
{
    private static readonly SystemId OwnerId = new("owner01");
    private static readonly AlterId AlterOne = new(1);
    private static readonly AlterId AlterTwo = new(2);

    private static async Task<(SqliteFrontingRepository Repo, string DbPath)> BuildRepositoryAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"interfold-fronting-ut-{Guid.NewGuid():N}.db");
        var cs = $"Data Source={dbPath}";
        await SqliteMigrationService.MigrateAsync(cs, NullLogger.Instance, CancellationToken.None);
        var factory = new SqliteConnectionFactory(cs);
        var encryption = new SqliteEncryptionStateRepository(factory, TimeProvider.System);
        var accounts = new SqliteAccountRepository(factory, encryption, TimeProvider.System);
        var friendships = new SqliteFriendshipRepository(factory, TimeProvider.System, accounts);

        var fields = new SqliteSettingsFieldRepository(factory, TimeProvider.System);
        var alterFieldDefinitions = new AlterFieldDefinitionsAdapter(fields, NullLogger<AlterFieldDefinitionsAdapter>.Instance);
        var alters = new SqliteAlterRepository(factory, friendships, fields, alterFieldDefinitions, NullLogger<SqliteAlterRepository>.Instance);
        var repo = new SqliteFrontingRepository(factory, friendships, alters, NullLogger<SqliteFrontingRepository>.Instance, TimeProvider.System);
        return (repo, dbPath);
    }

    [Test]
    public async Task ListAllAsync_ReturnsOpenAndClosedFrontsOrderedByStartDesc()
    {
        var (repo, dbPath) = await BuildRepositoryAsync();
        try
        {
            var closed = new DateTimeOffset(2024, 4, 1, 0, 0, 0, TimeSpan.Zero);
            var open = new DateTimeOffset(2024, 5, 1, 0, 0, 0, TimeSpan.Zero);

            _ = await repo.StartAsync(OwnerId, AlterOne, comment: "closed", startedAt: closed);
            await repo.EndAsync(OwnerId, AlterOne, endedAt: closed.AddDays(1));
            _ = await repo.StartAsync(OwnerId, AlterTwo, comment: "open", startedAt: open);

            var all = await repo.ListAllAsync(OwnerId);

            using (Assert.Multiple())
            {
                await Assert.That(all.Count).IsEqualTo(2)
                    .Because("ListAllAsync must surface both open and closed fronts; ListActiveAsync/ListHistoryBetweenAsync individually can't.");
                await Assert.That(all[0].TimeStart).IsEqualTo(open)
                    .Because("Ordered by time_start descending so the export writes newest fronts first.");
                await Assert.That(all[0].TimeEnd).IsNull();
                await Assert.That(all[1].TimeStart).IsEqualTo(closed);
                await Assert.That(all[1].TimeEnd).IsEqualTo(closed.AddDays(1));
            }
        }
        finally
        {
            CleanDb(dbPath);
        }
    }

    [Test]
    public async Task ListAllAsync_EmptyStore_ReturnsEmptyList()
    {
        var (repo, dbPath) = await BuildRepositoryAsync();
        try
        {
            var all = await repo.ListAllAsync(OwnerId);
            await Assert.That(all.Count).IsEqualTo(0);
        }
        finally
        {
            CleanDb(dbPath);
        }
    }

    private static void CleanDb(string path)
    {
        foreach (var p in new[] { path, path + "-wal", path + "-shm" })
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { }
        }
    }
}
