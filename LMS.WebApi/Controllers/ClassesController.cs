using LMS.Application.Common.Abstractions;
using LMS.Application.Common.Models;
using LMS.Application.Common.Security;
using LMS.Application.Features.Analytics;
using LMS.Application.Features.Classes;
using LMS.Application.Features.ClassResources;
using LMS.Application.Features.Sessions;
using LMS.Application.Features.Students;
using LMS.WebApi.Common;
using LMS.WebApi.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LMS.WebApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class ClassesController(ISender sender) : ControllerBase
{
    [HttpGet]
    [PermissionAuthorize(Permissions.Classes.Read)]
    public async Task<ActionResult<ApiResponse<PagedResult<ClassDto>>>> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        [FromQuery] string? search = null,
        CancellationToken ct = default)
    {
        var r = await sender.Send(new GetClassesQuery(page, pageSize, search), ct);
        return Ok(ApiResponse<PagedResult<ClassDto>>.Ok(r.Data, r.Message));
    }

    [HttpGet("{id:guid}")]
    [PermissionAuthorize(Permissions.Classes.Read)]
    public async Task<ActionResult<ApiResponse<ClassDto>>> Get(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new GetClassByIdQuery(id), ct);
        return r.ToApiResultOrNotFound();
    }

    [HttpGet("assigned/{teacherUserId:guid}")]
    [PermissionAuthorize(Permissions.Classes.Read)]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<ClassDto>>>> Assigned(Guid teacherUserId,
        CancellationToken ct)
    {
        var r = await sender.Send(new GetAssignedClassesQuery(teacherUserId), ct);
        return Ok(ApiResponse<IReadOnlyCollection<ClassDto>>.Ok(r.Data, r.Message));
    }

    /// <summary>
    /// The caller's enrolled classes (student self-view) — teacher name and
    /// next session joined in. Self-scoped from the JWT, so no Classes.Read
    /// is required; students can open their own classes without the admin
    /// read permission.
    /// </summary>
    [HttpGet("mine")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<MyClassDto>>>> Mine(CancellationToken ct)
    {
        var r = await sender.Send(new GetMyClassesQuery(), ct);
        return Ok(ApiResponse<IReadOnlyCollection<MyClassDto>>.Ok(r.Data, r.Message));
    }

    [HttpGet("{id:guid}/students")]
    [PermissionAuthorize(Permissions.Classes.Read)]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<Guid>>>> Students(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new GetClassStudentsQuery(id), ct);
        return Ok(ApiResponse<IReadOnlyCollection<Guid>>.Ok(r.Data, r.Message));
    }

    /// <summary>
    /// Class engagement + outcomes: attendance rate, average grade, assignment +
    /// lesson completion rates, and at-risk students. Self-scoped in the handler
    /// to the class's own teacher or staff — no cross-class access.
    /// </summary>
    [HttpGet("{id:guid}/analytics")]
    public async Task<ActionResult<ApiResponse<ClassAnalyticsDto>>> Analytics(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new GetClassAnalyticsQuery(id), ct);
        return r.Success
            ? Ok(ApiResponse<ClassAnalyticsDto>.Ok(r.Data, r.Message))
            : r.ErrorCode == "FORBIDDEN"
                ? StatusCode(StatusCodes.Status403Forbidden, ApiResponse<ClassAnalyticsDto>.Fail(r.Message ?? "Forbidden"))
                : NotFound(ApiResponse<ClassAnalyticsDto>.Fail(r.Message ?? "Not found"));
    }

    // ===== Class-level content hub ========================================
    // The shared "course setup" — roadmap, video lessons, links, default
    // homework — that an admin (or the class teacher) attaches to a whole
    // class. Self-scoped in the handlers: read = staff / class teacher /
    // enrolled student; manage = staff / class teacher. Plain [Authorize]
    // here (no Classes.* permission) so students can read their own class's
    // content without the admin read permission.

    /// <summary>Every content item attached to the class, ordered for the hub.</summary>
    [HttpGet("{id:guid}/resources")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<ClassResourceDto>>>> GetResources(
        Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new GetClassResourcesQuery(id), ct);
        return r.Success
            ? Ok(ApiResponse<IReadOnlyCollection<ClassResourceDto>>.Ok(r.Data, r.Message))
            : r.ErrorCode == "FORBIDDEN"
                ? StatusCode(StatusCodes.Status403Forbidden,
                    ApiResponse<IReadOnlyCollection<ClassResourceDto>>.Fail(r.Message ?? "Forbidden"))
                : NotFound(ApiResponse<IReadOnlyCollection<ClassResourceDto>>.Fail(r.Message ?? "Not found"));
    }

    [HttpPost("{id:guid}/resources")]
    public async Task<ActionResult<ApiResponse<ClassResourceDto>>> CreateResource(
        Guid id, [FromBody] CreateClassResourceCommand cmd, CancellationToken ct)
    {
        var r = await sender.Send(cmd with { ClassId = id }, ct);
        return MapWrite(r);
    }

    [HttpPut("{id:guid}/resources/{resourceId:guid}")]
    public async Task<ActionResult<ApiResponse<ClassResourceDto>>> UpdateResource(
        Guid id, Guid resourceId, [FromBody] UpdateClassResourceCommand cmd, CancellationToken ct)
    {
        var r = await sender.Send(cmd with { ClassId = id, ResourceId = resourceId }, ct);
        return MapWrite(r);
    }

    [HttpDelete("{id:guid}/resources/{resourceId:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteResource(
        Guid id, Guid resourceId, CancellationToken ct)
    {
        var r = await sender.Send(new DeleteClassResourceCommand(id, resourceId), ct);
        return r.Success
            ? Ok(ApiResponse<object>.Ok(new { }, r.Message))
            : r.ErrorCode == "FORBIDDEN"
                ? StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.Fail(r.Message ?? "Forbidden"))
                : r.ErrorCode == "NOT_FOUND"
                    ? NotFound(ApiResponse<object>.Fail(r.Message ?? "Not found"))
                    : BadRequest(ApiResponse<object>.Fail(r.Message ?? "Failed"));
    }

    private ActionResult<ApiResponse<ClassResourceDto>> MapWrite(Result<ClassResourceDto> r) =>
        r.Success
            ? Ok(ApiResponse<ClassResourceDto>.Ok(r.Data, r.Message))
            : r.ErrorCode == "FORBIDDEN"
                ? StatusCode(StatusCodes.Status403Forbidden, ApiResponse<ClassResourceDto>.Fail(r.Message ?? "Forbidden"))
                : r.ErrorCode == "NOT_FOUND"
                    ? NotFound(ApiResponse<ClassResourceDto>.Fail(r.Message ?? "Not found"))
                    : BadRequest(ApiResponse<ClassResourceDto>.Fail(r.Message ?? "Failed"));

    /// <summary>The class's recurring schedule pattern; 404 until one is set.</summary>
    [HttpGet("{id:guid}/schedule-pattern")]
    [PermissionAuthorize(Permissions.Sessions.Read)]
    public async Task<ActionResult<ApiResponse<SchedulePatternDto>>> GetSchedulePattern(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new GetClassSchedulePatternQuery(id), ct);
        return r.ToApiResultOrNotFound();
    }

    /// <summary>
    /// Upserts the recurring pattern and (re)generates the class's lesson
    /// sessions from it. Past lessons and lessons that already have
    /// attendance marks are never touched — only future, unmarked lessons
    /// are replaced. This is the bulk alternative to creating sessions one
    /// by one via POST /api/ClassSessions.
    /// </summary>
    [HttpPut("{id:guid}/schedule-pattern")]
    [PermissionAuthorize(Permissions.Sessions.Create)]
    public async Task<ActionResult<ApiResponse<ApplyScheduleResultDto>>> ApplySchedulePattern(
        Guid id, [FromBody] ApplyClassScheduleCommand cmd, CancellationToken ct)
    {
        var r = await sender.Send(cmd with { ClassId = id }, ct);
        return r.ToApiResult();
    }

    [HttpPost]
    [PermissionAuthorize(Permissions.Classes.Create)]
    public async Task<ActionResult<ApiResponse<ClassDto>>> Create([FromBody] CreateClassCommand cmd,
        CancellationToken ct)
    {
        var r = await sender.Send(cmd, ct);
        return r.ToApiResult();
    }

    [HttpPut("{id:guid}")]
    [PermissionAuthorize(Permissions.Classes.Update)]
    public async Task<ActionResult<ApiResponse<ClassDto>>> Update(Guid id, [FromBody] UpdateClassCommand cmd,
        CancellationToken ct)
    {
        var r = await sender.Send(cmd with { ClassId = id }, ct);
        return r.ToApiResult();
    }

    [HttpDelete("{id:guid}")]
    [PermissionAuthorize(Permissions.Classes.Delete)]
    public async Task<ActionResult<ApiResponse<object>>> Cancel(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new CancelClassCommand(id), ct);
        return r.Success
            ? Ok(ApiResponse<object>.Ok(new { }, r.Message))
            : BadRequest(ApiResponse<object>.Fail(r.Message ?? "Failed"));
    }

    /// <summary>Reactivate an archived (cancelled) class — flips it back to the normal state.</summary>
    [HttpPost("{id:guid}/reactivate")]
    [PermissionAuthorize(Permissions.Classes.Update)]
    public async Task<ActionResult<ApiResponse<object>>> Reactivate(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new ReactivateClassCommand(id), ct);
        return r.Success
            ? Ok(ApiResponse<object>.Ok(new { }, r.Message))
            : BadRequest(ApiResponse<object>.Fail(r.Message ?? "Failed"));
    }

    [HttpPost("{id:guid}/enroll/{studentProfileId:guid}")]
    [PermissionAuthorize(Permissions.Classes.Enroll)]
    public async Task<ActionResult<ApiResponse<object>>> Enroll(Guid id, Guid studentProfileId, CancellationToken ct)
    {
        var r = await sender.Send(new EnrollStudentCommand(id, studentProfileId), ct);
        return r.Success
            ? Ok(ApiResponse<object>.Ok(new { }, r.Message))
            : BadRequest(ApiResponse<object>.Fail(r.Message ?? "Failed"));
    }

    [HttpDelete("{id:guid}/enroll/{studentProfileId:guid}")]
    [PermissionAuthorize(Permissions.Classes.Enroll)]
    public async Task<ActionResult<ApiResponse<object>>> Drop(Guid id, Guid studentProfileId, CancellationToken ct)
    {
        var r = await sender.Send(new RemoveStudentFromClassCommand(id, studentProfileId), ct);
        return r.Success
            ? Ok(ApiResponse<object>.Ok(new { }, r.Message))
            : BadRequest(ApiResponse<object>.Fail(r.Message ?? "Failed"));
    }

    /// <summary>
    /// Bulk-enrols students into the class from an uploaded .xlsx whose first
    /// column holds emails (a "Email" header row is auto-skipped). Existing users
    /// are reused; unknown emails get a new student account with a generated
    /// password. Responds with a downloadable result workbook (Summary + Results
    /// sheets: Email / Password / Status / Failure Reason) and mirrors the four
    /// headline counts in X-Import-* response headers. On success the body is the
    /// spreadsheet; a bad request/not-found returns the usual JSON envelope.
    /// </summary>
    [HttpPost("{id:guid}/students/import")]
    [PermissionAuthorize(Permissions.Classes.Enroll)]
    public async Task<IActionResult> ImportStudents(
        Guid id,
        IFormFile file,
        [FromServices] IExcelService excel,
        CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(ApiResponse<object>.Fail("No file was uploaded."));

        IReadOnlyList<ExcelImportRow> parsed;
        try
        {
            await using var stream = file.OpenReadStream();
            parsed = excel.ReadNameEmailRows(stream);
        }
        catch
        {
            return BadRequest(ApiResponse<object>.Fail("Could not read the file. Upload a valid .xlsx workbook."));
        }

        // A leading header row ("FIO | Email", "Name | E-mail", …) has no '@' in
        // its email cell — drop it so the header isn't treated as an address.
        if (parsed.Count > 0 && !parsed[0].Email.Contains('@'))
            parsed = parsed.Skip(1).ToList();

        var inputs = parsed
            .Select(row => new BulkImportStudentInput(row.Email, row.FullName))
            .ToList();

        var r = await sender.Send(new BulkImportStudentsCommand(id, inputs), ct);
        if (!r.Success || r.Data is null)
            return r.ErrorCode == "NOT_FOUND"
                ? NotFound(ApiResponse<object>.Fail(r.Message ?? "Not found"))
                : BadRequest(ApiResponse<object>.Fail(r.Message ?? "Failed"));

        var result = r.Data;

        var summary = new ExcelSheet(
            "Summary",
            new[] { "Metric", "Count" },
            new List<IReadOnlyList<string?>>
            {
                new[] { "Total Rows", result.TotalRows.ToString() },
                new[] { "Created Users", result.CreatedUsers.ToString() },
                new[] { "Existing Users Added", result.ExistingUsersAdded.ToString() },
                new[] { "Failed Rows", result.FailedRows.ToString() },
            });

        var results = new ExcelSheet(
            "Results",
            new[] { "Name", "Email", "Generated Password", "Status", "Failure Reason" },
            result.Rows
                .Select(row => (IReadOnlyList<string?>)new[] { row.Name, row.Email, row.Password, row.Status, row.Reason })
                .ToList());

        var bytes = excel.Build(new[] { summary, results });

        Response.Headers["X-Import-Total"] = result.TotalRows.ToString();
        Response.Headers["X-Import-Created"] = result.CreatedUsers.ToString();
        Response.Headers["X-Import-Existing"] = result.ExistingUsersAdded.ToString();
        Response.Headers["X-Import-Failed"] = result.FailedRows.ToString();

        var fileName = $"import-result-{DateTime.UtcNow:yyyyMMdd-HHmmss}.xlsx";
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }
}
