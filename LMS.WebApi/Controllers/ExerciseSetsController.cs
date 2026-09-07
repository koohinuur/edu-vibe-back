using LMS.Application.Common.Abstractions;
using LMS.Application.Common.Security;
using LMS.Application.Features.Exercises;
using LMS.Application.Features.ExerciseSets;
using LMS.WebApi.Common;
using LMS.WebApi.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LMS.WebApi.Controllers;

/// <summary>
/// Reusable exercise sets — teacher/admin-authored collections of practice exercises
/// attached to classes. Reading is gated by <c>Practice.Read</c> and authoring/management
/// by <c>Tasks.Manage</c> (both held by teachers), and owner-scoped in the handler.
/// Exercises are authored with the SAME bulk endpoint shape
/// as lesson exercises, and students SUBMIT via the existing
/// <c>POST /api/exercises/{exerciseId}/submit</c> — no set-specific submit endpoint, so the
/// self-check / XP / grading engine is reused unchanged.
/// </summary>
[ApiController]
[Route("api")]
[Authorize]
public sealed class ExerciseSetsController(ISender sender, ICurrentUserService currentUser) : ControllerBase
{
    [HttpPost("exercise-sets")]
    [PermissionAuthorize(Permissions.Tasks.Manage)]
    public async Task<ActionResult<ApiResponse<ExerciseSetDto>>> Create(
        [FromBody] ExerciseSetRequest body, CancellationToken ct)
    {
        if (currentUser.UserId is not { } uid)
            return Unauthorized(ApiResponse<ExerciseSetDto>.Fail("Not authenticated."));
        var r = await sender.Send(new CreateExerciseSetCommand(body.Title, body.Description, uid), ct);
        return r.ToApiResult();
    }

    /// <summary>Sets the caller manages — their own, or all for an admin.</summary>
    [HttpGet("exercise-sets")]
    [PermissionAuthorize(Permissions.Practice.Read)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<ExerciseSetDto>>>> List(CancellationToken ct)
    {
        var r = await sender.Send(new GetExerciseSetsQuery(), ct);
        return Ok(ApiResponse<IReadOnlyList<ExerciseSetDto>>.Ok(r.Data, r.Message));
    }

    [HttpGet("exercise-sets/{setId:guid}")]
    [PermissionAuthorize(Permissions.Practice.Read)]
    public async Task<ActionResult<ApiResponse<ExerciseSetDto>>> Get(Guid setId, CancellationToken ct)
    {
        var r = await sender.Send(new GetExerciseSetByIdQuery(setId), ct);
        return r.ToApiResultOrNotFound();
    }

    [HttpPut("exercise-sets/{setId:guid}")]
    [PermissionAuthorize(Permissions.Tasks.Manage)]
    public async Task<ActionResult<ApiResponse<ExerciseSetDto>>> Update(
        Guid setId, [FromBody] ExerciseSetRequest body, CancellationToken ct)
    {
        var r = await sender.Send(new UpdateExerciseSetCommand(setId, body.Title, body.Description), ct);
        return r.ToApiResultOrNotFound();
    }

    [HttpDelete("exercise-sets/{setId:guid}")]
    [PermissionAuthorize(Permissions.Tasks.Manage)]
    public async Task<ActionResult<ApiResponse<object>>> Delete(Guid setId, CancellationToken ct)
    {
        var r = await sender.Send(new DeleteExerciseSetCommand(setId), ct);
        return r.Success
            ? Ok(ApiResponse<object>.Ok(new { }, r.Message))
            : (r.ErrorCode == "NOT_FOUND"
                ? NotFound(ApiResponse<object>.Fail(r.Message ?? "Not found"))
                : BadRequest(ApiResponse<object>.Fail(r.Message ?? "Failed")));
    }

    /// <summary>Replace the set's attached classes wholesale. Body: <c>{ "classIds": [ … ] }</c>.</summary>
    [HttpPut("exercise-sets/{setId:guid}/classes")]
    [PermissionAuthorize(Permissions.Tasks.Manage)]
    public async Task<ActionResult<ApiResponse<object>>> SetClasses(
        Guid setId, [FromBody] SetClassesRequest body, CancellationToken ct)
    {
        var r = await sender.Send(new SetExerciseSetClassesCommand(setId, body.ClassIds ?? []), ct);
        return r.Success
            ? Ok(ApiResponse<object>.Ok(new { }, r.Message))
            : (r.ErrorCode == "NOT_FOUND"
                ? NotFound(ApiResponse<object>.Fail(r.Message ?? "Not found"))
                : BadRequest(ApiResponse<object>.Fail(r.Message ?? "Failed")));
    }

    /// <summary>Bulk add/update the set's exercises (upsert by orderIndex) — same body shape as
    /// the lesson bulk endpoint.</summary>
    [HttpPost("exercise-sets/{setId:guid}/exercises/bulk")]
    [PermissionAuthorize(Permissions.Tasks.Manage)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<Guid>>>> AddBulk(
        Guid setId, [FromBody] BulkExercisesRequest body, CancellationToken ct)
    {
        var r = await sender.Send(new AddExercisesToSetCommand(setId, body.Exercises ?? []), ct);
        if (r.Success) return Ok(ApiResponse<IReadOnlyList<Guid>>.Ok(r.Data, r.Message));
        return r.ErrorCode == "NOT_FOUND"
            ? NotFound(ApiResponse<IReadOnlyList<Guid>>.Fail(r.Message ?? "Not found"))
            : BadRequest(ApiResponse<IReadOnlyList<Guid>>.Fail(r.Message ?? "Failed"));
    }

    /// <summary>A set's exercises + the current user's saved results. Used by the teacher preview
    /// AND the student runner; access-checked in the handler (owner / admin / teaches or is
    /// enrolled in an attached class).</summary>
    [HttpGet("exercise-sets/{setId:guid}/exercises")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<ExerciseWithResultDto>>>> GetExercises(
        Guid setId, CancellationToken ct)
    {
        var r = await sender.Send(new GetSetExercisesQuery(setId), ct);
        return r.ToApiResultOrNotFound();
    }

    /// <summary>The current student's reachable sets (via their enrolled classes) + progress.</summary>
    [HttpGet("student/exercise-sets")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<StudentExerciseSetDto>>>> StudentSets(
        CancellationToken ct)
    {
        var r = await sender.Send(new GetStudentExerciseSetsQuery(), ct);
        return Ok(ApiResponse<IReadOnlyList<StudentExerciseSetDto>>.Ok(r.Data, r.Message));
    }
}

/// <summary>Create/update body for an exercise set.</summary>
public sealed record ExerciseSetRequest(string Title, string? Description);

/// <summary>Body for replacing a set's attached classes.</summary>
public sealed record SetClassesRequest(List<Guid> ClassIds);
