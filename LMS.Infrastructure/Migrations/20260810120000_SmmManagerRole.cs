using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LMS.Infrastructure.Migrations
{
    /// <summary>
    /// Seeds the SmmManager role row. Its permission grants are applied at
    /// startup by RolePermissionSeederHostedService (RolePermissionMatrix.ForSmm).
    /// Idempotent — safe to re-run; only inserts when the role is missing.
    /// </summary>
    public partial class SmmManagerRole : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO roles (""Id"", ""Code"", ""Name"", ""CreatedAt"", ""UpdatedAt"")
                SELECT '10000000-0000-0000-0000-000000000008', 'SmmManager', 'SMM Manager',
                       (now() at time zone 'utc'), (now() at time zone 'utc')
                WHERE NOT EXISTS (SELECT 1 FROM roles WHERE ""Code"" = 'SmmManager');
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Remove the role's grants first (FK), then the role itself.
            migrationBuilder.Sql(@"
                DELETE FROM role_permissions rp
                USING roles r
                WHERE rp.""RoleId"" = r.""Id"" AND r.""Code"" = 'SmmManager';
                DELETE FROM roles WHERE ""Code"" = 'SmmManager';
            ");
        }
    }
}
