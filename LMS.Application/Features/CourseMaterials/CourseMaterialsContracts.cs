using LMS.Application.Common.Models;
using LMS.Domain.Enums;
using MediatR;

namespace LMS.Application.Features.CourseMaterials;

/// <summary>A course material attached to a curriculum lesson under a skill section (spec #9/#10).</summary>
public sealed record LessonMaterialDto(
    Guid Id,
    Guid MaterialId,
    string Title,
    string? Description,
    string OriginalFileName,
    string MimeType,
    long FileSize,
    LessonMaterialSection Section,
    int Order,
    /// <summary>Classes this attachment is restricted to. Empty = visible to every class on the course.</summary>
    IReadOnlyCollection<Guid> ClassIds);

/// <summary>A class that follows a course (curriculum template) — the targeting picker's source.</summary>
public sealed record CourseClassDto(Guid Id, string Title);

/// <summary>Classes on a course, for the per-material class-targeting picker in the builder.</summary>
public sealed record GetCourseClassesQuery(Guid CurriculumTemplateId)
    : IRequest<Result<IReadOnlyCollection<CourseClassDto>>>;

/// <summary>A material available to attach for a course — the per-lesson picker's source.</summary>
public sealed record CourseMaterialDto(
    Guid Id,
    string Title,
    string? Description,
    string OriginalFileName,
    string MimeType,
    long FileSize);

/// <summary>All materials attached to a lesson (any section), ordered by section then Order.</summary>
public sealed record GetLessonMaterialsQuery(Guid CurriculumLessonId)
    : IRequest<Result<IReadOnlyCollection<LessonMaterialDto>>>;

/// <summary>Materials belonging to a course (curriculum template) — the picker source (spec #9).</summary>
public sealed record GetCourseMaterialsQuery(Guid CurriculumTemplateId)
    : IRequest<Result<IReadOnlyCollection<CourseMaterialDto>>>;

/// <summary>
/// Attach a material to a lesson under a section. Idempotent per (lesson, material,
/// section): re-attaching updates the target classes. <paramref name="ClassIds"/>
/// empty = visible to every class on the course; otherwise restricted to those classes.
/// </summary>
public sealed record AttachLessonMaterialCommand(
    Guid CurriculumLessonId, Guid MaterialId, LessonMaterialSection Section,
    IReadOnlyCollection<Guid>? ClassIds = null)
    : IRequest<Result<LessonMaterialDto>>;

/// <summary>Remove a material from a lesson's section.</summary>
public sealed record DetachLessonMaterialCommand(Guid CurriculumLessonId, Guid MaterialId, LessonMaterialSection Section)
    : IRequest<Result<bool>>;

// ---- lesson-based navigation (spec #10) ------------------------------------

public sealed record ClassLessonMaterialItemDto(
    Guid MaterialId, string Title, string? Description, string OriginalFileName, string MimeType, long FileSize,
    LessonMaterialSection Section, int Order);

public sealed record ClassLessonDto(
    Guid LessonId, string Title, int Order, IReadOnlyCollection<ClassLessonMaterialItemDto> Materials);

public sealed record ClassLessonMaterialsDto(
    Guid ClassId, string? ClassTitle, IReadOnlyCollection<ClassLessonDto> Lessons);

/// <summary>
/// A class's lessons with their attached materials, grouped by lesson then section
/// (spec #10). For lesson-based navigation — accessible to the class's teacher(s),
/// an admin, or an enrolled student.
/// </summary>
public sealed record GetClassLessonMaterialsQuery(Guid ClassId)
    : IRequest<Result<ClassLessonMaterialsDto>>;
