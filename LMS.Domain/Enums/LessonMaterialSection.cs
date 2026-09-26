namespace LMS.Domain.Enums;

/// <summary>
/// The skill section a lesson's material belongs to (spec #9). Mirrors the four
/// IELTS papers, plus a catch-all for anything skill-agnostic. The ordinals are
/// fixed — the frontend depends on them.
/// </summary>
public enum LessonMaterialSection
{
    Reading = 1,
    Listening = 2,
    Writing = 3,
    Speaking = 4,
    /// <summary>Skill-agnostic material (handouts, syllabus, mixed content).</summary>
    General = 5,
}
