using LMS.Application.Common.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace LMS.Infrastructure.Services;

/// <summary>
/// Resolves a platform user id → their linked Telegram id and DMs them through
/// the platform bot via <see cref="ITelegramNotifier.SendToUserAsync"/>. Works
/// for any role; unlinked users are skipped. Never throws — the underlying
/// notifier swallows delivery failures.
/// </summary>
public sealed class NotificationService(IApplicationDbContext db, ITelegramNotifier notifier)
    : INotificationService
{
    public async Task NotifyUserAsync(Guid userId, string text, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty || string.IsNullOrWhiteSpace(text)) return;
        var telegramId = await db.TelegramAccounts
            .Where(t => t.UserId == userId)
            .Select(t => (long?)t.TelegramUserId)
            .FirstOrDefaultAsync(cancellationToken);
        if (telegramId is { } id) await notifier.SendToUserAsync(id, text, cancellationToken);
    }

    public async Task NotifyUsersAsync(
        IReadOnlyCollection<Guid> userIds, string text, CancellationToken cancellationToken = default)
    {
        if (userIds is null || userIds.Count == 0 || string.IsNullOrWhiteSpace(text)) return;
        var ids = userIds.Where(u => u != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0) return;

        var telegramIds = await db.TelegramAccounts
            .Where(t => ids.Contains(t.UserId))
            .Select(t => t.TelegramUserId)
            .ToListAsync(cancellationToken);
        foreach (var id in telegramIds)
            await notifier.SendToUserAsync(id, text, cancellationToken);
    }

    public async Task NotifyUserAsync(
        Guid userId, Func<string, string> textFactory, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty || textFactory is null) return;
        var row = await (
            from ta in db.TelegramAccounts
            where ta.UserId == userId
            join ts in db.TelegramSubscribers on ta.TelegramUserId equals ts.ChatId into tsj
            from ts in tsj.DefaultIfEmpty()
            select new { ta.TelegramUserId, Lang = ts != null ? ts.LanguageCode : null })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null) return; // not linked
        var text = textFactory(string.IsNullOrWhiteSpace(row.Lang) ? "en" : row.Lang!);
        if (!string.IsNullOrWhiteSpace(text))
            await notifier.SendToUserAsync(row.TelegramUserId, text, cancellationToken);
    }

    public async Task NotifyUsersAsync(
        IReadOnlyCollection<Guid> userIds, Func<string, string> textFactory, CancellationToken cancellationToken = default)
    {
        if (userIds is null || userIds.Count == 0 || textFactory is null) return;
        var ids = userIds.Where(u => u != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0) return;

        var rows = await (
            from ta in db.TelegramAccounts
            where ids.Contains(ta.UserId)
            join ts in db.TelegramSubscribers on ta.TelegramUserId equals ts.ChatId into tsj
            from ts in tsj.DefaultIfEmpty()
            select new { ta.TelegramUserId, Lang = ts != null ? ts.LanguageCode : null })
            .ToListAsync(cancellationToken);

        foreach (var r in rows)
        {
            var text = textFactory(string.IsNullOrWhiteSpace(r.Lang) ? "en" : r.Lang!);
            if (!string.IsNullOrWhiteSpace(text))
                await notifier.SendToUserAsync(r.TelegramUserId, text, cancellationToken);
        }
    }
}
