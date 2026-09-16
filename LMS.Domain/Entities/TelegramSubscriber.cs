using LMS.Domain.Common;

namespace LMS.Domain.Entities;

/// <summary>
/// Anyone who has interacted with the platform Telegram bot (pressed /start or
/// sent it a message). We track them so the bot can broadcast news + newly-opened
/// mock tests to everyone, look a person's mock results up by the phone they
/// share, and remember a tiny conversational state (e.g. "waiting for their
/// question"). A subscriber is NOT a platform account — most are anonymous leads.
/// </summary>
public sealed class TelegramSubscriber : BaseEntity
{
    private TelegramSubscriber() { }

    public TelegramSubscriber(long chatId, string? languageCode, string? firstName)
    {
        ChatId = chatId;
        LanguageCode = NormalizeLang(languageCode);
        FirstName = Clip(firstName, 128);
        State = TelegramChatState.Idle;
    }

    /// <summary>Private-chat id == the user's Telegram user id; what we DM back.</summary>
    public long ChatId { get; private set; }
    /// <summary>Phone the user shared (digits only). Null until they share a contact.</summary>
    public string? Phone { get; private set; }
    public string LanguageCode { get; private set; } = "en";
    public string? FirstName { get; private set; }
    public TelegramChatState State { get; private set; }
    /// <summary>Set when a send reveals the bot was blocked, so broadcasts skip them.</summary>
    public bool IsBlocked { get; private set; }

    /// <summary>Refresh language / name on every inbound update (and un-block on return).</summary>
    public void Seen(string? languageCode, string? firstName)
    {
        if (!string.IsNullOrWhiteSpace(languageCode)) LanguageCode = NormalizeLang(languageCode);
        if (!string.IsNullOrWhiteSpace(firstName)) FirstName = Clip(firstName, 128);
        IsBlocked = false;
        Touch();
    }

    public void SetPhone(string phone) { Phone = DigitsOnly(phone); Touch(); }
    public void SetState(TelegramChatState state) { State = state; Touch(); }
    public void MarkBlocked() { IsBlocked = true; Touch(); }

    private static string NormalizeLang(string? code) =>
        string.IsNullOrEmpty(code) ? "en"
        : code.StartsWith("uz", StringComparison.OrdinalIgnoreCase) ? "uz"
        : code.StartsWith("ru", StringComparison.OrdinalIgnoreCase) ? "ru"
        : "en";

    private static string? Clip(string? v, int max)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        var t = v.Trim();
        return t.Length > max ? t[..max] : t;
    }

    /// <summary>Keep only the digits of a phone so "+998 90 …" and "99890…" match.</summary>
    public static string DigitsOnly(string? s) =>
        string.IsNullOrEmpty(s) ? "" : new string(s.Where(char.IsDigit).ToArray());
}

public enum TelegramChatState
{
    Idle = 0,
    AwaitingQuestion = 1,
}
