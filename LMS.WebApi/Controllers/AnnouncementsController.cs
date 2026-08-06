using LMS.Application.Common.Abstractions;
using LMS.Application.Common.Security;
using LMS.Application.Features.Announcements;
using LMS.Domain.Enums;
using LMS.WebApi.Common;
using LMS.WebApi.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LMS.WebApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class AnnouncementsController(ISender sender, ICurrentUserService currentUser) : ControllerBase
{
    /// <summary>
    /// Anonymous marketing-site feed. Only IsPublic + currently inside the
    /// visibility window; capped to <paramref name="take"/> (1..50).
    /// </summary>
    [HttpGet("public")]
    [AllowAnonymous]
    [Microsoft.AspNetCore.OutputCaching.OutputCache(PolicyName = LMS.WebApi.Common.PublicReadCacheHeaderPolicy.Name)]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<AnnouncementDto>>>> Public(
        [FromQuery] int take = 10, CancellationToken ct = default)
    {
        var r = await sender.Send(new GetPublicAnnouncementsQuery(take), ct);
        return Ok(ApiResponse<IReadOnlyCollection<AnnouncementDto>>.Ok(r.Data, r.Message));
    }

    /// <summary>
    /// Signed-in feed — every announcement (incl. private). Pass
    /// <c>onlyLive=true</c> for the student dashboard widget.
    /// </summary>
    [HttpGet]
    [Authorize]
    [PermissionAuthorize(Permissions.Announcements.Read)]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<AnnouncementDto>>>> GetAll(
        [FromQuery] bool onlyLive = false,
        [FromQuery] AnnouncementAudience? audience = null,
        CancellationToken ct = default)
    {
        var r = await sender.Send(new GetAnnouncementsQuery(onlyLive, audience), ct);
        return Ok(ApiResponse<IReadOnlyCollection<AnnouncementDto>>.Ok(r.Data, r.Message));
    }

    [HttpPost]
    [Authorize]
    [PermissionAuthorize(Permissions.Announcements.Manage)]
    public async Task<ActionResult<ApiResponse<AnnouncementDto>>> Create(
        [FromBody] CreateAnnouncementBody body, CancellationToken ct)
    {
        if (currentUser.UserId is not { } uid)
            return Unauthorized(ApiResponse<AnnouncementDto>.Fail("Not authenticated."));

        var r = await sender.Send(new CreateAnnouncementCommand(
            body.Title, body.Body, body.IsPublic, body.Audience,
            body.PublishesAt, body.ExpiresAt, uid), ct);
        return r.ToApiResult();
    }

    [HttpPut("{id:guid}")]
    [Authorize]
    [PermissionAuthorize(Permissions.Announcements.Manage)]
    public async Task<ActionResult<ApiResponse<AnnouncementDto>>> Update(
        Guid id, [FromBody] CreateAnnouncementBody body, CancellationToken ct)
    {
        var r = await sender.Send(new UpdateAnnouncementCommand(
            id, body.Title, body.Body, body.IsPublic, body.Audience,
            body.PublishesAt, body.ExpiresAt), ct);
        return r.ToApiResult();
    }

    [HttpDelete("{id:guid}")]
    [Authorize]
    [PermissionAuthorize(Permissions.Announcements.Manage)]
    public async Task<ActionResult<ApiResponse<object>>> Delete(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new DeleteAnnouncementCommand(id), ct);
        return r.Success
            ? Ok(ApiResponse<object>.Ok(new { }, r.Message))
            : BadRequest(ApiResponse<object>.Fail(r.Message ?? "Failed"));
    }
}

public sealed record CreateAnnouncementBody(
    string Title,
    string Body,
    bool IsPublic,
    AnnouncementAudience Audience,
    DateTime? PublishesAt,
    DateTime? ExpiresAt);
