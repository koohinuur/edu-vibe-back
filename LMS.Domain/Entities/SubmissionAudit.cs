using LMS.Domain.Common;
using LMS.Domain.Exceptions;

namespace LMS.Domain.Entities;

/// <summary>
/// Append-only audit trail for a submission — who did what, when. Covers the
/// anti-cheat "maintain audit logs for uploads, edits, and submissions"
/// requirement. Rows are never updated or deleted; <see cref="BaseEntity.CreatedAt"/>
/// is the authoritative timestamp.
/// </summary>
public sealed class SubmissionAudit : BaseEntity
{
    /// <summary>Must match the Detail column's HasMaxLength in EntityConfigurations.</summary>
    private const int MaxDetailLength = 1024;

    private SubmissionAudit() { } // EF

    public SubmissionAudit(Guid submissionId, Guid? actorUserId, string action, string? detail)
    {
        if (submissionId == Guid.Empty) throw new DomainException("Submission id is required.");
        if (string.IsNullOrWhiteSpace(action)) throw new DomainException("Audit action is required.");

        SubmissionId = submissionId;
        ActorUserId = actorUserId;
        Action = action;
        // The detail can be free-form (e.g. a teacher's full return reason, which the
        // student also sees in Submission.Feedback). This is only the audit trail, so
        // cap it to the column size instead of letting a long note throw on SaveChanges.
        Detail = detail is { Length: > MaxDetailLength } ? detail[..MaxDetailLength] : detail;
    }

    public Guid SubmissionId { get; private set; }

    /// <summary>The user who performed the action (student or teacher). Null for system actions.</summary>
    public Guid? ActorUserId { get; private set; }

    /// <summary>Short verb: "created", "file-uploaded", "file-deleted", "locked", "graded".</summary>
    public string Action { get; private set; } = null!;

    /// <summary>Free-text detail — e.g. the file name or score.</summary>
    public string? Detail { get; private set; }
}
