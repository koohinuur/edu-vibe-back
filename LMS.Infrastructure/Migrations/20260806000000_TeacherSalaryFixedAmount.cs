using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using LMS.Infrastructure.Persistence;

#nullable disable

namespace LMS.Infrastructure.Migrations
{
    /// <summary>
    /// Adds an optional flat <c>FixedAmount</c> to <c>teacher_salary_configs</c>.
    /// When set on a per-class row, the salary calculation pays that flat amount
    /// for the class instead of revenue × percentage. Null keeps the existing
    /// percentage-of-revenue behaviour, so this is fully backward compatible.
    ///
    /// House style: hand-authored + idempotent (ADD COLUMN IF NOT EXISTS). Safe to
    /// re-run; snapshot intentionally not machine-regenerated.
    /// </summary>
    [DbContext(typeof(LMSDbContext))]
    [Migration("20260806000000_TeacherSalaryFixedAmount")]
    public partial class TeacherSalaryFixedAmount : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE teacher_salary_configs ADD COLUMN IF NOT EXISTS ""FixedAmount"" numeric(12,2);
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE teacher_salary_configs DROP COLUMN IF EXISTS ""FixedAmount"";
            ");
        }
    }
}
