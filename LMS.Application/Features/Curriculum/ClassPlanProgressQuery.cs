using LMS.Application.Common.Abstractions;
using LMS.Application.Features.Classes;
using LMS.Application.Common.Models;
using LMS.Application.Common.Security;
using LMS.Application.Features.Curriculum.Planning;
using LMS.Domain.Entities;
using LMS.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LMS.Application.Features.Curriculum;

// ---- Student day-plan progress (the 24 day-cards with completion) -------------

/// <summary>A plan-day for a specific class — its lessons + whether it's done + when.</summary>
public sealed record ClassPlanDayDto(
    Guid Id, int Order, string? Title, bool Completed, DateOnly? ScheduledDate,
    IReadOnlyList<TemplatePlanDayLessonDto> Lessons);

/// <summary>A class's progress through its template's day-plan (Day 1 = 1A+1B …).</summary>
public sealed record ClassPlanProgressDto(
    Guid ClassId, string ClassTitle, Guid? TemplateId, string? TemplateName,
    int CompletedDays, int TotalDays, int ProgressPct, IReadOnlyList<ClassPlanDayDto> Days);

public sealed record GetClassPlanProgressQuery(Guid ClassId) : IRequest<Result<ClassPlanProgressDto>>;

/// <summary>
/// The class's journey rendered as PLAN DAYS rather than 74 individual lessons:
/// each day covers its paired lessons (1A+1B), a day is complete only when all of its
/// lessons are done, and 100% is reachable (24/24 days). Completion + scheduled date
/// come from the session↔lesson join (class_session_lessons). Readable by the enrolled
/// student, the class teacher, or an admin.
/// </summary>
public sealed class GetClassPlanProgressHandler(IApplicationDbContext db, ICurrentUserService currentUser)
    : IRequestHandler<GetClassPlanProgressQuery, Result<ClassPlanProgressDto>>
{
    private bool IsAdmin => currentUser.IsAdmin();

    public async Task<Result<ClassPlanProgressDto>> Handle(GetClassPlanProgressQuery request, CancellationToken ct)
    {
        var cls = await db.Classes.AsNoTracking().FirstOrDefaultAsync(c => c.Id == request.ClassId, ct);
        if (cls is null) return Result<ClassPlanProgressDto>.Fail("NOT_FOUND", "Class not found.");
        if (!await CanViewAsync(cls, ct))
            return Result<ClassPlanProgressDto>.Fail("FORBIDDEN", "You don't have access to this class.");

        string? templateName = null;
        if (cls.CurriculumTemplateId is { } tid)
            templateName = await db.CurriculumTemplates.AsNoTracking()
                .Where(t => t.Id == tid).Select(t => t.Name).FirstOrDefaultAsync(ct);

        // No curriculum, or a template with no plan loaded → an empty (but valid) journey.
        if (cls.CurriculumTemplateId is not { } templateId)
            return Result<ClassPlanProgressDto>.Ok(new ClassPlanProgressDto(
                cls.Id, cls.Title, null, null, 0, 0, 0, Array.Empty<ClassPlanDayDto>()));

        var days = await db.CurriculumPlanDays.AsNoTracking()
            .Where(d => d.TemplateId == templateId).OrderBy(d => d.Order)
            .Select(d => new { d.Id, d.Order, d.Title }).ToListAsync(ct);
        if (days.Count == 0)
            return Result<ClassPlanProgressDto>.Ok(new ClassPlanProgressDto(
                cls.Id, cls.Title, templateId, templateName, 0, 0, 0, Array.Empty<ClassPlanDayDto>()));

        var dayIds = days.Select(d => d.Id).ToList();
        var dayLessons = await (
            from pl in db.CurriculumPlanDayLessons.AsNoTracking()
            join l in db.CurriculumLessons.AsNoTracking() on pl.CurriculumLessonId equals l.Id
            where dayIds.Contains(pl.PlanDayId)
            orderby pl.Order
            select new { pl.PlanDayId, pl.Order, l.Id, l.Title, l.LessonType, l.IsAssessment, l.XpReward })
            .ToListAsync(ct);

        // Completion + scheduled date from the session↔lesson join. A lesson is "done"
        // when a session covering it is Completed, or has simply passed (uncancelled).
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var sessionLessons = await (
            from csl in db.ClassSessionLessons.AsNoTracking()
            join s in db.ClassSessions.AsNoTracking() on csl.ClassSessionId equals s.Id
            where s.ClassId == request.ClassId
            select new { csl.CurriculumLessonId, s.SessionDate, s.Status }).ToListAsync(ct);

        var doneLessonIds = sessionLessons
            .Where(x => x.Status == ClassSessionStatus.Completed
                        || (x.Status != ClassSessionStatus.Cancelled && x.SessionDate < today))
            .Select(x => x.CurriculumLessonId).ToHashSet();
        var lessonDate = sessionLessons
            .GroupBy(x => x.CurriculumLessonId)
            .ToDictionary(g => g.Key, g => g.Min(x => x.SessionDate));

        var planDayLessons = days
            .Select(d => new PlanDayLessons(d.Id, d.Order,
                dayLessons.Where(x => x.PlanDayId == d.Id).Select(x => x.Id).ToList()))
            .ToList();
        var progress = LessonPlanLogic.ComputePlanProgress(planDayLessons, doneLessonIds);
        var completedByDay = progress.Days.ToDictionary(p => p.PlanDayId, p => p.Completed);

        var dto = new ClassPlanProgressDto(
            cls.Id, cls.Title, templateId, templateName,
            progress.CompletedDays, progress.TotalDays, progress.ProgressPct,
            days.Select(d =>
            {
                var ls = dayLessons.Where(x => x.PlanDayId == d.Id).OrderBy(x => x.Order).ToList();
                var dates = ls.Where(x => lessonDate.ContainsKey(x.Id)).Select(x => lessonDate[x.Id]).ToList();
                DateOnly? scheduled = dates.Count > 0 ? dates.Min() : null;
                return new ClassPlanDayDto(
                    d.Id, d.Order, d.Title,
                    completedByDay.TryGetValue(d.Id, out var done) && done, scheduled,
                    ls.Select(x => new TemplatePlanDayLessonDto(
                        x.Id, x.Order, x.Title, x.LessonType, x.IsAssessment, x.XpReward)).ToList());
            }).ToList());

        return Result<ClassPlanProgressDto>.Ok(dto);
    }

    /// <summary>Admin, the class's teacher, or a student enrolled in the class may read it.</summary>
    private async Task<bool> CanViewAsync(Class cls, CancellationToken ct)
    {
        if (IsAdmin) return true;
        var uid = currentUser.UserId;
        if (uid is null) return false;
        if (await db.IsClassTeacherAsync(cls.Id, uid.Value, ct)) return true;
        var profileId = await db.StudentProfiles.AsNoTracking()
            .Where(p => p.UserId == uid).Select(p => p.Id).FirstOrDefaultAsync(ct);
        if (profileId == Guid.Empty) return false;
        return await db.Enrollments.AsNoTracking()
            .AnyAsync(e => e.ClassId == cls.Id && e.StudentProfileId == profileId, ct);
    }
}
