using LMS.Domain.Common;
using LMS.Domain.Exceptions;

namespace LMS.Domain.Entities;

/// <summary>
/// F8 — a single student's result for an <see cref="Exam"/>, entered manually by a
/// teacher. One per (Exam, Student) — re-entering corrects in place (idempotent
/// upsert). <see cref="OverallPercent"/> and <see cref="Passed"/> are computed by
/// the handler from the section scores + the exam's effective threshold.
/// </summary>
public sealed class ExamResult : BaseEntity
{
    private ExamResult() { }

    public ExamResult(Guid examId, Guid studentProfileId, Guid enteredByUserId)
    {
        if (examId == Guid.Empty) throw new DomainException("Exam is required.");
        if (studentProfileId == Guid.Empty) throw new DomainException("Student is required.");
        ExamId = examId;
        StudentProfileId = studentProfileId;
        EnteredByUserId = enteredByUserId;
    }

    public Guid ExamId { get; private set; }
    public Exam? Exam { get; private set; }
    public Guid StudentProfileId { get; private set; }
    public StudentProfile? StudentProfile { get; private set; }

    public decimal OverallPercent { get; private set; }
    public bool Passed { get; private set; }
    public Guid EnteredByUserId { get; private set; }
    public DateTime EnteredAt { get; private set; }

    /// <summary>
    /// Result visibility gate (spec #12): a student sees this result only after a
    /// teacher explicitly publishes it. False until published; re-entering scores
    /// does NOT change it (a teacher revising before/after publish is intentional).
    /// </summary>
    public bool IsPublished { get; private set; }
    public DateTime? PublishedAt { get; private set; }

    public ICollection<ExamSectionScore> SectionScores { get; } = new List<ExamSectionScore>();

    /// <summary>Records the computed outcome + who/when. Re-callable for idempotent correction.</summary>
    public void Record(decimal overallPercent, bool passed, Guid enteredByUserId, DateTime now)
    {
        OverallPercent = overallPercent;
        Passed = passed;
        EnteredByUserId = enteredByUserId;
        EnteredAt = now;
        Touch();
    }

    /// <summary>Unlocks the result for the student to see.</summary>
    public void Publish(DateTime now)
    {
        if (IsPublished) return;
        IsPublished = true;
        PublishedAt = now;
        Touch();
    }

    /// <summary>Hides the result from the student again.</summary>
    public void Unpublish()
    {
        if (!IsPublished) return;
        IsPublished = false;
        PublishedAt = null;
        Touch();
    }
}

/// <summary>One section's raw score within an <see cref="ExamResult"/>. Max is validated against the section.</summary>
public sealed class ExamSectionScore : BaseEntity
{
    private ExamSectionScore() { }

    public ExamSectionScore(Guid examResultId, Guid examSectionId, decimal score)
    {
        if (examResultId == Guid.Empty) throw new DomainException("Exam result is required.");
        if (examSectionId == Guid.Empty) throw new DomainException("Exam section is required.");
        if (score < 0m) throw new DomainException("Score cannot be negative.");
        ExamResultId = examResultId;
        ExamSectionId = examSectionId;
        Score = score;
    }

    public Guid ExamResultId { get; private set; }
    public ExamResult? ExamResult { get; private set; }
    public Guid ExamSectionId { get; private set; }
    public ExamSection? ExamSection { get; private set; }
    public decimal Score { get; private set; }

    /// <summary>Teacher's written feedback for this section (spec #12). Null = none.</summary>
    public string? Feedback { get; private set; }

    public void SetScore(decimal score)
    {
        if (score < 0m) throw new DomainException("Score cannot be negative.");
        Score = score;
        Touch();
    }

    /// <summary>Sets (or clears) this section's feedback. Trims; null/blank clears. Max 4000 chars.</summary>
    public void SetFeedback(string? feedback)
    {
        var trimmed = string.IsNullOrWhiteSpace(feedback) ? null : feedback.Trim();
        if (trimmed is { Length: > 4000 }) throw new DomainException("Feedback must be 4000 characters or fewer.");
        Feedback = trimmed;
        Touch();
    }
}
