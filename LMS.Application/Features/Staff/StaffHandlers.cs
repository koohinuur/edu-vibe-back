using LMS.Application.Common.Abstractions;
using LMS.Application.Common.Models;
using LMS.Application.Common.Storage;
using LMS.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;
using static LMS.Application.Features.Staff.StaffMapper;

namespace LMS.Application.Features.Staff;

public sealed class GetStaffQueryHandler(IApplicationDbContext db)
    : IRequestHandler<GetStaffQuery, Result<PagedResult<StaffDto>>>
{
    public async Task<Result<PagedResult<StaffDto>>> Handle(GetStaffQuery request,
        CancellationToken cancellationToken)
    {
        var page = new PageRequest(request.Page, request.PageSize, request.Search);

        var query =
            from s in db.StaffProfiles.AsNoTracking()
            join u in db.Users.AsNoTracking() on s.UserId equals u.Id
            select new { s, u };

        if (page.NormalizedSearch is { } search)
        {
            query = query.Where(x =>
                x.u.Email.ToLower().Contains(search) ||
                (x.s.FirstName != null && x.s.FirstName.ToLower().Contains(search)) ||
                (x.s.LastName != null && x.s.LastName.ToLower().Contains(search)) ||
                (x.s.PhoneNumber != null && x.s.PhoneNumber.Contains(search)) ||
                (x.s.Position != null && x.s.Position.ToLower().Contains(search)));
        }

        var total = await query.CountAsync(cancellationToken);
        // Status-first ordering (IsActive DESC): Active staff first, then
        // Inactive/Blocked, then full name (First, Last) ascending with email as
        // the final tiebreaker. Ordered in SQL before Skip/Take.
        var items = await query
            .OrderByDescending(x => x.u.Status == Domain.Enums.UserStatus.Active)
            .ThenBy(x => x.s.FirstName)
            .ThenBy(x => x.s.LastName)
            .ThenBy(x => x.u.Email)
            .Skip(page.Skip)
            .Take(page.NormalizedPageSize)
            .Select(x => new StaffDto(
                x.s.Id, x.s.UserId, x.u.Email, x.s.EmploymentType,
                x.s.FirstName, x.s.LastName, x.s.PhoneNumber, x.s.Description, x.s.AvatarUrl,
                x.u.Status, x.s.Position, x.s.IsPubliclyVisible, x.s.Certifications, x.s.YearsExperience,
                x.s.DisplayOrder))
            .ToListAsync(cancellationToken);

        return Result<PagedResult<StaffDto>>.Ok(PagedResult<StaffDto>.From(items, total, page));
    }
}

public sealed class CreateStaffCommandHandler(IApplicationDbContext db)
    : IRequestHandler<CreateStaffCommand, Result<StaffDto>>
{
    public async Task<Result<StaffDto>> Handle(CreateStaffCommand request, CancellationToken cancellationToken)
    {
        var user = await db.Users.FirstOrDefaultAsync(x => x.Id == request.UserId, cancellationToken);
        if (user is null) return Result<StaffDto>.Fail("NOT_FOUND", "User not found.");
        if (await db.StaffProfiles.AnyAsync(x => x.UserId == request.UserId, cancellationToken))
            return Result<StaffDto>.Fail("EXISTS", "Staff profile already exists.");

        var sp = new StaffProfile(request.UserId, request.EmploymentType);
        await db.StaffProfiles.AddAsync(sp, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Result<StaffDto>.Ok(Map(sp, user.Email, user.Status));
    }
}

public sealed class UpdateStaffProfileCommandHandler(IApplicationDbContext db)
    : IRequestHandler<UpdateStaffProfileCommand, Result<StaffDto>>
{
    public async Task<Result<StaffDto>> Handle(UpdateStaffProfileCommand request, CancellationToken cancellationToken)
    {
        var sp = await db.StaffProfiles.FirstOrDefaultAsync(x => x.Id == request.StaffProfileId, cancellationToken);
        if (sp is null) return Result<StaffDto>.Fail("NOT_FOUND", "Staff profile not found.");
        sp.SetEmploymentType(request.EmploymentType);
        await db.SaveChangesAsync(cancellationToken);
        var user = await db.Users.FirstAsync(x => x.Id == sp.UserId, cancellationToken);
        return Result<StaffDto>.Ok(Map(sp, user.Email, user.Status));
    }
}

public sealed class UpdateStaffDetailsCommandHandler(IApplicationDbContext db)
    : IRequestHandler<UpdateStaffDetailsCommand, Result<StaffDto>>
{
    public async Task<Result<StaffDto>> Handle(UpdateStaffDetailsCommand request,
        CancellationToken cancellationToken)
    {
        var sp = await db.StaffProfiles.FirstOrDefaultAsync(x => x.Id == request.StaffProfileId, cancellationToken);
        if (sp is null) return Result<StaffDto>.Fail("NOT_FOUND", "Staff profile not found.");
        sp.UpdateProfile(request.FirstName, request.LastName, request.PhoneNumber, request.Description, request.Position);
        sp.SetCredentials(request.Certifications, request.YearsExperience);
        await db.SaveChangesAsync(cancellationToken);
        var user = await db.Users.FirstAsync(x => x.Id == sp.UserId, cancellationToken);
        return Result<StaffDto>.Ok(Map(sp, user.Email, user.Status));
    }
}

/// <summary>
/// Self-edit: looks up the caller's staff profile from
/// <see cref="ICurrentUserService.UserId"/>, then delegates to the same
/// domain method as the admin path. Position is intentionally NOT exposed
/// here — only an admin sets a teacher's job title.
/// </summary>
public sealed class UpdateMyStaffDetailsCommandHandler(
    IApplicationDbContext db,
    ICurrentUserService currentUser)
    : IRequestHandler<UpdateMyStaffDetailsCommand, Result<StaffDto>>
{
    public async Task<Result<StaffDto>> Handle(UpdateMyStaffDetailsCommand request,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is null)
            return Result<StaffDto>.Fail("UNAUTHENTICATED", "No authenticated user.");

        var sp = await db.StaffProfiles
            .FirstOrDefaultAsync(x => x.UserId == currentUser.UserId.Value, cancellationToken);
        if (sp is null) return Result<StaffDto>.Fail("NOT_FOUND", "Staff profile not found for this user.");

        // Preserve existing position — admins set it via the full
        // UpdateStaffDetailsCommand; self-edit must not change it.
        sp.UpdateProfile(request.FirstName, request.LastName, request.PhoneNumber, request.Description, sp.Position);
        await db.SaveChangesAsync(cancellationToken);
        var user = await db.Users.FirstAsync(x => x.Id == sp.UserId, cancellationToken);
        return Result<StaffDto>.Ok(Map(sp, user.Email, user.Status));
    }
}

public sealed class GetMyStaffProfileQueryHandler(IApplicationDbContext db, ICurrentUserService currentUser)
    : IRequestHandler<GetMyStaffProfileQuery, Result<StaffDto>>
{
    public async Task<Result<StaffDto>> Handle(GetMyStaffProfileQuery request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is null)
            return Result<StaffDto>.Fail("UNAUTHENTICATED", "No authenticated user.");

        var query = db.StaffProfiles.AsNoTracking()
            .Join(db.Users, s => s.UserId, u => u.Id,
                (s, u) => new { s, u });

        var match = currentUser.StaffProfileId is { } profileId
            ? await query.FirstOrDefaultAsync(x => x.s.Id == profileId, cancellationToken)
            : await query.FirstOrDefaultAsync(x => x.s.UserId == currentUser.UserId, cancellationToken);

        if (match is null)
            return Result<StaffDto>.Fail("NOT_FOUND", "No staff profile is linked to this account.");

        return Result<StaffDto>.Ok(Map(match.s, match.u.Email, match.u.Status));
    }
}

public sealed class SetStaffAvatarCommandHandler(IApplicationDbContext db, IAvatarFileStore avatarStore)
    : IRequestHandler<SetStaffAvatarCommand, Result<StaffDto>>
{
    public async Task<Result<StaffDto>> Handle(SetStaffAvatarCommand request, CancellationToken ct)
    {
        var sp = await db.StaffProfiles.FirstOrDefaultAsync(x => x.Id == request.StaffProfileId, ct);
        if (sp is null) return Result<StaffDto>.Fail("NOT_FOUND", "Staff profile not found.");

        var previousUrl = sp.AvatarUrl;
        sp.SetAvatarUrl(request.AvatarUrl);
        await db.SaveChangesAsync(ct);

        // Best-effort cleanup of the replaced/removed file AFTER the new value is
        // committed, so a stored avatar is never orphaned on the disk. Only our own
        // locally-stored files (/uploads/avatars/…) are touched; external URLs
        // (e.g. a Telegram photo) resolve to a non-existent local name and no-op.
        await AvatarCleanup.DeletePreviousAsync(avatarStore, previousUrl, sp.AvatarUrl, ct);

        var user = await db.Users.FirstAsync(x => x.Id == sp.UserId, ct);
        return Result<StaffDto>.Ok(Map(sp, user.Email, user.Status));
    }
}

public sealed class SetStaffStatusCommandHandler(IApplicationDbContext db, ICurrentUserService currentUser)
    : IRequestHandler<SetStaffStatusCommand, Result<StaffDto>>
{
    public async Task<Result<StaffDto>> Handle(SetStaffStatusCommand request, CancellationToken ct)
    {
        var sp = await db.StaffProfiles.FirstOrDefaultAsync(x => x.Id == request.StaffProfileId, ct);
        if (sp is null) return Result<StaffDto>.Fail("NOT_FOUND", "Staff profile not found.");

        var user = await db.Users.FirstOrDefaultAsync(x => x.Id == sp.UserId, ct);
        if (user is null) return Result<StaffDto>.Fail("NOT_FOUND", "Linked user not found.");

        if (currentUser.UserId == user.Id && request.Status != Domain.Enums.UserStatus.Active)
            return Result<StaffDto>.Fail("VALIDATION", "Cannot change your own status.");

        user.SetStatus(request.Status);
        await db.SaveChangesAsync(ct);

        return Result<StaffDto>.Ok(Map(sp, user.Email, user.Status));
    }
}

public sealed class SetStaffPublicVisibilityCommandHandler(IApplicationDbContext db)
    : IRequestHandler<SetStaffPublicVisibilityCommand, Result<StaffDto>>
{
    public async Task<Result<StaffDto>> Handle(SetStaffPublicVisibilityCommand request, CancellationToken ct)
    {
        var sp = await db.StaffProfiles.FirstOrDefaultAsync(x => x.Id == request.StaffProfileId, ct);
        if (sp is null) return Result<StaffDto>.Fail("NOT_FOUND", "Staff profile not found.");
        sp.SetPubliclyVisible(request.IsPubliclyVisible);
        await db.SaveChangesAsync(ct);
        var user = await db.Users.FirstAsync(x => x.Id == sp.UserId, ct);
        return Result<StaffDto>.Ok(Map(sp, user.Email, user.Status));
    }
}

public sealed class ReorderPublicTeachersCommandHandler(IApplicationDbContext db)
    : IRequestHandler<ReorderPublicTeachersCommand, Result<int>>
{
    public async Task<Result<int>> Handle(ReorderPublicTeachersCommand request, CancellationToken ct)
    {
        var ids = request.OrderedStaffProfileIds;
        if (ids is null || ids.Count == 0) return Result<int>.Ok(0);

        // Load only the referenced rows, then assign DisplayOrder = position.
        var idSet = ids.ToHashSet();
        var profiles = await db.StaffProfiles
            .Where(s => idSet.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, ct);

        var updated = 0;
        for (var i = 0; i < ids.Count; i++)
        {
            if (profiles.TryGetValue(ids[i], out var sp))
            {
                sp.SetDisplayOrder(i);
                updated++;
            }
        }

        if (updated > 0) await db.SaveChangesAsync(ct);
        return Result<int>.Ok(updated);
    }
}

internal static class StaffMapper
{
    public static StaffDto Map(StaffProfile sp, string email, Domain.Enums.UserStatus status) => new(
        sp.Id, sp.UserId, email, sp.EmploymentType,
        sp.FirstName, sp.LastName, sp.PhoneNumber, sp.Description, sp.AvatarUrl,
        status, sp.Position, sp.IsPubliclyVisible, sp.Certifications, sp.YearsExperience,
        sp.DisplayOrder);
}

public sealed class GetPublicTeachersQueryHandler(IApplicationDbContext db)
    : IRequestHandler<GetPublicTeachersQuery, Result<IReadOnlyCollection<PublicTeacherDto>>>
{
    public async Task<Result<IReadOnlyCollection<PublicTeacherDto>>> Handle(
        GetPublicTeachersQuery request, CancellationToken ct)
    {
        var take = Math.Clamp(request.Take, 1, 100);
        // Only IsPubliclyVisible + currently Active accounts surface — admins
        // freezing an account should hide them from the marketing site too.
        var rows = await db.StaffProfiles.AsNoTracking()
            .Where(s => s.IsPubliclyVisible)
            .Join(db.Users.AsNoTracking(),
                  s => s.UserId, u => u.Id,
                  (s, u) => new { s, u })
            .Where(x => x.u.Status == Domain.Enums.UserStatus.Active)
            // Admin-curated order first (lower = earlier), then name as the tiebreaker.
            .OrderBy(x => x.s.DisplayOrder).ThenBy(x => x.s.LastName).ThenBy(x => x.s.FirstName)
            .Take(take)
            .Select(x => new
            {
                x.s.Id,
                x.s.FirstName,
                x.s.LastName,
                x.s.Position,
                x.s.Description,
                x.s.AvatarUrl,
                x.s.Certifications,
                x.s.YearsExperience,
                Specs = x.s.Specializations
                    .Where(ss => ss.Specialization != null && ss.Specialization.IsActive)
                    .Select(ss => ss.Specialization!.Name).ToList(),
            })
            .ToListAsync(ct);

        var items = rows.Select(r =>
        {
            var name = string.Join(" ", new[] { r.FirstName, r.LastName }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
            if (string.IsNullOrWhiteSpace(name)) name = "Staff member";
            return new PublicTeacherDto(r.Id, name, r.Position, r.Description, r.AvatarUrl,
                (IReadOnlyCollection<string>)r.Specs, r.Certifications, r.YearsExperience);
        }).ToList();

        return Result<IReadOnlyCollection<PublicTeacherDto>>.Ok(items);
    }
}
