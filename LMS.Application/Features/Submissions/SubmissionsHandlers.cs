using LMS.Application.Common.Abstractions;
using LMS.Application.Common.Models;
using LMS.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LMS.Application.Features.Submissions;

public sealed class SubmissionsHandlers(
    IApplicationDbContext db, ICurrentUserService currentUser, INotificationService notifications,
    IInboxNotifier inbox) :
    IRequestHandler<SubmitAssignmentCommand, Result<SubmissionDto>>,
    IRequestHandler<SaveSubmissionDraftCommand, Result<SubmissionDto>>,
    IRequestHandler<GradeSubmissionCommand, Result<SubmissionDto>>,
    IRequestHandler<ReturnSubmissionCommand, Result<SubmissionDto>>,
    IRequestHandler<GetAssignmentSubmissionsQuery, Result<IReadOnlyCollection<SubmissionDto>>>,
    IRequestHandler<GetStudentSubmissionsQuery, Result<IReadOnlyCollection<SubmissionDto>>>,
    IRequestHandler<AddSubmissionFileCommand, Result<SubmissionFileDto>>,
    IRequestHandler<RemoveSubmissionFileCommand, Result<string>>,
    IRequestHandler<GetSubmissionFilesQuery, Result<IReadOnlyCollection<SubmissionFileDto>>>,
    IRequestHandler<GetSubmissionFileForDownloadQuery, Result<SubmissionFileDownloadDto>>,
    IRequestHandler<FinalizeSubmissionCommand, Result<SubmissionDto>>,
    IRequestHandler<SetSubmissionLockCommand, Result<SubmissionDto>>,
    IRequestHandler<GetSubmissionAuditQuery, Result<IReadOnlyCollection<SubmissionAuditDto>>>
{
    private bool CallerIsStaff => currentUser.StaffProfileId is not null;

    public async Task<Result<IReadOnlyCollection<SubmissionDto>>> Handle(GetAssignmentSubmissionsQuery request,
        CancellationToken cancellationToken)
    {
        // SECURITY: students see only their OWN submissions; staff get the full
        // roster for grading. Resolve the self-scope FIRST so the ownership filter
        // runs in SQL — a student used to pull the whole class's rows, then discard
        // all but their own in memory.
        Guid? ownProfileId = null;
        if (!CallerIsStaff)
        {
            ownProfileId = currentUser.StudentProfileId;
            if (ownProfileId is null)
                return Result<IReadOnlyCollection<SubmissionDto>>.Fail(
                    "FORBIDDEN", "Caller is neither a student nor staff.");
        }

        var query = db.Submissions.AsNoTracking()
            .Where(x => x.AssignmentId == request.AssignmentId);
        if (ownProfileId is { } pid)
            query = query.Where(x => x.StudentProfileId == pid);

        var items = await query
            .Select(s => new SubmissionDto(s.Id, s.AssignmentId, s.StudentProfileId, s.Content, s.Status, s.Score,
                s.IsLocked, s.Files.Count, s.MaxScore, s.Feedback))
            .ToListAsync(cancellationToken);

        return Result<IReadOnlyCollection<SubmissionDto>>.Ok(items);
    }

    public async Task<Result<IReadOnlyCollection<SubmissionDto>>> Handle(GetStudentSubmissionsQuery request,
        CancellationToken cancellationToken)
    {
        if (!CallerIsStaff)
        {
            if (currentUser.StudentProfileId is null)
                return Result<IReadOnlyCollection<SubmissionDto>>.Fail(
                    "FORBIDDEN", "Caller is neither a student nor staff.");
            if (currentUser.StudentProfileId != request.StudentProfileId)
                return Result<IReadOnlyCollection<SubmissionDto>>.Fail(
                    "FORBIDDEN", "Students may only read their own submissions.");
        }

        return Result<IReadOnlyCollection<SubmissionDto>>.Ok(await db.Submissions
            .AsNoTracking()
            .Where(x => x.StudentProfileId == request.StudentProfileId)
            .Select(s => new SubmissionDto(s.Id, s.AssignmentId, s.StudentProfileId, s.Content, s.Status, s.Score,
                s.IsLocked, s.Files.Count, s.MaxScore, s.Feedback))
            .ToListAsync(cancellationToken));
    }

    public async Task<Result<SubmissionDto>> Handle(GradeSubmissionCommand request, CancellationToken cancellationToken)
    {
        var s = await db.Submissions.Include(x => x.Files)
            .FirstOrDefaultAsync(x => x.Id == request.SubmissionId, cancellationToken);
        if (s is null) return Result<SubmissionDto>.Fail("NOT_FOUND", "Submission not found.");
        s.Grade(request.Score, request.MaxScore, request.Feedback);
        var scoreText = request.MaxScore is { } max ? $"{request.Score}/{max}" : $"{request.Score}";
        db.SubmissionAudits.Add(new SubmissionAudit(s.Id, currentUser.UserId, "graded", $"score={scoreText}"));
        await db.SaveChangesAsync(cancellationToken);

        // Notify the student in-app (Messages) + mirror to Telegram — one call.
        var studentUserId = await db.StudentProfiles
            .Where(sp => sp.Id == s.StudentProfileId)
            .Select(sp => sp.UserId)
            .FirstOrDefaultAsync(cancellationToken);
        if (studentUserId != Guid.Empty)
        {
            var lang = await ResolveStudentLangAsync(studentUserId, cancellationToken);
            await inbox.NotifyAsync(
                studentUserId, currentUser.UserId ?? Guid.Empty,
                SubmissionNotificationTexts.Graded(lang, scoreText), cancellationToken);
        }

        return Result<SubmissionDto>.Ok(Map(s));
    }

    public async Task<Result<SubmissionDto>> Handle(ReturnSubmissionCommand request, CancellationToken ct)
    {
        var s = await db.Submissions.Include(x => x.Files)
            .FirstOrDefaultAsync(x => x.Id == request.SubmissionId, ct);
        if (s is null) return Result<SubmissionDto>.Fail("NOT_FOUND", "Submission not found.");
        s.ReturnForRedo(request.Feedback);
        db.SubmissionAudits.Add(new SubmissionAudit(s.Id, currentUser.UserId, "returned", request.Feedback ?? ""));
        await db.SaveChangesAsync(ct);

        // Let the student know (in-app + Telegram) why it was returned + what to fix.
        var studentUserId = await db.StudentProfiles
            .Where(sp => sp.Id == s.StudentProfileId).Select(sp => sp.UserId).FirstOrDefaultAsync(ct);
        if (studentUserId != Guid.Empty)
        {
            var lang = await ResolveStudentLangAsync(studentUserId, ct);
            await inbox.NotifyAsync(
                studentUserId, currentUser.UserId ?? Guid.Empty,
                SubmissionNotificationTexts.Returned(lang, request.Feedback), ct);
        }

        return Result<SubmissionDto>.Ok(Map(s));
    }

    public async Task<Result<SubmissionDto>> Handle(SubmitAssignmentCommand request,
        CancellationToken cancellationToken)
    {
        // SECURITY: always force the caller's own profile — ignore the wire id.
        var callerStudentProfileId = currentUser.StudentProfileId;
        if (callerStudentProfileId is null)
            return Result<SubmissionDto>.Fail("FORBIDDEN", "Only students may submit assignments.");

        var assignment = await db.Assignments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == request.AssignmentId, cancellationToken);
        if (assignment is null) return Result<SubmissionDto>.Fail("NOT_FOUND", "Assignment not found.");

        // Late is computed server-side from the deadline — never trusted from
        // the client.
        var isLate = assignment.IsPastDue(DateTime.UtcNow);

        var existing = await db.Submissions
            .FirstOrDefaultAsync(x => x.AssignmentId == request.AssignmentId
                                      && x.StudentProfileId == callerStudentProfileId.Value, cancellationToken);
        if (existing is not null)
        {
            // A re-submit of the text body. Blocked once locked.
            if (existing.IsLocked)
                return Result<SubmissionDto>.Fail("LOCKED", "Submission is locked and can no longer be changed.");
            existing.Submit(request.Content, isLate);
            db.SubmissionAudits.Add(new SubmissionAudit(existing.Id, currentUser.UserId, "resubmitted", null));
            await db.SaveChangesAsync(cancellationToken);
            await NotifyTeacherOfSubmissionAsync(assignment, callerStudentProfileId.Value, cancellationToken);
            await NotifyStudentSubmittedAsync(assignment, cancellationToken);
            return Result<SubmissionDto>.Ok(Map(existing));
        }

        var list = Array.Empty<Submission>();
        var s = Submission.Create(request.AssignmentId, callerStudentProfileId.Value, request.Content, list);
        s.Submit(request.Content, isLate);
        await db.Submissions.AddAsync(s, cancellationToken);
        db.SubmissionAudits.Add(new SubmissionAudit(s.Id, currentUser.UserId, "created", isLate ? "late" : null));
        await db.SaveChangesAsync(cancellationToken);
        await NotifyTeacherOfSubmissionAsync(assignment, callerStudentProfileId.Value, cancellationToken);
        await NotifyStudentSubmittedAsync(assignment, cancellationToken);
        return Result<SubmissionDto>.Ok(Map(s));
    }

    /// <summary>
    /// Fire-and-forget Telegram DM to the class's teacher (or, if the class has
    /// no teacher assigned, whoever created the assignment) that a student has
    /// submitted homework. No-op if the teacher hasn't linked Telegram.
    /// </summary>
    private async Task NotifyTeacherOfSubmissionAsync(Assignment assignment, Guid studentProfileId, CancellationToken ct)
    {
        var classTeacher = await db.Classes
            .Where(c => c.Id == assignment.ClassId)
            .Select(c => c.TeacherUserId)
            .FirstOrDefaultAsync(ct);
        var recipient = classTeacher ?? assignment.CreatedByTeacherId;
        if (recipient == Guid.Empty) return;

        var name = await db.StudentProfiles
            .Where(sp => sp.Id == studentProfileId)
            .Select(sp => ((sp.FirstName ?? "") + " " + (sp.LastName ?? "")).Trim())
            .FirstOrDefaultAsync(ct);
        var who = string.IsNullOrWhiteSpace(name) ? "A student" : name;

        await notifications.NotifyUserAsync(
            recipient,
            $"📝 {who} submitted homework: {assignment.Title}.\nOpen EduVibe to review.",
            ct);
    }

    /// <summary>
    /// Confirms to the submitting student (in-app Messages + Telegram mirror) that
    /// their homework was received. Sender is the class teacher (or the assignment
    /// author), so it lands in the normal teacher↔student thread. No-op with no
    /// teacher to attribute it to.
    /// </summary>
    private async Task NotifyStudentSubmittedAsync(Assignment assignment, CancellationToken ct)
    {
        var studentUserId = currentUser.UserId ?? Guid.Empty;
        if (studentUserId == Guid.Empty) return;

        var classTeacher = await db.Classes
            .Where(c => c.Id == assignment.ClassId).Select(c => c.TeacherUserId).FirstOrDefaultAsync(ct);
        var sender = classTeacher ?? assignment.CreatedByTeacherId;

        var lang = await ResolveStudentLangAsync(studentUserId, ct);
        await inbox.NotifyAsync(
            studentUserId, sender, SubmissionNotificationTexts.Submitted(lang, assignment.Title), ct);
    }

    /// <summary>
    /// Best-effort UI language for a user's notifications: the language of the bot
    /// chat behind their linked Telegram account, or "en" when unknown.
    /// </summary>
    private async Task<string> ResolveStudentLangAsync(Guid userId, CancellationToken ct)
    {
        var lang = await (
            from ta in db.TelegramAccounts
            join ts in db.TelegramSubscribers on ta.TelegramUserId equals ts.ChatId
            where ta.UserId == userId
            select ts.LanguageCode).FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(lang) ? "en" : lang;
    }

    public async Task<Result<SubmissionDto>> Handle(SaveSubmissionDraftCommand request,
        CancellationToken cancellationToken)
    {
        // SECURITY: always the caller's own profile — never trust a wire id.
        var me = currentUser.StudentProfileId;
        if (me is null) return Result<SubmissionDto>.Fail("FORBIDDEN", "Only students may save drafts.");

        var assignment = await db.Assignments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == request.AssignmentId, cancellationToken);
        if (assignment is null) return Result<SubmissionDto>.Fail("NOT_FOUND", "Assignment not found.");

        // Late is derived from the deadline server-side, same as a full submit.
        var isLate = assignment.IsPastDue(DateTime.UtcNow);

        var existing = await db.Submissions.Include(s => s.Files)
            .FirstOrDefaultAsync(x => x.AssignmentId == request.AssignmentId
                                      && x.StudentProfileId == me.Value, cancellationToken);
        if (existing is not null)
        {
            // A locked submission is final — autosave must not silently mutate it.
            if (existing.IsLocked)
                return Result<SubmissionDto>.Fail("LOCKED", "Submission is locked and can no longer be changed.");
            existing.Submit(request.Content, isLate); // upsert text; stays unlocked (editable)
            await db.SaveChangesAsync(cancellationToken); // no per-save audit — autosave would flood it
            return Result<SubmissionDto>.Ok(Map(existing));
        }

        var s = Submission.Create(request.AssignmentId, me.Value, request.Content, Array.Empty<Submission>());
        s.Submit(request.Content, isLate);
        await db.Submissions.AddAsync(s, cancellationToken);
        // Audit the creation once (the autosaves that follow stay silent).
        db.SubmissionAudits.Add(new SubmissionAudit(s.Id, currentUser.UserId, "created", "draft"));
        await db.SaveChangesAsync(cancellationToken);
        return Result<SubmissionDto>.Ok(Map(s));
    }

    // ---- File submissions + anti-cheat ------------------------------------

    public async Task<Result<SubmissionFileDto>> Handle(AddSubmissionFileCommand request,
        CancellationToken ct)
    {
        var me = currentUser.StudentProfileId;
        if (me is null) return Result<SubmissionFileDto>.Fail("FORBIDDEN", "Only students may upload submission files.");

        var assignment = await db.Assignments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == request.AssignmentId, ct);
        if (assignment is null) return Result<SubmissionFileDto>.Fail("NOT_FOUND", "Assignment not found.");

        // ANTI-CHEAT: no uploads after the deadline.
        if (assignment.IsPastDue(DateTime.UtcNow))
            return Result<SubmissionFileDto>.Fail("DEADLINE", "The submission deadline has passed.");

        var submission = await db.Submissions.Include(s => s.Files)
            .FirstOrDefaultAsync(s => s.AssignmentId == request.AssignmentId && s.StudentProfileId == me.Value, ct);
        if (submission is null)
        {
            submission = Submission.Create(request.AssignmentId, me.Value, content: null, Array.Empty<Submission>());
            await db.Submissions.AddAsync(submission, ct);
            db.SubmissionAudits.Add(new SubmissionAudit(submission.Id, currentUser.UserId, "created", "file submission"));
        }

        // ANTI-CHEAT: locked submissions are frozen.
        if (submission.IsLocked)
            return Result<SubmissionFileDto>.Fail("LOCKED", "Submission is locked and can no longer be changed.");

        // ANTI-CHEAT: reject an identical re-upload within the same submission.
        if (submission.Files.Any(f => f.Sha256 == request.Sha256))
            return Result<SubmissionFileDto>.Fail("DUPLICATE", "You've already uploaded an identical file.");

        // ANTI-CHEAT: flag the exact same bytes appearing under another
        // student for this assignment. Flag, don't block — the teacher judges.
        var crossDup = await db.SubmissionFiles
            .Where(f => f.Sha256 == request.Sha256)
            .Join(db.Submissions, f => f.SubmissionId, s => s.Id, (f, s) => s)
            .AnyAsync(s => s.AssignmentId == request.AssignmentId && s.StudentProfileId != me.Value, ct);

        var file = new SubmissionFile(submission.Id, request.StoredFileName, request.OriginalFileName,
            request.MimeType, request.FileSize, request.Sha256);
        if (crossDup) file.FlagAsCrossStudentDuplicate();
        await db.SubmissionFiles.AddAsync(file, ct);
        db.SubmissionAudits.Add(new SubmissionAudit(submission.Id, currentUser.UserId, "file-uploaded",
            crossDup ? $"{request.OriginalFileName} [duplicate-flagged]" : request.OriginalFileName));

        await db.SaveChangesAsync(ct);
        return Result<SubmissionFileDto>.Ok(MapFile(file));
    }

    public async Task<Result<string>> Handle(RemoveSubmissionFileCommand request, CancellationToken ct)
    {
        var me = currentUser.StudentProfileId;
        if (me is null) return Result<string>.Fail("FORBIDDEN", "Only students may remove their files.");

        var submission = await db.Submissions.Include(s => s.Files)
            .FirstOrDefaultAsync(s => s.Id == request.SubmissionId, ct);
        if (submission is null) return Result<string>.Fail("NOT_FOUND", "Submission not found.");
        if (submission.StudentProfileId != me.Value)
            return Result<string>.Fail("FORBIDDEN", "This isn't your submission.");
        if (submission.IsLocked)
            return Result<string>.Fail("LOCKED", "Submission is locked and can no longer be changed.");

        var assignment = await db.Assignments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == submission.AssignmentId, ct);
        if (assignment is not null && assignment.IsPastDue(DateTime.UtcNow))
            return Result<string>.Fail("DEADLINE", "The submission deadline has passed.");

        var file = submission.Files.FirstOrDefault(f => f.Id == request.FileId);
        if (file is null) return Result<string>.Fail("NOT_FOUND", "File not found.");

        var storedName = file.StoredFileName;
        db.SubmissionFiles.Remove(file);
        db.SubmissionAudits.Add(new SubmissionAudit(submission.Id, currentUser.UserId, "file-deleted", file.OriginalFileName));
        await db.SaveChangesAsync(ct);
        return Result<string>.Ok(storedName, "Removed");
    }

    public async Task<Result<IReadOnlyCollection<SubmissionFileDto>>> Handle(GetSubmissionFilesQuery request,
        CancellationToken ct)
    {
        var submission = await db.Submissions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == request.SubmissionId, ct);
        if (submission is null)
            return Result<IReadOnlyCollection<SubmissionFileDto>>.Fail("NOT_FOUND", "Submission not found.");

        // SECURITY: a student may only list files on their own submission.
        if (!CallerIsStaff && currentUser.StudentProfileId != submission.StudentProfileId)
            return Result<IReadOnlyCollection<SubmissionFileDto>>.Fail("FORBIDDEN", "This isn't your submission.");

        var files = await db.SubmissionFiles.AsNoTracking()
            .Where(f => f.SubmissionId == request.SubmissionId)
            .OrderBy(f => f.CreatedAt)
            .Select(f => new SubmissionFileDto(f.Id, f.SubmissionId, f.OriginalFileName, f.MimeType, f.FileSize,
                f.IsDuplicateAcrossStudents, f.CreatedAt))
            .ToListAsync(ct);
        return Result<IReadOnlyCollection<SubmissionFileDto>>.Ok(files);
    }

    public async Task<Result<SubmissionFileDownloadDto>> Handle(GetSubmissionFileForDownloadQuery request,
        CancellationToken ct)
    {
        var row = await db.SubmissionFiles.AsNoTracking()
            .Where(f => f.Id == request.FileId)
            .Join(db.Submissions, f => f.SubmissionId, s => s.Id, (f, s) => new { f, s.StudentProfileId })
            .FirstOrDefaultAsync(ct);
        if (row is null) return Result<SubmissionFileDownloadDto>.Fail("NOT_FOUND", "File not found.");

        // SECURITY: own file (student) or any (staff).
        if (!CallerIsStaff && currentUser.StudentProfileId != row.StudentProfileId)
            return Result<SubmissionFileDownloadDto>.Fail("FORBIDDEN", "This isn't your file.");

        return Result<SubmissionFileDownloadDto>.Ok(
            new SubmissionFileDownloadDto(row.f.StoredFileName, row.f.OriginalFileName, row.f.MimeType));
    }

    public async Task<Result<SubmissionDto>> Handle(FinalizeSubmissionCommand request, CancellationToken ct)
    {
        var me = currentUser.StudentProfileId;
        if (me is null) return Result<SubmissionDto>.Fail("FORBIDDEN", "Only students may finalise submissions.");

        var submission = await db.Submissions.Include(s => s.Files)
            .FirstOrDefaultAsync(s => s.Id == request.SubmissionId, ct);
        if (submission is null) return Result<SubmissionDto>.Fail("NOT_FOUND", "Submission not found.");
        if (submission.StudentProfileId != me.Value)
            return Result<SubmissionDto>.Fail("FORBIDDEN", "This isn't your submission.");

        submission.Lock();
        db.SubmissionAudits.Add(new SubmissionAudit(submission.Id, currentUser.UserId, "finalized", null));
        await db.SaveChangesAsync(ct);
        return Result<SubmissionDto>.Ok(Map(submission));
    }

    public async Task<Result<SubmissionDto>> Handle(SetSubmissionLockCommand request, CancellationToken ct)
    {
        var submission = await db.Submissions.Include(s => s.Files)
            .FirstOrDefaultAsync(s => s.Id == request.SubmissionId, ct);
        if (submission is null) return Result<SubmissionDto>.Fail("NOT_FOUND", "Submission not found.");

        if (request.Locked) submission.Lock(); else submission.Unlock();
        db.SubmissionAudits.Add(new SubmissionAudit(submission.Id, currentUser.UserId,
            request.Locked ? "locked" : "unlocked", "by staff"));
        await db.SaveChangesAsync(ct);
        return Result<SubmissionDto>.Ok(Map(submission));
    }

    public async Task<Result<IReadOnlyCollection<SubmissionAuditDto>>> Handle(GetSubmissionAuditQuery request,
        CancellationToken ct)
    {
        var rows = await db.SubmissionAudits.AsNoTracking()
            .Where(a => a.SubmissionId == request.SubmissionId)
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => new SubmissionAuditDto(a.Id, a.SubmissionId, a.ActorUserId, a.Action, a.Detail, a.CreatedAt))
            .ToListAsync(ct);
        return Result<IReadOnlyCollection<SubmissionAuditDto>>.Ok(rows);
    }

    private static SubmissionDto Map(Submission s) =>
        new(s.Id, s.AssignmentId, s.StudentProfileId, s.Content, s.Status, s.Score, s.IsLocked, s.Files?.Count ?? 0,
            s.MaxScore, s.Feedback);

    private static SubmissionFileDto MapFile(SubmissionFile f) =>
        new(f.Id, f.SubmissionId, f.OriginalFileName, f.MimeType, f.FileSize, f.IsDuplicateAcrossStudents, f.CreatedAt);
}
