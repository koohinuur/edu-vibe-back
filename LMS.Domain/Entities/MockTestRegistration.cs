using LMS.Domain.Common;
using LMS.Domain.Exceptions;

namespace LMS.Domain.Entities;

/// <summary>
/// One person registered for a <see cref="MockTestSlot"/>. Registrants can be
/// public leads (name + phone/email, no account) or a logged-in student (linked
/// via <see cref="StudentProfileId"/>). Per-section scores + an overall band are
/// attached by an admin after the test.
/// </summary>
public sealed class MockTestRegistration : BaseEntity
{
    private MockTestRegistration() { }

    public MockTestRegistration(Guid slotId, string fullName, string? phone, string? email, Guid? studentProfileId)
    {
        if (slotId == Guid.Empty) throw new DomainException("Mock test slot is required.");
        if (string.IsNullOrWhiteSpace(fullName)) throw new DomainException("Full name is required.");

        SlotId = slotId;
        FullName = fullName.Trim();
        Phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();
        Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();
        StudentProfileId = studentProfileId;
    }

    public Guid SlotId { get; private set; }
    public MockTestSlot? Slot { get; private set; }

    public string FullName { get; private set; } = null!;
    public string? Phone { get; private set; }
    public string? Email { get; private set; }

    /// <summary>Set when a logged-in student registers; null for public leads.</summary>
    public Guid? StudentProfileId { get; private set; }

    // ---- Results (null until an admin attaches them) ------------------------
    public decimal? Listening { get; private set; }
    public decimal? Reading { get; private set; }
    public decimal? Writing { get; private set; }
    public decimal? Speaking { get; private set; }
    public decimal? Overall { get; private set; }
    public string? ResultNotes { get; private set; }

    /// <summary>Attaches (or updates) the final per-section scores + overall band.</summary>
    public void SetResult(decimal? listening, decimal? reading, decimal? writing,
        decimal? speaking, decimal? overall, string? notes)
    {
        Listening = listening;
        Reading = reading;
        Writing = writing;
        Speaking = speaking;
        Overall = overall;
        ResultNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        Touch();
    }
}
