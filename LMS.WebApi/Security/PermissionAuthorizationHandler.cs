using System.Security.Claims;
using LMS.Application.Common.Abstractions;
using LMS.Application.Common.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace LMS.WebApi.Security;

public sealed class PermissionAuthorizationHandler(IApplicationDbContext db) : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (!context.User.Identity?.IsAuthenticated ?? true) return;

        if (context.User.IsInRole(RoleCodes.SuperAdmin))
        {
            context.Succeed(requirement);
            return;
        }

        if (context.User.Claims.Any(c => c.Type == "permission" && string.Equals(c.Value, requirement.Permission, StringComparison.OrdinalIgnoreCase)))
        {
            context.Succeed(requirement);
            return;
        }

        var userIdRaw = context.User.FindFirstValue("userId");
        if (!Guid.TryParse(userIdRaw, out var userId)) return;

        // Union of role grants + direct per-user grants — so a permission handed
        // to a user directly takes effect on the next request, without re-login.
        var hasPermission = await db.EffectivePermissionCodes(userId)
            .AnyAsync(code => code == requirement.Permission);

        if (hasPermission) context.Succeed(requirement);
    }
}
