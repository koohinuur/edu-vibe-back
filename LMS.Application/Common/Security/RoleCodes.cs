namespace LMS.Application.Common.Security;

/// <summary>
/// Backend role code constants.
///
/// The platform has consolidated to three primary roles
/// (<see cref="Admin"/>, <see cref="Teacher"/>, <see cref="Student"/>). The
/// four legacy codes (SuperAdmin / AcademyDirector / OfficeAdmin /
/// SupportTeacher) still exist for backward-compat with historical user
/// assignments and a few solution-strip allowlists, but new code should not
/// grant them. See <c>DemoUsersSeederHostedService</c> and
/// <c>RolePermissionSeederHostedService</c> — neither seeds the legacy four.
/// </summary>
public static class RoleCodes
{
    public const string Admin = "Admin";
    public const string Teacher = "Teacher";
    public const string Student = "Student";

    /// <summary>
    /// SMM (social-media / marketing) manager. A focused role whose job is the
    /// marketing website's content — see <c>RolePermissionMatrix.ForSmm</c>.
    /// Lands in a CMS-focused panel on the frontend.
    /// </summary>
    public const string SmmManager = "SmmManager";

    // Legacy — kept for backward compat with historical role rows.
    public const string SuperAdmin = "SuperAdmin";
    public const string AcademyDirector = "AcademyDirector";
    public const string OfficeAdmin = "OfficeAdmin";
    public const string SupportTeacher = "SupportTeacher";

    /// <summary>
    /// The seven platform roles seeded by <c>SeedData</c>. These are structural —
    /// the auth stack, seeders and solution-strip allowlists reference them by
    /// code — so the RBAC admin API must never let them be renamed (their
    /// <c>Code</c>) or deleted. Their permission grants stay fully editable; a
    /// SuperAdmin can also freely create/rename/delete any CUSTOM role.
    /// </summary>
    public static readonly IReadOnlySet<string> BuiltIn = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase)
    {
        Admin, Teacher, Student, SmmManager, SuperAdmin, AcademyDirector, OfficeAdmin, SupportTeacher,
    };

    public static bool IsBuiltIn(string? code) => code is not null && BuiltIn.Contains(code);
}
