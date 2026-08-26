using LMS.Application.Common.Models;
using MediatR;

namespace LMS.Application.Features.CmsAccess;

/// <summary>
/// One staff member in the "who can manage the CMS" picker: their user id,
/// display name, email, and whether CMS access is currently attached to them
/// (i.e. the CMS permission bundle is granted directly, via this feature).
/// </summary>
public sealed record CmsStaffDto(Guid UserId, string Name, string Email, bool HasAccess);

/// <summary>Staff list for the CMS-access picker.</summary>
public sealed record GetCmsStaffQuery() : IRequest<Result<IReadOnlyCollection<CmsStaffDto>>>;

/// <summary>
/// Reconcile CMS access so exactly these staff users have it: everyone listed is
/// granted the CMS permission bundle (as direct grants), everyone not listed has
/// it removed. Admins/SuperAdmins are unaffected — they already have everything.
/// </summary>
public sealed record SetCmsAccessCommand(IReadOnlyCollection<Guid> UserIds) : IRequest<Result>;
