using System.Globalization;
using System.Text.Json;
using Interfold.Alters.Contracts.Models.Commands;
using Interfold.Alters.Domain.Abstractions.Repository;
using Interfold.Fronting.Contracts.Models.Commands;
using Interfold.Fronting.Domain.Abstractions.Repository;
using Interfold.Polls.Contracts.Models;
using Interfold.Polls.Contracts.Models.Commands;
using Interfold.Polls.Domain.Abstractions.Repository;
using Interfold.Settings.Api.Json;
using Interfold.Settings.Api.Services.Export;
using Interfold.Settings.Contracts.Models.ImportOperations;
using Interfold.Settings.Domain.Abstractions;
using Interfold.Settings.Domain.Abstractions.ImportJobs;
using Interfold.Settings.Domain.Abstractions.Repository;
using Interfold.Shared.Api.Services;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Shared.Domain.Abstractions.Repository;
using Interfold.Tags.Contracts.Ids;
using Interfold.Tags.Contracts.Models.Commands;
using Interfold.Tags.Domain.Abstractions.Repository;

namespace Interfold.Settings.Api.Services.Octocon;

public sealed class OctoconImportService : IOctoconImportService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAlterRepository _alterRepository;
    private readonly ITagRepository _tagRepository;
    private readonly IFrontingRepository _frontingRepository;
    private readonly ISettingsFieldRepository _fieldRepository;
    private readonly IAccountRepository _accountRepository;
    private readonly IPollRepository _pollRepository;
    private readonly IAvatarStorage _avatarStorage;
    private readonly ILogger<OctoconImportService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IStorageTransactionFactory _transactions;

    public OctoconImportService(
        IHttpClientFactory httpClientFactory,
        IAlterRepository alterRepository,
        ITagRepository tagRepository,
        IFrontingRepository frontingRepository,
        ISettingsFieldRepository fieldRepository,
        IAccountRepository accountRepository,
        IPollRepository pollRepository,
        IAvatarStorage avatarStorage,
        ILogger<OctoconImportService> logger,
        TimeProvider timeProvider,
        IStorageTransactionFactory transactions)
    {
        _httpClientFactory = httpClientFactory;
        _alterRepository = alterRepository;
        _tagRepository = tagRepository;
        _frontingRepository = frontingRepository;
        _fieldRepository = fieldRepository;
        _accountRepository = accountRepository;
        _pollRepository = pollRepository;
        _avatarStorage = avatarStorage;
        _logger = logger;
        _timeProvider = timeProvider;
        _transactions = transactions;
    }

    public async Task<ImportJobOutcome> ImportAsync(
        SystemId systemId,
        string rawPayload,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting Octocon backup import for system {SystemId}", systemId);

        OctoconExportPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize(rawPayload, ExportJsonContext.Default.OctoconExportPayload);
            if (payload is null)
            {
                return new ImportJobOutcome(false, 0, ImportErrorCode.ImportFailed, "Failed to deserialize Octocon payload.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse Octocon JSON payload for system {SystemId}", systemId);
            return new ImportJobOutcome(false, 0, ImportErrorCode.ImportFailed, $"Invalid JSON payload: {ex.Message}");
        }

        var now = _timeProvider.GetUtcNow();
        int alterCount;
        List<(AlterId AlterId, string Url)> alterAvatarUrls;
        {
            await using var transaction = await _transactions.BeginAsync(cancellationToken);
            try
            {
                var fieldIdMap = await ImportFieldsAsync(systemId, payload.User?.Fields, now.UtcDateTime, cancellationToken).ConfigureAwait(false);
                (alterCount, var alterIdMap, alterAvatarUrls) = await ImportAltersAsync(systemId, payload.Alters, fieldIdMap, now, cancellationToken).ConfigureAwait(false);
                await ImportTagsAsync(systemId, payload.Tags, alterIdMap, now.UtcDateTime, cancellationToken).ConfigureAwait(false);
                await ImportFrontsAsync(systemId, payload.Fronts, alterIdMap, now, cancellationToken).ConfigureAwait(false);
                await ImportPollsAsync(systemId, payload.Polls, alterIdMap, now.UtcDateTime, cancellationToken).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(payload.User?.Description))
                {
                    var desc = payload.User.Description;
                    var truncated = desc.Length > 3000 ? desc[..3000] : desc;
                    await _accountRepository.UpdateDescriptionAsync(systemId, truncated, cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed during Octocon backup data ingestion for system {SystemId}", systemId);
                return new ImportJobOutcome(false, 0, ImportErrorCode.ImportFailed, ex.Message);
            }
        }

        await DownloadAndSaveAvatarsAsync(systemId, payload.User?.AvatarUrl, alterAvatarUrls, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Octocon backup import completed successfully for system {SystemId}: {AlterCount} alters imported", systemId, alterCount);
        return new ImportJobOutcome(true, alterCount);
    }

    private async Task<Dictionary<Guid, FieldId>> ImportFieldsAsync(
        SystemId systemId,
        IReadOnlyList<OctoconExportUserField>? fields,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var fieldIdMap = new Dictionary<Guid, FieldId>();
        if (fields is not { Count: > 0 })
            return fieldIdMap;

        var writes = new List<SettingsFieldInsert>(fields.Count);
        foreach (var field in fields)
        {
            var fieldType = FieldType.Text;
            if (field.Type is { Length: > 0 } && Enum.TryParse<FieldType>(field.Type, ignoreCase: true, out var parsedType))
                fieldType = parsedType;

            var name = string.IsNullOrWhiteSpace(field.Name) ? "Unnamed field" : field.Name;
            if (name.Length > 100) name = name[..100];
            writes.Add(new SettingsFieldInsert(name, fieldType, field.SecurityLevel, field.Locked, nowUtc));
        }

        var created = await _fieldRepository.CreateManyAsync(systemId, writes, ct).ConfigureAwait(false);
        for (var i = 0; i < fields.Count; i++)
            fieldIdMap[fields[i].Id] = created[i];

        return fieldIdMap;
    }

    private async Task<(int AlterCount, Dictionary<short, AlterId> AlterIdMap, List<(AlterId AlterId, string Url)> AlterAvatarUrls)> ImportAltersAsync(
        SystemId systemId,
        IReadOnlyList<OctoconExportAlter>? alters,
        Dictionary<Guid, FieldId> fieldIdMap,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var alterIdMap = new Dictionary<short, AlterId>();
        var alterAvatarUrls = new List<(AlterId AlterId, string Url)>();

        if (alters is not { Count: > 0 })
            return (0, alterIdMap, alterAvatarUrls);

        var writes = new List<ImportAlterWrite>(alters.Count);
        foreach (var alter in alters)
        {
            var name = string.IsNullOrWhiteSpace(alter.Name) ? "Unnamed alter" : alter.Name;
            if (name.Length > 80) name = name[..80];

            var pronouns = alter.Pronouns;
            if (pronouns?.Length > 50) pronouns = pronouns[..50];

            var desc = alter.Description;
            if (desc?.Length > 3000) desc = desc[..3000];

            List<AlterFieldCommand>? fields = null;
            if (alter.Fields is { Count: > 0 })
            {
                fields = new List<AlterFieldCommand>();
                foreach (var f in alter.Fields)
                {
                    if (fieldIdMap.TryGetValue(f.Id, out var newFieldId))
                        fields.Add(new AlterFieldCommand(newFieldId, f.Value ?? string.Empty));
                }
                if (fields.Count == 0) fields = null;
            }

            writes.Add(new ImportAlterWrite(
                name,
                pronouns,
                desc,
                HexColor.FromNullable(ParseColor(alter.Color)),
                alter.ProxyName,
                fields,
                now));
        }

        var created = await _alterRepository.ImportManyAsync(systemId, writes, ct).ConfigureAwait(false);
        for (var i = 0; i < alters.Count; i++)
        {
            alterIdMap[alters[i].Id] = created[i];
            if (alters[i].AvatarUrl is { } avatarUrl && !string.IsNullOrWhiteSpace(avatarUrl))
                alterAvatarUrls.Add((created[i], avatarUrl));
        }

        return (created.Count, alterIdMap, alterAvatarUrls);
    }

    private async Task ImportTagsAsync(
        SystemId systemId,
        IReadOnlyList<OctoconExportTag>? tags,
        Dictionary<short, AlterId> alterIdMap,
        DateTime nowUtc,
        CancellationToken ct)
    {
        if (tags is not { Count: > 0 })
            return;

        var writes = new List<ImportTagWrite>(tags.Count);
        foreach (var tag in tags)
        {
            var name = string.IsNullOrWhiteSpace(tag.Name) ? "Unnamed tag" : tag.Name;
            if (name.Length > 100) name = name[..100];

            var desc = tag.Description;
            if (desc?.Length > 1000) desc = desc[..1000];
            if (string.IsNullOrWhiteSpace(desc)) desc = null;

            var insertedAt = ParseIsoTimestamp(tag.InsertedAt, new DateTimeOffset(nowUtc, TimeSpan.Zero)).UtcDateTime;
            writes.Add(new ImportTagWrite(name, desc, HexColor.FromNullable(ParseColor(tag.Color)), tag.SecurityLevel, insertedAt));
        }

        var created = await _tagRepository.CreateManyAsync(systemId, writes, ct).ConfigureAwait(false);
        var tagIdMap = new Dictionary<Guid, TagId>(tags.Count);
        for (var i = 0; i < tags.Count; i++)
            tagIdMap[tags[i].Id] = created[i];

        var parents = new List<SetParentTagCommand>();
        var attachments = new List<AttachAlterToTagCommand>();
        foreach (var tag in tags)
        {
            if (!tagIdMap.TryGetValue(tag.Id, out var ourTagId))
                continue;

            if (tag.ParentTagId is { } parentGuid && tagIdMap.TryGetValue(parentGuid, out var parentTagId))
                parents.Add(new SetParentTagCommand(ourTagId, parentTagId));

            if (tag.Alters is not { Count: > 0 })
                continue;

            foreach (var oldAlterId in tag.Alters)
            {
                if (alterIdMap.TryGetValue(oldAlterId, out var newAlterId))
                    attachments.Add(new AttachAlterToTagCommand(ourTagId, newAlterId));
            }
        }

        await _tagRepository.SetParentsAsync(systemId, parents, ct).ConfigureAwait(false);
        await _tagRepository.AttachAltersAsync(systemId, attachments, ct).ConfigureAwait(false);
    }

    private async Task ImportFrontsAsync(
        SystemId systemId,
        IReadOnlyList<OctoconExportFront>? fronts,
        Dictionary<short, AlterId> alterIdMap,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (fronts is not { Count: > 0 })
            return;

        var writes = new List<ImportFrontWrite>(fronts.Count);
        foreach (var front in fronts)
        {
            if (!alterIdMap.TryGetValue(front.AlterId, out var newAlterId))
                continue;

            var comment = front.Comment;
            if (comment?.Length > 50) comment = comment[..50];
            DateTimeOffset? timeEnd = string.IsNullOrWhiteSpace(front.TimeEnd)
                ? null
                : ParseIsoTimestamp(front.TimeEnd, now);
            writes.Add(new ImportFrontWrite(newAlterId, comment, ParseIsoTimestamp(front.TimeStart, now), timeEnd));
        }

        await _frontingRepository.ImportManyAsync(systemId, writes, ct).ConfigureAwait(false);
    }

    private async Task ImportPollsAsync(
        SystemId systemId,
        IReadOnlyList<OctoconExportPoll>? polls,
        Dictionary<short, AlterId> alterIdMap,
        DateTime nowUtc,
        CancellationToken ct)
    {
        if (polls is not { Count: > 0 })
            return;

        var writes = new List<ImportPollWrite>(polls.Count);
        foreach (var poll in polls)
        {
            var title = string.IsNullOrWhiteSpace(poll.Title) ? "Unnamed poll" : poll.Title;
            if (title.Length > 100) title = title[..100];

            var desc = poll.Description;
            if (desc?.Length > 2000) desc = desc[..2000];

            var insertedAt = ParseIsoTimestamp(poll.InsertedAt, new DateTimeOffset(nowUtc, TimeSpan.Zero)).UtcDateTime;
            DateTime? timeEnd = !string.IsNullOrWhiteSpace(poll.TimeEnd)
                ? ParseIsoTimestamp(poll.TimeEnd, new DateTimeOffset(nowUtc, TimeSpan.Zero)).UtcDateTime
                : null;

            writes.Add(new ImportPollWrite(
                title,
                desc,
                poll.Type,
                BuildMappedPollData(poll.Type, poll.Data, alterIdMap),
                timeEnd,
                insertedAt));
        }

        await _pollRepository.CreateManyAsync(systemId, writes, ct).ConfigureAwait(false);
    }

    internal static JsonElement BuildMappedPollData(
        PollType pollType,
        JsonElement data,
        Dictionary<short, AlterId> alterIdMap)
    {
        if (data.ValueKind != JsonValueKind.Object)
            return data;

        if (pollType == PollType.Choice)
        {
            var choices = new List<PollDataChoice>();
            // Octocon spells the options array "choice". The stored shape uses "choices".
            if (!data.TryGetProperty("choice", out var choiceProperty))
                choiceProperty = default;

            if (choiceProperty.ValueKind == JsonValueKind.Array)
            {
                var choiceIndex = 0;
                foreach (var item in choiceProperty.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        var id = item.TryGetProperty("id", out var idElem) && idElem.GetString() is { } idStr
                            ? idStr
                            : Guid.NewGuid().ToString();
                        var name = item.TryGetProperty("name", out var nameElem) ? nameElem.GetString() ?? string.Empty : string.Empty;
                        choices.Add(new PollDataChoice(new PollChoiceId(id), name));
                    }
                    else if (item.ValueKind == JsonValueKind.String)
                    {
                        var name = item.GetString() ?? string.Empty;
                        choices.Add(new PollDataChoice(new PollChoiceId(choiceIndex.ToString()), name));
                    }
                    choiceIndex++;
                }
            }

            var responses = new List<PollDataResponse>();
            if (data.TryGetProperty("responses", out var respArr) && respArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var resp in respArr.EnumerateArray())
                {
                    if (resp.ValueKind == JsonValueKind.Object &&
                        resp.TryGetProperty("alter_id", out var alterIdElem) && alterIdElem.TryGetInt16(out var oldAlterId))
                    {
                        if (alterIdMap.TryGetValue(oldAlterId, out var newAlterId))
                        {
                            PollChoiceId? choiceId = null;
                            if (resp.TryGetProperty("choice_id", out var cIdElem) && cIdElem.GetString() is { } cId)
                            {
                                choiceId = new PollChoiceId(cId);
                            }
                            else if (resp.TryGetProperty("choice", out var chElem))
                            {
                                if (chElem.ValueKind == JsonValueKind.Number && chElem.TryGetInt32(out var choiceIdx) && choiceIdx >= 0 && choiceIdx < choices.Count)
                                {
                                    choiceId = choices[choiceIdx].Id;
                                }
                                else if (chElem.ValueKind == JsonValueKind.String && chElem.GetString() is { } chStr)
                                {
                                    choiceId = new PollChoiceId(chStr);
                                }
                            }

                            var comment = resp.TryGetProperty("comment", out var commElem) ? commElem.GetString() : null;
                            responses.Add(new PollDataResponse(newAlterId, Vote: null, ChoiceId: choiceId, Comment: comment));
                        }
                    }
                }
            }

            return PollDataJson.ToJsonElement(new ChoicePollData(choices, responses));
        }
        else
        {
            var allowVeto = data.TryGetProperty("allow_veto", out var vetoElem) && vetoElem.ValueKind is JsonValueKind.True;
            var responses = new List<PollDataResponse>();
            if (data.TryGetProperty("responses", out var respArr) && respArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var resp in respArr.EnumerateArray())
                {
                    if (resp.ValueKind == JsonValueKind.Object &&
                        resp.TryGetProperty("alter_id", out var alterIdElem) && alterIdElem.TryGetInt16(out var oldAlterId))
                    {
                        if (alterIdMap.TryGetValue(oldAlterId, out var newAlterId))
                        {
                            VoteValue? voteVal = null;
                            if (resp.TryGetProperty("vote", out var voteElem) && voteElem.GetString() is { } voteStr)
                            {
                                voteVal = PollDataJson.TryParseVoteValue(voteStr);
                            }
                            var comment = resp.TryGetProperty("comment", out var commElem) ? commElem.GetString() : null;
                            if (voteVal is not null)
                            {
                                responses.Add(new PollDataResponse(newAlterId, Vote: voteVal, ChoiceId: null, Comment: comment));
                            }
                        }
                    }
                }
            }

            return PollDataJson.ToJsonElement(new VotePollData(responses, allowVeto));
        }
    }

    private async Task DownloadAndSaveAvatarsAsync(
        SystemId systemId,
        string? systemAvatarUrl,
        IReadOnlyList<(AlterId AlterId, string AvatarUrl)> alterAvatars,
        CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);

        if (!string.IsNullOrWhiteSpace(systemAvatarUrl) && Uri.IsWellFormedUriString(systemAvatarUrl, UriKind.Absolute))
        {
            try
            {
                using var response = await client.GetAsync(systemAvatarUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    var localUrl = await _avatarStorage.SaveSystemAvatarAsync(systemId, stream, cancellationToken).ConfigureAwait(false);
                    await _accountRepository.UpdateAvatarAsync(systemId, localUrl, AvatarSource.Local, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to download system avatar from {Url} for system {SystemId}. Skipping.", systemAvatarUrl, systemId);
            }
        }

        foreach (var (alterId, avatarUrl) in alterAvatars)
        {
            if (string.IsNullOrWhiteSpace(avatarUrl) || !Uri.IsWellFormedUriString(avatarUrl, UriKind.Absolute))
                continue;

            try
            {
                using var response = await client.GetAsync(avatarUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    var localUrl = await _avatarStorage.SaveAlterAvatarAsync(systemId, alterId, stream, cancellationToken).ConfigureAwait(false);
                    await _alterRepository.UpdateAsync(systemId, new UpdateAlterCommand
                    {
                        AlterId = alterId,
                        AvatarUrl = localUrl,
                        AvatarSource = AvatarSource.Local,
                        UpdatedAt = _timeProvider.GetUtcNow(),
                    }, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to download alter avatar from {Url} for alter {AlterId} in system {SystemId}. Skipping.", avatarUrl, alterId, systemId);
            }
        }
    }

    private static DateTimeOffset ParseIsoTimestamp(string? raw, DateTimeOffset fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;

        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
            return dto;

        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
            return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));

        return fallback;
    }

    private static string? ParseColor(string? color) => HexColor.Normalise(color);
}
