using LMS.Application.Common.Abstractions;
using LMS.Domain.Entities;
using LMS.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace LMS.Application.Features.Exams;

/// <summary>
/// Shared auto-grading for the HTML Listening/Reading papers: for each target
/// student's latest attempt, matches the captured answers to the section's answer
/// key, converts the raw correct count (out of 40) to an IELTS band, and upserts it
/// as that section's score — other sections' scores + feedback are preserved and the
/// overall % recomputed. Used both at submit time (so results appear automatically,
/// no teacher step) and by the teacher's manual re-grade action.
///
/// Mutates the context but does NOT call SaveChanges — the caller owns the unit of work.
/// </summary>
public static class ExamAutoGradeRunner
{
    public static async Task<int> AutoGradeAsync(
        IApplicationDbContext db, Exam exam, IReadOnlyCollection<Guid> studentIds,
        Guid enteredByUserId, DateTime now, CancellationToken ct)
    {
        var gradable = exam.Sections
            .Where(s => (s.Kind == ExamSectionKind.Listening || s.Kind == ExamSectionKind.Reading)
                        && !string.IsNullOrWhiteSpace(s.AnswerKey))
            .ToList();
        if (gradable.Count == 0 || studentIds.Count == 0) return 0;

        // Latest attempt (with responses) per target student, mirroring the review view.
        var attempts = await db.ExamAttempts.AsNoTracking().Include(a => a.Responses)
            .Where(a => a.ExamId == exam.Id && studentIds.Contains(a.StudentProfileId))
            .ToListAsync(ct);
        var attemptByStudent = attempts
            .GroupBy(a => a.StudentProfileId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.StartedAt).First());

        var maxById = exam.Sections.ToDictionary(s => s.Id, s => s.MaxScore);
        var graded = 0;

        foreach (var sid in studentIds)
        {
            if (!attemptByStudent.TryGetValue(sid, out var attempt)) continue;

            var sectionBands = new List<(Guid SectionId, decimal Band)>();
            foreach (var sec in gradable)
            {
                var resp = attempt.Responses.FirstOrDefault(r => r.ExamSectionId == sec.Id);
                if (resp?.AnswersJson is null) continue;
                if (ExamAutoGrade.Grade(sec.AnswerKey, resp.AnswersJson) is not { } g) continue;
                var band = ExamScoring.IeltsBand(g.Raw, sec.Kind == ExamSectionKind.Reading);
                if (band > sec.MaxScore) band = sec.MaxScore;
                sectionBands.Add((sec.Id, band));
            }
            if (sectionBands.Count == 0) continue;

            var result = await db.ExamResults.Include(r => r.SectionScores)
                .FirstOrDefaultAsync(r => r.ExamId == exam.Id && r.StudentProfileId == sid, ct);
            if (result is null)
            {
                result = new ExamResult(exam.Id, sid, enteredByUserId);
                await db.ExamResults.AddAsync(result, ct);
            }

            foreach (var (sectionId, band) in sectionBands)
            {
                var existing = result.SectionScores.FirstOrDefault(ss => ss.ExamSectionId == sectionId);
                if (existing is null)
                {
                    var added = new ExamSectionScore(result.Id, sectionId, band);
                    await db.ExamSectionScores.AddAsync(added, ct);
                    result.SectionScores.Add(added);
                }
                else
                {
                    existing.SetScore(band);
                }
            }

            // Overall % from whatever sections currently have a score (Writing/Speaking
            // may still be pending manual grading).
            var inputs = result.SectionScores
                .Where(ss => maxById.ContainsKey(ss.ExamSectionId))
                .Select(ss => new ExamSectionInput(ss.Score, maxById[ss.ExamSectionId])).ToList();
            var score = ExamScoring.Compute(inputs, exam.EffectiveThresholdPercent);
            result.Record(score.OverallPercent, score.Passed, enteredByUserId, now);
            graded++;
        }

        return graded;
    }
}
