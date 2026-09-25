using System.Net.Http.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LMS.Infrastructure.Services;

/// <summary>
/// On startup, points the bot's webhook at our <c>/api/Telegram/webhook</c>
/// endpoint (Bot API <c>setWebhook</c>) so <c>/start</c> gets a live welcome —
/// no manual curl. Registers the shared secret Telegram must echo back, and
/// limits deliveries to <c>message</c> updates (all we handle). Fully
/// fire-and-forget and idempotent: if the token/URL/secret are missing or the
/// URL isn't https, it logs and moves on — same contract as the menu-button
/// service. Only wires up when <see cref="TelegramOptions.WebhookUrl"/> is set,
/// so dev/local boots clean.
/// </summary>
internal sealed class TelegramWebhookRegistrationHostedService : BackgroundService
{
    private const string HttpClientName = "Telegram";
    private const string ApiBase = "https://api.telegram.org";

    private readonly IHttpClientFactory _httpFactory;
    private readonly TelegramOptions _options;
    private readonly ILogger<TelegramWebhookRegistrationHostedService> _logger;

    public TelegramWebhookRegistrationHostedService(
        IHttpClientFactory httpFactory,
        IOptions<TelegramOptions> options,
        ILogger<TelegramWebhookRegistrationHostedService> logger)
    {
        _httpFactory = httpFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var token = _options.BotToken;
        var url = _options.WebhookUrl?.Trim();
        var secret = _options.WebhookSecret;

        // Nothing to register (no token / URL / secret) — no-op, no noise. A real
        // token is "<id>:<secret>", so skip obvious placeholders.
        if (string.IsNullOrWhiteSpace(token) || !token.Contains(':') ||
            string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(secret))
            return;

        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "Telegram webhook not set — WebhookUrl must be https (got {Url}).", url);
            return;
        }

        try
        {
            var http = _httpFactory.CreateClient(HttpClientName);
            var api = $"{ApiBase}/bot{token}/setWebhook";
            var payload = new
            {
                url,
                secret_token = secret,
                allowed_updates = new[] { "message", "callback_query" },
                drop_pending_updates = true,
            };

            using var response = await http.PostAsJsonAsync(api, payload, stoppingToken);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Telegram webhook registered at {Url}.", url);
            }
            else
            {
                var body = await response.Content.ReadAsStringAsync(stoppingToken);
                _logger.LogWarning(
                    "Telegram setWebhook failed ({Status}): {Body}", (int)response.StatusCode, body);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutting down before the call finished — ignore.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telegram setWebhook threw — webhook not registered.");
        }
    }
}
