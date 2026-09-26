using LMS.Application.Features.CourseMaterials;
using LMS.Domain.Enums;
using LMS.WebApi.Common;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LMS.WebApi.Controllers;

/// <summary>
/// Per-lesson course materials (spec #9/#10): read a lesson's materials grouped
/// by skill section, list a course's material library for the picker, and
/// attach/detach a course material to a lesson. Authorization is self-scoped in
/// the handlers (admin, a group's teacher, or — for reads — an enrolled student).
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class CourseMaterialsController(ISender sender) : ControllerBase
{
    /// <summary>All materials attached to a lesson (any section), for the lesson view.</summary>
    [HttpGet("lesson/{lessonId:guid}")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<LessonMaterialDto>>>> LessonMaterials(
        Guid lessonId, CancellationToken ct)
        => (await sender.Send(new GetLessonMaterialsQuery(lessonId), ct)).ToApiResult();

    /// <summary>Materials belonging to a course — the per-lesson picker's source.</summary>
    [HttpGet("course/{templateId:guid}")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<CourseMaterialDto>>>> CourseMaterials(
        Guid templateId, CancellationToken ct)
        => (await sender.Send(new GetCourseMaterialsQuery(templateId), ct)).ToApiResult();

    public sealed record AttachBody(Guid MaterialId, LessonMaterialSection Section);

    /// <summary>Attach a course material to a lesson under a section.</summary>
    [HttpPost("lesson/{lessonId:guid}/attach")]
    public async Task<ActionResult<ApiResponse<LessonMaterialDto>>> Attach(
        Guid lessonId, [FromBody] AttachBody body, CancellationToken ct)
        => (await sender.Send(new AttachLessonMaterialCommand(lessonId, body.MaterialId, body.Section), ct)).ToApiResult();

    /// <summary>Remove a material from a lesson's section.</summary>
    [HttpDelete("lesson/{lessonId:guid}/material/{materialId:guid}/section/{section}")]
    public async Task<ActionResult<ApiResponse<bool>>> Detach(
        Guid lessonId, Guid materialId, LessonMaterialSection section, CancellationToken ct)
        => (await sender.Send(new DetachLessonMaterialCommand(lessonId, materialId, section), ct)).ToApiResult();
}
