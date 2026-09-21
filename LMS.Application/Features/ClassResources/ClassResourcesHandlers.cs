using LMS.Application.Common.Abstractions;
using LMS.Application.Features.Classes;
using LMS.Application.Common.Models;
using LMS.Application.Common.Security;
using LMS.Domain.Entities;
using LMS.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LMS.Application.Features.ClassResources;

/// <summary>
/// Class-level content hub. Every handler self-scopes (no cross-class access):
///   • read  — admin/staff, the class's teacher, or an enrolled student
///   • manage — admin/staff or the class's own teacher
/// Gated by plain [Authorize] at the controller; the real authorization lives
/// here, mirroring the Analytics / Lessons features.
/// </summary>
public sealed class ClassResourcesHandlers(IApplicationDbContext db, ICurrentUserService currentUser) :
    IRequestHandler<GetClassResourcesQuery, Result<IReadOnlyCollection<ClassResourceDto>>>,
    IRequestHandler<CreateClassResourceCommand, Result<ClassResourceDto>>,
    IRequestHandler<UpdateClassResourceCommand, Result<ClassResourceDto>>,
    IRequestHandler<DeleteClassResourceCommand, Result>
{
    private bool IsAdmin() => currentUser.IsAdmin();
    private bool IsTeacher() => currentUser.IsTeacher();

    private static ClassResourceDto Map(ClassResource r) =>
        new(r.Id, r.ClassId, r.Kind, r.Title, r.Url, r.Content, r.Position, r.CreatedAt);

    /// <summary>Resolves the class's teacher, returning null when the class doesn't exist.</summary>
    private async Task<(bool Exists, Guid? TeacherUserId)> ClassInfoAsync(Guid classId, CancellationToken ct)
    {
        var row = await db.Classes.AsNoTracking()
            .Where(c => c.Id == classId)
            .Select(c => new { c.TeacherUserId })
            .FirstOrDefaultAsync(ct);
        return row is null ? (false, null) : (true, row.TeacherUserId);
    }

    private async Task<bool> CanManageAsync(Guid classId, CancellationToken ct) =>
        IsAdmin() || (IsTeacher() && currentUser.UserId is { } uid && await db.IsClassTeacherAsync(classId, uid, ct));

    /// <summary>The caller's student profile id — from the JWT claim, else via UserId.</summary>
    private async Task<Guid?> ResolveStudentProfileAsync(CancellationToken ct)
    {
        if (currentUser.StudentProfileId is { } sp) return sp;
        if (currentUser.UserId is not { } uid) return null;
        return await db.StudentProfiles.AsNoTracking()
            .Where(s => s.UserId == uid).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
    }

    private async Task<bool> CanReadAsync(Guid classId, Guid? teacherUserId, CancellationToken ct)
    {
        if (await CanManageAsync(classId, ct)) return true;
        var profileId = await ResolveStudentProfileAsync(ct);
        if (profileId is null) return false;
        return await db.Enrollments.AsNoTracking()
            .AnyAsync(e => e.ClassId == classId
                        && e.StudentProfileId == profileId.Value
                        && e.Status == EnrollmentStatus.Active, ct);
    }

    public async Task<Result<IReadOnlyCollection<ClassResourceDto>>> Handle(
        GetClassResourcesQuery request, CancellationToken ct)
    {
        var (exists, teacherUserId) = await ClassInfoAsync(request.ClassId, ct);
        if (!exists) return Result<IReadOnlyCollection<ClassResourceDto>>.Fail("NOT_FOUND", "Class not found.");
        if (!await CanReadAsync(request.ClassId, teacherUserId, ct))
            return Result<IReadOnlyCollection<ClassResourceDto>>.Fail("FORBIDDEN", "You can't view this class's content.");

        var items = await db.ClassResources.AsNoTracking()
            .Where(r => r.ClassId == request.ClassId)
            .OrderBy(r => r.Position).ThenBy(r => r.CreatedAt)
            .Select(r => new ClassResourceDto(r.Id, r.ClassId, r.Kind, r.Title, r.Url, r.Content, r.Position, r.CreatedAt))
            .ToListAsync(ct);
        return Result<IReadOnlyCollection<ClassResourceDto>>.Ok(items);
    }

    public async Task<Result<ClassResourceDto>> Handle(CreateClassResourceCommand request, CancellationToken ct)
    {
        var (exists, teacherUserId) = await ClassInfoAsync(request.ClassId, ct);
        if (!exists) return Result<ClassResourceDto>.Fail("NOT_FOUND", "Class not found.");
        if (!await CanManageAsync(request.ClassId, ct))
            return Result<ClassResourceDto>.Fail("FORBIDDEN", "Only the class teacher or an admin can manage class content.");
        if (currentUser.UserId is not { } uid)
            return Result<ClassResourceDto>.Fail("FORBIDDEN", "Sign in to manage class content.");

        // Append after the current last item.
        var nextPosition = await db.ClassResources
            .Where(r => r.ClassId == request.ClassId)
            .Select(r => (int?)r.Position).MaxAsync(ct) is { } max ? max + 1 : 0;

        var resource = new ClassResource(request.ClassId, request.Kind, request.Title, request.Url, request.Content, uid);
        resource.SetPosition(nextPosition);
        await db.ClassResources.AddAsync(resource, ct);
        await db.SaveChangesAsync(ct);
        return Result<ClassResourceDto>.Ok(Map(resource), "Resource added.");
    }

    public async Task<Result<ClassResourceDto>> Handle(UpdateClassResourceCommand request, CancellationToken ct)
    {
        var (exists, teacherUserId) = await ClassInfoAsync(request.ClassId, ct);
        if (!exists) return Result<ClassResourceDto>.Fail("NOT_FOUND", "Class not found.");
        if (!await CanManageAsync(request.ClassId, ct))
            return Result<ClassResourceDto>.Fail("FORBIDDEN", "Only the class teacher or an admin can manage class content.");

        var resource = await db.ClassResources
            .FirstOrDefaultAsync(r => r.Id == request.ResourceId && r.ClassId == request.ClassId, ct);
        if (resource is null) return Result<ClassResourceDto>.Fail("NOT_FOUND", "Resource not found.");

        resource.Update(request.Kind, request.Title, request.Url, request.Content);
        await db.SaveChangesAsync(ct);
        return Result<ClassResourceDto>.Ok(Map(resource), "Resource updated.");
    }

    public async Task<Result> Handle(DeleteClassResourceCommand request, CancellationToken ct)
    {
        var (exists, teacherUserId) = await ClassInfoAsync(request.ClassId, ct);
        if (!exists) return Result.Fail("NOT_FOUND", "Class not found.");
        if (!await CanManageAsync(request.ClassId, ct))
            return Result.Fail("FORBIDDEN", "Only the class teacher or an admin can manage class content.");

        var resource = await db.ClassResources
            .FirstOrDefaultAsync(r => r.Id == request.ResourceId && r.ClassId == request.ClassId, ct);
        if (resource is null) return Result.Fail("NOT_FOUND", "Resource not found.");

        db.ClassResources.Remove(resource);
        await db.SaveChangesAsync(ct);
        return Result.Ok("Resource removed.");
    }
}
