using LMS.Domain.Common;
using LMS.Domain.Enums;
using LMS.Domain.Exceptions;

namespace LMS.Domain.Entities;

public sealed class Assignment : BaseEntity
{
    // EF materialisation constructor — fields are populated reflectively by
    // EF Core when reading from the database. Public callers must use the
    // parameterised ctor below, which enforces all invariants.
    private Assignment()
    {
    }
    public Assignment(Guid classId, string title, User createdByTeacher)
    {
        if (classId == Guid.Empty) throw new DomainException("Class id is required.");

        if (string.IsNullOrWhiteSpace(title)) throw new DomainException("Assignment title is required.");

        if (createdByTeacher is null) throw new DomainException("Teacher is required.");

        ClassId = classId;
        Title = title.Trim();
        CreatedByTeacherId = createdByTeacher.Id;
        Status = AssignmentStatus.Draft;
    }

    public Guid ClassId { get; private set; }
    public Class? Class { get; private set; }

    // null! signals to the nullable analyser that the EF materialisation
    // path will populate this — the public ctor always sets it before the
    // entity is observable.
    public string Title { get; private set; } = null!;

    /// <summary>
    /// Optional instructions/description shown to students alongside the title.
    /// Null or empty = title-only assignment. Capped at 4000 chars.
    /// </summary>
    public string? Description { get; private set; }

    public AssignmentStatus Status { get; private set; }

    /// <summary>
    /// Optional submission deadline (UTC). When set, students can't add or
    /// remove submission files after it passes, and a submission made after
    /// the deadline is flagged Late. Null = no deadline.
    /// </summary>
    public DateTime? DueDate { get; private set; }

    public Guid CreatedByTeacherId { get; private set; }
    public User? CreatedByTeacher { get; private set; }

    /// <summary>
    /// Optional link to a specific lesson (ClassSession) within the class. When
    /// set, the assignment shows on that lesson's hub. Null = a general
    /// class-level assignment not tied to one lesson.
    /// </summary>
    public Guid? ClassSessionId { get; private set; }

    /// <summary>
    /// Optional provenance link to the curriculum lesson this assignment was
    /// materialised from. With multi-lesson class days (1A+1B) a session can spawn
    /// one assignment per lesson, so this keeps each separately reconcilable when a
    /// teacher changes the day's lesson set. Null = not curriculum-derived.
    /// </summary>
    public Guid? CurriculumLessonId { get; private set; }

    public ICollection<Submission> Submissions { get; } = new List<Submission>();

    /// <summary>Links/unlinks this assignment to a lesson. Pass null to unlink.</summary>
    public void SetSession(Guid? classSessionId)
    {
        ClassSessionId = classSessionId;
        Touch();
    }

    /// <summary>Sets/clears the curriculum-lesson provenance. Pass null to clear.</summary>
    public void SetCurriculumLesson(Guid? curriculumLessonId)
    {
        CurriculumLessonId = curriculumLessonId;
        Touch();
    }

    public void UpdateTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new DomainException("Assignment title is required.");
        Title = title.Trim();
        Touch();
    }

    /// <summary>Sets or clears the instructions. Trims; null/blank clears it.</summary>
    public void SetDescription(string? description)
    {
        var trimmed = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (trimmed is { Length: > 4000 })
            throw new DomainException("Assignment description must be 4000 characters or fewer.");
        Description = trimmed;
        Touch();
    }

    /// <summary>Sets or clears the submission deadline. Pass null to remove it.</summary>
    public void SetDueDate(DateTime? dueDateUtc)
    {
        DueDate = dueDateUtc;
        Touch();
    }

    /// <summary>True when a deadline is set and now is past it.</summary>
    public bool IsPastDue(DateTime nowUtc) => DueDate is { } due && nowUtc > due;

    public void Publish()
    {
        if (Status != AssignmentStatus.Draft) throw new DomainException("Only draft assignment can be published.");

        Status = AssignmentStatus.Published;
        Touch();
    }

    public void Close()
    {
        if (Status == AssignmentStatus.Closed) throw new DomainException("Assignment is already closed.");

        Status = AssignmentStatus.Closed;
        Touch();
    }

    /// <summary>Reopens a closed assignment back to Published so students can submit again.</summary>
    public void Reopen()
    {
        if (Status != AssignmentStatus.Closed) throw new DomainException("Only a closed assignment can be reopened.");

        Status = AssignmentStatus.Published;
        Touch();
    }
}