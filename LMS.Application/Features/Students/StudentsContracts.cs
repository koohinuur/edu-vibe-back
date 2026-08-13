using LMS.Application.Common.Models;
using LMS.Domain.Enums;
using MediatR;

namespace LMS.Application.Features.Students;

public sealed record StudentDto(
    Guid StudentProfileId,
    Guid UserId,
    string Email,
    int Xp,
    int Streak,
    string? FirstName,
    string? LastName,
    string? PhoneNumber,
    string? Description,
    string? ParentPhoneNumber,
    string? Level,
    string? AvatarUrl,
    UserStatus Status);

public sealed record SetStudentStatusCommand(Guid StudentProfileId, UserStatus Status)
    : IRequest<Result<StudentDto>>;

public sealed record UpdateStudentAdminFieldsCommand(
    Guid StudentProfileId,
    string? ParentPhoneNumber,
    string? Level) : IRequest<Result<StudentDto>>;

public sealed record SetStudentAvatarCommand(Guid StudentProfileId, string? AvatarUrl)
    : IRequest<Result<StudentDto>>;

public sealed record RegisterStudentCommand(Guid UserId) : IRequest<Result<StudentDto>>;

public sealed record UpdateStudentProfileCommand(Guid StudentProfileId, int Xp, int Streak)
    : IRequest<Result<StudentDto>>;

/// <summary>
/// Updates the editable profile fields (name, phone, description). XP and
/// streak are separate via <see cref="UpdateStudentProfileCommand"/>.
/// </summary>
public sealed record UpdateStudentDetailsCommand(
    Guid StudentProfileId,
    string? FirstName,
    string? LastName,
    string? PhoneNumber,
    string? Description) : IRequest<Result<StudentDto>>;

/// <summary>
/// Self-edit variant of <see cref="UpdateStudentDetailsCommand"/>. The
/// target student profile is resolved from the caller's JWT, so no profile
/// id is accepted from the body (rules out IDOR). Used by the student-panel
/// Settings page — controller gate is plain [Authorize].
/// </summary>
public sealed record UpdateMyStudentDetailsCommand(
    string? FirstName,
    string? LastName,
    string? PhoneNumber,
    string? Description) : IRequest<Result<StudentDto>>;

public sealed record GetStudentsQuery(int Page = 1, int PageSize = 25, string? Search = null)
    : IRequest<Result<PagedResult<StudentDto>>>;

public sealed record GetStudentDetailQuery(Guid StudentProfileId) : IRequest<Result<StudentDto>>;

/// <summary>
/// Returns the student profile linked to the currently authenticated user.
/// Resolved via the <c>studentProfileId</c> JWT claim, falling back to a UserId lookup.
/// </summary>
public sealed record GetMyStudentProfileQuery : IRequest<Result<StudentDto>>;

// ---- Bulk import into a class ----------------------------------------------

/// <summary>One parsed input row for a bulk import: a student's full name (FIO, optional) and email.</summary>
public sealed record BulkImportStudentInput(string Email, string? FullName);

/// <summary>Per-row outcome of a bulk import. <see cref="Password"/> is only set for newly created users.</summary>
public sealed record BulkImportStudentRow(string Email, string? Name, string? Password, string Status, string? Reason);

/// <summary>Outcome + statistics of a bulk student import.</summary>
public sealed record BulkImportStudentsResult(
    int TotalRows,
    int CreatedUsers,
    int ExistingUsersAdded,
    int FailedRows,
    IReadOnlyList<BulkImportStudentRow> Rows);

/// <summary>
/// Enrolls a batch of students (name + email) into a class. Existing users are
/// reused and simply enrolled; unknown emails get a new student account with a
/// generated password and their name (FIO) applied. Empty rows are ignored,
/// in-file duplicates are flagged, and each row is processed independently so one
/// failure never aborts the rest.
/// </summary>
public sealed record BulkImportStudentsCommand(Guid ClassId, IReadOnlyList<BulkImportStudentInput> Rows)
    : IRequest<Result<BulkImportStudentsResult>>;

/// <summary>Well-known status strings surfaced in the result + downloadable workbook.</summary>
public static class BulkImportStatus
{
    public const string Created = "Created";
    public const string ExistingUserAdded = "Existing User Added";
    public const string Failed = "Failed";
}
