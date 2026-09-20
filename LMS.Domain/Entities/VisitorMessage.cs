using LMS.Domain.Common;
using LMS.Domain.Exceptions;

namespace LMS.Domain.Entities;

/// <summary>
/// A message left by an unauthenticated visitor on the marketing site
/// (contact form, demo lesson request, etc). Surfaces in the admin inbox
/// and fires a Telegram notification on create.
/// </summary>
public sealed class VisitorMessage : BaseEntity
{
    private VisitorMessage() { }

    public VisitorMessage(
        string name,
        string phone,
        string? email,
        string message,
        VisitorMessageSource source,
        string? course = null,
        string? preferredTime = null,
        string? language = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("Name is required.");
        if (string.IsNullOrWhiteSpace(phone)) throw new DomainException("Phone is required.");
        if (string.IsNullOrWhiteSpace(message)) throw new DomainException("Message is required.");

        Name = name.Trim();
        Phone = phone.Trim();
        Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();
        Message = message.Trim();
        Source = source;
        Course = string.IsNullOrWhiteSpace(course) ? null : course.Trim();
        PreferredTime = string.IsNullOrWhiteSpace(preferredTime) ? null : preferredTime.Trim();
        Language = string.IsNullOrWhiteSpace(language) ? null : language.Trim();
        IsRead = false;
    }

    public string Name { get; private set; } = string.Empty;
    public string Phone { get; private set; } = string.Empty;
    public string? Email { get; private set; }
    public string Message { get; private set; } = string.Empty;
    public VisitorMessageSource Source { get; private set; }
    public string? Course { get; private set; }
    public string? PreferredTime { get; private set; }
    /// <summary>Language code the visitor was browsing in (uz/ru/en) — used for admin context.</summary>
    public string? Language { get; private set; }
    public bool IsRead { get; private set; }
    public DateTime? ReadAt { get; private set; }

    public void MarkRead()
    {
        if (IsRead) return;
        IsRead = true;
        ReadAt = DateTime.UtcNow;
        Touch();
    }

    public void MarkUnread()
    {
        if (!IsRead) return;
        IsRead = false;
        ReadAt = null;
        Touch();
    }
}

public enum VisitorMessageSource
{
    Contact = 1,
    DemoLesson = 2,
    MockTest = 3,
    LevelCheck = 4,
    Telegram = 5,
}
