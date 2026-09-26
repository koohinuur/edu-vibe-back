using LMS.Application.Common.Models;
using LMS.Application.Common.Security;
using LMS.Application.Features.Exams;
using LMS.WebApi.Common;
using LMS.WebApi.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LMS.WebApi.Controllers;

/// <summary>
/// F8 — offline exams. Config + score entry require <see cref="Permissions.Exams.Manage"/>
/// (self-scoped in handlers to the class teacher or an admin). Reads use
/// <see cref="Permissions.Exams.Read"/>; the student-results endpoint self-scopes
/// so a student sees only their own results.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class ExamsController(ISender sender) : ControllerBase
{
    [HttpPost]
    [PermissionAuthorize(Permissions.Exams.Manage)]
    public async Task<ActionResult<ApiResponse<ExamDto>>> Create(
        [FromBody] CreateExamCommand cmd, CancellationToken ct)
        => Respond(await sender.Send(cmd, ct));

    [HttpPut("{id:guid}")]
    [PermissionAuthorize(Permissions.Exams.Manage)]
    public async Task<ActionResult<ApiResponse<ExamDto>>> Update(
        Guid id, [FromBody] UpdateExamCommand cmd, CancellationToken ct)
        => Respond(await sender.Send(cmd with { ExamId = id }, ct));

    [HttpDelete("{id:guid}")]
    [PermissionAuthorize(Permissions.Exams.Manage)]
    public async Task<ActionResult<ApiResponse<object>>> Delete(Guid id, CancellationToken ct)
        => Respond(await sender.Send(new DeleteExamCommand(id), ct));

    [HttpGet("{id:guid}")]
    [PermissionAuthorize(Permissions.Exams.Read)]
    public async Task<ActionResult<ApiResponse<ExamDto>>> Get(Guid id, CancellationToken ct)
        => Respond(await sender.Send(new GetExamByIdQuery(id), ct));

    [HttpGet("class/{classId:guid}")]
    [PermissionAuthorize(Permissions.Exams.Read)]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<ExamDto>>>> GetByClass(
        Guid classId, CancellationToken ct)
        => Respond(await sender.Send(new GetClassExamsQuery(classId), ct));

    /// <summary>The score-entry grid: enrolled students + each one's current result.</summary>
    [HttpGet("{id:guid}/roster")]
    [PermissionAuthorize(Permissions.Exams.Manage)]
    public async Task<ActionResult<ApiResponse<ExamRosterDto>>> Roster(Guid id, CancellationToken ct)
        => Respond(await sender.Send(new GetExamRosterQuery(id), ct));

    /// <summary>Enter/correct one student's per-section scores (idempotent upsert).</summary>
    [HttpPost("{id:guid}/results")]
    [PermissionAuthorize(Permissions.Exams.Manage)]
    public async Task<ActionResult<ApiResponse<ExamResultDto>>> EnterResult(
        Guid id, [FromBody] EnterExamResultCommand cmd, CancellationToken ct)
        => Respond(await sender.Send(cmd with { ExamId = id }, ct));

    [HttpDelete("{id:guid}/results/{studentProfileId:guid}")]
    [PermissionAuthorize(Permissions.Exams.Manage)]
    public async Task<ActionResult<ApiResponse<object>>> DeleteResult(
        Guid id, Guid studentProfileId, CancellationToken ct)
        => Respond(await sender.Send(new DeleteExamResultCommand(id, studentProfileId), ct));

    public sealed record PublishBody(bool Publish);

    /// <summary>Publish (or hide) one student's result — the spec #12 visibility gate.</summary>
    [HttpPost("{id:guid}/results/{studentProfileId:guid}/publish")]
    [PermissionAuthorize(Permissions.Exams.Manage)]
    public async Task<ActionResult<ApiResponse<ExamResultDto>>> PublishResult(
        Guid id, Guid studentProfileId, [FromBody] PublishBody body, CancellationToken ct)
        => Respond(await sender.Send(new PublishExamResultCommand(id, studentProfileId, body.Publish), ct));

    /// <summary>A student's exam results for the profile view (self-scoped in the handler).</summary>
    [HttpGet("student/{studentProfileId:guid}/results")]
    [PermissionAuthorize(Permissions.Exams.Read)]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<StudentExamResultDto>>>> StudentResults(
        Guid studentProfileId, CancellationToken ct)
        => Respond(await sender.Send(new GetStudentExamResultsQuery(studentProfileId), ct));

    // ---- response mapping --------------------------------------------------

    private static int StatusFor(string? errorCode) => errorCode switch
    {
        "NOT_FOUND" => StatusCodes.Status404NotFound,
        "FORBIDDEN" => StatusCodes.Status403Forbidden,
        "CONFLICT" => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status400BadRequest,
    };

    private ActionResult<ApiResponse<T>> Respond<T>(Result<T> r)
        => r.Success
            ? Ok(ApiResponse<T>.Ok(r.Data!, r.Message))
            : StatusCode(StatusFor(r.ErrorCode), ApiResponse<T>.Fail(r.Message ?? "Failed"));

    private ActionResult<ApiResponse<object>> Respond(Result r)
        => r.Success
            ? Ok(ApiResponse<object>.Ok(new { }, r.Message))
            : StatusCode(StatusFor(r.ErrorCode), ApiResponse<object>.Fail(r.Message ?? "Failed"));
}
