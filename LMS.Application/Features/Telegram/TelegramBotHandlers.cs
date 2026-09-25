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

        // 0) A "result date" inline button was tapped → show that test's result + feedback.
        if (!string.IsNullOrWhiteSpace(u.CallbackData))
        {
            await db.SaveChangesAsync(ct);
            if (u.CallbackData!.StartsWith("res:", StringComparison.Ordinal)
                && Guid.TryParse(u.CallbackData.AsSpan(4), out var pickedSlot))
                return new TelegramReply(
                    await BuildOneResultAsync(pickedSlot, TelegramSubscriber.DigitsOnly(sub.Phone), lang, ct), true, lang);
            return new TelegramReply(TelegramBotTexts.MenuHint(lang), true, lang);
        }

        // 1) Shared a contact → remember the phone + show the results picker.
        if (!string.IsNullOrWhiteSpace(u.ContactPhone))
        {
            sub.SetPhone(u.ContactPhone!);
            sub.SetState(TelegramChatState.Idle);
            await db.SaveChangesAsync(ct);
            return await ResultsReplyAsync(TelegramSubscriber.DigitsOnly(u.ContactPhone), lang, ct);
        }

        var text = u.Text?.Trim() ?? "";
        var cmd = text.StartsWith('/') ? text.Split(' ')[0].Split('@')[0].ToLowerInvariant() : "";

        // 2) /start or /help → welcome + menu.
        if (cmd is "/start" or "/help")
        {
            sub.SetState(TelegramChatState.Idle);
            await db.SaveChangesAsync(ct);
            return new TelegramReply(TelegramBotTexts.Welcome(lang), true, lang);
        }

        // 3) Ask a question — the button or /ask (contact an admin).
        if (cmd == "/ask" || TelegramBotTexts.IsAskButton(text))
        {
            sub.SetState(TelegramChatState.AwaitingQuestion);
            await db.SaveChangesAsync(ct);
            return new TelegramReply(TelegramBotTexts.AskPrompt(lang), true, lang);
        }

        // 4) Check results — the button or /results.
        if (cmd == "/results" || TelegramBotTexts.IsResultsButton(text))
        {
            sub.SetState(TelegramChatState.Idle);
            await db.SaveChangesAsync(ct);
            // We already know their phone → jump straight to the date picker.
            if (!string.IsNullOrEmpty(sub.Phone))
                return await ResultsReplyAsync(TelegramSubscriber.DigitsOnly(sub.Phone), lang, ct);
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

        // 6) A typed phone number → results picker (fallback for Telegram Desktop,
        //    where the request_contact button isn't tappable).
        var typedDigits = TelegramSubscriber.DigitsOnly(text);
        if (LooksLikePhone(text, typedDigits))
        {
            sub.SetPhone(typedDigits);
            sub.SetState(TelegramChatState.Idle);
            await db.SaveChangesAsync(ct);
            return await ResultsReplyAsync(typedDigits, lang, ct);
        }

        // 7) Anything else → a gentle menu nudge.
        await db.SaveChangesAsync(ct);
        return new TelegramReply(TelegramBotTexts.MenuHint(lang), true, lang);
    }

    /// <summary>True when the text is basically just a phone number.</summary>
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
    /// Reply for a phone lookup: no results → a message; one → that result +
    /// feedback; several → a date picker (one inline button per mock test).
    /// </summary>
    private async Task<TelegramReply> ResultsReplyAsync(string phoneDigits, string lang, CancellationToken ct)
    {
        var matched = await MatchRegistrationsAsync(phoneDigits, ct);
        if (matched.Count == 0)
            return new TelegramReply(TelegramBotTexts.NoResults(lang), true, lang);
        if (matched.Count == 1)
            return new TelegramReply(FormatResult(matched[0], lang), true, lang);

        var buttons = matched
            .Select(m => new TelegramInlineButton($"📝 {m.Title} — {m.When:yyyy-MM-dd}", $"res:{m.SlotId}"))
            .ToList();
        return new TelegramReply(TelegramBotTexts.PickTest(lang), false, lang, buttons);
    }

    /// <summary>The single result for a picked mock test (by slot + this phone).</summary>
    private async Task<string> BuildOneResultAsync(Guid slotId, string phoneDigits, string lang, CancellationToken ct)
    {
        var matched = await MatchRegistrationsAsync(phoneDigits, ct);
        var one = matched.FirstOrDefault(m => m.SlotId == slotId);
        return one is null ? TelegramBotTexts.NoResults(lang) : FormatResult(one, lang);
    }

    /// <summary>Registrations whose phone contains the number's last 9 digits, joined with slot title/date.</summary>
    private async Task<List<MatchedReg>> MatchRegistrationsAsync(string phoneDigits, CancellationToken ct)
    {
        if (phoneDigits.Length < 7) return new List<MatchedReg>();
        var tail = phoneDigits.Length >= 9 ? phoneDigits[^9..] : phoneDigits;

        var regs = await db.MockTestRegistrations.AsNoTracking()
            .Where(r => r.Phone != null && r.Phone.Contains(tail))
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(ct);
        if (regs.Count == 0) return new List<MatchedReg>();

        var slotIds = regs.Select(r => r.SlotId).Distinct().ToList();
        var slots = await db.MockTestSlots.AsNoTracking()
            .Where(s => slotIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => new { s.Title, s.StartsAt }, ct);

        return regs.Select(r =>
        {
            slots.TryGetValue(r.SlotId, out var s);
            return new MatchedReg(r.SlotId, s?.Title ?? "Mock test", s?.StartsAt ?? r.CreatedAt, r);
        }).ToList();
    }

    /// <summary>One test's result block: title, date, section bands (or pending), and feedback.</summary>
    private static string FormatResult(MatchedReg m, string lang)
    {
        var r = m.Reg;
        var sb = new StringBuilder();
        sb.AppendLine(TelegramBotTexts.ResultsHeader(lang));
        sb.AppendLine();
        sb.AppendLine($"📝 <b>{Escape(m.Title)}</b> — {m.When:yyyy-MM-dd}");
        if (r.Overall is not null)
            sb.AppendLine(
                $"L {Fmt(r.Listening)} · R {Fmt(r.Reading)} · W {Fmt(r.Writing)} · S {Fmt(r.Speaking)} → <b>Band {Fmt(r.Overall)}</b>");
        else
            sb.AppendLine($"⏳ {TelegramBotTexts.Pending(lang)}");
        if (!string.IsNullOrWhiteSpace(r.ResultNotes))
        {
            sb.AppendLine();
            sb.AppendLine($"💬 <b>{TelegramBotTexts.FeedbackLabel(lang)}:</b> {Escape(r.ResultNotes!)}");
        }
        return sb.ToString().TrimEnd();
    }

    private sealed record MatchedReg(Guid SlotId, string Title, DateTime When, MockTestRegistration Reg);

    private static string Fmt(decimal? d) =>
        d?.ToString("0.#", CultureInfo.InvariantCulture) ?? "—";

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
