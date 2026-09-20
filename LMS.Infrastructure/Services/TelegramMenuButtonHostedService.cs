using System.Net.Http.Json;
using LMS.Application.Features.Telegram;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LMS.Infrastructure.Services;

/// <summary>
/// On startup, registers the bot's <em>command menu</em> via the Bot API
/// (<c>setMyCommands</c> for uz/ru/en) and points the chat <em>Menu button</em>
/// at those commands (<c>setChatMenuButton</c> → type "commands"). This makes the
/// bot fully usable from Telegram's native menu — /start, /results, /ask, /help —
/// without depending on the Mini App webview.
///
/// Runs as a background task so it never blocks host startup, and is fully
/// fire-and-forget: if the token is missing or Telegram is unreachable, it logs
/// and moves on. Idempotent — safe to re-run every boot.
/// </summary>
internal sealed class TelegramMenuButtonHostedService : BackgroundService
{
    private const string HttpClientName = "Telegram";
    private const string ApiBase = "https://api.telegram.org";

    private readonly IHttpClientFactory _httpFactory;
    private readonly TelegramOptions _options;
    private readonly ILogger<TelegramMenuButtonHostedService> _logger;

    public TelegramMenuButtonHostedService(
        IHttpClientFactory httpFactory,
        IOptions<TelegramOptions> options,
        ILogger<TelegramMenuButtonHostedService> logger)
    {
        _httpFactory = httpFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var token = _options.BotToken;

        // Nothing to configure (local/dev without a token) — no-op, no noise.
        // A real bot token is "<id>:<secret>"; skip obvious placeholders so dev
        // boots clean instead of logging a confusing 404 from api.telegram.org.
        if (string.IsNullOrWhiteSpace(token) || !token.Contains(':'))
            return;

        try
        {
            var http = _httpFactory.CreateClient(HttpClientName);

            // 1) Register the command menu per language. The default set (no
            //    language_code) covers everyone; uz/ru add localized descriptions.
            foreach (var lang in new[] { "en", "uz", "ru" })
            {
                var commands = TelegramBotTexts.Commands(lang)
                    .Select(c => new { command = c.Command, description = c.Description })
                    .ToArray();
                object payload = lang == "en"
                    ? new { commands }
                    : new { commands, language_code = lang };
                await PostAsync(http, token, "setMyCommands", payload, stoppingToken);
            }

            // 2) Make the Menu button show those commands (type "commands"), so the
            //    bot is navigable from the native menu without the Mini App webview.
            await PostAsync(http, token, "setChatMenuButton",
                new { menu_button = new { type = "commands" } }, stoppingToken);

            _logger.LogInformation("Telegram bot commands + menu button configured.");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutting down before the call finished — ignore.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telegram bot setup (commands/menu) threw — skipped.");
        }
    }

    private async Task PostAsync(HttpClient http, string token, string method, object payload, CancellationToken ct)
    {
        var url = $"{ApiBase}/bot{token}/{method}";
        using var response = await http.PostAsJsonAsync(url, payload, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Telegram {Method} failed ({Status}): {Body}", method, (int)response.StatusCode, body);
        }
    }
}
