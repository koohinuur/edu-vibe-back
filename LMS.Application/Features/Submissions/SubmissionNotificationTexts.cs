namespace LMS.Application.Features.Submissions;

/// <summary>
/// Localized (uz/ru/en) bodies for the student-facing homework notifications —
/// submission received, graded, returned. Shared by the submission handlers so
/// the in-app Message and the Telegram mirror always carry identical text. The
/// student's language is resolved best-effort from their Telegram bot language;
/// unknown falls back to English.
/// </summary>
public static class SubmissionNotificationTexts
{
    private static string Norm(string? lang) =>
        string.IsNullOrEmpty(lang) ? "en"
        : lang.StartsWith("uz", System.StringComparison.OrdinalIgnoreCase) ? "uz"
        : lang.StartsWith("ru", System.StringComparison.OrdinalIgnoreCase) ? "ru"
        : "en";

    public static string Submitted(string? lang, string title) => Norm(lang) switch
    {
        "uz" => $"✅ Uy vazifangiz qabul qilindi: {title}.",
        "ru" => $"✅ Ваше домашнее задание принято: {title}.",
        _ => $"✅ Your homework was submitted: {title}.",
    };

    public static string Graded(string? lang, string scoreText) => Norm(lang) switch
    {
        "uz" => $"✅ Ishingiz baholandi: {scoreText}. Izohni ko'rish uchun EduVibe'ni oching.",
        "ru" => $"✅ Ваша работа оценена: {scoreText}. Откройте EduVibe, чтобы увидеть отзыв.",
        _ => $"✅ Your submission was graded: {scoreText}. Open EduVibe to see feedback.",
    };

    public static string Returned(string? lang, string? reason)
    {
        var l = Norm(lang);
        var head = l switch
        {
            "uz" => "↩️ Ishingiz qayta ishlash uchun qaytarildi. Tuzatib qayta yuboring.",
            "ru" => "↩️ Ваша работа возвращена на доработку. Исправьте и отправьте снова.",
            _ => "↩️ Your submission was returned for a redo. Revise and resubmit.",
        };
        if (string.IsNullOrWhiteSpace(reason)) return head;
        var label = l switch { "uz" => "Sabab", "ru" => "Причина", _ => "Reason" };
        return $"{head}\n{label}: {reason.Trim()}";
    }
}
