using LMS.Domain.Common;
using LMS.Domain.Exceptions;

namespace LMS.Domain.Entities;

/// <summary>
/// Targets a <see cref="CurriculumLessonMaterial"/> (a material attached to a
/// lesson section) at a specific <see cref="Class"/>. When a lesson-material has
/// NO target rows it is visible to every class on the course (the default);
/// adding rows restricts it to exactly those classes. Its presence is the grant.
/// </summary>
public sealed class CurriculumLessonMaterialClass : BaseEntity
{
    private CurriculumLessonMaterialClass() { } // EF

    public CurriculumLessonMaterialClass(Guid curriculumLessonMaterialId, Guid classId)
    {
        if (curriculumLessonMaterialId == Guid.Empty) throw new DomainException("Lesson material id is required.");
        if (classId == Guid.Empty) throw new DomainException("Class id is required.");
        CurriculumLessonMaterialId = curriculumLessonMaterialId;
        ClassId = classId;
    }

    public Guid CurriculumLessonMaterialId { get; private set; }
    public CurriculumLessonMaterial? LessonMaterial { get; private set; }

    public Guid ClassId { get; private set; }
    public Class? Class { get; private set; }
}
