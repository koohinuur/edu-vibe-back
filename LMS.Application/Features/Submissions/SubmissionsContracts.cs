using LMS.Application.Common.Models;
using LMS.Domain.Enums;
using MediatR;

namespace LMS.Application.Features.Submissions;

public sealed record SubmissionDto(
    Guid Id,
    Guid AssignmentId,
    Guid StudentProfileId,
    string Content,
    SubmissionStatus Status,
    decimal? Score,
    bool IsLocked,
    int FileCount,
    // Teacher grade detail: the scale graded on and any written feedback (both null
    // until a teacher grades with them). Score alone stays back-compatible.
    decimal? MaxScore = null,
    string? Feedback = null);

public sealed record SubmissionFileDto(
    Guid Id,
    Guid SubmissionId,
    string OriginalFileName,
    string MimeType,
    long FileSize,
    bool IsDuplicateAcrossStudents,
    DateTime CreatedAt);

/// <summary>Lean shape for streaming a submission file back to the caller.</summary>
public sealed record SubmissionFileDownloadDto(string StoredFileName, string OriginalFileName, string MimeType);

public sealed record SubmissionAuditDto(
    Guid Id,
    Guid SubmissionId,
    Guid? ActorUserId,
    string Action,
    string? Detail,
    DateTime CreatedAt);

public sealed record SubmitAssignmentCommand(Guid AssignmentId, Guid StudentProfileId, string Content, bool IsLate)
    : IRequest<Result<SubmissionDto>>;

/// <summary>
/// Auto-saves the student's draft answer text WITHOUT finalising. Upserts the
/// caller's submission (creating it on first save) and leaves it editable
/// (unlocked) until the student finalises or a teacher locks it. Unlike
/// <see cref="SubmitAssignmentCommand"/> it writes no per-save audit row (an
/// autosave would otherwise flood the trail) — the create and the finalize are
/// still audited. Student profile is always the caller's; never trusted from
/// the wire.
/// </summary>
public sealed record SaveSubmissionDraftCommand(Guid AssignmentId, string Content)
    : IRequest<Result<SubmissionDto>>;

public sealed record GradeSubmissionCommand(
    Guid SubmissionId, decimal Score, decimal? MaxScore = null, string? Feedback = null)
    : IRequest<Result<SubmissionDto>>;

public sealed record GetAssignmentSubmissionsQuery(Guid AssignmentId)
    : IRequest<Result<IReadOnlyCollection<SubmissionDto>>>;

public sealed record GetStudentSubmissionsQuery(Guid StudentProfileId)
    : IRequest<Result<IReadOnlyCollection<SubmissionDto>>>;

// ---- File submissions + anti-cheat ----------------------------------------

/// <summary>
/// Attaches an already-stored file to the caller's submission for an
/// assignment, creating the submission if it doesn't exist yet. The
/// controller does the multipart read + disk write (which yields the
/// sha/size); this command applies every access + anti-cheat rule and
/// records the row. Returns the new file, or a failure the controller maps
/// to an HTTP status (and uses to delete the orphaned blob).
/// </summary>
public sealed record AddSubmissionFileCommand(
    Guid AssignmentId,
    string StoredFileName,
    string OriginalFileName,
    string MimeType,
    long FileSize,
    string Sha256) : IRequest<Result<SubmissionFileDto>>;

/// <summary>Removes a file from the caller's own submission. Returns the stored name so the controller deletes the blob.</summary>
public sealed record RemoveSubmissionFileCommand(Guid SubmissionId, Guid FileId) : IRequest<Result<string>>;

public sealed record GetSubmissionFilesQuery(Guid SubmissionId)
    : IRequest<Result<IReadOnlyCollection<SubmissionFileDto>>>;

public sealed record GetSubmissionFileForDownloadQuery(Guid FileId)
    : IRequest<Result<SubmissionFileDownloadDto>>;

/// <summary>Student finalises their own submission (locks it). Teacher uses <see cref="SetSubmissionLockCommand"/>.</summary>
public sealed record FinalizeSubmissionCommand(Guid SubmissionId) : IRequest<Result<SubmissionDto>>;

/// <summary>Teacher locks / unlocks a submission (staff-only).</summary>
public sealed record SetSubmissionLockCommand(Guid SubmissionId, bool Locked) : IRequest<Result<SubmissionDto>>;

/// <summary>Teacher returns a submission for the student to redo + resubmit (unlocks it).</summary>
public sealed record ReturnSubmissionCommand(Guid SubmissionId, string? Feedback) : IRequest<Result<SubmissionDto>>;

public sealed record GetSubmissionAuditQuery(Guid SubmissionId)
    : IRequest<Result<IReadOnlyCollection<SubmissionAuditDto>>>;