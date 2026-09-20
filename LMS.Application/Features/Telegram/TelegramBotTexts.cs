namespace LMS.Application.Features.Telegram;

/// <summary>
/// All user-facing bot copy (uz/ru/en) + the reply-keyboard button labels.
/// Shared by the update handler (which builds message bodies + matches button
/// taps) and the webhook controller (which renders the keyboard), so the two
/// never drift apart.
/// </summary>
public static class TelegramBotTexts
{
    public static string Lang(string? code) =>
        string.IsNullOrEmpty(code) ? "en"
        : code.StartsWith("uz", System.StringComparison.OrdinalIgnoreCase) ? "uz"
        : code.StartsWith("ru", System.StringComparison.OrdinalIgnoreCase) ? "ru"
        : "en";

    // ---- Reply-keyboard button labels -------------------------------------
    public static string ResultsButton(string lang) => lang switch
    {
        "uz" => "📊 Natijalarim",
        "ru" => "📊 Мои результаты",
        _ => "📊 My results",
    };

    public static string AskButton(string lang) => lang switch
    {
        "uz" => "❓ Savol berish",
        "ru" => "❓ Задать вопрос",
        _ => "❓ Ask a question",
    };

    /// <summary>Does this text match the "results" button in any locale?</summary>
    public static bool IsResultsButton(string text) =>
        Matches(text, ResultsButton("uz"), ResultsButton("ru"), ResultsButton("en"));

    /// <summary>Does this text match the "ask" button in any locale?</summary>
    public static bool IsAskButton(string text) =>
        Matches(text, AskButton("uz"), AskButton("ru"), AskButton("en"));

    private static bool Matches(string text, params string[] labels)
    {
        var t = text.Trim();
        foreach (var l in labels)
            if (string.Equals(t, l, System.StringComparison.Ordinal)) return true;
        return false;
    }

    // ---- Message bodies ----------------------------------------------------
    public static string Welcome(string lang) => lang switch
    {
        "uz" =>
            "<b>EduVibe botiga xush kelibsiz!</b> 📚\n\n" +
            "Bu yerdan quyidagilarni qilishingiz mumkin:\n" +
            "• 📊 Mock testdagi natijalaringizni ko'rish (telefon raqamingiz orqali)\n" +
            "• ❓ Savol berish / bog'lanish\n" +
            "• 🔔 Yangiliklar va yangi mock testlardan xabardor bo'lish\n\n" +
            "Pastdagi tugmalardan foydalaning 👇",
        "ru" =>
            "<b>Добро пожаловать в бот EduVibe!</b> 📚\n\n" +
            "Здесь вы можете:\n" +
            "• 📊 Посмотреть свои результаты пробного теста (по номеру телефона)\n" +
            "• ❓ Задать вопрос / связаться с нами\n" +
            "• 🔔 Получать новости и анонсы новых пробных тестов\n\n" +
            "Используйте кнопки ниже 👇",
        _ =>
            "<b>Welcome to the EduVibe bot!</b> 📚\n\n" +
            "From here you can:\n" +
            "• 📊 See your mock-test results (by your phone number)\n" +
            "• ❓ Ask a question / get in touch\n" +
            "• 🔔 Get news and new mock-test announcements\n\n" +
            "Use the buttons below 👇",
    };

    public static string SharePhonePrompt(string lang) => lang switch
    {
        "uz" => "Natijalaringizni ko'rish uchun pastdagi «📊 Natijalarim» tugmasini bosib, telefon raqamingizni ulashing.",
        "ru" => "Чтобы увидеть результаты, нажмите «📊 Мои результаты» ниже и поделитесь номером телефона.",
        _ => "To see your results, tap “📊 My results” below and share your phone number.",
    };

    public static string AskPrompt(string lang) => lang switch
    {
        "uz" => "Savolingizni yozib yuboring — jamoamiz ko'rib chiqadi va siz bilan bog'lanadi. ✍️",
        "ru" => "Напишите ваш вопрос — наша команда его увидит и свяжется с вами. ✍️",
        _ => "Type your question — our team will see it and get back to you. ✍️",
    };

    public static string QuestionSaved(string lang) => lang switch
    {
        "uz" => "✅ Savolingiz yuborildi. Tez orada javob beramiz!",
        "ru" => "✅ Ваш вопрос отправлен. Мы скоро ответим!",
        _ => "✅ Your question has been sent. We'll get back to you soon!",
    };

    public static string NoResults(string lang) => lang switch
    {
        "uz" => "Bu raqam bo'yicha mock test ro'yxatidan o'tgan ma'lumot topilmadi. Agar yaqinda yozilgan bo'lsangiz, birozdan so'ng qayta urinib ko'ring yoki biz bilan bog'laning.",
        "ru" => "По этому номеру записей на пробный тест не найдено. Если вы недавно записались, попробуйте позже или свяжитесь с нами.",
        _ => "No mock-test registrations found for this number. If you registered recently, try again later or contact us.",
    };

    public static string ResultsHeader(string lang) => lang switch
    {
        "uz" => "<b>📊 Sizning mock test natijalaringiz</b>",
        "ru" => "<b>📊 Ваши результаты пробных тестов</b>",
        _ => "<b>📊 Your mock-test results</b>",
    };

    public static string Pending(string lang) => lang switch
    {
        "uz" => "natija hali kiritilmagan",
        "ru" => "результат ещё не внесён",
        _ => "result not entered yet",
    };

    public static string MenuHint(string lang) => lang switch
    {
        "uz" => "Pastdagi tugmalardan foydalaning: natijalaringizni ko'ring yoki savol bering 👇",
        "ru" => "Используйте кнопки ниже: посмотрите результаты или задайте вопрос 👇",
        _ => "Use the buttons below: see your results or ask a question 👇",
    };

    // ---- Bot command menu (setMyCommands) --------------------------------
    /// <summary>The "/" command list + Menu button entries for a locale.</summary>
    public static (string Command, string Description)[] Commands(string lang) => lang switch
    {
        "uz" => new[] { ("start", "Boshlash"), ("results", "Natijalarim"), ("ask", "Savol berish"), ("help", "Yordam") },
        "ru" => new[] { ("start", "Начать"), ("results", "Мои результаты"), ("ask", "Задать вопрос"), ("help", "Помощь") },
        _ => new[] { ("start", "Start"), ("results", "My mock results"), ("ask", "Ask a question"), ("help", "Help") },
    };

    // ---- Broadcast templates (plain text — the DM queue sends no parse_mode) --
    public static string NewMockTest(string lang, string title, string when, string? site) => (lang switch
    {
        "uz" => $"🆕 Yangi mock test!\n\n📝 {title}\n🗓 {when}",
        "ru" => $"🆕 Новый пробный тест!\n\n📝 {title}\n🗓 {when}",
        _ => $"🆕 New mock test!\n\n📝 {title}\n🗓 {when}",
    }) + (string.IsNullOrWhiteSpace(site) ? "" : (lang switch
    {
        "uz" => $"\n\n👉 Ro'yxatdan o'ting: {site}",
        "ru" => $"\n\n👉 Записаться: {site}",
        _ => $"\n\n👉 Register: {site}",
    }));
}
