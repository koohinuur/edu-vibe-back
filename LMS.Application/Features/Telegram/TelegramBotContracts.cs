using MediatR;

namespace LMS.Application.Features.Telegram;

/// <summary>The fields the webhook pulls off a Bot API update for the handler.</summary>
public sealed record TelegramUpdateInput(
    long ChatId, string? Text, string? ContactPhone, string? LanguageCode, string? FirstName);

/// <summary>
/// What the bot should reply. <see cref="Text"/> is already localized (HTML); the
/// controller renders the reply keyboard from <see cref="ShowMenu"/> + <see cref="Lang"/>.
/// </summary>
public sealed record TelegramReply(string Text, bool ShowMenu, string Lang);

/// <summary>Process one inbound update; returns the reply to send, or null to just ack.</summary>
public sealed record ProcessTelegramUpdateCommand(TelegramUpdateInput Update) : IRequest<TelegramReply?>;

/// <summary>
/// Broadcast a plain-text message to every (non-blocked) bot subscriber, picking
/// the copy that matches each subscriber's language. Returns how many were queued.
/// </summary>
public sealed record BroadcastTelegramCommand(string TextUz, string TextRu, string TextEn) : IRequest<int>;
