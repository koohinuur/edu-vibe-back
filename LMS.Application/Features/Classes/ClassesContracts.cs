using LMS.Application.Common.Models;
using LMS.Domain.Enums;
using MediatR;

namespace LMS.Application.Features.Classes;

public sealed record ClassDto(
    Guid Id,
    string Title,
    int MaxStudents,
    Modality Modality,
    ClassStatus Status,
    Guid? TeacherUserId,
    int EnrolledCount,
    decimal? MonthlyPrice = null);

public sealed record CreateClassCommand(string Title, int MaxStudents, Modality Modality, Guid? TeacherUserId)
    : IRequest<Result<ClassDto>>;

public sealed record UpdateClassCommand(
    Guid ClassId,
    string Title,
    int MaxStudents,
    Modality Modality,
    Guid? TeacherUserId) : IRequest<Result<ClassDto>>;

public sealed record CancelClassCommand(Guid ClassId) : IRequest<Result>;

/// <summary>Permanently delete a class (children cascade; payments are kept).</summary>
public sealed record HardDeleteClassCommand(Guid ClassId) : IRequest<Result>;

/// <summary>Reactivate an archived (cancelled) class — flips it back to Planned.</summary>
public sealed record ReactivateClassCommand(Guid ClassId) : IRequest<Result>;

public sealed record GetClassesQuery(int Page = 1, int PageSize = 25, string? Search = null)
    : IRequest<Result<PagedResult<ClassDto>>>;

public sealed record GetClassByIdQuery(Guid ClassId) : IRequest<Result<ClassDto>>;

public sealed record GetAssignedClassesQuery(Guid TeacherUserId) : IRequest<Result<IReadOnlyCollection<ClassDto>>>;

public sealed record EnrollStudentCommand(Guid ClassId, Guid StudentProfileId) : IRequest<Result>;

public sealed record RemoveStudentFromClassCommand(Guid ClassId, Guid StudentProfileId) : IRequest<Result>;

public sealed record GetClassStudentsQuery(Guid ClassId) : IRequest<Result<IReadOnlyCollection<Guid>>>;

/// <summary>
/// A class as the enrolled STUDENT sees it. Students can't read /api/Classes
/// or /api/Staff, so the teacher's display name and the next upcoming
/// session are joined server-side and handed over in one shape.
/// </summary>
public sealed record MyClassDto(
    Guid Id,
    string Title,
    Modality Modality,
    ClassStatus Status,
    string? TeacherName,
    int EnrolledCount,
    DateOnly? NextSessionDate,
    TimeOnly? NextSessionStartsAt,
    TimeOnly? NextSessionEndsAt);

/// <summary>
/// The caller's enrolled classes, resolved from their student profile on the
/// JWT. Self-scoped — gated by plain [Authorize], no Classes.Read needed.
/// </summary>
public sealed record GetMyClassesQuery : IRequest<Result<IReadOnlyCollection<MyClassDto>>>;