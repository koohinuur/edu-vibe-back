using LMS.Application.Common.Abstractions;
using LMS.Application.Common.Models;
using LMS.Application.Common.Security;
using LMS.Application.Common.Storage;
using LMS.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LMS.Application.Features.Students;

public sealed class GetStudentsQueryHandler(IApplicationDbContext db, ICurrentUserService currentUser)
    : IRequestHandler<GetStudentsQuery, Result<PagedResult<StudentDto>>>
{
    public async Task<Result<PagedResult<StudentDto>>> Handle(GetStudentsQuery request,
        CancellationToken cancellationToken)
    {
        var page = new PageRequest(request.Page, request.PageSize, request.Search);

        // Join to an anonymous shape, filter + order on entity columns, project
        // to the DTO last so EF translates the whole query into one SQL
        // statement.
        var query =
            from s in db.StudentProfiles.AsNoTracking()
            join u in db.Users.AsNoTracking() on s.UserId equals u.Id
            select new { s, u };

        // Teacher scoping: admins/office see every student; a teacher only sees
        // students enrolled in a class they teach (Class.TeacherUserId == them).
        // Mirrors the same scope enforced on the XP award so the list and the
        // "Award XP" action stay consistent.
        var isAdmin = currentUser.IsInRole(RoleCodes.Admin)
            || currentUser.IsInRole(RoleCodes.SuperAdmin)
            || currentUser.IsInRole(RoleCodes.OfficeAdmin);
        if (!isAdmin)
        {
            var uid = currentUser.UserId;
            query = uid is null
                ? query.Where(_ => false)
                : query.Where(x => db.Enrollments.Any(e =>
                    e.StudentProfileId == x.s.Id &&
                    db.Classes.Any(c => c.Id == e.ClassId && c.TeacherUserId == uid.Value)));
        }

        if (page.NormalizedSearch is { } search)
        {
            // Match against email AND the new firstName / lastName / phone
            // fields so the admin can search by any of them. ToLower() keeps
            // the comparison case-insensitive; nulls fall through (null !=
            // any contains expression, so they don't match).
            query = query.Where(x =>
                x.u.Email.ToLower().Contains(search) ||
                (x.s.FirstName != null && x.s.FirstName.ToLower().Contains(search)) ||
                (x.s.LastName != null && x.s.LastName.ToLower().Contains(search)) ||
                (x.s.PhoneNumber != null && x.s.PhoneNumber.Contains(search)));
        }

        var total = await query.CountAsync(cancellationToken);
        // Status-first ordering (IsActive DESC): Active students first, then
        // Inactive/Blocked, then by full name (First, Last) ascending with email
        // as the final tiebreaker. Ordered in SQL before Skip/Take so it works
        // with search + pagination.
        var items = await query
            .OrderByDescending(x => x.u.Status == LMS.Domain.Enums.UserStatus.Active)
            .ThenBy(x => x.s.FirstName)
            .ThenBy(x => x.s.LastName)
            .ThenBy(x => x.u.Email)
            .Skip(page.Skip)
            .Take(page.NormalizedPageSize)
            .Select(x => new StudentDto(
                x.s.Id, x.s.UserId, x.u.Email, x.s.XP, x.s.Streak,
                x.s.FirstName, x.s.LastName, x.s.PhoneNumber, x.s.Description,
                x.s.ParentPhoneNumber, x.s.Level, x.s.AvatarUrl, x.u.Status))
            .ToListAsync(cancellationToken);

        return Result<PagedResult<StudentDto>>.Ok(PagedResult<StudentDto>.From(items, total, page));
    }
}

public sealed class GetStudentDetailQueryHandler(IApplicationDbContext db)
    : IRequestHandler<GetStudentDetailQuery, Result<StudentDto>>
{
    public async Task<Result<StudentDto>> Handle(GetStudentDetailQuery request, CancellationToken cancellationToken)
    {
        var sp = await db.StudentProfiles.AsNoTracking()
            .Join(db.Users.AsNoTracking(), s => s.UserId, u => u.Id, (s, u) => new { s, u })
            .FirstOrDefaultAsync(x => x.s.Id == request.StudentProfileId, cancellationToken);
        if (sp is null) return Result<StudentDto>.Fail("NOT_FOUND", "Student profile not found.");
        return Result<StudentDto>.Ok(new StudentDto(
            sp.s.Id, sp.s.UserId, sp.u.Email, sp.s.XP, sp.s.Streak,
            sp.s.FirstName, sp.s.LastName, sp.s.PhoneNumber, sp.s.Description,
            sp.s.ParentPhoneNumber, sp.s.Level, sp.s.AvatarUrl, sp.u.Status));
    }
}

public sealed class RegisterStudentCommandHandler(IApplicationDbContext db)
    : IRequestHandler<RegisterStudentCommand, Result<StudentDto>>
{
    public async Task<Result<StudentDto>> Handle(RegisterStudentCommand request, CancellationToken cancellationToken)
    {
        var user = await db.Users.FirstOrDefaultAsync(x => x.Id == request.UserId, cancellationToken);
        if (user is null) return Result<StudentDto>.Fail("NOT_FOUND", "User not found.");
        var existing = await db.StudentProfiles.FirstOrDefaultAsync(x => x.UserId == request.UserId, cancellationToken);
        if (existing is not null)
            return Result<StudentDto>.Ok(
                new StudentDto(existing.Id, existing.UserId, user.Email, existing.XP, existing.Streak,
                    existing.FirstName, existing.LastName, existing.PhoneNumber, existing.Description,
                    existing.ParentPhoneNumber, existing.Level, existing.AvatarUrl, user.Status),
                "Already exists.");

        var sp = new StudentProfile(user.Id, user);
        await db.StudentProfiles.AddAsync(sp, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Result<StudentDto>.Ok(new StudentDto(sp.Id, sp.UserId, user.Email, sp.XP, sp.Streak,
            sp.FirstName, sp.LastName, sp.PhoneNumber, sp.Description,
            sp.ParentPhoneNumber, sp.Level, sp.AvatarUrl, user.Status));
    }
}

public sealed class UpdateStudentProfileCommandHandler(IApplicationDbContext db)
    : IRequestHandler<UpdateStudentProfileCommand, Result<StudentDto>>
{
    public async Task<Result<StudentDto>> Handle(UpdateStudentProfileCommand request,
        CancellationToken cancellationToken)
    {
        var sp = await db.StudentProfiles.FirstOrDefaultAsync(x => x.Id == request.StudentProfileId, cancellationToken);
        if (sp is null) return Result<StudentDto>.Fail("NOT_FOUND", "Student profile not found.");
        if (request.Xp > sp.XP) sp.AddXp(request.Xp - sp.XP);
        sp.UpdateStreak(request.Streak);
        await db.SaveChangesAsync(cancellationToken);
        var user = await db.Users.FirstAsync(x => x.Id == sp.UserId, cancellationToken);
        return Result<StudentDto>.Ok(new StudentDto(sp.Id, sp.UserId, user.Email, sp.XP, sp.Streak,
            sp.FirstName, sp.LastName, sp.PhoneNumber, sp.Description,
            sp.ParentPhoneNumber, sp.Level, sp.AvatarUrl, user.Status));
    }
}

public sealed class UpdateStudentDetailsCommandHandler(IApplicationDbContext db)
    : IRequestHandler<UpdateStudentDetailsCommand, Result<StudentDto>>
{
    public async Task<Result<StudentDto>> Handle(UpdateStudentDetailsCommand request,
        CancellationToken cancellationToken)
    {
        var sp = await db.StudentProfiles.FirstOrDefaultAsync(x => x.Id == request.StudentProfileId, cancellationToken);
        if (sp is null) return Result<StudentDto>.Fail("NOT_FOUND", "Student profile not found.");
        sp.UpdateProfile(request.FirstName, request.LastName, request.PhoneNumber, request.Description);
        await db.SaveChangesAsync(cancellationToken);
        var user = await db.Users.FirstAsync(x => x.Id == sp.UserId, cancellationToken);
        return Result<StudentDto>.Ok(new StudentDto(sp.Id, sp.UserId, user.Email, sp.XP, sp.Streak,
            sp.FirstName, sp.LastName, sp.PhoneNumber, sp.Description,
            sp.ParentPhoneNumber, sp.Level, sp.AvatarUrl, user.Status));
    }
}

/// <summary>
/// Self-edit: resolves the caller's student profile from
/// <see cref="ICurrentUserService.UserId"/>, then delegates to the same
/// domain method as the admin path. Admin-managed fields (parent phone,
/// CEFR level, XP, streak) are intentionally NOT exposed here.
/// </summary>
public sealed class UpdateMyStudentDetailsCommandHandler(
    IApplicationDbContext db,
    ICurrentUserService currentUser)
    : IRequestHandler<UpdateMyStudentDetailsCommand, Result<StudentDto>>
{
    public async Task<Result<StudentDto>> Handle(UpdateMyStudentDetailsCommand request,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is null)
            return Result<StudentDto>.Fail("UNAUTHENTICATED", "No authenticated user.");

        var sp = await db.StudentProfiles
            .FirstOrDefaultAsync(x => x.UserId == currentUser.UserId.Value, cancellationToken);
        if (sp is null) return Result<StudentDto>.Fail("NOT_FOUND", "Student profile not found for this user.");

        sp.UpdateProfile(request.FirstName, request.LastName, request.PhoneNumber, request.Description);
        await db.SaveChangesAsync(cancellationToken);
        var user = await db.Users.FirstAsync(x => x.Id == sp.UserId, cancellationToken);
        return Result<StudentDto>.Ok(new StudentDto(sp.Id, sp.UserId, user.Email, sp.XP, sp.Streak,
            sp.FirstName, sp.LastName, sp.PhoneNumber, sp.Description,
            sp.ParentPhoneNumber, sp.Level, sp.AvatarUrl, user.Status));
    }
}

public sealed class GetMyStudentProfileQueryHandler(IApplicationDbContext db, ICurrentUserService currentUser)
    : IRequestHandler<GetMyStudentProfileQuery, Result<StudentDto>>
{
    public async Task<Result<StudentDto>> Handle(GetMyStudentProfileQuery request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is null)
            return Result<StudentDto>.Fail("UNAUTHENTICATED", "No authenticated user.");

        // Prefer the JWT claim (free lookup); fall back to UserId scan if not yet enriched.
        var query = db.StudentProfiles.AsNoTracking()
            .Join(db.Users, s => s.UserId, u => u.Id, (s, u) => new { s, u });

        var match = currentUser.StudentProfileId is { } profileId
            ? await query.FirstOrDefaultAsync(x => x.s.Id == profileId, cancellationToken)
            : await query.FirstOrDefaultAsync(x => x.s.UserId == currentUser.UserId, cancellationToken);

        if (match is null)
            return Result<StudentDto>.Fail("NOT_FOUND", "No student profile is linked to this account.");

        return Result<StudentDto>.Ok(new StudentDto(
            match.s.Id, match.s.UserId, match.u.Email, match.s.XP, match.s.Streak,
            match.s.FirstName, match.s.LastName, match.s.PhoneNumber, match.s.Description,
            match.s.ParentPhoneNumber, match.s.Level, match.s.AvatarUrl, match.u.Status));
    }
}

public sealed class UpdateStudentAdminFieldsCommandHandler(IApplicationDbContext db)
    : IRequestHandler<UpdateStudentAdminFieldsCommand, Result<StudentDto>>
{
    public async Task<Result<StudentDto>> Handle(UpdateStudentAdminFieldsCommand request, CancellationToken ct)
    {
        var sp = await db.StudentProfiles.FirstOrDefaultAsync(x => x.Id == request.StudentProfileId, ct);
        if (sp is null) return Result<StudentDto>.Fail("NOT_FOUND", "Student profile not found.");
        sp.SetParentPhoneNumber(request.ParentPhoneNumber);
        sp.SetLevel(request.Level);
        await db.SaveChangesAsync(ct);
        var user = await db.Users.FirstAsync(x => x.Id == sp.UserId, ct);
        return Result<StudentDto>.Ok(new StudentDto(sp.Id, sp.UserId, user.Email, sp.XP, sp.Streak,
            sp.FirstName, sp.LastName, sp.PhoneNumber, sp.Description,
            sp.ParentPhoneNumber, sp.Level, sp.AvatarUrl, user.Status));
    }
}

public sealed class SetStudentAvatarCommandHandler(IApplicationDbContext db, IAvatarFileStore avatarStore)
    : IRequestHandler<SetStudentAvatarCommand, Result<StudentDto>>
{
    public async Task<Result<StudentDto>> Handle(SetStudentAvatarCommand request, CancellationToken ct)
    {
        var sp = await db.StudentProfiles.FirstOrDefaultAsync(x => x.Id == request.StudentProfileId, ct);
        if (sp is null) return Result<StudentDto>.Fail("NOT_FOUND", "Student profile not found.");

        var previousUrl = sp.AvatarUrl;
        sp.SetAvatarUrl(request.AvatarUrl);
        await db.SaveChangesAsync(ct);
        await AvatarCleanup.DeletePreviousAsync(avatarStore, previousUrl, sp.AvatarUrl, ct);

        var user = await db.Users.FirstAsync(x => x.Id == sp.UserId, ct);
        return Result<StudentDto>.Ok(new StudentDto(sp.Id, sp.UserId, user.Email, sp.XP, sp.Streak,
            sp.FirstName, sp.LastName, sp.PhoneNumber, sp.Description,
            sp.ParentPhoneNumber, sp.Level, sp.AvatarUrl, user.Status));
    }
}

/// <summary>
/// Admin freeze/block/restore for a student. Same pattern as
/// <see cref="LMS.Application.Features.Staff.SetStaffStatusCommandHandler"/>.
/// </summary>
public sealed class SetStudentStatusCommandHandler(IApplicationDbContext db, ICurrentUserService currentUser)
    : IRequestHandler<SetStudentStatusCommand, Result<StudentDto>>
{
    public async Task<Result<StudentDto>> Handle(SetStudentStatusCommand request, CancellationToken ct)
    {
        var sp = await db.StudentProfiles.FirstOrDefaultAsync(x => x.Id == request.StudentProfileId, ct);
        if (sp is null) return Result<StudentDto>.Fail("NOT_FOUND", "Student profile not found.");
        var user = await db.Users.FirstOrDefaultAsync(x => x.Id == sp.UserId, ct);
        if (user is null) return Result<StudentDto>.Fail("NOT_FOUND", "Linked user not found.");

        if (currentUser.UserId == user.Id && request.Status != Domain.Enums.UserStatus.Active)
            return Result<StudentDto>.Fail("VALIDATION", "Cannot change your own status.");

        user.SetStatus(request.Status);
        await db.SaveChangesAsync(ct);

        return Result<StudentDto>.Ok(new StudentDto(sp.Id, sp.UserId, user.Email, sp.XP, sp.Streak,
            sp.FirstName, sp.LastName, sp.PhoneNumber, sp.Description,
            sp.ParentPhoneNumber, sp.Level, sp.AvatarUrl, user.Status));
    }
}
