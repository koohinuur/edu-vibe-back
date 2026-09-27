using LMS.Domain.Common;
using LMS.Domain.Enums;
using LMS.Domain.Exceptions;

namespace LMS.Domain.Entities;

public sealed class Class : BaseEntity
{
    // EF materialisation constructor — fields are populated reflectively by
    // EF Core when reading from the database. Public callers must use the
    // parameterised ctor below, which enforces all invariants.
    private Class()
    {
    }

    public Class(string title, int maxStudents, Modality modality)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new DomainException("Class title is required.");

        if (maxStudents <= 0) throw new DomainException("Max students must be greater than zero.");

        Title = title.Trim();
        MaxStudents = maxStudents;
        Modality = modality;
        Status = ClassStatus.Planned;
    }

    // null! signals to the nullable analyser that the EF materialisation
    // path will populate this — the public ctor always sets it before the
    // entity is observable.
    public string Title { get; private set; } = null!;
    public int MaxStudents { get; private set; }
    public Modality Modality { get; private set; }
    public ClassStatus Status { get; private set; }

    public Guid? TeacherUserId { get; private set; }
    public User? Teacher { get; private set; }

    /// <summary>The curriculum template this class follows (null = ad-hoc class, pre-curriculum behaviour).</summary>
    public Guid? CurriculumTemplateId { get; private set; }
    public CurriculumTemplate? CurriculumTemplate { get; private set; }

    /// <summary>Monthly group price per student (currency-agnostic). Null = not priced yet.</summary>
    public decimal? MonthlyPrice { get; private set; }

    /// <summary>
    /// The group's type — e.g. "IELTS", "Pre-IELTS", "General English", or a custom
    /// value the admin typed. Free-form so admins can add their own types; drives
    /// exam type (F8) and helps categorise groups. Null = not set.
    /// </summary>
    public string? GroupType { get; private set; }

    public ICollection<Enrollment> Enrollments { get; } = new List<Enrollment>();
    public ICollection<Assignment> Assignments { get; } = new List<Assignment>();
    public ICollection<ClassResource> Resources { get; } = new List<ClassResource>();

    public void EnrollStudent(Guid studentProfileId)
    {
        if (studentProfileId == Guid.Empty) throw new DomainException("Student profile id is required.");

        var activeEnrollmentsCount = Enrollments.Count(x => x.Status == EnrollmentStatus.Active);
        if (activeEnrollmentsCount >= MaxStudents) throw new DomainException("Class is full.");

        if (Enrollments.Any(x => x.StudentProfileId == studentProfileId && x.Status != EnrollmentStatus.Dropped))
            throw new DomainException("Student is already enrolled.");

        Enrollments.Add(Enrollment.Create(Id, studentProfileId, Enrollments));
        Touch();
    }

    public void AssignTeacher(User teacher)
    {
        if (teacher is null) throw new DomainException("Teacher is required.");

        TeacherUserId = teacher.Id;
        Teacher = teacher;
        Touch();
    }

    /// <summary>Clears the primary teacher (used when the class's teacher list is emptied).</summary>
    public void UnassignTeacher()
    {
        TeacherUserId = null;
        Teacher = null;
        Touch();
    }

    public void UpdateDetails(string title, int maxStudents, Modality modality)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new DomainException("Class title is required.");
        if (maxStudents <= 0) throw new DomainException("Max students must be greater than zero.");
        if (Status == ClassStatus.Cancelled)
            throw new DomainException("Cannot modify a cancelled class.");

        Title = title.Trim();
        MaxStudents = maxStudents;
        Modality = modality;
        Touch();
    }

    public void Cancel()
    {
        if (Status == ClassStatus.Cancelled) return;
        Status = ClassStatus.Cancelled;
        Touch();
    }

    /// <summary>Bring an archived (cancelled) class back to the normal operating state.
    /// No-op for a class that isn't cancelled.</summary>
    public void Reactivate()
    {
        if (Status != ClassStatus.Cancelled) return;
        Status = ClassStatus.Planned;
        Touch();
    }

    /// <summary>Sets (or clears) the curriculum template the class follows.</summary>
    public void SetCurriculumTemplate(Guid? templateId)
    {
        CurriculumTemplateId = templateId;
        Touch();
    }

    /// <summary>Sets (or clears) the monthly group price. Negative is rejected.</summary>
    public void SetMonthlyPrice(decimal? price)
    {
        if (price is < 0m) throw new DomainException("Monthly price can't be negative.");
        MonthlyPrice = price;
        Touch();
    }

    /// <summary>Sets (or clears) the group's type. Trims; null/blank clears it. Max 64 chars.</summary>
    public void SetGroupType(string? groupType)
    {
        var trimmed = string.IsNullOrWhiteSpace(groupType) ? null : groupType.Trim();
        if (trimmed is { Length: > 64 })
            throw new DomainException("Group type must be 64 characters or fewer.");
        GroupType = trimmed;
        Touch();
    }
}