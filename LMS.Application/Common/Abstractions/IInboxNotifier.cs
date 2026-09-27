namespace LMS.Application.Common.Abstractions;

/// <summary>
/// Delivers a single notification to a user through <b>both</b> channels the
/// platform already has: the in-app <em>Messages</em> inbox (a direct
/// conversation from <paramref name="senderUserId"/> to the recipient) and, when
/// the recipient has linked their Telegram, a mirror DM via the platform bot
/// (<see cref="INotificationService"/>). One call, one message, two channels —
/// so callers never hand-roll their own messaging or duplicate the Telegram
/// fan-out. The in-app write shares the caller's unit of work / DbContext; the
/// Telegram mirror is fire-and-forget and never throws. When no real sender is
/// available the in-app copy is skipped and only the Telegram mirror is sent.
/// </summary>
public interface IInboxNotifier
{
    /// <summary>
    /// Post <paramref name="text"/> to <paramref name="recipientUserId"/>'s inbox
    /// (as a message from <paramref name="senderUserId"/> in their direct thread)
    /// and mirror it to their Telegram. No-op when recipient or text is empty.
    /// </summary>
    Task NotifyAsync(Guid recipientUserId, Guid senderUserId, string text, CancellationToken cancellationToken = default);
}
