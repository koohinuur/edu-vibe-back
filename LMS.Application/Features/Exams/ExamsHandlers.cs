using LMS.Application.Common.Abstractions;
using LMS.Application.Common.Models;
using LMS.Application.Common.Security;
using LMS.Domain.Entities;
using LMS.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LMS.Application.Features.Exams;

/// <summary>
/// F8 — offline exams. Teachers (self-scoped to their classes) and admins configure
/// exams on exam-type curriculum lessons and enter per-section scores; the system
/// computes overall % + pass/fail via <see cref="ExamScoring"/>. Students read only
/// their own results. Cleanly separate from the F4 online task system.
/// </summary>
public sealed class ExamsHandlers(IApplicationDbContext db, ICurrentUserService currentUser) :
    IRequestHandler<CreateExamCommand, Result<ExamDto>>,
    IRequestHandler<UpdateExamCommand, Result<ExamDto>>,
    IRequestHandler<DeleteExamCommand, Result>,
    IRequestHandler<GetExamByIdQuery, Result<ExamDto>>,
    IRequestHandler<GetClassExamsQuery, Result<IReadOnlyCollection<ExamDto>>>,
    IRequestHandler<GetExamRosterQuery, Result<ExamRosterDto>>,
    IRequestHandler<EnterExamResultCommand, Result<ExamResultDto>>,
    IRequestHandler<PublishExamResultCommand, Result<ExamResultDto>>,
    IRequestHandler<DeleteExamResultCommand, Result>,
    IRequestHandler<GetStudentExamResultsQuery, Result<IReadOnlyCollection<StudentExamResultDto>>>
{
    // ---- config ------------------------------------------------------------

    public async Task<Result<ExamDto>> Handle(CreateExamCommand request, CancellationToken ct)
    {
        var (cls, code, msg) = await ResolveOwnedClassAsync(request.ClassId, ct);
        if (cls is null) return Result<ExamDto>.Fail(code!, msg!);

        if (string.IsNullOrWhiteSpace(request.Title))
            return Result<ExamDto>.Fail("VALIDATION", "Exam title is required.");
        if (request.PassThresholdPercent is < 0m or > 100m)
            return Result<ExamDto>.Fail("VALIDATION", "Pass threshold must be between 0 and 100.");
        var sectionError = ValidateSections(request.Sections);
        if (sectionError is not null) return Result<ExamDto>.Fail("VALIDATION", sectionError);

        var lesson = await db.CurriculumLessons.FirstOrDefaultAsync(l => l.Id == request.CurriculumLessonId, ct);
        if (lesson is null) return Result<ExamDto>.Fail("NOT_FOUND", "Curriculum lesson not found.");
        if (lesson.LessonType != CurriculumLessonType.Exam)
            return Result<ExamDto>.Fail("VALIDATION", "The lesson must be of type Exam.");

        // The lesson must live under this class's own curriculum template.
        var templateId = await (
            from u in db.CurriculumUnits
            join m in db.CurriculumModules on u.ModuleId equals m.Id
            where u.Id == lesson.UnitId
            select (Guid?)m.TemplateId).FirstOrDefaultAsync(ct);
        if (templateId is null || cls.CurriculumTemplateId is null || templateId != cls.CurriculumTemplateId)
            return Result<ExamDto>.Fail("VALIDATION", "The lesson does not belong to this class.");

        if (await db.Exams.AnyAsync(e => e.CurriculumLessonId == request.CurriculumLessonId, ct))
            return Result<ExamDto>.Fail("CONFLICT", "An exam already exists for this lesson.");

        var exam = new Exam(cls.Id, request.CurriculumLessonId, request.Title, request.PassThresholdPercent);
        // Exam type (spec #11) — the create UI pre-fills it from the group's
        // GroupType (feat/group-type), so an IELTS group's exam is an IELTS exam.
        exam.SetExamType(request.ExamType);
        foreach (var s in request.Sections)
            exam.Sections.Add(new ExamSection(exam.Id, s.Name, s.MaxScore, s.Order));
        await db.Exams.AddAsync(exam, ct);
        await db.SaveChangesAsync(ct);
        return Result<ExamDto>.Ok(MapExam(exam), "Exam created.");
    }

    public async Task<Result<ExamDto>> Handle(UpdateExamCommand request, CancellationToken ct)
    {
        var (exam, code, msg) = await ResolveOwnedExamAsync(request.ExamId, ct, tracking: true);
        if (exam is null) return Result<ExamDto>.Fail(code!, msg!);

        if (string.IsNullOrWhiteSpace(request.Title))
            return Result<ExamDto>.Fail("VALIDATION", "Exam title is required.");
        if (request.PassThresholdPercent is < 0m or > 100m)
            return Result<ExamDto>.Fail("VALIDATION", "Pass threshold must be between 0 and 100.");
        var sectionError = ValidateSections(request.Sections);
        if (sectionError is not null) return Result<ExamDto>.Fail("VALIDATION", sectionError);

        // Once any result exists, removing a section or lowering a max is blocked —
        // it would destroy entered per-section data and shift overall %.
        var hasResults = await db.ExamResults.AnyAsync(r => r.ExamId == exam.Id, ct);
        if (hasResults)
        {
            var existing = exam.Sections.Select(s => new ExamSectionState(s.Id, s.MaxScore)).ToList();
            var requested = request.Sections.Select(s => new ExamSectionEdit(s.Id, s.MaxScore)).ToList();
            if (ExamSectionChange.IsDestructive(existing, requested))
                return Result<ExamDto>.Fail("CONFLICT",
                    "Cannot remove a section or lower its max while results exist. Delete all results first to restructure.");
        }

        exam.SetTitle(request.Title);
        exam.SetPassThreshold(request.PassThresholdPercent);
        exam.SetExamType(request.ExamType ?? exam.ExamType);

        var existingById = exam.Sections.ToDictionary(s => s.Id);
        var keepIds = new HashSet<Guid>();
        foreach (var s in request.Sections)
        {
            if (s.Id is { } id && existingById.TryGetValue(id, out var sec))
            {
                sec.SetName(s.Name);
                sec.SetMaxScore(s.MaxScore);
                sec.SetOrder(s.Order);
                keepIds.Add(id);
            }
            else
            {
                var added = new ExamSection(exam.Id, s.Name, s.MaxScore, s.Order);
                await db.ExamSections.AddAsync(added, ct);
                exam.Sections.Add(added);
                keepIds.Add(added.Id);
            }
        }
        var toRemove = exam.Sections.Where(s => !keepIds.Contains(s.Id)).ToList();
        if (toRemove.Count > 0) db.ExamSections.RemoveRange(toRemove);

        await db.SaveChangesAsync(ct);
        var dto = await LoadExamDtoAsync(exam.Id, ct);
        return Result<ExamDto>.Ok(dto!, "Exam updated.");
    }

    public async Task<Result> Handle(DeleteExamCommand request, CancellationToken ct)
    {
        var (exam, code, msg) = await ResolveOwnedExamAsync(request.ExamId, ct, tracking: true);
        if (exam is null) return Result.Fail(code!, msg!);
        db.Exams.Remove(exam); // cascades to sections, results, section scores
        await db.SaveChangesAsync(ct);
        return Result.Ok("Exam deleted.");
    }

    public async Task<Result<ExamDto>> Handle(GetExamByIdQuery request, CancellationToken ct)
    {
        var (exam, code, msg) = await ResolveOwnedExamAsync(request.ExamId, ct, tracking: false);
        if (exam is null) return Result<ExamDto>.Fail(code!, msg!);
        return Result<ExamDto>.Ok(MapExam(exam));
    }

    public async Task<Result<IReadOnlyCollection<ExamDto>>> Handle(GetClassExamsQuery request, CancellationToken ct)
    {
        var (cls, code, msg) = await ResolveOwnedClassAsync(request.ClassId, ct);
        if (cls is null) return Result<IReadOnlyCollection<ExamDto>>.Fail(code!, msg!);
        var exams = await db.Exams.AsNoTracking().Include(e => e.Sections)
            .Where(e => e.ClassId == request.ClassId).ToListAsync(ct);
        return Result<IReadOnlyCollection<ExamDto>>.Ok(exams.Select(MapExam).ToList());
    }

    // ---- roster + score entry ---------------------------------------------

    public async Task<Result<ExamRosterDto>> Handle(GetExamRosterQuery request, CancellationToken ct)
    {
        var (exam, code, msg) = await ResolveOwnedExamAsync(request.ExamId, ct, tracking: false);
        if (exam is null) return Result<ExamRosterDto>.Fail(code!, msg!);

        var students = await (
            from e in db.Enrollments
            join sp in db.StudentProfiles on e.StudentProfileId equals sp.Id
            join u in db.Users on sp.UserId equals u.Id
            where e.ClassId == exam.ClassId && e.Status == EnrollmentStatus.Active
            orderby sp.FirstName, sp.LastName
            select new { sp.Id, sp.FirstName, sp.LastName, u.Email }).ToListAsync(ct);

        var results = await db.ExamResults.AsNoTracking().Include(r => r.SectionScores)
            .Where(r => r.ExamId == exam.Id).ToListAsync(ct);
        var byStudent = results.ToDictionary(r => r.StudentProfileId);

        var rows = students.Select(s => new ExamRosterRowDto(
            s.Id, s.FirstName, s.LastName, s.Email,
            byStudent.TryGetValue(s.Id, out var r) ? MapResult(r) : null)).ToList();

        return Result<ExamRosterDto>.Ok(new ExamRosterDto(MapExam(exam), rows));
    }

    public async Task<Result<ExamResultDto>> Handle(EnterExamResultCommand request, CancellationToken ct)
    {
        var (exam, code, msg) = await ResolveOwnedExamAsync(request.ExamId, ct, tracking: true);
        if (exam is null) return Result<ExamResultDto>.Fail(code!, msg!);

        var enrolled = await db.Enrollments.AnyAsync(e =>
            e.ClassId == exam.ClassId && e.StudentProfileId == request.StudentProfileId
            && e.Status == EnrollmentStatus.Active, ct);
        if (!enrolled) return Result<ExamResultDto>.Fail("VALIDATION", "The student is not enrolled in this class.");

        // Require exactly one score per section.
        var sectionIds = exam.Sections.Select(s => s.Id).ToHashSet();
        var providedIds = request.Scores.Select(s => s.ExamSectionId).ToList();
        if (providedIds.Count != sectionIds.Count
            || providedIds.Distinct().Count() != providedIds.Count
            || !providedIds.All(sectionIds.Contains))
            return Result<ExamResultDto>.Fail("VALIDATION", "Provide exactly one score for each section.");

        var maxById = exam.Sections.ToDictionary(s => s.Id, s => s.MaxScore);
        var inputs = request.Scores.Select(s => new ExamSectionInput(s.Score, maxById[s.ExamSectionId])).ToList();
        if (!ExamScoring.AreScoresValid(inputs))
            return Result<ExamResultDto>.Fail("VALIDATION", "Each score must be between 0 and the section's max.");

        var score = ExamScoring.Compute(inputs, exam.EffectiveThresholdPercent);
        var enteredBy = currentUser.UserId!.Value;
        var now = DateTime.UtcNow;

        // Idempotent upsert — re-entering corrects in place, never duplicates.
        var result = await db.ExamResults.Include(r => r.SectionScores)
            .FirstOrDefaultAsync(r => r.ExamId == exam.Id && r.StudentProfileId == request.StudentProfileId, ct);
        if (result is null)
        {
            result = new ExamResult(exam.Id, request.StudentProfileId, enteredBy);
            await db.ExamResults.AddAsync(result, ct);
        }
        else
        {
            db.ExamSectionScores.RemoveRange(result.SectionScores);
            result.SectionScores.Clear();
        }
        result.Record(score.OverallPercent, score.Passed, enteredBy, now);
        foreach (var s in request.Scores)
        {
            var added = new ExamSectionScore(result.Id, s.ExamSectionId, s.Score);
            added.SetFeedback(s.Feedback); // per-section feedback (spec #12)
            await db.ExamSectionScores.AddAsync(added, ct);
            result.SectionScores.Add(added);
        }
        await db.SaveChangesAsync(ct);
        return Result<ExamResultDto>.Ok(MapResult(result), "Scores saved.");
    }

    public async Task<Result<ExamResultDto>> Handle(PublishExamResultCommand request, CancellationToken ct)
    {
        var (exam, code, msg) = await ResolveOwnedExamAsync(request.ExamId, ct, tracking: true);
        if (exam is null) return Result<ExamResultDto>.Fail(code!, msg!);

        var result = await db.ExamResults.Include(r => r.SectionScores)
            .FirstOrDefaultAsync(r => r.ExamId == request.ExamId && r.StudentProfileId == request.StudentProfileId, ct);
        if (result is null) return Result<ExamResultDto>.Fail("NOT_FOUND", "Enter the result before publishing it.");

        if (request.Publish) result.Publish(DateTime.UtcNow);
        else result.Unpublish();
        await db.SaveChangesAsync(ct);
        return Result<ExamResultDto>.Ok(MapResult(result), request.Publish ? "Result published." : "Result hidden.");
    }

    public async Task<Result> Handle(DeleteExamResultCommand request, CancellationToken ct)
    {
        var (exam, code, msg) = await ResolveOwnedExamAsync(request.ExamId, ct, tracking: true);
        if (exam is null) return Result.Fail(code!, msg!);
        var result = await db.ExamResults.Include(r => r.SectionScores)
            .FirstOrDefaultAsync(r => r.ExamId == request.ExamId && r.StudentProfileId == request.StudentProfileId, ct);
        if (result is null) return Result.Fail("NOT_FOUND", "No result to delete.");
        db.ExamSectionScores.RemoveRange(result.SectionScores);
        db.ExamResults.Remove(result);
        await db.SaveChangesAsync(ct);
        return Result.Ok("Result deleted.");
    }

    // ---- student profile ---------------------------------------------------

    public async Task<Result<IReadOnlyCollection<StudentExamResultDto>>> Handle(
        GetStudentExamResultsQuery request, CancellationToken ct)
    {
        var isSelf = currentUser.StudentProfileId is { } spid && spid == request.StudentProfileId;
        if (!IsAdmin && !isSelf)
        {
            // A teacher may view results only for students in one of their classes.
            var isOwnStudent = await (
                from e in db.Enrollments
                join c in db.Classes on e.ClassId equals c.Id
                where e.StudentProfileId == request.StudentProfileId && c.TeacherUserId == currentUser.UserId
                select e.Id).AnyAsync(ct);
            if (!isOwnStudent)
                return Result<IReadOnlyCollection<StudentExamResultDto>>.Fail(
                    "FORBIDDEN", "You can only view your own exam results.");
        }

        // Load each collection in its own single-collection query — a single query
        // that Includes both SectionScores AND Exam.Sections would trip the context's
        // MultipleCollectionIncludeWarning (configured to throw). Kept provider-agnostic
        // (no AsSplitQuery, which lives in the Relational assembly the Application layer
        // doesn't reference).
        // A student sees only PUBLISHED results (spec #12); an admin or the
        // student's teacher sees every result (published or not) for review.
        var publishedOnly = isSelf && !IsAdmin;

        var results = await db.ExamResults.AsNoTracking()
            .Where(r => r.StudentProfileId == request.StudentProfileId)
            .Where(r => !publishedOnly || r.IsPublished)
            .Include(r => r.SectionScores)
            .OrderByDescending(r => r.EnteredAt)
            .ToListAsync(ct);

        var examIds = results.Select(r => r.ExamId).Distinct().ToList();
        var exams = await db.Exams.AsNoTracking()
            .Where(e => examIds.Contains(e.Id))
            .Include(e => e.Sections) // single collection — safe
            .ToListAsync(ct);
        var examById = exams.ToDictionary(e => e.Id);

        var classIds = exams.Select(e => e.ClassId).Distinct().ToList();
        var classTitleById = await db.Classes.AsNoTracking()
            .Where(c => classIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Title })
            .ToDictionaryAsync(x => x.Id, x => x.Title, ct);

        var dtos = results
            .Where(r => examById.ContainsKey(r.ExamId))
            .Select(r =>
            {
                var exam = examById[r.ExamId];
                var sectionById = exam.Sections.ToDictionary(s => s.Id);
                var sections = r.SectionScores
                    .Where(ss => sectionById.ContainsKey(ss.ExamSectionId))
                    .Select(ss => new { Sec = sectionById[ss.ExamSectionId], ss.Score, ss.Feedback })
                    .OrderBy(x => x.Sec.Order)
                    .Select(x => new StudentExamSectionDto(x.Sec.Name, x.Score, x.Sec.MaxScore, x.Feedback))
                    .ToList();
                return new StudentExamResultDto(
                    r.ExamId, exam.Title, exam.ClassId,
                    classTitleById.TryGetValue(exam.ClassId, out var clsTitle) ? clsTitle : null,
                    r.OverallPercent, r.Passed, exam.EffectiveThresholdPercent, r.EnteredAt, sections, exam.ExamType);
            }).ToList();

        return Result<IReadOnlyCollection<StudentExamResultDto>>.Ok(dtos);
    }

    // ---- shared plumbing ---------------------------------------------------

    private bool IsAdmin => currentUser.IsAdmin();

    private async Task<(Class? Cls, string? Code, string? Msg)> ResolveOwnedClassAsync(
        Guid classId, CancellationToken ct)
    {
        var cls = await db.Classes.AsNoTracking().FirstOrDefaultAsync(c => c.Id == classId, ct);
        if (cls is null) return (null, "NOT_FOUND", "Class not found.");
        if (!IsAdmin && (cls.TeacherUserId is null || cls.TeacherUserId != currentUser.UserId))
            return (null, "FORBIDDEN", "Only the class teacher or an admin can manage exams for this class.");
        return (cls, null, null);
    }

    private async Task<(Exam? Exam, string? Code, string? Msg)> ResolveOwnedExamAsync(
        Guid examId, CancellationToken ct, bool tracking)
    {
        var query = tracking
            ? db.Exams.Include(e => e.Sections)
            : db.Exams.AsNoTracking().Include(e => e.Sections);
        var exam = await query.FirstOrDefaultAsync(e => e.Id == examId, ct);
        if (exam is null) return (null, "NOT_FOUND", "Exam not found.");
        var (cls, code, msg) = await ResolveOwnedClassAsync(exam.ClassId, ct);
        return cls is null ? (null, code, msg) : (exam, null, null);
    }

    private async Task<ExamDto?> LoadExamDtoAsync(Guid examId, CancellationToken ct)
    {
        var e = await db.Exams.AsNoTracking().Include(x => x.Sections)
            .FirstOrDefaultAsync(x => x.Id == examId, ct);
        return e is null ? null : MapExam(e);
    }

    private static string? ValidateSections(IReadOnlyCollection<ExamSectionInputDto> sections)
    {
        if (sections is null || sections.Count == 0) return "At least one section is required.";
        if (sections.Any(s => string.IsNullOrWhiteSpace(s.Name) || s.MaxScore <= 0m))
            return "Each section needs a name and a positive max score.";
        return null;
    }

    private static ExamDto MapExam(Exam e) => new(
        e.Id, e.ClassId, e.CurriculumLessonId, e.Title, e.PassThresholdPercent, e.EffectiveThresholdPercent,
        e.Sections.OrderBy(s => s.Order)
            .Select(s => new ExamSectionDto(s.Id, s.Name, s.MaxScore, s.Order)).ToList(),
        e.ExamType);

    private static ExamResultDto MapResult(ExamResult r) => new(
        r.Id, r.ExamId, r.StudentProfileId, r.OverallPercent, r.Passed, r.EnteredAt,
        r.SectionScores.Select(s => new ExamSectionScoreDto(s.ExamSectionId, s.Score, s.Feedback)).ToList(),
        r.IsPublished, r.PublishedAt);
}
