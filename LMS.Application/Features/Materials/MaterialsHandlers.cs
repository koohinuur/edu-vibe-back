using LMS.Application.Common.Abstractions;
using LMS.Application.Common.Models;
using LMS.Application.Common.Security;
using LMS.Domain.Entities;
using LMS.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LMS.Application.Features.Materials;

/// <summary>
/// Materials CQRS handlers.
///
/// Visibility rules — implemented uniformly in <see cref="VisibleMaterialIds"/>:
///   • Admin / Office Admin / Director — see everything (returns null filter),
///     including AdminsOnly / TeachersOnly / StudentsOnly materials.
///   • Teachers — Public + TeachersOnly + Private uploaded by them + Private
///     linked to a class they teach.
///   • Students — Public + StudentsOnly + Private linked to a class they're
///     enrolled in.
///   • Everyone else (unknown role with Materials.Read) — Public only.
///   • AdminsOnly materials are seen by admins alone.
///
/// Filtering happens before projection so EF translates it to a single SQL
/// query with a CTE / sub-select per branch.
/// </summary>
public sealed class MaterialsHandlers(IApplicationDbContext db, ICurrentUserService currentUser) :
    IRequestHandler<GetMaterialsQuery, Result<IReadOnlyCollection<MaterialDto>>>,
    IRequestHandler<GetPublicMaterialsQuery, Result<IReadOnlyCollection<MaterialDto>>>,
    IRequestHandler<GetMaterialByIdQuery, Result<MaterialDto>>,
    IRequestHandler<GetMaterialForDownloadQuery, Result<MaterialDownloadDto>>,
    IRequestHandler<GetPublicMaterialDownloadQuery, Result<MaterialDownloadDto>>,
    IRequestHandler<UploadMaterialCommand, Result<MaterialDto>>,
    IRequestHandler<UpdateMaterialCommand, Result<MaterialDto>>,
    IRequestHandler<DeleteMaterialCommand, Result<string>>
{

    public async Task<Result<IReadOnlyCollection<MaterialDto>>> Handle(
        GetMaterialsQuery request, CancellationToken ct)
    {
        var allowedIds = await VisibleMaterialIds(ct);

        var q = db.Materials.AsNoTracking();
        if (allowedIds is { } ids) q = q.Where(m => ids.Contains(m.Id));
        if (request.ClassId is { } classId)
            q = q.Where(m => m.ClassLinks.Any(l => l.ClassId == classId));

        var items = await q
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => new MaterialDto(
                m.Id, m.Title, m.Description, m.Visibility,
                m.OriginalFileName, m.MimeType, m.FileSize,
                m.UploadedByUserId, m.CreatedAt,
                m.ClassLinks.Select(l => l.ClassId).ToList(), m.CurriculumTemplateId))
            .ToListAsync(ct);

        return Result<IReadOnlyCollection<MaterialDto>>.Ok(items);
    }

    public async Task<Result<IReadOnlyCollection<MaterialDto>>> Handle(
        GetPublicMaterialsQuery request, CancellationToken ct)
    {
        var take = Math.Clamp(request.Take, 1, 100);
        var items = await db.Materials.AsNoTracking()
            .Where(m => m.Visibility == MaterialVisibility.Public)
            .OrderByDescending(m => m.CreatedAt)
            .Take(take)
            .Select(m => new MaterialDto(
                m.Id, m.Title, m.Description, m.Visibility,
                m.OriginalFileName, m.MimeType, m.FileSize,
                m.UploadedByUserId, m.CreatedAt,
                m.ClassLinks.Select(l => l.ClassId).ToList(), m.CurriculumTemplateId))
            .ToListAsync(ct);
        return Result<IReadOnlyCollection<MaterialDto>>.Ok(items);
    }

    public async Task<Result<MaterialDto>> Handle(GetMaterialByIdQuery request, CancellationToken ct)
    {
        var allowedIds = await VisibleMaterialIds(ct);
        var m = await db.Materials.AsNoTracking()
            .Where(x => x.Id == request.MaterialId)
            .Where(x => allowedIds == null || allowedIds.Contains(x.Id))
            .Select(x => new MaterialDto(
                x.Id, x.Title, x.Description, x.Visibility,
                x.OriginalFileName, x.MimeType, x.FileSize,
                x.UploadedByUserId, x.CreatedAt,
                x.ClassLinks.Select(l => l.ClassId).ToList(), x.CurriculumTemplateId))
            .FirstOrDefaultAsync(ct);
        return m is null
            ? Result<MaterialDto>.Fail("NOT_FOUND", "Material not found.")
            : Result<MaterialDto>.Ok(m);
    }

    public async Task<Result<MaterialDownloadDto>> Handle(
        GetPublicMaterialDownloadQuery request, CancellationToken ct)
    {
        // Public-only — no role check, no allowlist filter. The Where on
        // visibility = Public guarantees Private rows can't be enumerated
        // by id-guessing the public endpoint.
        var m = await db.Materials.AsNoTracking()
            .Where(x => x.Id == request.MaterialId && x.Visibility == MaterialVisibility.Public)
            .Select(x => new MaterialDownloadDto(x.Id, x.StoredFileName, x.OriginalFileName, x.MimeType))
            .FirstOrDefaultAsync(ct);
        return m is null
            ? Result<MaterialDownloadDto>.Fail("NOT_FOUND", "Material not found.")
            : Result<MaterialDownloadDto>.Ok(m);
    }

    public async Task<Result<MaterialDownloadDto>> Handle(GetMaterialForDownloadQuery request, CancellationToken ct)
    {
        var allowedIds = await VisibleMaterialIds(ct);
        var m = await db.Materials.AsNoTracking()
            .Where(x => x.Id == request.MaterialId)
            .Where(x => allowedIds == null || allowedIds.Contains(x.Id))
            .Select(x => new MaterialDownloadDto(x.Id, x.StoredFileName, x.OriginalFileName, x.MimeType))
            .FirstOrDefaultAsync(ct);
        return m is null
            ? Result<MaterialDownloadDto>.Fail("NOT_FOUND", "Material not found.")
            : Result<MaterialDownloadDto>.Ok(m);
    }

    public async Task<Result<MaterialDto>> Handle(UploadMaterialCommand request, CancellationToken ct)
    {
        if (request.Visibility == MaterialVisibility.Private && request.ClassIds.Count == 0)
            return Result<MaterialDto>.Fail("VALIDATION", "Private materials require at least one class.");

        var distinctClassIds = request.ClassIds.Distinct().ToList();
        if (distinctClassIds.Count > 0)
        {
            var existingCount = await db.Classes
                .Where(c => distinctClassIds.Contains(c.Id))
                .CountAsync(ct);
            if (existingCount != distinctClassIds.Count)
                return Result<MaterialDto>.Fail("VALIDATION", "One or more class ids do not exist.");
        }

        var entity = new Material(
            request.Title,
            request.Description,
            request.Visibility,
            request.StoredFileName,
            request.OriginalFileName,
            request.MimeType,
            request.FileSize,
            request.UploadedByUserId);
        entity.SetCourse(request.CurriculumTemplateId);

        foreach (var classId in distinctClassIds)
            entity.ClassLinks.Add(new MaterialClass(entity.Id, classId));

        await db.Materials.AddAsync(entity, ct);
        await db.SaveChangesAsync(ct);

        return Result<MaterialDto>.Ok(Map(entity));
    }

    public async Task<Result<MaterialDto>> Handle(UpdateMaterialCommand request, CancellationToken ct)
    {
        var entity = await db.Materials
            .Include(m => m.ClassLinks)
            .FirstOrDefaultAsync(m => m.Id == request.MaterialId, ct);
        if (entity is null) return Result<MaterialDto>.Fail("NOT_FOUND", "Material not found.");

        if (!CanManage(entity))
            return Result<MaterialDto>.Fail("FORBIDDEN", "Not allowed to edit this material.");

        if (request.Visibility == MaterialVisibility.Private && request.ClassIds.Count == 0)
            return Result<MaterialDto>.Fail("VALIDATION", "Private materials require at least one class.");

        var distinctClassIds = request.ClassIds.Distinct().ToList();
        if (distinctClassIds.Count > 0)
        {
            var existingCount = await db.Classes
                .Where(c => distinctClassIds.Contains(c.Id))
                .CountAsync(ct);
            if (existingCount != distinctClassIds.Count)
                return Result<MaterialDto>.Fail("VALIDATION", "One or more class ids do not exist.");
        }

        entity.UpdateDetails(request.Title, request.Description, request.Visibility);
        entity.SetCourse(request.CurriculumTemplateId);

        // Replace the class set wholesale — keeps the call deterministic and
        // matches how the admin UI sends a full re-selection on save.
        var toRemove = entity.ClassLinks.ToList();
        foreach (var link in toRemove) entity.ClassLinks.Remove(link);
        foreach (var classId in distinctClassIds)
            entity.ClassLinks.Add(new MaterialClass(entity.Id, classId));

        await db.SaveChangesAsync(ct);
        return Result<MaterialDto>.Ok(Map(entity));
    }

    public async Task<Result<string>> Handle(DeleteMaterialCommand request, CancellationToken ct)
    {
        var entity = await db.Materials
            .Include(m => m.ClassLinks)
            .FirstOrDefaultAsync(m => m.Id == request.MaterialId, ct);
        if (entity is null) return Result<string>.Fail("NOT_FOUND", "Material not found.");
        if (!CanManage(entity))
            return Result<string>.Fail("FORBIDDEN", "Not allowed to delete this material.");

        var storedName = entity.StoredFileName;
        db.Materials.Remove(entity);
        await db.SaveChangesAsync(ct);
        // The controller is responsible for the blob delete — it owns the
        // file store. We hand back the stored name so it can do that.
        return Result<string>.Ok(storedName, "Deleted");
    }

    /// <summary>
    /// Returns the ids the caller is allowed to see, OR null when no filter
    /// is needed (admin / director). Returns an empty set when the user has
    /// no allowed records (still callable so an empty list is returned, not
    /// an error).
    /// </summary>
    private async Task<HashSet<Guid>?> VisibleMaterialIds(CancellationToken ct)
    {
        if (IsPrivileged()) return null;

        var userId = currentUser.UserId;
        if (userId is null) return new HashSet<Guid>();

        var isTeacher = currentUser.IsTeacher();
        var isStudent = currentUser.IsStudent();

        // Public materials are visible to anyone signed in.
        var publicIds = db.Materials
            .Where(m => m.Visibility == MaterialVisibility.Public)
            .Select(m => m.Id);

        // Role-scoped (TeachersOnly / StudentsOnly) + class-private ids for
        // this caller. Stays null when the caller is neither teacher nor
        // student — avoids `publicIds.Concat(Enumerable.Empty<Guid>()
        // .AsQueryable())`, which EF Core 8 can't translate ("Empty
        // collections are not supported as inline query roots"). AdminsOnly
        // materials never appear here — only privileged callers (short-
        // circuited above) ever see them.
        IQueryable<Guid>? scopedIds = null;

        if (isTeacher)
        {
            var teachingClassIds = db.Classes
                .Where(c => c.TeacherUserId == userId)
                .Select(c => c.Id);
            scopedIds = db.Materials
                .Where(m => m.Visibility == MaterialVisibility.TeachersOnly
                            || (m.Visibility == MaterialVisibility.Private
                                && (m.UploadedByUserId == userId
                                    || m.ClassLinks.Any(l => teachingClassIds.Contains(l.ClassId)))))
                .Select(m => m.Id);
        }
        else if (isStudent)
        {
            // The student's classes are the active enrollments of their
            // student profile. Resolve via StudentProfile.UserId so the
            // join is a single hop.
            var studentClassIds = db.Enrollments
                .Where(e => e.Status == EnrollmentStatus.Active
                            && db.StudentProfiles.Any(sp =>
                                sp.UserId == userId && sp.Id == e.StudentProfileId))
                .Select(e => e.ClassId);
            scopedIds = db.Materials
                .Where(m => m.Visibility == MaterialVisibility.StudentsOnly
                            || (m.Visibility == MaterialVisibility.Private
                                && m.ClassLinks.Any(l => studentClassIds.Contains(l.ClassId))))
                .Select(m => m.Id);
        }

        var combined = scopedIds is null ? publicIds : publicIds.Concat(scopedIds).Distinct();
        var ids = await combined.ToListAsync(ct);
        return new HashSet<Guid>(ids);
    }

    private bool IsPrivileged()
    {
        // Any admin-level role can see and manage every material.
        return currentUser.IsAdmin();
    }

    private bool CanManage(Material entity)
    {
        if (IsPrivileged()) return true;
        return currentUser.UserId is { } uid && entity.UploadedByUserId == uid;
    }

    private static MaterialDto Map(Material m) => new(
        m.Id, m.Title, m.Description, m.Visibility,
        m.OriginalFileName, m.MimeType, m.FileSize,
        m.UploadedByUserId, m.CreatedAt,
        m.ClassLinks.Select(l => l.ClassId).ToList(), m.CurriculumTemplateId);
}
