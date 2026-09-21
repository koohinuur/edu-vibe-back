using LMS.Application.Common.Abstractions;
using LMS.Application.Common.Security;
using LMS.Application.Features.Classes;
using LMS.Domain.Entities;

namespace LMS.Application.Features.Curriculum;

/// <summary>
/// Self-scoping for class-curriculum actions. These endpoints are NOT gated by a
/// static permission (teachers don't hold <c>Classes.Update</c>, which is class-
/// settings admin) — instead the caller is authorized here, mirroring the Course
/// Builder handlers: an admin-level user (admin / office / director, via
/// <see cref="CurrentUserRoleExtensions.IsAdmin"/>) may manage any class, and a
/// teacher may manage a class they teach. This lets a teacher set up and run their
/// OWN class's curriculum (assign a template, generate the course, set the current
/// position) without a permission error, while still blocking students and other
/// teachers.
/// </summary>
internal static class CurriculumAuthorization
{
    public static bool CanManageClass(ICurrentUserService user, Class cls)
        => CanManageClass(user, cls.TeacherUserId);

    /// <summary>Overload for query projections that only pulled the class's teacher id.</summary>
    public static bool CanManageClass(ICurrentUserService user, Guid? classTeacherUserId)
        => user.IsAdmin() || (classTeacherUserId is { } teacherId && teacherId == user.UserId);

    /// <summary>
    /// Co-teacher-aware check: an admin, the primary teacher, OR any co-teacher of
    /// the class may manage it. Prefer this everywhere a class has multiple teachers.
    /// </summary>
    public static async Task<bool> CanManageClassAsync(
        IApplicationDbContext db, ICurrentUserService user, Guid classId, CancellationToken ct)
        => user.IsAdmin() || (user.UserId is { } uid && await db.IsClassTeacherAsync(classId, uid, ct));
}
