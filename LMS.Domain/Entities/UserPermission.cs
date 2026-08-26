using LMS.Domain.Common;
using LMS.Domain.Exceptions;

namespace LMS.Domain.Entities;

/// <summary>
/// A permission granted directly to a single user, ADDITIVE on top of whatever
/// their roles already grant. Lets a SuperAdmin hand one person access to a
/// feature (e.g. Mock tests / CMS) without minting a whole role for them.
/// Effective permissions = role permissions ∪ user permissions.
/// </summary>
public sealed class UserPermission : BaseEntity
{
    public UserPermission(Guid userId, Guid permissionId) : base()
    {
        if (userId == Guid.Empty) throw new DomainException("User id is required.");
        if (permissionId == Guid.Empty) throw new DomainException("Permission id is required.");
        UserId = userId;
        PermissionId = permissionId;
    }

    public Guid UserId { get; private set; }
    public User? User { get; private set; }
    public Guid PermissionId { get; private set; }
    public Permission? Permission { get; private set; }
}
