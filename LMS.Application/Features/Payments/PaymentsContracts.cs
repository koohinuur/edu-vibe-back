using LMS.Application.Common.Models;
using LMS.Domain.Enums;
using MediatR;

namespace LMS.Application.Features.Payments;

public sealed record PaymentDto(
    Guid Id,
    Guid StudentProfileId,
    Guid? ClassId,
    DateOnly PeriodMonth,
    decimal Amount,
    PaymentMethod Method,
    PaymentStatus Status,
    // Student's display name (first + last, falling back to email). Populated by
    // the list/by-student queries so admin tables show a name, not an id.
    string? StudentName = null);

public sealed record CreatePaymentCommand(
    Guid StudentProfileId, Guid ClassId, DateOnly PeriodMonth, decimal Amount, PaymentMethod Method)
    : IRequest<Result<PaymentDto>>;

public sealed record MarkPaymentPaidCommand(Guid PaymentId) : IRequest<Result<PaymentDto>>;

public sealed record MarkPaymentFailedCommand(Guid PaymentId) : IRequest<Result<PaymentDto>>;

public sealed record GetStudentPaymentsQuery(Guid StudentProfileId) : IRequest<Result<IReadOnlyCollection<PaymentDto>>>;

public sealed record GetPaymentsQuery(
    int Page = 1,
    int PageSize = 25,
    PaymentStatus? Status = null,
    Guid? ClassId = null,
    DateOnly? Month = null) : IRequest<Result<PagedResult<PaymentDto>>>;

public sealed record GetRevenueSummaryQuery : IRequest<Result<decimal>>;

// ---- F5: teacher salary + config + group price ----------------------------

/// <summary>Computes the teacher's monthly salary breakdown (revenue × % − punishments).</summary>
public sealed record GetTeacherSalaryQuery(Guid TeacherId, DateOnly Month)
    : IRequest<Result<LMS.Application.Common.Salary.SalaryBreakdown>>;

public sealed record TeacherSalaryConfigDto(
    Guid Id, Guid TeacherId, Guid? ClassId, decimal Percentage, decimal? FixedAmount);

/// <summary>Upserts the (TeacherId, ClassId?) revenue-share row. ClassId null = the teacher default.</summary>
public sealed record SetTeacherSalaryConfigCommand(Guid TeacherId, Guid? ClassId, decimal Percentage)
    : IRequest<Result<TeacherSalaryConfigDto>>;

/// <summary>
/// Assigns (or clears with a null amount) a flat fixed monthly payment across one
/// or more of a teacher's classes in a single call. Upserts the per-class
/// (TeacherId, ClassId) rows — existing percentage on a row is preserved, only the
/// fixed amount changes — so the unique index prevents duplicate assignments.
/// </summary>
public sealed record SetTeacherClassFixedAmountCommand(
    Guid TeacherId, IReadOnlyList<Guid> ClassIds, decimal? FixedAmount)
    : IRequest<Result<IReadOnlyCollection<TeacherSalaryConfigDto>>>;

public sealed record GetTeacherSalaryConfigsQuery(Guid TeacherId)
    : IRequest<Result<IReadOnlyCollection<TeacherSalaryConfigDto>>>;

public sealed record DeleteTeacherSalaryConfigCommand(Guid Id) : IRequest<Result>;

/// <summary>Sets (or clears) a class's monthly group price.</summary>
public sealed record SetClassMonthlyPriceCommand(Guid ClassId, decimal? MonthlyPrice) : IRequest<Result>;