using LMS.Application.Common.Abstractions;
using LMS.Application.Features.Classes;
using LMS.Application.Common.Models;
using LMS.Application.Common.Security;
using LMS.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LMS.Application.Features.Analytics;

/// <summary>
/// Learning-analytics computation layer. Pure aggregation over existing data
/// (attendance, submissions, task submissions, lesson progress, enrollments) —
/// no new tables. Every handler self-scopes:
///   • a student may read only their own performance
///   • a teacher only their own classes / their classes' students
///   • staff (admin / office / director) may read any
/// </summary>
public sealed class AnalyticsHandlers(IApplicationDbContext db, ICurrentUserService currentUser) :
    IRequestHandler<GetStudentPerformanceQuery, Result<StudentPerformanceDto>>,
    IRequestHandler<GetClassAnalyticsQuery, Result<ClassAnalyticsDto>>,
    IRequestHandler<BulkMarkAttendanceCommand, Result<SessionAttendanceDto>>,
    IRequestHandler<GetSessionAttendanceSummaryQuery, Result<SessionAttendanceDto>>
{
    private bool IsAdmin() => currentUser.IsAdmin();
    private bool IsTeacher() => currentUser.IsTeacher();

    private static double Pct(int numerator, int denominator) =>
        denominator == 0 ? 0 : Math.Round(100.0 * numerator / denominator, 1);

    private static string NameOf(string? first, string? last, Guid id)
    {
        var name = $"{first} {last}".Trim();
        return string.IsNullOrEmpty(name) ? $"Student #{id.ToString()[..6]}" : name;
    }

    // ===== Student performance ============================================
    public async Task<Result<StudentPerformanceDto>> Handle(GetStudentPerformanceQuery request, CancellationToken ct)
    {
        var spId = request.StudentProfileId;

        // Access: self, admin/staff, or a teacher of one of the student's classes.
        var allowed = currentUser.StudentProfileId == spId || IsAdmin();
        if (!allowed && IsTeacher() && currentUser.UserId is { } tuid)
        {
            allowed = await db.Enrollments.AsNoTracking()
                .Where(e => e.StudentProfileId == spId && e.Status != EnrollmentStatus.Dropped)
                .AnyAsync(e => db.Classes.Any(c => c.Id == e.ClassId && c.TeacherUserId == tuid), ct);
        }
        if (!allowed) return Result<StudentPerformanceDto>.Fail("FORBIDDEN", "You can't view this student's performance.");

        var classIds = await db.Enrollments.AsNoTracking()
            .Where(e => e.StudentProfileId == spId && e.Status != EnrollmentStatus.Dropped)
            .Select(e => e.ClassId).ToListAsync(ct);

        // Attendance %
        var att = await db.Attendance.AsNoTracking()
            .Where(a => a.StudentProfileId == spId).Select(a => a.Status).ToListAsync(ct);
        var attended = att.Count(s => s is AttendanceStatus.Present or AttendanceStatus.Late);

        // Assignment completion + missing + late
        var publishedIds = await db.Assignments.AsNoTracking()
            .Where(a => classIds.Contains(a.ClassId) && a.Status == AssignmentStatus.Published)
            .Select(a => a.Id).ToListAsync(ct);
        var mySubs = await db.Submissions.AsNoTracking()
            .Where(s => s.StudentProfileId == spId)
            .Select(s => new { s.AssignmentId, s.Status }).ToListAsync(ct);
        var submittedSet = mySubs.Select(s => s.AssignmentId).ToHashSet();
        var submitted = publishedIds.Count(id => submittedSet.Contains(id));
        var missing = publishedIds.Count - submitted;
        var late = mySubs.Count(s => s.Status == SubmissionStatus.Late);

        // Lesson completion
        var sessionIds = await db.ClassSessions.AsNoTracking()
            .Where(s => classIds.Contains(s.ClassId) && !s.IsBackfilled).Select(s => s.Id).ToListAsync(ct);
        var completedLessons = sessionIds.Count == 0 ? 0 : await db.LessonProgress.AsNoTracking()
            .CountAsync(p => p.StudentProfileId == spId && sessionIds.Contains(p.ClassSessionId), ct);

        // Average score across graded assignments (0–100) + quizzes (0–1 → ×100)
        var subScores = await db.Submissions.AsNoTracking()
            .Where(s => s.StudentProfileId == spId && s.Score != null)
            .Select(s => s.Score!.Value).ToListAsync(ct);
        var taskScores = await db.TaskSubmissions.AsNoTracking()
            .Where(t => t.StudentProfileId == spId && t.Score != null)
            .Select(t => t.Score!.Value).ToListAsync(ct);
        var allScores = subScores.Select(x => (double)x).Concat(taskScores.Select(x => (double)x * 100)).ToList();
        double? avg = allScores.Count == 0 ? null : Math.Round(allScores.Average(), 1);

        return Result<StudentPerformanceDto>.Ok(new StudentPerformanceDto(
            spId,
            Pct(attended, att.Count),
            Pct(submitted, publishedIds.Count),
            Pct(completedLessons, sessionIds.Count),
            avg, missing, late,
            att.Count, attended, publishedIds.Count, submitted, sessionIds.Count, completedLessons));
    }

    // ===== Class analytics ================================================
    public async Task<Result<ClassAnalyticsDto>> Handle(GetClassAnalyticsQuery request, CancellationToken ct)
    {
        var cls = await db.Classes.AsNoTracking()
            .Where(c => c.Id == request.ClassId)
            .Select(c => new { c.Id, c.Title, c.TeacherUserId }).FirstOrDefaultAsync(ct);
        if (cls is null) return Result<ClassAnalyticsDto>.Fail("NOT_FOUND", "Class not found.");

        var allowed = IsAdmin() || (IsTeacher() && currentUser.UserId is { } auid && await db.IsClassTeacherAsync(cls.Id, auid, ct));
        if (!allowed) return Result<ClassAnalyticsDto>.Fail("FORBIDDEN", "You can't view this class's analytics.");

        var students = await db.Enrollments.AsNoTracking()
            .Where(e => e.ClassId == request.ClassId && e.Status == EnrollmentStatus.Active)
            .Join(db.StudentProfiles, e => e.StudentProfileId, sp => sp.Id,
                (e, sp) => new { sp.Id, sp.FirstName, sp.LastName })
            .ToListAsync(ct);
        var n = students.Count;
        var enrolledIds = students.Select(s => s.Id).ToHashSet();

        var attRows = await db.Attendance.AsNoTracking()
            .Where(a => a.ClassId == request.ClassId)
            .Select(a => new { a.StudentProfileId, a.Status }).ToListAsync(ct);

        var publishedIds = await db.Assignments.AsNoTracking()
            .Where(a => a.ClassId == request.ClassId && a.Status == AssignmentStatus.Published)
            .Select(a => a.Id).ToListAsync(ct);
        var p = publishedIds.Count;
        var subs = await db.Submissions.AsNoTracking()
            .Where(s => publishedIds.Contains(s.AssignmentId))
            .Select(s => new { s.StudentProfileId, s.AssignmentId, s.Score }).ToListAsync(ct);

        var sessionIds = await db.ClassSessions.AsNoTracking()
            .Where(s => s.ClassId == request.ClassId && !s.IsBackfilled).Select(s => s.Id).ToListAsync(ct);
        var s = sessionIds.Count;
        var lpCount = sessionIds.Count == 0 ? 0 : await db.LessonProgress.AsNoTracking()
            .CountAsync(lp => sessionIds.Contains(lp.ClassSessionId) && enrolledIds.Contains(lp.StudentProfileId), ct);

        // Class-level rates
        var attendedAll = attRows.Count(a => a.Status is AttendanceStatus.Present or AttendanceStatus.Late);
        var gradedScores = subs.Where(x => x.Score != null).Select(x => (double)x.Score!.Value).ToList();
        double? avgGrade = gradedScores.Count == 0 ? null : Math.Round(gradedScores.Average(), 1);
        var distinctPairs = subs.Where(x => enrolledIds.Contains(x.StudentProfileId))
            .Select(x => (x.StudentProfileId, x.AssignmentId)).Distinct().Count();
        var assignmentCompletionRate = (p == 0 || n == 0) ? 0 : Math.Min(100, Math.Round(100.0 * distinctPairs / (p * n), 1));
        var lessonCompletionRate = (s == 0 || n == 0) ? 0 : Math.Min(100, Math.Round(100.0 * lpCount / (s * n), 1));

        // Per-student at-risk
        var attByStudent = attRows.GroupBy(a => a.StudentProfileId).ToDictionary(g => g.Key, g => g.ToList());
        var subsByStudent = subs.GroupBy(x => x.StudentProfileId).ToDictionary(g => g.Key, g => g.ToList());
        var atRisk = new List<AtRiskStudentDto>();
        foreach (var stu in students)
        {
            var sa = attByStudent.GetValueOrDefault(stu.Id) ?? new();
            double? attPct = sa.Count == 0 ? null
                : Math.Round(100.0 * sa.Count(a => a.Status is AttendanceStatus.Present or AttendanceStatus.Late) / sa.Count, 1);

            var ss = subsByStudent.GetValueOrDefault(stu.Id) ?? new();
            var submittedDistinct = ss.Select(x => x.AssignmentId).Where(publishedIds.Contains).Distinct().Count();
            double? complPct = p == 0 ? null : Math.Round(100.0 * submittedDistinct / p, 1);
            var sgraded = ss.Where(x => x.Score != null).Select(x => (double)x.Score!.Value).ToList();
            double? sAvg = sgraded.Count == 0 ? null : Math.Round(sgraded.Average(), 1);

            var reasons = new List<string>();
            if (attPct is { } a1 && a1 < 70) reasons.Add("Low attendance");
            if (complPct is { } c1 && c1 < 50) reasons.Add("Low assignment completion");
            if (sAvg is { } g1 && g1 < 60) reasons.Add("Low average grade");
            if (reasons.Count > 0)
                atRisk.Add(new AtRiskStudentDto(stu.Id, NameOf(stu.FirstName, stu.LastName, stu.Id),
                    attPct ?? 0, complPct ?? 0, sAvg, reasons));
        }

        return Result<ClassAnalyticsDto>.Ok(new ClassAnalyticsDto(
            cls.Id, cls.Title, n,
            Pct(attendedAll, attRows.Count), avgGrade,
            assignmentCompletionRate, lessonCompletionRate, p, s, atRisk));
    }

    // ===== Attendance: bulk + summary =====================================
    private async Task<(Guid ClassId, Guid? TeacherUserId)?> ResolveSessionClassAsync(Guid sessionId, CancellationToken ct)
    {
        var row = await db.ClassSessions.AsNoTracking()
            .Where(x => x.Id == sessionId)
            .Join(db.Classes, x => x.ClassId, c => c.Id, (x, c) => new { c.Id, c.TeacherUserId })
            .FirstOrDefaultAsync(ct);
        return row is null ? null : (row.Id, row.TeacherUserId);
    }

    private async Task<bool> CanManageClassAsync(Guid classId, CancellationToken ct) =>
        IsAdmin() || (IsTeacher() && currentUser.UserId is { } uid && await db.IsClassTeacherAsync(classId, uid, ct));

    public async Task<Result<SessionAttendanceDto>> Handle(BulkMarkAttendanceCommand request, CancellationToken ct)
    {
        var info = await ResolveSessionClassAsync(request.SessionId, ct);
        if (info is null) return Result<SessionAttendanceDto>.Fail("NOT_FOUND", "Session not found.");
        if (!await CanManageClassAsync(info.Value.ClassId, ct))
            return Result<SessionAttendanceDto>.Fail("FORBIDDEN", "Only the class teacher can mark attendance.");

        var existing = await db.Attendance
            .Where(a => a.SessionId == request.SessionId).ToListAsync(ct);
        var byStudent = existing.ToDictionary(a => a.StudentProfileId);

        foreach (var mark in request.Marks)
        {
            if (byStudent.TryGetValue(mark.StudentProfileId, out var row))
            {
                row.Mark(mark.Status);
            }
            else
            {
                var created = Domain.Entities.Attendance.Create(
                    info.Value.ClassId, request.SessionId, mark.StudentProfileId, existing);
                created.Mark(mark.Status);
                await db.Attendance.AddAsync(created, ct);
                existing.Add(created);
                byStudent[mark.StudentProfileId] = created;
            }
        }
        await db.SaveChangesAsync(ct);
        return Result<SessionAttendanceDto>.Ok(await BuildSummaryAsync(request.SessionId, info.Value.ClassId, ct),
            "Attendance saved.");
    }

    public async Task<Result<SessionAttendanceDto>> Handle(GetSessionAttendanceSummaryQuery request, CancellationToken ct)
    {
        var info = await ResolveSessionClassAsync(request.SessionId, ct);
        if (info is null) return Result<SessionAttendanceDto>.Fail("NOT_FOUND", "Session not found.");
        if (!await CanManageClassAsync(info.Value.ClassId, ct))
            return Result<SessionAttendanceDto>.Fail("FORBIDDEN", "Only the class teacher can view attendance.");

        return Result<SessionAttendanceDto>.Ok(await BuildSummaryAsync(request.SessionId, info.Value.ClassId, ct));
    }

    private async Task<SessionAttendanceDto> BuildSummaryAsync(Guid sessionId, Guid classId, CancellationToken ct)
    {
        var students = await db.Enrollments.AsNoTracking()
            .Where(e => e.ClassId == classId && e.Status == EnrollmentStatus.Active)
            .Join(db.StudentProfiles, e => e.StudentProfileId, sp => sp.Id,
                (e, sp) => new { sp.Id, sp.FirstName, sp.LastName })
            .ToListAsync(ct);
        var marks = await db.Attendance.AsNoTracking()
            .Where(a => a.SessionId == sessionId)
            .Select(a => new { a.StudentProfileId, a.Status }).ToListAsync(ct);
        var byStudent = marks.ToDictionary(m => m.StudentProfileId, m => m.Status);

        var rows = students
            .Select(stu => new SessionAttendanceRowDto(
                stu.Id, NameOf(stu.FirstName, stu.LastName, stu.Id),
                byStudent.TryGetValue(stu.Id, out var st) ? st : (AttendanceStatus?)null))
            .OrderBy(r => r.Name)
            .ToList();

        int Count(AttendanceStatus s) => rows.Count(r => r.Status == s);
        return new SessionAttendanceDto(
            sessionId, classId,
            Count(AttendanceStatus.Present), Count(AttendanceStatus.Absent),
            Count(AttendanceStatus.Late), Count(AttendanceStatus.Excused),
            rows.Count(r => r.Status is null), rows.Count, rows);
    }
}
