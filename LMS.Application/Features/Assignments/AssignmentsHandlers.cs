using LMS.Application.Common;
using LMS.Application.Common.Abstractions;
using LMS.Application.Common.Models;
using LMS.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LMS.Application.Features.Assignments;

public sealed class AssignmentsHandlers(
    IApplicationDbContext db, ITelegramNotifier notifier, ICurrentUserService currentUser) :
    IRequestHandler<CreateAssignmentCommand, Result<AssignmentDto>>,
    IRequestHandler<UpdateAssignmentCommand, Result<AssignmentDto>>,
    IRequestHandler<PublishAssignmentCommand, Result<AssignmentDto>>,
    IRequestHandler<CloseAssignmentCommand, Result<AssignmentDto>>,
    IRequestHandler<ReopenAssignmentCommand, Result<AssignmentDto>>,
    IRequestHandler<DeleteAssignmentCommand, Result>,
    IRequestHandler<GetClassAssignmentsQuery, Result<IReadOnlyCollection<AssignmentDto>>>,
    IRequestHandler<GetStudentAssignmentsQuery, Result<IReadOnlyCollection<AssignmentDto>>>,
    IRequestHandler<GetAssignmentsQuery, Result<IReadOnlyCollection<AssignmentDto>>>,
    IRequestHandler<AttachBookToAssignmentCommand, Result<AssignmentBookDto>>,
    IRequestHandler<DetachBookFromAssignmentCommand, Result>,
    IRequestHandler<GetAssignmentBooksQuery, Result<IReadOnlyCollection<AssignmentBookDto>>>,
    IRequestHandler<SetAssignmentAssigneesCommand, Result<IReadOnlyCollection<AssignmentAssigneeDto>>>,
    IRequestHandler<GetAssignmentAssigneesQuery, Result<IReadOnlyCollection<AssignmentAssigneeDto>>>,
    IRequestHandler<AddAssignmentFileCommand, Result<AssignmentFileDto>>,
    IRequestHandler<RemoveAssignmentFileCommand, Result<string>>,
    IRequestHandler<GetAssignmentFilesQuery, Result<IReadOnlyCollection<AssignmentFileDto>>>,
    IRequestHandler<GetAssignmentFileForDownloadQuery, Result<AssignmentFileDownloadDto>>
{
    public async Task<Result<AssignmentDto>> Handle(CloseAssignmentCommand request, CancellationToken cancellationToken)
    {
        var a = await db.Assignments.FirstOrDefaultAsync(x => x.Id == request.AssignmentId, cancellationToken);
        if (a is null) return Result<AssignmentDto>.Fail("NOT_FOUND", "Assignment not found.");
        a.Close();
        await db.SaveChangesAsync(cancellationToken);
        return Result<AssignmentDto>.Ok(Map(a));
    }

    public async Task<Result<AssignmentDto>> Handle(ReopenAssignmentCommand request, CancellationToken cancellationToken)
    {
        var a = await db.Assignments.FirstOrDefaultAsync(x => x.Id == request.AssignmentId, cancellationToken);
        if (a is null) return Result<AssignmentDto>.Fail("NOT_FOUND", "Assignment not found.");
        a.Reopen();
        await db.SaveChangesAsync(cancellationToken);
        return Result<AssignmentDto>.Ok(Map(a));
    }

    public async Task<Result> Handle(DeleteAssignmentCommand request, CancellationToken cancellationToken)
    {
        var a = await db.Assignments.FirstOrDefaultAsync(x => x.Id == request.AssignmentId, cancellationToken);
        if (a is null) return Result.Fail("NOT_FOUND", "Assignment not found.");
        // Books, files, assignees, learning tasks and submissions cascade at the DB level.
        db.Assignments.Remove(a);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Ok("Assignment deleted.");
    }

    public async Task<Result<AssignmentDto>> Handle(CreateAssignmentCommand request,
        CancellationToken cancellationToken)
    {
        var t = await db.Users.FirstOrDefaultAsync(x => x.Id == request.TeacherUserId, cancellationToken);
        if (t is null) return Result<AssignmentDto>.Fail("NOT_FOUND", "Teacher not found.");
        var a = new Assignment(request.ClassId, request.Title, t);
        if (request.DueDate.HasValue)
            a.SetDueDate(DateTime.SpecifyKind(request.DueDate.Value, DateTimeKind.Utc));
        a.SetDescription(request.Description);
        await db.Assignments.AddAsync(a, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Result<AssignmentDto>.Ok(Map(a));
    }

    public async Task<Result<IReadOnlyCollection<AssignmentDto>>> Handle(GetClassAssignmentsQuery request,
        CancellationToken cancellationToken)
    {
        // SECURITY: non-staff callers (students) only see assignments for classes
        // they're enrolled in; staff get the unrestricted teaching view.
        var scope = await NonStaffEnrolledClassScopeAsync(cancellationToken);
        var q = db.Assignments.Where(x => x.ClassId == request.ClassId);
        if (scope is not null) q = q.Where(x => scope.Contains(x.ClassId));
        return Result<IReadOnlyCollection<AssignmentDto>>.Ok(await q
            .Select(a => new AssignmentDto(a.Id, a.ClassId, a.Title, a.Status, a.CreatedByTeacherId, a.DueDate, a.Description))
            .ToListAsync(cancellationToken));
    }

    // SECURITY helper: null for staff (no class restriction); the caller's enrolled
    // class ids for a student; an empty list for a profile-less caller (sees
    // nothing). Scopes the class-level assignment listings so a student can't
    // enumerate assignment metadata for classes they aren't enrolled in.
    private async Task<List<Guid>?> NonStaffEnrolledClassScopeAsync(CancellationToken ct)
    {
        if (currentUser.StaffProfileId is not null) return null;
        if (currentUser.StudentProfileId is not { } spid) return new List<Guid>();
        return await db.Enrollments.Where(e => e.StudentProfileId == spid)
            .Select(e => e.ClassId).ToListAsync(ct);
    }

    // ----- Worksheet files -------------------------------------------------

    public async Task<Result<AssignmentFileDto>> Handle(AddAssignmentFileCommand request,
        CancellationToken cancellationToken)
    {
        var exists = await db.Assignments.AnyAsync(a => a.Id == request.AssignmentId, cancellationToken);
        if (!exists) return Result<AssignmentFileDto>.Fail("NOT_FOUND", "Assignment not found.");

        var file = new AssignmentFile(request.AssignmentId, request.StoredFileName,
            request.OriginalFileName, request.MimeType, request.FileSize);
        await db.AssignmentFiles.AddAsync(file, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Result<AssignmentFileDto>.Ok(
            new AssignmentFileDto(file.Id, file.AssignmentId, file.OriginalFileName,
                file.MimeType, file.FileSize, file.CreatedAt));
    }

    public async Task<Result<string>> Handle(RemoveAssignmentFileCommand request,
        CancellationToken cancellationToken)
    {
        var file = await db.AssignmentFiles.FirstOrDefaultAsync(
            f => f.Id == request.FileId && f.AssignmentId == request.AssignmentId, cancellationToken);
        if (file is null) return Result<string>.Fail("NOT_FOUND", "File not found.");
        var storedName = file.StoredFileName;
        db.AssignmentFiles.Remove(file);
        await db.SaveChangesAsync(cancellationToken);
        return Result<string>.Ok(storedName, "Removed");
    }

    public async Task<Result<IReadOnlyCollection<AssignmentFileDto>>> Handle(
        GetAssignmentFilesQuery request, CancellationToken cancellationToken)
    {
        // SECURITY: students only see files for assignments in their enrolled classes.
        var scope = await NonStaffEnrolledClassScopeAsync(cancellationToken);
        if (scope is not null)
        {
            var classId = await db.Assignments.Where(a => a.Id == request.AssignmentId)
                .Select(a => (Guid?)a.ClassId).FirstOrDefaultAsync(cancellationToken);
            if (classId is null || !scope.Contains(classId.Value))
                return Result<IReadOnlyCollection<AssignmentFileDto>>.Fail("FORBIDDEN", "Not your assignment.");
        }

        var items = await db.AssignmentFiles.AsNoTracking()
            .Where(f => f.AssignmentId == request.AssignmentId)
            .OrderBy(f => f.CreatedAt)
            .Select(f => new AssignmentFileDto(f.Id, f.AssignmentId, f.OriginalFileName,
                f.MimeType, f.FileSize, f.CreatedAt))
            .ToListAsync(cancellationToken);
        return Result<IReadOnlyCollection<AssignmentFileDto>>.Ok(items);
    }

    public async Task<Result<AssignmentFileDownloadDto>> Handle(
        GetAssignmentFileForDownloadQuery request, CancellationToken cancellationToken)
    {
        var row = await db.AssignmentFiles.AsNoTracking()
            .Where(f => f.Id == request.FileId)
            .Join(db.Assignments, f => f.AssignmentId, a => a.Id, (f, a) => new { f, a.ClassId })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null) return Result<AssignmentFileDownloadDto>.Fail("NOT_FOUND", "File not found.");

        // SECURITY: staff any; a student must be enrolled in the assignment's class.
        var scope = await NonStaffEnrolledClassScopeAsync(cancellationToken);
        if (scope is not null && !scope.Contains(row.ClassId))
            return Result<AssignmentFileDownloadDto>.Fail("FORBIDDEN", "Not your assignment.");

        return Result<AssignmentFileDownloadDto>.Ok(
            new AssignmentFileDownloadDto(row.f.StoredFileName, row.f.OriginalFileName, row.f.MimeType));
    }

    public async Task<Result<IReadOnlyCollection<AssignmentDto>>> Handle(GetStudentAssignmentsQuery request,
        CancellationToken cancellationToken)
    {
        // SECURITY: a student caller may only read their OWN assignments. Staff
        // (StaffProfileId present) can read any student's for teaching workflows.
        if (currentUser.StaffProfileId is null &&
            (currentUser.StudentProfileId is null || currentUser.StudentProfileId != request.StudentProfileId))
        {
            return Result<IReadOnlyCollection<AssignmentDto>>.Fail(
                "FORBIDDEN", "You can only view your own assignments.");
        }

        var enrolledClassIds = await db.Enrollments
            .Where(x => x.StudentProfileId == request.StudentProfileId)
            .Select(x => x.ClassId)
            .ToListAsync(cancellationToken);

        // An assignment is visible to this student if:
        //  • The student is enrolled in the assignment's class AND
        //    (the assignment has NO assignee restriction
        //     OR the student is in the assignee set)
        // No assignee rows = whole class. Some rows = targeted subset.
        var rows = await db.Assignments
            .Where(a => enrolledClassIds.Contains(a.ClassId))
            .Where(a =>
                !db.AssignmentAssignees.Any(aa => aa.AssignmentId == a.Id) ||
                db.AssignmentAssignees.Any(aa =>
                    aa.AssignmentId == a.Id && aa.StudentProfileId == request.StudentProfileId))
            .Select(a => new
            {
                Dto = new AssignmentDto(a.Id, a.ClassId, a.Title, a.Status, a.CreatedByTeacherId, a.DueDate, a.Description),
                LessonDate = a.ClassSessionId == null
                    ? (DateOnly?)null
                    : db.ClassSessions.Where(s => s.Id == a.ClassSessionId)
                        .Select(s => (DateOnly?)s.SessionDate).FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        // F3↔F4 date-gate: a student only sees a lesson's homework on/after the lesson
        // day (school-local, shared rule). Staff see everything regardless of date.
        var isStudent = currentUser.StaffProfileId is null;
        var schoolToday = SchoolCalendar.Today(DateTime.UtcNow);
        var data = rows
            .Where(r => !isStudent || SchoolCalendar.IsLessonHomeworkVisibleToStudent(r.LessonDate, schoolToday))
            .Select(r => r.Dto)
            .ToList();

        return Result<IReadOnlyCollection<AssignmentDto>>.Ok(data);
    }

    public async Task<Result<AssignmentDto>> Handle(PublishAssignmentCommand request,
        CancellationToken cancellationToken)
    {
        var a = await db.Assignments.FirstOrDefaultAsync(x => x.Id == request.AssignmentId, cancellationToken);
        if (a is null) return Result<AssignmentDto>.Fail("NOT_FOUND", "Assignment not found.");
        a.Publish();
        await db.SaveChangesAsync(cancellationToken);
        await NotifyTargetedStudentsAsync(a, cancellationToken);
        return Result<AssignmentDto>.Ok(Map(a));
    }

    /// <summary>
    /// Fire-and-forget Telegram DM to the students this assignment targets
    /// (its explicit assignees, or the whole class when none are set) who have
    /// linked their Telegram. Sent via the platform bot. Never throws — the
    /// notifier swallows failures, so publishing always succeeds.
    /// </summary>
    private async Task NotifyTargetedStudentsAsync(Assignment a, CancellationToken ct)
    {
        var assigneeIds = await db.AssignmentAssignees
            .Where(x => x.AssignmentId == a.Id).Select(x => x.StudentProfileId).ToListAsync(ct);
        var profileIds = assigneeIds.Count > 0
            ? assigneeIds
            : await db.Enrollments.Where(e => e.ClassId == a.ClassId)
                .Select(e => e.StudentProfileId).ToListAsync(ct);
        if (profileIds.Count == 0) return;

        var userIds = await db.StudentProfiles
            .Where(s => profileIds.Contains(s.Id)).Select(s => s.UserId).ToListAsync(ct);
        var telegramIds = await db.TelegramAccounts
            .Where(t => userIds.Contains(t.UserId)).Select(t => t.TelegramUserId).ToListAsync(ct);
        if (telegramIds.Count == 0) return;

        var className = await db.Classes
            .Where(c => c.Id == a.ClassId).Select(c => c.Title).FirstOrDefaultAsync(ct);
        var due = a.DueDate is { } d ? $"\nDue: {d:yyyy-MM-dd HH:mm} UTC" : "";
        var text =
            $"📚 New assignment: {a.Title}" +
            (string.IsNullOrWhiteSpace(className) ? "" : $"\nClass: {className}") +
            due +
            "\n\nOpen EduVibe to view it.";

        foreach (var telegramId in telegramIds)
            await notifier.SendToUserAsync(telegramId, text, ct);
    }

    public async Task<Result<AssignmentDto>> Handle(UpdateAssignmentCommand request,
        CancellationToken cancellationToken)
    {
        var a = await db.Assignments.FirstOrDefaultAsync(x => x.Id == request.AssignmentId, cancellationToken);
        if (a is null) return Result<AssignmentDto>.Fail("NOT_FOUND", "Assignment not found.");
        a.UpdateTitle(request.Title);
        a.SetDueDate(request.DueDate.HasValue
            ? DateTime.SpecifyKind(request.DueDate.Value, DateTimeKind.Utc)
            : null);
        a.SetDescription(request.Description);
        await db.SaveChangesAsync(cancellationToken);
        return Result<AssignmentDto>.Ok(Map(a));
    }

    public async Task<Result<IReadOnlyCollection<AssignmentDto>>> Handle(GetAssignmentsQuery request,
        CancellationToken cancellationToken)
    {
        var q = db.Assignments.AsQueryable();
        if (request.TeacherUserId is { } teacherId)
            q = q.Where(x => x.CreatedByTeacherId == teacherId);
        if (request.ClassId is { } classId)
            q = q.Where(x => x.ClassId == classId);
        if (request.Status is { } status)
            q = q.Where(x => x.Status == status);

        // SECURITY: non-staff callers only see assignments for their enrolled classes.
        var scope = await NonStaffEnrolledClassScopeAsync(cancellationToken);
        if (scope is not null) q = q.Where(x => scope.Contains(x.ClassId));

        var data = await q
            .OrderByDescending(x => x.CreatedAt)
            .Select(a => new AssignmentDto(a.Id, a.ClassId, a.Title, a.Status, a.CreatedByTeacherId, a.DueDate, a.Description))
            .ToListAsync(cancellationToken);
        return Result<IReadOnlyCollection<AssignmentDto>>.Ok(data);
    }

    // ----- Book attachments ------------------------------------------------

    public async Task<Result<AssignmentBookDto>> Handle(AttachBookToAssignmentCommand request,
        CancellationToken cancellationToken)
    {
        var assignmentExists = await db.Assignments.AnyAsync(a => a.Id == request.AssignmentId, cancellationToken);
        if (!assignmentExists) return Result<AssignmentBookDto>.Fail("NOT_FOUND", "Assignment not found.");
        var bookExists = await db.Books.AnyAsync(b => b.Id == request.BookId, cancellationToken);
        if (!bookExists) return Result<AssignmentBookDto>.Fail("NOT_FOUND", "Book not found.");

        var existing = await db.AssignmentBooks.FirstOrDefaultAsync(
            x => x.AssignmentId == request.AssignmentId && x.BookId == request.BookId,
            cancellationToken);
        if (existing is not null)
        {
            existing.SetNote(request.Note);
            await db.SaveChangesAsync(cancellationToken);
            return Result<AssignmentBookDto>.Ok(
                new AssignmentBookDto(existing.Id, existing.BookId, existing.Note),
                "Note updated.");
        }

        var link = new AssignmentBook(request.AssignmentId, request.BookId, request.Note);
        await db.AssignmentBooks.AddAsync(link, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Result<AssignmentBookDto>.Ok(new AssignmentBookDto(link.Id, link.BookId, link.Note));
    }

    public async Task<Result> Handle(DetachBookFromAssignmentCommand request, CancellationToken cancellationToken)
    {
        var link = await db.AssignmentBooks.FirstOrDefaultAsync(
            x => x.AssignmentId == request.AssignmentId && x.BookId == request.BookId,
            cancellationToken);
        if (link is null) return Result.Fail("NOT_FOUND", "Book is not attached to this assignment.");
        db.AssignmentBooks.Remove(link);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Ok("Book detached.");
    }

    public async Task<Result<IReadOnlyCollection<AssignmentBookDto>>> Handle(
        GetAssignmentBooksQuery request, CancellationToken cancellationToken)
    {
        var items = await db.AssignmentBooks
            .Where(x => x.AssignmentId == request.AssignmentId)
            .Select(x => new AssignmentBookDto(x.Id, x.BookId, x.Note))
            .ToListAsync(cancellationToken);
        return Result<IReadOnlyCollection<AssignmentBookDto>>.Ok(items);
    }

    // ----- Per-student targeting ------------------------------------------

    public async Task<Result<IReadOnlyCollection<AssignmentAssigneeDto>>> Handle(
        SetAssignmentAssigneesCommand request, CancellationToken cancellationToken)
    {
        var assignment = await db.Assignments.FirstOrDefaultAsync(a => a.Id == request.AssignmentId, cancellationToken);
        if (assignment is null)
            return Result<IReadOnlyCollection<AssignmentAssigneeDto>>.Fail("NOT_FOUND", "Assignment not found.");

        // Only allow assignees who are actually enrolled in the assignment's class.
        var enrolled = await db.Enrollments
            .Where(e => e.ClassId == assignment.ClassId)
            .Select(e => e.StudentProfileId)
            .ToListAsync(cancellationToken);
        var enrolledSet = enrolled.ToHashSet();

        var requested = request.StudentProfileIds.Where(id => enrolledSet.Contains(id)).Distinct().ToList();

        var existing = await db.AssignmentAssignees
            .Where(a => a.AssignmentId == request.AssignmentId)
            .ToListAsync(cancellationToken);
        var existingSet = existing.Select(e => e.StudentProfileId).ToHashSet();

        var toRemove = existing.Where(e => !requested.Contains(e.StudentProfileId)).ToList();
        var toAdd = requested.Where(id => !existingSet.Contains(id))
            .Select(id => new AssignmentAssignee(request.AssignmentId, id))
            .ToList();

        if (toRemove.Count > 0) db.AssignmentAssignees.RemoveRange(toRemove);
        if (toAdd.Count > 0) await db.AssignmentAssignees.AddRangeAsync(toAdd, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        var final = requested
            .Select(id => new AssignmentAssigneeDto(request.AssignmentId, id))
            .ToList();
        return Result<IReadOnlyCollection<AssignmentAssigneeDto>>.Ok(final);
    }

    public async Task<Result<IReadOnlyCollection<AssignmentAssigneeDto>>> Handle(
        GetAssignmentAssigneesQuery request, CancellationToken cancellationToken)
    {
        var items = await db.AssignmentAssignees
            .Where(a => a.AssignmentId == request.AssignmentId)
            .Select(a => new AssignmentAssigneeDto(a.AssignmentId, a.StudentProfileId))
            .ToListAsync(cancellationToken);
        return Result<IReadOnlyCollection<AssignmentAssigneeDto>>.Ok(items);
    }

    private static AssignmentDto Map(Assignment a)
    {
        return new AssignmentDto(a.Id, a.ClassId, a.Title, a.Status, a.CreatedByTeacherId, a.DueDate, a.Description);
    }
}