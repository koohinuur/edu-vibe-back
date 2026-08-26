using LMS.Application.Common.Models;
using LMS.Domain.Enums;
using MediatR;

namespace LMS.Application.Features.Roles;

public sealed record RoleDto(Guid Id, string Code, string Name, IReadOnlyCollection<Guid> PermissionIds)
{
    /// <summary>Built-in roles can't be renamed (Code) or deleted; the UI locks those actions.</summary>
    public bool IsBuiltIn { get; init; }
}

public sealed record PermissionDto(Guid Id, string Code, string Module, string? Description)
{
    /// <summary>System permissions (discovered from code) can't be deleted; only custom ones can.</summary>
    public bool IsSystem { get; init; }
}

/// <summary>
/// One row of the SuperAdmin "who can access what" overview: a user, their
/// assigned role codes, and the distinct permission codes those roles grant.
/// A SuperAdmin holder has <see cref="IsSuperAdmin"/>=true and implicitly every
/// permission (the auth handler short-circuits), so the UI shows "all access".
/// </summary>
public sealed record UserAccessDto(
    Guid UserId, string Email, UserStatus Status,
    IReadOnlyCollection<string> Roles, IReadOnlyCollection<string> Permissions, bool IsSuperAdmin);

public sealed record GetRolesQuery() : IRequest<Result<IReadOnlyCollection<RoleDto>>>;
public sealed record GetPermissionsQuery() : IRequest<Result<IReadOnlyCollection<PermissionDto>>>;
public sealed record GetAccessOverviewQuery() : IRequest<Result<IReadOnlyCollection<UserAccessDto>>>;
public sealed record CreateRoleCommand(string Code, string Name) : IRequest<Result<RoleDto>>;
public sealed record UpdateRoleCommand(Guid RoleId, string Code, string Name) : IRequest<Result<RoleDto>>;
public sealed record DeleteRoleCommand(Guid RoleId) : IRequest<Result>;
public sealed record CreatePermissionCommand(string Code, string Module, string? Description) : IRequest<Result<PermissionDto>>;
public sealed record UpdatePermissionCommand(Guid PermissionId, string Code, string Module, string? Description) : IRequest<Result<PermissionDto>>;
public sealed record DeletePermissionCommand(Guid PermissionId) : IRequest<Result>;
public sealed record AssignRolePermissionsCommand(Guid RoleId, IReadOnlyCollection<Guid> PermissionIds) : IRequest<Result>;

/// <summary>The permission ids granted DIRECTLY to a user (on top of their roles).</summary>
public sealed record GetUserPermissionsQuery(Guid UserId) : IRequest<Result<IReadOnlyCollection<Guid>>>;

/// <summary>Replace a user's direct permission grants with exactly this set.</summary>
public sealed record SetUserPermissionsCommand(Guid UserId, IReadOnlyCollection<Guid> PermissionIds) : IRequest<Result>;
