using System.Globalization;
using LMS.Application.Common.Models;
using LMS.Application.Common.Salary;
using LMS.Application.Common.Security;
using LMS.Application.Features.Payments;
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
public sealed class PaymentsController(ISender sender) : ControllerBase
{
    [HttpGet]
    [PermissionAuthorize(Permissions.Payments.Read)]
    public async Task<ActionResult<ApiResponse<PagedResult<PaymentDto>>>> All(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        [FromQuery] PaymentStatus? status = null,
        [FromQuery] Guid? classId = null,
        [FromQuery] string? month = null,
        CancellationToken ct = default)
    {
        var r = await sender.Send(new GetPaymentsQuery(page, pageSize, status, classId, ParseMonth(month)), ct);
        return Ok(ApiResponse<PagedResult<PaymentDto>>.Ok(r.Data, r.Message));
    }

    /// <summary>The teacher's monthly salary breakdown (revenue × % − punishments). Defaults to this month.</summary>
    [HttpGet("teacher/{teacherId:guid}/salary")]
    [PermissionAuthorize(Permissions.Payments.Read)]
    public async Task<ActionResult<ApiResponse<SalaryBreakdown>>> Salary(
        Guid teacherId, [FromQuery] string? month, CancellationToken ct)
    {
        var m = ParseMonth(month) ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var r = await sender.Send(new GetTeacherSalaryQuery(teacherId, m), ct);
        return Ok(ApiResponse<SalaryBreakdown>.Ok(r.Data, r.Message));
    }

    /// <summary>The teacher's revenue-share config rows (default + per-class overrides).</summary>
    [HttpGet("salary-configs/teacher/{teacherId:guid}")]
    [PermissionAuthorize(Permissions.Payments.Read)]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<TeacherSalaryConfigDto>>>> SalaryConfigs(
        Guid teacherId, CancellationToken ct)
    {
        var r = await sender.Send(new GetTeacherSalaryConfigsQuery(teacherId), ct);
        return Ok(ApiResponse<IReadOnlyCollection<TeacherSalaryConfigDto>>.Ok(r.Data, r.Message));
    }

    /// <summary>Upsert a (teacher, class?) revenue share. ClassId omitted = the teacher default.</summary>
    [HttpPut("salary-configs")]
    [PermissionAuthorize(Permissions.Payments.Update)]
    public async Task<ActionResult<ApiResponse<TeacherSalaryConfigDto>>> SetSalaryConfig(
        [FromBody] SetTeacherSalaryConfigCommand cmd, CancellationToken ct)
    {
        var r = await sender.Send(cmd, ct);
        return r.Success
            ? Ok(ApiResponse<TeacherSalaryConfigDto>.Ok(r.Data, r.Message))
            : r.ErrorCode == "NOT_FOUND"
                ? NotFound(ApiResponse<TeacherSalaryConfigDto>.Fail(r.Message ?? "Not found"))
                : BadRequest(ApiResponse<TeacherSalaryConfigDto>.Fail(r.Message ?? "Failed"));
    }

    /// <summary>
    /// Assigns (or clears) a flat fixed monthly payment across one or more of the
    /// teacher's classes at once. Body: {"teacherId":"...","classIds":["..."],
    /// "fixedAmount":150}. A null fixedAmount clears the override on those classes
    /// (reverting them to the percentage-of-revenue calculation).
    /// </summary>
    [HttpPut("salary-configs/fixed-amount")]
    [PermissionAuthorize(Permissions.Payments.Update)]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<TeacherSalaryConfigDto>>>> SetFixedAmount(
        [FromBody] SetTeacherClassFixedAmountCommand cmd, CancellationToken ct)
    {
        var r = await sender.Send(cmd, ct);
        return r.Success
            ? Ok(ApiResponse<IReadOnlyCollection<TeacherSalaryConfigDto>>.Ok(r.Data, r.Message))
            : r.ErrorCode == "NOT_FOUND"
                ? NotFound(ApiResponse<IReadOnlyCollection<TeacherSalaryConfigDto>>.Fail(r.Message ?? "Not found"))
                : BadRequest(ApiResponse<IReadOnlyCollection<TeacherSalaryConfigDto>>.Fail(r.Message ?? "Failed"));
    }

    [HttpDelete("salary-configs/{id:guid}")]
    [PermissionAuthorize(Permissions.Payments.Update)]
    public async Task<ActionResult<ApiResponse<object>>> DeleteSalaryConfig(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new DeleteTeacherSalaryConfigCommand(id), ct);
        return r.Success
            ? Ok(ApiResponse<object>.Ok(new { }, r.Message))
            : NotFound(ApiResponse<object>.Fail(r.Message ?? "Not found"));
    }

    /// <summary>Set (or clear) a class's monthly group price. Body: {"classId":"...","monthlyPrice":150}.</summary>
    [HttpPut("class/{classId:guid}/monthly-price")]
    [PermissionAuthorize(Permissions.Payments.Update)]
    public async Task<ActionResult<ApiResponse<object>>> SetClassPrice(
        Guid classId, [FromBody] SetClassMonthlyPriceCommand cmd, CancellationToken ct)
    {
        var r = await sender.Send(cmd with { ClassId = classId }, ct);
        return r.Success
            ? Ok(ApiResponse<object>.Ok(new { }, r.Message))
            : r.ErrorCode == "NOT_FOUND"
                ? NotFound(ApiResponse<object>.Fail(r.Message ?? "Not found"))
                : BadRequest(ApiResponse<object>.Fail(r.Message ?? "Failed"));
    }

    private static DateOnly? ParseMonth(string? month)
        => string.IsNullOrWhiteSpace(month)
            ? null
            : DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d
                : null;

    [HttpGet("student/{studentProfileId:guid}")]
    [PermissionAuthorize(Permissions.Payments.Read)]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<PaymentDto>>>> Student(Guid studentProfileId,
        CancellationToken ct)
    {
        var r = await sender.Send(new GetStudentPaymentsQuery(studentProfileId), ct);
        return Ok(ApiResponse<IReadOnlyCollection<PaymentDto>>.Ok(r.Data, r.Message));
    }

    [HttpGet("revenue")]
    [PermissionAuthorize(Permissions.Payments.Read)]
    public async Task<ActionResult<ApiResponse<decimal>>> Revenue(CancellationToken ct)
    {
        var r = await sender.Send(new GetRevenueSummaryQuery(), ct);
        return Ok(ApiResponse<decimal>.Ok(r.Data, r.Message));
    }

    [HttpPost]
    [PermissionAuthorize(Permissions.Payments.Create)]
    public async Task<ActionResult<ApiResponse<PaymentDto>>> Create([FromBody] CreatePaymentCommand cmd,
        CancellationToken ct)
    {
        var r = await sender.Send(cmd, ct);
        return r.ToApiResult();
    }

    [HttpPost("{id:guid}/paid")]
    [PermissionAuthorize(Permissions.Payments.Update)]
    public async Task<ActionResult<ApiResponse<PaymentDto>>> Paid(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new MarkPaymentPaidCommand(id), ct);
        return r.ToApiResult();
    }

    [HttpPost("{id:guid}/failed")]
    [PermissionAuthorize(Permissions.Payments.Update)]
    public async Task<ActionResult<ApiResponse<PaymentDto>>> Failed(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new MarkPaymentFailedCommand(id), ct);
        return r.ToApiResult();
    }
}
