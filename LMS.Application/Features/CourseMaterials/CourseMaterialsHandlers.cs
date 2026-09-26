using LMS.Application.Common.Abstractions;
using LMS.Application.Common.Models;
using LMS.Application.Common.Security;
using LMS.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LMS.Application.Features.CourseMaterials;

/// <summary>
/// Per-lesson course materials (spec #9/#10): attach/detach a course-scoped
/// <see cref="Material"/> to a <see cref="CurriculumLesson"/> under a skill
/// section, and read a lesson's materials or a course's material library.
///
/// Because lessons live on the shared curriculum template, the mapping is the
/// same for every teacher of a group that follows it. Authorization mirrors the
/// course builder: an admin, or a teacher of a class that follows the lesson's
/// template, may manage it. Materials offered/attached must belong to that same
/// course (Material.CurriculumTemplateId).
/// </summary>
public sealed class CourseMaterialsHandlers(IApplicationDbContext db, ICurrentUserService currentUser) :
    IRequestHandler<GetLessonMaterialsQuery, Result<IReadOnlyCollection<LessonMaterialDto>>>,
    IRequestHandler<GetCourseMaterialsQuery, Result<IReadOnlyCollection<CourseMaterialDto>>>,
    IRequestHandler<AttachLessonMaterialCommand, Result<LessonMaterialDto>>,
    IRequestHandler<DetachLessonMaterialCommand, Result<bool>>
{
    public async Task<Result<IReadOnlyCollection<LessonMaterialDto>>> Handle(
        GetLessonMaterialsQuery request, CancellationToken ct)
    {
        var templateId = await LessonTemplateAsync(request.CurriculumLessonId, ct);
        if (templateId is null) return Result<IReadOnlyCollection<LessonMaterialDto>>.Fail("NOT_FOUND", "Lesson not found.");
        if (!await CanViewCourseAsync(templateId.Value, ct))
            return Result<IReadOnlyCollection<LessonMaterialDto>>.Fail("FORBIDDEN", "You can't view this lesson's materials.");

        var items = await (
            from lm in db.CurriculumLessonMaterials
            join m in db.Materials on lm.MaterialId equals m.Id
            where lm.CurriculumLessonId == request.CurriculumLessonId
            orderby lm.Section, lm.Order, m.Title
            select new LessonMaterialDto(
                lm.Id, m.Id, m.Title, m.Description, m.OriginalFileName, m.MimeType, m.FileSize, lm.Section, lm.Order))
            .ToListAsync(ct);

        return Result<IReadOnlyCollection<LessonMaterialDto>>.Ok(items);
    }

    public async Task<Result<IReadOnlyCollection<CourseMaterialDto>>> Handle(
        GetCourseMaterialsQuery request, CancellationToken ct)
    {
        if (!await CanViewCourseAsync(request.CurriculumTemplateId, ct))
            return Result<IReadOnlyCollection<CourseMaterialDto>>.Fail("FORBIDDEN", "You can't view this course's materials.");

        var items = await db.Materials.AsNoTracking()
            .Where(m => m.CurriculumTemplateId == request.CurriculumTemplateId)
            .OrderBy(m => m.Title)
            .Select(m => new CourseMaterialDto(m.Id, m.Title, m.Description, m.OriginalFileName, m.MimeType, m.FileSize))
            .ToListAsync(ct);

        return Result<IReadOnlyCollection<CourseMaterialDto>>.Ok(items);
    }

    public async Task<Result<LessonMaterialDto>> Handle(AttachLessonMaterialCommand request, CancellationToken ct)
    {
        var templateId = await LessonTemplateAsync(request.CurriculumLessonId, ct);
        if (templateId is null) return Result<LessonMaterialDto>.Fail("NOT_FOUND", "Lesson not found.");
        if (!await CanManageCourseAsync(templateId.Value, ct))
            return Result<LessonMaterialDto>.Fail("FORBIDDEN", "Only an admin or a group's teacher can attach materials.");

        var material = await db.Materials.FirstOrDefaultAsync(m => m.Id == request.MaterialId, ct);
        if (material is null) return Result<LessonMaterialDto>.Fail("NOT_FOUND", "Material not found.");
        // Spec #9: only materials belonging to this course are selectable.
        if (material.CurriculumTemplateId != templateId)
            return Result<LessonMaterialDto>.Fail("VALIDATION", "This material doesn't belong to the lesson's course.");

        var existing = await db.CurriculumLessonMaterials.FirstOrDefaultAsync(
            x => x.CurriculumLessonId == request.CurriculumLessonId
                 && x.MaterialId == request.MaterialId && x.Section == request.Section, ct);
        if (existing is null)
        {
            var nextOrder = await db.CurriculumLessonMaterials
                .Where(x => x.CurriculumLessonId == request.CurriculumLessonId && x.Section == request.Section)
                .CountAsync(ct);
            existing = new CurriculumLessonMaterial(request.CurriculumLessonId, request.MaterialId, request.Section, nextOrder);
            await db.CurriculumLessonMaterials.AddAsync(existing, ct);
            await db.SaveChangesAsync(ct);
        }

        return Result<LessonMaterialDto>.Ok(new LessonMaterialDto(
            existing.Id, material.Id, material.Title, material.Description, material.OriginalFileName,
            material.MimeType, material.FileSize, existing.Section, existing.Order));
    }

    public async Task<Result<bool>> Handle(DetachLessonMaterialCommand request, CancellationToken ct)
    {
        var templateId = await LessonTemplateAsync(request.CurriculumLessonId, ct);
        if (templateId is null) return Result<bool>.Fail("NOT_FOUND", "Lesson not found.");
        if (!await CanManageCourseAsync(templateId.Value, ct))
            return Result<bool>.Fail("FORBIDDEN", "Only an admin or a group's teacher can remove materials.");

        var row = await db.CurriculumLessonMaterials.FirstOrDefaultAsync(
            x => x.CurriculumLessonId == request.CurriculumLessonId
                 && x.MaterialId == request.MaterialId && x.Section == request.Section, ct);
        if (row is not null)
        {
            db.CurriculumLessonMaterials.Remove(row);
            await db.SaveChangesAsync(ct);
        }
        return Result<bool>.Ok(true);
    }

    // ----- helpers ---------------------------------------------------------

    /// <summary>The template (course) a curriculum lesson belongs to, via unit → module.</summary>
    private Task<Guid?> LessonTemplateAsync(Guid lessonId, CancellationToken ct) =>
        (from l in db.CurriculumLessons
         join u in db.CurriculumUnits on l.UnitId equals u.Id
         join m in db.CurriculumModules on u.ModuleId equals m.Id
         where l.Id == lessonId
         select (Guid?)m.TemplateId).FirstOrDefaultAsync(ct);

    /// <summary>Admin, or a teacher of any class that follows this course — may edit its materials.</summary>
    private async Task<bool> CanManageCourseAsync(Guid templateId, CancellationToken ct)
    {
        if (currentUser.IsAdmin()) return true;
        if (currentUser.UserId is not { } uid) return false;
        return await db.Classes.AnyAsync(c => c.CurriculumTemplateId == templateId
            && (c.TeacherUserId == uid || db.ClassTeachers.Any(t => t.ClassId == c.Id && t.UserId == uid)), ct);
    }

    /// <summary>Viewing is open to a manager or any student enrolled in a class on this course.</summary>
    private async Task<bool> CanViewCourseAsync(Guid templateId, CancellationToken ct)
    {
        if (await CanManageCourseAsync(templateId, ct)) return true;
        if (currentUser.StudentProfileId is not { } spid) return false;
        return await db.Enrollments.AnyAsync(e => e.StudentProfileId == spid
            && db.Classes.Any(c => c.Id == e.ClassId && c.CurriculumTemplateId == templateId), ct);
    }
}
