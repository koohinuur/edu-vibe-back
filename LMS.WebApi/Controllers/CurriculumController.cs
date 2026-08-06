using LMS.Application.Common.Models;
using LMS.Application.Common.Security;
using LMS.Application.Features.Curriculum;
using LMS.WebApi.Common;
using LMS.WebApi.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LMS.WebApi.Controllers;

/// <summary>
/// Curriculum template engine — list/read reusable learning paths, bind a path
/// to a class (auto-mapping its scheduled sessions to lessons), and read a
/// class's dated curriculum (the "today's topic" + planner source).
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class CurriculumController(ISender sender) : ControllerBase
{
    /// <summary>
    /// Maps a self-scoped handler result to HTTP: FORBIDDEN → 403, NOT_FOUND → 404,
    /// any other failure → 400. Used by the class-curriculum endpoints that
    /// authorize in the handler (admin/office/director OR the class teacher) rather
    /// than via a static permission attribute.
    /// </summary>
    private ActionResult<ApiResponse<T>> Map<T>(Result<T> r) =>
        r.Success
            ? Ok(ApiResponse<T>.Ok(r.Data, r.Message))
            : r.ErrorCode switch
            {
                "NOT_FOUND" => NotFound(ApiResponse<T>.Fail(r.Message ?? "Not found")),
                "FORBIDDEN" => StatusCode(StatusCodes.Status403Forbidden, ApiResponse<T>.Fail(r.Message ?? "Forbidden")),
                _ => BadRequest(ApiResponse<T>.Fail(r.Message ?? "Failed")),
            };

    /// <summary>All published templates (optionally filtered by category) with module/unit/lesson counts.</summary>
    [HttpGet("templates")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<CurriculumTemplateSummaryDto>>>> Templates(
        [FromQuery] LMS.Domain.Entities.CurriculumCategory? category, CancellationToken ct)
    {
        var r = await sender.Send(new GetCurriculumTemplatesQuery(category), ct);
        return Ok(ApiResponse<IReadOnlyCollection<CurriculumTemplateSummaryDto>>.Ok(r.Data, r.Message));
    }

    /// <summary>The full Module → Unit → Lesson tree for one template.</summary>
    [HttpGet("templates/{id:guid}")]
    public async Task<ActionResult<ApiResponse<CurriculumTreeDto>>> Tree(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new GetCurriculumTreeQuery(id), ct);
        return r.ToApiResultOrNotFound();
    }

    /// <summary>Admin: ALL master templates (published + unpublished) with counts + class usage.</summary>
    [HttpGet("admin/templates")]
    [PermissionAuthorize(Permissions.Classes.Update)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<AdminTemplateDto>>>> AdminTemplates(CancellationToken ct)
    {
        var r = await sender.Send(new GetAdminTemplatesQuery(), ct);
        return Ok(ApiResponse<IReadOnlyList<AdminTemplateDto>>.Ok(r.Data, r.Message));
    }

    /// <summary>Admin: edit a template's metadata + published flag.</summary>
    [HttpPut("templates/{id:guid}")]
    [PermissionAuthorize(Permissions.Classes.Update)]
    public async Task<ActionResult<ApiResponse<AdminTemplateDto>>> UpdateTemplate(
        Guid id, [FromBody] UpdateTemplateCommand body, CancellationToken ct)
    {
        var r = await sender.Send(body with { Id = id }, ct);
        if (r.Success) return Ok(ApiResponse<AdminTemplateDto>.Ok(r.Data, r.Message));
        return r.ErrorCode == "NOT_FOUND"
            ? NotFound(ApiResponse<AdminTemplateDto>.Fail(r.Message ?? "Not found"))
            : BadRequest(ApiResponse<AdminTemplateDto>.Fail(r.Message ?? "Failed"));
    }

    /// <summary>Admin: delete a template (blocked while any class uses it).</summary>
    [HttpDelete("templates/{id:guid}")]
    [PermissionAuthorize(Permissions.Classes.Update)]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteTemplate(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new DeleteTemplateCommand(id), ct);
        if (r.Success) return Ok(ApiResponse<bool>.Ok(r.Data, r.Message));
        return r.ErrorCode == "NOT_FOUND"
            ? NotFound(ApiResponse<bool>.Fail(r.Message ?? "Not found"))
            : BadRequest(ApiResponse<bool>.Fail(r.Message ?? "Failed"));
    }

    // ---- Admin template STRUCTURE editing (units + lessons) ----------------

    private ActionResult<ApiResponse<TemplateCourseDto>> Course(Result<TemplateCourseDto> r) =>
        r.Success
            ? Ok(ApiResponse<TemplateCourseDto>.Ok(r.Data, r.Message))
            : r.ErrorCode == "NOT_FOUND"
                ? NotFound(ApiResponse<TemplateCourseDto>.Fail(r.Message ?? "Not found"))
                : BadRequest(ApiResponse<TemplateCourseDto>.Fail(r.Message ?? "Failed"));

    /// <summary>The master template's editable units → lessons tree.</summary>
    [HttpGet("templates/{id:guid}/course")]
    [PermissionAuthorize(Permissions.Classes.Update)]
    public async Task<ActionResult<ApiResponse<TemplateCourseDto>>> TemplateCourse(Guid id, CancellationToken ct)
        => Course(await sender.Send(new GetTemplateCourseQuery(id), ct));

    [HttpPost("templates/{id:guid}/units")]
    [PermissionAuthorize(Permissions.Classes.Update)]
    public async Task<ActionResult<ApiResponse<TemplateCourseDto>>> AddTemplateUnit(
        Guid id, [FromBody] CreateTemplateUnitCommand body, CancellationToken ct)
        => Course(await sender.Send(body with { TemplateId = id }, ct));

    [HttpPost("templates/{id:guid}/units/reorder")]
    [PermissionAuthorize(Permissions.Classes.Update)]
    public async Task<ActionResult<ApiResponse<TemplateCourseDto>>> ReorderTemplateUnits(
        Guid id, [FromBody] ReorderTemplateUnitsCommand body, CancellationToken ct)
        => Course(await sender.Send(body with { TemplateId = id }, ct));

    [HttpPut("template-units/{unitId:guid}")]
    [PermissionAuthorize(Permissions.Classes.Update)]
    public async Task<ActionResult<ApiResponse<TemplateCourseDto>>> UpdateTemplateUnit(
        Guid unitId, [FromBody] UpdateTemplateUnitCommand body, CancellationToken ct)
        => Course(await sender.Send(body with { UnitId = unitId }, ct));

    [HttpDelete("template-units/{unitId:guid}")]
    [PermissionAuthorize(Permissions.Classes.Update)]
    public async Task<ActionResult<ApiResponse<TemplateCourseDto>>> DeleteTemplateUnit(Guid unitId, CancellationToken ct)
        => Course(await sender.Send(new DeleteTemplateUnitCommand(unitId), ct));

    [HttpPost("template-units/{unitId:guid}/lessons")]
    [PermissionAuthorize(Permissions.Classes.Update)]
    public async Task<ActionResult<ApiResponse<TemplateCourseDto>>> AddTemplateLesson(
        Guid unitId, [FromBody] CreateTemplateLessonCommand body, CancellationToken ct)
        => Course(await sender.Send(body with { UnitId = unitId }, ct));

    [HttpPost("template-units/{unitId:guid}/lessons/reorder")]
    [PermissionAuthorize(Permissions.Classes.Update)]
    public async Task<ActionResult<ApiResponse<TemplateCourseDto>>> ReorderTemplateLessons(
        Guid unitId, [FromBody] ReorderTemplateLessonsCommand body, CancellationToken ct)
        => Course(await sender.Send(body with { UnitId = unitId }, ct));

    [HttpPut("template-lessons/{lessonId:guid}")]
    [PermissionAuthorize(Permissions.Classes.Update)]
    public async Task<ActionResult<ApiResponse<TemplateCourseDto>>> UpdateTemplateLesson(
        Guid lessonId, [FromBody] UpdateTemplateLessonCommand body, CancellationToken ct)
        => Course(await sender.Send(body with { LessonId = lessonId }, ct));

    [HttpDelete("template-lessons/{lessonId:guid}")]
    [PermissionAuthorize(Permissions.Classes.Update)]
    public async Task<ActionResult<ApiResponse<TemplateCourseDto>>> DeleteTemplateLesson(Guid lessonId, CancellationToken ct)
        => Course(await sender.Send(new DeleteTemplateLessonCommand(lessonId), ct));

    /// <summary>The reusable day-by-day teaching plan for a template (Day 1 = 1A + 1B …).</summary>
    [HttpGet("templates/{id:guid}/plan")]
    public async Task<ActionResult<ApiResponse<TemplatePlanDto>>> Plan(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new GetTemplatePlanQuery(id), ct);
        return r.ToApiResultOrNotFound();
    }

    // ---- Admin day-plan EDITING (define which lessons form each class day) ---
    // Every command returns the whole refreshed plan so the editor re-renders from
    // one response. Admin-scoped (Classes.Update), like template structure editing.

    private ActionResult<ApiResponse<TemplatePlanDto>> PlanResult(Result<TemplatePlanDto> r) =>
        r.Success
            ? Ok(ApiResponse<TemplatePlanDto>.Ok(r.Data, r.Message))
            : r.ErrorCode == "NOT_FOUND"
                ? NotFound(ApiResponse<TemplatePlanDto>.Fail(r.Message ?? "Not found"))
                : BadRequest(ApiResponse<TemplatePlanDto>.Fail(r.Message ?? "Failed"));

    /// <summary>Append a new empty day to the template's plan.</summary>
    [HttpPost("templates/{id:guid}/plan/days")]
    [PermissionAuthorize(Permissions.Classes.Update)]
    public async Task<ActionResult<ApiResponse<TemplatePlanDto>>> AddPlanDay(
        Guid id, [FromBody] AddPlanDayCommand body, CancellationToken ct)
        => PlanResult(await sender.Send(body with { TemplateId = id }, ct));

    /// <summary>Reorder a template's days (dayIds in top-to-bottom order).</summary>
    [HttpPost("templates/{id:guid}/plan/days/reorder")]
    [PermissionAuthorize(Permissions.Classes.Update)]
    public async Task<ActionResult<ApiResponse<TemplatePlanDto>>> ReorderPlanDays(
        Guid id, [FromBody] ReorderPlanDaysCommand body, CancellationToken ct)
        => PlanResult(await sender.Send(body with { TemplateId = id }, ct));

    /// <summary>Rename a plan-day (null/blank clears the custom title).</summary>
    [HttpPut("plan-days/{dayId:guid}")]
    [PermissionAuthorize(Permissions.Classes.Update)]
    public async Task<ActionResult<ApiResponse<TemplatePlanDto>>> RenamePlanDay(
        Guid dayId, [FromBody] RenamePlanDayCommand body, CancellationToken ct)
        => PlanResult(await sender.Send(body with { PlanDayId = dayId }, ct));

    /// <summary>Delete a plan-day and its lesson links (lessons themselves untouched).</summary>
    [HttpDelete("plan-days/{dayId:guid}")]
    [PermissionAuthorize(Permissions.Classes.Update)]
    public async Task<ActionResult<ApiResponse<TemplatePlanDto>>> DeletePlanDay(Guid dayId, CancellationToken ct)
        => PlanResult(await sender.Send(new DeletePlanDayCommand(dayId), ct));

    /// <summary>Replace a day's lessons (in-day order = list order; empty clears the day).</summary>
    [HttpPut("plan-days/{dayId:guid}/lessons")]
    [PermissionAuthorize(Permissions.Classes.Update)]
    public async Task<ActionResult<ApiResponse<TemplatePlanDto>>> SetPlanDayLessons(
        Guid dayId, [FromBody] SetPlanDayLessonsCommand body, CancellationToken ct)
        => PlanResult(await sender.Send(body with { PlanDayId = dayId }, ct));

    /// <summary>A class's journey through its day-plan (24 day-cards + completion).</summary>
    [HttpGet("class/{classId:guid}/plan-progress")]
    public async Task<ActionResult<ApiResponse<ClassPlanProgressDto>>> ClassPlanProgress(Guid classId, CancellationToken ct)
    {
        var r = await sender.Send(new GetClassPlanProgressQuery(classId), ct);
        if (r.Success) return Ok(ApiResponse<ClassPlanProgressDto>.Ok(r.Data, r.Message));
        return r.ErrorCode == "FORBIDDEN"
            ? StatusCode(403, ApiResponse<ClassPlanProgressDto>.Fail(r.Message ?? "Forbidden"))
            : NotFound(ApiResponse<ClassPlanProgressDto>.Fail(r.Message ?? "Not found"));
    }

    /// <summary>
    /// Bind a template to a class and auto-map its upcoming sessions to the
    /// template's lessons. Body: {"classId": "...", "templateId": "..."}.
    /// </summary>
    // NO permission attribute: like the Course Builder endpoints, a teacher must be
    // able to set up their OWN class's curriculum. Authorization is self-scoped in
    // the handler (admin/office/director OR the class's teacher) → FORBIDDEN ⇒ 403.
    [HttpPost("assign")]
    public async Task<ActionResult<ApiResponse<ClassCurriculumDto>>> Assign(
        [FromBody] AssignCurriculumToClassCommand cmd, CancellationToken ct)
        => Map(await sender.Send(cmd, ct));

    /// <summary>
    /// F6: suggests the class's current curriculum position from its schedule
    /// pattern (StartDate + cadence). Always returns the ordered lesson list so the
    /// admin can edit the suggestion; with no pattern, CanSuggest is false.
    /// </summary>
    [HttpGet("class/{classId:guid}/suggest-position")]
    public async Task<ActionResult<ApiResponse<SuggestPositionDto>>> SuggestPosition(Guid classId, CancellationToken ct)
        => Map(await sender.Send(new SuggestPositionQuery(classId), ct));

    /// <summary>
    /// F6 (LIVE DATA): marks every lesson before the chosen one Completed via
    /// backfilled sessions. Idempotent + reconciling — re-run with a corrected
    /// lesson to fix a mistake; excess backfill is removed, real sessions untouched.
    /// Body: {"lessonId":"..."}.
    /// </summary>
    [HttpPost("class/{classId:guid}/set-position")]
    public async Task<ActionResult<ApiResponse<SetPositionResultDto>>> SetPosition(
        Guid classId, [FromBody] SetPositionCommand cmd, CancellationToken ct)
        => Map(await sender.Send(cmd with { ClassId = classId }, ct));

    /// <summary>A class's dated curriculum: progress + today + next + the full plan.</summary>
    [HttpGet("class/{classId:guid}")]
    public async Task<ActionResult<ApiResponse<ClassCurriculumDto>>> ForClass(Guid classId, CancellationToken ct)
    {
        var r = await sender.Send(new GetClassCurriculumQuery(classId), ct);
        return r.ToApiResultOrNotFound();
    }

    /// <summary>The class's curriculum as a student learning journey (units → lessons, with progress + locks).</summary>
    [HttpGet("class/{classId:guid}/roadmap")]
    public async Task<ActionResult<ApiResponse<StudentRoadmapDto>>> Roadmap(Guid classId, CancellationToken ct)
    {
        var r = await sender.Send(new GetStudentRoadmapQuery(classId), ct);
        return r.Success
            ? Ok(ApiResponse<StudentRoadmapDto>.Ok(r.Data, r.Message))
            : r.ErrorCode == "FORBIDDEN"
                ? StatusCode(StatusCodes.Status403Forbidden, ApiResponse<StudentRoadmapDto>.Fail(r.Message ?? "Forbidden"))
                : NotFound(ApiResponse<StudentRoadmapDto>.Fail(r.Message ?? "Not found"));
    }

    // ===== Course Builder (teacher of the class / admin) ===================
    // Every endpoint returns the whole refreshed course so the roadmap re-renders
    // from one response. NO permission attribute here on purpose: teachers don't
    // hold Classes.Update (that's admin-only, for editing class settings), yet
    // they must build their OWN class's course. Authorization is self-scoped in
    // the handler (WithCourse → class teacher or admin), matching the lesson hub
    // and schedule-curriculum endpoints. FORBIDDEN → 403 via MapBuilder.

    private ActionResult<ApiResponse<ClassCourseBuilderDto>> MapBuilder(Result<ClassCourseBuilderDto> r) =>
        r.Success
            ? Ok(ApiResponse<ClassCourseBuilderDto>.Ok(r.Data, r.Message))
            : r.ErrorCode switch
            {
                "NOT_FOUND" => NotFound(ApiResponse<ClassCourseBuilderDto>.Fail(r.Message ?? "Not found")),
                "FORBIDDEN" => StatusCode(StatusCodes.Status403Forbidden, ApiResponse<ClassCourseBuilderDto>.Fail(r.Message ?? "Forbidden")),
                _ => BadRequest(ApiResponse<ClassCourseBuilderDto>.Fail(r.Message ?? "Failed")),
            };

    /// <summary>Reads (and lazily provisions) the class's editable course structure.</summary>
    [HttpGet("class/{classId:guid}/builder")]
    public async Task<ActionResult<ApiResponse<ClassCourseBuilderDto>>> Builder(Guid classId, CancellationToken ct)
        => MapBuilder(await sender.Send(new GetClassCourseBuilderQuery(classId), ct));

    /// <summary>One-click: deep-copy a curriculum template into this class as its editable course.</summary>
    [HttpPost("class/{classId:guid}/use-template/{templateId:guid}")]
    public async Task<ActionResult<ApiResponse<ClassCourseBuilderDto>>> UseTemplate(
        Guid classId, Guid templateId, CancellationToken ct)
        => MapBuilder(await sender.Send(new CloneTemplateToClassCommand(classId, templateId), ct));

    /// <summary>
    /// F3 one-click course setup — clone the template, generate the 2–3 lessons/day
    /// session calendar, and map sessions to lessons, atomically. Body carries the
    /// schedule window + daily slots: {"templateId","type","daysOfWeekMask",
    /// "startDate","endDate","slots":[{"startsAt","endsAt"}],"roomId"}.
    /// </summary>
    [HttpPost("class/{classId:guid}/generate-course")]
    public async Task<ActionResult<ApiResponse<GenerateCourseResultDto>>> GenerateCourse(
        Guid classId, [FromBody] GenerateCourseCommand cmd, CancellationToken ct)
        => Map(await sender.Send(cmd with { ClassId = classId }, ct));

    [HttpPost("units")]
    public async Task<ActionResult<ApiResponse<ClassCourseBuilderDto>>> CreateUnit(
        [FromBody] CreateCourseUnitCommand cmd, CancellationToken ct)
        => MapBuilder(await sender.Send(cmd, ct));

    [HttpPut("units/{id:guid}")]
    public async Task<ActionResult<ApiResponse<ClassCourseBuilderDto>>> UpdateUnit(
        Guid id, [FromBody] UpdateCourseUnitCommand cmd, CancellationToken ct)
        => MapBuilder(await sender.Send(cmd with { UnitId = id }, ct));

    [HttpDelete("units/{id:guid}")]
    public async Task<ActionResult<ApiResponse<ClassCourseBuilderDto>>> DeleteUnit(Guid id, CancellationToken ct)
        => MapBuilder(await sender.Send(new DeleteCourseUnitCommand(id), ct));

    [HttpPost("units/reorder")]
    public async Task<ActionResult<ApiResponse<ClassCourseBuilderDto>>> ReorderUnits(
        [FromBody] ReorderCourseUnitsCommand cmd, CancellationToken ct)
        => MapBuilder(await sender.Send(cmd, ct));

    [HttpPost("lessons")]
    public async Task<ActionResult<ApiResponse<ClassCourseBuilderDto>>> CreateLesson(
        [FromBody] CreateCourseLessonCommand cmd, CancellationToken ct)
        => MapBuilder(await sender.Send(cmd, ct));

    [HttpPut("lessons/{id:guid}")]
    public async Task<ActionResult<ApiResponse<ClassCourseBuilderDto>>> UpdateLesson(
        Guid id, [FromBody] UpdateCourseLessonCommand cmd, CancellationToken ct)
        => MapBuilder(await sender.Send(cmd with { LessonId = id }, ct));

    [HttpDelete("lessons/{id:guid}")]
    public async Task<ActionResult<ApiResponse<ClassCourseBuilderDto>>> DeleteLesson(Guid id, CancellationToken ct)
        => MapBuilder(await sender.Send(new DeleteCourseLessonCommand(id), ct));

    [HttpPost("lessons/reorder")]
    public async Task<ActionResult<ApiResponse<ClassCourseBuilderDto>>> ReorderLessons(
        [FromBody] ReorderCourseLessonsCommand cmd, CancellationToken ct)
        => MapBuilder(await sender.Send(cmd, ct));

    /// <summary>Create a whole unit and all its lessons in one request (the one-click builder flow).</summary>
    [HttpPost("units/bulk")]
    public async Task<ActionResult<ApiResponse<ClassCourseBuilderDto>>> BulkCreateUnit(
        [FromBody] BulkCreateUnitCommand cmd, CancellationToken ct)
        => MapBuilder(await sender.Send(cmd, ct));

    /// <summary>Duplicate a unit and all of its lessons (appended to the end of the course).</summary>
    [HttpPost("units/{id:guid}/duplicate")]
    public async Task<ActionResult<ApiResponse<ClassCourseBuilderDto>>> DuplicateUnit(Guid id, CancellationToken ct)
        => MapBuilder(await sender.Send(new DuplicateCourseUnitCommand(id), ct));

    /// <summary>Duplicate a lesson within its unit.</summary>
    [HttpPost("lessons/{id:guid}/duplicate")]
    public async Task<ActionResult<ApiResponse<ClassCourseBuilderDto>>> DuplicateLesson(Guid id, CancellationToken ct)
        => MapBuilder(await sender.Send(new DuplicateCourseLessonCommand(id), ct));

    /// <summary>Move a lesson to another unit in the same course. Body: {"targetUnitId":"...","targetOrder":3}.</summary>
    [HttpPost("lessons/{id:guid}/move")]
    public async Task<ActionResult<ApiResponse<ClassCourseBuilderDto>>> MoveLesson(
        Guid id, [FromBody] MoveCourseLessonCommand cmd, CancellationToken ct)
        => MapBuilder(await sender.Send(cmd with { LessonId = id }, ct));
}
