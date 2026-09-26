using LMS.Domain.Common;
using LMS.Domain.Enums;
using LMS.Domain.Exceptions;

namespace LMS.Domain.Entities;

/// <summary>
/// Attaches a course <see cref="Material"/> to a <see cref="CurriculumLesson"/>
/// under a skill <see cref="LessonMaterialSection"/> (spec #9). Because the
/// lesson lives on the shared curriculum template, every teacher of a group that
/// follows that template sees the same lesson → section → material mapping
/// (spec #10). One row per (lesson, material, section); <see cref="Order"/>
/// sequences materials within a section.
/// </summary>
public sealed class CurriculumLessonMaterial : BaseEntity
{
    private CurriculumLessonMaterial() { } // EF

    public CurriculumLessonMaterial(Guid curriculumLessonId, Guid materialId, LessonMaterialSection section, int order = 0)
    {
        if (curriculumLessonId == Guid.Empty) throw new DomainException("Lesson id is required.");
        if (materialId == Guid.Empty) throw new DomainException("Material id is required.");
        CurriculumLessonId = curriculumLessonId;
        MaterialId = materialId;
        Section = section;
        Order = order;
    }

    public Guid CurriculumLessonId { get; private set; }
    public CurriculumLesson? CurriculumLesson { get; private set; }

    public Guid MaterialId { get; private set; }
    public Material? Material { get; private set; }

    public LessonMaterialSection Section { get; private set; }
    public int Order { get; private set; }

    public void SetOrder(int order)
    {
        Order = order;
        Touch();
    }
}
