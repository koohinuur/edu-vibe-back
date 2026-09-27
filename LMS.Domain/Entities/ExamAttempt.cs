using LMS.Domain.Common;
using LMS.Domain.Exceptions;

namespace LMS.Domain.Entities;

/// <summary>
/// A student's sitting of an <see cref="Exam"/> in real IELTS format. Records when
/// they started and submitted, plus their per-section responses (Writing/Speaking
/// essays; a self-reported auto-score for the HTML Listening/Reading tests). The
/// official band is still entered + published by a teacher (spec #12) — this is
/// the raw sitting, not the graded result. One (open) attempt per (exam, student).
/// </summary>
public sealed class ExamAttempt : BaseEntity
{
    private ExamAttempt() { }

    public ExamAttempt(Guid examId, Guid studentProfileId, DateTime now)
    {
        if (examId == Guid.Empty) throw new DomainException("Exam is required.");
        if (studentProfileId == Guid.Empty) throw new DomainException("Student is required.");
        ExamId = examId;
        StudentProfileId = studentProfileId;
        StartedAt = now;
    }

    public Guid ExamId { get; private set; }
    public Exam? Exam { get; private set; }
    public Guid StudentProfileId { get; private set; }
    public StudentProfile? StudentProfile { get; private set; }

    public DateTime StartedAt { get; private set; }
    public DateTime? SubmittedAt { get; private set; }
    public bool IsSubmitted => SubmittedAt is not null;

    public ICollection<ExamSectionResponse> Responses { get; } = new List<ExamSectionResponse>();

    public void Submit(DateTime now)
    {
        if (SubmittedAt is not null) return;
        SubmittedAt = now;
        Touch();
    }
}

/// <summary>
/// One section's answer within an <see cref="ExamAttempt"/>: the student's written
/// response (Writing/Speaking) and/or a self-reported score the HTML test computed
/// (Listening/Reading). Advisory only — the teacher enters the official band.
/// </summary>
public sealed class ExamSectionResponse : BaseEntity
{
    private ExamSectionResponse() { }

    public ExamSectionResponse(Guid examAttemptId, Guid examSectionId, string? responseText, decimal? selfScore)
    {
        if (examAttemptId == Guid.Empty) throw new DomainException("Attempt is required.");
        if (examSectionId == Guid.Empty) throw new DomainException("Section is required.");
        ExamAttemptId = examAttemptId;
        ExamSectionId = examSectionId;
        SetResponse(responseText, selfScore);
    }

    public Guid ExamAttemptId { get; private set; }
    public ExamAttempt? ExamAttempt { get; private set; }
    public Guid ExamSectionId { get; private set; }
    public ExamSection? ExamSection { get; private set; }

    public string? ResponseText { get; private set; }
    public decimal? SelfScore { get; private set; }

    public void SetResponse(string? responseText, decimal? selfScore)
    {
        var text = string.IsNullOrWhiteSpace(responseText) ? null : responseText.Trim();
        if (text is { Length: > 50_000 }) throw new DomainException("Response is too long.");
        ResponseText = text;
        if (selfScore is < 0m) throw new DomainException("Self score can't be negative.");
        SelfScore = selfScore;
        Touch();
    }
}
