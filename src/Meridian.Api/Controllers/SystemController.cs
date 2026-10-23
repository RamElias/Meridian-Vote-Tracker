using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Meridian.Api.Controllers;

[ApiController]
public sealed class SystemController : ApiControllerBase
{
    [AllowAnonymous]
    [HttpGet("/health", Name = "Health")]
    public IActionResult Health()
    { return Ok(new { status = "ok" }); }

    [AllowAnonymous]
    [HttpGet("/api/v1/meta", Name = "GetMetadata")]
    public IActionResult GetMetadata()
    { return Ok(new { name = "Meridian Vote Tracker", version = "v1" }); }

    [AllowAnonymous]
    [HttpGet("/openapi/v1.json", Name = "OpenApi")]
    public async Task<IActionResult> OpenApi(CancellationToken ct)
    { return Content(await System.IO.File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "openapi.json"), ct), "application/json"); }
}
