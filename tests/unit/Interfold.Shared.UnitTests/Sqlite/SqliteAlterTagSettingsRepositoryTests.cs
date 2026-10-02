using Interfold.Alters.Contracts.Models.Commands;
using Interfold.Infrastructure.Sqlite;
using Interfold.Infrastructure.Sqlite.Repository;
using Interfold.Settings.Domain;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Tags.Contracts.Models.Commands;
using Microsoft.Extensions.Logging.Abstractions;

namespace Interfold.Api.UnitTests.Sqlite;

public sealed class SqliteAlterRepositoryTests
{
    [Test]
    public async Task Create_Then_Get_RoundTrip()
    {
        var path = await SqliteTestDb.CreateMigratedAsync();
        try
        {
            var factory = SqliteTestDb.Factory(path);
            var friendships = new SqliteFriendshipRepository(factory, TimeProvider.System);
            var settings = new SqliteSettingsFieldRepository(factory, TimeProvider.System);
            var alterFields = new AlterFieldDefinitionsAdapter(settings, NullLogger<AlterFieldDefinitionsAdapter>.Instance);
            var alters = new SqliteAlterRepository(
                factory, friendships, settings, alterFields,
                NullLogger<SqliteAlterRepository>.Instance);

            var systemId = new SystemId("alter01");
            var created = await alters.CreateAsync(systemId, new CreateAlterCommand("Nova", DateTimeOffset.UtcNow));
            await Assert.That(created.HasValue).IsTrue();
            var alterId = created!.Value;

            var got = await alters.GetAsync(systemId, alterId);
            await Assert.That(got).IsNotNull();
            await Assert.That(got!.Name).IsEqualTo("Nova");
            await Assert.That(got.SecurityLevel).IsEqualTo(VisibilityLevel.Private);
            await Assert.That(await alters.ExistsAsync(systemId, alterId)).IsTrue();
        }
        finally
        {
            SqliteTestDb.Delete(path);
        }
    }
}

public sealed class SqliteTagRepositoryTests
{
    [Test]
    public async Task Create_Then_Get_RoundTrip()
    {
        var path = await SqliteTestDb.CreateMigratedAsync();
        try
        {
            var factory = SqliteTestDb.Factory(path);
            var friendships = new SqliteFriendshipRepository(factory, TimeProvider.System);
            var settings = new SqliteSettingsFieldRepository(factory, TimeProvider.System);
            var alterFields = new AlterFieldDefinitionsAdapter(settings, NullLogger<AlterFieldDefinitionsAdapter>.Instance);
            var alters = new SqliteAlterRepository(
                factory, friendships, settings, alterFields,
                NullLogger<SqliteAlterRepository>.Instance);
            var tags = new SqliteTagRepository(
                factory, friendships, alters, NullLogger<SqliteTagRepository>.Instance, TimeProvider.System);

            var systemId = new SystemId("tag0001");
            var created = await tags.CreateAsync(
                systemId, new CreateTagCommand("Core", ParentTagId: null, DateTime.UtcNow));
            await Assert.That(created.HasValue).IsTrue();
            var tagId = created!.Value;

            var got = await tags.GetAsync(systemId, tagId);
            await Assert.That(got).IsNotNull();
            await Assert.That(got!.Name).IsEqualTo("Core");
            await Assert.That(got.SecurityLevel).IsEqualTo(VisibilityLevel.Private);
            await Assert.That(await tags.ExistsAsync(systemId, tagId)).IsTrue();
        }
        finally
        {
            SqliteTestDb.Delete(path);
        }
    }

    [Test]
    public async Task UncommittedTagWipe_LeavesEveryTag()
    {
        var path = await SqliteTestDb.CreateMigratedAsync();
        try
        {
            var factory = SqliteTestDb.Factory(path);
            var friendships = new SqliteFriendshipRepository(factory, TimeProvider.System);
            var settings = new SqliteSettingsFieldRepository(factory, TimeProvider.System);
            var alterFields = new AlterFieldDefinitionsAdapter(settings, NullLogger<AlterFieldDefinitionsAdapter>.Instance);
            var alters = new SqliteAlterRepository(
                factory, friendships, settings, alterFields,
                NullLogger<SqliteAlterRepository>.Instance);
            var tags = new SqliteTagRepository(
                factory, friendships, alters, NullLogger<SqliteTagRepository>.Instance, TimeProvider.System);

            var systemId = new SystemId("tag0002");
            var first = await tags.CreateAsync(systemId, new CreateTagCommand("Core", ParentTagId: null, DateTime.UtcNow));
            var second = await tags.CreateAsync(systemId, new CreateTagCommand("Side", ParentTagId: null, DateTime.UtcNow));
            await Assert.That(first).IsNotNull();
            await Assert.That(second).IsNotNull();

            var transactions = new SqliteStorageTransactionFactory(factory);
            await using (var transaction = await transactions.BeginAsync())
            {
                await Assert.That(await tags.DeleteAsync(systemId, first!.Value)).IsTrue();
                await Assert.That(await tags.DeleteAsync(systemId, second!.Value)).IsTrue();
            }

            await Assert.That(await tags.ExistsAsync(systemId, first.Value)).IsTrue();
            await Assert.That(await tags.ExistsAsync(systemId, second.Value)).IsTrue();

            await using (var transaction = await transactions.BeginAsync())
            {
                await Assert.That(await tags.DeleteAsync(systemId, first.Value)).IsTrue();
                await Assert.That(await tags.DeleteAsync(systemId, second.Value)).IsTrue();
                await transaction.CommitAsync();
            }

            await Assert.That(await tags.ExistsAsync(systemId, first.Value)).IsFalse();
            await Assert.That(await tags.ExistsAsync(systemId, second.Value)).IsFalse();
        }
        finally
        {
            SqliteTestDb.Delete(path);
        }
    }
}

public sealed class SqliteSettingsFieldRepositoryTests
{
    [Test]
    public async Task Create_Then_List_RoundTrip()
    {
        var path = await SqliteTestDb.CreateMigratedAsync();
        try
        {
            var factory = SqliteTestDb.Factory(path);
            var settings = new SqliteSettingsFieldRepository(factory, TimeProvider.System);

            var systemId = new SystemId("field01");
            var fieldId = await settings.CreateAsync(
                systemId,
                name: "Age",
                type: FieldType.Number,
                securityLevel: VisibilityLevel.FriendsOnly,
                locked: false,
                insertedAtUtc: DateTime.UtcNow);
            await Assert.That(fieldId).IsNotNull();

            var list = await settings.ListAsync(systemId);
            await Assert.That(list.Count).IsEqualTo(1);
            await Assert.That(list[0].Id).IsEqualTo(fieldId!);
            await Assert.That(list[0].Name).IsEqualTo("Age");
            await Assert.That(list[0].Type).IsEqualTo(FieldType.Number);
            await Assert.That(list[0].SecurityLevel).IsEqualTo(VisibilityLevel.FriendsOnly);
            await Assert.That(list[0].Index).IsEqualTo(0);
        }
        finally
        {
            SqliteTestDb.Delete(path);
        }
    }

    [Test]
    public async Task DeleteField_RollsBackAlterValuesWithTheField()
    {
        var path = await SqliteTestDb.CreateMigratedAsync();
        try
        {
            var factory = SqliteTestDb.Factory(path);
            var friendships = new SqliteFriendshipRepository(factory, TimeProvider.System);
            var settings = new SqliteSettingsFieldRepository(factory, TimeProvider.System);
            var alterFields = new AlterFieldDefinitionsAdapter(settings, NullLogger<AlterFieldDefinitionsAdapter>.Instance);
            var alters = new SqliteAlterRepository(
                factory, friendships, settings, alterFields,
                NullLogger<SqliteAlterRepository>.Instance);
            var transactions = new SqliteStorageTransactionFactory(factory);

            var systemId = new SystemId("field02");
            var fieldId = await settings.CreateAsync(
                systemId,
                name: "Age",
                type: FieldType.Number,
                securityLevel: VisibilityLevel.FriendsOnly,
                locked: false,
                insertedAtUtc: DateTime.UtcNow);
            await Assert.That(fieldId).IsNotNull();

            var alterId = await alters.CreateAsync(systemId, new CreateAlterCommand("Nova", DateTimeOffset.UtcNow));
            await Assert.That(alterId).IsNotNull();
            await alters.UpdateAsync(systemId, new UpdateAlterCommand
            {
                AlterId = alterId!.Value,
                Name = "Nova",
                Fields = [new AlterFieldCommand(fieldId!.Value, "12")],
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            await using (var transaction = await transactions.BeginAsync())
            {
                await Assert.That(await settings.DeleteAsync(systemId, fieldId.Value)).IsTrue();
                await alters.RemoveFieldValuesAsync(systemId, fieldId.Value);
            }

            await Assert.That((await settings.ListAsync(systemId)).Count).IsEqualTo(1);
            var kept = await alters.GetAsync(systemId, alterId.Value);
            await Assert.That(kept!.Fields.Single().Value).IsEqualTo("12");

            await using (var transaction = await transactions.BeginAsync())
            {
                await Assert.That(await settings.DeleteAsync(systemId, fieldId.Value)).IsTrue();
                await alters.RemoveFieldValuesAsync(systemId, fieldId.Value);
                await transaction.CommitAsync();
            }

            await Assert.That((await settings.ListAsync(systemId)).Count).IsEqualTo(0);
        }
        finally
        {
            SqliteTestDb.Delete(path);
        }
    }
}
