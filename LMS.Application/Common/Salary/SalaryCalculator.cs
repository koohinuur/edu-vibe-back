using LMS.Domain.Enums;

namespace LMS.Application.Common.Salary;

/// <summary>
/// Pure, DB-free teacher-salary computation (F5). The handler loads the data
/// (per-class paid revenue, salary configs, punishments) and calls this; keeping
/// it pure makes the money logic unit-testable.
///
/// Rules (locked):
///  • A class with an assigned FixedAmount pays exactly that amount (percentage
///    and revenue are ignored for that class).
///  • Otherwise: effective class % = per-class override ?? teacher default ?? 0,
///    and the class pays classRevenue × effective% / 100.
///  • Base salary = Σ of the per-class amounts (fixed or percentage-derived).
///  • FixedAmount punishment → subtract its Value.
///  • Percentage punishment → subtract (Value/100 × baseSalary) — ALWAYS computed
///    from the original base salary, never the post-fixed remainder.
///  • Net = max(0, base − Σfixed − Σpct). Negative clamps to 0 (no carry-over debt).
/// </summary>
public interface ISalaryCalculator
{
    SalaryBreakdown Calculate(SalaryInput input);
}

public sealed record SalaryInput(
    decimal? TeacherDefaultPercentage,
    IReadOnlyList<ClassRevenue> ClassRevenues,
    IReadOnlyList<PunishmentLine> Punishments);

/// <summary>
/// One class's paid revenue for the month + its optional per-class % override and
/// optional flat <see cref="FixedAmount"/>. When <see cref="FixedAmount"/> is set,
/// the class pays that amount flat and revenue/percentage are ignored for it.
/// </summary>
public sealed record ClassRevenue(Guid ClassId, decimal Revenue, decimal? OverridePercentage, decimal? FixedAmount = null);

public sealed record PunishmentLine(Guid Id, PunishmentType Type, decimal Value, string Title);

public sealed record SalaryBreakdown(
    decimal GrossRevenue,
    decimal BaseSalary,
    IReadOnlyList<ClassSalaryLine> ClassLines,
    IReadOnlyList<AppliedPunishment> Punishments,
    decimal TotalFixedDeducted,
    decimal TotalPercentageDeducted,
    decimal NetSalary);

/// <summary>
/// A single class's contribution to base salary. <see cref="IsFixed"/> is true when
/// the amount came from an assigned flat fixed payment rather than revenue × %.
/// </summary>
public sealed record ClassSalaryLine(Guid ClassId, decimal Revenue, decimal Percentage, decimal Amount, bool IsFixed = false);

/// <summary>A single punishment with its individually-computed deduction — for the auditable statement.</summary>
public sealed record AppliedPunishment(Guid Id, string Title, PunishmentType Type, decimal Value, decimal Deduction);

public sealed class SalaryCalculator : ISalaryCalculator
{
    public SalaryBreakdown Calculate(SalaryInput input)
    {
        var classLines = input.ClassRevenues.Select(c =>
        {
            // A class with an assigned fixed amount pays that flat sum — percentage
            // and revenue are ignored for it.
            if (c.FixedAmount is { } fixedAmount)
                return new ClassSalaryLine(c.ClassId, c.Revenue, 0m, Round(fixedAmount), IsFixed: true);

            var pct = c.OverridePercentage ?? input.TeacherDefaultPercentage ?? 0m;
            return new ClassSalaryLine(c.ClassId, c.Revenue, pct, Round(c.Revenue * pct / 100m));
        }).ToList();

        var gross = input.ClassRevenues.Sum(c => c.Revenue);
        var baseSalary = classLines.Sum(l => l.Amount);

        var applied = input.Punishments.Select(p =>
        {
            var deduction = p.Type == PunishmentType.FixedAmount
                ? p.Value
                : Round(baseSalary * p.Value / 100m); // percentage always against the ORIGINAL base
            return new AppliedPunishment(p.Id, p.Title, p.Type, p.Value, deduction);
        }).ToList();

        var totalFixed = applied.Where(a => a.Type == PunishmentType.FixedAmount).Sum(a => a.Deduction);
        var totalPct = applied.Where(a => a.Type == PunishmentType.Percentage).Sum(a => a.Deduction);

        var net = baseSalary - totalFixed - totalPct;
        if (net < 0m) net = 0m; // clamp — never negative, no carry-over

        return new SalaryBreakdown(gross, baseSalary, classLines, applied, totalFixed, totalPct, net);
    }

    private static decimal Round(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}
