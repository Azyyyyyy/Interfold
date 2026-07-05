using System.Diagnostics;
using System.Net;
using Interfold.Api.Helpers;
using Interfold.Api.Middleware;
using Interfold.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Contracts;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Api.Controllers.Base;

[ApiController]
[Authorize]
public abstract class InterfoldControllerBase : ControllerBase
{
    protected SystemId PrincipalId
    {
        get
        {
            if (HttpContext.Items.TryGetValue(InterfoldPrincipalMiddleware.PrincipalIdItemKey, out var value)
                && value is string principal)
            {
                return new SystemId(principal);
            }

            throw new InvalidOperationException(
                "PrincipalId is unavailable. Ensure InterfoldPrincipalMiddleware is configured.");
        }
    }

    protected async ValueTask CheckAlterId(AlterId alterId, SystemId? principal = null, CancellationToken? ct = null)
    {
        if (alterId.Value <= 0)
        {
            throw new InterfoldException("Invalid alter ID.", ErrorCodes.InvalidAlterId);
        }

        if (string.IsNullOrWhiteSpace(principal?.Value))
        {
            return;
        }

        if (!ct.HasValue)
        {
            throw new InterfoldException("CT is required on alter check", ErrorCodes.AlterCheckServerIssue);
        }

        var alterRepository = HttpContext.RequestServices.GetRequiredService<IAlterRepository>();
        var alterExists = await alterRepository.ExistsAsync(principal.Value, alterId, ct.Value);
        if (!alterExists)
        {
            throw new InterfoldException("Alter not found", ErrorCodes.AlterNotFound);
        }
    }

    /// <summary>
    /// Resolves the idempotency key for the current request: the
    /// <c>X-Interfold-Idempotency-Key</c> header when present, otherwise a fresh GUID
    /// (each unkeyed request is its own operation). The header is the only client-supplied
    /// source — payload-level keys were removed.
    /// </summary>
    protected IdempotencyKey GetIdempotencyKey()
    {
        var header = Request.Headers[InterfoldHeaders.IdempotencyKey].FirstOrDefault();
        return new IdempotencyKey(!string.IsNullOrWhiteSpace(header) ? header : Guid.NewGuid().ToString("N"));
    }

    /// <summary>
    /// Returns <paramref name="url"/> with the server origin prepended when the stored
    /// value is a relative path. Already-absolute URLs are returned unchanged.
    /// </summary>
    /// <remarks>
    /// Avatar callers should prefer <see cref="QualifyAvatar"/>, which uses the persisted
    /// <see cref="AvatarSource"/> as the authoritative discriminator instead of inferring
    /// hosting from the URL prefix.
    /// </remarks>
    protected string? QualifyUrl(string? url)
        => AvatarUrlQualifier.Qualify(url, Request.Scheme, Request.Host);

    /// <summary>
    /// Source-aware avatar qualification: prepends the server origin only when the avatar
    /// is locally hosted (<see cref="AvatarSource.Local"/>). External URLs are passed
    /// through verbatim, and a null source (no avatar set) is returned unchanged.
    /// </summary>
    protected string? QualifyAvatar(string? url, AvatarSource? source)
        => AvatarUrlQualifier.QualifyAvatar(url, source, Request.Scheme, Request.Host);

    /// <summary>
    /// Executes a command handler with:
    /// <list type="bullet">
    ///   <item>Latency measurement recorded in <see cref="InterfoldMetrics.CommandLatencyMs"/>.</item>
    ///   <item>Outcome counted in <see cref="InterfoldMetrics.CommandsTotal"/> (accepted / replay / rejected).</item>
    ///   <item>Conflict counted in <see cref="InterfoldMetrics.ConflictsTotal"/> when applicable.</item>
    ///   <item><c>X-Interfold-Command-Id</c> response header set from <paramref name="envelope"/>.</item>
    /// </list>
    /// </summary>
    protected async Task<IActionResult> ExecuteCommandAsync<TPayload, TResult>(
        ICommandHandler<TPayload, TResult> handler,
        CommandEnvelope<TPayload> envelope,
        CancellationToken ct)
        where TResult : ICommandResult
    {
        var sw = Stopwatch.StartNew();
        var result = await handler.HandleAsync(envelope, ct);
        sw.Stop();

        var opTag = new KeyValuePair<string, object?>("operation_id", envelope.OperationId);

        InterfoldMetrics.CommandLatencyMs.Record(
            sw.Elapsed.TotalMilliseconds,
            opTag);

        if (result.Accepted)
        {
            var outcome = result.Result!.Replay ? "replay" : "accepted";
            InterfoldMetrics.CommandsTotal.Add(1, opTag,
                new KeyValuePair<string, object?>("outcome", outcome));
        }
        else
        {
            InterfoldMetrics.CommandsTotal.Add(1, opTag,
                new KeyValuePair<string, object?>("outcome", "rejected"));

            InterfoldMetrics.ConflictsTotal.Add(1, opTag,
                new KeyValuePair<string, object?>("conflict_code",
                    result.Conflict!.Code.ToString()));
        }

        Response.Headers[InterfoldHeaders.CommandId] = envelope.CommandId.ToString("N");

        if (result.Accepted)
            return Ok(result.Result);

        return result.Conflict!.Code switch
        {
            ConflictCode.ConflictDuplicate    => Conflict(result.Conflict),
            ConflictCode.ConflictInvariant    => UnprocessableEntity(result.Conflict),
            _                                 => StatusCode(500, new ErrorResponse(
                "An unknown error occurred.",
                ErrorCodes.UnknownError,
                System.Net.HttpStatusCode.InternalServerError))
        };
    }

    /// <summary>
    /// Maps a <see cref="CommandExecutionResult{T}"/> to a 204 No Content <see cref="Response{NoContent}"/>
    /// on success, or an <see cref="ErrorResponse"/> on failure.
    /// </summary>
    protected Response CommandNoContent<T>(CommandExecutionResult<T> result)
    {
        if (result.Accepted)
            return new Response();

        return ConflictToError(result.Conflict!);
    }

    /// <summary>
    /// Maps a <see cref="CommandExecutionResult{T}"/> to a 201 Created <see cref="Response{TData}"/>
    /// carrying the mapped <paramref name="dataSelector"/> result, or an <see cref="ErrorResponse"/> on failure.
    /// </summary>
    protected Response<TData> CommandCreated<T, TData>(CommandExecutionResult<T> result, Func<T, TData> dataSelector, Func<T?, bool?>? replaySelector = null)
    {
        if (result.Accepted)
            return new SuccessResponse<TData>(dataSelector(result.Result!), HttpStatusCode.Created, replaySelector?.Invoke(result.Result));

        return ConflictToError(result.Conflict!);
    }

    /// <summary>
    /// Maps a <see cref="CommandExecutionResult{T}"/> to a 202 Accepted <see cref="Response{TData}"/>
    /// carrying the mapped <paramref name="dataSelector"/> result. Used by endpoints whose
    /// command handler dispatches work asynchronously (the lifecycle then continues out-of-band,
    /// typically via WebSocket frames) — the controller is done as soon as the dispatch lands.
    /// </summary>
    protected Response<TData> CommandAccepted<T, TData>(CommandExecutionResult<T> result, Func<T, TData> dataSelector)
    {
        if (result.Accepted)
            return new SuccessResponse<TData>(dataSelector(result.Result!), HttpStatusCode.Accepted);

        return ConflictToError(result.Conflict!);
    }

    protected ErrorResponse ConflictToError(Contracts.Operations.ConflictResult conflict)
    {
        Response.Headers[InterfoldHeaders.OperationId] = conflict.OperationId.Value;
        
        return conflict.Code switch
        {
            // ResolutionHint doubles as the client-visible error code; ToWireValue keeps the
            // exact legacy strings ("no_retry" / "manual_merge_required") on the wire.
            ConflictCode.ConflictDuplicate => new ErrorResponse(
                "A duplicate conflict occurred.", conflict.ResolutionHint.ToWireValue(), HttpStatusCode.Conflict, conflict.EntityRef.Value),
            ConflictCode.ConflictInvariant => new ErrorResponse(
                "The request could not be processed due to a conflict.", conflict.ResolutionHint.ToWireValue(),
                HttpStatusCode.UnprocessableEntity, conflict.EntityRef.Value),
            _ => new ErrorResponse("An unknown error occurred.", ErrorCodes.UnknownError, HttpStatusCode.InternalServerError, conflict.EntityRef.Value)
        };
    }
}