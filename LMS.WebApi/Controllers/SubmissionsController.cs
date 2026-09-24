using LMS.Application.Common.Abstractions;
using LMS.Application.Common.Security;
using LMS.Application.Features.Submissions;
using LMS.WebApi.Common;
using LMS.WebApi.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LMS.WebApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class SubmissionsController(ISender sender, ISubmissionFileStore fileStore) : ControllerBase
{
    private const long UploadSizeLimit = 25 * 1024 * 1024; // 25 MB per file

    [HttpGet("assignment/{assignmentId:guid}")]
    [PermissionAuthorize(Permissions.Submissions.Read)]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<SubmissionDto>>>> Assignment(Guid assignmentId,
        CancellationToken ct)
    {
        var r = await sender.Send(new GetAssignmentSubmissionsQuery(assignmentId), ct);
        return Ok(ApiResponse<IReadOnlyCollection<SubmissionDto>>.Ok(r.Data, r.Message));
    }

    [HttpGet("student/{studentProfileId:guid}")]
    [PermissionAuthorize(Permissions.Submissions.Read)]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<SubmissionDto>>>> Student(Guid studentProfileId,
        CancellationToken ct)
    {
        var r = await sender.Send(new GetStudentSubmissionsQuery(studentProfileId), ct);
        return Ok(ApiResponse<IReadOnlyCollection<SubmissionDto>>.Ok(r.Data, r.Message));
    }

    [HttpPost]
    [PermissionAuthorize(Permissions.Submissions.Create)]
    public async Task<ActionResult<ApiResponse<SubmissionDto>>> Submit([FromBody] SubmitAssignmentCommand cmd,
        CancellationToken ct)
    {
        var r = await sender.Send(cmd, ct);
        return r.ToApiResult();
    }

    /// <summary>
    /// Auto-saves the student's draft answer text for an assignment without
    /// finalising. Upserts the caller's submission and leaves it editable. The
    /// student profile is taken from the JWT, never the body. Body: {"content": "…"}.
    /// </summary>
    [HttpPost("assignment/{assignmentId:guid}/draft")]
    [PermissionAuthorize(Permissions.Submissions.Create)]
    public async Task<ActionResult<ApiResponse<SubmissionDto>>> SaveDraft(
        Guid assignmentId, [FromBody] SaveDraftRequest body, CancellationToken ct)
    {
        var r = await sender.Send(new SaveSubmissionDraftCommand(assignmentId, body.Content ?? string.Empty), ct);
        return r.ToApiResult();
    }

    [HttpPost("{id:guid}/grade/{score:decimal}")]
    [PermissionAuthorize(Permissions.Submissions.Grade)]
    public async Task<ActionResult<ApiResponse<SubmissionDto>>> Grade(Guid id, decimal score, CancellationToken ct)
    {
        var r = await sender.Send(new GradeSubmissionCommand(id, score), ct);
        return r.ToApiResult();
    }

    /// <summary>Richer grade: score out of an optional max, plus optional written feedback
    /// (e.g. for a writing task). Body: {"score":8,"maxScore":10,"feedback":"…"}.</summary>
    [HttpPost("{id:guid}/grade")]
    [PermissionAuthorize(Permissions.Submissions.Grade)]
    public async Task<ActionResult<ApiResponse<SubmissionDto>>> GradeWithFeedback(
        Guid id, [FromBody] GradeRequest body, CancellationToken ct)
    {
        var r = await sender.Send(new GradeSubmissionCommand(id, body.Score, body.MaxScore, body.Feedback), ct);
        return r.ToApiResult();
    }

    // ---- File submissions -------------------------------------------------

    /// <summary>
    /// Student uploads a file to their submission for an assignment (creating
    /// the submission on first upload). The blob is written + hashed here, then
    /// the command applies the deadline / lock / duplicate anti-cheat rules. If
    /// the command rejects the file, the orphaned blob is deleted.
    /// </summary>
    [HttpPost("assignment/{assignmentId:guid}/files")]
    [PermissionAuthorize(Permissions.Submissions.Create)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(UploadSizeLimit)]
    public async Task<ActionResult<ApiResponse<SubmissionFileDto>>> UploadFile(
        Guid assignmentId, IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(ApiResponse<SubmissionFileDto>.Fail("File is required."));
        if (file.Length > UploadSizeLimit)
            return BadRequest(ApiResponse<SubmissionFileDto>.Fail("File exceeds the 25 MB limit."));

        SavedSubmissionFile saved;
        try
        {
            await using var stream = file.OpenReadStream();
            saved = await fileStore.SaveAsync(stream, file.FileName, file.ContentType, ct);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<SubmissionFileDto>.Fail(ex.Message));
        }

        var r = await sender.Send(new AddSubmissionFileCommand(
            assignmentId, saved.StoredFileName, file.FileName, file.ContentType, saved.Size, saved.Sha256), ct);

        if (!r.Success)
        {
            await fileStore.DeleteAsync(saved.StoredFileName, ct); // clean up the orphan
            return BadRequest(ApiResponse<SubmissionFileDto>.Fail(r.Message ?? "Upload rejected"));
        }
        return Ok(ApiResponse<SubmissionFileDto>.Ok(r.Data, r.Message));
    }

    [HttpGet("{submissionId:guid}/files")]
    [PermissionAuthorize(Permissions.Submissions.Read)]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<SubmissionFileDto>>>> Files(
        Guid submissionId, CancellationToken ct)
    {
        var r = await sender.Send(new GetSubmissionFilesQuery(submissionId), ct);
        if (!r.Success)
            return StatusCode(StatusCodes.Status403Forbidden,
                ApiResponse<IReadOnlyCollection<SubmissionFileDto>>.Fail(r.Message ?? "Forbidden"));
        return Ok(ApiResponse<IReadOnlyCollection<SubmissionFileDto>>.Ok(r.Data, r.Message));
    }

    [HttpDelete("{submissionId:guid}/files/{fileId:guid}")]
    [PermissionAuthorize(Permissions.Submissions.Create)]
    public async Task<ActionResult<ApiResponse<object>>> DeleteFile(
        Guid submissionId, Guid fileId, CancellationToken ct)
    {
        var r = await sender.Send(new RemoveSubmissionFileCommand(submissionId, fileId), ct);
        if (!r.Success) return BadRequest(ApiResponse<object>.Fail(r.Message ?? "Failed"));
        if (!string.IsNullOrEmpty(r.Data)) await fileStore.DeleteAsync(r.Data, ct);
        return Ok(ApiResponse<object>.Ok(new { }, r.Message));
    }

    /// <summary>
    /// Streams a submission file. Self-only for students; staff can read any.
    /// The bucket is private, so this is the only path to the bytes.
    /// </summary>
    [HttpGet("files/{fileId:guid}/download")]
    [PermissionAuthorize(Permissions.Submissions.Read)]
    public async Task<IActionResult> DownloadFile(Guid fileId, CancellationToken ct)
    {
        var r = await sender.Send(new GetSubmissionFileForDownloadQuery(fileId), ct);
        if (!r.Success || r.Data is null) return Forbid();
        var stream = await fileStore.OpenAsync(r.Data.StoredFileName, ct);
        if (stream is null) return NotFound();
        return File(stream, r.Data.MimeType, r.Data.OriginalFileName);
    }

    /// <summary>Student finalises (locks) their own submission — no more file edits.</summary>
    [HttpPost("{submissionId:guid}/finalize")]
    [PermissionAuthorize(Permissions.Submissions.Create)]
    public async Task<ActionResult<ApiResponse<SubmissionDto>>> Finalize(Guid submissionId, CancellationToken ct)
    {
        var r = await sender.Send(new FinalizeSubmissionCommand(submissionId), ct);
        return r.ToApiResult();
    }

    /// <summary>Teacher locks / unlocks a submission. Body: {"locked": true|false}.</summary>
    [HttpPost("{submissionId:guid}/lock")]
    [PermissionAuthorize(Permissions.Submissions.Grade)]
    public async Task<ActionResult<ApiResponse<SubmissionDto>>> Lock(
        Guid submissionId, [FromBody] SetLockRequest body, CancellationToken ct)
    {
        var r = await sender.Send(new SetSubmissionLockCommand(submissionId, body.Locked), ct);
        return r.ToApiResult();
    }

    /// <summary>Teacher returns a submission for a redo — unlocks it so the student can resubmit.</summary>
    [HttpPost("{submissionId:guid}/return")]
    [PermissionAuthorize(Permissions.Submissions.Grade)]
    public async Task<ActionResult<ApiResponse<SubmissionDto>>> Return(
        Guid submissionId, [FromBody] ReturnRequest? body, CancellationToken ct)
    {
        var r = await sender.Send(new ReturnSubmissionCommand(submissionId, body?.Feedback), ct);
        return r.ToApiResult();
    }

    /// <summary>Submission audit trail — staff only (gated by the grade permission).</summary>
    [HttpGet("{submissionId:guid}/audit")]
    [PermissionAuthorize(Permissions.Submissions.Grade)]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<SubmissionAuditDto>>>> Audit(
        Guid submissionId, CancellationToken ct)
    {
        var r = await sender.Send(new GetSubmissionAuditQuery(submissionId), ct);
        return Ok(ApiResponse<IReadOnlyCollection<SubmissionAuditDto>>.Ok(r.Data, r.Message));
    }
}

public sealed record SetLockRequest(bool Locked);

public sealed record ReturnRequest(string? Feedback);

public sealed record GradeRequest(decimal Score, decimal? MaxScore, string? Feedback);

public sealed record SaveDraftRequest(string? Content);
