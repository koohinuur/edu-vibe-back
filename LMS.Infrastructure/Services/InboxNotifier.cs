using LMS.Application.Common.Abstractions;
using LMS.Domain.Entities;
using LMS.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace LMS.Infrastructure.Services;

/// <summary>
/// <see cref="IInboxNotifier"/> over the existing Conversations/Messages tables
/// plus <see cref="INotificationService"/> for the Telegram mirror. Reuses the
/// same direct (Private) conversation between the two users every time — found
/// by participation, created once on first contact — so notifications land in
/// the normal Messages thread the student already reads, not a parallel system.
/// </summary>
public sealed class InboxNotifier(IApplicationDbContext db, INotificationService telegram) : IInboxNotifier
{
    public async Task NotifyAsync(
        Guid recipientUserId, Guid senderUserId, string text, CancellationToken cancellationToken = default)
    {
        if (recipientUserId == Guid.Empty || string.IsNullOrWhiteSpace(text)) return;

        // In-app copy needs a real sender (Message.SenderUserId is required). When
        // there is none we still deliver the Telegram mirror below.
        if (senderUserId != Guid.Empty && senderUserId != recipientUserId)
        {
            var conversationId =
                await FindDirectConversationAsync(recipientUserId, senderUserId, cancellationToken)
                ?? await CreateDirectConversationAsync(recipientUserId, senderUserId, cancellationToken);

            db.Messages.Add(new Message(conversationId, senderUserId, text));
            await db.SaveChangesAsync(cancellationToken);
        }

        // Mirror to Telegram (no-op if the recipient hasn't linked their account).
        await telegram.NotifyUserAsync(recipientUserId, text, cancellationToken);
    }

    /// <summary>The existing 1:1 Private conversation both users are in, if any.</summary>
    private Task<Guid?> FindDirectConversationAsync(Guid a, Guid b, CancellationToken ct) =>
        (from c in db.Conversations
         where c.Type == ConversationType.Private
             && db.ConversationParticipants.Any(p => p.ConversationId == c.Id && p.UserId == a)
             && db.ConversationParticipants.Any(p => p.ConversationId == c.Id && p.UserId == b)
         select (Guid?)c.Id).FirstOrDefaultAsync(ct);

    /// <summary>Creates a fresh direct conversation with both participants (not yet saved).</summary>
    private async Task<Guid> CreateDirectConversationAsync(Guid a, Guid b, CancellationToken ct)
    {
        var conversation = new Conversation(ConversationType.Private);
        await db.Conversations.AddAsync(conversation, ct);
        await db.ConversationParticipants.AddAsync(new ConversationParticipant(conversation.Id, a), ct);
        await db.ConversationParticipants.AddAsync(new ConversationParticipant(conversation.Id, b), ct);
        return conversation.Id;
    }
}
