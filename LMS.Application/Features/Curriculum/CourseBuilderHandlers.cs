using LMS.Application.Common.Abstractions;
using LMS.Application.Features.Classes;
using LMS.Application.Common.Models;
using LMS.Application.Common.Security;
using LMS.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LMS.Application.Features.Curriculum;

/// <summary>
/// Teacher Course Builder. Operates on the class's OWN editable curriculum
/// template (lazily created the first time the builder opens) so a teacher can
/// structure units + lessons without touching shared system templates. Every
/// call self-scopes to the class teacher (or an admin).
/// </summary>
public sealed class CourseBuilderHandlers(IApplicationDbContext db, ICurrentUserService currentUser) :
    IRequestHandler<GetClassCourseBuilderQuery, Result<ClassCourseBuilderDto>>,
    IRequestHandler<CreateCourseUnitCommand, Result<ClassCourseBuilderDto>>,
    IRequestHandler<UpdateCourseUnitCommand, Result<ClassCourseBuilderDto>>,
    IRequestHandler<DeleteCourseUnitCommand, Result<ClassCourseBuilderDto>>,
    IRequestHandler<ReorderCourseUnitsCommand, Result<ClassCourseBuilderDto>>,
    IRequestHandler<CreateCourseLessonCommand, Result<ClassCourseBuilderDto>>,
    IRequestHandler<UpdateCourseLessonCommand, Result<ClassCourseBuilderDto>>,
    IRequestHandler<DeleteCourseLessonCommand, Result<ClassCourseBuilderDto>>,
    IRequestHandler<ReorderCourseLessonsCommand, Result<ClassCourseBuilderDto>>,
    IRequestHandler<BulkCreateUnitCommand, Result<ClassCourseBuilderDto>>,
    IRequestHandler<DuplicateCourseUnitCommand, Result<ClassCourseBuilderDto>>,
    IRequestHandler<DuplicateCourseLessonCommand, Result<ClassCourseBuilderDto>>,
    IRequestHandler<MoveCourseLessonCommand, Result<ClassCourseBuilderDto>>,
    IRequestHandler<CloneTemplateToClassCommand, Result<ClassCourseBuilderDto>>
{
    // ---- public handlers --------------------------------------------------

    public async Task<Result<ClassCourseBuilderDto>> Handle(GetClassCourseBuilderQuery request, CancellationToken ct)
        => await WithCourse(request.ClassId, ct, (_, _) => Task.CompletedTask, isMutation: false);

    public async Task<Result<ClassCourseBuilderDto>> Handle(CloneTemplateToClassCommand request, CancellationToken ct)
    {
        var cls = await db.Classes.FirstOrDefaultAsync(c => c.Id == request.ClassId, ct);
        if (cls is null) return Fail("NOT_FOUND", "Class not found.");
        if (!IsAdmin && (currentUser.UserId is not { } uid || !await db.IsClassTeacherAsync(cls.Id, uid, ct)))
            return Fail("FORBIDDEN", "Only the class teacher or an admin can set up this course.");

        var source = await db.CurriculumTemplates.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == request.TemplateId, ct);
        if (source is null) return Fail("NOT_FOUND", "Template not found.");

        // Fresh editable course owned by the class — a deep copy of the source.
        var clone = new CurriculumTemplate(
            $"{cls.Title} — {source.Name}", source.Category, source.Level, source.Description, isSystem: false);
        await db.CurriculumTemplates.AddAsync(clone, ct);
        await db.SaveChangesAsync(ct);

        var modules = await db.CurriculumModules.AsNoTracking()
            .Where(m => m.TemplateId == source.Id).OrderBy(m => m.Order).ToListAsync(ct);
        var moduleIds = modules.Select(m => m.Id).ToList();
        var units = await db.CurriculumUnits.AsNoTracking()
            .Where(u => moduleIds.Contains(u.ModuleId)).OrderBy(u => u.Order).ToListAsync(ct);
        var unitIds = units.Select(u => u.Id).ToList();
        var lessons = await db.CurriculumLessons.AsNoTracking()
            .Where(l => unitIds.Contains(l.UnitId)).OrderBy(l => l.Order).ToListAsync(ct);

        // Source default tasks, grouped by lesson — deep-copied onto the clone's
        // lessons below (F3 copy-at-clone). Loaded once to avoid per-lesson queries.
        var lessonIds = lessons.Select(l => l.Id).ToList();
        var defaultTasksBySource = (await db.LessonDefaultTasks.AsNoTracking()
                .Where(t => lessonIds.Contains(t.CurriculumLessonId)).ToListAsync(ct))
            .GroupBy(t => t.CurriculumLessonId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Source self-check exercises, grouped by lesson — deep-copied onto the clone's
        // lessons below (alongside default tasks), so a class that clones a template
        // inherits the lesson's exercises too. Without this, cloning silently dropped
        // them and students saw "No exercises for this lesson yet".
        var exercisesBySource = (await db.LessonExercises.AsNoTracking()
                .Where(e => e.LessonId != null && lessonIds.Contains(e.LessonId.Value)).ToListAsync(ct))
            .GroupBy(e => e.LessonId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Source→clone lesson id map, accumulated across all units, so the template
        // teaching plan (curriculum_plan_days) can be re-pointed onto the clone's
        // lessons after the tree is copied.
        var lessonIdMap = new Dictionary<Guid, Guid>();

        foreach (var m in modules)
        {
            var mc = new CurriculumModule(clone.Id, m.Order, m.Title);
            await db.CurriculumModules.AddAsync(mc, ct);
            await db.SaveChangesAsync(ct);
            foreach (var u in units.Where(x => x.ModuleId == m.Id))
            {
                var uc = new CurriculumUnit(mc.Id, u.Order, u.Title);
                uc.Update(u.Title, u.Description);
                uc.SetMeta(u.Icon, u.EstimatedMinutes, u.XpReward);
                await db.CurriculumUnits.AddAsync(uc, ct);
                await db.SaveChangesAsync(ct);
                var lessonClones = new List<(Guid SourceId, CurriculumLesson Clone)>();
                foreach (var l in lessons.Where(x => x.UnitId == u.Id))
                {
                    var lc = new CurriculumLesson(uc.Id, l.Order, l.Title,
                        l.Objectives, l.HomeworkPlaceholder, l.MaterialsPlaceholder, l.IsAssessment);
                    lc.SetMeta(l.LessonType, l.DurationMinutes, l.XpReward);
                    await db.CurriculumLessons.AddAsync(lc, ct);
                    lessonClones.Add((l.Id, lc));
                }
                await db.SaveChangesAsync(ct);

                // Record source→clone lesson ids (the clone Ids are populated by the
                // save above) so the template plan can be re-pointed onto the clone.
                foreach (var (sourceId, lc) in lessonClones)
                    lessonIdMap[sourceId] = lc.Id;

                // Copy each source lesson's default tasks onto its clone — the
                // clone's Id is populated by the save above. Each clone is a fresh
                // template, so this never duplicates (GenerateCourse also skips a
                // re-clone when the class already holds this template's clone).
                foreach (var (sourceId, lc) in lessonClones)
                    if (defaultTasksBySource.TryGetValue(sourceId, out var dts))
                        foreach (var dt in dts)
                            await db.LessonDefaultTasks.AddAsync(
                                new LessonDefaultTask(lc.Id, dt.Order, dt.Type, dt.Title, dt.Points, dt.ContentJson, dt.SolutionJson), ct);

                // Carry each source lesson's self-check exercises onto its clone.
                foreach (var (sourceId, lc) in lessonClones)
                    if (exercisesBySource.TryGetValue(sourceId, out var exs))
                        foreach (var ex in exs)
                            await db.LessonExercises.AddAsync(
                                new LessonExercise(lc.Id, ex.Type, ex.Title, ex.OrderIndex, ex.ContentJson), ct);
                await db.SaveChangesAsync(ct);
            }
        }

        // Inherit the book's reusable teaching plan: copy the source template's
        // plan-days (and their lesson sets) onto the clone, re-pointing each plan-day
        // lesson to the clone's copy. A book with no plan yet copies nothing.
        await CopyTemplatePlanAsync(source.Id, clone.Id, lessonIdMap, ct);

        cls.SetCurriculumTemplate(clone.Id);
        await db.SaveChangesAsync(ct);
        return await BuildDtoAsync(request.ClassId, clone.Id, ct);
    }

    /// <summary>
    /// Copies a source template's teaching plan (curriculum_plan_days +
    /// curriculum_plan_day_lessons) onto a freshly-cloned template, re-pointing each
    /// plan-day lesson to the clone's corresponding lesson via <paramref name="lessonIdMap"/>.
    /// A plan-day lesson with no mapping (shouldn't happen for a full clone) is skipped;
    /// an empty/exam day is still copied so its order in the plan is preserved.
    /// </summary>
    private async Task CopyTemplatePlanAsync(
        Guid sourceTemplateId, Guid cloneTemplateId,
        IReadOnlyDictionary<Guid, Guid> lessonIdMap, CancellationToken ct)
    {
        var planDays = await db.CurriculumPlanDays.AsNoTracking()
            .Where(d => d.TemplateId == sourceTemplateId).OrderBy(d => d.Order).ToListAsync(ct);
        if (planDays.Count == 0) return;

        var dayIds = planDays.Select(d => d.Id).ToList();
        var dayLessons = await db.CurriculumPlanDayLessons.AsNoTracking()
            .Where(pl => dayIds.Contains(pl.PlanDayId)).OrderBy(pl => pl.Order).ToListAsync(ct);

        foreach (var d in planDays)
        {
            var dc = new CurriculumPlanDay(cloneTemplateId, d.Order, d.Title);
            await db.CurriculumPlanDays.AddAsync(dc, ct);
            await db.SaveChangesAsync(ct);

            foreach (var pl in dayLessons.Where(x => x.PlanDayId == d.Id))
                if (lessonIdMap.TryGetValue(pl.CurriculumLessonId, out var cloneLessonId))
                    await db.CurriculumPlanDayLessons.AddAsync(
                        new CurriculumPlanDayLesson(dc.Id, cloneLessonId, pl.Order), ct);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<Result<ClassCourseBuilderDto>> Handle(CreateCourseUnitCommand request, CancellationToken ct)
        => await WithCourse(request.ClassId, ct, async (templateId, moduleId) =>
        {
            var nextOrder = await NextUnitOrder(templateId, ct);
            var unit = new CurriculumUnit(moduleId, nextOrder, request.Title);
            unit.Update(request.Title, request.Description);
            unit.SetMeta(request.Icon, request.EstimatedMinutes, request.XpReward);
            await db.CurriculumUnits.AddAsync(unit, ct);
            await db.SaveChangesAsync(ct);
        });

    public async Task<Result<ClassCourseBuilderDto>> Handle(UpdateCourseUnitCommand request, CancellationToken ct)
    {
        var unit = await db.CurriculumUnits.FirstOrDefaultAsync(u => u.Id == request.UnitId, ct);
        if (unit is null) return Fail("NOT_FOUND", "Unit not found.");
        var classId = await ClassIdForModule(unit.ModuleId, ct);
        return await WithCourse(classId, ct, async (_, _) =>
        {
            unit.Update(request.Title, request.Description);
            unit.SetMeta(request.Icon, request.EstimatedMinutes, request.XpReward);
            await db.SaveChangesAsync(ct);
        });
    }

    public async Task<Result<ClassCourseBuilderDto>> Handle(DeleteCourseUnitCommand request, CancellationToken ct)
    {
        var unit = await db.CurriculumUnits.FirstOrDefaultAsync(u => u.Id == request.UnitId, ct);
        if (unit is null) return Fail("NOT_FOUND", "Unit not found.");
        var classId = await ClassIdForModule(unit.ModuleId, ct);
        return await WithCourse(classId, ct, async (_, _) =>
        {
            db.CurriculumUnits.Remove(unit); // cascade removes its lessons
            await db.SaveChangesAsync(ct);
        });
    }

    public async Task<Result<ClassCourseBuilderDto>> Handle(ReorderCourseUnitsCommand request, CancellationToken ct)
        => await WithCourse(request.ClassId, ct, async (templateId, _) =>
        {
            var moduleIds = await db.CurriculumModules.Where(m => m.TemplateId == templateId)
                .Select(m => m.Id).ToListAsync(ct);
            var units = await db.CurriculumUnits.Where(u => moduleIds.Contains(u.ModuleId)).ToListAsync(ct);
            for (var i = 0; i < request.UnitIds.Count; i++)
                units.FirstOrDefault(u => u.Id == request.UnitIds[i])?.SetOrder(i + 1);
            await db.SaveChangesAsync(ct);
        });

    public async Task<Result<ClassCourseBuilderDto>> Handle(CreateCourseLessonCommand request, CancellationToken ct)
    {
        var unit = await db.CurriculumUnits.FirstOrDefaultAsync(u => u.Id == request.UnitId, ct);
        if (unit is null) return Fail("NOT_FOUND", "Unit not found.");
        var classId = await ClassIdForModule(unit.ModuleId, ct);
        return await WithCourse(classId, ct, async (_, _) =>
        {
            var nextOrder = (await db.CurriculumLessons.Where(l => l.UnitId == unit.Id)
                .MaxAsync(l => (int?)l.Order, ct) ?? 0) + 1;
            var lesson = new CurriculumLesson(unit.Id, nextOrder, request.Title,
                request.Objectives, request.Homework, request.Materials, request.IsAssessment);
            lesson.SetMeta(request.LessonType, request.DurationMinutes, request.XpReward);
            await db.CurriculumLessons.AddAsync(lesson, ct);
            await db.SaveChangesAsync(ct);
        });
    }

    public async Task<Result<ClassCourseBuilderDto>> Handle(UpdateCourseLessonCommand request, CancellationToken ct)
    {
        var lesson = await db.CurriculumLessons.FirstOrDefaultAsync(l => l.Id == request.LessonId, ct);
        if (lesson is null) return Fail("NOT_FOUND", "Lesson not found.");
        var classId = await ClassIdForUnit(lesson.UnitId, ct);
        return await WithCourse(classId, ct, async (_, _) =>
        {
            lesson.Update(request.Title, request.Objectives, request.Homework, request.Materials, request.IsAssessment);
            lesson.SetMeta(request.LessonType, request.DurationMinutes, request.XpReward);
            await db.SaveChangesAsync(ct);
        });
    }

    public async Task<Result<ClassCourseBuilderDto>> Handle(DeleteCourseLessonCommand request, CancellationToken ct)
    {
        var lesson = await db.CurriculumLessons.FirstOrDefaultAsync(l => l.Id == request.LessonId, ct);
        if (lesson is null) return Fail("NOT_FOUND", "Lesson not found.");
        var classId = await ClassIdForUnit(lesson.UnitId, ct);
        return await WithCourse(classId, ct, async (_, _) =>
        {
            db.CurriculumLessons.Remove(lesson);
            await db.SaveChangesAsync(ct);
        });
    }

    public async Task<Result<ClassCourseBuilderDto>> Handle(ReorderCourseLessonsCommand request, CancellationToken ct)
    {
        var unit = await db.CurriculumUnits.FirstOrDefaultAsync(u => u.Id == request.UnitId, ct);
        if (unit is null) return Fail("NOT_FOUND", "Unit not found.");
        var classId = await ClassIdForModule(unit.ModuleId, ct);
        return await WithCourse(classId, ct, async (_, _) =>
        {
            var lessons = await db.CurriculumLessons.Where(l => l.UnitId == unit.Id).ToListAsync(ct);
            for (var i = 0; i < request.LessonIds.Count; i++)
                lessons.FirstOrDefault(l => l.Id == request.LessonIds[i])?.SetOrder(i + 1);
            await db.SaveChangesAsync(ct);
        });
    }

    public async Task<Result<ClassCourseBuilderDto>> Handle(BulkCreateUnitCommand request, CancellationToken ct)
        => await WithCourse(request.ClassId, ct, async (templateId, moduleId) =>
        {
            var nextOrder = await NextUnitOrder(templateId, ct);
            var unit = new CurriculumUnit(moduleId, nextOrder, request.Title);
            unit.Update(request.Title, request.Description);
            unit.SetMeta(request.Icon, request.EstimatedMinutes, request.XpReward);
            await db.CurriculumUnits.AddAsync(unit, ct);

            var order = 1;
            foreach (var l in request.Lessons ?? Array.Empty<BulkLessonInput>())
            {
                if (string.IsNullOrWhiteSpace(l.Title)) continue;
                var lesson = new CurriculumLesson(unit.Id, order++, l.Title,
                    l.Objectives, l.Homework, l.Materials, l.IsAssessment);
                lesson.SetMeta(l.LessonType, l.DurationMinutes, l.XpReward);
                await db.CurriculumLessons.AddAsync(lesson, ct);
            }
            await db.SaveChangesAsync(ct);
        });

    public async Task<Result<ClassCourseBuilderDto>> Handle(DuplicateCourseUnitCommand request, CancellationToken ct)
    {
        var unit = await db.CurriculumUnits.FirstOrDefaultAsync(u => u.Id == request.UnitId, ct);
        if (unit is null) return Fail("NOT_FOUND", "Unit not found.");
        var classId = await ClassIdForModule(unit.ModuleId, ct);
        return await WithCourse(classId, ct, async (templateId, _) =>
        {
            var lessons = await db.CurriculumLessons.Where(l => l.UnitId == unit.Id)
                .OrderBy(l => l.Order).ToListAsync(ct);
            var nextOrder = await NextUnitOrder(templateId, ct);
            var copy = new CurriculumUnit(unit.ModuleId, nextOrder, $"{unit.Title} (copy)");
            copy.Update($"{unit.Title} (copy)", unit.Description);
            copy.SetMeta(unit.Icon, unit.EstimatedMinutes, unit.XpReward);
            await db.CurriculumUnits.AddAsync(copy, ct);
            await db.SaveChangesAsync(ct);

            var order = 1;
            foreach (var l in lessons)
            {
                var lc = new CurriculumLesson(copy.Id, order++, l.Title,
                    l.Objectives, l.HomeworkPlaceholder, l.MaterialsPlaceholder, l.IsAssessment);
                lc.SetMeta(l.LessonType, l.DurationMinutes, l.XpReward);
                await db.CurriculumLessons.AddAsync(lc, ct);
            }
            await db.SaveChangesAsync(ct);
        });
    }

    public async Task<Result<ClassCourseBuilderDto>> Handle(DuplicateCourseLessonCommand request, CancellationToken ct)
    {
        var lesson = await db.CurriculumLessons.FirstOrDefaultAsync(l => l.Id == request.LessonId, ct);
        if (lesson is null) return Fail("NOT_FOUND", "Lesson not found.");
        var classId = await ClassIdForUnit(lesson.UnitId, ct);
        return await WithCourse(classId, ct, async (_, _) =>
        {
            var nextOrder = (await db.CurriculumLessons.Where(l => l.UnitId == lesson.UnitId)
                .MaxAsync(l => (int?)l.Order, ct) ?? 0) + 1;
            var copy = new CurriculumLesson(lesson.UnitId, nextOrder, $"{lesson.Title} (copy)",
                lesson.Objectives, lesson.HomeworkPlaceholder, lesson.MaterialsPlaceholder, lesson.IsAssessment);
            copy.SetMeta(lesson.LessonType, lesson.DurationMinutes, lesson.XpReward);
            await db.CurriculumLessons.AddAsync(copy, ct);
            await db.SaveChangesAsync(ct);
        });
    }

    public async Task<Result<ClassCourseBuilderDto>> Handle(MoveCourseLessonCommand request, CancellationToken ct)
    {
        var lesson = await db.CurriculumLessons.FirstOrDefaultAsync(l => l.Id == request.LessonId, ct);
        if (lesson is null) return Fail("NOT_FOUND", "Lesson not found.");
        var srcClassId = await ClassIdForUnit(lesson.UnitId, ct);
        if (lesson.UnitId == request.TargetUnitId)
            return await WithCourse(srcClassId, ct, (_, _) => Task.CompletedTask); // no-op, return current

        var target = await db.CurriculumUnits.FirstOrDefaultAsync(u => u.Id == request.TargetUnitId, ct);
        if (target is null) return Fail("NOT_FOUND", "Target unit not found.");
        var targetClassId = await ClassIdForModule(target.ModuleId, ct);
        if (srcClassId == Guid.Empty || srcClassId != targetClassId)
            return Fail("FORBIDDEN", "A lesson can only move within the same course.");

        var sourceUnitId = lesson.UnitId;
        return await WithCourse(srcClassId, ct, async (_, _) =>
        {
            var append = (await db.CurriculumLessons.Where(l => l.UnitId == target.Id)
                .MaxAsync(l => (int?)l.Order, ct) ?? 0) + 1;
            lesson.MoveToUnit(target.Id, request.TargetOrder is > 0 ? request.TargetOrder.Value : append);
            await db.SaveChangesAsync(ct);
            await Renumber(sourceUnitId, ct); // close the gap left behind
            await Renumber(target.Id, ct);    // make orders contiguous in the destination
            await db.SaveChangesAsync(ct);
        });
    }

    /// <summary>Re-sequences a unit's lessons to 1..N by current order (used after a cross-unit move).</summary>
    private async Task Renumber(Guid unitId, CancellationToken ct)
    {
        var ls = await db.CurriculumLessons.Where(l => l.UnitId == unitId).OrderBy(l => l.Order).ToListAsync(ct);
        for (var i = 0; i < ls.Count; i++) ls[i].SetOrder(i + 1);
    }

    // ---- shared plumbing --------------------------------------------------

    private static Result<ClassCourseBuilderDto> Fail(string code, string msg) =>
        Result<ClassCourseBuilderDto>.Fail(code, msg);

    private bool IsAdmin => currentUser.IsAdmin();

    /// <summary>
    /// Authorizes the caller for the class, ensures the class has its own
    /// editable template + default module, runs the supplied mutation, then
    /// returns the refreshed builder tree.
    /// </summary>
    private async Task<Result<ClassCourseBuilderDto>> WithCourse(
        Guid classId, CancellationToken ct, Func<Guid, Guid, Task> mutate, bool isMutation = true)
    {
        var cls = await db.Classes.FirstOrDefaultAsync(c => c.Id == classId, ct);
        if (cls is null) return Fail("NOT_FOUND", "Class not found.");
        if (!IsAdmin && (currentUser.UserId is not { } uid || !await db.IsClassTeacherAsync(cls.Id, uid, ct)))
            return Fail("FORBIDDEN", "Only the class teacher or an admin can edit this course.");

        var (templateId, moduleId) = await EnsureClassCourseAsync(cls, ct);

        // A class bound to a shared LIBRARY (system) template is READ-ONLY here — a
        // per-class edit would change the master for every class that uses it. Curriculum
        // edits go through the Template Library instead. Reads (isMutation:false) still
        // return the tree so the builder can display the shared course + its day-plan.
        if (isMutation && await db.CurriculumTemplates.AnyAsync(t => t.Id == templateId && t.IsSystem, ct))
            return Fail("READONLY", "This class uses a shared library template — edit the course in the Template Library, not per-class.");

        await mutate(templateId, moduleId);
        return await BuildDtoAsync(classId, templateId, ct);
    }

    /// <summary>Gives the class its own editable (non-system) template + a default module if it has none.</summary>
    private async Task<(Guid TemplateId, Guid ModuleId)> EnsureClassCourseAsync(Class cls, CancellationToken ct)
    {
        Guid templateId;
        // Use whatever template the class is bound to — a shared LIBRARY master OR its
        // own editable course. Never clone a master: sharing the ready template is the point.
        if (cls.CurriculumTemplateId is { } tid &&
            await db.CurriculumTemplates.AnyAsync(t => t.Id == tid, ct))
        {
            templateId = tid;
        }
        else
        {
            var template = new CurriculumTemplate(
                $"{cls.Title} — Course", CurriculumCategory.Custom, null, null, isSystem: false);
            await db.CurriculumTemplates.AddAsync(template, ct);
            await db.SaveChangesAsync(ct);
            cls.SetCurriculumTemplate(template.Id);
            await db.SaveChangesAsync(ct);
            templateId = template.Id;
        }

        // A shared master already owns its modules; never add one (that would mutate the
        // master). Only the class's own scratch course gets a default module.
        var isSystem = await db.CurriculumTemplates
            .Where(t => t.Id == templateId).Select(t => t.IsSystem).FirstOrDefaultAsync(ct);
        var module = await db.CurriculumModules
            .Where(m => m.TemplateId == templateId).OrderBy(m => m.Order)
            .FirstOrDefaultAsync(ct);
        if (module is null && !isSystem)
        {
            module = new CurriculumModule(templateId, 1, "Course");
            await db.CurriculumModules.AddAsync(module, ct);
            await db.SaveChangesAsync(ct);
        }
        return (templateId, module?.Id ?? Guid.Empty);
    }

    private async Task<int> NextUnitOrder(Guid templateId, CancellationToken ct)
    {
        var moduleIds = await db.CurriculumModules.Where(m => m.TemplateId == templateId).Select(m => m.Id).ToListAsync(ct);
        return (await db.CurriculumUnits.Where(u => moduleIds.Contains(u.ModuleId))
            .MaxAsync(u => (int?)u.Order, ct) ?? 0) + 1;
    }

    private async Task<Guid> ClassIdForModule(Guid moduleId, CancellationToken ct) =>
        await db.CurriculumModules.Where(m => m.Id == moduleId)
            .Join(db.Classes, m => m.TemplateId, c => c.CurriculumTemplateId, (m, c) => c.Id)
            .FirstOrDefaultAsync(ct);

    private async Task<Guid> ClassIdForUnit(Guid unitId, CancellationToken ct)
    {
        var moduleId = await db.CurriculumUnits.Where(u => u.Id == unitId).Select(u => u.ModuleId).FirstOrDefaultAsync(ct);
        return await ClassIdForModule(moduleId, ct);
    }

    private async Task<Result<ClassCourseBuilderDto>> BuildDtoAsync(Guid classId, Guid templateId, CancellationToken ct)
    {
        var templateName = await db.CurriculumTemplates.Where(t => t.Id == templateId)
            .Select(t => t.Name).FirstAsync(ct);

        var moduleIds = await db.CurriculumModules.Where(m => m.TemplateId == templateId).Select(m => m.Id).ToListAsync(ct);
        var units = await db.CurriculumUnits.AsNoTracking()
            .Where(u => moduleIds.Contains(u.ModuleId)).OrderBy(u => u.Order)
            .Select(u => new { u.Id, u.Order, u.Title, u.Description, u.Icon, u.EstimatedMinutes, u.XpReward }).ToListAsync(ct);
        var unitIds = units.Select(u => u.Id).ToList();
        var lessons = await db.CurriculumLessons.AsNoTracking()
            .Where(l => unitIds.Contains(l.UnitId)).OrderBy(l => l.Order)
            .Select(l => new { l.UnitId, l.Id, l.Order, l.Title, l.Objectives, l.HomeworkPlaceholder, l.MaterialsPlaceholder, l.IsAssessment, l.LessonType, l.DurationMinutes, l.XpReward })
            .ToListAsync(ct);

        // Homework count reflects materialisable default-task BLUEPRINTS, not the
        // free-text HomeworkPlaceholder — so the teacher's count matches the tasks a
        // student will actually get once the lesson is materialised.
        var lessonIdsForCount = lessons.Select(l => l.Id).ToList();
        var lessonIdsWithBlueprints = (await db.LessonDefaultTasks.AsNoTracking()
                .Where(t => lessonIdsForCount.Contains(t.CurriculumLessonId))
                .Select(t => t.CurriculumLessonId).Distinct().ToListAsync(ct))
            .ToHashSet();

        // Self-check exercise count per lesson — surfaced as a badge so the teacher
        // can see which lessons already have exercises (and confirm a save landed).
        var exerciseCounts = (await db.LessonExercises.AsNoTracking()
                .Where(e => e.LessonId != null && lessonIdsForCount.Contains(e.LessonId.Value))
                .GroupBy(e => e.LessonId)
                .Select(g => new { LessonId = g.Key, Count = g.Count() })
                .ToListAsync(ct))
            .ToDictionary(x => x.LessonId!.Value, x => x.Count);

        // Lessons referenced by this template's teaching plan — the ONLY lessons a
        // student can reach in their journey. A lesson with exercises but not in the
        // plan is authored-but-unreachable, so the editor warns about it.
        var planLessonIds = (await (
                from pl in db.CurriculumPlanDayLessons.AsNoTracking()
                join d in db.CurriculumPlanDays.AsNoTracking() on pl.PlanDayId equals d.Id
                where d.TemplateId == templateId
                select pl.CurriculumLessonId).Distinct().ToListAsync(ct))
            .ToHashSet();

        var unitDtos = units.Select(u =>
        {
            var ls = lessons.Where(x => x.UnitId == u.Id).ToList();
            return new CourseBuilderUnitDto(
                u.Id, u.Order, u.Title, u.Description,
                u.Icon, u.EstimatedMinutes, u.XpReward,
                ls.Count,
                ls.Count(x => x.MaterialsPlaceholder != null),
                ls.Count(x => lessonIdsWithBlueprints.Contains(x.Id)),
                ls.Count(x => x.IsAssessment),
                u.XpReward + ls.Sum(x => x.XpReward),
                ls.Select(x => new CourseBuilderLessonDto(x.Id, x.Order, x.Title, x.Objectives,
                    x.HomeworkPlaceholder, x.MaterialsPlaceholder, x.IsAssessment,
                    x.LessonType, x.DurationMinutes, x.XpReward, exerciseCounts.GetValueOrDefault(x.Id),
                    planLessonIds.Contains(x.Id))).ToList());
        }).ToList();

        return Result<ClassCourseBuilderDto>.Ok(new ClassCourseBuilderDto(classId, templateId, templateName, unitDtos));
    }
}
