using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LMS.Application.Features.Auth;
using LMS.Application.Features.Telegram;
using LMS.Infrastructure.Services;
using LMS.WebApi.Common;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace LMS.WebApi.Controllers;

/// <summary>
/// Telegram Mini App auth surface. <c>/auth</c> is the only anonymous endpoint —
/// it trades a signed initData for the same JWT/refresh pair as email login.
/// The link/profile/unlink endpoints operate on the authenticated web user so a
/// signed-in person can connect or disconnect their Telegram from the panels.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class TelegramController(ISender sender) : ControllerBase
{
    /// <summary>
    /// Authenticate with <c>window.Telegram.WebApp.initData</c>. Signs in the
    /// linked user or auto-provisions a Student on first contact. Anonymous +
    /// rate-limited (shares the auth throttle) since it mints a session.
    /// </summary>
    [HttpPost("auth")]
    [AllowAnonymous]
    [EnableRateLimiting("auth-anon")]
    public async Task<ActionResult<ApiResponse<AuthTokensResponse>>> Auth(
        [FromBody] TelegramAuthCommand command, CancellationToken cancellationToken)
    {
        var result = await sender.Send(command, cancellationToken);
        if (!result.Success)
            return Unauthorized(ApiResponse<AuthTokensResponse>.Fail(result.Message ?? "Telegram auth failed"));
        return Ok(ApiResponse<AuthTokensResponse>.Ok(result.Data, result.Message));
    }

    /// <summary>Connect the verified Telegram identity to the signed-in user.</summary>
    [HttpPost("link")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<TelegramProfileDto>>> Link(
        [FromBody] TelegramLinkCommand command, CancellationToken cancellationToken)
    {
        var result = await sender.Send(command, cancellationToken);
        if (!result.Success)
            return BadRequest(ApiResponse<TelegramProfileDto>.Fail(result.Message ?? "Telegram link failed"));
        return Ok(ApiResponse<TelegramProfileDto>.Ok(result.Data, result.Message));
    }

    /// <summary>The signed-in user's linked Telegram profile (null if none).</summary>
    [HttpGet("profile")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<TelegramProfileDto?>>> Profile(CancellationToken cancellationToken)
    {
        var result = await sender.Send(new GetTelegramProfileQuery(), cancellationToken);
        if (!result.Success)
            return BadRequest(ApiResponse<TelegramProfileDto?>.Fail(result.Message ?? "Failed to load profile"));
        return Ok(ApiResponse<TelegramProfileDto?>.Ok(result.Data, result.Message));
    }

    /// <summary>Disconnect the signed-in user's Telegram link (idempotent).</summary>
    [HttpDelete("link")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<object>>> Unlink(CancellationToken cancellationToken)
    {
        var result = await sender.Send(new UnlinkTelegramCommand(), cancellationToken);
        if (!result.Success)
            return BadRequest(ApiResponse<object>.Fail(result.Message ?? "Telegram unlink failed"));
        return Ok(ApiResponse<object>.Ok(new { }, result.Message));
    }

    /// <summary>
    /// Mints a one-time deep-link handoff token for the signed-in user and
    /// returns the Telegram Mini App deep link to open. The Mini App then signs
    /// the same user in (no password) via the token.
    /// </summary>
    [HttpPost("deep-link")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<DeepLinkTokenDto>>> CreateDeepLink(CancellationToken cancellationToken)
    {
        var result = await sender.Send(new CreateDeepLinkTokenCommand(), cancellationToken);
        if (!result.Success)
            return BadRequest(ApiResponse<DeepLinkTokenDto>.Fail(result.Message ?? "Couldn't create deep link"));
        return Ok(ApiResponse<DeepLinkTokenDto>.Ok(result.Data, result.Message));
    }

    // ----- Platform bot settings ------------------------------------------

    /// <summary>
    /// Public bot settings (bot @username + Mini App URL) so any panel can build
    /// the "Open in Telegram" deep link. Sourced from server config — the bot is
    /// fixed by the operator and cannot be changed from the UI (no upsert). Anonymous.
    /// </summary>
    [HttpGet("settings")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<TelegramSettingsDto>>> GetSettings(CancellationToken cancellationToken)
    {
        var result = await sender.Send(new GetTelegramSettingsQuery(), cancellationToken);
        return Ok(ApiResponse<TelegramSettingsDto>.Ok(result.Data, result.Message));
    }

    // ----- Bot webhook (interactive) --------------------------------------

    /// <summary>
    /// Telegram Bot API webhook. Anonymous (Telegram calls it directly) but gated
    /// by the shared <c>Telegram:WebhookSecret</c> echoed in the
    /// <c>X-Telegram-Bot-Api-Secret-Token</c> header — so it fails closed until a
    /// secret is configured. Each update is handed to the bot handler (which
    /// tracks the subscriber, returns mock results by phone, captures questions,
    /// or shows the menu); we answer via Telegram's "respond with a method"
    /// convention — a localized message plus the persistent reply keyboard.
    /// </summary>
    [HttpPost("webhook")]
    [AllowAnonymous]
    public async Task<IActionResult> Webhook(
        [FromBody] TgUpdate update,
        [FromServices] IOptions<TelegramOptions> options,
        CancellationToken ct)
    {
        var opts = options.Value;

        var provided = Request.Headers["X-Telegram-Bot-Api-Secret-Token"].ToString();
        if (string.IsNullOrEmpty(opts.WebhookSecret) || !SecretMatches(provided, opts.WebhookSecret))
            return Unauthorized();

        var msg = update.Message;
        var chatId = msg?.Chat?.Id;
        if (chatId is null) return Ok();

        var input = new TelegramUpdateInput(
            ChatId: chatId.Value,
            Text: msg!.Text,
            ContactPhone: msg.Contact?.PhoneNumber,
            LanguageCode: msg.From?.LanguageCode,
            FirstName: msg.From?.FirstName);

        var reply = await sender.Send(new ProcessTelegramUpdateCommand(input), ct);
        if (reply is null) return Ok();

        // Serialize ourselves so the raw snake_case Bot API keys survive regardless
        // of the JSON naming policy MVC is configured with.
        return Content(JsonSerializer.Serialize(BuildReply(chatId.Value, reply)), "application/json");
    }

    /// <summary>Constant-time compare of the webhook secret header.</summary>
    private static bool SecretMatches(string provided, string expected)
    {
        if (string.IsNullOrEmpty(provided)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected));
    }

    /// <summary>
    /// The Bot API sendMessage payload: the localized body plus (when asked) the
    /// persistent reply keyboard — a request_contact "My results" button and an
    /// "Ask a question" button, in the subscriber's language.
    /// </summary>
    private static Dictionary<string, object?> BuildReply(long chatId, TelegramReply reply)
    {
        var payload = new Dictionary<string, object?>
        {
            ["method"] = "sendMessage",
            ["chat_id"] = chatId,
            ["text"] = reply.Text,
            ["parse_mode"] = "HTML",
            ["disable_web_page_preview"] = true,
        };

        if (reply.ShowMenu)
        {
            payload["reply_markup"] = new Dictionary<string, object?>
            {
                ["keyboard"] = new List<List<Dictionary<string, object?>>>
                {
                    new() { new() { ["text"] = TelegramBotTexts.ResultsButton(reply.Lang), ["request_contact"] = true } },
                    new() { new() { ["text"] = TelegramBotTexts.AskButton(reply.Lang) } },
                },
                ["resize_keyboard"] = true,
            };
        }

        return payload;
    }
}

// ---- Minimal Bot API update shape — only the fields the bot needs. ---------
public sealed record TgUpdate([property: JsonPropertyName("message")] TgMessage? Message);

public sealed record TgMessage(
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("chat")] TgChat? Chat,
    [property: JsonPropertyName("from")] TgFrom? From,
    [property: JsonPropertyName("contact")] TgContact? Contact);

public sealed record TgChat([property: JsonPropertyName("id")] long Id);

public sealed record TgFrom(
    [property: JsonPropertyName("language_code")] string? LanguageCode,
    [property: JsonPropertyName("first_name")] string? FirstName);

public sealed record TgContact([property: JsonPropertyName("phone_number")] string? PhoneNumber);
