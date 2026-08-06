using LMS.Application.Common.Security;
using LMS.Application.Features.MarketingCms;
using LMS.WebApi.Common;
using LMS.WebApi.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LMS.WebApi.Controllers;

/// <summary>
/// Marketing-site course catalog. Admin manages via the signed-in endpoints
/// (Marketing.Manage); the marketing site reads from /public anonymously.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class MarketingCoursesController(ISender sender) : ControllerBase
{
    [HttpGet("public")]
    [AllowAnonymous]
    [Microsoft.AspNetCore.OutputCaching.OutputCache(PolicyName = LMS.WebApi.Common.PublicReadCacheHeaderPolicy.Name)]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<MarketingCourseDto>>>> Public(CancellationToken ct)
    {
        var r = await sender.Send(new GetPublicMarketingCoursesQuery(), ct);
        return Ok(ApiResponse<IReadOnlyCollection<MarketingCourseDto>>.Ok(r.Data, r.Message));
    }

    [HttpGet]
    [Authorize]
    [PermissionAuthorize(Permissions.Marketing.Manage)]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<MarketingCourseDto>>>> GetAll(
        [FromQuery] bool onlyActive = false, CancellationToken ct = default)
    {
        var r = await sender.Send(new GetMarketingCoursesQuery(onlyActive), ct);
        return Ok(ApiResponse<IReadOnlyCollection<MarketingCourseDto>>.Ok(r.Data, r.Message));
    }

    [HttpPost]
    [Authorize]
    [PermissionAuthorize(Permissions.Marketing.Manage)]
    public async Task<ActionResult<ApiResponse<MarketingCourseDto>>> Create(
        [FromBody] CreateMarketingCourseCommand cmd, CancellationToken ct)
    {
        var r = await sender.Send(cmd, ct);
        return r.ToApiResult();
    }

    [HttpPut("{id:guid}")]
    [Authorize]
    [PermissionAuthorize(Permissions.Marketing.Manage)]
    public async Task<ActionResult<ApiResponse<MarketingCourseDto>>> Update(
        Guid id, [FromBody] UpdateMarketingCourseCommand cmd, CancellationToken ct)
    {
        var r = await sender.Send(cmd with { CourseId = id }, ct);
        return r.ToApiResult();
    }

    [HttpDelete("{id:guid}")]
    [Authorize]
    [PermissionAuthorize(Permissions.Marketing.Manage)]
    public async Task<ActionResult<ApiResponse<object>>> Delete(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new DeleteMarketingCourseCommand(id), ct);
        return r.Success
            ? Ok(ApiResponse<object>.Ok(new { }, r.Message))
            : BadRequest(ApiResponse<object>.Fail(r.Message ?? "Failed"));
    }
}
