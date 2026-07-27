using LMS.Application.Common.Models;
using LMS.Domain.Enums;
using MediatR;

namespace LMS.Application.Features.Assignments;

public sealed record AssignmentDto(
    Guid Id,
    Guid ClassId,
    string Title,
    AssignmentStatus Status,
    Guid CreatedByTeacherId,
    DateTime? DueDate,
    string? Description = null);

public sealed record CreateAssignmentCommand(
    Guid ClassId, string Title, Guid TeacherUserId, DateTime? DueDate = null, string? Description = null)
    : IRequest<Result<AssignmentDto>>;

public sealed record UpdateAssignmentCommand(
    Guid AssignmentId, string Title, DateTime? DueDate = null, string? Description = null)
    : IRequest<Result<AssignmentDto>>;

public sealed record PublishAssignmentCommand(Guid AssignmentId) : IRequest<Result<AssignmentDto>>;

public sealed record CloseAssignmentCommand(Guid AssignmentId) : IRequest<Result<AssignmentDto>>;

public sealed record GetClassAssignmentsQuery(Guid ClassId) : IRequest<Result<IReadOnlyCollection<AssignmentDto>>>;

public sealed record GetStudentAssignmentsQuery(Guid StudentProfileId)
    : IRequest<Result<IReadOnlyCollection<AssignmentDto>>>;

/// <summary>
/// Lists assignments, optionally filtered. Used by the teacher Homework page and admin views.
/// </summary>
public sealed record GetAssignmentsQuery(
    Guid? TeacherUserId = null,
    Guid? ClassId = null,
    AssignmentStatus? Status = null)
    : IRequest<Result<IReadOnlyCollection<AssignmentDto>>>;

// ----- Book attachments ---------------------------------------------------

public sealed record AssignmentBookDto(Guid Id, Guid BookId, string? Note);

public sealed record AttachBookToAssignmentCommand(Guid AssignmentId, Guid BookId, string? Note)
    : IRequest<Result<AssignmentBookDto>>;

public sealed record DetachBookFromAssignmentCommand(Guid AssignmentId, Guid BookId)
    : IRequest<Result>;

public sealed record GetAssignmentBooksQuery(Guid AssignmentId)
    : IRequest<Result<IReadOnlyCollection<AssignmentBookDto>>>;

// ----- Per-student targeting ---------------------------------------------

public sealed record AssignmentAssigneeDto(Guid AssignmentId, Guid StudentProfileId);

/// <summary>
/// Replace the assignee set on an assignment. Empty list = whole class
/// (the implicit default). Use for "assign to selected students" workflows.
/// </summary>
public sealed record SetAssignmentAssigneesCommand(
    Guid AssignmentId,
    IReadOnlyCollection<Guid> StudentProfileIds)
    : IRequest<Result<IReadOnlyCollection<AssignmentAssigneeDto>>>;

public sealed record GetAssignmentAssigneesQuery(Guid AssignmentId)
    : IRequest<Result<IReadOnlyCollection<AssignmentAssigneeDto>>>;

// ----- Worksheet files -----------------------------------------------------

public sealed record AssignmentFileDto(
    Guid Id, Guid AssignmentId, string OriginalFileName, string MimeType, long FileSize, DateTime CreatedAt);

public sealed record AssignmentFileDownloadDto(string StoredFileName, string OriginalFileName, string MimeType);

public sealed record AddAssignmentFileCommand(
    Guid AssignmentId, string StoredFileName, string OriginalFileName, string MimeType, long FileSize)
    : IRequest<Result<AssignmentFileDto>>;

/// <summary>Removes a worksheet file. Returns the stored name so the controller deletes the blob.</summary>
public sealed record RemoveAssignmentFileCommand(Guid AssignmentId, Guid FileId) : IRequest<Result<string>>;

public sealed record GetAssignmentFilesQuery(Guid AssignmentId)
    : IRequest<Result<IReadOnlyCollection<AssignmentFileDto>>>;

public sealed record GetAssignmentFileForDownloadQuery(Guid FileId)
    : IRequest<Result<AssignmentFileDownloadDto>>;