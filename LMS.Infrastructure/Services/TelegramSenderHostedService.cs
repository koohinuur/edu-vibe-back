using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LMS.Application.Common.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LMS.Infrastructure.Services;

/// <summary>
/// Single-reader background worker that drains the Telegram queue and POSTs
/// to <c>api.telegram.org/sendMessage</c>. Behaviour:
///
///   • Retries 5xx and transient network errors with exponential backoff.
///   • Honors HTTP 429 <c>retry_after</c> (header or JSON body parameter).
///   • Gives up on 4xx (other than 429) — payload is bad, retrying won't help.
///   • Pulls bot token + chat id from <see cref="TelegramOptions"/> (cached, no
///     IConfiguration lookup per call).
///   • Stops cleanly on host shutdown; the await-foreach exits when the channel
///     completes or stoppingToken fires.
/// </summary>
internal sealed class TelegramSenderHostedService : BackgroundService
{
    private const string HttpClientName = "Telegram";
    private const string ApiBase = "https://api.telegram.org";

    private readonly TelegramNotifier _notifier;
    private readonly IHttpClientFactory _httpFactory;
    private readonly TelegramOptions _options;
    private readonly ILogger<TelegramSenderHostedService> _logger;

    public TelegramSenderHostedService(
        ITelegramNotifier notifier,
        IHttpClientFactory httpFactory,
        IOptions<TelegramOptions> options,
        ILogger<TelegramSenderHostedService> logger)
    {
        // The hosted service needs the channel reader, which is internal to the
        // concrete TelegramNotifier — cast is intentional and safe (we register
        // the singleton both as the interface and the concrete type).
        _notifier = (TelegramNotifier)notifier;
        _httpFactory = httpFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.IsEnabled) return;

        // Rate-limit so a broadcast stays under Telegram's ~30/sec bot limit and
        // doesn't trigger a 429 storm. Pace from the end of each send (slightly
        // under the configured rate, which is the safe side).
        var interval = TimeSpan.FromMilliseconds(1000.0 / Math.Max(1, _options.SendsPerSecond));
        var nextSlot = DateTime.UtcNow;

        try
        {
            await foreach (var message in _notifier.Reader.ReadAllAsync(stoppingToken))
            {
                var now = DateTime.UtcNow;
                if (nextSlot > now)
                {
                    try { await Task.Delay(nextSlot - now, stoppingToken); }
                    catch (OperationCanceledException) { return; }
                }
                await SendWithRetryAsync(message, stoppingToken);
                nextSlot = DateTime.UtcNow + interval;
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutting down — normal exit.
        }
    }

    private async Task SendWithRetryAsync(TelegramOutbound message, CancellationToken ct)
    {
        var max = Math.Max(1, _options.MaxAttempts);
        for (var attempt = 1; attempt <= max; attempt++)
        {
            var outcome = await TrySendOnceAsync(message, ct);
            switch (outcome.Kind)
            {
                case OutcomeKind.Success:
                    return;
                case OutcomeKind.PermanentFailure:
                    // 4xx (non-429) — payload broken, no point retrying.
                    return;
                case OutcomeKind.TransientFailure:
                    if (attempt >= max)
                    {
                        _logger.LogError(
                            "Telegram send exhausted {Max} attempts — dropping message.", max);
                        return;
                    }
                    var delay = outcome.RetryAfter ?? ExponentialBackoff(attempt);
                    try { await Task.Delay(delay, ct); }
                    catch (OperationCanceledException) { return; }
                    break;
            }
        }
    }

    private async Task<Outcome> TrySendOnceAsync(TelegramOutbound message, CancellationToken ct)
    {
        try
        {
            var http = _httpFactory.CreateClient(HttpClientName);
            var url = $"{ApiBase}/bot{message.BotToken}/sendMessage";
            // parse_mode is omitted for plain-text DMs (no MarkdownV2 escaping needed).
            object payload = message.ParseMode is null
                ? new { chat_id = message.ChatId, text = message.Text, disable_web_page_preview = true }
                : new { chat_id = message.ChatId, text = message.Text, parse_mode = message.ParseMode, disable_web_page_preview = true };

            using var response = await http.PostAsJsonAsync(url, payload, ct);
            if (response.IsSuccessStatusCode) return Outcome.Success;

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = await ReadRetryAfterAsync(response, ct);
                _logger.LogWarning(
                    "Telegram 429 — backing off {Delay}s.", retryAfter.TotalSeconds);
                return Outcome.Transient(retryAfter);
            }

            // Other 4xx — read body for diagnostics and stop.
            if ((int)response.StatusCode is >= 400 and < 500)
            {
                var body = await SafeReadBodyAsync(response, ct);
                _logger.LogError(
                    "Telegram rejected message ({Status}): {Body}",
                    (int)response.StatusCode, body);
                return Outcome.Permanent;
            }

            // 5xx — transient.
            _logger.LogWarning("Telegram 5xx ({Status}).", (int)response.StatusCode);
            return Outcome.Transient(null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Bubble up so the outer loop can exit cleanly on shutdown.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telegram send threw — will retry.");
            return Outcome.Transient(null);
        }
    }

    private TimeSpan ExponentialBackoff(int attempt)
    {
        // 250ms, 500ms, 1s, 2s, ... clamped at 30s.
        var ms = _options.BaseBackoffMilliseconds * (1L << Math.Min(attempt - 1, 10));
        return TimeSpan.FromMilliseconds(Math.Min(ms, 30_000));
    }

    private static async Task<TimeSpan> ReadRetryAfterAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
            return delta;

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (doc.RootElement.TryGetProperty("parameters", out var p) &&
                p.TryGetProperty("retry_after", out var ra) &&
                ra.TryGetInt32(out var seconds))
            {
                return TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 60));
            }
        }
        catch
        {
            // ignored — fall through to default
        }

        return TimeSpan.FromSeconds(5);
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadAsStringAsync(ct); }
        catch { return "<unreadable>"; }
    }

    private enum OutcomeKind { Success, TransientFailure, PermanentFailure }

    private readonly record struct Outcome(OutcomeKind Kind, TimeSpan? RetryAfter)
    {
        public static Outcome Success { get; } = new(OutcomeKind.Success, null);
        public static Outcome Permanent { get; } = new(OutcomeKind.PermanentFailure, null);
        public static Outcome Transient(TimeSpan? retryAfter) => new(OutcomeKind.TransientFailure, retryAfter);
    }
}
