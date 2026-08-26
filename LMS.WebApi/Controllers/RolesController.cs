using LMS.Application.Common.Security;
using LMS.Application.Features.Roles;
using LMS.WebApi.Common;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LMS.WebApi.Controllers;

/// <summary>
/// RBAC administration — roles, the permission catalog, the role→permission
/// matrix, and a "who can access what" overview. Deliberately SuperAdmin-ONLY:
/// this is the god-mode surface that can grant any permission to any role, so a
/// regular Admin (who holds every operational permission) still can't reach it.
/// The gate is role-based, not permission-based, precisely so it can't be
/// self-granted through the matrix it controls.
/// </summary>
[ApiController]
[Route("api/admin/rbac")]
[Authorize(Roles = RoleCodes.SuperAdmin)]
public sealed class RolesController(ISender sender) : ControllerBase
{
    [HttpGet("roles")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<RoleDto>>>> GetRoles(CancellationToken ct)
    {
        var r = await sender.Send(new GetRolesQuery(), ct);
        return Ok(ApiResponse<IReadOnlyCollection<RoleDto>>.Ok(r.Data, r.Message));
    }

    [HttpPost("roles")]
    public async Task<ActionResult<ApiResponse<RoleDto>>> CreateRole([FromBody] CreateRoleCommand cmd, CancellationToken ct)
    {
        var r = await sender.Send(cmd, ct);
        return r.ToApiResult();
    }

    [HttpPut("roles/{id:guid}")]
    public async Task<ActionResult<ApiResponse<RoleDto>>> UpdateRole(Guid id, [FromBody] UpdateRoleCommand cmd, CancellationToken ct)
    {
        var r = await sender.Send(cmd with { RoleId = id }, ct);
        return r.ToApiResult();
    }

    [HttpDelete("roles/{id:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteRole(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new DeleteRoleCommand(id), ct);
        return r.Success ? Ok(ApiResponse<object>.Ok(new { }, r.Message)) : BadRequest(ApiResponse<object>.Fail(r.Message ?? "Failed"));
    }

    [HttpGet("permissions")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<PermissionDto>>>> GetPermissions(CancellationToken ct)
    {
        var r = await sender.Send(new GetPermissionsQuery(), ct);
        return Ok(ApiResponse<IReadOnlyCollection<PermissionDto>>.Ok(r.Data, r.Message));
    }

    [HttpPost("permissions")]
    public async Task<ActionResult<ApiResponse<PermissionDto>>> CreatePermission([FromBody] CreatePermissionCommand cmd, CancellationToken ct)
    {
        var r = await sender.Send(cmd, ct);
        return r.ToApiResult();
    }

    [HttpPut("permissions/{id:guid}")]
    public async Task<ActionResult<ApiResponse<PermissionDto>>> UpdatePermission(Guid id, [FromBody] UpdatePermissionCommand cmd, CancellationToken ct)
    {
        var r = await sender.Send(cmd with { PermissionId = id }, ct);
        return r.ToApiResult();
    }

    [HttpDelete("permissions/{id:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> DeletePermission(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new DeletePermissionCommand(id), ct);
        return r.Success ? Ok(ApiResponse<object>.Ok(new { }, r.Message)) : BadRequest(ApiResponse<object>.Fail(r.Message ?? "Failed"));
    }

    [HttpPut("roles/{id:guid}/permissions")]
    public async Task<ActionResult<ApiResponse<object>>> AssignPermissions(Guid id, [FromBody] IReadOnlyCollection<Guid> permissionIds, CancellationToken ct)
    {
        var r = await sender.Send(new AssignRolePermissionsCommand(id, permissionIds), ct);
        return r.Success ? Ok(ApiResponse<object>.Ok(new { }, r.Message)) : BadRequest(ApiResponse<object>.Fail(r.Message ?? "Failed"));
    }

    /// <summary>Access overview: every user with their roles + effective permission codes.</summary>
    [HttpGet("users")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<UserAccessDto>>>> GetAccessOverview(CancellationToken ct)
    {
        var r = await sender.Send(new GetAccessOverviewQuery(), ct);
        return Ok(ApiResponse<IReadOnlyCollection<UserAccessDto>>.Ok(r.Data, r.Message));
    }

    /// <summary>The permission ids granted directly to one user (on top of their roles).</summary>
    [HttpGet("users/{userId:guid}/permissions")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<Guid>>>> GetUserPermissions(Guid userId, CancellationToken ct)
    {
        var r = await sender.Send(new GetUserPermissionsQuery(userId), ct);
        return Ok(ApiResponse<IReadOnlyCollection<Guid>>.Ok(r.Data, r.Message));
    }

    /// <summary>Replace a user's direct permission grants with exactly the given set.</summary>
    [HttpPut("users/{userId:guid}/permissions")]
    public async Task<ActionResult<ApiResponse<object>>> SetUserPermissions(Guid userId, [FromBody] IReadOnlyCollection<Guid> permissionIds, CancellationToken ct)
    {
        var r = await sender.Send(new SetUserPermissionsCommand(userId, permissionIds), ct);
        return r.Success ? Ok(ApiResponse<object>.Ok(new { }, r.Message)) : BadRequest(ApiResponse<object>.Fail(r.Message ?? "Failed"));
    }
}
