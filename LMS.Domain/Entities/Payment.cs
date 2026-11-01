using LMS.Domain.Common;
using LMS.Domain.Enums;
using LMS.Domain.Exceptions;

namespace LMS.Domain.Entities;

public sealed class Payment : BaseEntity
{
    private Payment() { } // EF — also avoids binding the nullable ClassId to a ctor param.

    /// <summary>
    /// A student's monthly payment for a class. ClassId + PeriodMonth are
    /// required for new payments (older rows pre-dating group billing keep a
    /// null ClassId). PeriodMonth is normalised to the 1st of the month.
    /// </summary>
    public Payment(Guid studentProfileId, Guid classId, DateOnly periodMonth, decimal amount, PaymentMethod method,
        Currency currency = Currency.UZS, string? comment = null)
    {
        if (studentProfileId == Guid.Empty) throw new DomainException("Student profile id is required.");
        if (classId == Guid.Empty) throw new DomainException("Class is required.");
        if (amount <= 0) throw new DomainException("Amount must be greater than zero.");

        StudentProfileId = studentProfileId;
        ClassId = classId;
        PeriodMonth = new DateOnly(periodMonth.Year, periodMonth.Month, 1);
        Amount = amount;
        Method = method;
        Currency = currency;
        Status = PaymentStatus.Pending;
        SetComment(comment);
    }

    public Guid StudentProfileId { get; private set; }
    public StudentProfile? StudentProfile { get; private set; }

    /// <summary>The class this payment is for. Nullable only for legacy rows.</summary>
    public Guid? ClassId { get; private set; }
    public Class? Class { get; private set; }

    /// <summary>The billing month — always the 1st (platform PeriodMonth convention).</summary>
    public DateOnly PeriodMonth { get; private set; }

    public decimal Amount { get; private set; }
    public PaymentMethod Method { get; private set; }
    /// <summary>Currency of <see cref="Amount"/>. Defaults to UZS for legacy rows.</summary>
    public Currency Currency { get; private set; }
    public PaymentStatus Status { get; private set; }

    /// <summary>Free-text note the staff can attach to a payment (e.g. "paid in cash to Aziza", "partial"). Max 500 chars.</summary>
    public string? Comment { get; private set; }

    /// <summary>Sets (or clears) the payment note. Trims; null/blank clears it.</summary>
    public void SetComment(string? comment)
    {
        var trimmed = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        if (trimmed is { Length: > 500 }) throw new DomainException("Comment must be 500 characters or fewer.");
        Comment = trimmed;
        Touch();
    }

    public void MarkPaid()
    {
        if (Status == PaymentStatus.Paid) throw new DomainException("Payment is already paid.");
        Status = PaymentStatus.Paid;
        Touch();
    }

    public void MarkFailed()
    {
        if (Status == PaymentStatus.Failed) return;
        Status = PaymentStatus.Failed;
        Touch();
    }

    /// <summary>Flag an unpaid payment as overdue (past its due month).</summary>
    public void MarkOverdue()
    {
        if (Status == PaymentStatus.Paid) throw new DomainException("A paid payment can't be marked overdue.");
        Status = PaymentStatus.Overdue;
        Touch();
    }
}
