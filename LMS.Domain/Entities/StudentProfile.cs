using LMS.Domain.Common;
using LMS.Domain.Exceptions;

namespace LMS.Domain.Entities;

public sealed class StudentProfile : BaseEntity
{
    // EF materialisation constructor — User is set via the navigation
    // property when EF loads the entity. Public callers must use the
    // parameterised ctor below, which enforces all invariants.
    private StudentProfile()
    {
    }

    public StudentProfile(Guid userId, User user)
    {
        if (userId == Guid.Empty) throw new DomainException("Student user id is required.");

        UserId = userId;
        User = user ?? throw new DomainException("User is required.");
        XP = 0;
        Streak = 0;
    }

    public Guid UserId { get; private set; }
    // null! signals to the nullable analyser that EF will fill this from the
    // navigation property — every entity going through the public ctor is
    // guaranteed to have it set before it's observable.
    public User User { get; private set; } = null!;

    public int XP { get; private set; }
    public int Streak { get; private set; }

    /// <summary>UTC calendar day of the student's last XP-earning activity. Drives the
    /// consecutive-day streak in <see cref="RegisterDailyActivity"/>. Null ⇒ no activity yet.</summary>
    public DateOnly? LastActivityOn { get; private set; }

    // Profile details surfaced to the admin UI. All optional — existing rows
    // stay valid after migration because every column is nullable. The admin
    // search filter (firstName/lastName) reads from these.
    public string? FirstName { get; private set; }
    public string? LastName { get; private set; }
    public string? PhoneNumber { get; private set; }
    public string? Description { get; private set; }

    /// <summary>Phone number of the student's parent / guardian. Admin-tracked.</summary>
    public string? ParentPhoneNumber { get; private set; }

    /// <summary>CEFR-style level label admin can set (A1, A2, B1, …). Free-text up to 16 chars.</summary>
    public string? Level { get; private set; }

    public string? AvatarUrl { get; private set; }

    public ICollection<Enrollment> Enrollments { get; } = new List<Enrollment>();
    public ICollection<Attendance> Attendances { get; } = new List<Attendance>();
    public ICollection<Submission> Submissions { get; } = new List<Submission>();
    public ICollection<StudentBadge> StudentBadges { get; } = new List<StudentBadge>();
    public ICollection<XpLedger> XpLedgerEntries { get; } = new List<XpLedger>();

    public void AddXp(int amount)
    {
        if (amount <= 0) throw new DomainException("XP amount must be greater than zero.");

        XP += amount;
        Touch();
    }

    public void UpdateStreak(int newStreak)
    {
        if (newStreak < 0) throw new DomainException("Streak cannot be negative.");

        Streak = newStreak;
        Touch();
    }

    /// <summary>
    /// Advances the daily-practice streak for activity on <paramref name="today"/> (UTC day).
    /// Same day ⇒ no change; the very next day ⇒ streak + 1; any longer gap (or first ever)
    /// ⇒ streak resets to 1. Idempotent within a day, so it's safe to call on every submit.
    /// </summary>
    public void RegisterDailyActivity(DateOnly today)
    {
        if (LastActivityOn == today) return;
        Streak = LastActivityOn == today.AddDays(-1) ? Streak + 1 : 1;
        LastActivityOn = today;
        Touch();
    }

    /// <summary>
    /// Updates the editable profile fields. Pass null to clear a field, or
    /// omit it (default null) to leave the existing value untouched is NOT
    /// supported — callers always pass the full intended state.
    /// </summary>
    public void UpdateProfile(string? firstName, string? lastName, string? phoneNumber, string? description)
    {
        FirstName = NormalizeOrNull(firstName, maxLength: 128, fieldName: nameof(FirstName));
        LastName = NormalizeOrNull(lastName, maxLength: 128, fieldName: nameof(LastName));
        PhoneNumber = NormalizeOrNull(phoneNumber, maxLength: 32, fieldName: nameof(PhoneNumber));
        Description = NormalizeOrNull(description, maxLength: 2000, fieldName: nameof(Description));
        Touch();
    }

    public void SetParentPhoneNumber(string? parentPhoneNumber)
    {
        ParentPhoneNumber = NormalizeOrNull(parentPhoneNumber, maxLength: 32, fieldName: nameof(ParentPhoneNumber));
        Touch();
    }

    public void SetLevel(string? level)
    {
        Level = NormalizeOrNull(level, maxLength: 16, fieldName: nameof(Level));
        Touch();
    }

    public void SetAvatarUrl(string? avatarUrl)
    {
        AvatarUrl = string.IsNullOrWhiteSpace(avatarUrl) ? null : avatarUrl.Trim();
        Touch();
    }

    private static string? NormalizeOrNull(string? value, int maxLength, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
            throw new DomainException($"{fieldName} must be {maxLength} characters or fewer.");
        return trimmed;
    }
}
