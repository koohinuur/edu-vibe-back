using LMS.Application.Common.Abstractions;
using LMS.Application.Common.Models;
using LMS.Application.Features.Telegram;
using LMS.Domain.Entities;
using LMS.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LMS.Application.Features.Announcements;

public sealed class AnnouncementsHandlers(IApplicationDbContext db, ISender mediator) :
    IRequestHandler<GetAnnouncementsQuery, Result<IReadOnlyCollection<AnnouncementDto>>>,
    IRequestHandler<GetPublicAnnouncementsQuery, Result<IReadOnlyCollection<AnnouncementDto>>>,
    IRequestHandler<CreateAnnouncementCommand, Result<AnnouncementDto>>,
    IRequestHandler<UpdateAnnouncementCommand, Result<AnnouncementDto>>,
    IRequestHandler<DeleteAnnouncementCommand, Result>
{
    public async Task<Result<IReadOnlyCollection<AnnouncementDto>>> Handle(
        GetAnnouncementsQuery request, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var q = db.Announcements.AsNoTracking();
        if (request.OnlyLive)
        {
            q = q.Where(a => (a.PublishesAt == null || a.PublishesAt <= now)
                          && (a.ExpiresAt == null || a.ExpiresAt >= now));
        }
        if (request.AudienceFilter is { } aud)
        {
            // Everyone always wins; the role-specific filters only allow rows
            // tagged for that exact role.
            q = q.Where(a => a.Audience == AnnouncementAudience.Everyone || a.Audience == aud);
        }
        var items = await q
            .OrderByDescending(a => a.CreatedAt)
            .Select(Project())
            .ToListAsync(ct);
        return Result<IReadOnlyCollection<AnnouncementDto>>.Ok(items);
    }

    public async Task<Result<IReadOnlyCollection<AnnouncementDto>>> Handle(
        GetPublicAnnouncementsQuery request, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var take = Math.Clamp(request.Take, 1, 50);
        var items = await db.Announcements.AsNoTracking()
            .Where(a => a.IsPublic
                     && (a.PublishesAt == null || a.PublishesAt <= now)
                     && (a.ExpiresAt == null || a.ExpiresAt >= now))
            .OrderByDescending(a => a.CreatedAt)
            .Take(take)
            .Select(Project())
            .ToListAsync(ct);
        return Result<IReadOnlyCollection<AnnouncementDto>>.Ok(items);
    }

    public async Task<Result<AnnouncementDto>> Handle(CreateAnnouncementCommand request, CancellationToken ct)
    {
        var entity = new Announcement(
            request.Title, request.Body, request.IsPublic, request.Audience,
            request.PublishesAt, request.ExpiresAt, request.AuthorUserId);
        await db.Announcements.AddAsync(entity, ct);
        await db.SaveChangesAsync(ct);

        // Push "Everyone" news that is live now out to every Telegram bot
        // subscriber. Same body for all languages — it's the admin's own text.
        var liveNow = entity.PublishesAt is null || entity.PublishesAt <= DateTime.UtcNow;
        if (entity.Audience == AnnouncementAudience.Everyone && liveNow)
        {
            var text = $"📢 {request.Title}\n\n{request.Body}";
            await mediator.Send(new BroadcastTelegramCommand(text, text, text), ct);
        }

        return Result<AnnouncementDto>.Ok(Map(entity));
    }

    public async Task<Result<AnnouncementDto>> Handle(UpdateAnnouncementCommand request, CancellationToken ct)
    {
        var entity = await db.Announcements.FirstOrDefaultAsync(a => a.Id == request.AnnouncementId, ct);
        if (entity is null) return Result<AnnouncementDto>.Fail("NOT_FOUND", "Announcement not found.");
        entity.Update(request.Title, request.Body, request.IsPublic, request.Audience,
            request.PublishesAt, request.ExpiresAt);
        await db.SaveChangesAsync(ct);
        return Result<AnnouncementDto>.Ok(Map(entity));
    }

    public async Task<Result> Handle(DeleteAnnouncementCommand request, CancellationToken ct)
    {
        var entity = await db.Announcements.FirstOrDefaultAsync(a => a.Id == request.AnnouncementId, ct);
        if (entity is null) return Result.Fail("NOT_FOUND", "Announcement not found.");
        db.Announcements.Remove(entity);
        await db.SaveChangesAsync(ct);
        return Result.Ok("Deleted");
    }

    private static System.Linq.Expressions.Expression<Func<Announcement, AnnouncementDto>> Project()
        => a => new AnnouncementDto(
            a.Id, a.Title, a.Body, a.IsPublic, a.Audience,
            a.PublishesAt, a.ExpiresAt, a.AuthorUserId, a.CreatedAt);

    private static AnnouncementDto Map(Announcement a) => new(
        a.Id, a.Title, a.Body, a.IsPublic, a.Audience,
        a.PublishesAt, a.ExpiresAt, a.AuthorUserId, a.CreatedAt);
}
