using LMS.Application.Common.Abstractions;
using LMS.Infrastructure.Persistence;
using LMS.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace LMS.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connection = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:DefaultConnection is required.");

        // DbContext pooling halves per-request CPU on the change tracker /
        // model snapshot / query cache. EnableRetryOnFailure absorbs transient
        // Postgres blips (connection reset, brief network partition) without
        // bubbling up as 500s.
        services.AddDbContextPool<LMSDbContext>(opt =>
        {
            opt.UseNpgsql(connection, npg =>
            {
                npg.EnableRetryOnFailure(
                    maxRetryCount: 3,
                    maxRetryDelay: TimeSpan.FromSeconds(2),
                    errorCodesToAdd: null);
                npg.MigrationsAssembly(typeof(LMSDbContext).Assembly.FullName);
            });
            // Warnings as errors for footguns we never want to silently ship.
            opt.ConfigureWarnings(w => w.Throw(
                RelationalEventId.MultipleCollectionIncludeWarning,
                CoreEventId.RowLimitingOperationWithoutOrderByWarning));
        });
        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<LMSDbContext>());
        services.AddScoped<IPasswordHasher, PasswordHasher>();
        // No per-request state — singleton avoids per-request key allocation.
        services.AddSingleton<IJwtTokenGenerator, JwtTokenGenerator>();
        services.AddScoped<ICurrentUserService, CurrentUserService>();
        services.AddScoped<IResultImageService, ResultImageService>();
        services.AddSingleton<IDateTimeProvider, DateTimeProvider>();
        services.AddHttpContextAccessor();

        // Telegram pipeline: producer/consumer split.
        //   • TelegramNotifier (singleton) enqueues into a bounded Channel — non-blocking.
        //   • TelegramSenderHostedService drains the channel on one worker with retry +
        //     backoff and honors Telegram's 429 retry_after.
        //   • Notifier is registered both as the interface (for callers) and the
        //     concrete type (so the hosted service can reach the channel reader
        //     without duplicating the singleton).
        services.Configure<TelegramOptions>(configuration.GetSection(TelegramOptions.SectionName));
        services.AddHttpClient("Telegram", (sp, c) =>
        {
            var opts = sp.GetRequiredService<IOptions<TelegramOptions>>().Value;
            c.Timeout = TimeSpan.FromSeconds(Math.Max(1, opts.RequestTimeoutSeconds));
        });
        services.AddSingleton<TelegramNotifier>();
        services.AddSingleton<ITelegramNotifier>(sp => sp.GetRequiredService<TelegramNotifier>());
        services.AddHostedService<TelegramSenderHostedService>();
        // Read-only bot config (username + Mini App URL) for the Application layer,
        // sourced from TelegramOptions — the bot is fixed by the operator, not the DB.
        services.AddSingleton<ITelegramConfig, TelegramConfig>();
        // On boot, point the bot's default menu button at the production Mini App
        // ({MiniAppUrl}/tg) via setChatMenuButton — no @BotFather step needed.
        services.AddHostedService<TelegramMenuButtonHostedService>();
        // On boot, register the bot's webhook (setWebhook) so /start gets a live
        // welcome. No-op unless Telegram:WebhookUrl + WebhookSecret are configured.
        services.AddHostedService<TelegramWebhookRegistrationHostedService>();
        services.AddSingleton<ITelegramInitDataValidator, TelegramInitDataValidator>();
        // Universal per-user notifier: resolves a user → their linked Telegram
        // and DMs via the platform bot. Scoped (depends on the scoped DbContext).
        services.AddScoped<INotificationService, NotificationService>();

        services.AddSingleton<ITaskGrader, TaskGrader>();
        services.AddSingleton<LMS.Application.Common.Salary.ISalaryCalculator, LMS.Application.Common.Salary.SalaryCalculator>();
        // Stateless .xlsx read/write (bulk student import + result workbooks).
        services.AddSingleton<IExcelService, ClosedXmlExcelService>();
        services.AddSingleton<IAvatarFileStore, LocalAvatarFileStore>();
        services.AddSingleton<IMaterialFileStore, LocalMaterialFileStore>();
        services.AddSingleton<ISubmissionFileStore, LocalSubmissionFileStore>();
        services.AddSingleton<ILessonMaterialFileStore, LocalLessonMaterialFileStore>();
        services.AddSingleton<IExerciseAudioStore, LocalExerciseAudioStore>();
        services.AddSingleton<IExerciseImageStore, LocalExerciseImageStore>();
        services.AddSingleton<IExerciseFileStore, LocalExerciseFileStore>();
        return services;
    }

    public static IServiceCollection AddJwtAuthentication(this IServiceCollection services,
        IConfiguration configuration)
    {
        // Bind the "Jwt" section once and register it for IOptions consumers
        // (JwtTokenGenerator + the auth handlers) — no more scattered
        // configuration["Jwt:*"] indexer reads.
        var jwtSection = configuration.GetSection(JwtOptions.SectionName);
        services.Configure<JwtOptions>(jwtSection);

        var jwt = jwtSection.Get<JwtOptions>() ?? new JwtOptions();
        // Single presence + ≥32-byte validation (shared with JwtTokenGenerator).
        var keyBytes = jwt.SigningKeyBytes();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = jwt.Issuer,
                    ValidAudience = jwt.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(keyBytes),
                    // Defaults to 5 min — too generous for short-lived tokens.
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
            });
        return services;
    }
}
