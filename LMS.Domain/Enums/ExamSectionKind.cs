namespace LMS.Domain.Enums;

/// <summary>
/// The IELTS paper an exam section represents (spec: real IELTS format). Drives
/// how the student takes it: Listening/Reading render an uploaded self-contained
/// HTML test (+ audio for Listening); Writing/Speaking show prompts the student
/// answers in a textarea. Ordinals are fixed — the frontend depends on them.
/// </summary>
public enum ExamSectionKind
{
    /// <summary>Skill-agnostic / not one of the four papers (default for legacy sections).</summary>
    Generic = 0,
    Listening = 1,
    Reading = 2,
    Writing = 3,
    Speaking = 4,
}
