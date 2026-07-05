namespace Interfold.Contracts.Models.Read;

/// <summary>
/// Empty optional body for command endpoints that need no fields. Idempotency keys are
/// supplied exclusively via the <c>X-Interfold-Idempotency-Key</c> request header; the
/// former payload-level <c>IdempotencyKey</c> member has been removed (no client ever
/// sent it, and unknown JSON members are ignored by model binding).
/// </summary>
public record BaseRequest;
