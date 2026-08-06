using LMS.Domain.Common;
using LMS.Domain.Exceptions;

namespace LMS.Domain.Entities;

/// <summary>
/// The revenue share a teacher earns. A class-specific row (<see cref="ClassId"/>
/// set) overrides the teacher-wide default (<see cref="ClassId"/> null) in the
/// salary calculation. <see cref="Percentage"/> is 0–100. Uniqueness on
/// (TeacherId, ClassId) is enforced by a NULLS-NOT-DISTINCT index so there's at
/// most one default + one per class.
///
/// <para><b>Fixed amount override.</b> A per-class row may additionally carry a
/// <see cref="FixedAmount"/>. When set, the salary calculation pays that flat
/// amount for the class and ignores the percentage/revenue path entirely — used
/// when a teacher is paid a fixed sum for a specific class regardless of how much
/// revenue it collected. Null <see cref="FixedAmount"/> keeps the existing
/// percentage-of-revenue behaviour. The default (class-less) row never carries a
/// fixed amount.</para>
/// </summary>
public sealed class TeacherSalaryConfig : BaseEntity
{
    private TeacherSalaryConfig() { } // EF

    public TeacherSalaryConfig(Guid teacherId, Guid? classId, decimal percentage, decimal? fixedAmount = null)
    {
        if (teacherId == Guid.Empty) throw new DomainException("Teacher is required.");
        TeacherId = teacherId;
        ClassId = classId;
        SetPercentage(percentage);
        SetFixedAmount(fixedAmount);
    }

    public Guid TeacherId { get; private set; }
    public User? Teacher { get; private set; }
    /// <summary>Null = the teacher's default share; set = a per-class override.</summary>
    public Guid? ClassId { get; private set; }
    public Class? Class { get; private set; }
    public decimal Percentage { get; private set; }

    /// <summary>
    /// Optional flat monthly payment for this class. When non-null it replaces the
    /// percentage-of-revenue calculation for the class. Only valid on a per-class
    /// row (<see cref="ClassId"/> set); ignored/blocked on the default row.
    /// </summary>
    public decimal? FixedAmount { get; private set; }

    public void SetPercentage(decimal percentage)
    {
        if (percentage < 0m || percentage > 100m)
            throw new DomainException("Percentage must be between 0 and 100.");
        Percentage = percentage;
        Touch();
    }

    /// <summary>
    /// Sets (or clears with null) the flat per-class payment. A fixed amount only
    /// makes sense for a specific class, so it can't be attached to the default
    /// (class-less) row. Negative values are rejected.
    /// </summary>
    public void SetFixedAmount(decimal? fixedAmount)
    {
        if (fixedAmount is { } amount)
        {
            if (ClassId is null)
                throw new DomainException("A fixed amount can only be assigned to a specific class.");
            if (amount < 0m)
                throw new DomainException("Fixed amount cannot be negative.");
        }
        FixedAmount = fixedAmount;
        Touch();
    }
}
