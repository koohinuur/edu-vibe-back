using FluentValidation;

namespace LMS.Application.Features.Payments;

/// <summary>
/// Validates a bulk fixed-amount assignment: at least one class, and a
/// non-negative amount when one is supplied (a null amount clears the override).
/// </summary>
public sealed class SetTeacherClassFixedAmountCommandValidator
    : AbstractValidator<SetTeacherClassFixedAmountCommand>
{
    public SetTeacherClassFixedAmountCommandValidator()
    {
        RuleFor(x => x.TeacherId).NotEmpty();
        RuleFor(x => x.ClassIds)
            .NotNull()
            .Must(ids => ids is { Count: > 0 })
            .WithMessage("Select at least one class.");
        RuleForEach(x => x.ClassIds).NotEmpty();
        RuleFor(x => x.FixedAmount)
            .GreaterThanOrEqualTo(0m)
            .When(x => x.FixedAmount.HasValue)
            .WithMessage("Fixed amount cannot be negative.");
    }
}
