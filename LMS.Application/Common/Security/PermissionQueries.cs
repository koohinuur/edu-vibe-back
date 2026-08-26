using LMS.Application.Common.Abstractions;

namespace LMS.Application.Common.Security;

/// <summary>
/// Single source of truth for a user's EFFECTIVE permission codes: the union of
/// what their roles grant and what's granted to them directly (user_permissions).
/// Used by the token issuers (login / refresh / Telegram) and the per-request
/// authorization handler so all four agree on what a user can do.
/// </summary>
public static class PermissionQueries
{
    /// <summary>
    /// Distinct permission codes the user effectively holds (role ∪ direct grants),
    /// as a composable IQueryable so callers can `.AnyAsync(...)` or `.ToArrayAsync()`.
    /// </summary>
    public static IQueryable<string> EffectivePermissionCodes(this IApplicationDbContext db, Guid userId)
    {
        var rolePermissionCodes = db.UserRoles
            .Where(ur => ur.UserId == userId)
            .Join(db.RolePermissions, ur => ur.RoleId, rp => rp.RoleId, (ur, rp) => rp.PermissionId)
            .Join(db.Permissions, pid => pid, p => p.Id, (pid, p) => p.Code);

        var directPermissionCodes = db.UserPermissions
            .Where(up => up.UserId == userId)
            .Join(db.Permissions, up => up.PermissionId, p => p.Id, (up, p) => p.Code);

        return rolePermissionCodes.Concat(directPermissionCodes).Distinct();
    }
}
