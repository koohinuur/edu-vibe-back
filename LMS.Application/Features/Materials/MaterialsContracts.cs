using LMS.Application.Common.Models;
using LMS.Domain.Enums;
using MediatR;

namespace LMS.Application.Features.Materials;

public sealed record MaterialDto(
    Guid Id,
    string Title,
    string? Description,
    MaterialVisibility Visibility,
    string OriginalFileName,
    string MimeType,
    long FileSize,
    Guid UploadedByUserId,
    DateTime CreatedAt,
    IReadOnlyCollection<Guid> ClassIds,
    Guid? CurriculumTemplateId = null);

/// <summary>
/// Lists materials the caller is allowed to see. Public materials are always
/// included; Private materials are filtered by:
///   • teachers — they see Private materials they uploaded or that link to
///     a class they teach;
///   • students — they see Private materials linked to a class they're
///     enrolled in;
///   • admins / office-admins — they see everything.
/// </summary>
public sealed record GetMaterialsQuery(Guid? ClassId = null) : IRequest<Result<IReadOnlyCollection<MaterialDto>>>;

/// <summary>
/// Marketing-site feed — only Public materials, no role checks, no class
/// scope. Capped by <paramref name="Take"/>.
/// </summary>
public sealed record GetPublicMaterialsQuery(int Take = 24)
    : IRequest<Result<IReadOnlyCollection<MaterialDto>>>;

public sealed record GetMaterialByIdQuery(Guid MaterialId) : IRequest<Result<MaterialDto>>;

/// <summary>
/// Resolves a download — returns the stored file name + original name +
/// mime so the controller can stream the blob. Subject to the same
/// visibility checks as <see cref="GetMaterialsQuery"/>.
/// </summary>
public sealed record GetMaterialForDownloadQuery(Guid MaterialId)
    : IRequest<Result<MaterialDownloadDto>>;

/// <summary>
/// Marketing-site download. Returns the blob descriptor only when the
/// material is Public — never leaks Private rows even by id guess.
/// </summary>
public sealed record GetPublicMaterialDownloadQuery(Guid MaterialId)
    : IRequest<Result<MaterialDownloadDto>>;

public sealed record MaterialDownloadDto(
    Guid Id,
    string StoredFileName,
    string OriginalFileName,
    string MimeType);

/// <summary>
/// Upload command. The controller has already saved the blob to disk and
/// passes the opaque <see cref="StoredFileName"/> here; the handler creates
/// the metadata row + class links. If the handler fails, the controller
/// removes the orphan file from disk.
/// </summary>
public sealed record UploadMaterialCommand(
    string Title,
    string? Description,
    MaterialVisibility Visibility,
    string StoredFileName,
    string OriginalFileName,
    string MimeType,
    long FileSize,
    Guid UploadedByUserId,
    IReadOnlyCollection<Guid> ClassIds,
    Guid? CurriculumTemplateId = null) : IRequest<Result<MaterialDto>>;

public sealed record UpdateMaterialCommand(
    Guid MaterialId,
    string Title,
    string? Description,
    MaterialVisibility Visibility,
    IReadOnlyCollection<Guid> ClassIds,
    Guid? CurriculumTemplateId = null) : IRequest<Result<MaterialDto>>;

public sealed record DeleteMaterialCommand(Guid MaterialId) : IRequest<Result<string>>;
