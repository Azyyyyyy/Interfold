using Interfold.Journals.Contracts.Ids;
using Interfold.Journals.Contracts.Models.Commands;
using Interfold.Journals.Contracts.Models.Read;
using Interfold.Journals.Domain;
using Interfold.Journals.Domain.Abstractions.Repository;
using Interfold.Shared.Api.Http;
using Interfold.Shared.Api.Models;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Operations;

namespace Interfold.Journals.Api.DependencyInjection;

public static partial class JournalsModuleServiceCollectionExtensions
{
    public static IEndpointRouteBuilder MapJournalsModule(this IEndpointRouteBuilder endpoints)
    {
        var global = endpoints.MapInterfoldGroup("/api/journals");
        global.MapGet("/", IndexGlobal);
        global.MapGet("/{id}", ShowGlobal);
        global.MapPost("/", CreateGlobal);
        global.MapPatch("/{id}", UpdateGlobal);
        global.MapDelete("/{id}", DeleteGlobal);
        global.MapPost("/{id}/lock", LockGlobal);
        global.MapPost("/{id}/unlock", UnlockGlobal);
        global.MapPost("/{id}/pin", PinGlobal);
        global.MapPost("/{id}/unpin", UnpinGlobal);
        global.MapPost("/{id}/alter", AttachAlter);
        global.MapDelete("/{id}/alter", DetachAlter);

        var alter = endpoints.MapInterfoldGroup("/api/systems/me/alters");
        alter.MapGet("/{alterId}/journals", IndexAlter);
        alter.MapGet("/journals/{journalId}", ShowAlter);
        alter.MapPost("/{alterId}/journals", CreateAlter);
        alter.MapPatch("/journals/{journalId}", UpdateAlter);
        alter.MapDelete("/journals/{journalId}", DeleteAlter);
        alter.MapPost("/journals/{journalId}/lock", LockAlter);
        alter.MapPost("/journals/{journalId}/unlock", UnlockAlter);
        alter.MapPost("/journals/{journalId}/pin", PinAlter);
        alter.MapPost("/journals/{journalId}/unpin", UnpinAlter);
        return endpoints;
    }

    private static async Task<Response<IReadOnlyList<JournalReadModel>>> IndexGlobal(
        HttpContext http, IJournalRepository journalRepository, CancellationToken ct)
    {
        var entries = await journalRepository.ListGlobalAsync(http.PrincipalId(), ct);
        return new SuccessResponse<IReadOnlyList<JournalReadModel>>(entries);
    }

    private static async Task<Response<JournalReadModel>> ShowGlobal(
        HttpContext http, EntryId id, IJournalRepository journalRepository, CancellationToken ct)
    {
        var entry = await journalRepository.GetGlobalAsync(http.PrincipalId(), id, ct);
        return InterfoldHttp.OkOrNotFound(entry, "Journal entry not found.", ErrorCodes.JournalEntryNotFound);
    }

    private static async Task<Response<JournalReadModel>> CreateGlobal(
        HttpContext http, CreateGlobalJournalRequest req, IJournalRepository journalRepository,
        CreateGlobalJournalEntryCommandHandler create, CancellationToken ct)
    {
        var principal = http.PrincipalId();
        return await http.DispatchCreatedAsync(
            create,
            OperationIds.JournalGlobalCreate,
            new CreateGlobalJournalEntryCommand(req.Title),
            async (res) => await journalRepository.GetGlobalAsync(principal, res.EntryId, ct),
            ct);
    }

    private static Task<Response> UpdateGlobal(
        HttpContext http, EntryId id, UpdateGlobalJournalRequest req, UpdateGlobalJournalEntryCommandHandler update, CancellationToken ct)
        => http.DispatchNoContentAsync(update, OperationIds.JournalGlobalUpdate, new UpdateGlobalJournalEntryCommand(id, req.Title, req.Content, req.Color), ct);

    private static Task<Response> DeleteGlobal(
        HttpContext http, EntryId id, DeleteGlobalJournalEntryCommandHandler delete, CancellationToken ct)
        => http.DispatchNoContentAsync(delete, OperationIds.JournalGlobalDelete, new DeleteGlobalJournalEntryCommand(id), ct);

    private static Task<Response> LockGlobal(
        HttpContext http, EntryId id, SetGlobalJournalLockedCommandHandler setLocked, CancellationToken ct)
        => http.DispatchNoContentAsync(setLocked, OperationIds.JournalGlobalLock, new SetGlobalJournalLockedCommand(id, true), ct);

    private static Task<Response> UnlockGlobal(
        HttpContext http, EntryId id, SetGlobalJournalLockedCommandHandler setLocked, CancellationToken ct)
        => http.DispatchNoContentAsync(setLocked, OperationIds.JournalGlobalUnlock, new SetGlobalJournalLockedCommand(id, false), ct);

    private static Task<Response> PinGlobal(
        HttpContext http, EntryId id, SetGlobalJournalPinnedCommandHandler setPinned, CancellationToken ct)
        => http.DispatchNoContentAsync(setPinned, OperationIds.JournalGlobalPin, new SetGlobalJournalPinnedCommand(id, true), ct);

    private static Task<Response> UnpinGlobal(
        HttpContext http, EntryId id, SetGlobalJournalPinnedCommandHandler setPinned, CancellationToken ct)
        => http.DispatchNoContentAsync(setPinned, OperationIds.JournalGlobalUnpin, new SetGlobalJournalPinnedCommand(id, false), ct);

    private static Task<Response> AttachAlter(
        HttpContext http, EntryId id, JournalAlterRequest req, AttachAlterToGlobalJournalCommandHandler attachAlter, CancellationToken ct)
        => InterfoldHttp.InvalidAlterId(req.AlterId) is { } invalid
            ? Task.FromResult<Response>(invalid)
            : http.DispatchNoContentAsync(attachAlter, OperationIds.JournalGlobalAttachAlter, new AttachAlterToGlobalJournalCommand(id, req.AlterId), ct);

    private static Task<Response> DetachAlter(
        HttpContext http, EntryId id, JournalAlterRequest req, DetachAlterFromGlobalJournalCommandHandler detachAlter, CancellationToken ct)
        => InterfoldHttp.InvalidAlterId(req.AlterId) is { } invalid
            ? Task.FromResult<Response>(invalid)
            : http.DispatchNoContentAsync(detachAlter, OperationIds.JournalGlobalDetachAlter, new DetachAlterFromGlobalJournalCommand(id, req.AlterId), ct);

    private static async Task<Response<IEnumerable<AlterJournalReadModel>>> IndexAlter(
        HttpContext http, AlterId alterId, IJournalRepository journalRepository, CancellationToken ct)
    {
        IEnumerable<AlterJournalReadModel> entries = await journalRepository.ListAlterAsync(http.PrincipalId(), alterId, ct);
        return new SuccessResponse<IEnumerable<AlterJournalReadModel>>(entries);
    }

    private static async Task<Response<AlterJournalReadModel>> ShowAlter(
        HttpContext http, EntryId journalId, IJournalRepository journalRepository, CancellationToken ct)
    {
        var entry = await journalRepository.GetAlterAsync(http.PrincipalId(), journalId, ct);
        return InterfoldHttp.OkOrNotFound(entry, "Journal entry not found.", ErrorCodes.JournalEntryNotFound);
    }

    private static async Task<Response<AlterJournalReadModel>> CreateAlter(
        HttpContext http, AlterId alterId, CreateAlterJournalRequest req, IJournalRepository journalRepository,
        CreateAlterJournalEntryCommandHandler create, CancellationToken ct)
    {
        var principal = http.PrincipalId();
        return await http.DispatchCreatedAsync(
            create,
            OperationIds.JournalAlterCreate,
            new CreateAlterJournalEntryCommand(alterId, req.Title, http.Time().GetUtcNow()),
            async (res) => await journalRepository.GetAlterAsync(principal, res.EntryId, ct),
            ct);
    }

    private static Task<Response> UpdateAlter(
        HttpContext http, EntryId journalId, UpdateAlterJournalRequest req, UpdateAlterJournalEntryCommandHandler update, CancellationToken ct)
        => http.DispatchNoContentAsync(update, OperationIds.JournalAlterUpdate, new UpdateAlterJournalEntryCommand(journalId, req.Title, req.Content, req.Color, http.Time().GetUtcNow()), ct);

    private static Task<Response> DeleteAlter(
        HttpContext http, EntryId journalId, DeleteAlterJournalEntryCommandHandler delete, CancellationToken ct)
        => http.DispatchNoContentAsync(delete, OperationIds.JournalAlterDelete, new DeleteAlterJournalEntryCommand(journalId), ct);

    private static Task<Response> LockAlter(
        HttpContext http, EntryId journalId, SetAlterJournalLockedCommandHandler setLocked, CancellationToken ct)
        => http.DispatchNoContentAsync(setLocked, OperationIds.JournalAlterLock, new SetAlterJournalLockedCommand(journalId, true), ct);

    private static Task<Response> UnlockAlter(
        HttpContext http, EntryId journalId, SetAlterJournalLockedCommandHandler setLocked, CancellationToken ct)
        => http.DispatchNoContentAsync(setLocked, OperationIds.JournalAlterUnlock, new SetAlterJournalLockedCommand(journalId, false), ct);

    private static Task<Response> PinAlter(
        HttpContext http, EntryId journalId, SetAlterJournalPinnedCommandHandler setPinned, CancellationToken ct)
        => http.DispatchNoContentAsync(setPinned, OperationIds.JournalAlterPin, new SetAlterJournalPinnedCommand(journalId, true), ct);

    private static Task<Response> UnpinAlter(
        HttpContext http, EntryId journalId, SetAlterJournalPinnedCommandHandler setPinned, CancellationToken ct)
        => http.DispatchNoContentAsync(setPinned, OperationIds.JournalAlterUnpin, new SetAlterJournalPinnedCommand(journalId, false), ct);
}
