using System.Text.Json;
using System.Text.Json.Nodes;
using LMS.Application.Common;
using LMS.Application.Common.Abstractions;
using LMS.Application.Common.Models;
using LMS.Domain.Entities;
using LMS.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LMS.Application.Features.Exercises;

public sealed class ExercisesHandlers(IApplicationDbContext db) :
    IRequestHandler<AddExercisesToLessonCommand, Result<IReadOnlyList<Guid>>>,
    IRequestHandler<GetLessonExercisesQuery, Result<IReadOnlyList<ExerciseWithResultDto>>>,
    IRequestHandler<SubmitExerciseAnswerCommand, Result<SubmitResultDto>>,
    IRequestHandler<GetLessonExerciseResultsQuery, Result<LessonExerciseResultsDto>>,
    IRequestHandler<GetWritingSubmissionsQuery, Result<IReadOnlyList<WritingExerciseReviewDto>>>,
    IRequestHandler<GradeExerciseSubmissionCommand, Result<WritingGradeDto>>
{
    public async Task<Result<IReadOnlyList<Guid>>> Handle(AddExercisesToLessonCommand request, CancellationToken ct)
    {
        if (request.Exercises is null || request.Exercises.Count == 0)
            return Result<IReadOnlyList<Guid>>.Fail("VALIDATION", "At least one exercise is required.");
        if (!await db.CurriculumLessons.AnyAsync(l => l.Id == request.LessonId, ct))
            return Result<IReadOnlyList<Guid>>.Fail("NOT_FOUND", "Lesson not found.");

        // Upsert by (LessonId, OrderIndex), atomically (retriable transaction).
        return await db.ExecuteInTransactionAsync<IReadOnlyList<Guid>>(async () =>
        {
            var byOrder = (await db.LessonExercises
                    .Where(e => e.LessonId == request.LessonId).ToListAsync(ct))
                .ToDictionary(e => e.OrderIndex);

            var ids = new List<Guid>(request.Exercises.Count);
            foreach (var dto in request.Exercises)
            {
                var contentJson = dto.Content.ValueKind == JsonValueKind.Undefined ? "{}" : dto.Content.GetRawText();
                if (byOrder.TryGetValue(dto.OrderIndex, out var ex))
                {
                    ex.Update(dto.Type, dto.Title ?? string.Empty, dto.OrderIndex, contentJson);
                    ids.Add(ex.Id);
                }
                else
                {
                    var created = new LessonExercise(
                        request.LessonId, dto.Type, dto.Title ?? string.Empty, dto.OrderIndex, contentJson);
                    await db.LessonExercises.AddAsync(created, ct);
                    byOrder[dto.OrderIndex] = created;
                    ids.Add(created.Id);
                }
            }

            // Sync: drop exercises the client no longer includes (removed in the editor).
            // The authoring dialog always sends the FULL desired set, so this only deletes
            // what the teacher explicitly removed. Cascades that exercise's self-check
            // submissions — intentional, since the lesson's exercise set is being redefined.
            var keptOrders = request.Exercises.Select(e => e.OrderIndex).ToHashSet();
            var removed = byOrder.Values.Where(e => !keptOrders.Contains(e.OrderIndex)).ToList();
            if (removed.Count > 0) db.LessonExercises.RemoveRange(removed);

            await db.SaveChangesAsync(ct);
            return Result<IReadOnlyList<Guid>>.Ok(ids);
        }, ct);
    }

    public async Task<Result<IReadOnlyList<ExerciseWithResultDto>>> Handle(
        GetLessonExercisesQuery request, CancellationToken ct)
    {
        // Single query: each exercise with the user's result via a correlated LEFT JOIN.
        var rows = await db.LessonExercises.AsNoTracking()
            .Where(e => e.LessonId == request.LessonId)
            .OrderBy(e => e.OrderIndex)
            .Select(e => new
            {
                e.Id,
                e.Type,
                e.Title,
                e.OrderIndex,
                e.ContentJson,
                Sub = db.LessonExerciseSubmissions
                    .Where(s => s.LessonExerciseId == e.Id && s.UserId == request.UserId)
                    .Select(s => new
                    {
                        s.AnswersJson, s.Score, s.Total, s.IsCompleted,
                        s.TeacherScore, s.TeacherMaxScore, s.TeacherFeedback, s.GradedAt,
                    })
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        var items = rows.Select(r => new ExerciseWithResultDto(
                r.Id, r.Type, r.Title, r.OrderIndex, ParseNode(r.ContentJson),
                r.Sub is null
                    ? null
                    : new ExerciseResultDto(
                        ParseNode(r.Sub.AnswersJson), r.Sub.Score, r.Sub.Total, r.Sub.IsCompleted,
                        r.Sub.TeacherScore, r.Sub.TeacherMaxScore, r.Sub.TeacherFeedback, r.Sub.GradedAt != null)))
            .ToList();

        return Result<IReadOnlyList<ExerciseWithResultDto>>.Ok(items);
    }

    public async Task<Result<SubmitResultDto>> Handle(SubmitExerciseAnswerCommand request, CancellationToken ct)
    {
        var ex = await db.LessonExercises.AsNoTracking()
            .Where(e => e.Id == request.LessonExerciseId)
            .Select(e => new { e.Type, e.ContentJson })
            .FirstOrDefaultAsync(ct);
        if (ex is null) return Result<SubmitResultDto>.Fail("NOT_FOUND", "Exercise not found.");

        var (score, total) = ExerciseChecker.Check(ex.Type, ex.ContentJson, request.Answers);
        var answersJson = request.Answers.ValueKind == JsonValueKind.Undefined ? "{}" : request.Answers.GetRawText();

        return await db.ExecuteInTransactionAsync<SubmitResultDto>(async () =>
        {
            var sub = await db.LessonExerciseSubmissions.FirstOrDefaultAsync(
                s => s.LessonExerciseId == request.LessonExerciseId && s.UserId == request.UserId, ct);
            if (sub is null)
            {
                sub = new LessonExerciseSubmission(request.LessonExerciseId, request.UserId, answersJson, score, total);
                await db.LessonExerciseSubmissions.AddAsync(sub, ct);
            }
            else
            {
                sub.Apply(answersJson, score, total);
            }

            // Game rewards: advance the daily streak on any activity, and grant XP
            // ONCE for a perfect completion (2/slot, 5..40). Both flush in the
            // SaveChanges below so they can never drift from the submission row.
            // Non-students (no profile) are skipped cleanly.
            var awardedXp = 0;
            var sp = await db.StudentProfiles.FirstOrDefaultAsync(p => p.UserId == request.UserId, ct);
            if (sp is not null)
            {
                sp.RegisterDailyActivity(SchoolCalendar.Today(DateTime.UtcNow));
                if (!sub.XpAwarded && total > 0 && score == total)
                {
                    // A teacher-set content.xp overrides the automatic per-slot amount.
                    var xp = ExerciseXp.CustomFromContent(ex.ContentJson) ?? ExerciseXp.ForCompletion(total);
                    if (xp > 0)
                    {
                        sp.AddXp(xp);
                        await db.XpLedger.AddAsync(
                            XpLedger.CreateEntry(sp.Id, xp, XpSourceType.Exercise, "Exercise complete"), ct);
                        sub.MarkXpAwarded();
                        awardedXp = xp;
                    }
                }

                // Auto-badges: award each milestone the student now qualifies for but
                // doesn't yet hold (once each). Badge XP counts toward their total too.
                var earnedCodes = GameBadges.EarnedFor(awardedXp > 0, sp.Streak).ToList();
                if (earnedCodes.Count > 0)
                {
                    var held = await db.StudentBadges.Where(x => x.StudentProfileId == sp.Id).ToListAsync(ct);
                    var heldIds = held.Select(x => x.BadgeId).ToHashSet();
                    var toAward = await db.Badges
                        .Where(bd => earnedCodes.Contains(EF.Property<string>(bd, "Code")))
                        .Select(bd => new { bd.Id, bd.XpReward })
                        .ToListAsync(ct);
                    foreach (var badge in toAward.Where(bd => !heldIds.Contains(bd.Id)))
                    {
                        await db.StudentBadges.AddAsync(StudentBadge.Award(sp.Id, badge.Id, held), ct);
                        if (badge.XpReward > 0)
                        {
                            sp.AddXp(badge.XpReward);
                            await db.XpLedger.AddAsync(
                                XpLedger.CreateEntry(sp.Id, badge.XpReward, XpSourceType.Badge, "Badge"), ct);
                        }
                    }
                }
            }

            await db.SaveChangesAsync(ct);
            // The game shows the real reward: XP granted now, the fresh streak, and
            // the numeric level derived from the (possibly just-bumped) total XP.
            return Result<SubmitResultDto>.Ok(new SubmitResultDto(
                score, total, awardedXp, sp?.Streak ?? 0, LevelCurve.LevelFor(sp?.XP ?? 0)));
        }, ct);
    }

    public async Task<Result<LessonExerciseResultsDto>> Handle(
        GetLessonExerciseResultsQuery request, CancellationToken ct)
    {
        var exercises = await db.LessonExercises.AsNoTracking()
            .Where(e => e.LessonId == request.LessonId)
            .OrderBy(e => e.OrderIndex)
            .Select(e => new ExerciseResultsHeaderDto(e.Id, e.Title, e.Type, e.OrderIndex))
            .ToListAsync(ct);
        var exerciseIds = exercises.Select(e => e.Id).ToList();

        // The class's active students → (userId, name).
        var students = await db.Enrollments.AsNoTracking()
            .Where(en => en.ClassId == request.ClassId && en.Status != EnrollmentStatus.Dropped)
            .Join(db.StudentProfiles, en => en.StudentProfileId, sp => sp.Id,
                (en, sp) => new { sp.UserId, sp.FirstName, sp.LastName })
            .ToListAsync(ct);
        var userIds = students.Select(s => s.UserId).ToList();

        var subs = exerciseIds.Count == 0 || userIds.Count == 0
            ? new List<StudentSub>()
            : await db.LessonExerciseSubmissions.AsNoTracking()
                .Where(s => exerciseIds.Contains(s.LessonExerciseId) && userIds.Contains(s.UserId))
                .Select(s => new StudentSub(s.UserId, s.LessonExerciseId, s.Score, s.Total, s.IsCompleted))
                .ToListAsync(ct);
        var byUser = subs.GroupBy(s => s.UserId).ToDictionary(g => g.Key, g => g.ToList());

        var summaries = students
            .Select(s =>
            {
                var mine = byUser.TryGetValue(s.UserId, out var list) ? list : new List<StudentSub>();
                var results = mine
                    .Select(r => new StudentExerciseResultDto(r.ExerciseId, r.Score, r.Total, r.IsCompleted))
                    .ToList();
                var name = $"{s.FirstName} {s.LastName}".Trim();
                return new StudentExerciseSummaryDto(
                    s.UserId, string.IsNullOrWhiteSpace(name) ? "Student" : name,
                    results.Count(r => r.IsCompleted), exercises.Count, results);
            })
            .OrderBy(s => s.StudentName)
            .ToList();

        return Result<LessonExerciseResultsDto>.Ok(new LessonExerciseResultsDto(request.LessonId, exercises, summaries));
    }

    public async Task<Result<IReadOnlyList<WritingExerciseReviewDto>>> Handle(
        GetWritingSubmissionsQuery request, CancellationToken ct)
    {
        var exercises = await db.LessonExercises.AsNoTracking()
            .Where(e => e.LessonId == request.LessonId && e.Type == "writing")
            .OrderBy(e => e.OrderIndex)
            .Select(e => new { e.Id, e.Title, e.OrderIndex, e.ContentJson })
            .ToListAsync(ct);
        if (exercises.Count == 0)
            return Result<IReadOnlyList<WritingExerciseReviewDto>>.Ok(Array.Empty<WritingExerciseReviewDto>());

        var exIds = exercises.Select(e => e.Id).ToList();

        // The class's active students → (userId, name).
        var students = await db.Enrollments.AsNoTracking()
            .Where(en => en.ClassId == request.ClassId && en.Status != EnrollmentStatus.Dropped)
            .Join(db.StudentProfiles, en => en.StudentProfileId, sp => sp.Id,
                (en, sp) => new { sp.UserId, sp.FirstName, sp.LastName })
            .ToListAsync(ct);
        var nameByUser = students.ToDictionary(s => s.UserId, s => $"{s.FirstName} {s.LastName}".Trim());
        var userIds = students.Select(s => s.UserId).ToList();

        var subs = exIds.Count == 0 || userIds.Count == 0
            ? new List<SubRow>()
            : await db.LessonExerciseSubmissions.AsNoTracking()
                .Where(s => exIds.Contains(s.LessonExerciseId) && userIds.Contains(s.UserId))
                .Select(s => new SubRow(
                    s.Id, s.LessonExerciseId, s.UserId, s.AnswersJson,
                    s.TeacherScore, s.TeacherMaxScore, s.TeacherFeedback, s.GradedAt,
                    s.CompletedAt, s.UpdatedAt))
                .ToListAsync(ct);
        var byExercise = subs.GroupBy(s => s.ExerciseId).ToDictionary(g => g.Key, g => g.ToList());

        var result = exercises.Select(e =>
        {
            var (instructions, minWords, modelAnswer) = ReadWritingMeta(e.ContentJson);
            var mine = byExercise.TryGetValue(e.Id, out var list) ? list : new List<SubRow>();
            var reviews = mine
                .Select(s =>
                {
                    var text = ReadWritingText(s.AnswersJson);
                    var wordCount = string.IsNullOrWhiteSpace(text)
                        ? 0
                        : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
                    var name = nameByUser.TryGetValue(s.UserId, out var n) && n.Length > 0 ? n : "Student";
                    return new WritingSubmissionReviewDto(
                        s.Id, s.UserId, name, text, wordCount,
                        s.TeacherScore, s.TeacherMaxScore, s.TeacherFeedback, s.GradedAt != null,
                        s.CompletedAt ?? s.UpdatedAt);
                })
                .OrderBy(r => r.StudentName)
                .ToList();
            return new WritingExerciseReviewDto(e.Id, e.Title, e.OrderIndex, instructions, minWords, modelAnswer, reviews);
        }).ToList();

        return Result<IReadOnlyList<WritingExerciseReviewDto>>.Ok(result);
    }

    public async Task<Result<WritingGradeDto>> Handle(GradeExerciseSubmissionCommand request, CancellationToken ct)
    {
        if (request.MaxScore <= 0)
            return Result<WritingGradeDto>.Fail("VALIDATION", "Max score must be greater than zero.");
        if (request.Score < 0 || request.Score > request.MaxScore)
            return Result<WritingGradeDto>.Fail("VALIDATION", "Score must be between 0 and the max.");

        return await db.ExecuteInTransactionAsync<WritingGradeDto>(async () =>
        {
            var sub = await db.LessonExerciseSubmissions
                .FirstOrDefaultAsync(s => s.Id == request.SubmissionId, ct);
            if (sub is null) return Result<WritingGradeDto>.Fail("NOT_FOUND", "Submission not found.");

            sub.Grade(request.Score, request.MaxScore, request.Feedback, request.GradedByUserId);
            await db.SaveChangesAsync(ct);
            return Result<WritingGradeDto>.Ok(
                new WritingGradeDto(sub.TeacherScore!.Value, sub.TeacherMaxScore!.Value, sub.TeacherFeedback, sub.GradedAt!.Value));
        }, ct);
    }

    private sealed record StudentSub(Guid UserId, Guid ExerciseId, int Score, int Total, bool IsCompleted);

    private sealed record SubRow(
        Guid Id, Guid ExerciseId, Guid UserId, string AnswersJson,
        decimal? TeacherScore, decimal? TeacherMaxScore, string? TeacherFeedback, DateTime? GradedAt,
        DateTime? CompletedAt, DateTime UpdatedAt);

    /// <summary>Pull the essay text out of a writing submission's answers (<c>{ "text": "…" }</c>).</summary>
    private static string ReadWritingText(string? answersJson)
    {
        if (string.IsNullOrWhiteSpace(answersJson)) return "";
        try
        {
            using var doc = JsonDocument.Parse(answersJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                return t.GetString() ?? "";
            if (root.ValueKind == JsonValueKind.String) return root.GetString() ?? "";
        }
        catch { /* malformed answers → empty */ }
        return "";
    }

    /// <summary>Read a writing exercise's prompt fields from its content JSON.</summary>
    private static (string? Instructions, int? MinWords, string? ModelAnswer) ReadWritingMeta(string? contentJson)
    {
        if (string.IsNullOrWhiteSpace(contentJson)) return (null, null, null);
        try
        {
            using var doc = JsonDocument.Parse(contentJson);
            var root = doc.RootElement;
            string? instr = root.TryGetProperty("instructions", out var i) && i.ValueKind == JsonValueKind.String
                ? i.GetString() : null;
            int? min = root.TryGetProperty("minWords", out var m) && m.ValueKind == JsonValueKind.Number
                && m.TryGetInt32(out var mv) ? mv : null;
            string? model = root.TryGetProperty("modelAnswer", out var ma) && ma.ValueKind == JsonValueKind.String
                ? ma.GetString() : null;
            return (instr, min, model);
        }
        catch { return (null, null, null); }
    }

    private static JsonNode? ParseNode(string? json)
        => string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json);
}
