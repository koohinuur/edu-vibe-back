using LMS.Application.Common.Models;
using LMS.Domain.Enums;
using MediatR;

namespace LMS.Application.Features.Exams;

// ---- read DTOs -------------------------------------------------------------

/// <summary>Section metadata for config/lists. HasContent avoids shipping the full HTML here.</summary>
public sealed record ExamSectionDto(
    Guid Id, string Name, decimal MaxScore, int Order,
    ExamSectionKind Kind = ExamSectionKind.Generic,
    string? Prompt = null, string? AudioUrl = null, int? DurationMinutes = null, bool HasContent = false);

public sealed record ExamDto(
    Guid Id,
    Guid ClassId,
    Guid? CurriculumLessonId,
    string Title,
    decimal? PassThresholdPercent,
    decimal EffectiveThresholdPercent,
    IReadOnlyCollection<ExamSectionDto> Sections,
    string? ExamType = null);

public sealed record ExamSectionScoreDto(Guid ExamSectionId, decimal Score, string? Feedback = null);

public sealed record ExamResultDto(
    Guid Id,
    Guid ExamId,
    Guid StudentProfileId,
    decimal OverallPercent,
    bool Passed,
    DateTime EnteredAt,
    IReadOnlyCollection<ExamSectionScoreDto> SectionScores,
    bool IsPublished = false,
    DateTime? PublishedAt = null);

/// <summary>A roster row for the score-entry grid: a student + their current result (null = not yet entered).</summary>
public sealed record ExamRosterRowDto(
    Guid StudentProfileId,
    string? FirstName,
    string? LastName,
    string Email,
    ExamResultDto? Result);

public sealed record ExamRosterDto(ExamDto Exam, IReadOnlyCollection<ExamRosterRowDto> Rows);

/// <summary>Per-section breakdown shown on the student profile.</summary>
public sealed record StudentExamSectionDto(string Name, decimal Score, decimal MaxScore, string? Feedback = null);

public sealed record StudentExamResultDto(
    Guid ExamId,
    string ExamTitle,
    Guid ClassId,
    string? ClassTitle,
    decimal OverallPercent,
    bool Passed,
    decimal ThresholdPercent,
    DateTime EnteredAt,
    IReadOnlyCollection<StudentExamSectionDto> Sections,
    string? ExamType = null);

// ---- write DTOs ------------------------------------------------------------

/// <summary>A requested section in a create/update. Id null = new section.</summary>
public sealed record ExamSectionInputDto(
    Guid? Id, string Name, decimal MaxScore, int Order,
    ExamSectionKind Kind = ExamSectionKind.Generic,
    string? ContentHtml = null, string? AudioUrl = null, string? Prompt = null, int? DurationMinutes = null);

// ---- taking DTOs (student sitting the exam) --------------------------------

/// <summary>One section as the student takes it — carries the full HTML/prompt/audio.</summary>
public sealed record TakeExamSectionDto(
    Guid Id, string Name, int Order, ExamSectionKind Kind,
    string? ContentHtml, string? AudioUrl, string? Prompt, int? DurationMinutes,
    string? SavedResponse);

/// <summary>The exam the student is taking, plus their attempt state.</summary>
public sealed record TakeExamDto(
    Guid ExamId, string Title, string? ExamType,
    Guid? AttemptId, DateTime? StartedAt, bool IsSubmitted,
    IReadOnlyCollection<TakeExamSectionDto> Sections);

/// <summary>A student's answer to one section at submit time.</summary>
public sealed record SectionResponseInputDto(Guid ExamSectionId, string? ResponseText, decimal? SelfScore);

public sealed record ExamAttemptDto(
    Guid Id, Guid ExamId, Guid StudentProfileId, DateTime StartedAt, DateTime? SubmittedAt);

/// <summary>One section's answer, for the teacher reviewing a student's sitting.</summary>
public sealed record StudentAttemptResponseDto(
    Guid ExamSectionId, string SectionName, ExamSectionKind Kind, string? ResponseText, decimal? SelfScore);

/// <summary>A student's sitting of an exam, for teacher review (null attempt = not taken yet).</summary>
public sealed record StudentAttemptDto(
    Guid? AttemptId, DateTime? StartedAt, DateTime? SubmittedAt,
    IReadOnlyCollection<StudentAttemptResponseDto> Responses);

/// <summary>Read a student's attempt (writing answers) for grading. Staff-only.</summary>
public sealed record GetStudentExamAttemptQuery(Guid ExamId, Guid StudentProfileId)
    : IRequest<Result<StudentAttemptDto>>;

/// <summary>An exam the signed-in student can take, with their progress + result state.</summary>
public sealed record MyExamDto(
    Guid ExamId, string Title, string? ExamType, Guid ClassId, string? ClassTitle,
    int SectionCount, bool HasAttempt, bool IsSubmitted, bool HasPublishedResult);

/// <summary>The signed-in student's exams across their enrolled classes.</summary>
public sealed record GetMyExamsQuery : IRequest<Result<IReadOnlyCollection<MyExamDto>>>;

public sealed record SectionScoreInputDto(Guid ExamSectionId, decimal Score, string? Feedback = null);

// ---- commands / queries ----------------------------------------------------

public sealed record CreateExamCommand(
    Guid ClassId,
    Guid? CurriculumLessonId,
    string Title,
    decimal? PassThresholdPercent,
    IReadOnlyCollection<ExamSectionInputDto> Sections,
    string? ExamType = null) : IRequest<Result<ExamDto>>;

public sealed record UpdateExamCommand(
    Guid ExamId,
    string Title,
    decimal? PassThresholdPercent,
    IReadOnlyCollection<ExamSectionInputDto> Sections,
    string? ExamType = null) : IRequest<Result<ExamDto>>;

public sealed record DeleteExamCommand(Guid ExamId) : IRequest<Result>;

public sealed record GetExamByIdQuery(Guid ExamId) : IRequest<Result<ExamDto>>;

public sealed record GetClassExamsQuery(Guid ClassId) : IRequest<Result<IReadOnlyCollection<ExamDto>>>;

public sealed record GetExamRosterQuery(Guid ExamId) : IRequest<Result<ExamRosterDto>>;

/// <summary>Enter/correct one student's per-section scores (idempotent upsert + recompute).</summary>
public sealed record EnterExamResultCommand(
    Guid ExamId,
    Guid StudentProfileId,
    IReadOnlyCollection<SectionScoreInputDto> Scores) : IRequest<Result<ExamResultDto>>;

public sealed record DeleteExamResultCommand(Guid ExamId, Guid StudentProfileId) : IRequest<Result>;

/// <summary>Publish (or unpublish) one student's result — the spec #12 visibility gate.</summary>
public sealed record PublishExamResultCommand(Guid ExamId, Guid StudentProfileId, bool Publish)
    : IRequest<Result<ExamResultDto>>;

public sealed record GetStudentExamResultsQuery(Guid StudentProfileId)
    : IRequest<Result<IReadOnlyCollection<StudentExamResultDto>>>;

// ---- taking commands / queries ---------------------------------------------

/// <summary>The exam an enrolled student is taking (content + their attempt state).</summary>
public sealed record GetExamForTakingQuery(Guid ExamId) : IRequest<Result<TakeExamDto>>;

/// <summary>Starts (or resumes) the caller's attempt at an exam. Idempotent.</summary>
public sealed record StartExamAttemptCommand(Guid ExamId) : IRequest<Result<ExamAttemptDto>>;

/// <summary>Saves the caller's per-section responses and submits their attempt.</summary>
public sealed record SubmitExamAttemptCommand(
    Guid ExamId, IReadOnlyCollection<SectionResponseInputDto> Responses) : IRequest<Result<ExamAttemptDto>>;
