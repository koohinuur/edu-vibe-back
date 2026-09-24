using LMS.Domain.Common;
using LMS.Domain.Enums;
using LMS.Domain.Exceptions;

namespace LMS.Domain.Entities;

public sealed class Submission : BaseEntity
{
    private Submission(Guid assignmentId, Guid studentProfileId, string? content)
    {
        if (assignmentId == Guid.Empty || studentProfileId == Guid.Empty)
            throw new DomainException("Assignment and student profile ids are required.");

        AssignmentId = assignmentId;
        StudentProfileId = studentProfileId;
        // Content is optional now — a submission can be files-only. We keep the
        // column NOT NULL (empty string) to avoid a nullable migration.
        Content = content?.Trim() ?? string.Empty;
        Status = SubmissionStatus.Submitted;
    }

    public Guid AssignmentId { get; }
    public Assignment? Assignment { get; private set; }

    public Guid StudentProfileId { get; }
    public StudentProfile? StudentProfile { get; private set; }

    public string Content { get; private set; }
    public SubmissionStatus Status { get; private set; }
    public decimal? Score { get; private set; }

    /// <summary>The scale the teacher graded on (e.g. 10 for "8/10", or a band). Null ⇒ raw score / not set.</summary>
    public decimal? MaxScore { get; private set; }

    /// <summary>Teacher's written feedback the student sees with the grade (e.g. on a writing task).</summary>
    public string? Feedback { get; private set; }

    /// <summary>
    /// When true the student can no longer add or remove files — either the
    /// student finalised it, or a teacher locked it. Grading still works.
    /// </summary>
    public bool IsLocked { get; private set; }

    /// <summary>Uploaded files attached to this submission (anti-cheat: each carries a sha256).</summary>
    public ICollection<SubmissionFile> Files { get; } = new List<SubmissionFile>();

    public static Submission Create(Guid assignmentId, Guid studentProfileId, string? content,
        IEnumerable<Submission> existingSubmissions)
    {
        if (existingSubmissions.Any(x => x.AssignmentId == assignmentId && x.StudentProfileId == studentProfileId))
            throw new DomainException("Only one submission is allowed per student per assignment.");

        return new Submission(assignmentId, studentProfileId, content);
    }

    /// <summary>Updates the text content. Empty is allowed for files-only submissions.</summary>
    public void Submit(string? content, bool isLate = false)
    {
        if (IsLocked) throw new DomainException("Submission is locked and can no longer be changed.");
        Content = content?.Trim() ?? string.Empty;
        Status = isLate ? SubmissionStatus.Late : SubmissionStatus.Submitted;
        Touch();
    }

    public void Grade(decimal score, decimal? maxScore = null, string? feedback = null)
    {
        if (score < 0) throw new DomainException("Score cannot be negative.");
        if (maxScore is { } max)
        {
            if (max <= 0) throw new DomainException("Max score must be greater than zero.");
            if (score > max) throw new DomainException("Score cannot exceed the max score.");
        }

        Score = score;
        MaxScore = maxScore;
        Feedback = string.IsNullOrWhiteSpace(feedback) ? null : feedback.Trim();
        Status = SubmissionStatus.Graded;
        Touch();
    }

    /// <summary>
    /// Sends the work back for a redo: unlocks it so the student can resubmit,
    /// clears the previous grade (it's a fresh attempt), and keeps the teacher's
    /// note as feedback. The student's next Submit() flips it back to Submitted.
    /// </summary>
    public void ReturnForRedo(string? feedback = null)
    {
        Status = SubmissionStatus.Returned;
        IsLocked = false;
        Score = null;
        MaxScore = null;
        if (!string.IsNullOrWhiteSpace(feedback)) Feedback = feedback.Trim();
        Touch();
    }

    /// <summary>Marks the submission late (kept distinct from a re-submit).</summary>
    public void MarkLate()
    {
        if (Status != SubmissionStatus.Graded) Status = SubmissionStatus.Late;
        Touch();
    }

    /// <summary>Locks the submission — student can no longer add/remove files.</summary>
    public void Lock()
    {
        IsLocked = true;
        Touch();
    }

    /// <summary>Unlocks the submission (teacher-only path, e.g. to allow a resubmit).</summary>
    public void Unlock()
    {
        IsLocked = false;
        Touch();
    }
}
