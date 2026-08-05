using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using LMS.Infrastructure.Persistence;

#nullable disable

namespace LMS.Infrastructure.Migrations
{
    /// <summary>
    /// Marketing credentials on <c>staff_profiles</c>:
    ///   • Certifications  — free text ("IELTS, CELTA, BSc Physics"); the marketing
    ///     site splits it into badges.
    ///   • YearsExperience — nullable int, shown on the teacher card / profile.
    ///
    /// House style: hand-authored + idempotent (ADD COLUMN IF NOT EXISTS). Safe to
    /// re-run; snapshot intentionally not machine-regenerated.
    /// </summary>
    [DbContext(typeof(LMSDbContext))]
    [Migration("20260805000000_StaffCredentials")]
    public partial class StaffCredentials : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE staff_profiles ADD COLUMN IF NOT EXISTS ""Certifications"" text;
                ALTER TABLE staff_profiles ADD COLUMN IF NOT EXISTS ""YearsExperience"" integer;
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE staff_profiles DROP COLUMN IF EXISTS ""Certifications"";
                ALTER TABLE staff_profiles DROP COLUMN IF EXISTS ""YearsExperience"";
            ");
        }
    }
}
