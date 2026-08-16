using Interfold.Journals.Contracts.Ids;
using Interfold.Journals.Contracts.Models.Commands;
using Interfold.Settings.Contracts.Models.ImportOperations;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Infrastructure.Sqlite;
using Interfold.Infrastructure.Sqlite.Repository;
using Microsoft.Extensions.Time.Testing;

namespace Interfold.Api.UnitTests.Sqlite;

public sealed class SqliteJournalRepositoryTests
{
    [Test]
    public async Task Global_Create_Update_Attach_List_Delete()
    {
        var path = await SqliteTestDb.CreateMigratedAsync();
        try
        {
            var region = new SqliteRegionContext(ScyllaKeyspace.Nam);
            var repo = new SqliteJournalRepository(SqliteTestDb.Factory(path), region);
            var systemId = new SystemId("journal-user");

            var entryId = await repo.CreateGlobalAsync(systemId, new CreateGlobalJournalEntryCommand("hello"));
            await Assert.That(entryId).IsNotNull();
            await Assert.That(await repo.ExistsGlobalAsync(systemId, entryId!.Value)).IsTrue();

            await Assert.That(await repo.UpdateGlobalAsync(
                systemId,
                new UpdateGlobalJournalEntryCommand(entryId.Value, "hello2", "body", new HexColor("#ff00aa")))).IsTrue();

            await Assert.That(await repo.SetGlobalPinnedAsync(systemId, entryId.Value, true)).IsTrue();
            await Assert.That(await repo.AttachGlobalAlterAsync(systemId, entryId.Value, new AlterId(3))).IsTrue();

            var got = await repo.GetGlobalAsync(systemId, entryId.Value);
            await Assert.That(got).IsNotNull();
            await Assert.That(got!.Title).IsEqualTo("hello2");
            await Assert.That(got.Content).IsEqualTo("body");
            await Assert.That(got.Pinned).IsTrue();
            await Assert.That(got.Alters.Count).IsEqualTo(1);
            await Assert.That(got.Alters[0].Value).IsEqualTo((short)3);
            await Assert.That(got.UserId.Value).IsEqualTo("journal-user");

            var listed = await repo.ListGlobalAsync(systemId);
            await Assert.That(listed.Count).IsEqualTo(1);

            await Assert.That(await repo.DetachGlobalAlterAsync(systemId, entryId.Value, new AlterId(3))).IsTrue();
            await Assert.That(await repo.DeleteGlobalAsync(systemId, entryId.Value)).IsTrue();
            await Assert.That(await repo.ExistsGlobalAsync(systemId, entryId.Value)).IsFalse();
        }
        finally
        {
            SqliteTestDb.Delete(path);
        }
    }

    [Test]
    public async Task Alter_Create_Update_List_CascadeDelete()
    {
        var path = await SqliteTestDb.CreateMigratedAsync();
        try
        {
            var region = new SqliteRegionContext(ScyllaKeyspace.Nam);
            var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-15T12:00:00Z"));
            var repo = new SqliteJournalRepository(SqliteTestDb.Factory(path), region, clock);
            var systemId = new SystemId("alter-journal-user");
            var alterId = new AlterId(7);

            var entryId = await repo.CreateAlterAsync(
                systemId,
                new CreateAlterJournalEntryCommand(alterId, "note", clock.GetUtcNow()));
            await Assert.That(entryId).IsNotNull();

            var globalId = await repo.CreateGlobalAsync(systemId, new CreateGlobalJournalEntryCommand("g"));
            await repo.AttachGlobalAlterAsync(systemId, globalId!.Value, alterId);

            await Assert.That(await repo.UpdateAlterAsync(
                systemId,
                new UpdateAlterJournalEntryCommand(entryId!.Value, "note2", "c", null, clock.GetUtcNow()))).IsTrue();
            await Assert.That(await repo.SetAlterLockedAsync(systemId, entryId.Value, true)).IsTrue();

            var got = await repo.GetAlterAsync(systemId, entryId.Value);
            await Assert.That(got).IsNotNull();
            await Assert.That(got!.Title).IsEqualTo("note2");
            await Assert.That(got.Locked).IsTrue();
            await Assert.That(got.AlterId.Value).IsEqualTo((short)7);

            var listed = await repo.ListAlterAsync(systemId, alterId);
            await Assert.That(listed.Count).IsEqualTo(1);

            var removed = await repo.DeleteAllForAlterAsync(systemId, alterId);
            await Assert.That(removed).IsEqualTo(1);
            await Assert.That(await repo.GetAlterAsync(systemId, entryId.Value)).IsNull();

            var global = await repo.GetGlobalAsync(systemId, globalId.Value);
            await Assert.That(global).IsNotNull();
            await Assert.That(global!.Alters.Count).IsEqualTo(0);
        }
        finally
        {
            SqliteTestDb.Delete(path);
        }
    }
}

public sealed class SqliteImportOperationRepositoryTests
{
    [Test]
    public async Task TryClaim_Collapses_Duplicate_And_Terminal_Releases_Slot()
    {
        var path = await SqliteTestDb.CreateMigratedAsync();
        try
        {
            var region = new SqliteRegionContext(ScyllaKeyspace.Nam);
            var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-02-01T00:00:00Z"));
            var repo = new SqliteImportOperationRepository(SqliteTestDb.Factory(path), region, clock);
            var systemId = new SystemId("importer");
            var key = new IdempotencyKey("idem-1");

            var first = await repo.TryClaimAsync(systemId, ImportOperationKind.SimplyPlural, key);
            await Assert.That(first.IsNew).IsTrue();

            var second = await repo.TryClaimAsync(systemId, ImportOperationKind.SimplyPlural, new IdempotencyKey("idem-2"));
            await Assert.That(second.IsNew).IsFalse();
            await Assert.That(second.OperationId).IsEqualTo(first.OperationId);

            await repo.MarkRunningAsync(systemId, first.OperationId);
            var running = await repo.GetByIdAsync(systemId, first.OperationId);
            await Assert.That(running).IsNotNull();
            await Assert.That(running!.Status).IsEqualTo(ImportOperationStatus.Running);

            await repo.MarkSucceededAsync(systemId, first.OperationId, ImportOperationKind.SimplyPlural, alterCount: 4);
            var done = await repo.GetByIdAsync(systemId, first.OperationId);
            await Assert.That(done!.Status).IsEqualTo(ImportOperationStatus.Succeeded);
            await Assert.That(done.AlterCount).IsEqualTo(4);
            await Assert.That(await repo.GetActiveOperationIdAsync(systemId, ImportOperationKind.SimplyPlural)).IsNull();

            var third = await repo.TryClaimAsync(systemId, ImportOperationKind.SimplyPlural, new IdempotencyKey("idem-3"));
            await Assert.That(third.IsNew).IsTrue();
            await Assert.That(third.OperationId).IsNotEqualTo(first.OperationId);
        }
        finally
        {
            SqliteTestDb.Delete(path);
        }
    }

    [Test]
    public async Task GetStaleRunning_Returns_Only_Older_Than_Cutoff()
    {
        var path = await SqliteTestDb.CreateMigratedAsync();
        try
        {
            var region = new SqliteRegionContext(ScyllaKeyspace.Nam);
            var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-02-01T00:00:00Z"));
            var repo = new SqliteImportOperationRepository(SqliteTestDb.Factory(path), region, clock);
            var systemId = new SystemId("stale-user");

            var claim = await repo.TryClaimAsync(systemId, ImportOperationKind.PluralKit, new IdempotencyKey("s1"));
            await repo.MarkRunningAsync(systemId, claim.OperationId);

            clock.Advance(TimeSpan.FromHours(2));
            var stale = await repo.GetStaleRunningAsync(TimeSpan.FromHours(1));
            await Assert.That(stale.Count).IsEqualTo(1);
            await Assert.That(stale[0].OperationId).IsEqualTo(claim.OperationId);

            var fresh = await repo.GetStaleRunningAsync(TimeSpan.FromHours(3));
            await Assert.That(fresh.Count).IsEqualTo(0);
        }
        finally
        {
            SqliteTestDb.Delete(path);
        }
    }

    [Test]
    public async Task MarkFailed_Releases_Slot()
    {
        var path = await SqliteTestDb.CreateMigratedAsync();
        try
        {
            var region = new SqliteRegionContext(ScyllaKeyspace.Nam);
            var repo = new SqliteImportOperationRepository(SqliteTestDb.Factory(path), region);
            var systemId = new SystemId("fail-user");

            var claim = await repo.TryClaimAsync(systemId, ImportOperationKind.SimplyPlural, new IdempotencyKey("f1"));
            await repo.MarkRunningAsync(systemId, claim.OperationId);
            await repo.MarkFailedAsync(
                systemId,
                claim.OperationId,
                ImportOperationKind.SimplyPlural,
                ImportErrorCode.SpAuthFailed,
                "bad token");

            var snap = await repo.GetByIdAsync(systemId, claim.OperationId);
            await Assert.That(snap!.Status).IsEqualTo(ImportOperationStatus.Failed);
            await Assert.That(snap.ErrorCode).IsEqualTo(ImportErrorCode.SpAuthFailed);
            await Assert.That(await repo.GetActiveOperationIdAsync(systemId, ImportOperationKind.SimplyPlural)).IsNull();
        }
        finally
        {
            SqliteTestDb.Delete(path);
        }
    }
}
