using LMS.Application.Common.Abstractions;
using LMS.Application.Common.Models;
using LMS.Application.Features.Telegram;
using LMS.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LMS.Application.Features.MarketingCms;

public sealed class MockTestSlotsHandlers(IApplicationDbContext db, ISender mediator) :
    IRequestHandler<GetMockTestSlotsQuery, Result<IReadOnlyCollection<MockTestSlotDto>>>,
    IRequestHandler<GetPublicMockTestSlotsQuery, Result<IReadOnlyCollection<MockTestSlotDto>>>,
    IRequestHandler<CreateMockTestSlotCommand, Result<MockTestSlotDto>>,
    IRequestHandler<UpdateMockTestSlotCommand, Result<MockTestSlotDto>>,
    IRequestHandler<DeleteMockTestSlotCommand, Result>
{
    public async Task<Result<IReadOnlyCollection<MockTestSlotDto>>> Handle(
        GetMockTestSlotsQuery request, CancellationToken ct)
    {
        var q = db.MockTestSlots.AsNoTracking();
        if (request.OnlyActive) q = q.Where(s => s.IsActive);
        var items = await q
            .OrderBy(s => s.StartsAt).ThenBy(s => s.SortOrder)
            .Select(Project()).ToListAsync(ct);
        return Result<IReadOnlyCollection<MockTestSlotDto>>.Ok(items);
    }

    public async Task<Result<IReadOnlyCollection<MockTestSlotDto>>> Handle(
        GetPublicMockTestSlotsQuery request, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var items = await db.MockTestSlots.AsNoTracking()
            .Where(s => s.IsActive && s.StartsAt >= now)
            .OrderBy(s => s.StartsAt).ThenBy(s => s.SortOrder)
            .Select(Project()).ToListAsync(ct);
        return Result<IReadOnlyCollection<MockTestSlotDto>>.Ok(items);
    }

    public async Task<Result<MockTestSlotDto>> Handle(CreateMockTestSlotCommand request, CancellationToken ct)
    {
        var s = new MockTestSlot(
            request.Title, ToUtc(request.StartsAt), request.DurationText,
            request.Capacity, request.AvailableSeats, request.SortOrder, request.IsActive);
        await db.MockTestSlots.AddAsync(s, ct);
        await db.SaveChangesAsync(ct);

        // Announce a newly-opened, active mock test to every Telegram subscriber.
        if (s.IsActive)
        {
            var when = s.StartsAt.AddHours(5).ToString("yyyy-MM-dd HH:mm"); // Tashkent (UTC+5, no DST)
            const string site = "https://edu-vibe.uz";
            await mediator.Send(new BroadcastTelegramCommand(
                TelegramBotTexts.NewMockTest("uz", s.Title, when, site),
                TelegramBotTexts.NewMockTest("ru", s.Title, when, site),
                TelegramBotTexts.NewMockTest("en", s.Title, when, site)), ct);
        }

        return Result<MockTestSlotDto>.Ok(Map(s));
    }

    public async Task<Result<MockTestSlotDto>> Handle(UpdateMockTestSlotCommand request, CancellationToken ct)
    {
        var s = await db.MockTestSlots.FirstOrDefaultAsync(x => x.Id == request.SlotId, ct);
        if (s is null) return Result<MockTestSlotDto>.Fail("NOT_FOUND", "Mock test slot not found.");
        s.Update(request.Title, ToUtc(request.StartsAt), request.DurationText,
            request.Capacity, request.AvailableSeats, request.SortOrder, request.IsActive);
        await db.SaveChangesAsync(ct);
        return Result<MockTestSlotDto>.Ok(Map(s));
    }

    public async Task<Result> Handle(DeleteMockTestSlotCommand request, CancellationToken ct)
    {
        var s = await db.MockTestSlots.FirstOrDefaultAsync(x => x.Id == request.SlotId, ct);
        if (s is null) return Result.Fail("NOT_FOUND", "Mock test slot not found.");
        db.MockTestSlots.Remove(s);
        await db.SaveChangesAsync(ct);
        return Result.Ok("Deleted");
    }

    // Npgsql 'timestamp with time zone' requires UTC. Normalize whatever the
    // client sent (Z → Utc, offset → Local, bare → assume Utc).
    private static DateTime ToUtc(DateTime d) => d.Kind switch
    {
        DateTimeKind.Utc => d,
        DateTimeKind.Local => d.ToUniversalTime(),
        _ => DateTime.SpecifyKind(d, DateTimeKind.Utc),
    };

    private static System.Linq.Expressions.Expression<Func<MockTestSlot, MockTestSlotDto>> Project()
        => s => new MockTestSlotDto(
            s.Id, s.Title, s.StartsAt, s.DurationText, s.Capacity, s.AvailableSeats, s.SortOrder, s.IsActive);

    private static MockTestSlotDto Map(MockTestSlot s) => new(
        s.Id, s.Title, s.StartsAt, s.DurationText, s.Capacity, s.AvailableSeats, s.SortOrder, s.IsActive);
}
