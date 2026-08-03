using LMS.Application.Common.Abstractions;
using LMS.Application.Common.Models;
using LMS.Domain.Entities;
using LMS.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LMS.Application.Features.Results;

public sealed class ResultsHandlers(
    IApplicationDbContext db,
    IResultImageService imageService)
    : IRequestHandler<ResultListQuery, Result<IReadOnlyCollection<ResultDto>>>,
      IRequestHandler<ResultByIdQuery, Result<ResultDto>>,
      IRequestHandler<FeaturedResultsQuery, Result<IReadOnlyCollection<ResultDto>>>,
      IRequestHandler<CreateResultCommand, Result<ResultDto>>,
      IRequestHandler<UpdateResultCommand, Result<ResultDto>>,
      IRequestHandler<DeleteResultCommand, Result>,
      IRequestHandler<UploadResultImageCommand, Result<ResultImageDto>>,
      IRequestHandler<ReplaceResultImageCommand, Result<ResultImageDto>>,
      IRequestHandler<DeleteResultImageCommand, Result>,
      IRequestHandler<ResultsAdminStatsQuery, Result<ResultsAdminStatsDto>>,
      IRequestHandler<AdminResultListQuery, Result<PagedResult<ResultDto>>>
{
    /// <summary>
    /// Single-row mapper. Use only for the byId endpoint where you have exactly
    /// one ResultEntry — two extra queries is acceptable. For lists use
    /// <see cref="MapManyAsync"/> which batches the child loads.
    /// </summary>
    private async Task<ResultDto> Map(ResultEntry r, CancellationToken ct)
    {
        var b = await db.ResultScoreBreakdowns.AsNoTracking()
            .Where(x => x.ResultId == r.Id && !x.IsDeleted)
            .Select(x => new ScoreBreakdownItemDto(x.Key, x.Value)).ToListAsync(ct);
        var imgs = await db.ResultImages.AsNoTracking()
            .Where(x => x.ResultId == r.Id && !x.IsDeleted)
            .Select(x => new ResultImageDto(x.Id, x.ImageUrl, x.ThumbnailUrl, x.IsMain)).ToListAsync(ct);
        return new ResultDto(r.Id, r.StudentFullName, r.MainImageUrl, r.ExamType, r.OverallScore, r.Description,
            r.ImprovementText, r.DurationText, r.Notes, r.BadgeIcon, r.DisplayOrder, r.IsFeatured, r.IsPublished,
            r.Language, r.CreatedAt, r.ViewsCount, b, imgs);
    }

    /// <summary>
    /// Batched mapper. Pulls every breakdown + image for the given results in
    /// TWO queries total (regardless of list size), groups in memory, then
    /// assembles the DTOs. Use this on every list / featured / stats path —
    /// the previous shape fired 2 extra queries per row.
    /// </summary>
    private async Task<List<ResultDto>> MapManyAsync(IReadOnlyList<ResultEntry> results, CancellationToken ct)
    {
        if (results.Count == 0) return new List<ResultDto>();

        var ids = results.Select(r => r.Id).ToList();

        var breakdownsLookup = await db.ResultScoreBreakdowns.AsNoTracking()
            .Where(x => ids.Contains(x.ResultId) && !x.IsDeleted)
            .Select(x => new { x.ResultId, Item = new ScoreBreakdownItemDto(x.Key, x.Value) })
            .ToListAsync(ct);
        var breakdowns = breakdownsLookup
            .GroupBy(x => x.ResultId)
            .ToDictionary(g => g.Key, g => (IReadOnlyCollection<ScoreBreakdownItemDto>)g.Select(x => x.Item).ToList());

        var imagesLookup = await db.ResultImages.AsNoTracking()
            .Where(x => ids.Contains(x.ResultId) && !x.IsDeleted)
            .Select(x => new { x.ResultId, Item = new ResultImageDto(x.Id, x.ImageUrl, x.ThumbnailUrl, x.IsMain) })
            .ToListAsync(ct);
        var images = imagesLookup
            .GroupBy(x => x.ResultId)
            .ToDictionary(g => g.Key, g => (IReadOnlyCollection<ResultImageDto>)g.Select(x => x.Item).ToList());

        var dtos = new List<ResultDto>(results.Count);
        var emptyB = (IReadOnlyCollection<ScoreBreakdownItemDto>)Array.Empty<ScoreBreakdownItemDto>();
        var emptyI = (IReadOnlyCollection<ResultImageDto>)Array.Empty<ResultImageDto>();
        foreach (var r in results)
        {
            dtos.Add(new ResultDto(r.Id, r.StudentFullName, r.MainImageUrl, r.ExamType, r.OverallScore,
                r.Description, r.ImprovementText, r.DurationText, r.Notes, r.BadgeIcon, r.DisplayOrder,
                r.IsFeatured, r.IsPublished, r.Language, r.CreatedAt, r.ViewsCount,
                breakdowns.TryGetValue(r.Id, out var b) ? b : emptyB,
                images.TryGetValue(r.Id, out var i) ? i : emptyI));
        }
        return dtos;
    }

    public async Task<Result<IReadOnlyCollection<ResultDto>>> Handle(ResultListQuery q, CancellationToken ct)
    {
        var query = db.Results.Where(x => !x.IsDeleted && x.IsPublished);
        if (!string.IsNullOrWhiteSpace(q.Search)) query = query.Where(x => x.StudentFullName.Contains(q.Search));
        if (q.ExamType.HasValue) query = query.Where(x => x.ExamType == q.ExamType.Value);
        if (q.Featured.HasValue) query = query.Where(x => x.IsFeatured == q.Featured.Value);

        query = q.SortBy?.ToLowerInvariant() switch
        {
            "highest" => query.OrderByDescending(x => x.OverallScore),
            "latest" => query.OrderByDescending(x => x.CreatedAt),
            _ => query.OrderBy(x => x.DisplayOrder).ThenByDescending(x => x.CreatedAt)
        };

        // Clamp anonymous, client-supplied paging so a hostile ?pageSize=1000000
        // (or a negative page) can't force a huge scan + image fan-out.
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, 100);
        var list = await query.AsNoTracking()
            .Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return Result<IReadOnlyCollection<ResultDto>>.Ok(await MapManyAsync(list, ct));
    }

    public async Task<Result<ResultDto>> Handle(ResultByIdQuery q, CancellationToken ct)
    {
        var r = await db.Results.FirstOrDefaultAsync(x => x.Id == q.Id && !x.IsDeleted && x.IsPublished, ct);
        if (r is null) return Result<ResultDto>.Fail("NOT_FOUND", "Result not found.");

        if (q.TrackView)
        {
            r.IncrementViews();
            await db.ResultViews.AddAsync(new ResultView(r.Id, null, null), ct);
            await db.SaveChangesAsync(ct);
        }

        return Result<ResultDto>.Ok(await Map(r, ct));
    }

    public async Task<Result<IReadOnlyCollection<ResultDto>>> Handle(FeaturedResultsQuery q, CancellationToken ct)
    {
        var limit = Math.Clamp(q.Limit, 1, 50);
        var list = await db.Results.AsNoTracking()
            .Where(x => !x.IsDeleted && x.IsPublished && x.IsFeatured)
            .OrderBy(x => x.DisplayOrder).ThenByDescending(x => x.CreatedAt).Take(limit).ToListAsync(ct);
        return Result<IReadOnlyCollection<ResultDto>>.Ok(await MapManyAsync(list, ct));
    }

    public async Task<Result<ResultDto>> Handle(CreateResultCommand c, CancellationToken ct)
    {
        var r = new ResultEntry(c.StudentFullName, c.ExamType, c.OverallScore, c.Language);
        r.Update(c.StudentFullName, c.ExamType, c.OverallScore, c.Language, c.Description, c.ImprovementText,
            c.DurationText, c.Notes, c.BadgeIcon, c.DisplayOrder, c.IsFeatured, c.IsPublished);
        await db.Results.AddAsync(r, ct);
        await db.SaveChangesAsync(ct);

        foreach (var item in c.ScoreBreakdown)
        {
            await db.ResultScoreBreakdowns.AddAsync(new ResultScoreBreakdown(r.Id, item.Key, item.Value), ct);
        }

        await db.SaveChangesAsync(ct);
        return Result<ResultDto>.Ok(await Map(r, ct));
    }

    public async Task<Result<ResultDto>> Handle(UpdateResultCommand c, CancellationToken ct)
    {
        // The whole update runs inside one retriable transaction. The wrapper clears
        // the change tracker at the start of every attempt, so the entity load and the
        // r.Update(...) mutation must live INSIDE the lambda — otherwise a retry would
        // discard them. The two-step "delete + save, then insert + save" is deliberate:
        // score breakdowns are hard-deleted and flushed BEFORE the new rows are inserted,
        // so the reused (ResultId, Key) values don't collide with the unfiltered unique
        // index (soft-deleting the old rows would leave them occupying those index slots
        // and Postgres would throw a duplicate-key error). Both saves share the same
        // physical transaction, so a mid-way failure rolls the entire update back.
        return await db.ExecuteInTransactionAsync<ResultDto>(async () =>
        {
            var r = await db.Results.FirstOrDefaultAsync(x => x.Id == c.Id && !x.IsDeleted, ct);
            if (r is null) return Result<ResultDto>.Fail("NOT_FOUND", "Result not found.");

            r.Update(c.StudentFullName, c.ExamType, c.OverallScore, c.Language, c.Description, c.ImprovementText,
                c.DurationText, c.Notes, c.BadgeIcon, c.DisplayOrder, c.IsFeatured, c.IsPublished);

            var existing = await db.ResultScoreBreakdowns.Where(x => x.ResultId == r.Id).ToListAsync(ct);
            db.ResultScoreBreakdowns.RemoveRange(existing);
            await db.SaveChangesAsync(ct);

            foreach (var item in c.ScoreBreakdown)
            {
                await db.ResultScoreBreakdowns.AddAsync(new ResultScoreBreakdown(r.Id, item.Key, item.Value), ct);
            }
            await db.SaveChangesAsync(ct);

            return Result<ResultDto>.Ok(await Map(r, ct));
        }, ct);
    }

    public async Task<Result> Handle(DeleteResultCommand c, CancellationToken ct)
    {
        var r = await db.Results.FirstOrDefaultAsync(x => x.Id == c.Id && !x.IsDeleted, ct);
        if (r is null) return Result.Fail("NOT_FOUND", "Result not found.");
        r.SoftDelete();
        await db.SaveChangesAsync(ct);
        return Result.Ok("Result deleted.");
    }

    public async Task<Result<ResultImageDto>> Handle(UploadResultImageCommand c, CancellationToken ct)
    {
        var r = await db.Results.FirstOrDefaultAsync(x => x.Id == c.ResultId && !x.IsDeleted, ct);
        if (r is null) return Result<ResultImageDto>.Fail("NOT_FOUND", "Result not found.");

        var (imageUrl, thumbUrl) = await imageService.SaveAsync(c.FileStream, c.FileName, ct);
        var img = new ResultImage(r.Id, imageUrl, thumbUrl, c.IsMain);
        await db.ResultImages.AddAsync(img, ct);
        if (c.IsMain) r.SetMainImage(imageUrl);
        await db.SaveChangesAsync(ct);
        return Result<ResultImageDto>.Ok(new ResultImageDto(img.Id, img.ImageUrl, img.ThumbnailUrl, img.IsMain));
    }

    public async Task<Result<ResultImageDto>> Handle(ReplaceResultImageCommand c, CancellationToken ct)
    {
        var img = await db.ResultImages.FirstOrDefaultAsync(x => x.Id == c.ImageId && x.ResultId == c.ResultId && !x.IsDeleted, ct);
        if (img is null) return Result<ResultImageDto>.Fail("NOT_FOUND", "Image not found.");

        await imageService.DeleteAsync(img.ImageUrl, img.ThumbnailUrl, ct);
        var (imageUrl, thumbUrl) = await imageService.SaveAsync(c.FileStream, c.FileName, ct);
        img.Replace(imageUrl, thumbUrl);

        var result = await db.Results.FirstOrDefaultAsync(x => x.Id == c.ResultId, ct);
        if (result is not null && img.IsMain) result.SetMainImage(imageUrl);

        await db.SaveChangesAsync(ct);
        return Result<ResultImageDto>.Ok(new ResultImageDto(img.Id, img.ImageUrl, img.ThumbnailUrl, img.IsMain));
    }

    public async Task<Result> Handle(DeleteResultImageCommand c, CancellationToken ct)
    {
        var img = await db.ResultImages.FirstOrDefaultAsync(x => x.Id == c.ImageId && x.ResultId == c.ResultId && !x.IsDeleted, ct);
        if (img is null) return Result.Fail("NOT_FOUND", "Image not found.");
        img.SoftDelete();
        await imageService.DeleteAsync(img.ImageUrl, img.ThumbnailUrl, ct);
        await db.SaveChangesAsync(ct);
        return Result.Ok("Image deleted.");
    }

    public async Task<Result<PagedResult<ResultDto>>> Handle(AdminResultListQuery q, CancellationToken ct)
    {
        var req = new PageRequest(q.Page, q.PageSize, q.Search);
        var query = db.Results.Where(x => !x.IsDeleted);
        if (req.NormalizedSearch is { } s) query = query.Where(x => x.StudentFullName.ToLower().Contains(s));
        if (q.ExamType.HasValue) query = query.Where(x => x.ExamType == q.ExamType.Value);
        if (q.Published.HasValue) query = query.Where(x => x.IsPublished == q.Published.Value);
        if (q.Featured.HasValue) query = query.Where(x => x.IsFeatured == q.Featured.Value);

        var total = await query.CountAsync(ct);
        var list = await query.AsNoTracking()
            .OrderBy(x => x.DisplayOrder).ThenByDescending(x => x.CreatedAt)
            .Skip(req.Skip).Take(req.NormalizedPageSize).ToListAsync(ct);
        var items = await MapManyAsync(list, ct);
        return Result<PagedResult<ResultDto>>.Ok(PagedResult<ResultDto>.From(items, total, req));
    }

    public async Task<Result<ResultsAdminStatsDto>> Handle(ResultsAdminStatsQuery q, CancellationToken ct)
    {
        var total = await db.Results.CountAsync(x => !x.IsDeleted, ct);
        var ielts = await db.Results.CountAsync(x => !x.IsDeleted && x.ExamType == ExamType.IELTS, ct);
        var sat = await db.Results.CountAsync(x => !x.IsDeleted && x.ExamType == ExamType.SAT, ct);
        var featured = await db.Results.CountAsync(x => !x.IsDeleted && x.IsFeatured, ct);
        var mostViewed = await db.Results.Where(x => !x.IsDeleted).OrderByDescending(x => x.ViewsCount)
            .Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
        var recent = await db.Results.AsNoTracking()
            .Where(x => !x.IsDeleted).OrderByDescending(x => x.CreatedAt).Take(5).ToListAsync(ct);
        var recentDtos = await MapManyAsync(recent, ct);

        return Result<ResultsAdminStatsDto>.Ok(new ResultsAdminStatsDto(total, ielts, sat, featured, mostViewed, recentDtos));
    }
}

