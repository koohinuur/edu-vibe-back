using LMS.Application.Common.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace LMS.Application.Features.Classes;

/// <summary>
/// Central "who teaches this class" logic. A class has a primary teacher
/// (<c>Class.TeacherUserId</c>) plus any number of co-teachers
/// (<c>class_teachers</c>); all of them count as teaching the class. Every
/// class-ownership check across the app goes through these so the single-teacher
/// and multi-teacher paths never diverge.
/// </summary>
public static class ClassTeacherQueries
{
    /// <summary>Class ids the user teaches — primary or co-teacher.</summary>
    public static IQueryable<Guid> TaughtClassIds(this IApplicationDbContext db, Guid userId) =>
        db.Classes.Where(c => c.TeacherUserId == userId).Select(c => c.Id)
            .Union(db.ClassTeachers.Where(t => t.UserId == userId).Select(t => t.ClassId));

    /// <summary>Is the user a teacher (primary or co) of the class?</summary>
    public static async Task<bool> IsClassTeacherAsync(
        this IApplicationDbContext db, Guid classId, Guid userId, CancellationToken ct) =>
        await db.Classes.AnyAsync(c => c.Id == classId && c.TeacherUserId == userId, ct) ||
        await db.ClassTeachers.AnyAsync(t => t.ClassId == classId && t.UserId == userId, ct);

    /// <summary>All teacher user ids of a class — primary first, then co-teachers.</summary>
    public static async Task<List<Guid>> TeacherUserIdsAsync(
        this IApplicationDbContext db, Guid classId, CancellationToken ct)
    {
        var primary = await db.Classes.Where(c => c.Id == classId)
            .Select(c => c.TeacherUserId).FirstOrDefaultAsync(ct);
        var co = await db.ClassTeachers.Where(t => t.ClassId == classId)
            .Select(t => t.UserId).ToListAsync(ct);

        var list = new List<Guid>();
        if (primary is { } p) list.Add(p);
        list.AddRange(co.Where(id => id != primary));
        return list;
    }
}
