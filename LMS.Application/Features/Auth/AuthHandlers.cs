using System.Security.Cryptography;
using LMS.Application.Common.Abstractions;
using LMS.Application.Common.Models;
using LMS.Application.Common.Security;
using LMS.Domain.Entities;
using LMS.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using UserRole = LMS.Domain.Entities.UserRole;

namespace LMS.Application.Features.Auth;

public sealed class RegisterUserCommandHandler(
    IApplicationDbContext dbContext,
    IPasswordHasher passwordHasher,
    IJwtTokenGenerator jwtTokenGenerator,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<RegisterUserCommand, Result<AuthTokensResponse>>
{
    public async Task<Result<AuthTokensResponse>> Handle(RegisterUserCommand request,
        CancellationToken cancellationToken)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        if (await dbContext.Users.AnyAsync(x => x.Email == email, cancellationToken))
            return Result<AuthTokensResponse>.Fail("EMAIL_EXISTS", "Email already exists.");

        // SECURITY: public self-registration ALWAYS creates a Student, never an
        // elevated role. The requested RoleCode is deliberately ignored — without
        // this, an anonymous caller could POST roleCode="admin"/"superadmin" and
        // mint themselves a privileged token. Staff/admin accounts are created
        // only through the authenticated, anti-escalation AssignRole path.
        var role = await dbContext.Roles.FirstOrDefaultAsync(x => x.Code == RoleCodes.Student, cancellationToken);
        if (role is null) return Result<AuthTokensResponse>.Fail("ROLE_NOT_FOUND", "Student role is not configured.");

        var user = new User(email, passwordHasher.Hash(request.Password));
        user.SetPhone(request.Phone);
        await dbContext.Users.AddAsync(user, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        await dbContext.UserRoles.AddAsync(new UserRole(user.Id, role.Id), cancellationToken);

        StudentProfile? studentProfile = null;
        StaffProfile? staffProfile = null;

        if (string.Equals(role.Code, RoleCodes.Student, StringComparison.OrdinalIgnoreCase))
        {
            studentProfile = await dbContext.StudentProfiles
                .FirstOrDefaultAsync(x => x.UserId == user.Id, cancellationToken);
            if (studentProfile is null)
            {
                studentProfile = new StudentProfile(user.Id, user);
                await dbContext.StudentProfiles.AddAsync(studentProfile, cancellationToken);
            }
        }
        else
        {
            staffProfile = await dbContext.StaffProfiles
                .FirstOrDefaultAsync(x => x.UserId == user.Id, cancellationToken);
            if (staffProfile is null)
            {
                staffProfile = new StaffProfile(user.Id, EmploymentType.FullTime);
                await dbContext.StaffProfiles.AddAsync(staffProfile, cancellationToken);
            }
        }

        var roles = new[] { role.Code };
        var permissions = await dbContext.RolePermissions
            .Where(rp => rp.RoleId == role.Id)
            .Join(dbContext.Permissions, rp => rp.PermissionId, p => p.Id, (rp, p) => p.Code)
            .ToArrayAsync(cancellationToken);
        var accessToken = jwtTokenGenerator.Generate(
            user.Id, user.Email, roles, permissions,
            studentProfileId: studentProfile?.Id,
            staffProfileId: staffProfile?.Id);
        var refreshToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        var refreshHash = passwordHasher.Hash(refreshToken);

        user.SetRefreshToken(refreshHash, dateTimeProvider.UtcNow.Add(jwtTokenGenerator.RefreshTokenLifetime));
        await dbContext.SaveChangesAsync(cancellationToken);

        return Result<AuthTokensResponse>.Ok(new AuthTokensResponse(user.Id, user.Email, accessToken, refreshToken,
            roles, permissions));
    }
}

public sealed class LoginCommandHandler(
    IApplicationDbContext dbContext,
    IPasswordHasher passwordHasher,
    IJwtTokenGenerator jwtTokenGenerator,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<LoginCommand, Result<AuthTokensResponse>>
{
    public async Task<Result<AuthTokensResponse>> Handle(LoginCommand request, CancellationToken cancellationToken)
    {
        // The identifier may be an email OR a phone. Match by whichever fits so
        // users can sign in with either. (The command field is still named Email
        // for wire-compat; it carries the identifier.)
        var identifier = request.Email.Trim();
        var email = identifier.ToLowerInvariant();
        var phone = Domain.Entities.User.NormalizePhone(identifier);
        var user = await dbContext.Users.FirstOrDefaultAsync(
            x => x.Email == email || (phone != null && x.Phone == phone), cancellationToken);
        if (user is null) return Result<AuthTokensResponse>.Fail("INVALID_CREDENTIALS", "Invalid credentials.");
        if (user.Status != UserStatus.Active)
            return Result<AuthTokensResponse>.Fail("USER_INACTIVE", "User is not active.");
        if (!passwordHasher.Verify(request.Password, user.PasswordHash))
            return Result<AuthTokensResponse>.Fail("INVALID_CREDENTIALS", "Invalid credentials.");

        var roles = await dbContext.UserRoles.Where(x => x.UserId == user.Id)
            .Join(dbContext.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => r.Code).ToArrayAsync(cancellationToken);
        var permissions = await dbContext.UserRoles.Where(x => x.UserId == user.Id)
            .Join(dbContext.RolePermissions, ur => ur.RoleId, rp => rp.RoleId, (ur, rp) => rp.PermissionId)
            .Join(dbContext.Permissions, pid => pid, p => p.Id, (pid, p) => p.Code)
            .Distinct()
            .ToArrayAsync(cancellationToken);

        var studentProfileId = await dbContext.StudentProfiles
            .Where(x => x.UserId == user.Id)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        var staffProfileId = await dbContext.StaffProfiles
            .Where(x => x.UserId == user.Id)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        var accessToken = jwtTokenGenerator.Generate(
            user.Id, user.Email, roles, permissions,
            studentProfileId: studentProfileId,
            staffProfileId: staffProfileId);
        var refreshToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        var refreshHash = passwordHasher.Hash(refreshToken);
        user.SetRefreshToken(refreshHash, dateTimeProvider.UtcNow.Add(jwtTokenGenerator.RefreshTokenLifetime));
        await dbContext.SaveChangesAsync(cancellationToken);

        return Result<AuthTokensResponse>.Ok(new AuthTokensResponse(user.Id, user.Email, accessToken, refreshToken,
            roles, permissions));
    }
}

public sealed class RefreshTokenCommandHandler(
    IApplicationDbContext dbContext,
    IPasswordHasher passwordHasher,
    IJwtTokenGenerator jwtTokenGenerator,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<RefreshTokenCommand, Result<AuthTokensResponse>>
{
    public async Task<Result<AuthTokensResponse>> Handle(RefreshTokenCommand request,
        CancellationToken cancellationToken)
    {
        // The refresh token hash is salted (PBKDF2-style), so SQL equality
        // can't pre-filter to a single row. But we CAN narrow the candidate
        // set to active users with a non-expired token via the
        // (RefreshTokenHash, RefreshTokenExpiresAt) composite index. The
        // in-memory hash verify then runs over a tiny set instead of every
        // user with any refresh token ever issued.
        var now = dateTimeProvider.UtcNow;
        var candidates = await dbContext.Users
            .Where(x => x.RefreshTokenHash != null
                        && x.RefreshTokenExpiresAt != null
                        && x.RefreshTokenExpiresAt >= now
                        && x.Status == UserStatus.Active)
            .ToListAsync(cancellationToken);

        var user = candidates.FirstOrDefault(x =>
            x.RefreshTokenHash is not null
            && passwordHasher.Verify(request.RefreshToken, x.RefreshTokenHash));
        if (user is null) return Result<AuthTokensResponse>.Fail("INVALID_REFRESH_TOKEN", "Invalid refresh token.");

        var roles = await dbContext.UserRoles.Where(x => x.UserId == user.Id)
            .Join(dbContext.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => r.Code).ToArrayAsync(cancellationToken);
        var permissions = await dbContext.UserRoles.Where(x => x.UserId == user.Id)
            .Join(dbContext.RolePermissions, ur => ur.RoleId, rp => rp.RoleId, (ur, rp) => rp.PermissionId)
            .Join(dbContext.Permissions, pid => pid, p => p.Id, (pid, p) => p.Code)
            .Distinct()
            .ToArrayAsync(cancellationToken);

        var studentProfileId = await dbContext.StudentProfiles
            .Where(x => x.UserId == user.Id)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        var staffProfileId = await dbContext.StaffProfiles
            .Where(x => x.UserId == user.Id)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        var accessToken = jwtTokenGenerator.Generate(
            user.Id, user.Email, roles, permissions,
            studentProfileId: studentProfileId,
            staffProfileId: staffProfileId);

        var newRefreshToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        user.SetRefreshToken(passwordHasher.Hash(newRefreshToken), dateTimeProvider.UtcNow.Add(jwtTokenGenerator.RefreshTokenLifetime));
        await dbContext.SaveChangesAsync(cancellationToken);

        return Result<AuthTokensResponse>.Ok(new AuthTokensResponse(user.Id, user.Email, accessToken, newRefreshToken,
            roles, permissions));
    }
}

/// <summary>
/// Self-service password change. Validates the current password against the
/// stored hash, rejects same-as-current, enforces the shared
/// <see cref="PasswordPolicy"/> (length + character-class complexity), and
/// wipes the refresh token so any other open session has to re-login.
/// </summary>
public sealed class ChangePasswordCommandHandler(
    IApplicationDbContext dbContext,
    IPasswordHasher passwordHasher,
    ICurrentUserService currentUser) : IRequestHandler<ChangePasswordCommand, Result>
{
    public async Task<Result> Handle(ChangePasswordCommand request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is null)
            return Result.Fail("UNAUTHENTICATED", "Caller must be authenticated.");

        if (string.IsNullOrWhiteSpace(request.CurrentPassword))
            return Result.Fail("VALIDATION", "Current password is required.");
        if (PasswordPolicy.Validate(request.NewPassword) is { } policyError)
            return Result.Fail("VALIDATION", policyError);
        if (request.NewPassword == request.CurrentPassword)
            return Result.Fail("VALIDATION", "New password must differ from the current one.");

        var user = await dbContext.Users.FirstOrDefaultAsync(
            x => x.Id == currentUser.UserId.Value, cancellationToken);
        if (user is null) return Result.Fail("USER_NOT_FOUND", "User not found.");

        if (!passwordHasher.Verify(request.CurrentPassword, user.PasswordHash))
            return Result.Fail("INVALID_CREDENTIALS", "Current password is incorrect.");

        user.SetPasswordHash(passwordHasher.Hash(request.NewPassword));
        // Force re-authentication on any other open session. The caller stays
        // signed in on this device because the access token they're using
        // remains valid until its TTL; the next refresh will fail and they'll
        // be sent through login again — that's intentional and matches the
        // typical "password changed → log everyone out" UX.
        user.ClearRefreshToken();
        await dbContext.SaveChangesAsync(cancellationToken);
        return Result.Ok("Password updated.");
    }
}

public sealed class AssignRoleCommandHandler(IApplicationDbContext dbContext, ICurrentUserService currentUser)
    : IRequestHandler<AssignRoleCommand, Result>
{
    public async Task<Result> Handle(AssignRoleCommand request, CancellationToken cancellationToken)
    {
        // Anti-privilege-escalation guards. The controller already gates on
        // Auth.AssignRole permission; these are defence-in-depth.
        if (currentUser.UserId is null)
            return Result.Fail("UNAUTHENTICATED", "Caller must be authenticated.");

        if (request.UserId == currentUser.UserId.Value)
            return Result.Fail("SELF_ROLE_CHANGE_FORBIDDEN", "You cannot modify your own roles.");

        // Only a SuperAdmin can promote another user to SuperAdmin.
        if (string.Equals(request.RoleCode, RoleCodes.SuperAdmin, StringComparison.OrdinalIgnoreCase)
            && !currentUser.IsInRole(RoleCodes.SuperAdmin))
            return Result.Fail("INSUFFICIENT_PRIVILEGES",
                "Only a SuperAdmin may grant the SuperAdmin role.");

        // Only Admin/SuperAdmin can grant Admin.
        if (string.Equals(request.RoleCode, RoleCodes.Admin, StringComparison.OrdinalIgnoreCase)
            && !currentUser.IsInRole(RoleCodes.SuperAdmin)
            && !currentUser.IsInRole(RoleCodes.Admin))
            return Result.Fail("INSUFFICIENT_PRIVILEGES",
                "Only an Admin or SuperAdmin may grant the Admin role.");

        var user = await dbContext.Users.FirstOrDefaultAsync(x => x.Id == request.UserId, cancellationToken);
        if (user is null) return Result.Fail("USER_NOT_FOUND", "User not found.");

        var role = await dbContext.Roles.FirstOrDefaultAsync(x => x.Code == request.RoleCode, cancellationToken);
        if (role is null) return Result.Fail("ROLE_NOT_FOUND", "Role not found.");

        var exists =
            await dbContext.UserRoles.AnyAsync(x => x.UserId == user.Id && x.RoleId == role.Id, cancellationToken);
        if (exists) return Result.Ok("Role already assigned.");

        await dbContext.UserRoles.AddAsync(new UserRole(user.Id, role.Id), cancellationToken);

        if (string.Equals(role.Code, RoleCodes.Student, StringComparison.OrdinalIgnoreCase))
        {
            if (!await dbContext.StudentProfiles.AnyAsync(x => x.UserId == user.Id, cancellationToken))
                await dbContext.StudentProfiles.AddAsync(new StudentProfile(user.Id, user), cancellationToken);
        }
        else
        {
            if (!await dbContext.StaffProfiles.AnyAsync(x => x.UserId == user.Id, cancellationToken))
                await dbContext.StaffProfiles.AddAsync(new StaffProfile(user.Id, EmploymentType.FullTime),
                    cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return Result.Ok("Role assigned.");
    }
}

/// <summary>
/// Password-reset request. Resolves the user by email OR phone; if they exist,
/// are active, and have a linked Telegram, stores a hashed one-time code (15-min
/// TTL) and DMs the code via the platform bot. The response is deliberately
/// generic for every path so it can't be used to enumerate accounts.
/// </summary>
public sealed class ForgotPasswordCommandHandler(
    IApplicationDbContext dbContext,
    IPasswordHasher passwordHasher,
    IDateTimeProvider dateTimeProvider,
    ITelegramNotifier notifier) : IRequestHandler<ForgotPasswordCommand, Result>
{
    private const string Generic = "If the account exists and has Telegram linked, a reset code was sent there.";

    public async Task<Result> Handle(ForgotPasswordCommand request, CancellationToken cancellationToken)
    {
        var identifier = request.Identifier.Trim();
        var email = identifier.ToLowerInvariant();
        var phone = User.NormalizePhone(identifier);
        var user = await dbContext.Users.FirstOrDefaultAsync(
            x => x.Email == email || (phone != null && x.Phone == phone), cancellationToken);
        if (user is null || user.Status != UserStatus.Active) return Result.Ok(Generic);

        var tg = await dbContext.TelegramAccounts
            .FirstOrDefaultAsync(t => t.UserId == user.Id, cancellationToken);
        if (tg is null) return Result.Ok(Generic);

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        user.SetPasswordResetCode(passwordHasher.Hash(code), dateTimeProvider.UtcNow.AddMinutes(15));
        await dbContext.SaveChangesAsync(cancellationToken);

        await notifier.SendToUserAsync(
            tg.TelegramUserId,
            $"EduVibe password reset code: {code}\nIt expires in 15 minutes. If you didn't request this, ignore this message.",
            cancellationToken);

        return Result.Ok(Generic);
    }
}

/// <summary>
/// Completes a reset: validates the new password, then requires a matching,
/// unexpired code for the user resolved by email OR phone. On success the
/// password is set, the code cleared, and other sessions invalidated.
/// </summary>
public sealed class ResetPasswordCommandHandler(
    IApplicationDbContext dbContext,
    IPasswordHasher passwordHasher,
    IDateTimeProvider dateTimeProvider) : IRequestHandler<ResetPasswordCommand, Result>
{
    public async Task<Result> Handle(ResetPasswordCommand request, CancellationToken cancellationToken)
    {
        if (PasswordPolicy.Validate(request.NewPassword) is { } policyError)
            return Result.Fail("VALIDATION", policyError);

        var identifier = request.Identifier.Trim();
        var email = identifier.ToLowerInvariant();
        var phone = User.NormalizePhone(identifier);
        var user = await dbContext.Users.FirstOrDefaultAsync(
            x => x.Email == email || (phone != null && x.Phone == phone), cancellationToken);

        if (user is null
            || user.PasswordResetCodeHash is null
            || user.PasswordResetExpiresAt is null
            || user.PasswordResetExpiresAt < dateTimeProvider.UtcNow
            || !passwordHasher.Verify(request.Code.Trim(), user.PasswordResetCodeHash))
            return Result.Fail("INVALID_RESET", "Invalid or expired code.");

        user.SetPasswordHash(passwordHasher.Hash(request.NewPassword));
        user.ClearPasswordResetCode();
        user.ClearRefreshToken(); // sign out other sessions
        await dbContext.SaveChangesAsync(cancellationToken);
        return Result.Ok("Password reset. You can now sign in with your new password.");
    }
}
