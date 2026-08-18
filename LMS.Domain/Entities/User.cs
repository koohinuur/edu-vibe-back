using System.Linq;
using LMS.Domain.Common;
using LMS.Domain.Enums;
using LMS.Domain.Exceptions;

namespace LMS.Domain.Entities;

public sealed class User : BaseEntity
{
    public User(string email, string passwordHash)
    {
        if (string.IsNullOrWhiteSpace(email)) throw new DomainException("Email is required.");
        if (string.IsNullOrWhiteSpace(passwordHash)) throw new DomainException("Password hash is required.");

        Email = email.Trim().ToLowerInvariant();
        PasswordHash = passwordHash;
        Status = UserStatus.Active;
    }

    public string Email { get; private set; }
    /// <summary>Optional phone, usable as a login identifier. Stored normalized
    /// (leading '+' plus digits only). Set via <see cref="SetPhone"/>.</summary>
    public string? Phone { get; private set; }
    public string PasswordHash { get; private set; }
    public string? RefreshTokenHash { get; private set; }
    public DateTime? RefreshTokenExpiresAt { get; private set; }
    /// <summary>Hashed one-time password-reset code (Telegram-delivered), or null.</summary>
    public string? PasswordResetCodeHash { get; private set; }
    public DateTime? PasswordResetExpiresAt { get; private set; }
    public UserStatus Status { get; private set; }

    public StudentProfile? StudentProfile { get; private set; }
    public StaffProfile? StaffProfile { get; private set; }

    public ICollection<UserRole> UserRoles { get; } = new List<UserRole>();
    public ICollection<Class> TeachingClasses { get; } = new List<Class>();

    public bool HasRole(string roleCode)
    {
        return UserRoles.Any(x => x.Role != null && x.Role.Code == roleCode);
    }

    public void SetPasswordHash(string passwordHash)
    {
        if (string.IsNullOrWhiteSpace(passwordHash)) throw new DomainException("Password hash is required.");
        PasswordHash = passwordHash;
        Touch();
    }

    public void SetEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email)) throw new DomainException("Email is required.");
        Email = email.Trim().ToLowerInvariant();
        Touch();
    }

    /// <summary>Sets (or clears) the login phone. Normalizes to '+' + digits.</summary>
    public void SetPhone(string? phone)
    {
        Phone = NormalizePhone(phone);
        Touch();
    }

    /// <summary>Stores a hashed, expiring one-time reset code.</summary>
    public void SetPasswordResetCode(string codeHash, DateTime expiresAt)
    {
        if (string.IsNullOrWhiteSpace(codeHash)) throw new DomainException("Reset code hash is required.");
        PasswordResetCodeHash = codeHash;
        PasswordResetExpiresAt = expiresAt;
        Touch();
    }

    /// <summary>Clears any pending reset code (after use or on password change).</summary>
    public void ClearPasswordResetCode()
    {
        PasswordResetCodeHash = null;
        PasswordResetExpiresAt = null;
        Touch();
    }

    /// <summary>Normalizes a phone to a leading '+' (if present) plus digits, or
    /// null when empty. Used both for storage and for matching on login.</summary>
    public static string? NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var trimmed = phone.Trim();
        var plus = trimmed.StartsWith('+') ? "+" : "";
        var digits = new string(trimmed.Where(char.IsDigit).ToArray());
        return digits.Length == 0 ? null : plus + digits;
    }

    public void SetRefreshToken(string refreshTokenHash, DateTime expiresAt)
    {
        if (string.IsNullOrWhiteSpace(refreshTokenHash)) throw new DomainException("Refresh token hash is required.");
        RefreshTokenHash = refreshTokenHash;
        RefreshTokenExpiresAt = expiresAt;
        Touch();
    }

    public void ClearRefreshToken()
    {
        RefreshTokenHash = null;
        RefreshTokenExpiresAt = null;
        Touch();
    }

    public void Deactivate()
    {
        Status = UserStatus.Inactive;
        Touch();
    }

    public void Activate()
    {
        Status = UserStatus.Active;
        Touch();
    }

    /// <summary>
    /// Admin entry point — freeze/block/restore an account. Backs the
    /// /api/Staff/{id}/status and /api/Students/{id}/status endpoints.
    /// Distinct from <see cref="Activate"/> / <see cref="Deactivate"/>
    /// because admin can also push to <see cref="UserStatus.Blocked"/>,
    /// which the login flow refuses outright (compared to Inactive which
    /// can be reactivated by the user later via a different flow).
    /// </summary>
    public void SetStatus(UserStatus status)
    {
        if (!Enum.IsDefined(typeof(UserStatus), status))
            throw new DomainException("Unknown user status.");
        Status = status;
        if (status != UserStatus.Active) ClearRefreshToken();
        Touch();
    }
}