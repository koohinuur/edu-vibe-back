using LMS.Application.Common.Abstractions;
using LMS.Application.Common.Models;
using LMS.Application.Common.Security;
using LMS.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LMS.Application.Features.CmsAccess;

/// <summary>
/// "Attach staff to the CMS" — a simple picker instead of the RBAC matrix.
/// Ticking a staff member grants them the CMS permission bundle directly (on
/// top of their roles); unticking removes it. Admins already hold everything,
/// so they're never toggled here.
/// </summary>
public sealed class CmsAccessHandlers(IApplicationDbContext db) :
    IRequestHandler<GetCmsStaffQuery, Result<IReadOnlyCollection<CmsStaffDto>>>,
    IRequestHandler<SetCmsAccessCommand, Result>
{
    // The permissions that unlock the CMS content tabs (Announcements, Office
    // Info, Courses, Videos, Mock tests, Results). Deliberately NOT Staff.Update
    // — attaching someone to CMS content shouldn't hand them staff editing.
    private static readonly string[] CmsBundle =
    {
        Permissions.Marketing.Manage,
        Permissions.Announcements.Read, Permissions.Announcements.Manage,
        Permissions.OfficeInfo.Read, Permissions.OfficeInfo.Manage,
        Permissions.Results.Read, Permissions.Results.Create,
        Permissions.Results.Update, Permissions.Results.Delete,
    };

    // The single code we treat as the "has CMS access" marker in the UI.
    private const string MarkerCode = Permissions.Marketing.Manage;

    public async Task<Result<IReadOnlyCollection<CmsStaffDto>>> Handle(GetCmsStaffQuery request, CancellationToken cancellationToken)
    {
        // Staff = users with a staff profile. Admins/SuperAdmins are excluded:
        // they already have full access, so toggling them is meaningless.
        var superRoleUserIds = await db.UserRoles.AsNoTracking()
            .Join(db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => new { ur.UserId, r.Code })
            .Where(x => x.Code == RoleCodes.Admin || x.Code == RoleCodes.SuperAdmin)
            .Select(x => x.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var superSet = superRoleUserIds.ToHashSet();

        var staff = await db.StaffProfiles.AsNoTracking()
            .Join(db.Users, sp => sp.UserId, u => u.Id, (sp, u) => new
            {
                u.Id,
                u.Email,
                sp.FirstName,
                sp.LastName,
            })
            .ToListAsync(cancellationToken);

        // Marketing.Manage granted DIRECTLY (via this feature) = has access.
        var withDirectMarker = (await db.UserPermissions.AsNoTracking()
                .Join(db.Permissions, up => up.PermissionId, p => p.Id, (up, p) => new { up.UserId, p.Code })
                .Where(x => x.Code == MarkerCode)
                .Select(x => x.UserId)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        var list = staff
            .Where(s => !superSet.Contains(s.Id))
            .Select(s =>
            {
                var name = string.Join(" ", new[] { s.FirstName, s.LastName }.Where(p => !string.IsNullOrWhiteSpace(p))).Trim();
                if (string.IsNullOrWhiteSpace(name)) name = s.Email;
                return new CmsStaffDto(s.Id, name, s.Email, withDirectMarker.Contains(s.Id));
            })
            .OrderBy(x => x.Name)
            .ToList();

        return Result<IReadOnlyCollection<CmsStaffDto>>.Ok(list);
    }

    public async Task<Result> Handle(SetCmsAccessCommand request, CancellationToken cancellationToken)
    {
        var bundleIds = await db.Permissions
            .Where(p => CmsBundle.Contains(p.Code))
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);
        if (bundleIds.Count == 0)
            return Result.Fail("VALIDATION", "CMS permissions aren't seeded yet.");
        var bundleSet = bundleIds.ToHashSet();

        // Only staff users may be toggled (guards against granting the bundle to
        // a student id passed by a tampered request).
        var staffUserIds = (await db.StaffProfiles.Select(sp => sp.UserId).ToListAsync(cancellationToken)).ToHashSet();
        var desired = request.UserIds.Where(staffUserIds.Contains).ToHashSet();

        // Everyone who currently holds any bundle grant directly.
        var currentGrants = await db.UserPermissions
            .Where(up => bundleSet.Contains(up.PermissionId))
            .ToListAsync(cancellationToken);
        var grantedByUser = currentGrants
            .GroupBy(g => g.UserId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.PermissionId).ToHashSet());

        var toAdd = new List<UserPermission>();
        var toRemove = new List<UserPermission>();

        // Grant the full bundle to each desired staff user (only the missing ids).
        foreach (var userId in desired)
        {
            var has = grantedByUser.GetValueOrDefault(userId, new HashSet<Guid>());
            foreach (var pid in bundleIds)
                if (!has.Contains(pid)) toAdd.Add(new UserPermission(userId, pid));
        }

        // Revoke the bundle from anyone who has it but is no longer desired.
        foreach (var grant in currentGrants)
            if (!desired.Contains(grant.UserId)) toRemove.Add(grant);

        if (toAdd.Count > 0) await db.UserPermissions.AddRangeAsync(toAdd, cancellationToken);
        if (toRemove.Count > 0) db.UserPermissions.RemoveRange(toRemove);
        if (toAdd.Count > 0 || toRemove.Count > 0) await db.SaveChangesAsync(cancellationToken);

        return Result.Ok("CMS access updated.");
    }
}
