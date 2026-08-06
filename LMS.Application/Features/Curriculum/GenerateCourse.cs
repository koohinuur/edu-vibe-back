using LMS.Application.Common.Abstractions;
using LMS.Application.Common.Models;
using LMS.Application.Features.Sessions;
using LMS.Application.Features.Tasks;
using LMS.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LMS.Application.Features.Curriculum;

/// <summary>
/// F3 one-shot course setup: clone a template onto the class (once), generate the
/// session calendar (2–3 lessons/day via <see cref="ScheduleSlot"/>), and map the
/// upcoming sessions to the cloned lessons in curriculum order — all inside ONE
/// transaction so a mid-flow failure leaves the class entirely unconfigured rather
/// than half-set-up. Idempotent: a re-run with the same template reuses the
/// existing clone and re-applies the (already idempotent) schedule + mapping, so
/// it never stacks duplicate courses.
/// </summary>
public sealed record GenerateCourseCommand(
    Guid ClassId,
    Guid TemplateId,
    SchedulePatternType Type,
    int DaysOfWeekMask,
    DateOnly StartDate,
    DateOnly EndDate,
    IReadOnlyList<ScheduleSlot> Slots,
    Guid? RoomId) : IRequest<Result<GenerateCourseResultDto>>;

public sealed record GenerateCourseResultDto(
    Guid ClassId,
    Guid CurriculumTemplateId,
    int GeneratedSessions,
    int RemovedSessions,
    int MappedLessons,
    int MaterializedTasks);

public sealed class GenerateCourseHandler(
    IApplicationDbContext db, ISender sender,
    ICurrentUserService currentUser, ILessonTaskMaterializer materializer)
    : IRequestHandler<GenerateCourseCommand, Result<GenerateCourseResultDto>>
{
    public async Task<Result<GenerateCourseResultDto>> Handle(GenerateCourseCommand request, CancellationToken ct)
    {
        if (request.Slots is null || request.Slots.Count == 0)
            return Result<GenerateCourseResultDto>.Fail("VALIDATION", "At least one daily time slot is required.");

        // One transaction across all four steps so a mid-flow failure leaves the class
        // entirely unconfigured rather than half-set-up. Wrapped in the execution
        // strategy because the context enables retry-on-failure — a bare
        // BeginTransactionAsync throws under NpgsqlRetryingExecutionStrategy. An early
        // Result.Fail returns from the lambda uncommitted ⇒ the helper rolls back.
        return await db.ExecuteInTransactionAsync<GenerateCourseResultDto>(async () =>
        {
        var cls = await db.Classes.FirstOrDefaultAsync(c => c.Id == request.ClassId, ct);
        if (cls is null) return Result<GenerateCourseResultDto>.Fail("NOT_FOUND", "Class not found.");
        if (!CurriculumAuthorization.CanManageClass(currentUser, cls))
            return Result<GenerateCourseResultDto>.Fail("FORBIDDEN", "Only the class teacher or an admin can set up this class's course.");

        var template = await db.CurriculumTemplates.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == request.TemplateId, ct);
        if (template is null) return Result<GenerateCourseResultDto>.Fail("NOT_FOUND", "Template not found.");

        var primary = request.Slots.OrderBy(s => s.StartsAt).First();

        // 1. Bind the class DIRECTLY to the chosen master template — NO per-class clone.
        //    The class shares the ready library template; its sessions map to that
        //    template's own lessons (step 3), and its day-plan is read from the master.
        //    Curriculum edits happen in the Template Library, never per-class.
        var courseTemplateId = template.Id;
        cls.SetCurriculumTemplate(courseTemplateId);

        // 2. Generate the session calendar (N-per-day via Slots).
        var sched = await sender.Send(new ApplyClassScheduleCommand(
            request.ClassId, request.Type, request.DaysOfWeekMask,
            request.StartDate, request.EndDate, primary.StartsAt, primary.EndsAt,
            request.RoomId, request.Slots), ct);
        if (!sched.Success || sched.Data is null)
            return Result<GenerateCourseResultDto>.Fail(sched.ErrorCode ?? "FAILED", sched.Message ?? "Scheduling failed.");

        // 3. Map upcoming sessions to the master template's lessons in order.
        var assign = await sender.Send(new AssignCurriculumToClassCommand(request.ClassId, courseTemplateId), ct);
        if (!assign.Success)
            return Result<GenerateCourseResultDto>.Fail(assign.ErrorCode ?? "FAILED", assign.Message ?? "Curriculum mapping failed.");

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var mapped = await db.ClassSessions.AsNoTracking()
            .CountAsync(s => s.ClassId == request.ClassId && s.SessionDate >= today && s.CurriculumLessonId != null, ct);

        // 4. Auto-materialise each mapped lesson's default-task blueprints into real
        //    gradeable tasks — inside the SAME transaction, so the course is fully set
        //    up or not at all. Only sessions without an assignment yet are touched, so a
        //    re-run is a clean no-op; the materialiser is fail-soft (no blueprints / exam
        //    lessons => 0) and idempotent regardless.
        var creatorId = cls.TeacherUserId ?? currentUser.UserId ?? Guid.Empty;
        var sessionsToMaterialize = await db.ClassSessions.AsNoTracking()
            .Where(s => s.ClassId == request.ClassId && s.CurriculumLessonId != null
                        && !db.Assignments.Any(a => a.ClassSessionId == s.Id))
            .Select(s => s.Id)
            .ToListAsync(ct);
        var materializedTasks = 0;
        foreach (var sid in sessionsToMaterialize)
            materializedTasks += (await materializer.MaterializeAsync(sid, creatorId, ct)).CreatedTasks;

        return Result<GenerateCourseResultDto>.Ok(
            new GenerateCourseResultDto(request.ClassId, courseTemplateId,
                sched.Data.GeneratedCount, sched.Data.RemovedCount, mapped, materializedTasks),
            $"Course ready: {sched.Data.GeneratedCount} session(s), {mapped} mapped, {materializedTasks} task(s) created.");
        }, ct);
    }
}
