using LMS.Application.Common.Models;
using LMS.Application.Common.Security;
using LMS.Application.Features.Specializations;
using LMS.WebApi.Common;
using LMS.WebApi.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LMS.WebApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class SpecializationsController(ISender sender) : ControllerBase
{
    /// <summary>Lists every active specialization. Pass includeInactive=true to see disabled ones too.</summary>
    [HttpGet]
    [PermissionAuthorize(Permissions.Specializations.Read)]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<SpecializationDto>>>> GetAll(
        [FromQuery] bool includeInactive = false, CancellationToken ct = default)
    {
        var r = await sender.Send(new GetSpecializationsQuery(includeInactive), ct);
        return Ok(ApiResponse<IReadOnlyCollection<SpecializationDto>>.Ok(r.Data, r.Message));
    }

    /// <summary>
    /// Anonymous read for the marketing site — only active specializations.
    /// The marketing site lists these as "what we teach" chips.
    /// </summary>
    [HttpGet("public")]
    [AllowAnonymous]
    [Microsoft.AspNetCore.OutputCaching.OutputCache(PolicyName = LMS.WebApi.Common.PublicReadCacheHeaderPolicy.Name)]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<SpecializationDto>>>> Public(CancellationToken ct)
    {
        var r = await sender.Send(new GetSpecializationsQuery(IncludeInactive: false), ct);
        return Ok(ApiResponse<IReadOnlyCollection<SpecializationDto>>.Ok(r.Data, r.Message));
    }

    [HttpPost]
    [PermissionAuthorize(Permissions.Specializations.Manage)]
    public async Task<ActionResult<ApiResponse<SpecializationDto>>> Create(
        [FromBody] CreateSpecializationCommand cmd, CancellationToken ct)
    {
        var r = await sender.Send(cmd, ct);
        return r.ToApiResult();
    }

    [HttpPut("{id:guid}")]
    [PermissionAuthorize(Permissions.Specializations.Manage)]
    public async Task<ActionResult<ApiResponse<SpecializationDto>>> Update(
        Guid id, [FromBody] UpdateSpecializationCommand cmd, CancellationToken ct)
    {
        var r = await sender.Send(cmd with { SpecializationId = id }, ct);
        return r.ToApiResult();
    }

    [HttpPost("{id:guid}/active")]
    [PermissionAuthorize(Permissions.Specializations.Manage)]
    public async Task<ActionResult<ApiResponse<SpecializationDto>>> SetActive(
        Guid id, [FromBody] SetSpecializationActiveCommand cmd, CancellationToken ct)
    {
        var r = await sender.Send(cmd with { SpecializationId = id }, ct);
        return r.ToApiResult();
    }

    [HttpDelete("{id:guid}")]
    [PermissionAuthorize(Permissions.Specializations.Manage)]
    public async Task<ActionResult<ApiResponse<object>>> Delete(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new DeleteSpecializationCommand(id), ct);
        return r.Success
            ? Ok(ApiResponse<object>.Ok(new { }, r.Message))
            : BadRequest(ApiResponse<object>.Fail(r.Message ?? "Failed"));
    }

    /// <summary>Returns the specializations the given staff member is assigned to.</summary>
    [HttpGet("staff/{staffProfileId:guid}")]
    [PermissionAuthorize(Permissions.Specializations.Read)]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<SpecializationDto>>>> ForStaff(
        Guid staffProfileId, CancellationToken ct)
    {
        var r = await sender.Send(new GetStaffSpecializationsQuery(staffProfileId), ct);
        return Ok(ApiResponse<IReadOnlyCollection<SpecializationDto>>.Ok(r.Data, r.Message));
    }

    /// <summary>
    /// Replaces the staff member's specialization set with the provided list.
    /// Empty list clears all. Teachers may call this against their own
    /// profile; admins may call it against anyone.
    /// </summary>
    [HttpPut("staff/{staffProfileId:guid}")]
    [PermissionAuthorize(Permissions.Specializations.Read)]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<SpecializationDto>>>> SetForStaff(
        Guid staffProfileId, [FromBody] SetStaffSpecializationsCommand cmd, CancellationToken ct)
    {
        var r = await sender.Send(cmd with { StaffProfileId = staffProfileId }, ct);
        return r.ToApiResult();
    }
}
