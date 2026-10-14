using System.Globalization;

namespace LMS.Application.Features.Notifications;

/// <summary>
/// Localized (uz/ru/en) bodies for the event-driven Telegram DMs — exam result
/// published, attendance marked (absent/late), payment received. Each student's
/// language is resolved best-effort from their Telegram bot language by the
/// notification service; unknown falls back to English.
/// </summary>
public static class EventNotificationTexts
{
    private static string Norm(string? lang) =>
        string.IsNullOrEmpty(lang) ? "en"
        : lang.StartsWith("uz", StringComparison.OrdinalIgnoreCase) ? "uz"
        : lang.StartsWith("ru", StringComparison.OrdinalIgnoreCase) ? "ru"
        : "en";

    /// <summary>An exam result was published to the student — section bands + overall %.</summary>
    public static string ExamResultPublished(string? lang, string examTitle, string bandsLine, decimal overallPercent)
    {
        var pct = overallPercent.ToString("0.#", CultureInfo.InvariantCulture);
        return Norm(lang) switch
        {
            "uz" => $"🎓 Imtihon natijangiz e'lon qilindi: {examTitle}\n{bandsLine}\nUmumiy: {pct}%\n\nBatafsil ko'rish uchun EduVibe'ni oching.",
            "ru" => $"🎓 Опубликован результат экзамена: {examTitle}\n{bandsLine}\nИтого: {pct}%\n\nОткройте EduVibe, чтобы посмотреть подробнее.",
            _ => $"🎓 Your exam result is published: {examTitle}\n{bandsLine}\nOverall: {pct}%\n\nOpen EduVibe to see the details.",
        };
    }

    /// <summary>A student was marked absent or late for a lesson.</summary>
    public static string AttendanceMarked(string? lang, bool late, DateOnly date, string? className)
    {
        var d = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var cls = string.IsNullOrWhiteSpace(className) ? "" : $" — {className}";
        return (Norm(lang), late) switch
        {
            ("uz", true) => $"⏰ Siz {d}{cls} darsiga kech qoldingiz deb belgilandingiz.",
            ("uz", false) => $"❌ Siz {d}{cls} darsida yo'q deb belgilandingiz.",
            ("ru", true) => $"⏰ Вам отмечено опоздание на занятие {d}{cls}.",
            ("ru", false) => $"❌ Вам отмечено отсутствие на занятии {d}{cls}.",
            (_, true) => $"⏰ You were marked late for the lesson on {d}{cls}.",
            (_, false) => $"❌ You were marked absent for the lesson on {d}{cls}.",
        };
    }

    /// <summary>A new assignment was published to the student's class.</summary>
    public static string HomeworkAssigned(string? lang, string title, string? className, DateTime? dueUtc)
    {
        var due = dueUtc is { } d ? d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC" : null;
        var cls = string.IsNullOrWhiteSpace(className) ? null : className;
        return Norm(lang) switch
        {
            "uz" => $"📚 Yangi vazifa: {title}"
                    + (cls is null ? "" : $"\nSinf: {cls}")
                    + (due is null ? "" : $"\nMuddat: {due}")
                    + "\n\nKo'rish uchun EduVibe'ni oching.",
            "ru" => $"📚 Новое задание: {title}"
                    + (cls is null ? "" : $"\nКласс: {cls}")
                    + (due is null ? "" : $"\nСрок: {due}")
                    + "\n\nОткройте EduVibe, чтобы посмотреть.",
            _ => $"📚 New assignment: {title}"
                    + (cls is null ? "" : $"\nClass: {cls}")
                    + (due is null ? "" : $"\nDue: {due}")
                    + "\n\nOpen EduVibe to view it.",
        };
    }

    /// <summary>A student's payment was confirmed as paid (receipt).</summary>
    public static string PaymentReceived(string? lang, decimal amount, string currency, DateOnly periodMonth)
    {
        var amt = $"{amount.ToString("0.##", CultureInfo.InvariantCulture)} {currency}".Trim();
        var month = periodMonth.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        return Norm(lang) switch
        {
            "uz" => $"✅ To'lovingiz qabul qilindi: {amt} ({month}). Rahmat!",
            "ru" => $"✅ Ваш платёж получен: {amt} ({month}). Спасибо!",
            _ => $"✅ Your payment was received: {amt} ({month}). Thank you!",
        };
    }
}
