using LMS.Domain.Common;
using LMS.Domain.Enums;
using LMS.Domain.Exceptions;

namespace LMS.Domain.Entities;

public sealed class StaffProfile : BaseEntity
{
    private StaffProfile() { }

    public StaffProfile(Guid userId, EmploymentType employmentType)
    {
        if (userId == Guid.Empty) throw new DomainException("User id is required.");
        UserId = userId;
        EmploymentType = employmentType;
    }

    public Guid UserId { get; private set; }
    public User? User { get; private set; }
    public EmploymentType EmploymentType { get; private set; }

    // Office Admin surface: editable profile fields. Mirror of StudentProfile —
    // every column nullable so the migration is safe over existing rows.
    public string? FirstName { get; private set; }
    public string? LastName { get; private set; }
    public string? PhoneNumber { get; private set; }
    public string? Description { get; private set; }
    /// <summary>
    /// Job position / title — "IELTS Expert", "Math Lead", etc. Surfaced
    /// on the marketing-site teachers grid.
    /// </summary>
    public string? Position { get; private set; }
    /// <summary>
    /// Comma/newline-separated certifications &amp; degrees ("IELTS, CELTA,
    /// BSc Physics"). Rendered as badges on the marketing teachers grid /
    /// profile. Split on comma or newline by the frontend.
    /// </summary>
    public string? Certifications { get; private set; }
    /// <summary>Whole years of teaching experience shown on the marketing card; null when unset.</summary>
    public int? YearsExperience { get; private set; }
    /// <summary>
    /// Admin toggle — whether this staff member appears on the public
    /// marketing-site "Meet our teachers" section. Defaults to false so
    /// new staff don't leak onto the marketing site by accident.
    /// </summary>
    public bool IsPubliclyVisible { get; private set; }

    /// <summary>
    /// Relative path under /uploads/avatars/ of the user's avatar image, or
    /// null if they haven't uploaded one. The file itself is served by the
    /// static file middleware; this field carries only the filename so the
    /// frontend can build a URL.
    /// </summary>
    public string? AvatarUrl { get; private set; }

    public void SetAvatarUrl(string? avatarUrl)
    {
        AvatarUrl = string.IsNullOrWhiteSpace(avatarUrl) ? null : avatarUrl.Trim();
        Touch();
    }
    /// Teaching specializations (Math, Physics, Chinese, …). Admin-curated
    /// list (see <see cref="Specialization"/>) — the teacher picks one or
    /// more from their profile page.
    /// </summary>
    public ICollection<StaffSpecialization> Specializations { get; } = new List<StaffSpecialization>();

    public void SetEmploymentType(EmploymentType employmentType)
    {
        EmploymentType = employmentType;
        Touch();
    }

    /// <summary>Sets the marketing credentials — certifications text + years of experience (clamped 0-80).</summary>
    public void SetCredentials(string? certifications, int? yearsExperience)
    {
        Certifications = NormalizeOrNull(certifications, maxLength: 512, fieldName: nameof(Certifications));
        YearsExperience = yearsExperience is null ? null : Math.Clamp(yearsExperience.Value, 0, 80);
        Touch();
    }

    /// <summary>
    /// Updates the editable profile fields. All arguments are always passed
    /// (no partial-update form) so the caller controls clearing.
    /// </summary>
    public void UpdateProfile(
        string? firstName,
        string? lastName,
        string? phoneNumber,
        string? description,
        string? position = null)
    {
        FirstName = NormalizeOrNull(firstName, maxLength: 128, fieldName: nameof(FirstName));
        LastName = NormalizeOrNull(lastName, maxLength: 128, fieldName: nameof(LastName));
        PhoneNumber = NormalizeOrNull(phoneNumber, maxLength: 32, fieldName: nameof(PhoneNumber));
        Description = NormalizeOrNull(description, maxLength: 2000, fieldName: nameof(Description));
        Position = NormalizeOrNull(position, maxLength: 128, fieldName: nameof(Position));
        Touch();
    }

    /// <summary>Admin flips this when a staff member should/should not appear on the marketing site.</summary>
    public void SetPubliclyVisible(bool value)
    {
        if (IsPubliclyVisible == value) return;
        IsPubliclyVisible = value;
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
