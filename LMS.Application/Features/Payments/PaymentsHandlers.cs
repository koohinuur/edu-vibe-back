using LMS.Application.Common;
using LMS.Application.Common.Abstractions;
using LMS.Application.Common.Models;
using LMS.Application.Common.Salary;
using LMS.Domain.Entities;
using LMS.Domain.Enums;
using LMS.Domain.Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LMS.Application.Features.Payments;

public sealed class PaymentsHandlers(IApplicationDbContext db, ISalaryCalculator salary) :
    IRequestHandler<CreatePaymentCommand, Result<PaymentDto>>,
    IRequestHandler<MarkPaymentPaidCommand, Result<PaymentDto>>,
    IRequestHandler<MarkPaymentFailedCommand, Result<PaymentDto>>,
    IRequestHandler<GetStudentPaymentsQuery, Result<IReadOnlyCollection<PaymentDto>>>,
    IRequestHandler<GetPaymentsQuery, Result<PagedResult<PaymentDto>>>,
    IRequestHandler<GetRevenueSummaryQuery, Result<decimal>>,
    IRequestHandler<GetTeacherSalaryQuery, Result<SalaryBreakdown>>,
    IRequestHandler<SetTeacherSalaryConfigCommand, Result<TeacherSalaryConfigDto>>,
    IRequestHandler<SetTeacherClassFixedAmountCommand, Result<IReadOnlyCollection<TeacherSalaryConfigDto>>>,
    IRequestHandler<GetTeacherSalaryConfigsQuery, Result<IReadOnlyCollection<TeacherSalaryConfigDto>>>,
    IRequestHandler<DeleteTeacherSalaryConfigCommand, Result>,
    IRequestHandler<SetClassMonthlyPriceCommand, Result>
{
    public async Task<Result<PaymentDto>> Handle(CreatePaymentCommand request, CancellationToken cancellationToken)
    {
        Payment p;
        try
        {
            p = new Payment(request.StudentProfileId, request.ClassId, request.PeriodMonth, request.Amount, request.Method);
        }
        catch (LMS.Domain.Exceptions.DomainException ex)
        {
            return Result<PaymentDto>.Fail("VALIDATION", ex.Message);
        }
        await db.Payments.AddAsync(p, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Result<PaymentDto>.Ok(Map(p));
    }

    public async Task<Result<PagedResult<PaymentDto>>> Handle(GetPaymentsQuery request,
        CancellationToken cancellationToken)
    {
        var page = new PageRequest(request.Page, request.PageSize);

        // Overdue is DERIVED, never stored: a Pending payment whose period month
        // has fully passed (school-local, Tashkent UTC+5) is shown as Overdue.
        // Both the status filter and the projection honour this rule so the list,
        // the badges, and the "Overdue"/"Pending" filters all agree.
        var currentMonth = CurrentSchoolMonth();

        var query = db.Payments.AsQueryable();
        if (request.Status is { } status)
        {
            query = status switch
            {
                PaymentStatus.Overdue => query.Where(p => p.Status == PaymentStatus.Pending && p.PeriodMonth < currentMonth),
                PaymentStatus.Pending => query.Where(p => p.Status == PaymentStatus.Pending && p.PeriodMonth >= currentMonth),
                _ => query.Where(p => p.Status == status),
            };
        }
        if (request.ClassId is { } cid) query = query.Where(p => p.ClassId == cid);
        if (request.Month is { } m)
        {
            var first = new DateOnly(m.Year, m.Month, 1);
            query = query.Where(p => p.PeriodMonth == first);
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(p => p.CreatedAt)
            .Skip(page.Skip)
            .Take(page.NormalizedPageSize)
            .Select(p => new PaymentDto(p.Id, p.StudentProfileId, p.ClassId, p.PeriodMonth, p.Amount, p.Method, p.Status))
            .ToListAsync(cancellationToken);
        // Derive the display status in memory (page is small) — keeps the EF
        // projection trivially translatable.
        var items = rows
            .Select(d => d with { Status = DeriveStatus(d.Status, d.PeriodMonth, currentMonth) })
            .ToList();

        return Result<PagedResult<PaymentDto>>.Ok(PagedResult<PaymentDto>.From(items, total, page));
    }

    public async Task<Result<decimal>> Handle(GetRevenueSummaryQuery request, CancellationToken cancellationToken)
    {
        return Result<decimal>.Ok(await db.Payments.Where(x => x.Status == PaymentStatus.Paid)
            .SumAsync(x => x.Amount, cancellationToken));
    }

    public async Task<Result<IReadOnlyCollection<PaymentDto>>> Handle(GetStudentPaymentsQuery request,
        CancellationToken cancellationToken)
    {
        var currentMonth = CurrentSchoolMonth();
        var rows = await db.Payments
            .Where(x => x.StudentProfileId == request.StudentProfileId)
            .Select(p => new PaymentDto(p.Id, p.StudentProfileId, p.ClassId, p.PeriodMonth, p.Amount, p.Method, p.Status))
            .ToListAsync(cancellationToken);
        return Result<IReadOnlyCollection<PaymentDto>>.Ok(
            rows.Select(d => d with { Status = DeriveStatus(d.Status, d.PeriodMonth, currentMonth) }).ToList());
    }

    /// <summary>
    /// The 1st-of-month for the current school-local month (Tashkent UTC+5). A
    /// payment is "overdue" once its period month is strictly before this.
    /// </summary>
    private static DateOnly CurrentSchoolMonth()
    {
        var today = SchoolCalendar.Today(DateTime.UtcNow);
        return new DateOnly(today.Year, today.Month, 1);
    }

    /// <summary>
    /// Overdue is derived, not stored: a still-Pending payment whose period month
    /// is strictly before the current school month is shown as Overdue. Paid /
    /// Failed are never changed.
    /// </summary>
    private static PaymentStatus DeriveStatus(PaymentStatus status, DateOnly periodMonth, DateOnly currentMonth)
        => status == PaymentStatus.Pending && periodMonth < currentMonth ? PaymentStatus.Overdue : status;

    public async Task<Result<PaymentDto>> Handle(MarkPaymentFailedCommand request, CancellationToken cancellationToken)
    {
        var p = await db.Payments.FirstOrDefaultAsync(x => x.Id == request.PaymentId, cancellationToken);
        if (p is null) return Result<PaymentDto>.Fail("NOT_FOUND", "Payment not found.");
        p.MarkFailed();
        await db.SaveChangesAsync(cancellationToken);
        return Result<PaymentDto>.Ok(Map(p));
    }

    public async Task<Result<PaymentDto>> Handle(MarkPaymentPaidCommand request, CancellationToken cancellationToken)
    {
        var p = await db.Payments.FirstOrDefaultAsync(x => x.Id == request.PaymentId, cancellationToken);
        if (p is null) return Result<PaymentDto>.Fail("NOT_FOUND", "Payment not found.");
        p.MarkPaid();
        await db.SaveChangesAsync(cancellationToken);
        return Result<PaymentDto>.Ok(Map(p));
    }

    public async Task<Result<SalaryBreakdown>> Handle(GetTeacherSalaryQuery request, CancellationToken ct)
    {
        var month = new DateOnly(request.Month.Year, request.Month.Month, 1);

        var classIds = await db.Classes.AsNoTracking()
            .Where(c => c.TeacherUserId == request.TeacherId).Select(c => c.Id).ToListAsync(ct);

        var revenueByClass = await db.Payments.AsNoTracking()
            .Where(p => p.Status == PaymentStatus.Paid && p.ClassId != null
                && classIds.Contains(p.ClassId!.Value) && p.PeriodMonth == month)
            .GroupBy(p => p.ClassId!.Value)
            .Select(g => new { ClassId = g.Key, Revenue = g.Sum(x => x.Amount) })
            .ToListAsync(ct);

        var configs = await db.TeacherSalaryConfigs.AsNoTracking()
            .Where(s => s.TeacherId == request.TeacherId).ToListAsync(ct);
        var defaultPct = configs.FirstOrDefault(s => s.ClassId == null)?.Percentage;
        var overrideByClass = configs.Where(s => s.ClassId != null)
            .ToDictionary(s => s.ClassId!.Value, s => s.Percentage);
        // Per-class flat overrides. A fixed amount is paid regardless of revenue,
        // so a fixed-amount class must appear in the breakdown even with zero paid
        // revenue this month.
        var fixedByClass = configs.Where(s => s.ClassId != null && s.FixedAmount != null)
            .ToDictionary(s => s.ClassId!.Value, s => s.FixedAmount!.Value);

        var revenueLookup = revenueByClass.ToDictionary(r => r.ClassId, r => r.Revenue);
        var classIds2 = revenueLookup.Keys.Union(fixedByClass.Keys);
        var classRevenues = classIds2.Select(cid => new ClassRevenue(
            cid,
            revenueLookup.TryGetValue(cid, out var rev) ? rev : 0m,
            overrideByClass.TryGetValue(cid, out var ov) ? ov : (decimal?)null,
            fixedByClass.TryGetValue(cid, out var fx) ? fx : (decimal?)null)).ToList();

        var punishments = (await db.Punishments.AsNoTracking()
                .Where(p => p.TeacherId == request.TeacherId && p.PeriodMonth == month).ToListAsync(ct))
            .Select(p => new PunishmentLine(p.Id, p.Type, p.Value, p.Title)).ToList();

        var breakdown = salary.Calculate(new SalaryInput(defaultPct, classRevenues, punishments));
        return Result<SalaryBreakdown>.Ok(breakdown);
    }

    public async Task<Result<TeacherSalaryConfigDto>> Handle(SetTeacherSalaryConfigCommand request, CancellationToken ct)
    {
        if (!await db.Users.AsNoTracking().AnyAsync(u => u.Id == request.TeacherId, ct))
            return Result<TeacherSalaryConfigDto>.Fail("NOT_FOUND", "Teacher not found.");

        var existing = await db.TeacherSalaryConfigs
            .FirstOrDefaultAsync(s => s.TeacherId == request.TeacherId && s.ClassId == request.ClassId, ct);
        try
        {
            if (existing is null)
            {
                existing = new TeacherSalaryConfig(request.TeacherId, request.ClassId, request.Percentage);
                await db.TeacherSalaryConfigs.AddAsync(existing, ct);
            }
            else
            {
                existing.SetPercentage(request.Percentage);
            }
            await db.SaveChangesAsync(ct);
            return Result<TeacherSalaryConfigDto>.Ok(
                new TeacherSalaryConfigDto(existing.Id, existing.TeacherId, existing.ClassId, existing.Percentage, existing.FixedAmount), "Saved.");
        }
        catch (DomainException ex)
        {
            return Result<TeacherSalaryConfigDto>.Fail("VALIDATION", ex.Message);
        }
    }

    public async Task<Result<IReadOnlyCollection<TeacherSalaryConfigDto>>> Handle(
        SetTeacherClassFixedAmountCommand request, CancellationToken ct)
    {
        if (!await db.Users.AsNoTracking().AnyAsync(u => u.Id == request.TeacherId, ct))
            return Result<IReadOnlyCollection<TeacherSalaryConfigDto>>.Fail("NOT_FOUND", "Teacher not found.");

        var classIds = request.ClassIds.Distinct().ToList();
        if (classIds.Count == 0)
            return Result<IReadOnlyCollection<TeacherSalaryConfigDto>>.Fail("VALIDATION", "Select at least one class.");

        // Every target class must actually be taught by this teacher — assigning a
        // fixed payment for a class the teacher doesn't run makes no sense and would
        // orphan the row.
        var teacherClassIds = await db.Classes.AsNoTracking()
            .Where(c => c.TeacherUserId == request.TeacherId && classIds.Contains(c.Id))
            .Select(c => c.Id).ToListAsync(ct);
        var unknown = classIds.Except(teacherClassIds).ToList();
        if (unknown.Count > 0)
            return Result<IReadOnlyCollection<TeacherSalaryConfigDto>>.Fail(
                "VALIDATION", "One or more classes are not taught by this teacher.");

        // Load the existing per-class rows in one query so the upsert never races
        // itself and the unique (TeacherId, ClassId) index is honoured (no duplicates).
        var existingRows = await db.TeacherSalaryConfigs
            .Where(s => s.TeacherId == request.TeacherId && s.ClassId != null && classIds.Contains(s.ClassId!.Value))
            .ToListAsync(ct);
        var existingByClass = existingRows.ToDictionary(s => s.ClassId!.Value);

        try
        {
            foreach (var classId in classIds)
            {
                if (existingByClass.TryGetValue(classId, out var row))
                {
                    row.SetFixedAmount(request.FixedAmount);
                }
                else
                {
                    // A brand-new fixed-amount row starts at 0% — the fixed amount
                    // is what pays, and percentage is only consulted when no fixed
                    // amount is set (which won't happen here unless later cleared).
                    var created = new TeacherSalaryConfig(request.TeacherId, classId, 0m, request.FixedAmount);
                    await db.TeacherSalaryConfigs.AddAsync(created, ct);
                    existingByClass[classId] = created;
                }
            }
            await db.SaveChangesAsync(ct);
        }
        catch (DomainException ex)
        {
            return Result<IReadOnlyCollection<TeacherSalaryConfigDto>>.Fail("VALIDATION", ex.Message);
        }

        var dtos = classIds
            .Select(cid => existingByClass[cid])
            .Select(s => new TeacherSalaryConfigDto(s.Id, s.TeacherId, s.ClassId, s.Percentage, s.FixedAmount))
            .ToList();
        return Result<IReadOnlyCollection<TeacherSalaryConfigDto>>.Ok(dtos, "Saved.");
    }

    public async Task<Result<IReadOnlyCollection<TeacherSalaryConfigDto>>> Handle(
        GetTeacherSalaryConfigsQuery request, CancellationToken ct)
    {
        var rows = await db.TeacherSalaryConfigs.AsNoTracking()
            .Where(s => s.TeacherId == request.TeacherId)
            .Select(s => new TeacherSalaryConfigDto(s.Id, s.TeacherId, s.ClassId, s.Percentage, s.FixedAmount))
            .ToListAsync(ct);
        return Result<IReadOnlyCollection<TeacherSalaryConfigDto>>.Ok(rows);
    }

    public async Task<Result> Handle(DeleteTeacherSalaryConfigCommand request, CancellationToken ct)
    {
        var s = await db.TeacherSalaryConfigs.FirstOrDefaultAsync(x => x.Id == request.Id, ct);
        if (s is null) return Result.Fail("NOT_FOUND", "Config not found.");
        db.TeacherSalaryConfigs.Remove(s);
        await db.SaveChangesAsync(ct);
        return Result.Ok("Deleted.");
    }

    public async Task<Result> Handle(SetClassMonthlyPriceCommand request, CancellationToken ct)
    {
        var c = await db.Classes.FirstOrDefaultAsync(x => x.Id == request.ClassId, ct);
        if (c is null) return Result.Fail("NOT_FOUND", "Class not found.");
        try
        {
            c.SetMonthlyPrice(request.MonthlyPrice);
        }
        catch (DomainException ex)
        {
            return Result.Fail("VALIDATION", ex.Message);
        }
        await db.SaveChangesAsync(ct);
        return Result.Ok("Saved.");
    }

    private static PaymentDto Map(Payment p)
    {
        return new PaymentDto(p.Id, p.StudentProfileId, p.ClassId, p.PeriodMonth, p.Amount, p.Method, p.Status);
    }
}