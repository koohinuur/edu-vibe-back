using LMS.Application.Common.Abstractions;
using LMS.Application.Common.Models;
using LMS.Application.Common.Security;
using LMS.Application.Features.Notifications;
using LMS.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LMS.Application.Features.Attendance;

public sealed class AttendanceHandlers(
    IApplicationDbContext db, ICurrentUserService currentUser, INotificationService notifications) :
    IRequestHandler<MarkAttendanceCommand, Result<AttendanceDto>>,
    IRequestHandler<UpdateAttendanceCommand, Result<AttendanceDto>>,
    IRequestHandler<GetSessionAttendanceQuery, Result<IReadOnlyCollection<AttendanceDto>>>,
    IRequestHandler<GetStudentAttendanceQuery, Result<IReadOnlyCollection<AttendanceDto>>>,
    IRequestHandler<GetMyAttendanceQuery, Result<IReadOnlyCollection<MyAttendanceDto>>>,
    IRequestHandler<GetAttendanceQuery, Result<IReadOnlyCollection<AttendanceDto>>>
{
    // Staff may read ANY student's attendance. Students hold Attendance.Read
    // too (for their own history), so the permission gate alone can't tell
    // "my attendance" from "someone else's".
    private bool CallerIsStaff() => currentUser.IsStaff();

    /// <summary>Resolves the caller's own student profile id from the JWT claim, falling back to a UserId lookup.</summary>
    private async Task<Guid?> ResolveOwnProfileIdAsync(CancellationToken ct)
    {
        var id = currentUser.StudentProfileId;
        if (id is null && currentUser.UserId is { } uid)
            id = await db.StudentProfiles.Where(sp => sp.UserId == uid)
                .Select(sp => (Guid?)sp.Id).FirstOrDefaultAsync(ct);
        return id;
    }

    public async Task<Result<IReadOnlyCollection<AttendanceDto>>> Handle(GetSessionAttendanceQuery request,
        CancellationToken cancellationToken)
    {
        // The whole session roster is staff-only — students hold Attendance.Read
        // for their OWN history, not to enumerate a session's attendees.
        if (!CallerIsStaff())
            return Result<IReadOnlyCollection<AttendanceDto>>.Fail(
                "FORBIDDEN", "Only staff can read a session's attendance roster.");

        return Result<IReadOnlyCollection<AttendanceDto>>.Ok(await db.Attendance
            .Where(x => x.SessionId == request.SessionId)
            .Select(a => new AttendanceDto(a.Id, a.ClassId, a.SessionId, a.StudentProfileId, a.Status))
            .ToListAsync(cancellationToken));
    }

    public async Task<Result<IReadOnlyCollection<AttendanceDto>>> Handle(GetStudentAttendanceQuery request,
        CancellationToken cancellationToken)
    {
        // Self-scope: a non-staff caller (i.e. a student) may only read their
        // OWN attendance. Resolve their profile from the JWT claim, falling
        // back to a UserId → StudentProfiles lookup if the claim is absent.
        if (!CallerIsStaff())
        {
            var ownProfileId = currentUser.StudentProfileId;
            if (ownProfileId is null && currentUser.UserId is { } uid)
            {
                ownProfileId = await db.StudentProfiles
                    .Where(sp => sp.UserId == uid)
                    .Select(sp => (Guid?)sp.Id)
                    .FirstOrDefaultAsync(cancellationToken);
            }
            if (ownProfileId is null || ownProfileId.Value != request.StudentProfileId)
                return Result<IReadOnlyCollection<AttendanceDto>>.Fail(
                    "FORBIDDEN", "You can only view your own attendance.");
        }

        return Result<IReadOnlyCollection<AttendanceDto>>.Ok(await db.Attendance
            .Where(x => x.StudentProfileId == request.StudentProfileId)
            .Select(a => new AttendanceDto(a.Id, a.ClassId, a.SessionId, a.StudentProfileId, a.Status))
            .ToListAsync(cancellationToken));
    }

    public async Task<Result<IReadOnlyCollection<MyAttendanceDto>>> Handle(GetMyAttendanceQuery request,
        CancellationToken cancellationToken)
    {
        // Self-scoped: resolve the caller's own student profile from the JWT.
        var profileId = currentUser.StudentProfileId;
        if (profileId is null && currentUser.UserId is { } uid)
        {
            profileId = await db.StudentProfiles
                .Where(sp => sp.UserId == uid)
                .Select(sp => (Guid?)sp.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }
        if (profileId is null)
            return Result<IReadOnlyCollection<MyAttendanceDto>>.Ok(Array.Empty<MyAttendanceDto>());

        // Join attendance → session (date/time) → class (title). Most-recent
        // first so the panel leads with the latest marks.
        var rows = await (
            from a in db.Attendance.AsNoTracking()
            where a.StudentProfileId == profileId.Value
            join s in db.ClassSessions.AsNoTracking() on a.SessionId equals s.Id
            join c in db.Classes.AsNoTracking() on a.ClassId equals c.Id
            orderby s.SessionDate descending, s.StartsAt descending
            select new MyAttendanceDto(
                a.Id, a.ClassId, c.Title, a.SessionId, s.SessionDate, s.StartsAt, a.Status))
            .ToListAsync(cancellationToken);

        return Result<IReadOnlyCollection<MyAttendanceDto>>.Ok(rows);
    }

    public async Task<Result<AttendanceDto>> Handle(MarkAttendanceCommand request, CancellationToken cancellationToken)
    {
        var existing = await db.Attendance.FirstOrDefaultAsync(
            x => x.SessionId == request.SessionId && x.StudentProfileId == request.StudentProfileId, cancellationToken);
        // Prior status (null = brand-new mark) — so we only DM on an actual change.
        AttendanceStatus? prev = existing?.Status;
        if (existing is null)
        {
            // Uniqueness was already established by the targeted lookup above, so
            // skip loading the whole session's roster just to re-check it in memory.
            existing = Domain.Entities.Attendance.Create(request.ClassId, request.SessionId, request.StudentProfileId,
                Array.Empty<Domain.Entities.Attendance>());
            await db.Attendance.AddAsync(existing, cancellationToken);
        }

        existing.Mark(request.Status);
        await db.SaveChangesAsync(cancellationToken);
        if (request.Status != prev)
            await NotifyIfAbsentOrLateAsync(existing.StudentProfileId, existing.ClassId, existing.SessionId, request.Status, cancellationToken);
        return Result<AttendanceDto>.Ok(Map(existing));
    }

    public async Task<Result<AttendanceDto>> Handle(UpdateAttendanceCommand request,
        CancellationToken cancellationToken)
    {
        var a = await db.Attendance.FirstOrDefaultAsync(x => x.Id == request.AttendanceId, cancellationToken);
        if (a is null) return Result<AttendanceDto>.Fail("NOT_FOUND", "Attendance not found.");
        var prev = a.Status;
        a.Mark(request.Status);
        await db.SaveChangesAsync(cancellationToken);
        if (request.Status != prev)
            await NotifyIfAbsentOrLateAsync(a.StudentProfileId, a.ClassId, a.SessionId, request.Status, cancellationToken);
        return Result<AttendanceDto>.Ok(Map(a));
    }

    /// <summary>
    /// DMs the student (via the platform bot) when they're marked Absent or Late,
    /// with the lesson date + class. No-op for Present/Excused or an unlinked student.
    /// Fire-and-forget — the notifier swallows failures.
    /// </summary>
    private async Task NotifyIfAbsentOrLateAsync(
        Guid studentProfileId, Guid classId, Guid sessionId, AttendanceStatus status, CancellationToken ct)
    {
        if (status is not (AttendanceStatus.Absent or AttendanceStatus.Late)) return;

        var studentUserId = await db.StudentProfiles
            .Where(sp => sp.Id == studentProfileId).Select(sp => sp.UserId).FirstOrDefaultAsync(ct);
        if (studentUserId == Guid.Empty) return;

        var date = await db.ClassSessions.Where(s => s.Id == sessionId)
            .Select(s => s.SessionDate).FirstOrDefaultAsync(ct);
        var className = await db.Classes.Where(c => c.Id == classId)
            .Select(c => c.Title).FirstOrDefaultAsync(ct);

        await notifications.NotifyUserAsync(
            studentUserId,
            lang => EventNotificationTexts.AttendanceMarked(lang, status == AttendanceStatus.Late, date, className),
            ct);
    }

    public async Task<Result<IReadOnlyCollection<AttendanceDto>>> Handle(GetAttendanceQuery request,
        CancellationToken cancellationToken)
    {
        var q = db.Attendance.AsQueryable();

        // Self-scope: a non-staff caller (student) only ever sees their OWN
        // attendance, regardless of the studentProfileId filter they pass.
        if (!CallerIsStaff())
        {
            var ownProfileId = await ResolveOwnProfileIdAsync(cancellationToken);
            if (ownProfileId is null)
                return Result<IReadOnlyCollection<AttendanceDto>>.Ok(Array.Empty<AttendanceDto>());
            q = q.Where(x => x.StudentProfileId == ownProfileId.Value);
        }
        else if (request.StudentProfileId is { } studentId)
        {
            q = q.Where(x => x.StudentProfileId == studentId);
        }

        if (request.ClassId is { } classId) q = q.Where(x => x.ClassId == classId);
        if (request.SessionId is { } sessionId) q = q.Where(x => x.SessionId == sessionId);
        if (request.Status is { } status) q = q.Where(x => x.Status == status);

        var data = await q
            .OrderByDescending(x => x.CreatedAt)
            .Select(a => new AttendanceDto(a.Id, a.ClassId, a.SessionId, a.StudentProfileId, a.Status))
            .ToListAsync(cancellationToken);
        return Result<IReadOnlyCollection<AttendanceDto>>.Ok(data);
    }

    private static AttendanceDto Map(Domain.Entities.Attendance a)
    {
        return new AttendanceDto(a.Id, a.ClassId, a.SessionId, a.StudentProfileId, a.Status);
    }
}