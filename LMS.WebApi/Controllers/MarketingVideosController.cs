using LMS.Application.Common.Security;
using LMS.Application.Features.MarketingCms;
using LMS.WebApi.Common;
using LMS.WebApi.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LMS.WebApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class MarketingVideosController(ISender sender) : ControllerBase
{
    [HttpGet("public")]
    [AllowAnonymous]
    [Microsoft.AspNetCore.OutputCaching.OutputCache(PolicyName = LMS.WebApi.Common.PublicReadCacheHeaderPolicy.Name)]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<MarketingVideoDto>>>> Public(CancellationToken ct)
    {
        var r = await sender.Send(new GetPublicMarketingVideosQuery(), ct);
        return Ok(ApiResponse<IReadOnlyCollection<MarketingVideoDto>>.Ok(r.Data, r.Message));
    }

    [HttpGet]
    [Authorize]
    [PermissionAuthorize(Permissions.Marketing.Manage)]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<MarketingVideoDto>>>> GetAll(
        [FromQuery] bool onlyActive = false, CancellationToken ct = default)
    {
        var r = await sender.Send(new GetMarketingVideosQuery(onlyActive), ct);
        return Ok(ApiResponse<IReadOnlyCollection<MarketingVideoDto>>.Ok(r.Data, r.Message));
    }

    [HttpPost]
    [Authorize]
    [PermissionAuthorize(Permissions.Marketing.Manage)]
    public async Task<ActionResult<ApiResponse<MarketingVideoDto>>> Create(
        [FromBody] CreateMarketingVideoCommand cmd, CancellationToken ct)
    {
        var r = await sender.Send(cmd, ct);
        return r.ToApiResult();
    }

    [HttpPut("{id:guid}")]
    [Authorize]
    [PermissionAuthorize(Permissions.Marketing.Manage)]
    public async Task<ActionResult<ApiResponse<MarketingVideoDto>>> Update(
        Guid id, [FromBody] UpdateMarketingVideoCommand cmd, CancellationToken ct)
    {
        var r = await sender.Send(cmd with { VideoId = id }, ct);
        return r.ToApiResult();
    }

    [HttpDelete("{id:guid}")]
    [Authorize]
    [PermissionAuthorize(Permissions.Marketing.Manage)]
    public async Task<ActionResult<ApiResponse<object>>> Delete(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new DeleteMarketingVideoCommand(id), ct);
        return r.Success
            ? Ok(ApiResponse<object>.Ok(new { }, r.Message))
            : BadRequest(ApiResponse<object>.Fail(r.Message ?? "Failed"));
    }
}
