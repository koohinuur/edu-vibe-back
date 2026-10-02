using LMS.Domain.Common;
using LMS.Domain.Enums;
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

    public Exam(Guid classId, Guid? curriculumLessonId, string title, decimal? passThresholdPercent)
    {
        if (classId == Guid.Empty) throw new DomainException("Class is required.");
        ClassId = classId;
        // Null = a standalone exam (e.g. an IELTS mock) not tied to a curriculum lesson.
        CurriculumLessonId = curriculumLessonId == Guid.Empty ? null : curriculumLessonId;
        SetTitle(title);
        SetPassThreshold(passThresholdPercent);
    }

    public Guid ClassId { get; private set; }
    public Class? Class { get; private set; }
    /// <summary>The exam-type curriculum lesson this exam sits on, or null for a standalone exam.</summary>
    public Guid? CurriculumLessonId { get; private set; }
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

    /// <summary>Which IELTS paper this section is — drives how the student takes it.</summary>
    public ExamSectionKind Kind { get; private set; } = ExamSectionKind.Generic;

    /// <summary>
    /// For Listening/Reading: the self-contained HTML test rendered in a sandboxed
    /// iframe. Null = no HTML (e.g. a Writing section that only has a prompt).
    /// </summary>
    public string? ContentHtml { get; private set; }

    /// <summary>For Listening: the audio track URL the test plays. Null = none.</summary>
    public string? AudioUrl { get; private set; }

    /// <summary>For Writing/Speaking: the task prompt the student answers. Null = none.</summary>
    public string? Prompt { get; private set; }

    /// <summary>
    /// For Writing (e.g. IELTS Task 1) / Speaking: an image the student describes —
    /// a chart, diagram or cue card. URL to the uploaded image. Null = none.
    /// </summary>
    public string? ImageUrl { get; private set; }

    /// <summary>
    /// For Writing/Speaking with multiple tasks (e.g. IELTS Task 1 + Task 2): a JSON
    /// array of tasks — [{ "title", "prompt", "imageUrl" }]. Null/empty = the section
    /// is a single task (uses <see cref="Prompt"/> / <see cref="ImageUrl"/>).
    /// </summary>
    public string? TasksJson { get; private set; }

    /// <summary>Suggested time for this section, in minutes. Null = not set.</summary>
    public int? DurationMinutes { get; private set; }

    /// <summary>
    /// For a Speaking section: how it's taken — the student records their answer
    /// (<see cref="SpeakingMode.CueCard"/>, the default) or it's done live on Zoom/Meet
    /// (<see cref="SpeakingMode.Live"/>). Ignored for other kinds.
    /// </summary>
    public SpeakingMode SpeakingMode { get; private set; } = SpeakingMode.CueCard;

    public void SetName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("Section name is required.");
        Name = name.Trim();
        Touch();
    }

    /// <summary>
    /// Sets the section's kind + take-content. HTML is capped to keep rows sane.
    /// <paramref name="contentHtml"/> is <c>null</c> ⇒ keep the existing HTML (so an
    /// edit that doesn't re-upload preserves it), empty ⇒ clear, non-empty ⇒ replace.
    /// </summary>
    public void SetContent(ExamSectionKind kind, string? contentHtml, string? audioUrl, string? prompt, int? durationMinutes, string? imageUrl = null)
    {
        Kind = kind;
        if (contentHtml is not null)
        {
            var html = contentHtml.Trim().Length == 0 ? null : contentHtml;
            if (html is { Length: > 2_000_000 }) throw new DomainException("Section HTML is too large (2 MB max).");
            ContentHtml = html;
        }
        AudioUrl = string.IsNullOrWhiteSpace(audioUrl) ? null : audioUrl.Trim();
        Prompt = string.IsNullOrWhiteSpace(prompt) ? null : prompt.Trim();
        ImageUrl = string.IsNullOrWhiteSpace(imageUrl) ? null : imageUrl.Trim();
        if (durationMinutes is < 0) throw new DomainException("Duration can't be negative.");
        DurationMinutes = durationMinutes;
        Touch();
    }

    /// <summary>Sets how a Speaking section is taken (cue-card recording vs live).</summary>
    public void SetSpeakingMode(SpeakingMode mode)
    {
        SpeakingMode = mode;
        Touch();
    }

    /// <summary>Sets the multi-task JSON ([] / null clears to a single-task section). Max 200 KB.</summary>
    public void SetTasks(string? tasksJson)
    {
        var json = string.IsNullOrWhiteSpace(tasksJson) || tasksJson.Trim() is "[]" ? null : tasksJson.Trim();
        if (json is { Length: > 200_000 }) throw new DomainException("Too many/large tasks.");
        TasksJson = json;
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
