using LMS.Application.Common.Abstractions;
using LMS.Application.Common.Models;
using LMS.Application.Common.Security;
using LMS.Application.Features.Materials;
using LMS.Domain.Enums;
using LMS.WebApi.Common;
using LMS.WebApi.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LMS.WebApi.Controllers;

/// <summary>
/// Course materials — uploads + listing + download. Downloads go through
/// the controller (not the static-file pipeline) so the Private/class
/// visibility check applies on every request.
///
/// The multipart upload format mirrors what the LMS admin frontend posts:
///   title, description, visibility ("Public"|"Private"|"AdminsOnly"|
///   "TeachersOnly"|"StudentsOnly", or the numeric enum value),
///   classIds (comma-separated list — easier on the FormData wire than
///   repeating the field; only used when visibility is Private), file.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class MaterialsController(
    ISender sender,
    ICurrentUserService currentUser,
    IMaterialFileStore store) : ControllerBase
{
    // 25 MB cap — generous enough for lecture PDFs / slide decks; small enough
    // that a hostile caller can't OOM the API with one upload. Anything over
    // this should go to object storage rather than the local disk.
    private const long UploadSizeLimit = 25L * 1024 * 1024;

    [HttpGet]
    [PermissionAuthorize(Permissions.Materials.Read)]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<MaterialDto>>>> GetAll(
        [FromQuery] Guid? classId,
        CancellationToken ct)
    {
        var r = await sender.Send(new GetMaterialsQuery(classId), ct);
        return Ok(ApiResponse<IReadOnlyCollection<MaterialDto>>.Ok(r.Data, r.Message));
    }

    /// <summary>
    /// Anonymous endpoint for the marketing site — only Public materials,
    /// capped to <paramref name="take"/>. The handler still applies the
    /// visibility filter so Private rows can never leak.
    /// </summary>
    [HttpGet("public")]
    [AllowAnonymous]
    [Microsoft.AspNetCore.OutputCaching.OutputCache(PolicyName = LMS.WebApi.Common.PublicReadCacheHeaderPolicy.Name)]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<MaterialDto>>>> Public(
        [FromQuery] int take = 24, CancellationToken ct = default)
    {
        var r = await sender.Send(new GetPublicMaterialsQuery(take), ct);
        return Ok(ApiResponse<IReadOnlyCollection<MaterialDto>>.Ok(r.Data, r.Message));
    }

    [HttpGet("{id:guid}")]
    [PermissionAuthorize(Permissions.Materials.Read)]
    public async Task<ActionResult<ApiResponse<MaterialDto>>> Get(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new GetMaterialByIdQuery(id), ct);
        return r.ToApiResultOrNotFound();
    }

    /// <summary>
    /// Streams the material blob. Subject to the same visibility check as
    /// the list — students get 404 (not 403) on materials they can't see,
    /// to avoid leaking existence.
    /// </summary>
    [HttpGet("{id:guid}/download")]
    [PermissionAuthorize(Permissions.Materials.Read)]
    public async Task<IActionResult> Download(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new GetMaterialForDownloadQuery(id), ct);
        if (!r.Success || r.Data is null) return NotFound();

        var stream = await store.OpenAsync(r.Data.StoredFileName, ct);
        if (stream is null) return NotFound();

        // Inline + range so the in-platform viewer can render PDFs/images and
        // seek video/audio without forcing a download. The UI's download button
        // uses the client-side download attribute when the user wants the file.
        Response.Headers.ContentDisposition = $"inline; filename=\"{r.Data.OriginalFileName.Replace("\"", "")}\"";
        return File(stream, r.Data.MimeType, enableRangeProcessing: true);
    }

    /// <summary>
    /// Anonymous download — only Public materials. Used by the marketing site
    /// download buttons. Private materials 404 here even though they may
    /// exist; same approach as the signed-in path to avoid existence leaks.
    /// </summary>
    [HttpGet("{id:guid}/download/public")]
    [AllowAnonymous]
    public async Task<IActionResult> DownloadPublic(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new GetPublicMaterialDownloadQuery(id), ct);
        if (!r.Success || r.Data is null) return NotFound();

        var stream = await store.OpenAsync(r.Data.StoredFileName, ct);
        if (stream is null) return NotFound();

        return File(stream, r.Data.MimeType, r.Data.OriginalFileName);
    }

    [HttpPost("upload")]
    [PermissionAuthorize(Permissions.Materials.Manage)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(UploadSizeLimit)]
    public async Task<ActionResult<ApiResponse<MaterialDto>>> Upload(
        [FromForm] UploadMaterialForm form,
        CancellationToken ct)
    {
        if (currentUser.UserId is not { } uid)
            return Unauthorized(ApiResponse<MaterialDto>.Fail("Not authenticated."));
        if (form.File is null || form.File.Length == 0)
            return BadRequest(ApiResponse<MaterialDto>.Fail("File is required."));
        if (string.IsNullOrWhiteSpace(form.Title))
            return BadRequest(ApiResponse<MaterialDto>.Fail("Title is required."));

        if (!TryParseVisibility(form.Visibility, out var visibility))
            return BadRequest(ApiResponse<MaterialDto>.Fail(
                "Visibility must be one of Public, Private, AdminsOnly, TeachersOnly or StudentsOnly."));

        var classIds = ParseClassIds(form.ClassIds);

        string storedName;
        try
        {
            await using var src = form.File.OpenReadStream();
            storedName = await store.SaveAsync(src, form.File.FileName, form.File.ContentType, ct);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<MaterialDto>.Fail(ex.Message));
        }

        var r = await sender.Send(new UploadMaterialCommand(
            form.Title.Trim(),
            string.IsNullOrWhiteSpace(form.Description) ? null : form.Description.Trim(),
            visibility,
            storedName,
            form.File.FileName,
            form.File.ContentType,
            form.File.Length,
            uid,
            classIds), ct);

        if (!r.Success)
        {
            // Roll the blob back so we don't leave orphans on disk.
            await store.DeleteAsync(storedName, ct);
            return BadRequest(ApiResponse<MaterialDto>.Fail(r.Message ?? "Failed"));
        }

        return Ok(ApiResponse<MaterialDto>.Ok(r.Data, r.Message));
    }

    [HttpPut("{id:guid}")]
    [PermissionAuthorize(Permissions.Materials.Manage)]
    public async Task<ActionResult<ApiResponse<MaterialDto>>> Update(
        Guid id,
        [FromBody] UpdateMaterialRequest body,
        CancellationToken ct)
    {
        var r = await sender.Send(new UpdateMaterialCommand(
            id, body.Title, body.Description, body.Visibility,
            body.ClassIds ?? Array.Empty<Guid>()), ct);
        return r.ToApiResult();
    }

    [HttpDelete("{id:guid}")]
    [PermissionAuthorize(Permissions.Materials.Manage)]
    public async Task<ActionResult<ApiResponse<object>>> Delete(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new DeleteMaterialCommand(id), ct);
        if (!r.Success || r.Data is null)
            return BadRequest(ApiResponse<object>.Fail(r.Message ?? "Failed"));

        // Best-effort blob delete — entity is already gone, so a stale file
        // on disk is just a leak, not a correctness issue.
        await store.DeleteAsync(r.Data, ct);
        return Ok(ApiResponse<object>.Ok(new { }, r.Message));
    }

    private static bool TryParseVisibility(string? raw, out MaterialVisibility visibility)
    {
        visibility = MaterialVisibility.Public;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (Enum.TryParse<MaterialVisibility>(raw.Trim(), ignoreCase: true, out var v))
        {
            visibility = v;
            return true;
        }
        if (int.TryParse(raw, out var n) && Enum.IsDefined(typeof(MaterialVisibility), n))
        {
            visibility = (MaterialVisibility)n;
            return true;
        }
        return false;
    }

    private static IReadOnlyCollection<Guid> ParseClassIds(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<Guid>();
        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ids = new List<Guid>(parts.Length);
        foreach (var p in parts)
            if (Guid.TryParse(p, out var g)) ids.Add(g);
        return ids;
    }
}

/// <summary>
/// Multipart upload form body. Visibility comes in as a string and is parsed
/// in the controller so admins can post "Public"/"Private" or the numeric
/// enum value without surprises.
/// </summary>
public sealed class UploadMaterialForm
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Visibility { get; set; }
    public string? ClassIds { get; set; }
    public IFormFile? File { get; set; }
}

public sealed record UpdateMaterialRequest(
    string Title,
    string? Description,
    MaterialVisibility Visibility,
    IReadOnlyCollection<Guid>? ClassIds);
