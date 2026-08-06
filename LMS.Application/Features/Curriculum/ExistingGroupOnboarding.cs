using LMS.Application.Common.Abstractions;
using LMS.Application.Common.Models;
using LMS.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LMS.Application.Features.Curriculum;

// ---- F6: existing-group onboarding (suggest + set curriculum position) -------

public sealed record OnboardingLessonDto(
    Guid LessonId, int Index,
    int ModuleOrder, string ModuleTitle,
    int UnitOrder, string UnitTitle,
    int LessonOrder, string LessonTitle);

public sealed record SuggestPositionDto(
    bool CanSuggest,
    Guid? SuggestedLessonId,
    int SuggestedIndex,
    int TotalLessons,
    int? ElapsedSessions,
    string Reasoning,
    IReadOnlyList<OnboardingLessonDto> Lessons);

/// <summary>Computes a suggested current curriculum position from StartDate + cadence.</summary>
public sealed record SuggestPositionQuery(Guid ClassId) : IRequest<Result<SuggestPositionDto>>;

public sealed record SetPositionResultDto(
    Guid ClassId, Guid LessonId, int CompletedThrough, int Created, int Deleted, int SkippedWithAttendance);

/// <summary>Marks every lesson BEFORE the chosen one Completed via backfilled sessions (reconciled).</summary>
public sealed record SetPositionCommand(Guid ClassId, Guid LessonId) : IRequest<Result<SetPositionResultDto>>;

public sealed class ExistingGroupOnboardingHandlers(IApplicationDbContext db, ICurrentUserService currentUser)
    : IRequestHandler<SuggestPositionQuery, Result<SuggestPositionDto>>,
      IRequestHandler<SetPositionCommand, Result<SetPositionResultDto>>
{
    public async Task<Result<SuggestPositionDto>> Handle(SuggestPositionQuery request, CancellationToken ct)
    {
        var cls = await db.Classes.AsNoTracking()
            .Where(c => c.Id == request.ClassId)
            .Select(c => new { c.CurriculumTemplateId, c.TeacherUserId }).FirstOrDefaultAsync(ct);
        if (cls is null) return Result<SuggestPositionDto>.Fail("NOT_FOUND", "Class not found.");
        if (!CurriculumAuthorization.CanManageClass(currentUser, cls.TeacherUserId))
            return Result<SuggestPositionDto>.Fail("FORBIDDEN", "Only the class teacher or an admin can manage this class's curriculum.");
        if (cls.CurriculumTemplateId is not { } tid)
            return Result<SuggestPositionDto>.Fail("VALIDATION", "This class has no curriculum assigned yet.");

        var ordered = await OrderedLessonsAsync(tid, ct);
        var total = ordered.Count;
        var lessons = ordered.Select((l, i) => new OnboardingLessonDto(
            l.Id, i, l.ModuleOrder, l.ModuleTitle, l.UnitOrder, l.UnitTitle, l.LessonOrder, l.Title)).ToList();

        var pattern = await db.ClassSchedulePatterns.AsNoTracking()
            .FirstOrDefaultAsync(p => p.ClassId == request.ClassId, ct);

        if (pattern is null)
            return Result<SuggestPositionDto>.Ok(new SuggestPositionDto(
                CanSuggest: false, SuggestedLessonId: null, SuggestedIndex: 0, TotalLessons: total,
                ElapsedSessions: null,
                Reasoning: "No schedule pattern is set for this class, so the elapsed lessons can't be inferred — choose the current lesson manually.",
                Lessons: lessons));

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var end = today < pattern.EndDate ? today : pattern.EndDate;
        var elapsed = 0;
        for (var d = pattern.StartDate; d <= end; d = d.AddDays(1))
            if (pattern.Matches(d)) elapsed++;

        var index = Math.Min(elapsed, total);
        var suggestedLessonId = index < total ? ordered[index].Id : (Guid?)null;
        var reasoning = index < total
            ? $"Class started {pattern.StartDate:yyyy-MM-dd}; about {elapsed} session(s) have elapsed through {today:yyyy-MM-dd}. Suggested current lesson: Unit {ordered[index].UnitOrder} · Lesson {ordered[index].LessonOrder} — “{ordered[index].Title}”. Everything before it will be marked completed."
            : $"Class started {pattern.StartDate:yyyy-MM-dd}; about {elapsed} session(s) have elapsed — that's the whole {total}-lesson course, so all lessons would be marked completed.";

        return Result<SuggestPositionDto>.Ok(new SuggestPositionDto(
            CanSuggest: true, suggestedLessonId, index, total, elapsed, reasoning, lessons));
    }

    public async Task<Result<SetPositionResultDto>> Handle(SetPositionCommand request, CancellationToken ct)
    {
        var cls = await db.Classes.AsNoTracking()
            .Where(c => c.Id == request.ClassId)
            .Select(c => new { c.CurriculumTemplateId, c.TeacherUserId }).FirstOrDefaultAsync(ct);
        if (cls is null) return Result<SetPositionResultDto>.Fail("NOT_FOUND", "Class not found.");
        if (!CurriculumAuthorization.CanManageClass(currentUser, cls.TeacherUserId))
            return Result<SetPositionResultDto>.Fail("FORBIDDEN", "Only the class teacher or an admin can manage this class's curriculum.");
        if (cls.CurriculumTemplateId is not { } tid)
            return Result<SetPositionResultDto>.Fail("VALIDATION", "This class has no curriculum assigned yet.");

        var ordered = await OrderedLessonsAsync(tid, ct);
        var index = ordered.FindIndex(l => l.Id == request.LessonId);
        if (index < 0)
            return Result<SetPositionResultDto>.Fail("VALIDATION", "That lesson isn't part of this class's curriculum.");

        // Lessons BEFORE the chosen position must end up completed.
        var targetLessonIds = ordered.Take(index).Select(l => l.Id).ToList();

        // Existing curriculum-linked sessions + whether each has attendance.
        var linked = await db.ClassSessions.AsNoTracking()
            .Where(s => s.ClassId == request.ClassId && s.CurriculumLessonId != null)
            .Select(s => new { s.Id, LessonId = s.CurriculumLessonId!.Value, s.IsBackfilled })
            .ToListAsync(ct);
        var withAttendance = (await db.Attendance.AsNoTracking()
                .Where(a => a.ClassId == request.ClassId).Select(a => a.SessionId).Distinct().ToListAsync(ct))
            .ToHashSet();

        var existing = linked
            .Select(s => new BackfillSession(s.Id, s.LessonId, s.IsBackfilled, withAttendance.Contains(s.Id)))
            .ToList();

        var plan = BackfillReconciler.Plan(targetLessonIds, existing);

        // Apply the plan atomically. Wrapped in the execution strategy because the
        // context enables retry-on-failure (a bare BeginTransactionAsync throws under
        // NpgsqlRetryingExecutionStrategy); partial failure leaves the class unchanged.
        return await db.ExecuteInTransactionAsync<SetPositionResultDto>(async () =>
        {
        if (plan.DeleteSessionIds.Count > 0)
        {
            var toDelete = await db.ClassSessions
                .Where(s => plan.DeleteSessionIds.Contains(s.Id)).ToListAsync(ct);
            db.ClassSessions.RemoveRange(toDelete);
        }

        var baseDate = (await db.ClassSchedulePatterns.AsNoTracking()
            .Where(p => p.ClassId == request.ClassId).Select(p => (DateOnly?)p.StartDate).FirstOrDefaultAsync(ct))
            ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var now = DateTime.UtcNow;
        var slot = new TimeOnly(0, 0);
        var slotEnd = new TimeOnly(0, 1);

        foreach (var lessonId in plan.CreateForLessonIds)
        {
            var li = ordered.FindIndex(l => l.Id == lessonId);
            var date = baseDate.AddDays(li); // distinct per lesson + reserved 00:00 slot ⇒ collision-proof
            await db.ClassSessions.AddAsync(
                ClassSession.CreateBackfilled(request.ClassId, date, slot, slotEnd, lessonId, ordered[li].Title, now), ct);
        }

        await db.SaveChangesAsync(ct);

        return Result<SetPositionResultDto>.Ok(
            new SetPositionResultDto(
                request.ClassId, request.LessonId, targetLessonIds.Count,
                plan.CreateForLessonIds.Count, plan.DeleteSessionIds.Count, plan.SkippedWithAttendance.Count),
            plan.SkippedWithAttendance.Count > 0
                ? $"Set position: {plan.CreateForLessonIds.Count} added, {plan.DeleteSessionIds.Count} removed, {plan.SkippedWithAttendance.Count} kept (had attendance)."
                : $"Set position: {targetLessonIds.Count} lesson(s) marked completed.");
        }, ct);
    }

    private async Task<List<OrderedLesson>> OrderedLessonsAsync(Guid templateId, CancellationToken ct) =>
        await (
            from l in db.CurriculumLessons.AsNoTracking()
            join u in db.CurriculumUnits.AsNoTracking() on l.UnitId equals u.Id
            join m in db.CurriculumModules.AsNoTracking() on u.ModuleId equals m.Id
            where m.TemplateId == templateId
            orderby m.Order, u.Order, l.Order
            select new OrderedLesson(l.Id, l.Title, m.Order, m.Title, u.Order, u.Title, l.Order)
        ).ToListAsync(ct);

    private sealed record OrderedLesson(
        Guid Id, string Title, int ModuleOrder, string ModuleTitle, int UnitOrder, string UnitTitle, int LessonOrder);
}
