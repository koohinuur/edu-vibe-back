using LMS.Application.Common.Abstractions;
using LMS.Application.Common.Models;
using LMS.Domain.Entities;
using LMS.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LMS.Application.Features.Classes;

public sealed class ClassesHandlers(IApplicationDbContext db, ICurrentUserService currentUser) :
    IRequestHandler<GetClassesQuery, Result<PagedResult<ClassDto>>>,
    IRequestHandler<GetClassByIdQuery, Result<ClassDto>>,
    IRequestHandler<GetAssignedClassesQuery, Result<IReadOnlyCollection<ClassDto>>>,
    IRequestHandler<GetMyClassesQuery, Result<IReadOnlyCollection<MyClassDto>>>,
    IRequestHandler<CreateClassCommand, Result<ClassDto>>,
    IRequestHandler<UpdateClassCommand, Result<ClassDto>>,
    IRequestHandler<CancelClassCommand, Result>,
    IRequestHandler<ReactivateClassCommand, Result>,
    IRequestHandler<HardDeleteClassCommand, Result>,
    IRequestHandler<EnrollStudentCommand, Result>,
    IRequestHandler<RemoveStudentFromClassCommand, Result>,
    IRequestHandler<GetClassStudentsQuery, Result<IReadOnlyCollection<Guid>>>
{
    public async Task<Result> Handle(CancelClassCommand request, CancellationToken cancellationToken)
    {
        var c = await db.Classes.FirstOrDefaultAsync(x => x.Id == request.ClassId, cancellationToken);
        if (c is null) return Result.Fail("NOT_FOUND", "Class not found.");
        c.Cancel();
        await db.SaveChangesAsync(cancellationToken);
        return Result.Ok("Cancelled");
    }

    public async Task<Result> Handle(HardDeleteClassCommand request, CancellationToken cancellationToken)
    {
        var c = await db.Classes.FirstOrDefaultAsync(x => x.Id == request.ClassId, cancellationToken);
        if (c is null) return Result.Fail("NOT_FOUND", "Class not found.");
        // Enrollments, sessions, schedule, assignments and resources cascade at
        // the DB level; payments are kept (their ClassId is set null).
        db.Classes.Remove(c);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Ok("Class deleted.");
    }

    public async Task<Result> Handle(ReactivateClassCommand request, CancellationToken cancellationToken)
    {
        var c = await db.Classes.FirstOrDefaultAsync(x => x.Id == request.ClassId, cancellationToken);
        if (c is null) return Result.Fail("NOT_FOUND", "Class not found.");
        c.Reactivate();
        await db.SaveChangesAsync(cancellationToken);
        return Result.Ok("Reactivated");
    }

    public async Task<Result<ClassDto>> Handle(CreateClassCommand request, CancellationToken cancellationToken)
    {
        var c = new Class(request.Title, request.MaxStudents, request.Modality);
        if (request.TeacherUserId.HasValue)
        {
            var t = await db.Users.FirstOrDefaultAsync(x => x.Id == request.TeacherUserId.Value, cancellationToken);
            if (t is null) return Result<ClassDto>.Fail("NOT_FOUND", "Teacher not found.");
            c.AssignTeacher(t);
        }

        await db.Classes.AddAsync(c, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Result<ClassDto>.Ok(Map(c));
    }

    public async Task<Result> Handle(EnrollStudentCommand request, CancellationToken cancellationToken)
    {
        // We do the validation + insert as discrete queries instead of going
        // through Class.EnrollStudent. The previous implementation loaded the
        // Class with its Enrollments collection, mutated it, and called
        // Class.Touch() — which made SaveChanges issue both an INSERT
        // (enrollments) AND an UPDATE (classes.UpdatedAt) in the same batch.
        // PostgreSQL 18 + Npgsql 8.0.11 reproducibly mis-reports the affected
        // row count for that batch, so EF throws DbUpdateConcurrencyException
        // ("expected 1 row, affected 0") even though the rows would have
        // landed correctly. Keeping the operation to a single INSERT skips
        // the batched UPDATE that triggers the bug.
        var classExists = await db.Classes
            .AsNoTracking()
            .AnyAsync(c => c.Id == request.ClassId, cancellationToken);
        if (!classExists) return Result.Fail("NOT_FOUND", "Class not found.");

        var maxStudents = await db.Classes
            .AsNoTracking()
            .Where(c => c.Id == request.ClassId)
            .Select(c => c.MaxStudents)
            .FirstAsync(cancellationToken);

        var activeCount = await db.Enrollments
            .AsNoTracking()
            .CountAsync(e => e.ClassId == request.ClassId
                         && e.Status == EnrollmentStatus.Active, cancellationToken);
        if (activeCount >= maxStudents)
            return Result.Fail("VALIDATION", "Class is full.");

        // The unique index `IX_enrollments_ClassId_StudentProfileId` covers
        // every status — so a row left behind by a previous Drop blocks a
        // fresh INSERT. Reactivate it instead of trying to add a new one;
        // domain still treats this as a fresh enrolment.
        var existing = await db.Enrollments
            .FirstOrDefaultAsync(e => e.ClassId == request.ClassId
                                   && e.StudentProfileId == request.StudentProfileId,
                                 cancellationToken);

        if (existing is not null)
        {
            if (existing.Status == EnrollmentStatus.Active)
                return Result.Fail("VALIDATION", "Student is already enrolled.");
            // Status was Dropped (or other) — flip back to Active.
            existing.Activate();
            await db.SaveChangesAsync(cancellationToken);
            return Result.Ok("Enrolled");
        }

        var enrollment = Enrollment.Create(request.ClassId, request.StudentProfileId,
            Array.Empty<Enrollment>());
        await db.Enrollments.AddAsync(enrollment, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Ok("Enrolled");
    }

    public async Task<Result<IReadOnlyCollection<ClassDto>>> Handle(GetAssignedClassesQuery request,
        CancellationToken cancellationToken)
    {
        return Result<IReadOnlyCollection<ClassDto>>.Ok(await db.Classes
            .Where(x => x.TeacherUserId == request.TeacherUserId)
            .Select(c => new ClassDto(c.Id, c.Title, c.MaxStudents, c.Modality, c.Status, c.TeacherUserId,
                c.Enrollments.Count(e => e.Status == EnrollmentStatus.Active), c.MonthlyPrice))
            .ToListAsync(cancellationToken));
    }

    public async Task<Result<IReadOnlyCollection<MyClassDto>>> Handle(GetMyClassesQuery request,
        CancellationToken cancellationToken)
    {
        // Resolve the caller's student profile — from the JWT claim if the
        // backend emitted it, otherwise via UserId → StudentProfiles.
        var profileId = currentUser.StudentProfileId;
        if (profileId is null && currentUser.UserId is { } uid)
        {
            profileId = await db.StudentProfiles
                .Where(sp => sp.UserId == uid)
                .Select(sp => (Guid?)sp.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }
        if (profileId is null)
            return Result<IReadOnlyCollection<MyClassDto>>.Ok(Array.Empty<MyClassDto>());

        var enrolledClassIds = await db.Enrollments
            .Where(e => e.StudentProfileId == profileId.Value && e.Status == EnrollmentStatus.Active)
            .Select(e => e.ClassId)
            .ToListAsync(cancellationToken);
        if (enrolledClassIds.Count == 0)
            return Result<IReadOnlyCollection<MyClassDto>>.Ok(Array.Empty<MyClassDto>());

        // Class header + the teacher's display name (join staff_profiles on the
        // class's TeacherUserId — students can't read /api/Staff themselves).
        var classes = await db.Classes
            .Where(c => enrolledClassIds.Contains(c.Id))
            .Select(c => new
            {
                c.Id,
                c.Title,
                c.Modality,
                c.Status,
                Active = c.Enrollments.Count(e => e.Status == EnrollmentStatus.Active),
                TeacherName = db.StaffProfiles
                    .Where(sp => sp.UserId == c.TeacherUserId)
                    .Select(sp => (sp.FirstName ?? "") + " " + (sp.LastName ?? ""))
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        // Next upcoming session per class — fetch the future sessions ordered
        // and pick the earliest per class in memory (cheap at academy scale).
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var futureSessions = await db.ClassSessions
            .Where(s => enrolledClassIds.Contains(s.ClassId) && s.SessionDate >= today)
            .OrderBy(s => s.SessionDate).ThenBy(s => s.StartsAt)
            .Select(s => new { s.ClassId, s.SessionDate, s.StartsAt, s.EndsAt })
            .ToListAsync(cancellationToken);
        var nextByClass = futureSessions
            .GroupBy(s => s.ClassId)
            .ToDictionary(g => g.Key, g => g.First());

        var dtos = classes
            .Select(c =>
            {
                var name = c.TeacherName?.Trim();
                nextByClass.TryGetValue(c.Id, out var next);
                return new MyClassDto(
                    c.Id, c.Title, c.Modality, c.Status,
                    string.IsNullOrWhiteSpace(name) ? null : name,
                    c.Active,
                    next?.SessionDate, next?.StartsAt, next?.EndsAt);
            })
            .OrderBy(c => c.Title)
            .ToList();

        return Result<IReadOnlyCollection<MyClassDto>>.Ok(dtos);
    }

    public async Task<Result<ClassDto>> Handle(GetClassByIdQuery request, CancellationToken cancellationToken)
    {
        var c = await db.Classes
            .Include(x => x.Enrollments)
            .FirstOrDefaultAsync(x => x.Id == request.ClassId, cancellationToken);
        return c is null ? Result<ClassDto>.Fail("NOT_FOUND", "Class not found.") : Result<ClassDto>.Ok(Map(c));
    }

    public async Task<Result<PagedResult<ClassDto>>> Handle(GetClassesQuery request,
        CancellationToken cancellationToken)
    {
        var page = new PageRequest(request.Page, request.PageSize, request.Search);

        // Keep the queryable on the entity, filter and order on entity columns,
        // and project to the DTO LAST. Projecting to `new ClassDto(...)` up
        // front and then OrderBy(d => d.Title) is the EF-untranslatable
        // anti-pattern that also broke Staff and Students earlier this session.
        var query = db.Classes.AsNoTracking();

        if (page.NormalizedSearch is { } search)
            query = query.Where(c => c.Title.ToLower().Contains(search));

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(c => c.Title)
            .Skip(page.Skip)
            .Take(page.NormalizedPageSize)
            .Select(c => new ClassDto(c.Id, c.Title, c.MaxStudents, c.Modality, c.Status, c.TeacherUserId,
                c.Enrollments.Count(e => e.Status == EnrollmentStatus.Active), c.MonthlyPrice))
            .ToListAsync(cancellationToken);

        return Result<PagedResult<ClassDto>>.Ok(PagedResult<ClassDto>.From(items, total, page));
    }

    public async Task<Result<IReadOnlyCollection<Guid>>> Handle(GetClassStudentsQuery request,
        CancellationToken cancellationToken)
    {
        return Result<IReadOnlyCollection<Guid>>.Ok(await db.Enrollments
            .Where(x => x.ClassId == request.ClassId && x.Status == EnrollmentStatus.Active)
            .Select(x => x.StudentProfileId).ToListAsync(cancellationToken));
    }

    public async Task<Result> Handle(RemoveStudentFromClassCommand request, CancellationToken cancellationToken)
    {
        var e = await db.Enrollments.FirstOrDefaultAsync(
            x => x.ClassId == request.ClassId && x.StudentProfileId == request.StudentProfileId, cancellationToken);
        if (e is null) return Result.Fail("NOT_FOUND", "Enrollment not found.");
        e.Drop();
        await db.SaveChangesAsync(cancellationToken);
        return Result.Ok("Dropped");
    }

    public async Task<Result<ClassDto>> Handle(UpdateClassCommand request, CancellationToken cancellationToken)
    {
        var c = await db.Classes.FirstOrDefaultAsync(x => x.Id == request.ClassId, cancellationToken);
        if (c is null) return Result<ClassDto>.Fail("NOT_FOUND", "Class not found.");
        c.UpdateDetails(request.Title, request.MaxStudents, request.Modality);
        if (request.TeacherUserId.HasValue)
        {
            var t = await db.Users.FirstOrDefaultAsync(x => x.Id == request.TeacherUserId.Value, cancellationToken);
            if (t is null) return Result<ClassDto>.Fail("NOT_FOUND", "Teacher not found.");
            c.AssignTeacher(t);
        }

        await db.SaveChangesAsync(cancellationToken);
        return Result<ClassDto>.Ok(Map(c));
    }

    private static ClassDto Map(Class c)
    {
        // For single-entity reads we use the in-memory Enrollments count —
        // entity has already been materialised so this avoids a second
        // round-trip. Returns 0 if Include(Enrollments) wasn't applied,
        // which is the safe default for callers that don't need the count.
        var active = c.Enrollments?.Count(e => e.Status == EnrollmentStatus.Active) ?? 0;
        return new ClassDto(c.Id, c.Title, c.MaxStudents, c.Modality, c.Status, c.TeacherUserId, active, c.MonthlyPrice);
    }
}