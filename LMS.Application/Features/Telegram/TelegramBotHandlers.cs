using System.Globalization;
using System.Text;
using LMS.Application.Common.Abstractions;
using LMS.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LMS.Application.Features.Telegram;

/// <summary>
/// The platform bot's conversational brain + the news/mock broadcaster.
///
/// Every inbound update upserts a <see cref="TelegramSubscriber"/> (so broadcasts
/// reach everyone who ever pressed /start). From there:
///   • a shared contact → look up + return the caller's mock-test results by phone;
///   • the "Ask a question" button → capture the next message as a VisitorMessage
///     (surfaced to staff in Inquiries + a group ping);
///   • /start or anything else → a localized menu.
/// The webhook controller turns the returned <see cref="TelegramReply"/> into the
/// Bot API sendMessage response (keyboard + parse_mode).
/// </summary>
public sealed class TelegramBotHandlers(IApplicationDbContext db, ITelegramNotifier notifier)
    : IRequestHandler<ProcessTelegramUpdateCommand, TelegramReply?>,
      IRequestHandler<BroadcastTelegramCommand, int>
{
    public async Task<TelegramReply?> Handle(ProcessTelegramUpdateCommand request, CancellationToken ct)
    {
        var u = request.Update;

        var sub = await db.TelegramSubscribers.FirstOrDefaultAsync(s => s.ChatId == u.ChatId, ct);
        if (sub is null)
        {
            sub = new TelegramSubscriber(u.ChatId, u.LanguageCode, u.FirstName);
            await db.TelegramSubscribers.AddAsync(sub, ct);
        }
        else
        {
            sub.Seen(u.LanguageCode, u.FirstName);
        }

        var lang = sub.LanguageCode;

        // 1) Shared a contact → remember the phone + show their mock results.
        if (!string.IsNullOrWhiteSpace(u.ContactPhone))
        {
            sub.SetPhone(u.ContactPhone!);
            sub.SetState(TelegramChatState.Idle);
            await db.SaveChangesAsync(ct);
            var results = await BuildResultsAsync(TelegramSubscriber.DigitsOnly(u.ContactPhone), lang, ct);
            return new TelegramReply(results, true, lang);
        }

        var text = u.Text?.Trim() ?? "";

        // 2) /start
        if (text == "/start" || text.StartsWith("/start ", StringComparison.Ordinal))
        {
            sub.SetState(TelegramChatState.Idle);
            await db.SaveChangesAsync(ct);
            return new TelegramReply(TelegramBotTexts.Welcome(lang), true, lang);
        }

        // 3) "Ask a question" button
        if (TelegramBotTexts.IsAskButton(text))
        {
            sub.SetState(TelegramChatState.AwaitingQuestion);
            await db.SaveChangesAsync(ct);
            return new TelegramReply(TelegramBotTexts.AskPrompt(lang), true, lang);
        }

        // 4) "My results" typed instead of tapped as a contact button
        if (TelegramBotTexts.IsResultsButton(text))
        {
            sub.SetState(TelegramChatState.Idle);
            await db.SaveChangesAsync(ct);
            return new TelegramReply(TelegramBotTexts.SharePhonePrompt(lang), true, lang);
        }

        // 5) They were prompted for a question → capture it as an inquiry.
        if (sub.State == TelegramChatState.AwaitingQuestion && !string.IsNullOrWhiteSpace(text))
        {
            var vm = new VisitorMessage(
                name: sub.FirstName ?? "Telegram",
                phone: string.IsNullOrEmpty(sub.Phone) ? $"tg:{sub.ChatId}" : sub.Phone!,
                email: null,
                message: text,
                source: VisitorMessageSource.Telegram,
                language: lang);
            await db.VisitorMessages.AddAsync(vm, ct);
            sub.SetState(TelegramChatState.Idle);
            await db.SaveChangesAsync(ct);
            await notifier.SendAsync(
                $"📩 *Telegram savol* — {sub.FirstName ?? "?"} ({sub.Phone ?? "no phone"})\n{text}", ct);
            return new TelegramReply(TelegramBotTexts.QuestionSaved(lang), true, lang);
        }

        // 6) A typed phone number → look up results. Fallback for when the
        //    "share contact" button isn't tappable (e.g. Telegram Desktop) or
        //    the user simply types their number.
        var typedDigits = TelegramSubscriber.DigitsOnly(text);
        if (LooksLikePhone(text, typedDigits))
        {
            sub.SetPhone(typedDigits);
            sub.SetState(TelegramChatState.Idle);
            await db.SaveChangesAsync(ct);
            return new TelegramReply(await BuildResultsAsync(typedDigits, lang, ct), true, lang);
        }

        // 7) Anything else → a gentle menu nudge.
        await db.SaveChangesAsync(ct);
        return new TelegramReply(TelegramBotTexts.MenuHint(lang), true, lang);
    }

    /// <summary>True when the text is basically just a phone number (9–15 digits,
    /// only digits + the usual + - space ( ) separators).</summary>
    private static bool LooksLikePhone(string text, string digits) =>
        digits.Length is >= 9 and <= 15 &&
        text.All(c => char.IsDigit(c) || c is '+' or '-' or ' ' or '(' or ')');

    public async Task<int> Handle(BroadcastTelegramCommand request, CancellationToken ct)
    {
        var subs = await db.TelegramSubscribers.AsNoTracking()
            .Where(s => !s.IsBlocked)
            .Select(s => new { s.ChatId, s.LanguageCode })
            .ToListAsync(ct);

        var queued = 0;
        foreach (var s in subs)
        {
            var text = s.LanguageCode switch
            {
                "uz" => request.TextUz,
                "ru" => request.TextRu,
                _ => request.TextEn,
            };
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (await notifier.SendToUserAsync(s.ChatId, text, ct)) queued++;
        }
        return queued;
    }

    /// <summary>
    /// Every mock registration whose stored phone contains the shared number's
    /// last 9 digits (Uzbek national length), formatted with scores or a pending
    /// marker. HTML — the webhook renders it with parse_mode=HTML.
    /// </summary>
    private async Task<string> BuildResultsAsync(string phoneDigits, string lang, CancellationToken ct)
    {
        if (phoneDigits.Length < 7) return TelegramBotTexts.NoResults(lang);
        var tail = phoneDigits.Length >= 9 ? phoneDigits[^9..] : phoneDigits;

        var regs = await db.MockTestRegistrations.AsNoTracking()
            .Where(r => r.Phone != null && r.Phone.Contains(tail))
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(ct);
        if (regs.Count == 0) return TelegramBotTexts.NoResults(lang);

        var slotIds = regs.Select(r => r.SlotId).Distinct().ToList();
        var slots = await db.MockTestSlots.AsNoTracking()
            .Where(s => slotIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => new { s.Title, s.StartsAt }, ct);

        var sb = new StringBuilder();
        sb.AppendLine(TelegramBotTexts.ResultsHeader(lang));
        foreach (var r in regs)
        {
            slots.TryGetValue(r.SlotId, out var slot);
            var title = slot?.Title ?? "Mock test";
            var when = slot is not null ? slot.StartsAt.ToString("yyyy-MM-dd") : "";
            sb.AppendLine();
            sb.AppendLine($"📝 <b>{Escape(title)}</b>{(string.IsNullOrEmpty(when) ? "" : $" — {when}")}");
            if (r.Overall is not null)
                sb.AppendLine(
                    $"L {Fmt(r.Listening)} · R {Fmt(r.Reading)} · W {Fmt(r.Writing)} · S {Fmt(r.Speaking)} → <b>Band {Fmt(r.Overall)}</b>");
            else
                sb.AppendLine($"⏳ {TelegramBotTexts.Pending(lang)}");
        }
        return sb.ToString().TrimEnd();
    }

    private static string Fmt(decimal? d) =>
        d?.ToString("0.#", CultureInfo.InvariantCulture) ?? "—";

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
