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
    decimal? MonthlyPrice = null,
    string? GroupType = null);

public sealed record CreateClassCommand(
    string Title, int MaxStudents, Modality Modality, Guid? TeacherUserId, string? GroupType = null)
    : IRequest<Result<ClassDto>>;

/// <summary>One roster entry: the student, their display name, and whether they've dropped out.</summary>
public sealed record ClassRosterStudentDto(Guid StudentProfileId, string Name, bool Dropped);

/// <summary>A class's full roster (active + dropped, flagged) with names.</summary>
public sealed record ClassRosterDto(Guid ClassId, IReadOnlyCollection<ClassRosterStudentDto> Students);

/// <summary>
/// Rosters (with names + dropped flag) for many classes in ONE query — the homework
/// board's batch read (replaces one roster call per class AND the whole-student-list
/// fetch for names). Scoped to classes the caller teaches (admins see all requested).
/// </summary>
public sealed record GetClassRostersQuery(IReadOnlyCollection<Guid> ClassIds)
    : IRequest<Result<IReadOnlyCollection<ClassRosterDto>>>;

public sealed record UpdateClassCommand(
    Guid ClassId,
    string Title,
    int MaxStudents,
    Modality Modality,
    Guid? TeacherUserId,
    string? GroupType = null) : IRequest<Result<ClassDto>>;

/// <summary>The distinct group types already in use, plus the built-in defaults —
/// the admin group-type picker's option list (supports custom entries).</summary>
public sealed record GetGroupTypesQuery : IRequest<Result<IReadOnlyCollection<string>>>;

public sealed record CancelClassCommand(Guid ClassId) : IRequest<Result>;

/// <summary>All teacher user ids of a class — primary first, then co-teachers.</summary>
public sealed record GetClassTeachersQuery(Guid ClassId) : IRequest<Result<IReadOnlyList<Guid>>>;

/// <summary>
/// Replace the class's full teacher list. The first id becomes the primary
/// (Class.TeacherUserId); the rest are co-teachers. An empty list clears all.
/// </summary>
public sealed record SetClassTeachersCommand(Guid ClassId, IReadOnlyList<Guid> TeacherUserIds)
    : IRequest<Result<IReadOnlyList<Guid>>>;

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