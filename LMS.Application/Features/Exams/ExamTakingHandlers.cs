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
    IRequestHandler<SubmitExamAttemptCommand, Result<ExamAttemptDto>>
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
                savedBySection.TryGetValue(s.Id, out var saved) ? saved : null))
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
                existing.SetResponse(r.ResponseText, r.SelfScore);
            }
            else
            {
                var added = new ExamSectionResponse(attempt.Id, r.ExamSectionId, r.ResponseText, r.SelfScore);
                await db.ExamSectionResponses.AddAsync(added, ct);
                attempt.Responses.Add(added);
            }
        }
        attempt.Submit(DateTime.UtcNow);
        await db.SaveChangesAsync(ct);
        return Result<ExamAttemptDto>.Ok(MapAttempt(attempt));
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
