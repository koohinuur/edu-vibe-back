using LMS.Application.Common.Security;
using LMS.Application.Features.MarketingCms;
using LMS.WebApi.Common;
using LMS.WebApi.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LMS.WebApi.Controllers;

/// <summary>
/// Marketing-site mock-test schedule. Admin manages via the signed-in
/// endpoints (Marketing.Manage); the marketing site reads active + upcoming
/// slots from /public anonymously.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class MockTestSlotsController(ISender sender) : ControllerBase
{
    [HttpGet("public")]
    [AllowAnonymous]
    [Microsoft.AspNetCore.OutputCaching.OutputCache(PolicyName = LMS.WebApi.Common.PublicReadCacheHeaderPolicy.Name)]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<MockTestSlotDto>>>> Public(CancellationToken ct)
    {
        var r = await sender.Send(new GetPublicMockTestSlotsQuery(), ct);
        return Ok(ApiResponse<IReadOnlyCollection<MockTestSlotDto>>.Ok(r.Data, r.Message));
    }

    [HttpGet]
    [Authorize]
    [PermissionAuthorize(Permissions.Marketing.Manage)]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<MockTestSlotDto>>>> GetAll(
        [FromQuery] bool onlyActive = false, CancellationToken ct = default)
    {
        var r = await sender.Send(new GetMockTestSlotsQuery(onlyActive), ct);
        return Ok(ApiResponse<IReadOnlyCollection<MockTestSlotDto>>.Ok(r.Data, r.Message));
    }

    [HttpPost]
    [Authorize]
    [PermissionAuthorize(Permissions.Marketing.Manage)]
    public async Task<ActionResult<ApiResponse<MockTestSlotDto>>> Create(
        [FromBody] CreateMockTestSlotCommand cmd, CancellationToken ct)
    {
        var r = await sender.Send(cmd, ct);
        return r.ToApiResult();
    }

    [HttpPut("{id:guid}")]
    [Authorize]
    [PermissionAuthorize(Permissions.Marketing.Manage)]
    public async Task<ActionResult<ApiResponse<MockTestSlotDto>>> Update(
        Guid id, [FromBody] UpdateMockTestSlotCommand cmd, CancellationToken ct)
    {
        var r = await sender.Send(cmd with { SlotId = id }, ct);
        return r.ToApiResult();
    }

    [HttpDelete("{id:guid}")]
    [Authorize]
    [PermissionAuthorize(Permissions.Marketing.Manage)]
    public async Task<ActionResult<ApiResponse<object>>> Delete(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new DeleteMockTestSlotCommand(id), ct);
        return r.Success
            ? Ok(ApiResponse<object>.Ok(new { }, r.Message))
            : BadRequest(ApiResponse<object>.Fail(r.Message ?? "Failed"));
    }
}
