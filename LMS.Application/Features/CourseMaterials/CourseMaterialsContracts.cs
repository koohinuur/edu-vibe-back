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
    int Order);

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

/// <summary>Attach a course material to a lesson under a section. Idempotent per (lesson, material, section).</summary>
public sealed record AttachLessonMaterialCommand(Guid CurriculumLessonId, Guid MaterialId, LessonMaterialSection Section)
    : IRequest<Result<LessonMaterialDto>>;

/// <summary>Remove a material from a lesson's section.</summary>
public sealed record DetachLessonMaterialCommand(Guid CurriculumLessonId, Guid MaterialId, LessonMaterialSection Section)
    : IRequest<Result<bool>>;
