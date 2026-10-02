using LMS.Application.Common.Abstractions;
using LMS.Application.Common.Models;
using LMS.Application.Common.Security;
using LMS.Domain.Entities;
using LMS.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LMS.Application.Features.Exams;

/// <summary>
/// Student-side exam sitting (E2): serve an exam in real IELTS format (HTML tests
/// for Listening/Reading, prompts for Writing/Speaking), start/resume an attempt,
/// and submit per-section responses. The official band is still entered + published
/// by a teacher (E1) — this only captures the raw sitting. Enrolled students take
/// their own class's exams; staff may preview (no attempt is created for them).
/// </summary>
public sealed class ExamTakingHandlers(IApplicationDbContext db, ICurrentUserService currentUser) :
    IRequestHandler<GetExamForTakingQuery, Result<TakeExamDto>>,
    IRequestHandler<StartExamAttemptCommand, Result<ExamAttemptDto>>,
    IRequestHandler<SubmitExamAttemptCommand, Result<ExamAttemptDto>>,
    IRequestHandler<GetStudentExamAttemptQuery, Result<StudentAttemptDto>>,
    IRequestHandler<GetMyExamsQuery, Result<IReadOnlyCollection<MyExamDto>>>
{
    public async Task<Result<TakeExamDto>> Handle(GetExamForTakingQuery request, CancellationToken ct)
    {
        var exam = await db.Exams.AsNoTracking().Include(e => e.Sections)
            .FirstOrDefaultAsync(e => e.Id == request.ExamId, ct);
        if (exam is null) return Result<TakeExamDto>.Fail("NOT_FOUND", "Exam not found.");

        var spid = currentUser.StudentProfileId;
        var canTake = spid is { } id && await IsEnrolledAsync(exam.ClassId, id, ct);
        if (!canTake && !await CanStaffViewAsync(exam.ClassId, ct))
            return Result<TakeExamDto>.Fail("FORBIDDEN", "You can't take this exam.");

        // The student's latest attempt (to resume saved answers + reflect submit state).
        ExamAttempt? attempt = null;
        if (spid is { } sp)
        {
            attempt = await db.ExamAttempts.AsNoTracking().Include(a => a.Responses)
                .Where(a => a.ExamId == exam.Id && a.StudentProfileId == sp)
                .OrderByDescending(a => a.StartedAt)
                .FirstOrDefaultAsync(ct);
        }
        var savedBySection = attempt?.Responses.ToDictionary(r => r.ExamSectionId, r => r.ResponseText)
            ?? new Dictionary<Guid, string?>();

        var sections = exam.Sections.OrderBy(s => s.Order)
            .Select(s => new TakeExamSectionDto(
                s.Id, s.Name, s.Order, s.Kind, s.ContentHtml, s.AudioUrl, s.Prompt, s.DurationMinutes,
                savedBySection.TryGetValue(s.Id, out var saved) ? saved : null, s.ImageUrl,
                ExamTaskJson.Parse(s.TasksJson), s.SpeakingMode))
            .ToList();

        return Result<TakeExamDto>.Ok(new TakeExamDto(
            exam.Id, exam.Title, exam.ExamType,
            attempt?.Id, attempt?.StartedAt, attempt?.IsSubmitted ?? false, sections));
    }

    public async Task<Result<ExamAttemptDto>> Handle(StartExamAttemptCommand request, CancellationToken ct)
    {
        var (exam, spid, err) = await ResolveTakerAsync(request.ExamId, ct);
        if (err is not null) return Result<ExamAttemptDto>.Fail(err.Value.Code, err.Value.Msg);

        var attempt = await db.ExamAttempts
            .Where(a => a.ExamId == exam!.Id && a.StudentProfileId == spid)
            .OrderByDescending(a => a.StartedAt)
            .FirstOrDefaultAsync(ct);
        if (attempt is null)
        {
            attempt = new ExamAttempt(exam!.Id, spid, DateTime.UtcNow);
            await db.ExamAttempts.AddAsync(attempt, ct);
            await db.SaveChangesAsync(ct);
        }
        return Result<ExamAttemptDto>.Ok(MapAttempt(attempt));
    }

    public async Task<Result<ExamAttemptDto>> Handle(SubmitExamAttemptCommand request, CancellationToken ct)
    {
        var (exam, spid, err) = await ResolveTakerAsync(request.ExamId, ct);
        if (err is not null) return Result<ExamAttemptDto>.Fail(err.Value.Code, err.Value.Msg);

        var validSectionIds = await db.ExamSections.Where(s => s.ExamId == exam!.Id)
            .Select(s => s.Id).ToListAsync(ct);
        var validSet = validSectionIds.ToHashSet();

        var attempt = await db.ExamAttempts.Include(a => a.Responses)
            .Where(a => a.ExamId == exam!.Id && a.StudentProfileId == spid)
            .OrderByDescending(a => a.StartedAt)
            .FirstOrDefaultAsync(ct);
        if (attempt is null)
        {
            attempt = new ExamAttempt(exam!.Id, spid, DateTime.UtcNow);
            await db.ExamAttempts.AddAsync(attempt, ct);
        }
        if (attempt.IsSubmitted)
            return Result<ExamAttemptDto>.Fail("CONFLICT", "This exam has already been submitted.");

        var bySection = attempt.Responses.ToDictionary(r => r.ExamSectionId);
        foreach (var r in request.Responses)
        {
            if (!validSet.Contains(r.ExamSectionId)) continue; // ignore unknown sections
            if (bySection.TryGetValue(r.ExamSectionId, out var existing))
            {
                existing.SetResponse(r.ResponseText, r.SelfScore, r.AnswersJson);
            }
            else
            {
                var added = new ExamSectionResponse(attempt.Id, r.ExamSectionId, r.ResponseText, r.SelfScore, r.AnswersJson);
                await db.ExamSectionResponses.AddAsync(added, ct);
                attempt.Responses.Add(added);
            }
        }
        attempt.RecordFocusLosses(request.FocusLossCount);
        attempt.Submit(DateTime.UtcNow);
        await db.SaveChangesAsync(ct);
        return Result<ExamAttemptDto>.Ok(MapAttempt(attempt));
    }

    public async Task<Result<StudentAttemptDto>> Handle(GetStudentExamAttemptQuery request, CancellationToken ct)
    {
        var exam = await db.Exams.AsNoTracking().FirstOrDefaultAsync(e => e.Id == request.ExamId, ct);
        if (exam is null) return Result<StudentAttemptDto>.Fail("NOT_FOUND", "Exam not found.");
        if (!await CanStaffViewAsync(exam.ClassId, ct))
            return Result<StudentAttemptDto>.Fail("FORBIDDEN", "Only the group's teacher or an admin can review attempts.");

        var attempt = await db.ExamAttempts.AsNoTracking().Include(a => a.Responses)
            .Where(a => a.ExamId == request.ExamId && a.StudentProfileId == request.StudentProfileId)
            .OrderByDescending(a => a.StartedAt)
            .FirstOrDefaultAsync(ct);
        if (attempt is null)
            return Result<StudentAttemptDto>.Ok(new StudentAttemptDto(null, null, null, [], 0));

        var sections = await db.ExamSections.AsNoTracking()
            .Where(s => s.ExamId == request.ExamId)
            .Select(s => new { s.Id, s.Name, s.Kind })
            .ToListAsync(ct);
        var sectionById = sections.ToDictionary(s => s.Id);

        var responses = attempt.Responses
            .Where(r => sectionById.ContainsKey(r.ExamSectionId))
            .Select(r => new StudentAttemptResponseDto(
                r.ExamSectionId, sectionById[r.ExamSectionId].Name, sectionById[r.ExamSectionId].Kind,
                r.ResponseText, r.SelfScore, r.AnswersJson))
            .ToList();

        return Result<StudentAttemptDto>.Ok(new StudentAttemptDto(
            attempt.Id, attempt.StartedAt, attempt.SubmittedAt, responses, attempt.FocusLossCount));
    }

    public async Task<Result<IReadOnlyCollection<MyExamDto>>> Handle(GetMyExamsQuery request, CancellationToken ct)
    {
        if (currentUser.StudentProfileId is not { } spid)
            return Result<IReadOnlyCollection<MyExamDto>>.Ok([]);

        var classIds = await db.Enrollments.AsNoTracking()
            .Where(e => e.StudentProfileId == spid && e.Status == EnrollmentStatus.Active)
            .Select(e => e.ClassId).Distinct().ToListAsync(ct);
        if (classIds.Count == 0) return Result<IReadOnlyCollection<MyExamDto>>.Ok([]);

        var exams = await db.Exams.AsNoTracking()
            .Where(e => classIds.Contains(e.ClassId))
            .Select(e => new
            {
                e.Id, e.Title, e.ExamType, e.ClassId,
                ClassTitle = db.Classes.Where(c => c.Id == e.ClassId).Select(c => c.Title).FirstOrDefault(),
                SectionCount = db.ExamSections.Count(s => s.ExamId == e.Id),
                Attempt = db.ExamAttempts.Where(a => a.ExamId == e.Id && a.StudentProfileId == spid)
                    .OrderByDescending(a => a.StartedAt)
                    .Select(a => new { a.Id, a.SubmittedAt }).FirstOrDefault(),
                HasPublishedResult = db.ExamResults.Any(r => r.ExamId == e.Id
                    && r.StudentProfileId == spid && r.IsPublished),
            })
            .ToListAsync(ct);

        var dtos = exams
            .OrderBy(e => e.ClassTitle).ThenBy(e => e.Title)
            .Select(e => new MyExamDto(
                e.Id, e.Title, e.ExamType, e.ClassId, e.ClassTitle, e.SectionCount,
                e.Attempt is not null, e.Attempt?.SubmittedAt is not null, e.HasPublishedResult))
            .ToList();

        return Result<IReadOnlyCollection<MyExamDto>>.Ok(dtos);
    }

    // ----- helpers ---------------------------------------------------------

    private async Task<(Exam? Exam, Guid Spid, (string Code, string Msg)? Err)> ResolveTakerAsync(
        Guid examId, CancellationToken ct)
    {
        var exam = await db.Exams.FirstOrDefaultAsync(e => e.Id == examId, ct);
        if (exam is null) return (null, Guid.Empty, ("NOT_FOUND", "Exam not found."));
        if (currentUser.StudentProfileId is not { } spid)
            return (null, Guid.Empty, ("FORBIDDEN", "Only a student can take an exam."));
        if (!await IsEnrolledAsync(exam.ClassId, spid, ct))
            return (null, Guid.Empty, ("FORBIDDEN", "You're not enrolled in this exam's class."));
        return (exam, spid, null);
    }

    private Task<bool> IsEnrolledAsync(Guid classId, Guid studentProfileId, CancellationToken ct) =>
        db.Enrollments.AnyAsync(e => e.ClassId == classId
            && e.StudentProfileId == studentProfileId && e.Status == EnrollmentStatus.Active, ct);

    private async Task<bool> CanStaffViewAsync(Guid classId, CancellationToken ct)
    {
        if (currentUser.IsAdmin()) return true;
        if (currentUser.UserId is not { } uid) return false;
        return await db.Classes.AnyAsync(c => c.Id == classId
            && (c.TeacherUserId == uid || db.ClassTeachers.Any(t => t.ClassId == c.Id && t.UserId == uid)), ct);
    }

    private static ExamAttemptDto MapAttempt(ExamAttempt a) =>
        new(a.Id, a.ExamId, a.StudentProfileId, a.StartedAt, a.SubmittedAt);
}
