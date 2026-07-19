using Interfold.Api.Models;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Models.Read;
using Microsoft.AspNetCore.Mvc;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Domain.Journals;
using Interfold.Api.Controllers.Base;
using Interfold.Contracts;

namespace Interfold.Api.Controllers;

[Route("api/journals")]
public sealed class JournalsController : InterfoldControllerBase
{
    private readonly IJournalRepository _journalRepository;
    private readonly CreateGlobalJournalEntryCommandHandler _create;
    private readonly UpdateGlobalJournalEntryCommandHandler _update;
    private readonly DeleteGlobalJournalEntryCommandHandler _delete;
    private readonly SetGlobalJournalLockedCommandHandler _setLocked;
    private readonly SetGlobalJournalPinnedCommandHandler _setPinned;
    private readonly AttachAlterToGlobalJournalCommandHandler _attachAlter;
    private readonly DetachAlterFromGlobalJournalCommandHandler _detachAlter;

    public JournalsController(
        IJournalRepository journalRepository,
        CreateGlobalJournalEntryCommandHandler create,
        UpdateGlobalJournalEntryCommandHandler update,
        DeleteGlobalJournalEntryCommandHandler delete,
        SetGlobalJournalLockedCommandHandler setLocked,
        SetGlobalJournalPinnedCommandHandler setPinned,
        AttachAlterToGlobalJournalCommandHandler attachAlter,
        DetachAlterFromGlobalJournalCommandHandler detachAlter)
    {
        _journalRepository = journalRepository;
        _create = create;
        _update = update;
        _delete = delete;
        _setLocked = setLocked;
        _setPinned = setPinned;
        _attachAlter = attachAlter;
        _detachAlter = detachAlter;
    }

    [HttpGet]
    public async Task<Response<IReadOnlyList<JournalReadModel>>> Index(CancellationToken ct)
    {
        var entries = await _journalRepository.ListGlobalAsync(PrincipalId, ct);
        return new SuccessResponse<IReadOnlyList<JournalReadModel>>(entries);
    }

    [HttpGet("{id}")]
    public async Task<Response<JournalReadModel>> Show(EntryId id, CancellationToken ct)
    {
        var entry = await _journalRepository.GetGlobalAsync(PrincipalId, id, ct);
        return entry is null
            ? new ErrorResponse("Journal entry not found.", ErrorCodes.JournalEntryNotFound, System.Net.HttpStatusCode.NotFound)
            : entry;
    }

    [HttpPost]
    public async Task<Response<JournalReadModel>> Create([FromBody] CreateGlobalJournalRequest req, CancellationToken ct)
    {
        var principal = PrincipalId;
        return await DispatchCreatedAsync(_create, OperationIds.JournalGlobalCreate, new CreateGlobalJournalEntryCommand(req.Title), async (res) => await _journalRepository.GetGlobalAsync(principal, res.EntryId, ct),
            replaySelector: res => res?.Replay
        , ct);
    }

    [HttpPatch("{id}")]
    public async Task<Response> Update(EntryId id, [FromBody] UpdateGlobalJournalRequest req, CancellationToken ct)
    {
        return await DispatchNoContentAsync(_update, OperationIds.JournalGlobalUpdate, new UpdateGlobalJournalEntryCommand(id, req.Title, req.Content, req.Color)
        , ct);
    }

    [HttpDelete("{id}")]
    public async Task<Response> Delete(EntryId id, [FromBody] DeleteGlobalJournalRequest? req, CancellationToken ct)
    {
        return await DispatchNoContentAsync(_delete, OperationIds.JournalGlobalDelete, new DeleteGlobalJournalEntryCommand(id)
        , ct);
    }

    [HttpPost("{id}/lock")]
    public async Task<Response> Lock(EntryId id, [FromBody] JournalActionRequest? req, CancellationToken ct)
        => await SetLockedInternal(id, true, OperationIds.JournalGlobalLock, req, ct);

    [HttpPost("{id}/unlock")]
    public async Task<Response> Unlock(EntryId id, [FromBody] JournalActionRequest? req, CancellationToken ct)
        => await SetLockedInternal(id, false, OperationIds.JournalGlobalUnlock, req, ct);

    [HttpPost("{id}/pin")]
    public async Task<Response> Pin(EntryId id, [FromBody] JournalActionRequest? req, CancellationToken ct)
        => await SetPinnedInternal(id, true, OperationIds.JournalGlobalPin, req, ct);

    [HttpPost("{id}/unpin")]
    public async Task<Response> Unpin(EntryId id, [FromBody] JournalActionRequest? req, CancellationToken ct)
        => await SetPinnedInternal(id, false, OperationIds.JournalGlobalUnpin, req, ct);

    [HttpPost("{id}/alter")]
    public async Task<Response> AttachAlter(EntryId id, [FromBody] JournalAlterRequest req, CancellationToken ct)
    {
        return await DispatchNoContentAsync(_attachAlter, OperationIds.JournalGlobalAttachAlter, new AttachAlterToGlobalJournalCommand(id, req.AlterId)
        , ct);
    }

    [HttpDelete("{id}/alter")]
    public async Task<Response> DetachAlter(EntryId id, [FromBody] JournalAlterRequest req, CancellationToken ct)
    {
        return await DispatchNoContentAsync(_detachAlter, OperationIds.JournalGlobalDetachAlter, new DetachAlterFromGlobalJournalCommand(id, req.AlterId)
        , ct);
    }

    private async Task<Response> SetLockedInternal(EntryId id, bool locked, OperationId operationId, JournalActionRequest? req, CancellationToken ct)
    {
        return await DispatchNoContentAsync(_setLocked, operationId, new SetGlobalJournalLockedCommand(id, locked)
        , ct);
    }

    private async Task<Response> SetPinnedInternal(EntryId id, bool pinned, OperationId operationId, JournalActionRequest? req, CancellationToken ct)
    {
        return await DispatchNoContentAsync(_setPinned, operationId, new SetGlobalJournalPinnedCommand(id, pinned)
        , ct);
    }
}