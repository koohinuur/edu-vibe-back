using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LMS.Infrastructure.Migrations
{
    /// <summary>
    /// Adds staff_profiles.DisplayOrder — a manual sort key for the public
    /// marketing-site teachers grid (lower first; name as the tiebreaker).
    /// Idempotent raw SQL so it's safe to re-run over an existing database.
    /// </summary>
    public partial class StaffDisplayOrder : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DO $$ BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_name = 'staff_profiles' AND column_name = 'DisplayOrder'
                    ) THEN
                        ALTER TABLE staff_profiles ADD COLUMN ""DisplayOrder"" integer NOT NULL DEFAULT 0;
                    END IF;
                END $$;
                CREATE INDEX IF NOT EXISTS ""IX_staff_profiles_DisplayOrder""
                    ON staff_profiles (""DisplayOrder"");
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DROP INDEX IF EXISTS ""IX_staff_profiles_DisplayOrder"";
                ALTER TABLE staff_profiles DROP COLUMN IF EXISTS ""DisplayOrder"";
            ");
        }
    }
}
