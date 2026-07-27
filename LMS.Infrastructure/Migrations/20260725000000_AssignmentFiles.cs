using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using LMS.Infrastructure.Persistence;

#nullable disable

namespace LMS.Infrastructure.Migrations
{
    /// <summary>
    /// Teacher-provided worksheet files on an assignment:
    ///   • assignment_files — metadata + opaque stored name for a blob in the material
    ///     file store; FK to assignments ON DELETE CASCADE. Students download these; their
    ///     own answer uploads stay in submission_files (unchanged).
    ///
    /// House style: hand-authored + idempotent (CREATE TABLE IF NOT EXISTS). Safe to re-run.
    /// Snapshot intentionally not machine-regenerated (migrations are hand-authored raw SQL).
    /// </summary>
    [DbContext(typeof(LMSDbContext))]
    [Migration("20260725000000_AssignmentFiles")]
    public partial class AssignmentFiles : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS assignment_files (
                    ""Id"" uuid NOT NULL,
                    ""AssignmentId"" uuid NOT NULL,
                    ""StoredFileName"" character varying(256) NOT NULL,
                    ""OriginalFileName"" character varying(512) NOT NULL,
                    ""MimeType"" character varying(256) NOT NULL,
                    ""FileSize"" bigint NOT NULL,
                    ""CreatedAt"" timestamp with time zone NOT NULL DEFAULT (now() at time zone 'utc'),
                    ""UpdatedAt"" timestamp with time zone NOT NULL DEFAULT (now() at time zone 'utc'),
                    CONSTRAINT pk_assignment_files PRIMARY KEY (""Id""),
                    CONSTRAINT fk_assignment_files_assignments FOREIGN KEY (""AssignmentId"")
                        REFERENCES assignments (""Id"") ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS ix_assignment_files_assignment
                    ON assignment_files (""AssignmentId"");
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS assignment_files;");
        }
    }
}
