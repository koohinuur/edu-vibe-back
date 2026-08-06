using LMS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LMS.WebApi.Security;

/// <summary>
/// Applies pending EF migrations on boot, gated by the
/// <c>Database:AutoMigrateOnStartup</c> config flag. Defaults to <c>true</c>
/// in Development (so every developer who pulls a branch with new migrations
/// gets them automatically — no more "42703: column does not exist" surprises)
/// and to <c>false</c> in Production (where migrations should be applied via
/// a release pipeline step, not a process restart).
///
/// Runs <b>first</b> in the hosted-service chain so the seeders that follow
/// see the up-to-date schema.
/// </summary>
public sealed class DatabaseInitializerHostedService(
    IServiceProvider serviceProvider,
    IConfiguration configuration,
    IHostEnvironment env,
    ILogger<DatabaseInitializerHostedService> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var defaultEnabled = env.IsDevelopment();
        var enabled = configuration.GetValue("Database:AutoMigrateOnStartup", defaultEnabled);

        if (!enabled)
        {
            logger.LogDebug(
                "Auto-migrate disabled (Database:AutoMigrateOnStartup=false). " +
                "Run `dotnet ef database update` from your release pipeline.");
            return;
        }

        using var scope = serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LMSDbContext>();

        // Wait for the database to actually accept connections before doing
        // anything. Right after a deploy the `db` container may still be warming
        // up (or was just recreated), so the first connection can transiently fail
        // — connection refused, "the database system is starting up", or even a
        // brief "password authentication failed" while Postgres finishes loading
        // its roles. Npgsql's EnableRetryOnFailure does NOT retry auth errors, so
        // without this the hosted service throws, the host shuts down, and the
        // container crash-loops (restart: unless-stopped) — which is exactly what
        // makes the first sign-ins after every deploy fail until it stabilises.
        await WaitForDatabaseAsync(db, cancellationToken);

        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count == 0)
        {
            logger.LogDebug("Database schema is up to date — no pending migrations.");
            return;
        }

        logger.LogInformation(
            "Applying {Count} pending migration(s): {Migrations}",
            pending.Count, string.Join(", ", pending));

        try
        {
            await db.Database.MigrateAsync(cancellationToken);
            logger.LogInformation("Database migrations applied successfully.");
        }
        catch (Exception ex)
        {
            // We log loudly but rethrow so the host shuts down — running with a
            // half-applied schema is the surest way to corrupt data.
            logger.LogCritical(ex,
                "Failed to apply pending migrations. Process will not start. " +
                "Run `dotnet ef database update -p LMS.Infrastructure -s LMS.WebApi` " +
                "manually and inspect the error.");
            throw;
        }
    }

    /// <summary>
    /// Polls the database until it accepts a connection, with a bounded backoff
    /// (~up to 60s). Tolerates every transient boot-time error so a slow / just-
    /// recreated Postgres doesn't crash the API. Only gives up after the budget is
    /// exhausted, letting the migrate call below surface a genuine, persistent
    /// failure (e.g. a real credential mismatch).
    /// </summary>
    private async Task WaitForDatabaseAsync(LMSDbContext db, CancellationToken cancellationToken)
    {
        const int maxAttempts = 20;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                if (await db.Database.CanConnectAsync(cancellationToken))
                {
                    if (attempt > 1)
                        logger.LogInformation("Database reachable after {Attempts} attempt(s).", attempt);
                    return;
                }
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                logger.LogWarning(
                    "Database not ready yet (attempt {Attempt}/{Max}): {Message}. Retrying…",
                    attempt, maxAttempts, ex.Message);
            }

            if (attempt < maxAttempts)
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(3, attempt)), cancellationToken);
        }

        logger.LogWarning(
            "Database still not confirmed reachable after {Max} attempts — proceeding; " +
            "the migration step will surface any persistent failure.", maxAttempts);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
