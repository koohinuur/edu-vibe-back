using System.Text.Json;
using LMS.Application.Common.Abstractions;
using LMS.Application.Common.Security;
using LMS.Application.Features.Exercises;
using LMS.WebApi.Common;
using LMS.WebApi.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LMS.WebApi.Controllers;

/// <summary>
/// Lesson self-check exercises (textbook-style practice). Teachers/admins bulk-author
/// them per curriculum lesson; students fetch them with their own saved results and
/// submit answers for instant self-check scoring. The user is ALWAYS the authenticated
/// caller (no client-supplied userId — avoids IDOR).
/// </summary>
[ApiController]
[Route("api")]
[Authorize]
public sealed class ExercisesController(ISender sender, ICurrentUserService currentUser) : ControllerBase
{
    /// <summary>
    /// Bulk add/update a lesson's exercises (upsert by orderIndex). Teacher/admin only.
    /// Body: <c>{ "exercises": [{ "type", "title", "orderIndex", "content": { … } }] }</c>.
    /// See <see cref="ExerciseChecker"/> / DTO docs for each type's <c>content</c> shape.
    /// </summary>
    [HttpPost("lessons/{lessonId:guid}/exercises/bulk")]
    [PermissionAuthorize(Permissions.Classes.Update)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<Guid>>>> AddBulk(
        Guid lessonId, [FromBody] BulkExercisesRequest body, CancellationToken ct)
    {
        var r = await sender.Send(new AddExercisesToLessonCommand(lessonId, body.Exercises ?? []), ct);
        if (r.Success) return Ok(ApiResponse<IReadOnlyList<Guid>>.Ok(r.Data, r.Message));
        return r.ErrorCode == "NOT_FOUND"
            ? NotFound(ApiResponse<IReadOnlyList<Guid>>.Fail(r.Message ?? "Not found"))
            : BadRequest(ApiResponse<IReadOnlyList<Guid>>.Fail(r.Message ?? "Failed"));
    }

    /// <summary>A lesson's exercises + the current user's saved results (one query).</summary>
    [HttpGet("lessons/{lessonId:guid}/exercises")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<ExerciseWithResultDto>>>> Get(
        Guid lessonId, CancellationToken ct)
    {
        if (currentUser.UserId is not { } userId)
            return Unauthorized(ApiResponse<IReadOnlyList<ExerciseWithResultDto>>.Fail("Not authenticated."));
        var r = await sender.Send(new GetLessonExercisesQuery(lessonId, userId), ct);
        return Ok(ApiResponse<IReadOnlyList<ExerciseWithResultDto>>.Ok(r.Data, r.Message));
    }

    /// <summary>Submit + self-check the current user's answer for one exercise.
    /// Body: <c>{ "answers": &lt;type-specific&gt; }</c>.</summary>
    [HttpPost("exercises/{exerciseId:guid}/submit")]
    public async Task<ActionResult<ApiResponse<SubmitResultDto>>> Submit(
        Guid exerciseId, [FromBody] SubmitExerciseRequest body, CancellationToken ct)
    {
        if (currentUser.UserId is not { } userId)
            return Unauthorized(ApiResponse<SubmitResultDto>.Fail("Not authenticated."));
        var r = await sender.Send(new SubmitExerciseAnswerCommand(exerciseId, userId, body.Answers), ct);
        return r.ToApiResultOrNotFound();
    }

    /// <summary>Teacher/admin: how every student in a class did on this lesson's exercises.</summary>
    [HttpGet("lessons/{lessonId:guid}/exercise-results/{classId:guid}")]
    [PermissionAuthorize(Permissions.Classes.Update)]
    public async Task<ActionResult<ApiResponse<LessonExerciseResultsDto>>> Results(
        Guid lessonId, Guid classId, CancellationToken ct)
    {
        var r = await sender.Send(new GetLessonExerciseResultsQuery(lessonId, classId), ct);
        return Ok(ApiResponse<LessonExerciseResultsDto>.Ok(r.Data, r.Message));
    }

    /// <summary>Teacher/admin: the writing exercises in a lesson + the class's submissions to grade
    /// (each with the student's text and any grade so far).</summary>
    [HttpGet("lessons/{lessonId:guid}/writing-submissions/{classId:guid}")]
    [PermissionAuthorize(Permissions.Classes.Update)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<WritingExerciseReviewDto>>>> WritingSubmissions(
        Guid lessonId, Guid classId, CancellationToken ct)
    {
        var r = await sender.Send(new GetWritingSubmissionsQuery(lessonId, classId), ct);
        return Ok(ApiResponse<IReadOnlyList<WritingExerciseReviewDto>>.Ok(r.Data, r.Message));
    }

    /// <summary>Teacher/admin: grade one (writing) submission — score out of max + optional feedback.
    /// Body: <c>{ "score", "maxScore", "feedback" }</c>. The grader is the authenticated caller.</summary>
    [HttpPost("exercise-submissions/{submissionId:guid}/grade")]
    [PermissionAuthorize(Permissions.Classes.Update)]
    public async Task<ActionResult<ApiResponse<WritingGradeDto>>> Grade(
        Guid submissionId, [FromBody] GradeSubmissionRequest body, CancellationToken ct)
    {
        if (currentUser.UserId is not { } graderId)
            return Unauthorized(ApiResponse<WritingGradeDto>.Fail("Not authenticated."));
        var r = await sender.Send(
            new GradeExerciseSubmissionCommand(submissionId, graderId, body.Score, body.MaxScore, body.Feedback), ct);
        return r.ToApiResultOrNotFound();
    }

    /// <summary>
    /// Upload a Listening audio file (teacher/admin). Returns the stored file name; the
    /// caller stores it as <c>content.audioUrl</c> = <c>/api/proxy/Exercises/audio/{fileName}</c>.
    /// </summary>
    [HttpPost("exercises/audio")]
    [PermissionAuthorize(Permissions.Classes.Update)]
    [RequestSizeLimit(30 * 1024 * 1024)]
    public async Task<ActionResult<ApiResponse<AudioUploadDto>>> UploadAudio(
        IFormFile file, [FromServices] IExerciseAudioStore store, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(ApiResponse<AudioUploadDto>.Fail("No file provided."));
        if (file.Length > 25 * 1024 * 1024)
            return BadRequest(ApiResponse<AudioUploadDto>.Fail("Audio must be 25 MB or smaller."));
        try
        {
            await using var s = file.OpenReadStream();
            var stored = await store.SaveAsync(s, file.FileName, ct);
            return Ok(ApiResponse<AudioUploadDto>.Ok(new AudioUploadDto(stored), "Uploaded."));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<AudioUploadDto>.Fail(ex.Message));
        }
    }

    /// <summary>Stream a previously uploaded exercise audio file (any authenticated user;
    /// range-enabled for seeking). Reached via the same-origin proxy.</summary>
    [HttpGet("exercises/audio/{fileName}")]
    public async Task<IActionResult> GetAudio(
        string fileName, [FromServices] IExerciseAudioStore store, CancellationToken ct)
    {
        var opened = await store.OpenAsync(fileName, ct);
        if (opened is null) return NotFound();
        return File(opened.Value.Stream, opened.Value.ContentType, enableRangeProcessing: true);
    }

    /// <summary>
    /// Upload an exercise image (teacher/admin) — e.g. a "label the picture" prompt
    /// (flags, jobs). Returns the stored file name; the caller stores it as an item's
    /// <c>imageUrl</c> = <c>/api/proxy/Exercises/image/{fileName}</c>.
    /// </summary>
    [HttpPost("exercises/image")]
    [PermissionAuthorize(Permissions.Classes.Update)]
    [RequestSizeLimit(8 * 1024 * 1024)]
    public async Task<ActionResult<ApiResponse<ImageUploadDto>>> UploadImage(
        IFormFile file, [FromServices] IExerciseImageStore store, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(ApiResponse<ImageUploadDto>.Fail("No file provided."));
        if (file.Length > 8 * 1024 * 1024)
            return BadRequest(ApiResponse<ImageUploadDto>.Fail("Image must be 8 MB or smaller."));
        try
        {
            await using var s = file.OpenReadStream();
            var stored = await store.SaveAsync(s, file.FileName, ct);
            return Ok(ApiResponse<ImageUploadDto>.Ok(new ImageUploadDto(stored), "Uploaded."));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<ImageUploadDto>.Fail(ex.Message));
        }
    }

    /// <summary>Stream a previously uploaded exercise image (any authenticated user).
    /// Reached via the same-origin proxy.</summary>
    [HttpGet("exercises/image/{fileName}")]
    public async Task<IActionResult> GetImage(
        string fileName, [FromServices] IExerciseImageStore store, CancellationToken ct)
    {
        var opened = await store.OpenAsync(fileName, ct);
        if (opened is null) return NotFound();
        return File(opened.Value.Stream, opened.Value.ContentType);
    }

    /// <summary>
    /// Upload an exercise document (teacher/admin) — a reading passage or writing worksheet
    /// as a PDF / Word file. Returns the stored file name; the caller stores it as
    /// <c>content.fileUrl</c> = <c>/api/proxy/Exercises/file/{fileName}</c>.
    /// </summary>
    [HttpPost("exercises/file")]
    [PermissionAuthorize(Permissions.Classes.Update)]
    [RequestSizeLimit(25 * 1024 * 1024)]
    public async Task<ActionResult<ApiResponse<FileUploadDto>>> UploadFile(
        IFormFile file, [FromServices] IExerciseFileStore store, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(ApiResponse<FileUploadDto>.Fail("No file provided."));
        if (file.Length > 20 * 1024 * 1024)
            return BadRequest(ApiResponse<FileUploadDto>.Fail("File must be 20 MB or smaller."));
        try
        {
            await using var s = file.OpenReadStream();
            var stored = await store.SaveAsync(s, file.FileName, ct);
            return Ok(ApiResponse<FileUploadDto>.Ok(new FileUploadDto(stored), "Uploaded."));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<FileUploadDto>.Fail(ex.Message));
        }
    }

    /// <summary>Stream a previously uploaded exercise document (any authenticated user).
    /// Reached via the same-origin proxy.</summary>
    [HttpGet("exercises/file/{fileName}")]
    public async Task<IActionResult> GetFile(
        string fileName, [FromServices] IExerciseFileStore store, CancellationToken ct)
    {
        var opened = await store.OpenAsync(fileName, ct);
        if (opened is null) return NotFound();
        return File(opened.Value.Stream, opened.Value.ContentType);
    }
}

/// <summary>Request body for the bulk add/update endpoint.</summary>
public sealed record BulkExercisesRequest(List<ExerciseInputDto> Exercises);

/// <summary>Request body for the submit endpoint — the user's answers (shape varies by type).</summary>
public sealed record SubmitExerciseRequest(JsonElement Answers);

/// <summary>Request body for grading a submission — score out of max + optional feedback.</summary>
public sealed record GradeSubmissionRequest(decimal Score, decimal MaxScore, string? Feedback);

/// <summary>Result of an audio upload — the opaque stored file name.</summary>
public sealed record AudioUploadDto(string FileName);

/// <summary>Result of an image upload — the opaque stored file name.</summary>
public sealed record ImageUploadDto(string FileName);

/// <summary>Result of a document (PDF / Word) upload — the opaque stored file name.</summary>
public sealed record FileUploadDto(string FileName);
