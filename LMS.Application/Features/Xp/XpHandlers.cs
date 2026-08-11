using LMS.Application.Common.Abstractions;
using LMS.Application.Common.Models;
using LMS.Application.Common.Security;
using LMS.Domain.Entities;
using LMS.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LMS.Application.Features.Xp;

public sealed class XpHandlers(IApplicationDbContext db, ICurrentUserService currentUser) :
    IRequestHandler<AddManualXpCommand, Result>,
    IRequestHandler<GetStudentXpLedgerQuery, Result<IReadOnlyCollection<XpLedgerDto>>>,
    IRequestHandler<GetLeaderboardQuery, Result<IReadOnlyCollection<LeaderboardDto>>>
{
    public async Task<Result> Handle(AddManualXpCommand request, CancellationToken cancellationToken)
    {
        var sp = await db.StudentProfiles.FirstOrDefaultAsync(x => x.Id == request.StudentProfileId, cancellationToken);
        if (sp is null) return Result.Fail("NOT_FOUND", "Student profile not found.");

        // Scope: admins/office may award anyone; a teacher may only award a student
        // enrolled in a class they teach (Class.TeacherUserId == them). Without this
        // any Xp.Grant holder could award XP to any student in the system.
        var isAdmin = currentUser.IsInRole(RoleCodes.Admin)
            || currentUser.IsInRole(RoleCodes.SuperAdmin)
            || currentUser.IsInRole(RoleCodes.OfficeAdmin);
        if (!isAdmin)
        {
            if (currentUser.UserId is not { } uid)
                return Result.Fail("UNAUTHENTICATED", "Caller must be authenticated.");
            var inMyClass = await db.Enrollments.AnyAsync(e =>
                e.StudentProfileId == sp.Id &&
                db.Classes.Any(c => c.Id == e.ClassId && c.TeacherUserId == uid),
                cancellationToken);
            if (!inMyClass)
                return Result.Fail("FORBIDDEN", "You can only award XP to students enrolled in your classes.");
        }

        sp.AddXp(request.Amount);
        await db.XpLedger.AddAsync(XpLedger.CreateEntry(sp.Id, request.Amount, XpSourceType.Manual, request.Note),
            cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Ok("XP added");
    }

    public async Task<Result<IReadOnlyCollection<LeaderboardDto>>> Handle(GetLeaderboardQuery request,
        CancellationToken cancellationToken)
    {
        // Names are resolved here (server-side) so the leaderboard needs no
        // separate students-list call the caller may lack permission for — that
        // gap is why teacher leaderboards rendered "Student #<id>" instead of
        // names. Concat + trim is done in memory to yield null (not "") for
        // profiles with no name set, so the client falls back cleanly.
        var query = db.StudentProfiles.AsQueryable();

        // Optional class scope: only students actively enrolled in that class.
        if (request.ClassId is { } classId)
        {
            query = query.Where(sp => db.Enrollments.Any(e =>
                e.ClassId == classId &&
                e.StudentProfileId == sp.Id &&
                e.Status != EnrollmentStatus.Dropped));
        }

        var rows = await query.OrderByDescending(x => x.XP)
            .Take(request.Top)
            .Select(x => new { x.Id, x.FirstName, x.LastName, x.Level, x.Streak, x.XP })
            .ToListAsync(cancellationToken);

        var dtos = rows.Select(x =>
        {
            var name = $"{x.FirstName} {x.LastName}".Trim();
            return new LeaderboardDto(x.Id, string.IsNullOrWhiteSpace(name) ? null : name, x.Level, x.Streak, x.XP);
        }).ToList();

        return Result<IReadOnlyCollection<LeaderboardDto>>.Ok(dtos);
    }

    public async Task<Result<IReadOnlyCollection<XpLedgerDto>>> Handle(GetStudentXpLedgerQuery request,
        CancellationToken cancellationToken)
    {
        // SECURITY: a student may read only their OWN XP ledger. Staff
        // (StaffProfileId present — teachers, support, office, admin) may read
        // any student's for teaching/support workflows. Without this guard any
        // Xp.Read holder could read another student's full XP history.
        if (currentUser.StaffProfileId is null &&
            (currentUser.StudentProfileId is null || currentUser.StudentProfileId != request.StudentProfileId))
        {
            return Result<IReadOnlyCollection<XpLedgerDto>>.Fail(
                "FORBIDDEN", "You can only view your own XP ledger.");
        }

        return Result<IReadOnlyCollection<XpLedgerDto>>.Ok(await db.XpLedger
            .Where(x => x.StudentProfileId == request.StudentProfileId).OrderByDescending(x => x.CreatedAt).Select(x =>
                new XpLedgerDto(x.Id, x.StudentProfileId, x.Amount, x.SourceType.ToString(), x.Note, x.CreatedAt))
            .ToListAsync(cancellationToken));
    }
}