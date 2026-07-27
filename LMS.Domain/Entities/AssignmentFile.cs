using LMS.Domain.Common;
using LMS.Domain.Exceptions;

namespace LMS.Domain.Entities;

/// <summary>
/// One worksheet file a teacher attaches to an <see cref="Assignment"/> for students to
/// download (a PDF/doc/slide task sheet). An assignment can carry many. The blob lives in
/// the material file store; this row holds the metadata + the opaque stored name. Distinct
/// from <see cref="SubmissionFile"/> (the student's uploaded answer, which is SHA-hashed for
/// anti-cheat) — worksheets need no hashing.
/// </summary>
public sealed class AssignmentFile : BaseEntity
{
    private AssignmentFile() { } // EF

    public AssignmentFile(
        Guid assignmentId,
        string storedFileName,
        string originalFileName,
        string mimeType,
        long fileSize)
    {
        if (assignmentId == Guid.Empty) throw new DomainException("Assignment id is required.");
        if (string.IsNullOrWhiteSpace(storedFileName)) throw new DomainException("Stored file name is required.");

        AssignmentId = assignmentId;
        StoredFileName = storedFileName;
        OriginalFileName = string.IsNullOrWhiteSpace(originalFileName) ? storedFileName : originalFileName.Trim();
        MimeType = string.IsNullOrWhiteSpace(mimeType) ? "application/octet-stream" : mimeType;
        FileSize = fileSize;
    }

    public Guid AssignmentId { get; private set; }
    public Assignment? Assignment { get; private set; }

    public string StoredFileName { get; private set; } = null!;
    public string OriginalFileName { get; private set; } = null!;
    public string MimeType { get; private set; } = null!;
    public long FileSize { get; private set; }
}
