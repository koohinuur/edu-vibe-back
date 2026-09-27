using LMS.Domain.Common;
using LMS.Domain.Exceptions;

namespace LMS.Domain.Entities;

/// <summary>System-wide exam defaults (overridable per exam).</summary>
public static class ExamDefaults
{
    /// <summary>Default pass threshold (%). An exam may override via <see cref="Exam.PassThresholdPercent"/>.</summary>
    public const decimal PassThresholdPercent = 60m;
}

/// <summary>
/// F8 — an OFFLINE exam configured on a curriculum lesson of type Exam. Holds the
/// per-exam config (title, sections, pass threshold); results are entered manually
/// by a teacher (no auto-grading). 1:1 with its <see cref="CurriculumLesson"/>;
/// <see cref="ClassId"/> is the owning class (for roster + self-scope).
/// </summary>
public sealed class Exam : BaseEntity
{
    private Exam() { }

    public Exam(Guid classId, Guid curriculumLessonId, string title, decimal? passThresholdPercent)
    {
        if (classId == Guid.Empty) throw new DomainException("Class is required.");
        if (curriculumLessonId == Guid.Empty) throw new DomainException("Curriculum lesson is required.");
        ClassId = classId;
        CurriculumLessonId = curriculumLessonId;
        SetTitle(title);
        SetPassThreshold(passThresholdPercent);
    }

    public Guid ClassId { get; private set; }
    public Class? Class { get; private set; }
    public Guid CurriculumLessonId { get; private set; }
    public CurriculumLesson? CurriculumLesson { get; private set; }

    public string Title { get; private set; } = null!;
    /// <summary>Null ⇒ falls back to <see cref="ExamDefaults.PassThresholdPercent"/>.</summary>
    public decimal? PassThresholdPercent { get; private set; }

    /// <summary>
    /// The exam's type (spec #11) — e.g. "IELTS", "Pre-IELTS", "General English".
    /// Defaults from the owning group's <see cref="Class.GroupType"/> at creation.
    /// Null = untyped.
    /// </summary>
    public string? ExamType { get; private set; }

    public ICollection<ExamSection> Sections { get; } = new List<ExamSection>();

    /// <summary>The threshold actually applied — the per-exam override or the system default.</summary>
    public decimal EffectiveThresholdPercent => PassThresholdPercent ?? ExamDefaults.PassThresholdPercent;

    public void SetTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new DomainException("Exam title is required.");
        Title = title.Trim();
        Touch();
    }

    public void SetPassThreshold(decimal? percent)
    {
        if (percent is < 0m or > 100m) throw new DomainException("Pass threshold must be between 0 and 100.");
        PassThresholdPercent = percent;
        Touch();
    }

    /// <summary>Sets (or clears) the exam type. Trims; null/blank clears it. Max 64 chars.</summary>
    public void SetExamType(string? examType)
    {
        var trimmed = string.IsNullOrWhiteSpace(examType) ? null : examType.Trim();
        if (trimmed is { Length: > 64 }) throw new DomainException("Exam type must be 64 characters or fewer.");
        ExamType = trimmed;
        Touch();
    }
}

/// <summary>A configurable section of an <see cref="Exam"/> (e.g. Reading) with its own max score.</summary>
public sealed class ExamSection : BaseEntity
{
    private ExamSection() { }

    public ExamSection(Guid examId, string name, decimal maxScore, int order)
    {
        if (examId == Guid.Empty) throw new DomainException("Exam is required.");
        ExamId = examId;
        Order = order;
        SetName(name);
        SetMaxScore(maxScore);
    }

    public Guid ExamId { get; private set; }
    public Exam? Exam { get; private set; }
    public string Name { get; private set; } = null!;
    public decimal MaxScore { get; private set; }
    public int Order { get; private set; }

    public void SetName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("Section name is required.");
        Name = name.Trim();
        Touch();
    }

    public void SetMaxScore(decimal maxScore)
    {
        if (maxScore <= 0m) throw new DomainException("Section max score must be greater than zero.");
        MaxScore = maxScore;
        Touch();
    }

    public void SetOrder(int order)
    {
        Order = order;
        Touch();
    }
}
