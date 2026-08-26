using LMS.Application.Common.Security;
using LMS.Application.Features.CmsAccess;
using LMS.WebApi.Common;
using LMS.WebApi.Security;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace LMS.WebApi.Controllers;

/// <summary>
/// "Attach staff to the CMS" — the simple replacement for the RBAC page. An
/// admin (Staff.Update) picks staff from a list to grant/revoke CMS access; the
/// grants land as direct per-user permissions. No SuperAdmin required.
/// </summary>
[ApiController]
[Route("api/admin/cms-access")]
public sealed class CmsAccessController(ISender sender) : ControllerBase
{
    [HttpGet("staff")]
    [PermissionAuthorize(Permissions.Staff.Update)]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<CmsStaffDto>>>> GetStaff(CancellationToken ct)
    {
        var r = await sender.Send(new GetCmsStaffQuery(), ct);
        return Ok(ApiResponse<IReadOnlyCollection<CmsStaffDto>>.Ok(r.Data, r.Message));
    }

    [HttpPut("staff")]
    [PermissionAuthorize(Permissions.Staff.Update)]
    public async Task<ActionResult<ApiResponse<object>>> SetStaff([FromBody] IReadOnlyCollection<Guid> userIds, CancellationToken ct)
    {
        var r = await sender.Send(new SetCmsAccessCommand(userIds), ct);
        return r.Success ? Ok(ApiResponse<object>.Ok(new { }, r.Message)) : BadRequest(ApiResponse<object>.Fail(r.Message ?? "Failed"));
    }
}
