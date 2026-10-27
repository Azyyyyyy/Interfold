using System.Net;
using Interfold.Shared.Api.Models;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Validation;

namespace Interfold.Shared.Api.Http;

/// <summary>Rejects route/query <see cref="AlterId"/> arguments that fail
/// <see cref="ValidAlterIdAttribute"/> with the same 400 <c>invalid_alter_id</c>
/// shape MVC's DataAnnotations factory produced.</summary>
public sealed class AlterIdValidationFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        foreach (var arg in context.Arguments)
        {
            if (arg is AlterId id && !ValidAlterIdAttribute.IsValid(id))
            {
                return (Response)new ErrorResponse("Invalid alter ID.", ErrorCodes.InvalidAlterId, HttpStatusCode.BadRequest);
            }
        }

        return await next(context);
    }
}
