using LMS.Application.Common.Abstractions;
using LMS.Application.Common.Security;
using LMS.Application.Features.Assignments;
using LMS.Domain.Enums;
using LMS.WebApi.Common;
using LMS.WebApi.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LMS.WebApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class AssignmentsController(ISender sender, IMaterialFileStore store) : ControllerBase
{
    private const long UploadSizeLimit = 25L * 1024 * 1024;

    /// <summary>Lists assignments, optionally filtered by teacher, class, or status.</summary>
    [HttpGet]
    [PermissionAuthorize(Permissions.Assignments.Read)]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<AssignmentDto>>>> GetAll(
        [FromQuery] Guid? teacherUserId,
        [FromQuery] Guid? classId,
        [FromQuery] AssignmentStatus? status,
        CancellationToken ct)
    {
        var r = await sender.Send(new GetAssignmentsQuery(teacherUserId, classId, status), ct);
        return Ok(ApiResponse<IReadOnlyCollection<AssignmentDto>>.Ok(r.Data, r.Message));
    }

    [HttpGet("class/{classId:guid}")]
    [PermissionAuthorize(Permissions.Assignments.Read)]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<AssignmentDto>>>> Class(Guid classId,
        CancellationToken ct)
    {
        var r = await sender.Send(new GetClassAssignmentsQuery(classId), ct);
        return Ok(ApiResponse<IReadOnlyCollection<AssignmentDto>>.Ok(r.Data, r.Message));
    }

    [HttpGet("student/{studentProfileId:guid}")]
    [PermissionAuthorize(Permissions.Assignments.Read)]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<AssignmentDto>>>> Student(Guid studentProfileId,
        CancellationToken ct)
    {
        var r = await sender.Send(new GetStudentAssignmentsQuery(studentProfileId), ct);
        if (!r.Success)
            return StatusCode(StatusCodes.Status403Forbidden,
                ApiResponse<IReadOnlyCollection<AssignmentDto>>.Fail(r.Message ?? "Forbidden"));
        return Ok(ApiResponse<IReadOnlyCollection<AssignmentDto>>.Ok(r.Data, r.Message));
    }

    [HttpPost]
    [PermissionAuthorize(Permissions.Assignments.Create)]
    public async Task<ActionResult<ApiResponse<AssignmentDto>>> Create([FromBody] CreateAssignmentCommand cmd,
        CancellationToken ct)
    {
        var r = await sender.Send(cmd, ct);
        return r.ToApiResult();
    }

    [HttpPut("{id:guid}")]
    [PermissionAuthorize(Permissions.Assignments.Update)]
    public async Task<ActionResult<ApiResponse<AssignmentDto>>> Update(Guid id, [FromBody] UpdateAssignmentCommand cmd,
        CancellationToken ct)
    {
        var r = await sender.Send(cmd with { AssignmentId = id }, ct);
        return r.ToApiResult();
    }

    [HttpPost("{id:guid}/publish")]
    [PermissionAuthorize(Permissions.Assignments.Publish)]
    public async Task<ActionResult<ApiResponse<AssignmentDto>>> Publish(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new PublishAssignmentCommand(id), ct);
        return r.ToApiResult();
    }

    [HttpPost("{id:guid}/close")]
    [PermissionAuthorize(Permissions.Assignments.Close)]
    public async Task<ActionResult<ApiResponse<AssignmentDto>>> Close(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new CloseAssignmentCommand(id), ct);
        return r.ToApiResult();
    }

    /// <summary>Reopen a closed assignment back to Published.</summary>
    [HttpPost("{id:guid}/reopen")]
    [PermissionAuthorize(Permissions.Assignments.Publish)]
    public async Task<ActionResult<ApiResponse<AssignmentDto>>> Reopen(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new ReopenAssignmentCommand(id), ct);
        return r.ToApiResult();
    }

    /// <summary>Permanently delete an assignment and everything under it.</summary>
    [HttpDelete("{id:guid}")]
    [PermissionAuthorize(Permissions.Assignments.Update)]
    public async Task<ActionResult<ApiResponse<object>>> Delete(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new DeleteAssignmentCommand(id), ct);
        return r.Success
            ? Ok(ApiResponse<object>.Ok(new { }, r.Message))
            : r.ErrorCode == "NOT_FOUND"
                ? NotFound(ApiResponse<object>.Fail(r.Message ?? "Not found"))
                : BadRequest(ApiResponse<object>.Fail(r.Message ?? "Failed"));
    }

    // ----- Book attachments ------------------------------------------------

    /// <summary>Books attached to this assignment (reference material).</summary>
    [HttpGet("{id:guid}/books")]
    [PermissionAuthorize(Permissions.Assignments.Read)]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<AssignmentBookDto>>>> GetBooks(Guid id,
        CancellationToken ct)
    {
        var r = await sender.Send(new GetAssignmentBooksQuery(id), ct);
        return Ok(ApiResponse<IReadOnlyCollection<AssignmentBookDto>>.Ok(r.Data, r.Message));
    }

    /// <summary>Attach a book to this assignment. If already attached, updates the note.</summary>
    [HttpPost("{id:guid}/books")]
    [PermissionAuthorize(Permissions.Assignments.Update)]
    public async Task<ActionResult<ApiResponse<AssignmentBookDto>>> AttachBook(Guid id,
        [FromBody] AttachBookToAssignmentCommand cmd, CancellationToken ct)
    {
        var r = await sender.Send(cmd with { AssignmentId = id }, ct);
        return r.ToApiResult();
    }

    /// <summary>Detach a book from this assignment.</summary>
    [HttpDelete("{id:guid}/books/{bookId:guid}")]
    [PermissionAuthorize(Permissions.Assignments.Update)]
    public async Task<ActionResult<ApiResponse<object>>> DetachBook(Guid id, Guid bookId, CancellationToken ct)
    {
        var r = await sender.Send(new DetachBookFromAssignmentCommand(id, bookId), ct);
        return r.Success
            ? Ok(ApiResponse<object>.Ok(null, r.Message))
            : BadRequest(ApiResponse<object>.Fail(r.Message ?? "Failed"));
    }

    // ----- Worksheet files -------------------------------------------------

    /// <summary>Worksheet files attached to this assignment (teacher-provided, for students to download).</summary>
    [HttpGet("{id:guid}/files")]
    [PermissionAuthorize(Permissions.Assignments.Read)]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<AssignmentFileDto>>>> GetFiles(Guid id,
        CancellationToken ct)
    {
        var r = await sender.Send(new GetAssignmentFilesQuery(id), ct);
        if (!r.Success)
            return StatusCode(403, ApiResponse<IReadOnlyCollection<AssignmentFileDto>>.Fail(r.Message ?? "Forbidden"));
        return Ok(ApiResponse<IReadOnlyCollection<AssignmentFileDto>>.Ok(r.Data, r.Message));
    }

    /// <summary>Teacher attaches a worksheet file to this assignment.</summary>
    [HttpPost("{id:guid}/files")]
    [PermissionAuthorize(Permissions.Assignments.Update)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(UploadSizeLimit)]
    public async Task<ActionResult<ApiResponse<AssignmentFileDto>>> UploadFile(
        Guid id, IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(ApiResponse<AssignmentFileDto>.Fail("File is required."));
        if (file.Length > UploadSizeLimit)
            return BadRequest(ApiResponse<AssignmentFileDto>.Fail("File exceeds the 25 MB limit."));

        string storedName;
        try
        {
            await using var stream = file.OpenReadStream();
            storedName = await store.SaveAsync(stream, file.FileName, file.ContentType, ct);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<AssignmentFileDto>.Fail(ex.Message));
        }

        var r = await sender.Send(new AddAssignmentFileCommand(
            id, storedName, file.FileName, file.ContentType, file.Length), ct);

        if (!r.Success)
        {
            await store.DeleteAsync(storedName, ct); // clean up the orphan blob
            return BadRequest(ApiResponse<AssignmentFileDto>.Fail(r.Message ?? "Upload rejected"));
        }
        return Ok(ApiResponse<AssignmentFileDto>.Ok(r.Data, r.Message));
    }

    /// <summary>Detach + delete a worksheet file.</summary>
    [HttpDelete("{id:guid}/files/{fileId:guid}")]
    [PermissionAuthorize(Permissions.Assignments.Update)]
    public async Task<ActionResult<ApiResponse<object>>> DeleteFile(Guid id, Guid fileId, CancellationToken ct)
    {
        var r = await sender.Send(new RemoveAssignmentFileCommand(id, fileId), ct);
        if (!r.Success) return BadRequest(ApiResponse<object>.Fail(r.Message ?? "Failed"));
        if (!string.IsNullOrEmpty(r.Data)) await store.DeleteAsync(r.Data, ct);
        return Ok(ApiResponse<object>.Ok(new { }, r.Message));
    }

    /// <summary>Streams a worksheet file — staff (any) or a student enrolled in the assignment's class.</summary>
    [HttpGet("files/{fileId:guid}/download")]
    [PermissionAuthorize(Permissions.Assignments.Read)]
    public async Task<IActionResult> DownloadFile(Guid fileId, CancellationToken ct)
    {
        var r = await sender.Send(new GetAssignmentFileForDownloadQuery(fileId), ct);
        if (!r.Success || r.Data is null) return NotFound();
        var stream = await store.OpenAsync(r.Data.StoredFileName, ct);
        if (stream is null) return NotFound();
        Response.Headers.ContentDisposition = $"inline; filename=\"{r.Data.OriginalFileName.Replace("\"", "")}\"";
        return File(stream, r.Data.MimeType, enableRangeProcessing: true);
    }

    // ----- Per-student targeting ------------------------------------------

    /// <summary>Targeted assignees. Empty list = assignment is visible to the whole class.</summary>
    [HttpGet("{id:guid}/assignees")]
    [PermissionAuthorize(Permissions.Assignments.Read)]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<AssignmentAssigneeDto>>>> GetAssignees(Guid id,
        CancellationToken ct)
    {
        var r = await sender.Send(new GetAssignmentAssigneesQuery(id), ct);
        return Ok(ApiResponse<IReadOnlyCollection<AssignmentAssigneeDto>>.Ok(r.Data, r.Message));
    }

    /// <summary>Replace the assignee set. Empty body = whole class (default). Body = subset of students.</summary>
    [HttpPut("{id:guid}/assignees")]
    [PermissionAuthorize(Permissions.Assignments.Update)]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<AssignmentAssigneeDto>>>> SetAssignees(Guid id,
        [FromBody] IReadOnlyCollection<Guid> studentProfileIds, CancellationToken ct)
    {
        var r = await sender.Send(new SetAssignmentAssigneesCommand(id, studentProfileIds ?? Array.Empty<Guid>()), ct);
        return r.ToApiResult();
    }
}
