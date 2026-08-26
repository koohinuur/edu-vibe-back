using System.Security.Cryptography;
using LMS.Application.Common.Abstractions;
using LMS.Application.Common.Models;
using LMS.Application.Common.Security;
using LMS.Application.Features.Auth;
using LMS.Domain.Entities;
using LMS.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using UserRole = LMS.Domain.Entities.UserRole;

namespace LMS.Application.Features.Telegram;

/// <summary>
/// Telegram Mini App sign-in. Verifies the signed initData, then resolves it to
/// a platform session:
///   • known Telegram id → sign the linked user in (refreshing the cached
///     Telegram profile);
///   • new Telegram id   → provision a Student account (synthetic email, random
///     password the user never uses — they always re-enter via Telegram) and
///     link it.
/// Either way it returns the exact <see cref="AuthTokensResponse"/> the
/// email/password login emits, so the rest of the stack is auth-source agnostic.
/// </summary>
public sealed class TelegramAuthCommandHandler(
    IApplicationDbContext dbContext,
    ITelegramInitDataValidator validator,
    IPasswordHasher passwordHasher,
    IJwtTokenGenerator jwtTokenGenerator,
    IDateTimeProvider dateTimeProvider,
    ITelegramNotifier notifier)
    : IRequestHandler<TelegramAuthCommand, Result<AuthTokensResponse>>
{
    /// <summary>
    /// DM sent to a Telegram account the moment it gets linked to (or switched onto)
    /// an EduVibe user — a confirmation + a lightweight security notice. Plain text
    /// (the notifier sends DMs without MarkdownV2 escaping).
    /// </summary>
    internal const string LinkNotice =
        "✅ Your Telegram is now linked to EduVibe.\n\n" +
        "You can open the app and sign in straight from Telegram anytime. " +
        "If this wasn't you, please contact your administrator right away.";

    public async Task<Result<AuthTokensResponse>> Handle(TelegramAuthCommand request,
        CancellationToken cancellationToken)
    {
        var (tg, error) = validator.Validate(request.InitData);
        if (tg is null)
            return Result<AuthTokensResponse>.Fail("TELEGRAM_INITDATA_INVALID", error ?? "Invalid Telegram initData.");

        // Deep-link session handoff: the panel minted a one-time token bound to
        // a signed-in user. Exchange it to sign in as THAT user (their real
        // role) and link this Telegram identity to them.
        if (!string.IsNullOrWhiteSpace(request.StartToken))
        {
            var handoff = await HandleHandoffAsync(tg, request.StartToken!, cancellationToken);
            if (handoff is not null) return handoff;
            // null → token wasn't usable; fall through to normal sign-in so a
            // stale link still logs the user in via their existing link (if any).
        }

        // Existing link → sign that user in.
        var account = await dbContext.TelegramAccounts
            .Include(x => x.User)
            .FirstOrDefaultAsync(x => x.TelegramUserId == tg.UserId, cancellationToken);

        // First contact for this Telegram id → we'll DM a link confirmation below.
        var firstTimeLink = account is null;

        User user;
        if (account is not null)
        {
            user = account.User!;
            if (user.Status != UserStatus.Active)
                return Result<AuthTokensResponse>.Fail("USER_INACTIVE", "User is not active.");

            // Keep the cached Telegram snapshot fresh on every sign-in.
            account.UpdateProfile(tg.Username, tg.FirstName, tg.LastName, tg.PhotoUrl);
        }
        else
        {
            // First-time Telegram visitor → provision a Student. The email is
            // synthetic + unique per Telegram id (never collides across users);
            // the password is random because this account only ever authenticates
            // through Telegram, not the email/password form.
            var studentRole = await dbContext.Roles
                .FirstOrDefaultAsync(x => x.Code == RoleCodes.Student, cancellationToken);
            if (studentRole is null)
                return Result<AuthTokensResponse>.Fail("ROLE_NOT_FOUND", "Student role is not configured.");

            var email = $"tg{tg.UserId}@telegram.eduvibe.local";
            var randomPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            user = new User(email, passwordHasher.Hash(randomPassword));
            await dbContext.Users.AddAsync(user, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);

            await dbContext.UserRoles.AddAsync(new UserRole(user.Id, studentRole.Id), cancellationToken);

            var studentProfile = new StudentProfile(user.Id, user);
            studentProfile.UpdateProfile(tg.FirstName, tg.LastName, phoneNumber: null, description: null);
            if (!string.IsNullOrWhiteSpace(tg.PhotoUrl)) studentProfile.SetAvatarUrl(tg.PhotoUrl);
            await dbContext.StudentProfiles.AddAsync(studentProfile, cancellationToken);

            account = new TelegramAccount(user.Id, tg.UserId, tg.Username, tg.FirstName, tg.LastName, tg.PhotoUrl);
            await dbContext.TelegramAccounts.AddAsync(account, cancellationToken);

            // Persist the role/profile/link BEFORE issuing the token — IssueTokens
            // reads roles/permissions back via LINQ (DB), which won't see pending
            // unsaved inserts, so a first-time token would otherwise carry no
            // role/permission claims.
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var tokens = await IssueTokensAsync(user, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        // Notify the freshly-linked Telegram (queued — never blocks the response).
        if (firstTimeLink)
            await notifier.SendToUserAsync(tg.UserId, LinkNotice, cancellationToken);

        return Result<AuthTokensResponse>.Ok(tokens, "Authenticated via Telegram.");
    }

    /// <summary>
    /// Exchanges a one-time deep-link token: consumes it, (re-)points the verified
    /// Telegram identity to the token's user, and issues a session as that user —
    /// so "Open in Telegram" from a panel always signs into THAT account, moving
    /// the Telegram link there if it was elsewhere. Returns null when the token
    /// isn't usable (caller falls back to normal sign-in); a failed Result only if
    /// the token's user is gone/inactive.
    /// </summary>
    private async Task<Result<AuthTokensResponse>?> HandleHandoffAsync(
        TelegramInitData tg, string startToken, CancellationToken ct)
    {
        var link = await dbContext.TelegramDeepLinkTokens
            .FirstOrDefaultAsync(x => x.Token == startToken, ct);
        if (link is null || !link.IsUsable(dateTimeProvider.UtcNow))
            return null;

        // One-time: burn it now, even if a guard below rejects the exchange.
        link.Consume(dateTimeProvider.UtcNow);

        var targetUser = await dbContext.Users.FirstOrDefaultAsync(x => x.Id == link.UserId, ct);
        if (targetUser is null)
        {
            await dbContext.SaveChangesAsync(ct);
            return Result<AuthTokensResponse>.Fail("USER_NOT_FOUND", "The linked account no longer exists.");
        }
        if (targetUser.Status != UserStatus.Active)
        {
            await dbContext.SaveChangesAsync(ct);
            return Result<AuthTokensResponse>.Fail("USER_INACTIVE", "User is not active.");
        }

        var byTg = await dbContext.TelegramAccounts
            .FirstOrDefaultAsync(x => x.TelegramUserId == tg.UserId, ct);

        // Already this user's Telegram → just refresh the cached profile + sign in.
        if (byTg is not null && byTg.UserId == targetUser.Id)
        {
            byTg.UpdateProfile(tg.Username, tg.FirstName, tg.LastName, tg.PhotoUrl);
            var same = await IssueTokensAsync(targetUser, ct);
            await dbContext.SaveChangesAsync(ct);
            return Result<AuthTokensResponse>.Ok(same, "Authenticated via Telegram.");
        }

        // RE-POINT. The handoff is an authenticated claim by targetUser (they minted
        // the token while signed into that panel), so "Open in Telegram" must sign
        // into THAT account — even if this Telegram was linked to someone else, or
        // the user previously linked a different Telegram. Clear the conflicting
        // link(s) and bind fresh. 1 TG = 1 user is preserved: the link MOVES, never
        // duplicates. Delete-then-insert across two saves avoids a unique-index race
        // on TelegramUserId / UserId.
        if (byTg is not null)
        {
            // Account switch: this Telegram was signed in as a DIFFERENT user
            // (e.g. a Student session) and is now being re-pointed to targetUser
            // (e.g. Admin). Revoke the displaced account's refresh token so its
            // session can't be silently resurrected from a stale refresh cookie —
            // no cross-account reuse, no session fixation. (Its 30-min access token
            // is already cleared client-side before this call.)
            if (byTg.UserId != targetUser.Id)
            {
                var displaced = await dbContext.Users.FirstOrDefaultAsync(x => x.Id == byTg.UserId, ct);
                displaced?.ClearRefreshToken();
            }
            dbContext.TelegramAccounts.Remove(byTg);
        }
        var byUser = await dbContext.TelegramAccounts
            .FirstOrDefaultAsync(x => x.UserId == targetUser.Id, ct);
        if (byUser is not null) dbContext.TelegramAccounts.Remove(byUser);
        await dbContext.SaveChangesAsync(ct); // commit consume + deletes before insert

        await dbContext.TelegramAccounts.AddAsync(new TelegramAccount(
            targetUser.Id, tg.UserId, tg.Username, tg.FirstName, tg.LastName, tg.PhotoUrl), ct);

        var tokens = await IssueTokensAsync(targetUser, ct);
        await dbContext.SaveChangesAsync(ct);

        // Confirm the new/switched link to that Telegram (queued — non-blocking).
        await notifier.SendToUserAsync(tg.UserId, LinkNotice, ct);

        return Result<AuthTokensResponse>.Ok(tokens, "Authenticated via Telegram.");
    }

    private async Task<AuthTokensResponse> IssueTokensAsync(User user, CancellationToken cancellationToken)
    {
        var roles = await dbContext.UserRoles.Where(x => x.UserId == user.Id)
            .Join(dbContext.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => r.Code)
            .ToArrayAsync(cancellationToken);
        var permissions = await dbContext.EffectivePermissionCodes(user.Id)
            .ToArrayAsync(cancellationToken);

        var studentProfileId = await dbContext.StudentProfiles
            .Where(x => x.UserId == user.Id).Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        var staffProfileId = await dbContext.StaffProfiles
            .Where(x => x.UserId == user.Id).Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        var accessToken = jwtTokenGenerator.Generate(
            user.Id, user.Email, roles, permissions,
            studentProfileId: studentProfileId,
            staffProfileId: staffProfileId);
        var refreshToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        user.SetRefreshToken(passwordHasher.Hash(refreshToken), dateTimeProvider.UtcNow.Add(jwtTokenGenerator.RefreshTokenLifetime));

        return new AuthTokensResponse(user.Id, user.Email, accessToken, refreshToken, roles, permissions);
    }
}

/// <summary>
/// Connect Telegram to the already-authenticated web user. The Telegram identity
/// comes from the verified initData; the platform user comes from the JWT — never
/// the body — so a caller can only ever link Telegram to <em>themselves</em>.
/// </summary>
public sealed class TelegramLinkCommandHandler(
    IApplicationDbContext dbContext,
    ITelegramInitDataValidator validator,
    ICurrentUserService currentUser,
    ITelegramNotifier notifier)
    : IRequestHandler<TelegramLinkCommand, Result<TelegramProfileDto>>
{
    public async Task<Result<TelegramProfileDto>> Handle(TelegramLinkCommand request,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is null)
            return Result<TelegramProfileDto>.Fail("UNAUTHENTICATED", "Caller must be authenticated.");

        var (tg, error) = validator.Validate(request.InitData);
        if (tg is null)
            return Result<TelegramProfileDto>.Fail("TELEGRAM_INITDATA_INVALID",
                error ?? "Invalid Telegram initData.");

        var userId = currentUser.UserId.Value;

        // Is this Telegram identity already attached somewhere?
        var byTelegram = await dbContext.TelegramAccounts
            .FirstOrDefaultAsync(x => x.TelegramUserId == tg.UserId, cancellationToken);
        if (byTelegram is not null && byTelegram.UserId != userId)
            return Result<TelegramProfileDto>.Fail("TELEGRAM_ALREADY_LINKED",
                "This Telegram account is already linked to another user.");

        // Does this user already have a (different) Telegram link?
        var byUser = await dbContext.TelegramAccounts
            .FirstOrDefaultAsync(x => x.UserId == userId, cancellationToken);

        TelegramAccount account;
        var linkChanged = false;
        if (byUser is not null)
        {
            account = byUser;
            if (account.TelegramUserId != tg.UserId)
            {
                // Switch this user onto a different Telegram (connect from any Telegram).
                account.Relink(tg.UserId, tg.Username, tg.FirstName, tg.LastName, tg.PhotoUrl);
                linkChanged = true;
            }
            else
            {
                account.UpdateProfile(tg.Username, tg.FirstName, tg.LastName, tg.PhotoUrl);
            }
        }
        else
        {
            account = new TelegramAccount(userId, tg.UserId, tg.Username, tg.FirstName, tg.LastName, tg.PhotoUrl);
            await dbContext.TelegramAccounts.AddAsync(account, cancellationToken);
            linkChanged = true;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        // DM the newly-linked/switched Telegram a confirmation (queued — non-blocking).
        if (linkChanged)
            await notifier.SendToUserAsync(tg.UserId, TelegramAuthCommandHandler.LinkNotice, cancellationToken);

        return Result<TelegramProfileDto>.Ok(ToDto(account), "Telegram linked.");
    }

    internal static TelegramProfileDto ToDto(TelegramAccount a) =>
        new(a.TelegramUserId, a.Username, a.FirstName, a.LastName, a.PhotoUrl, a.LinkedAt);
}

public sealed class GetTelegramProfileQueryHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser)
    : IRequestHandler<GetTelegramProfileQuery, Result<TelegramProfileDto?>>
{
    public async Task<Result<TelegramProfileDto?>> Handle(GetTelegramProfileQuery request,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is null)
            return Result<TelegramProfileDto?>.Fail("UNAUTHENTICATED", "Caller must be authenticated.");

        var account = await dbContext.TelegramAccounts
            .FirstOrDefaultAsync(x => x.UserId == currentUser.UserId.Value, cancellationToken);

        return Result<TelegramProfileDto?>.Ok(account is null ? null : TelegramLinkCommandHandler.ToDto(account));
    }
}

public sealed class UnlinkTelegramCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser)
    : IRequestHandler<UnlinkTelegramCommand, Result>
{
    public async Task<Result> Handle(UnlinkTelegramCommand request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is null)
            return Result.Fail("UNAUTHENTICATED", "Caller must be authenticated.");

        var account = await dbContext.TelegramAccounts
            .FirstOrDefaultAsync(x => x.UserId == currentUser.UserId.Value, cancellationToken);
        if (account is null) return Result.Ok("No Telegram link to remove.");

        dbContext.TelegramAccounts.Remove(account);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Result.Ok("Telegram disconnected.");
    }
}

/// <summary>
/// Reads the public bot settings (bot @username + Mini App URL) straight from
/// server config. The bot is fixed by the operator (appsettings / env) — admins
/// cannot change it from the UI — so there is no DB row and no upsert.
/// </summary>
public sealed class TelegramSettingsHandlers(ITelegramConfig config, IDateTimeProvider clock) :
    IRequestHandler<GetTelegramSettingsQuery, Result<TelegramSettingsDto>>
{
    public Task<Result<TelegramSettingsDto>> Handle(GetTelegramSettingsQuery request,
        CancellationToken cancellationToken) =>
        Task.FromResult(Result<TelegramSettingsDto>.Ok(
            new TelegramSettingsDto(config.BotUsername, config.MiniAppUrl, clock.UtcNow)));
}

/// <summary>
/// Mints a one-time deep-link handoff token for the signed-in user and builds
/// the Telegram Mini App deep link (t.me/&lt;bot&gt;?startapp=&lt;token&gt;) the panel opens.
/// </summary>
public sealed class CreateDeepLinkTokenCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser,
    IDateTimeProvider dateTimeProvider,
    ITelegramConfig config)
    : IRequestHandler<CreateDeepLinkTokenCommand, Result<DeepLinkTokenDto>>
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    public async Task<Result<DeepLinkTokenDto>> Handle(CreateDeepLinkTokenCommand request,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is null)
            return Result<DeepLinkTokenDto>.Fail("UNAUTHENTICATED", "Caller must be authenticated.");

        // URL-safe random token — also valid as a Telegram `startapp` parameter.
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var expiresAt = dateTimeProvider.UtcNow.Add(Ttl);

        await dbContext.TelegramDeepLinkTokens.AddAsync(
            new TelegramDeepLinkToken(currentUser.UserId.Value, token, expiresAt), cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        var botUsername = config.BotUsername;
        var deepLink = string.IsNullOrWhiteSpace(botUsername)
            ? string.Empty
            : $"https://t.me/{botUsername}?startapp={token}";

        return Result<DeepLinkTokenDto>.Ok(new DeepLinkTokenDto(token, deepLink, expiresAt));
    }
}
