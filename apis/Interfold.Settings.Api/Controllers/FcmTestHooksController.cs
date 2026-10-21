using Interfold.Infrastructure.Coordination;
using Interfold.Shared.Api.Controllers.Base;
using Interfold.Shared.Api.Filters;
using Microsoft.AspNetCore.Mvc;

namespace Interfold.Settings.Api.Controllers;

/// <summary>Test-only FCM outbox. Hidden unless <c>OCTOCON_TEST_HOOKS=1</c>.</summary>
[Route("api/internal/test/fcm-sends")]
[RequireTestHooks]
public sealed class FcmTestHooksController(IFcmSendRecorder recorder) : InterfoldControllerBase
{
    [HttpGet]
    public IActionResult List() => Ok(new { data = recorder.Snapshot() });
}
