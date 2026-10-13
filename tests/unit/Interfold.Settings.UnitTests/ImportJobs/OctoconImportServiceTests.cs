using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Interfold.Alters.Contracts.Models.Commands;
using Interfold.Alters.Domain.Abstractions.Repository;
using Interfold.Fronting.Domain.Abstractions.Repository;
using Interfold.Polls.Contracts.Ids;
using Interfold.Polls.Contracts.Models;
using Interfold.Polls.Contracts.Models.Commands;
using Interfold.Polls.Domain.Abstractions.Repository;
using Interfold.Settings.Api.Services.Octocon;
using Interfold.Settings.Contracts.Models.ImportOperations;
using Interfold.Settings.Domain.Abstractions.Repository;
using Interfold.Shared.Api.Services;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Domain.Abstractions.Repository;
using Interfold.Tags.Contracts.Ids;
using Interfold.Tags.Contracts.Models.Commands;
using Interfold.Tags.Domain.Abstractions.Repository;
using Microsoft.Extensions.Logging.Abstractions;

namespace Interfold.Api.UnitTests.ImportJobs;

public sealed class OctoconImportServiceTests
{
    private static readonly SystemId TestSystemId = new("nam:test-system");

    private sealed class TestHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    [Test]
    public async Task ImportAsync_ValidCompletePayload_SuccessfullyImportsAllEntitiesAndRemapsIds()
    {
        var fieldsMock = ISettingsFieldRepository.Mock(MockBehavior.Loose);
        var altersMock = IAlterRepository.Mock(MockBehavior.Loose);
        var tagsMock = ITagRepository.Mock(MockBehavior.Loose);
        var frontsMock = IFrontingRepository.Mock(MockBehavior.Loose);
        var pollsMock = IPollRepository.Mock(MockBehavior.Loose);
        var accountsMock = IAccountRepository.Mock(MockBehavior.Loose);
        var avatarStorageMock = IAvatarStorage.Mock(MockBehavior.Loose);

        var allocatedFieldId = new FieldId(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var allocatedAlter1Id = new AlterId(101);
        var allocatedAlter2Id = new AlterId(102);
        var allocatedTag1Id = new TagId(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        var allocatedTag2Id = new TagId(Guid.Parse("33333333-3333-3333-3333-333333333333"));
        var allocatedPollId = new PollId(Guid.Parse("44444444-4444-4444-4444-444444444444"));

        fieldsMock.CreateAsync(Any<SystemId>(), Any<string>(), Any<FieldType>(), Any<VisibilityLevel>(), Any<bool>(), Any<DateTime>(), Any<CancellationToken>())
            .Returns(allocatedFieldId);

        var alterCallCount = 0;
        altersMock.CreateAsync(Any<SystemId>(), Any<CreateAlterCommand>(), Any<CancellationToken>())
            .Returns((_, _, _) => alterCallCount++ == 0 ? allocatedAlter1Id : allocatedAlter2Id);

        var tagCallCount = 0;
        tagsMock.CreateAsync(Any<SystemId>(), Any<CreateTagCommand>(), Any<CancellationToken>())
            .Returns((_, _, _) => tagCallCount++ == 0 ? allocatedTag1Id : allocatedTag2Id);

        pollsMock.CreateAsync(Any<SystemId>(), Any<CreatePollCommand>(), Any<CancellationToken>())
            .Returns(allocatedPollId);

        avatarStorageMock.SaveSystemAvatarAsync(Any<SystemId>(), Any<Stream>(), Any<CancellationToken>())
            .Returns(new AvatarUrl("http://localhost/avatars/sys.png"));
        avatarStorageMock.SaveAlterAvatarAsync(Any<SystemId>(), Any<AlterId>(), Any<Stream>(), Any<CancellationToken>())
            .Returns(new AvatarUrl("http://localhost/avatars/alt.png"));

        var httpHandler = new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0])
        });
        var httpFactory = new TestHttpClientFactory(httpHandler);

        var service = new OctoconImportService(
            httpFactory,
            altersMock.Object,
            tagsMock.Object,
            frontsMock.Object,
            fieldsMock.Object,
            accountsMock.Object,
            pollsMock.Object,
            avatarStorageMock.Object,
            NullLogger<OctoconImportService>.Instance,
            TimeProvider.System);

        const string jsonPayload = """
        {
          "user": {
            "username": "testuser",
            "description": "System description",
            "avatar_url": "https://cdn.example.com/system.png",
            "fields": [
              {
                "id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "name": "PronounsField",
                "type": "text",
                "locked": false,
                "security_level": "public"
              }
            ]
          },
          "alters": [
            {
              "id": 1,
              "name": "Alice",
              "pronouns": "she/her",
              "description": "Primary alter",
              "color": "#ff0000",
              "avatar_url": "https://cdn.example.com/alice.png",
              "proxy_name": "AliceP",
              "discord_proxies": ["[a]"],
              "fields": [
                {
                  "id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                  "value": "she/they"
                }
              ]
            },
            {
              "id": 2,
              "name": "Bob",
              "pronouns": "he/him",
              "color": "0000ff",
              "discord_proxies": []
            }
          ],
          "tags": [
            {
              "id": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
              "name": "RootTag",
              "color": "#112233",
              "security_level": "public",
              "parent_tag_id": null,
              "alters": [1, 2]
            },
            {
              "id": "cccccccc-cccc-cccc-cccc-cccccccccccc",
              "name": "ChildTag",
              "color": null,
              "security_level": "public",
              "parent_tag_id": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
              "alters": [1]
            }
          ],
          "fronts": [
            {
              "id": "dddddddd-dddd-dddd-dddd-dddddddddddd",
              "alter_id": 1,
              "comment": "Fronting Alice",
              "time_start": "2024-05-01T10:00:00",
              "time_end": null
            },
            {
              "id": "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee",
              "alter_id": 2,
              "comment": "Fronting Bob",
              "time_start": "2024-04-01T10:00:00",
              "time_end": "2024-04-01T12:00:00"
            }
          ],
          "polls": [
            {
              "id": "ffffffff-ffff-ffff-ffff-ffffffffffff",
              "title": "Favorite Color",
              "type": "choice",
              "data": {
                "choice": ["Red", "Blue"],
                "responses": [
                  { "alter_id": 1, "choice": 0 }
                ]
              }
            }
          ]
        }
        """;

        var outcome = await service.ImportAsync(TestSystemId, jsonPayload);

        if (!outcome.Success)
        {
            throw new InvalidOperationException($"Import failed with: {outcome.ErrorMessage}");
        }

        using (Assert.Multiple())
        {
            await Assert.That(outcome.Success).IsTrue();
            await Assert.That(outcome.AlterCount).IsEqualTo(2);
            await Assert.That(outcome.ErrorCode).IsNull();
        }
    }

    [Test]
    public async Task ImportAsync_AvatarDownloadFailure_DoesNotFailImport()
    {
        var fieldsMock = ISettingsFieldRepository.Mock(MockBehavior.Loose);
        var altersMock = IAlterRepository.Mock(MockBehavior.Loose);
        var tagsMock = ITagRepository.Mock(MockBehavior.Loose);
        var frontsMock = IFrontingRepository.Mock(MockBehavior.Loose);
        var pollsMock = IPollRepository.Mock(MockBehavior.Loose);
        var accountsMock = IAccountRepository.Mock(MockBehavior.Loose);
        var avatarStorageMock = IAvatarStorage.Mock(MockBehavior.Loose);

        altersMock.CreateAsync(Any<SystemId>(), Any<CreateAlterCommand>(), Any<CancellationToken>())
            .Returns(new AlterId(201));

        // HTTP handler returns 500 InternalServerError
        var httpHandler = new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var httpFactory = new TestHttpClientFactory(httpHandler);

        var service = new OctoconImportService(
            httpFactory,
            altersMock.Object,
            tagsMock.Object,
            frontsMock.Object,
            fieldsMock.Object,
            accountsMock.Object,
            pollsMock.Object,
            avatarStorageMock.Object,
            NullLogger<OctoconImportService>.Instance,
            TimeProvider.System);

        const string jsonPayload = """
        {
          "user": {
            "avatar_url": "https://cdn.example.com/broken-system.png"
          },
          "alters": [
            {
              "id": 1,
              "name": "Alice",
              "avatar_url": "https://cdn.example.com/broken-alice.png"
            }
          ]
        }
        """;

        var outcome = await service.ImportAsync(TestSystemId, jsonPayload);

        using (Assert.Multiple())
        {
            await Assert.That(outcome.Success).IsTrue()
                .Because("Avatar download errors must be logged and non-fatal.");
            await Assert.That(outcome.AlterCount).IsEqualTo(1);
        }
    }

    [Test]
    public async Task ImportAsync_InvalidJson_ReturnsFailedOutcome()
    {
        var fieldsMock = ISettingsFieldRepository.Mock(MockBehavior.Loose);
        var altersMock = IAlterRepository.Mock(MockBehavior.Loose);
        var tagsMock = ITagRepository.Mock(MockBehavior.Loose);
        var frontsMock = IFrontingRepository.Mock(MockBehavior.Loose);
        var pollsMock = IPollRepository.Mock(MockBehavior.Loose);
        var accountsMock = IAccountRepository.Mock(MockBehavior.Loose);
        var avatarStorageMock = IAvatarStorage.Mock(MockBehavior.Loose);

        var httpHandler = new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var httpFactory = new TestHttpClientFactory(httpHandler);

        var service = new OctoconImportService(
            httpFactory,
            altersMock.Object,
            tagsMock.Object,
            frontsMock.Object,
            fieldsMock.Object,
            accountsMock.Object,
            pollsMock.Object,
            avatarStorageMock.Object,
            NullLogger<OctoconImportService>.Instance,
            TimeProvider.System);

        var outcome = await service.ImportAsync(TestSystemId, "{ invalid json broken");

        using (Assert.Multiple())
        {
            await Assert.That(outcome.Success).IsFalse();
            await Assert.That(outcome.AlterCount).IsEqualTo(0);
            await Assert.That(outcome.ErrorCode).IsEqualTo(ImportErrorCode.ImportFailed);
        }
    }
}
