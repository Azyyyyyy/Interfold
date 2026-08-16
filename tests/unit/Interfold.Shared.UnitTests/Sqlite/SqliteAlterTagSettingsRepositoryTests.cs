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
            var region = new SqliteRegionContext();
            var friendships = new SqliteFriendshipRepository(factory);
            var settings = new SqliteSettingsFieldRepository(factory, region);
            var alterFields = new AlterFieldDefinitionsAdapter(settings, NullLogger<AlterFieldDefinitionsAdapter>.Instance);
            var polls = new SqlitePollRepository(factory, region);
            var alters = new SqliteAlterRepository(
                factory, region, friendships, settings, alterFields, polls,
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
            var region = new SqliteRegionContext();
            var friendships = new SqliteFriendshipRepository(factory);
            var settings = new SqliteSettingsFieldRepository(factory, region);
            var alterFields = new AlterFieldDefinitionsAdapter(settings, NullLogger<AlterFieldDefinitionsAdapter>.Instance);
            var polls = new SqlitePollRepository(factory, region);
            var alters = new SqliteAlterRepository(
                factory, region, friendships, settings, alterFields, polls,
                NullLogger<SqliteAlterRepository>.Instance);
            var tags = new SqliteTagRepository(
                factory, region, friendships, alters, NullLogger<SqliteTagRepository>.Instance);

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
            var region = new SqliteRegionContext();
            var settings = new SqliteSettingsFieldRepository(factory, region);

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
}
