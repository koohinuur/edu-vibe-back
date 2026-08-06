using LMS.Application.Common.Security;
using LMS.Application.Features.OfficeInfo;
using LMS.WebApi.Common;
using LMS.WebApi.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LMS.WebApi.Controllers;

/// <summary>
/// Office Info — the academy's public contact + branding singleton.
///
/// /api/OfficeInfo/public is anonymous so the marketing site can fetch it
/// without an auth token. The signed-in GET / admin PUT live on the same
/// controller but are gated by permissions.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class OfficeInfoController(ISender sender) : ControllerBase
{
    /// <summary>Anonymous read for the marketing site. Always returns a row (placeholder if unset).</summary>
    [HttpGet("public")]
    [AllowAnonymous]
    [Microsoft.AspNetCore.OutputCaching.OutputCache(PolicyName = LMS.WebApi.Common.PublicReadCacheHeaderPolicy.Name)]
    public async Task<ActionResult<ApiResponse<OfficeInfoDto>>> Public(CancellationToken ct)
    {
        var r = await sender.Send(new GetOfficeInfoQuery(), ct);
        return Ok(ApiResponse<OfficeInfoDto>.Ok(r.Data, r.Message));
    }

    /// <summary>Signed-in read — same payload as <see cref="Public"/>, kept under auth for parity.</summary>
    [HttpGet]
    [Authorize]
    [PermissionAuthorize(Permissions.OfficeInfo.Read)]
    public async Task<ActionResult<ApiResponse<OfficeInfoDto>>> Get(CancellationToken ct)
    {
        var r = await sender.Send(new GetOfficeInfoQuery(), ct);
        return Ok(ApiResponse<OfficeInfoDto>.Ok(r.Data, r.Message));
    }

    /// <summary>
    /// Upsert — POST and PUT both route here. The handler creates the row
    /// the first time and updates it thereafter, so the admin UI doesn't
    /// have to know whether a row exists.
    /// </summary>
    [HttpPut]
    [Authorize]
    [PermissionAuthorize(Permissions.OfficeInfo.Manage)]
    public async Task<ActionResult<ApiResponse<OfficeInfoDto>>> Upsert(
        [FromBody] UpsertOfficeInfoCommand cmd, CancellationToken ct)
    {
        var r = await sender.Send(cmd, ct);
        return r.ToApiResult();
    }
}
