using LMS.Domain.Common;
using LMS.Domain.Enums;
using LMS.Domain.Exceptions;

namespace LMS.Domain.Entities;

/// <summary>
/// A teacher / admin uploaded file (PDF, slide deck, audio, image…) that
/// students can read or download. The blob lives on disk under
/// /uploads/materials/&lt;StoredFileName&gt;; only the metadata is in Postgres.
///
/// Visibility is either Public (everyone signed in) or Private (members of
/// the classes listed in <see cref="ClassLinks"/>). The visibility filter is
/// applied in the query handlers — the entity itself only stores intent.
/// </summary>
public sealed class Material : BaseEntity
{
    // EF materialisation ctor.
    private Material() { }

    public Material(
        string title,
        string? description,
        MaterialVisibility visibility,
        string storedFileName,
        string originalFileName,
        string mimeType,
        long fileSize,
        Guid uploadedByUserId)
    {
        Title = NormalizeTitle(title);
        Description = NormalizeDescription(description);
        Visibility = visibility;
        StoredFileName = RequireNonEmpty(storedFileName, nameof(storedFileName));
        OriginalFileName = RequireNonEmpty(originalFileName, nameof(originalFileName));
        MimeType = RequireNonEmpty(mimeType, nameof(mimeType));
        if (fileSize < 0) throw new DomainException("File size cannot be negative.");
        FileSize = fileSize;
        if (uploadedByUserId == Guid.Empty)
            throw new DomainException("Uploader user id is required.");
        UploadedByUserId = uploadedByUserId;
    }

    public string Title { get; private set; } = null!;
    public string? Description { get; private set; }
    public MaterialVisibility Visibility { get; private set; }

    public string StoredFileName { get; private set; } = null!;
    public string OriginalFileName { get; private set; } = null!;
    public string MimeType { get; private set; } = null!;
    public long FileSize { get; private set; }

    public Guid UploadedByUserId { get; private set; }
    public User? UploadedByUser { get; private set; }

    /// <summary>
    /// The course (curriculum template) this material belongs to (spec #9). When
    /// set, the Course Builder's per-lesson material picker offers this material
    /// only for that course. Null = a general/library material not tied to a course.
    /// </summary>
    public Guid? CurriculumTemplateId { get; private set; }

    public ICollection<MaterialClass> ClassLinks { get; } = new List<MaterialClass>();

    public void UpdateDetails(string title, string? description, MaterialVisibility visibility)
    {
        Title = NormalizeTitle(title);
        Description = NormalizeDescription(description);
        Visibility = visibility;
        Touch();
    }

    /// <summary>Sets (or clears) the course this material belongs to.</summary>
    public void SetCourse(Guid? curriculumTemplateId)
    {
        CurriculumTemplateId = curriculumTemplateId == Guid.Empty ? null : curriculumTemplateId;
        Touch();
    }

    private static string NormalizeTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new DomainException("Title is required.");
        var trimmed = title.Trim();
        if (trimmed.Length > 256)
            throw new DomainException("Title must be 256 characters or fewer.");
        return trimmed;
    }

    private static string? NormalizeDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;
        var trimmed = description.Trim();
        if (trimmed.Length > 2000)
            throw new DomainException("Description must be 2000 characters or fewer.");
        return trimmed;
    }

    private static string RequireNonEmpty(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new DomainException($"{name} is required.");
        return value;
    }
}
