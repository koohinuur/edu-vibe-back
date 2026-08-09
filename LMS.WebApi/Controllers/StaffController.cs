using LMS.Application.Common.Models;
using LMS.Application.Common.Security;
using LMS.Application.Features.Staff;
using LMS.Domain.Enums;
using LMS.WebApi.Common;
using LMS.WebApi.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LMS.WebApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class StaffController(ISender sender) : ControllerBase
{
    [HttpGet]
    [PermissionAuthorize(Permissions.Staff.Read)]
    public async Task<ActionResult<ApiResponse<PagedResult<StaffDto>>>> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        [FromQuery] string? search = null,
        CancellationToken ct = default)
    {
        var r = await sender.Send(new GetStaffQuery(page, pageSize, search), ct);
        return Ok(ApiResponse<PagedResult<StaffDto>>.Ok(r.Data, r.Message));
    }

    /// <summary>Returns the staff profile linked to the authenticated user. No extra permission required.</summary>
    [HttpGet("me")]
    public async Task<ActionResult<ApiResponse<StaffDto>>> GetMine(CancellationToken ct)
    {
        var r = await sender.Send(new GetMyStaffProfileQuery(), ct);
        return r.ToApiResultOrNotFound();
    }

    /// <summary>
    /// Self-edit: a teacher updates their own profile (name, phone, bio).
    /// The target profile is resolved from the JWT — body carries no
    /// profile id, so there's no IDOR surface. Inherits the controller's
    /// [Authorize] gate; the admin-only Position field stays untouched.
    /// </summary>
    [HttpPut("me/details")]
    public async Task<ActionResult<ApiResponse<StaffDto>>> UpdateMine(
        [FromBody] UpdateMyStaffDetailsCommand cmd, CancellationToken ct)
    {
        var r = await sender.Send(cmd, ct);
        return r.ToApiResult();
    }

    [HttpPost]
    [PermissionAuthorize(Permissions.Staff.Create)]
    public async Task<ActionResult<ApiResponse<StaffDto>>> Create([FromBody] CreateStaffCommand cmd,
        CancellationToken ct)
    {
        var r = await sender.Send(cmd, ct);
        return r.ToApiResult();
    }

    [HttpPut("{id:guid}")]
    [PermissionAuthorize(Permissions.Staff.Update)]
    public async Task<ActionResult<ApiResponse<StaffDto>>> Update(Guid id, [FromBody] UpdateStaffProfileCommand cmd,
        CancellationToken ct)
    {
        var r = await sender.Send(cmd with { StaffProfileId = id }, ct);
        return r.ToApiResult();
    }

    /// <summary>
    /// Updates the editable staff profile fields: first/last name, phone, description.
    /// Employment type is updated separately via the main PUT endpoint.
    /// </summary>
    [HttpPut("{id:guid}/details")]
    [PermissionAuthorize(Permissions.Staff.Update)]
    public async Task<ActionResult<ApiResponse<StaffDto>>> UpdateDetails(Guid id,
        [FromBody] UpdateStaffDetailsCommand cmd, CancellationToken ct)
    {
        var r = await sender.Send(cmd with { StaffProfileId = id }, ct);
        return r.ToApiResult();
    }

    /// <summary>
    /// Admin freeze/block/restore. Body shape: { "status": 1|2|3 } where
    /// 1=Active, 2=Inactive, 3=Blocked (UserStatus enum). The login flow
    /// rejects non-Active accounts so the lock takes effect on the next
    /// auth attempt — current sessions are also invalidated because
    /// <see cref="LMS.Domain.Entities.User.SetStatus"/> wipes the refresh
    /// token when status leaves Active.
    /// </summary>
    [HttpPost("{id:guid}/status")]
    [PermissionAuthorize(Permissions.Staff.Update)]
    public async Task<ActionResult<ApiResponse<StaffDto>>> SetStatus(Guid id,
        [FromBody] SetUserStatusRequest body, CancellationToken ct)
    {
        var r = await sender.Send(new SetStaffStatusCommand(id, body.Status), ct);
        return r.ToApiResult();
    }

    /// <summary>
    /// Admin toggles whether this staff member appears on the marketing
    /// site's teachers grid.
    /// </summary>
    [HttpPost("{id:guid}/public-visibility")]
    [PermissionAuthorize(Permissions.Staff.Update)]
    public async Task<ActionResult<ApiResponse<StaffDto>>> SetPublicVisibility(Guid id,
        [FromBody] SetPublicVisibilityRequest body, CancellationToken ct)
    {
        var r = await sender.Send(new SetStaffPublicVisibilityCommand(id, body.IsPubliclyVisible), ct);
        return r.ToApiResult();
    }

    /// <summary>
    /// Sets the marketing-grid order for the public teachers: each staff id's
    /// DisplayOrder becomes its index in the posted list. Admin drives this
    /// from the teachers reorder UI. Returns the number of rows updated.
    /// </summary>
    [HttpPut("public-order")]
    [PermissionAuthorize(Permissions.Staff.Update)]
    public async Task<ActionResult<ApiResponse<int>>> ReorderPublic(
        [FromBody] ReorderTeachersRequest body, CancellationToken ct)
    {
        var r = await sender.Send(new ReorderPublicTeachersCommand(body.OrderedIds ?? new List<Guid>()), ct);
        return Ok(ApiResponse<int>.Ok(r.Data, r.Message));
    }

    /// <summary>
    /// Anonymous public teacher feed for the marketing site. Only staff
    /// members the admin has flipped IsPubliclyVisible on appear here.
    /// Returns lean shape — no email/phone leakage.
    /// </summary>
    [HttpGet("public")]
    [AllowAnonymous]
    [Microsoft.AspNetCore.OutputCaching.OutputCache(PolicyName = LMS.WebApi.Common.PublicReadCacheHeaderPolicy.Name)]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<PublicTeacherDto>>>> Public(
        [FromQuery] int take = 30, CancellationToken ct = default)
    {
        var r = await sender.Send(new GetPublicTeachersQuery(take), ct);
        return Ok(ApiResponse<IReadOnlyCollection<PublicTeacherDto>>.Ok(r.Data, r.Message));
    }

    /// <summary>
    /// Uploads a new avatar for the staff member and stores its name on the
    /// profile. Returns the updated DTO so the frontend can swap the image
    /// in immediately.
    /// </summary>
    [HttpPost("{id:guid}/avatar")]
    [PermissionAuthorize(Permissions.Staff.Update)]
    [RequestSizeLimit(2 * 1024 * 1024)]
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<ApiResponse<StaffDto>>> UploadAvatar(Guid id,
        IFormFile file,
        [FromServices] LMS.Application.Common.Abstractions.IAvatarFileStore store,
        CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(ApiResponse<StaffDto>.Fail("File is required."));

        string storedName;
        try
        {
            await using var stream = file.OpenReadStream();
            storedName = await store.SaveAsync(stream, file.FileName, file.ContentType, ct);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<StaffDto>.Fail(ex.Message));
        }

        var r = await sender.Send(new SetStaffAvatarCommand(id, $"/uploads/avatars/{storedName}"), ct);
        if (!r.Success)
        {
            await store.DeleteAsync(storedName, ct);
            return BadRequest(ApiResponse<StaffDto>.Fail(r.Message ?? "Failed"));
        }
        return Ok(ApiResponse<StaffDto>.Ok(r.Data, r.Message));
    }

    /// <summary>
    /// Self-service avatar upload — a teacher changes their own photo from
    /// Settings. The profile is resolved from the JWT (no id in the route),
    /// so plain [Authorize] is enough; the admin-gated {id}/avatar endpoint
    /// above stays for admins changing someone else's photo.
    /// </summary>
    [HttpPost("me/avatar")]
    [RequestSizeLimit(2 * 1024 * 1024)]
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<ApiResponse<StaffDto>>> UploadMyAvatar(
        IFormFile file,
        [FromServices] LMS.Application.Common.Abstractions.IAvatarFileStore store,
        CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(ApiResponse<StaffDto>.Fail("File is required."));

        var mine = await sender.Send(new GetMyStaffProfileQuery(), ct);
        if (!mine.Success || mine.Data is null)
            return NotFound(ApiResponse<StaffDto>.Fail(mine.Message ?? "No staff profile for this user."));

        string storedName;
        try
        {
            await using var stream = file.OpenReadStream();
            storedName = await store.SaveAsync(stream, file.FileName, file.ContentType, ct);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<StaffDto>.Fail(ex.Message));
        }

        var r = await sender.Send(new SetStaffAvatarCommand(mine.Data.Id, $"/uploads/avatars/{storedName}"), ct);
        if (!r.Success)
        {
            await store.DeleteAsync(storedName, ct);
            return BadRequest(ApiResponse<StaffDto>.Fail(r.Message ?? "Failed"));
        }
        return Ok(ApiResponse<StaffDto>.Ok(r.Data, r.Message));
    }

    /// <summary>
    /// Removes the caller's own avatar (self-service). The stored file is deleted
    /// from disk by the command handler once the profile is cleared.
    /// </summary>
    [HttpDelete("me/avatar")]
    public async Task<ActionResult<ApiResponse<StaffDto>>> DeleteMyAvatar(CancellationToken ct)
    {
        var mine = await sender.Send(new GetMyStaffProfileQuery(), ct);
        if (!mine.Success || mine.Data is null)
            return NotFound(ApiResponse<StaffDto>.Fail(mine.Message ?? "No staff profile for this user."));

        var r = await sender.Send(new SetStaffAvatarCommand(mine.Data.Id, null), ct);
        return r.ToApiResult();
    }

    /// <summary>Admin removes another staff member's avatar.</summary>
    [HttpDelete("{id:guid}/avatar")]
    [PermissionAuthorize(Permissions.Staff.Update)]
    public async Task<ActionResult<ApiResponse<StaffDto>>> DeleteAvatar(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new SetStaffAvatarCommand(id, null), ct);
        return r.ToApiResult();
    }
}

/// <summary>
/// Shared body for the freeze/block endpoint on Staff + Students. Lives next
/// to the controllers because both shapes are identical and the admin UI
/// posts the same JSON to either.
/// </summary>
public sealed record SetUserStatusRequest(UserStatus Status);

public sealed record SetPublicVisibilityRequest(bool IsPubliclyVisible);

/// <summary>Body for the teachers reorder endpoint: staff-profile ids in the desired display order.</summary>
public sealed record ReorderTeachersRequest(IReadOnlyList<Guid> OrderedIds);
