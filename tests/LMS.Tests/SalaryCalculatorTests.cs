using FluentAssertions;
using LMS.Application.Common.Salary;
using LMS.Domain.Enums;
using Xunit;

namespace LMS.Tests;

/// <summary>
/// Locked F5 salary rules: effective% = override ?? default ?? 0; base = Σ(rev×%);
/// percentage punishments computed from the ORIGINAL base; net = max(0, base − Σfixed − Σpct).
/// </summary>
public sealed class SalaryCalculatorTests
{
    private readonly SalaryCalculator _calc = new();

    private static Guid C() => Guid.NewGuid();

    private static SalaryInput Input(
        decimal? teacherDefault,
        IReadOnlyList<ClassRevenue> classes,
        params PunishmentLine[] punishments)
        => new(teacherDefault, classes, punishments);

    private static ClassRevenue Cls(decimal revenue, decimal? overridePct = null) => new(C(), revenue, overridePct);
    private static ClassRevenue FixedCls(decimal revenue, decimal fixedAmount, decimal? overridePct = null)
        => new(C(), revenue, overridePct, fixedAmount);
    private static PunishmentLine Fixed(decimal amount) => new(C(), PunishmentType.FixedAmount, amount, "fixed");
    private static PunishmentLine Pct(decimal percent) => new(C(), PunishmentType.Percentage, percent, "pct");

    // (a) fixed-only punishment
    [Fact]
    public void FixedOnly_subtracts_the_amount()
    {
        // base = 1000 × 50% = 500; − 100 = 400
        var r = _calc.Calculate(Input(50m, new[] { Cls(1000m) }, Fixed(100m)));

        r.BaseSalary.Should().Be(500m);
        r.TotalFixedDeducted.Should().Be(100m);
        r.TotalPercentageDeducted.Should().Be(0m);
        r.Punishments.Should().ContainSingle().Which.Deduction.Should().Be(100m);
        r.NetSalary.Should().Be(400m);
    }

    // (b) percentage-only — computed from base salary
    [Fact]
    public void PercentageOnly_is_computed_from_base_salary()
    {
        // base = 500; 10% of 500 = 50; net = 450
        var r = _calc.Calculate(Input(50m, new[] { Cls(1000m) }, Pct(10m)));

        r.BaseSalary.Should().Be(500m);
        r.TotalPercentageDeducted.Should().Be(50m);
        r.Punishments.Single().Deduction.Should().Be(50m);
        r.NetSalary.Should().Be(450m);
    }

    // (c) both combined — percentage from ORIGINAL base, not the post-fixed remainder
    [Fact]
    public void Combined_percentage_uses_original_base_not_post_fixed()
    {
        // base = 500; fixed 100; pct 10% of 500 = 50 (NOT 10% of 400); net = 500 − 100 − 50 = 350
        var r = _calc.Calculate(Input(50m, new[] { Cls(1000m) }, Fixed(100m), Pct(10m)));

        r.TotalFixedDeducted.Should().Be(100m);
        r.TotalPercentageDeducted.Should().Be(50m);
        r.NetSalary.Should().Be(350m);
    }

    // (d) punishments exceeding base salary clamp to 0 (never negative)
    [Fact]
    public void Punishments_exceeding_base_clamp_to_zero()
    {
        // base = 500; fixed 600 → −100 → clamp 0
        var r = _calc.Calculate(Input(50m, new[] { Cls(1000m) }, Fixed(600m)));
        r.NetSalary.Should().Be(0m);

        // base = 500; pct 200% (1000) + fixed 50 → very negative → clamp 0
        var r2 = _calc.Calculate(Input(50m, new[] { Cls(1000m) }, Pct(200m), Fixed(50m)));
        r2.NetSalary.Should().Be(0m);
    }

    // (e) per-class override wins over the teacher-level default
    [Fact]
    public void Per_class_override_wins_over_teacher_default()
    {
        // default 50%, override 70% → effective 70% → base = 1000 × 70% = 700
        var r = _calc.Calculate(Input(50m, new[] { Cls(1000m, overridePct: 70m) }));

        r.ClassLines.Single().Percentage.Should().Be(70m);
        r.BaseSalary.Should().Be(700m);
        r.NetSalary.Should().Be(700m);
    }

    // (f) a class with no config at all → 0%
    [Fact]
    public void Class_with_no_config_contributes_zero()
    {
        var r = _calc.Calculate(Input(teacherDefault: null, new[] { Cls(1000m, overridePct: null) }));

        r.ClassLines.Single().Percentage.Should().Be(0m);
        r.BaseSalary.Should().Be(0m);
        r.NetSalary.Should().Be(0m);
    }

    // (g) zero paid payments → gross 0 → base 0 → net 0
    [Fact]
    public void No_revenue_yields_zero_everything()
    {
        var r = _calc.Calculate(Input(50m, Array.Empty<ClassRevenue>(), Fixed(100m)));

        r.GrossRevenue.Should().Be(0m);
        r.BaseSalary.Should().Be(0m);
        // a fixed punishment against a 0 base still clamps to 0 (no debt)
        r.NetSalary.Should().Be(0m);
    }

    // (h) a class with a fixed amount pays that flat sum, ignoring revenue + percentage
    [Fact]
    public void Fixed_amount_class_pays_flat_and_ignores_percentage()
    {
        // Even with 1000 revenue and a 70% override, the fixed 300 is what pays.
        var r = _calc.Calculate(Input(50m, new[] { FixedCls(1000m, 300m, overridePct: 70m) }));

        var line = r.ClassLines.Single();
        line.IsFixed.Should().BeTrue();
        line.Amount.Should().Be(300m);
        line.Percentage.Should().Be(0m);
        r.BaseSalary.Should().Be(300m);
        r.NetSalary.Should().Be(300m);
    }

    // (i) fixed amount applies even when the class collected zero revenue this month
    [Fact]
    public void Fixed_amount_applies_with_zero_revenue()
    {
        var r = _calc.Calculate(Input(50m, new[] { FixedCls(0m, 250m) }));

        r.ClassLines.Single().Amount.Should().Be(250m);
        r.BaseSalary.Should().Be(250m);
        r.NetSalary.Should().Be(250m);
    }

    // (j) mix of a fixed-amount class and a percentage class sums both
    [Fact]
    public void Fixed_and_percentage_classes_sum_together()
    {
        // fixed class = 300; pct class = 1000 × 50% = 500; base = 800
        var r = _calc.Calculate(Input(50m, new[] { FixedCls(2000m, 300m), Cls(1000m) }));

        r.BaseSalary.Should().Be(800m);
        r.NetSalary.Should().Be(800m);
    }

    // Bonus: multi-class with mixed override + default, and the auditable per-punishment list
    [Fact]
    public void Multi_class_sums_and_lists_each_punishment()
    {
        // class1 1000 × 70% (override) = 700; class2 500 × 50% (default) = 250; base = 950
        var r = _calc.Calculate(Input(50m,
            new[] { Cls(1000m, 70m), Cls(500m) },
            Fixed(100m), Pct(10m)));

        r.GrossRevenue.Should().Be(1500m);
        r.BaseSalary.Should().Be(950m);
        r.Punishments.Should().HaveCount(2);
        r.Punishments.Single(p => p.Type == PunishmentType.Percentage).Deduction.Should().Be(95m); // 10% of 950
        r.NetSalary.Should().Be(950m - 100m - 95m); // 755
    }
}
