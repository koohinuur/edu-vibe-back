using LMS.Application.Common.Models;
using LMS.Application.Features.Results;
using LMS.WebApi.Common;
using LMS.WebApi.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LMS.WebApi.Controllers;

[ApiController]
[Route("api")]
public sealed class ResultsController(ISender sender) : ControllerBase
{
    [HttpGet("results")]
    [AllowAnonymous]
    [Microsoft.AspNetCore.OutputCaching.OutputCache(PolicyName = LMS.WebApi.Common.PublicReadCacheHeaderPolicy.Name)]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<ResultDto>>>> GetAll(
        [FromQuery] string? search,
        [FromQuery] LMS.Domain.Enums.ExamType? examType,
        [FromQuery] bool? featured,
        [FromQuery] string? sortBy,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 12,
        CancellationToken cancellationToken = default)
    {
        var r = await sender.Send(new ResultListQuery(search, examType, featured, sortBy, page, pageSize), cancellationToken);
        return Ok(ApiResponse<IReadOnlyCollection<ResultDto>>.Ok(r.Data, r.Message));
    }

    [HttpGet("results/{id:guid}")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<ResultDto>>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var r = await sender.Send(new ResultByIdQuery(id, true), cancellationToken);
        return r.ToApiResultOrNotFound();
    }

    [HttpGet("results/featured")]
    [AllowAnonymous]
    [Microsoft.AspNetCore.OutputCaching.OutputCache(PolicyName = LMS.WebApi.Common.PublicReadCacheHeaderPolicy.Name)]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<ResultDto>>>> Featured([FromQuery] int limit = 6,
        CancellationToken cancellationToken = default)
    {
        var r = await sender.Send(new FeaturedResultsQuery(limit), cancellationToken);
        return Ok(ApiResponse<IReadOnlyCollection<ResultDto>>.Ok(r.Data, r.Message));
    }

    [HttpGet("admin/results/stats")]
    [PermissionAuthorize("Results.Read")]
    public async Task<ActionResult<ApiResponse<ResultsAdminStatsDto>>> Stats(CancellationToken cancellationToken)
    {
        var r = await sender.Send(new ResultsAdminStatsQuery(), cancellationToken);
        return Ok(ApiResponse<ResultsAdminStatsDto>.Ok(r.Data, r.Message));
    }

    // Admin management list — includes unpublished drafts (the public GET /results is
    // published-only), paginated, so the CMS can list + edit + publish every result.
    [HttpGet("admin/results")]
    [PermissionAuthorize("Results.Read")]
    public async Task<ActionResult<ApiResponse<PagedResult<ResultDto>>>> AdminList(
        [FromQuery] string? search,
        [FromQuery] LMS.Domain.Enums.ExamType? examType,
        [FromQuery] bool? published,
        [FromQuery] bool? featured,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        var r = await sender.Send(new AdminResultListQuery(search, examType, published, featured, page, pageSize), cancellationToken);
        return Ok(ApiResponse<PagedResult<ResultDto>>.Ok(r.Data, r.Message));
    }

    [HttpPost("admin/results")]
    [PermissionAuthorize("Results.Create")]
    public async Task<ActionResult<ApiResponse<ResultDto>>> Create([FromBody] CreateResultCommand command, CancellationToken cancellationToken)
    {
        var r = await sender.Send(command, cancellationToken);
        return r.Success ? Ok(ApiResponse<ResultDto>.Ok(r.Data, r.Message)) : BadRequest(ApiResponse<ResultDto>.Fail(r.Message ?? "Failed", r.ValidationErrors));
    }

    [HttpPut("admin/results/{id:guid}")]
    [PermissionAuthorize("Results.Update")]
    public async Task<ActionResult<ApiResponse<ResultDto>>> Update(Guid id, [FromBody] UpdateResultCommand command, CancellationToken cancellationToken)
    {
        var r = await sender.Send(command with { Id = id }, cancellationToken);
        return r.Success ? Ok(ApiResponse<ResultDto>.Ok(r.Data, r.Message)) : BadRequest(ApiResponse<ResultDto>.Fail(r.Message ?? "Failed", r.ValidationErrors));
    }

    [HttpDelete("admin/results/{id:guid}")]
    [PermissionAuthorize("Results.Delete")]
    public async Task<ActionResult<ApiResponse<object>>> Delete(Guid id, CancellationToken cancellationToken)
    {
        var r = await sender.Send(new DeleteResultCommand(id), cancellationToken);
        return r.Success ? Ok(ApiResponse<object>.Ok(new { }, r.Message)) : BadRequest(ApiResponse<object>.Fail(r.Message ?? "Failed"));
    }

    // Result images are small marketing screenshots; 5 MB is generous. The cap
    // brings this endpoint in line with the other uploads (avatar/material/…),
    // which each set their own limit rather than relying on the global body cap.
    private const int MaxResultImageBytes = 5 * 1024 * 1024;

    [HttpPost("admin/results/{id:guid}/images")]
    [PermissionAuthorize("Results.Update")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxResultImageBytes)]
    public async Task<ActionResult<ApiResponse<ResultImageDto>>> UploadImage(Guid id, IFormFile file, [FromQuery] bool isMain = false,
        CancellationToken cancellationToken = default)
    {
        if (file is null || file.Length == 0)
            return BadRequest(ApiResponse<ResultImageDto>.Fail("No file provided."));
        if (file.Length > MaxResultImageBytes)
            return BadRequest(ApiResponse<ResultImageDto>.Fail("Image must be 5 MB or smaller."));
        await using var stream = file.OpenReadStream();
        var r = await sender.Send(new UploadResultImageCommand(id, stream, file.FileName, isMain), cancellationToken);
        return r.ToApiResult();
    }

    [HttpPut("admin/results/{id:guid}/images/{imageId:guid}")]
    [PermissionAuthorize("Results.Update")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxResultImageBytes)]
    public async Task<ActionResult<ApiResponse<ResultImageDto>>> ReplaceImage(Guid id, Guid imageId, IFormFile file,
        CancellationToken cancellationToken = default)
    {
        if (file is null || file.Length == 0)
            return BadRequest(ApiResponse<ResultImageDto>.Fail("No file provided."));
        if (file.Length > MaxResultImageBytes)
            return BadRequest(ApiResponse<ResultImageDto>.Fail("Image must be 5 MB or smaller."));
        await using var stream = file.OpenReadStream();
        var r = await sender.Send(new ReplaceResultImageCommand(id, imageId, stream, file.FileName), cancellationToken);
        return r.ToApiResult();
    }

    [HttpDelete("admin/results/{id:guid}/images/{imageId:guid}")]
    [PermissionAuthorize("Results.Delete")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteImage(Guid id, Guid imageId, CancellationToken cancellationToken)
    {
        var r = await sender.Send(new DeleteResultImageCommand(id, imageId), cancellationToken);
        return r.Success ? Ok(ApiResponse<object>.Ok(new { }, r.Message)) : BadRequest(ApiResponse<object>.Fail(r.Message ?? "Failed"));
    }
}
