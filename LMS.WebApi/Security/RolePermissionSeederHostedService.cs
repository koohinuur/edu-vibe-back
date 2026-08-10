using LMS.Application.Common.Abstractions;
using LMS.Application.Common.Security;
using LMS.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace LMS.WebApi.Security;

/// <summary>
/// Applies <see cref="RolePermissionMatrix"/> defaults to roles. Two modes:
///
/// <para><b>Bootstrap-once (default).</b> The first time a role has zero rows
/// in <c>role_permissions</c> the matrix defaults land. Once it has any grants
/// — whether seeded here previously or created via the admin API — this service
/// leaves it alone forever. This protects an admin's deliberate removals from
/// being silently undone on the next boot.</para>
///
/// <para><b>Top-up mode (opt-in via config).</b> Set
/// <c>RolePermissions:TopUpDefaults=true</c> in configuration to additionally
/// grant any matrix permission a role is missing — useful when the matrix grew
/// new entries (e.g. new modules) and existing roles need to catch up. This
/// mode WILL re-add anything an admin removed, so flip it on once, restart,
/// then flip it back off.</para>
///
/// Runs immediately after <see cref="PermissionDiscoveryHostedService"/> in
/// <c>Program.cs</c>'s registration order, so the <c>permissions</c> table
/// is guaranteed populated when we look up ids by code.
/// </summary>
public sealed class RolePermissionSeederHostedService(
    IServiceProvider serviceProvider,
    IConfiguration configuration,
    ILogger<RolePermissionSeederHostedService> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();

        var permissionIdByCode = await db.Permissions
            .ToDictionaryAsync(p => p.Code, p => p.Id,
                StringComparer.OrdinalIgnoreCase, cancellationToken);

        if (permissionIdByCode.Count == 0)
        {
            logger.LogWarning(
                "Permissions table is empty — skipping default grant bootstrap. " +
                "PermissionDiscoveryHostedService should run first.");
            return;
        }

        var roleIdByCode = await db.Roles
            .ToDictionaryAsync(r => r.Code, r => r.Id,
                StringComparer.OrdinalIgnoreCase, cancellationToken);

        // Existing (role, permission) pairs — used by both bootstrap and top-up
        // modes to know what's already there.
        var existingPairs = await db.RolePermissions
            .Select(rp => new { rp.RoleId, rp.PermissionId })
            .ToListAsync(cancellationToken);
        var existing = new HashSet<(Guid RoleId, Guid PermissionId)>(
            existingPairs.Select(p => (p.RoleId, p.PermissionId)));
        var initialized = existing.Select(p => p.RoleId).ToHashSet();

        var topUp = configuration.GetValue("RolePermissions:TopUpDefaults", false);

        // (RoleCode → matrix defaults). We only seed the three primary roles
        // the UI surfaces. SuperAdmin/AcademyDirector/OfficeAdmin/SupportTeacher
        // rows still exist in the roles table for backward compatibility with
        // any historical user assignments, but they receive no automatic
        // matrix top-ups — if you still need them, manage their grants
        // explicitly via the /api/Roles admin endpoints.
        var defaults = new (string RoleCode, IEnumerable<string> Codes)[]
        {
            (RoleCodes.Admin,      Permissions.All),
            (RoleCodes.Teacher,    RolePermissionMatrix.ForTeacher),
            (RoleCodes.Student,    RolePermissionMatrix.ForStudent),
            (RoleCodes.SmmManager, RolePermissionMatrix.ForSmm),
        };

        var bootstrappedRoles = 0;
        var toppedUpRoles = 0;
        var insertedRows = 0;

        foreach (var (roleCode, codes) in defaults)
        {
            if (!roleIdByCode.TryGetValue(roleCode, out var roleId))
            {
                logger.LogWarning("Role {Role} not found in DB — skipping bootstrap.", roleCode);
                continue;
            }

            var isInitialized = initialized.Contains(roleId);

            // All three primary roles get an additive top-up on every boot:
            // when the matrix gains a permission (e.g. Practice.Read,
            // Analytics.Read), existing deployments must receive it or the
            // permission-gated UI silently hides features. The earlier
            // "bootstrap-once for Teacher/Student" safety left both roles
            // without the newer grants — exactly the bug that emptied their
            // sidebars. Top-up only ever ADDS missing matrix rows; grants an
            // admin added beyond the matrix are never touched or removed.
            _ = topUp; // config flag retained for wire-compat; top-up is now always on

            var addedThisRole = 0;
            foreach (var code in codes)
            {
                if (!permissionIdByCode.TryGetValue(code, out var permissionId))
                {
                    logger.LogWarning(
                        "Default permission {Permission} for role {Role} is not in the DB — skipping.",
                        code, roleCode);
                    continue;
                }

                // Top-up mode: skip pairs that already exist; only insert the
                // missing matrix entries. Bootstrap mode: nothing exists for
                // this role yet, so the existing-set check is also safe.
                if (existing.Contains((roleId, permissionId))) continue;

                await db.RolePermissions.AddAsync(new RolePermission(roleId, permissionId),
                    cancellationToken);
                existing.Add((roleId, permissionId));
                addedThisRole++;
                insertedRows++;
            }

            if (addedThisRole > 0)
            {
                if (isInitialized) toppedUpRoles++;
                else bootstrappedRoles++;
            }
        }

        if (insertedRows > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            if (bootstrappedRoles > 0)
                logger.LogInformation(
                    "Bootstrapped default permissions for {Roles} role(s).", bootstrappedRoles);
            if (toppedUpRoles > 0)
                logger.LogInformation(
                    "Topped up {Roles} existing role(s) with {Rows} missing matrix grant(s). " +
                    "Set RolePermissions:TopUpDefaults=false after this run to restore " +
                    "bootstrap-once safety.",
                    toppedUpRoles, insertedRows - (bootstrappedRoles > 0 ? 0 : 0));
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
