using LMS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LMS.Infrastructure.Persistence.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable("users");
        b.HasKey(x => x.Id);
        b.Property(x => x.Email).IsRequired().HasMaxLength(320);
        b.Property(x => x.PasswordHash).IsRequired().HasMaxLength(512);
        b.Property(x => x.RefreshTokenHash).HasMaxLength(512);
        b.HasIndex(x => x.Email).IsUnique();

        // RefreshTokenCommandHandler does a hash equality lookup as the first
        // filter. Indexing the hash + expiry lets PostgreSQL skip every user
        // whose token is null or expired without a table scan.
        b.HasIndex(x => new { x.RefreshTokenHash, x.RefreshTokenExpiresAt })
            .HasDatabaseName("ix_users_refresh_token_lookup");
    }
}

public sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> b)
    {
        b.ToTable("roles");
        b.HasKey(x => x.Id);
        b.Property(x => x.Code).IsRequired().HasMaxLength(64);
        b.Property(x => x.Name).IsRequired().HasMaxLength(128);
        b.HasIndex(x => x.Code).IsUnique();
    }
}

public sealed class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> b)
    {
        b.ToTable("permissions");
        b.HasKey(x => x.Id);
        b.Property(x => x.Code).IsRequired().HasMaxLength(128);
        b.Property(x => x.Module).IsRequired().HasMaxLength(64);
        b.Property(x => x.Description).HasMaxLength(512);
        b.HasIndex(x => x.Code).IsUnique();
    }
}

public sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> b)
    {
        b.ToTable("role_permissions");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.RoleId, x.PermissionId }).IsUnique();
        b.HasOne(x => x.Role).WithMany(x => x.RolePermissions).HasForeignKey(x => x.RoleId)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Permission).WithMany(x => x.RolePermissions).HasForeignKey(x => x.PermissionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class UserPermissionConfiguration : IEntityTypeConfiguration<UserPermission>
{
    public void Configure(EntityTypeBuilder<UserPermission> b)
    {
        b.ToTable("user_permissions");
        b.HasKey(x => x.Id);
        // The authz handler + token issuers filter on UserId; the unique pair
        // guards against duplicate grants of the same permission to a user.
        b.HasIndex(x => new { x.UserId, x.PermissionId }).IsUnique();
        b.HasIndex(x => x.UserId).HasDatabaseName("ix_user_permissions_user_id");
        // Grants follow the user (cleaned up if the user is removed); a permission
        // that's granted can't be hard-deleted until the grant is cleared first
        // (the DeletePermission handler clears them alongside role grants).
        b.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Permission).WithMany().HasForeignKey(x => x.PermissionId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
    public void Configure(EntityTypeBuilder<UserRole> b)
    {
        b.ToTable("user_roles");
        b.HasKey(x => x.Id);
        // Unique pair (UserId, RoleId) is already covered. The PermissionAuthorizationHandler
        // and LoginCommandHandler filter on UserId alone — needs its own index.
        b.HasIndex(x => new { x.UserId, x.RoleId }).IsUnique();
        b.HasIndex(x => x.UserId).HasDatabaseName("ix_user_roles_user_id");
        b.HasOne(x => x.User).WithMany(x => x.UserRoles).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Role).WithMany(x => x.UserRoles).HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class StaffProfileConfiguration : IEntityTypeConfiguration<StaffProfile>
{
    public void Configure(EntityTypeBuilder<StaffProfile> b)
    {
        b.ToTable("staff_profiles");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.UserId).IsUnique();
        b.Property(x => x.DisplayOrder).HasDefaultValue(0);
        // Speeds up the public teachers feed's ORDER BY DisplayOrder.
        b.HasIndex(x => x.DisplayOrder);
        b.HasOne(x => x.User).WithOne(x => x.StaffProfile).HasForeignKey<StaffProfile>(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class StudentProfileConfiguration : IEntityTypeConfiguration<StudentProfile>
{
    public void Configure(EntityTypeBuilder<StudentProfile> b)
    {
        b.ToTable("student_profiles");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.UserId).IsUnique();
        b.HasOne(x => x.User).WithOne(x => x.StudentProfile).HasForeignKey<StudentProfile>(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class CourseConfiguration : IEntityTypeConfiguration<Course>
{
    public void Configure(EntityTypeBuilder<Course> b)
    {
        b.ToTable("courses");
        b.HasKey(x => x.Id);
        b.Property(x => x.Code).IsRequired().HasMaxLength(64);
        b.Property(x => x.Name).IsRequired().HasMaxLength(256);
        b.HasIndex(x => x.Code).IsUnique();
    }
}

public sealed class RoomConfiguration : IEntityTypeConfiguration<Room>
{
    public void Configure(EntityTypeBuilder<Room> b)
    {
        b.ToTable("rooms");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).IsRequired().HasMaxLength(128);
        b.Property(x => x.MeetingLink).HasMaxLength(1024);
        b.HasIndex(x => x.Name).IsUnique();
    }
}

public sealed class ClassConfiguration : IEntityTypeConfiguration<Class>
{
    public void Configure(EntityTypeBuilder<Class> b)
    {
        b.ToTable("classes");
        b.HasKey(x => x.Id);
        b.Property(x => x.Title).IsRequired().HasMaxLength(256);
        b.Property(x => x.MonthlyPrice).HasPrecision(18, 2);
        // Used by Classes/assigned/{teacherUserId} + ResolveUserClassIds in the
        // session handlers. EF doesn't auto-index FKs on optional one-to-many.
        b.HasIndex(x => x.TeacherUserId).HasDatabaseName("ix_classes_teacher_user_id");
        b.HasOne(x => x.Teacher).WithMany(x => x.TeachingClasses).HasForeignKey(x => x.TeacherUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ClassResourceConfiguration : IEntityTypeConfiguration<ClassResource>
{
    public void Configure(EntityTypeBuilder<ClassResource> b)
    {
        b.ToTable("class_resources");
        b.HasKey(x => x.Id);
        b.Property(x => x.Title).IsRequired().HasMaxLength(256);
        // The hub reads every resource for one class ordered by Position — index the FK.
        b.HasIndex(x => x.ClassId).HasDatabaseName("ix_class_resources_class_id");
        b.HasOne(x => x.Class).WithMany(c => c.Resources)
            .HasForeignKey(x => x.ClassId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class EnrollmentConfiguration : IEntityTypeConfiguration<Enrollment>
{
    public void Configure(EntityTypeBuilder<Enrollment> b)
    {
        b.ToTable("enrollments");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.ClassId, x.StudentProfileId }).IsUnique();
        // The composite covers WHERE ClassId = X via left-prefix. Filtering by
        // StudentProfileId alone (assignments lookups, dashboards) needs its own index.
        b.HasIndex(x => x.StudentProfileId).HasDatabaseName("ix_enrollments_student_profile_id");
    }
}

public sealed class ClassSessionConfiguration : IEntityTypeConfiguration<ClassSession>
{
    public void Configure(EntityTypeBuilder<ClassSession> b)
    {
        b.ToTable("class_sessions");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.ClassId, x.SessionDate, x.StartsAt }).IsUnique();
    }
}

public sealed class ClassSchedulePatternConfiguration : IEntityTypeConfiguration<ClassSchedulePattern>
{
    public void Configure(EntityTypeBuilder<ClassSchedulePattern> b)
    {
        b.ToTable("class_schedule_patterns");
        b.HasKey(x => x.Id);
        // One pattern per class — re-applying updates the row in place.
        b.HasIndex(x => x.ClassId).IsUnique();
        b.HasOne(x => x.Class).WithMany().HasForeignKey(x => x.ClassId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class LessonMaterialConfiguration : IEntityTypeConfiguration<LessonMaterial>
{
    public void Configure(EntityTypeBuilder<LessonMaterial> b)
    {
        b.ToTable("lesson_materials");
        b.HasKey(x => x.Id);
        b.Property(x => x.StoredFileName).IsRequired().HasMaxLength(256);
        b.Property(x => x.OriginalFileName).IsRequired().HasMaxLength(512);
        b.Property(x => x.MimeType).IsRequired().HasMaxLength(256);
        b.HasIndex(x => x.ClassSessionId);
        b.HasOne(x => x.ClassSession).WithMany(s => s.Materials)
            .HasForeignKey(x => x.ClassSessionId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class LessonProgressConfiguration : IEntityTypeConfiguration<LessonProgress>
{
    public void Configure(EntityTypeBuilder<LessonProgress> b)
    {
        b.ToTable("lesson_progress");
        b.HasKey(x => x.Id);
        // One completion mark per (student, session).
        b.HasIndex(x => new { x.StudentProfileId, x.ClassSessionId }).IsUnique();
        b.HasIndex(x => x.ClassSessionId);
    }
}

public sealed class AttendanceConfiguration : IEntityTypeConfiguration<Attendance>
{
    public void Configure(EntityTypeBuilder<Attendance> b)
    {
        b.ToTable("attendance");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.SessionId, x.StudentProfileId }).IsUnique();
        // GET /api/Attendance and the student-history query filter by single columns.
        b.HasIndex(x => x.SessionId).HasDatabaseName("ix_attendance_session_id");
        b.HasIndex(x => x.StudentProfileId).HasDatabaseName("ix_attendance_student_profile_id");
        b.HasIndex(x => x.ClassId).HasDatabaseName("ix_attendance_class_id");
    }
}

public sealed class AssignmentConfiguration : IEntityTypeConfiguration<Assignment>
{
    public void Configure(EntityTypeBuilder<Assignment> b)
    {
        b.ToTable("assignments");
        b.HasKey(x => x.Id);
        b.Property(x => x.Title).IsRequired().HasMaxLength(256);
        b.Property(x => x.Description).HasMaxLength(4000);
        // Curriculum-lesson provenance (multi-lesson day homework). SET NULL matches
        // the migration's FK so deleting a lesson doesn't cascade away an assignment.
        b.HasIndex(x => x.CurriculumLessonId);
        b.HasOne<CurriculumLesson>().WithMany().HasForeignKey(x => x.CurriculumLessonId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public sealed class SubmissionConfiguration : IEntityTypeConfiguration<Submission>
{
    public void Configure(EntityTypeBuilder<Submission> b)
    {
        b.ToTable("submissions");
        b.HasKey(x => x.Id);
        b.Property(x => x.Score).HasPrecision(10, 2);
        b.Property(x => x.MaxScore).HasPrecision(10, 2);
        b.Property(x => x.Feedback).HasMaxLength(4000);
        b.HasIndex(x => new { x.AssignmentId, x.StudentProfileId }).IsUnique();
        b.HasMany(x => x.Files).WithOne(f => f.Submission)
            .HasForeignKey(f => f.SubmissionId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class SubmissionFileConfiguration : IEntityTypeConfiguration<SubmissionFile>
{
    public void Configure(EntityTypeBuilder<SubmissionFile> b)
    {
        b.ToTable("submission_files");
        b.HasKey(x => x.Id);
        b.Property(x => x.StoredFileName).IsRequired().HasMaxLength(256);
        b.Property(x => x.OriginalFileName).IsRequired().HasMaxLength(512);
        b.Property(x => x.MimeType).IsRequired().HasMaxLength(256);
        b.Property(x => x.Sha256).IsRequired().HasMaxLength(64);
        b.HasIndex(x => x.SubmissionId);
        // Cross-student duplicate detection scans by hash.
        b.HasIndex(x => x.Sha256);
    }
}

public sealed class SubmissionAuditConfiguration : IEntityTypeConfiguration<SubmissionAudit>
{
    public void Configure(EntityTypeBuilder<SubmissionAudit> b)
    {
        b.ToTable("submission_audits");
        b.HasKey(x => x.Id);
        b.Property(x => x.Action).IsRequired().HasMaxLength(32);
        b.Property(x => x.Detail).HasMaxLength(1024);
        b.HasIndex(x => x.SubmissionId);
    }
}

public sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> b)
    {
        b.ToTable("payments");
        b.HasKey(x => x.Id);
        b.Property(x => x.Amount).HasPrecision(18, 2);
        // Currency defaults to UZS so existing rows backfill cleanly on migrate.
        b.Property(x => x.Currency).HasDefaultValue(LMS.Domain.Enums.Currency.UZS);
        b.HasOne(x => x.Class).WithMany().HasForeignKey(x => x.ClassId).OnDelete(DeleteBehavior.SetNull);
        b.HasIndex(x => new { x.ClassId, x.PeriodMonth });
    }
}

public sealed class TeacherSalaryConfigConfiguration : IEntityTypeConfiguration<TeacherSalaryConfig>
{
    public void Configure(EntityTypeBuilder<TeacherSalaryConfig> b)
    {
        b.ToTable("teacher_salary_configs");
        b.HasKey(x => x.Id);
        b.Property(x => x.Percentage).HasPrecision(5, 2);
        // Flat per-class override; null keeps the percentage-of-revenue path.
        b.Property(x => x.FixedAmount).HasPrecision(12, 2);
        // Uniqueness on (TeacherId, ClassId) with NULLS NOT DISTINCT is enforced
        // by the migration (EF can't express NULLS NOT DISTINCT) — one default
        // + one per-class row. Plain index here for lookups.
        b.HasIndex(x => new { x.TeacherId, x.ClassId });
        b.HasOne(x => x.Teacher).WithMany().HasForeignKey(x => x.TeacherId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Class).WithMany().HasForeignKey(x => x.ClassId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public void Configure(EntityTypeBuilder<Conversation> b)
    {
        b.ToTable("conversations");
        b.HasKey(x => x.Id);
    }
}

public sealed class ConversationParticipantConfiguration : IEntityTypeConfiguration<ConversationParticipant>
{
    public void Configure(EntityTypeBuilder<ConversationParticipant> b)
    {
        b.ToTable("conversation_participants");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.ConversationId, x.UserId }).IsUnique();
        // Conversations/my/{userId} and unread-count both filter by UserId only.
        b.HasIndex(x => x.UserId).HasDatabaseName("ix_conversation_participants_user_id");
    }
}

public sealed class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> b)
    {
        b.ToTable("messages");
        b.HasKey(x => x.Id);
        b.Property(x => x.Text).IsRequired().HasMaxLength(4000);
        // Hot paths: list messages by conversation, count unread per user.
        b.HasIndex(x => new { x.ConversationId, x.ReadAt })
            .HasDatabaseName("ix_messages_conversation_read_at");
    }
}

public sealed class BadgeConfiguration : IEntityTypeConfiguration<Badge>
{
    public void Configure(EntityTypeBuilder<Badge> b)
    {
        b.ToTable("badges");
        b.HasKey(x => x.Id);
        b.Property<string>("Code").HasMaxLength(64);
        b.HasIndex("Code").IsUnique();
    }
}

public sealed class StudentBadgeConfiguration : IEntityTypeConfiguration<StudentBadge>
{
    public void Configure(EntityTypeBuilder<StudentBadge> b)
    {
        b.ToTable("student_badges");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.BadgeId, x.StudentProfileId }).IsUnique();
    }
}

public sealed class XpLedgerConfiguration : IEntityTypeConfiguration<XpLedger>
{
    public void Configure(EntityTypeBuilder<XpLedger> b)
    {
        b.ToTable("xp_ledger");
        b.HasKey(x => x.Id);
    }
}

public sealed class ResultEntryConfiguration : IEntityTypeConfiguration<ResultEntry>
{
    public void Configure(EntityTypeBuilder<ResultEntry> b)
    {
        b.ToTable("results");
        b.HasKey(x => x.Id);
        b.Property(x => x.StudentFullName).IsRequired().HasMaxLength(256);
        b.Property(x => x.MainImageUrl).HasMaxLength(1024);
        b.Property(x => x.OverallScore).HasPrecision(10, 2);
        b.Property(x => x.Description).HasMaxLength(4000);
        b.Property(x => x.ImprovementText).HasMaxLength(1000);
        b.Property(x => x.DurationText).HasMaxLength(500);
        b.Property(x => x.Notes).HasMaxLength(2000);
        b.Property(x => x.BadgeIcon).HasMaxLength(512);
        b.Property(x => x.Language).IsRequired().HasMaxLength(10);
        b.HasIndex(x => new { x.IsPublished, x.IsFeatured, x.ExamType });
    }
}

public sealed class ResultScoreBreakdownConfiguration : IEntityTypeConfiguration<ResultScoreBreakdown>
{
    public void Configure(EntityTypeBuilder<ResultScoreBreakdown> b)
    {
        b.ToTable("result_score_breakdowns");
        b.HasKey(x => x.Id);
        b.Property(x => x.Key).IsRequired().HasMaxLength(128);
        b.Property(x => x.Value).IsRequired().HasMaxLength(128);
        b.HasOne(x => x.Result).WithMany(x => x.ScoreBreakdowns).HasForeignKey(x => x.ResultId)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.ResultId, x.Key }).IsUnique();
    }
}

public sealed class ResultImageConfiguration : IEntityTypeConfiguration<ResultImage>
{
    public void Configure(EntityTypeBuilder<ResultImage> b)
    {
        b.ToTable("result_images");
        b.HasKey(x => x.Id);
        b.Property(x => x.ImageUrl).IsRequired().HasMaxLength(1024);
        b.Property(x => x.ThumbnailUrl).HasMaxLength(1024);
        b.HasOne(x => x.Result).WithMany(x => x.Images).HasForeignKey(x => x.ResultId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ResultViewConfiguration : IEntityTypeConfiguration<ResultView>
{
    public void Configure(EntityTypeBuilder<ResultView> b)
    {
        b.ToTable("result_views");
        b.HasKey(x => x.Id);
        b.Property(x => x.IpAddress).HasMaxLength(64);
        b.Property(x => x.UserAgent).HasMaxLength(1024);
        b.HasOne(x => x.Result).WithMany(x => x.Views).HasForeignKey(x => x.ResultId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.ResultId);
    }
}

public sealed class BookConfiguration : IEntityTypeConfiguration<Book>
{
    public void Configure(EntityTypeBuilder<Book> b)
    {
        b.ToTable("books");
        b.HasKey(x => x.Id);
        b.Property(x => x.Title).IsRequired().HasMaxLength(256);
        b.Property(x => x.Author).HasMaxLength(256);
        b.Property(x => x.Subject).HasMaxLength(64);
        b.Property(x => x.Level).HasMaxLength(32);
        b.Property(x => x.Description).HasMaxLength(2000);
        b.Property(x => x.CoverImageUrl).HasMaxLength(1024);
        b.Property(x => x.FileUrl).HasMaxLength(1024);
        b.HasIndex(x => x.Title);
        b.HasIndex(x => x.Subject);
    }
}

public sealed class AssignmentBookConfiguration : IEntityTypeConfiguration<AssignmentBook>
{
    public void Configure(EntityTypeBuilder<AssignmentBook> b)
    {
        b.ToTable("assignment_books");
        b.HasKey(x => x.Id);
        b.Property(x => x.Note).HasMaxLength(512);
        b.HasIndex(x => new { x.AssignmentId, x.BookId }).IsUnique();
        b.HasOne(x => x.Assignment).WithMany().HasForeignKey(x => x.AssignmentId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Book).WithMany(x => x.AssignmentLinks).HasForeignKey(x => x.BookId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class AssignmentFileConfiguration : IEntityTypeConfiguration<AssignmentFile>
{
    public void Configure(EntityTypeBuilder<AssignmentFile> b)
    {
        b.ToTable("assignment_files");
        b.HasKey(x => x.Id);
        b.Property(x => x.StoredFileName).IsRequired().HasMaxLength(256);
        b.Property(x => x.OriginalFileName).IsRequired().HasMaxLength(512);
        b.Property(x => x.MimeType).IsRequired().HasMaxLength(256);
        b.HasIndex(x => x.AssignmentId);
        b.HasOne(x => x.Assignment).WithMany().HasForeignKey(x => x.AssignmentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class AssignmentAssigneeConfiguration : IEntityTypeConfiguration<AssignmentAssignee>
{
    public void Configure(EntityTypeBuilder<AssignmentAssignee> b)
    {
        b.ToTable("assignment_assignees");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.AssignmentId, x.StudentProfileId }).IsUnique();
        b.HasIndex(x => x.StudentProfileId).HasDatabaseName("ix_assignment_assignees_student_id");
        b.HasOne(x => x.Assignment).WithMany().HasForeignKey(x => x.AssignmentId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.StudentProfile).WithMany().HasForeignKey(x => x.StudentProfileId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class LearningTaskConfiguration : IEntityTypeConfiguration<LearningTask>
{
    public void Configure(EntityTypeBuilder<LearningTask> b)
    {
        b.ToTable("learning_tasks");
        b.HasKey(x => x.Id);
        b.Property(x => x.Title).IsRequired().HasMaxLength(256);
        // jsonb on PostgreSQL — efficient nested-path queries + smaller storage.
        b.Property(x => x.ContentJson).IsRequired().HasColumnType("jsonb");
        b.Property(x => x.SolutionJson).HasColumnType("jsonb");
        b.HasIndex(x => new { x.AssignmentId, x.Order })
            .HasDatabaseName("ix_learning_tasks_assignment_order");
        b.HasOne(x => x.Assignment).WithMany().HasForeignKey(x => x.AssignmentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class TaskSubmissionConfiguration : IEntityTypeConfiguration<TaskSubmission>
{
    public void Configure(EntityTypeBuilder<TaskSubmission> b)
    {
        b.ToTable("task_submissions");
        b.HasKey(x => x.Id);
        b.Property(x => x.ResponseJson).IsRequired().HasColumnType("jsonb");
        b.Property(x => x.Score).HasPrecision(5, 4);
        b.Property(x => x.TeacherFeedback).HasMaxLength(2000);
        b.HasIndex(x => new { x.TaskId, x.StudentProfileId }).IsUnique();
        b.HasIndex(x => x.StudentProfileId).HasDatabaseName("ix_task_submissions_student_id");
        b.HasOne(x => x.Task).WithMany(x => x.Submissions).HasForeignKey(x => x.TaskId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.StudentProfile).WithMany().HasForeignKey(x => x.StudentProfileId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

// ---- Lesson self-check exercises (textbook-style practice) ------------------

public sealed class LessonExerciseConfiguration : IEntityTypeConfiguration<LessonExercise>
{
    public void Configure(EntityTypeBuilder<LessonExercise> b)
    {
        b.ToTable("lesson_exercises");
        b.HasKey(x => x.Id);
        b.Property(x => x.Type).IsRequired().HasMaxLength(40);
        b.Property(x => x.Title).HasMaxLength(300);
        // jsonb — type-specific structure (items / parts / wordBank / answers).
        b.Property(x => x.ContentJson).IsRequired().HasColumnType("jsonb");
        // Upsert key for bulk add: one exercise per (lesson, orderIndex).
        b.HasIndex(x => new { x.LessonId, x.OrderIndex }).IsUnique()
            .HasDatabaseName("ix_lesson_exercises_lesson_order");
        b.HasOne<CurriculumLesson>().WithMany().HasForeignKey(x => x.LessonId)
            .OnDelete(DeleteBehavior.Cascade);
        // Second (mutually-exclusive) owner: a reusable ExerciseSet. Nullable — exactly one
        // of LessonId / ExerciseSetId is set (DB CHECK enforces the XOR). Its own upsert
        // index mirrors the lesson one so bulk-add-to-set is keyed the same way.
        b.HasIndex(x => new { x.ExerciseSetId, x.OrderIndex }).IsUnique()
            .HasDatabaseName("ix_lesson_exercises_set_order");
        b.HasOne<ExerciseSet>().WithMany().HasForeignKey(x => x.ExerciseSetId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Submissions).WithOne(x => x.LessonExercise)
            .HasForeignKey(x => x.LessonExerciseId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ExerciseSetConfiguration : IEntityTypeConfiguration<ExerciseSet>
{
    public void Configure(EntityTypeBuilder<ExerciseSet> b)
    {
        b.ToTable("exercise_sets");
        b.HasKey(x => x.Id);
        b.Property(x => x.Title).IsRequired().HasMaxLength(200);
        b.Property(x => x.Description).HasMaxLength(2000);
        b.HasIndex(x => x.CreatedByUserId);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.ClassLinks).WithOne().HasForeignKey(x => x.ExerciseSetId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ExerciseSetClassConfiguration : IEntityTypeConfiguration<ExerciseSetClass>
{
    public void Configure(EntityTypeBuilder<ExerciseSetClass> b)
    {
        b.ToTable("exercise_set_classes");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.ExerciseSetId, x.ClassId }).IsUnique()
            .HasDatabaseName("ix_exercise_set_classes_set_class");
        b.HasIndex(x => x.ClassId);
        // ExerciseSet FK is configured via ExerciseSet.ClassLinks above.
        b.HasOne<Class>().WithMany().HasForeignKey(x => x.ClassId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class LessonExerciseSubmissionConfiguration : IEntityTypeConfiguration<LessonExerciseSubmission>
{
    public void Configure(EntityTypeBuilder<LessonExerciseSubmission> b)
    {
        b.ToTable("lesson_exercise_submissions");
        b.HasKey(x => x.Id);
        b.Property(x => x.AnswersJson).IsRequired().HasColumnType("jsonb");
        // Teacher grading for open-ended types (writing): score out of max + feedback.
        b.Property(x => x.TeacherScore).HasColumnType("numeric(6,2)");
        b.Property(x => x.TeacherMaxScore).HasColumnType("numeric(6,2)");
        b.Property(x => x.TeacherFeedback).HasMaxLength(4000);
        // Exactly one result per (exercise, user) — the upsert key.
        b.HasIndex(x => new { x.LessonExerciseId, x.UserId }).IsUnique()
            .HasDatabaseName("ix_lesson_exercise_submissions_exercise_user");
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class VisitorMessageConfiguration : IEntityTypeConfiguration<VisitorMessage>
{
    public void Configure(EntityTypeBuilder<VisitorMessage> b)
    {
        b.ToTable("visitor_messages");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).IsRequired().HasMaxLength(128);
        b.Property(x => x.Phone).IsRequired().HasMaxLength(64);
        b.Property(x => x.Email).HasMaxLength(320);
        b.Property(x => x.Message).IsRequired().HasMaxLength(4000);
        b.Property(x => x.Course).HasMaxLength(128);
        b.Property(x => x.PreferredTime).HasMaxLength(128);
        b.Property(x => x.Language).HasMaxLength(8);
        // Admin inbox sorts by CreatedAt desc and filters by IsRead.
        b.HasIndex(x => new { x.IsRead, x.CreatedAt }).HasDatabaseName("ix_visitor_messages_inbox");
    }
}


public sealed class ReminderConfiguration : IEntityTypeConfiguration<Reminder>
{
    public void Configure(EntityTypeBuilder<Reminder> b)
    {
        b.ToTable("reminders");
        b.HasKey(x => x.Id);
        b.Property(x => x.Title).IsRequired().HasMaxLength(256);
        b.Property(x => x.Notes).HasMaxLength(2000);
        b.HasOne(x => x.OwnerUser).WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.OwnerUserId, x.DueAt }).HasDatabaseName("ix_reminders_owner_due");
        b.HasIndex(x => new { x.OwnerUserId, x.IsCompleted }).HasDatabaseName("ix_reminders_owner_status");
    }
}

public sealed class SpecializationConfiguration : IEntityTypeConfiguration<Specialization>
{
    public void Configure(EntityTypeBuilder<Specialization> b)
    {
        b.ToTable("specializations");
        b.HasKey(x => x.Id);
        b.Property(x => x.Code).IsRequired().HasMaxLength(64);
        b.Property(x => x.Name).IsRequired().HasMaxLength(128);
        b.Property(x => x.IsActive).IsRequired();
        b.HasIndex(x => x.Code).IsUnique();
    }
}

public sealed class StaffSpecializationConfiguration : IEntityTypeConfiguration<StaffSpecialization>
{
    public void Configure(EntityTypeBuilder<StaffSpecialization> b)
    {
        b.ToTable("staff_specializations");
        b.HasKey(x => x.Id);
        b.HasOne(x => x.StaffProfile)
            .WithMany(x => x.Specializations)
            .HasForeignKey(x => x.StaffProfileId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Specialization)
            .WithMany(x => x.StaffLinks)
            .HasForeignKey(x => x.SpecializationId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.StaffProfileId, x.SpecializationId }).IsUnique();
    }
}

public sealed class MaterialConfiguration : IEntityTypeConfiguration<Material>
{
    public void Configure(EntityTypeBuilder<Material> b)
    {
        b.ToTable("materials");
        b.HasKey(x => x.Id);
        b.Property(x => x.Title).IsRequired().HasMaxLength(256);
        b.Property(x => x.Description).HasMaxLength(2000);
        b.Property(x => x.StoredFileName).IsRequired().HasMaxLength(256);
        b.Property(x => x.OriginalFileName).IsRequired().HasMaxLength(256);
        b.Property(x => x.MimeType).IsRequired().HasMaxLength(128);
        // Admin list orders by CreatedAt desc + filters by uploader / visibility.
        b.HasIndex(x => x.CreatedAt).HasDatabaseName("ix_materials_created_at");
        b.HasIndex(x => x.UploadedByUserId).HasDatabaseName("ix_materials_uploaded_by");
        b.HasIndex(x => x.Visibility).HasDatabaseName("ix_materials_visibility");
        b.HasOne(x => x.UploadedByUser).WithMany().HasForeignKey(x => x.UploadedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class MaterialClassConfiguration : IEntityTypeConfiguration<MaterialClass>
{
    public void Configure(EntityTypeBuilder<MaterialClass> b)
    {
        b.ToTable("material_classes");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.MaterialId, x.ClassId }).IsUnique();
        b.HasIndex(x => x.ClassId).HasDatabaseName("ix_material_classes_class_id");
        b.HasOne(x => x.Material).WithMany(x => x.ClassLinks)
            .HasForeignKey(x => x.MaterialId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Class).WithMany()
            .HasForeignKey(x => x.ClassId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class OfficeInfoConfiguration : IEntityTypeConfiguration<OfficeInfo>
{
    public void Configure(EntityTypeBuilder<OfficeInfo> b)
    {
        b.ToTable("office_info");
        b.HasKey(x => x.Id);
        b.Property(x => x.AcademyName).IsRequired().HasMaxLength(256);
        b.Property(x => x.Tagline).HasMaxLength(256);
        b.Property(x => x.PhoneNumber).HasMaxLength(64);
        b.Property(x => x.SecondaryPhone).HasMaxLength(64);
        b.Property(x => x.Email).HasMaxLength(320);
        b.Property(x => x.Address).HasMaxLength(512);
        b.Property(x => x.WorkingHours).HasMaxLength(256);
        b.Property(x => x.TelegramUrl).HasMaxLength(512);
        b.Property(x => x.InstagramUrl).HasMaxLength(512);
        b.Property(x => x.FacebookUrl).HasMaxLength(512);
        b.Property(x => x.YoutubeUrl).HasMaxLength(512);
        b.Property(x => x.WebsiteUrl).HasMaxLength(512);
        b.Property(x => x.AboutHtml).HasMaxLength(8000);
        b.Property(x => x.MapEmbedUrl).HasMaxLength(2000);
    }
}

public sealed class PunishmentConfiguration : IEntityTypeConfiguration<Punishment>
{
    public void Configure(EntityTypeBuilder<Punishment> b)
    {
        b.ToTable("punishments");
        b.HasKey(x => x.Id);
        b.Property(x => x.Title).IsRequired().HasMaxLength(256);
        b.Property(x => x.Description).HasMaxLength(2000);
        b.Property(x => x.Reason).HasMaxLength(2000);
        b.Property(x => x.Value).HasColumnType("numeric(18,2)");
        b.HasIndex(x => new { x.TeacherId, x.PeriodMonth });
        b.HasOne(x => x.Teacher).WithMany().HasForeignKey(x => x.TeacherId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class AnnouncementConfiguration : IEntityTypeConfiguration<Announcement>
{
    public void Configure(EntityTypeBuilder<Announcement> b)
    {
        b.ToTable("announcements");
        b.HasKey(x => x.Id);
        b.Property(x => x.Title).IsRequired().HasMaxLength(256);
        b.Property(x => x.Body).IsRequired().HasMaxLength(4000);
        b.Property(x => x.Audience).HasConversion<int>();
        b.HasIndex(x => new { x.IsPublic, x.PublishesAt, x.ExpiresAt })
            .HasDatabaseName("ix_announcements_visibility_window");
        b.HasIndex(x => x.CreatedAt).HasDatabaseName("ix_announcements_created_at");
        b.HasOne(x => x.AuthorUser).WithMany()
            .HasForeignKey(x => x.AuthorUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class MarketingCourseConfiguration : IEntityTypeConfiguration<MarketingCourse>
{
    public void Configure(EntityTypeBuilder<MarketingCourse> b)
    {
        b.ToTable("marketing_courses");
        b.HasKey(x => x.Id);
        b.Property(x => x.Slug).IsRequired().HasMaxLength(64);
        b.Property(x => x.Title).IsRequired().HasMaxLength(256);
        b.Property(x => x.Subtitle).HasMaxLength(256);
        b.Property(x => x.Description).HasMaxLength(4000);
        b.Property(x => x.CoverImageUrl).HasMaxLength(1024);
        b.Property(x => x.PriceText).HasMaxLength(64);
        b.Property(x => x.DurationText).HasMaxLength(64);
        b.Property(x => x.LevelText).HasMaxLength(64);
        b.HasIndex(x => x.Slug).IsUnique();
        b.HasIndex(x => new { x.IsActive, x.SortOrder });
    }
}

public sealed class MarketingVideoConfiguration : IEntityTypeConfiguration<MarketingVideo>
{
    public void Configure(EntityTypeBuilder<MarketingVideo> b)
    {
        b.ToTable("marketing_videos");
        b.HasKey(x => x.Id);
        b.Property(x => x.Title).IsRequired().HasMaxLength(256);
        b.Property(x => x.Description).HasMaxLength(2000);
        b.Property(x => x.VideoUrl).IsRequired().HasMaxLength(1024);
        b.Property(x => x.ThumbnailUrl).HasMaxLength(1024);
        b.HasIndex(x => new { x.IsActive, x.SortOrder });
    }
}

public sealed class MockTestSlotConfiguration : IEntityTypeConfiguration<MockTestSlot>
{
    public void Configure(EntityTypeBuilder<MockTestSlot> b)
    {
        b.ToTable("mock_test_slots");
        b.HasKey(x => x.Id);
        b.Property(x => x.Title).IsRequired().HasMaxLength(256);
        b.Property(x => x.DurationText).HasMaxLength(64);
        // Public list filters by IsActive + future StartsAt, ordered by StartsAt.
        b.HasIndex(x => new { x.IsActive, x.StartsAt });
    }
}

public sealed class MockTestRegistrationConfiguration : IEntityTypeConfiguration<MockTestRegistration>
{
    public void Configure(EntityTypeBuilder<MockTestRegistration> b)
    {
        b.ToTable("mock_test_registrations");
        b.HasKey(x => x.Id);
        b.Property(x => x.FullName).IsRequired().HasMaxLength(256);
        b.Property(x => x.Phone).HasMaxLength(32);
        b.Property(x => x.Email).HasMaxLength(256);
        b.Property(x => x.ResultNotes).HasMaxLength(2000);
        foreach (var score in new[] { nameof(MockTestRegistration.Listening), nameof(MockTestRegistration.Reading),
                     nameof(MockTestRegistration.Writing), nameof(MockTestRegistration.Speaking),
                     nameof(MockTestRegistration.Overall) })
            b.Property(score).HasPrecision(4, 1);
        b.HasOne(x => x.Slot).WithMany().HasForeignKey(x => x.SlotId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.SlotId);
    }
}

public sealed class TelegramAccountConfiguration : IEntityTypeConfiguration<TelegramAccount>
{
    public void Configure(EntityTypeBuilder<TelegramAccount> b)
    {
        b.ToTable("telegram_accounts");
        b.HasKey(x => x.Id);
        b.Property(x => x.Username).HasMaxLength(64);
        b.Property(x => x.FirstName).HasMaxLength(128);
        b.Property(x => x.LastName).HasMaxLength(128);
        b.Property(x => x.PhotoUrl).HasMaxLength(1024);
        // One Telegram identity ↔ one platform user.
        b.HasIndex(x => x.TelegramUserId).IsUnique();
        b.HasIndex(x => x.UserId).IsUnique();
        b.HasOne(x => x.User).WithMany()
            .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class TelegramSettingsConfiguration : IEntityTypeConfiguration<TelegramSettings>
{
    public void Configure(EntityTypeBuilder<TelegramSettings> b)
    {
        b.ToTable("telegram_settings");
        b.HasKey(x => x.Id);
        b.Property(x => x.BotUsername).HasMaxLength(64);
    }
}

public sealed class TelegramDeepLinkTokenConfiguration : IEntityTypeConfiguration<TelegramDeepLinkToken>
{
    public void Configure(EntityTypeBuilder<TelegramDeepLinkToken> b)
    {
        b.ToTable("telegram_deep_link_tokens");
        b.HasKey(x => x.Id);
        b.Property(x => x.Token).IsRequired().HasMaxLength(128);
        b.HasIndex(x => x.Token).IsUnique();
        b.HasIndex(x => x.ExpiresAt);
        b.HasOne(x => x.User).WithMany()
            .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

// ---- Curriculum template tree (Template → Module → Unit → Lesson) ----------

public sealed class CurriculumTemplateConfiguration : IEntityTypeConfiguration<CurriculumTemplate>
{
    public void Configure(EntityTypeBuilder<CurriculumTemplate> b)
    {
        b.ToTable("curriculum_templates");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).IsRequired().HasMaxLength(200);
        b.Property(x => x.Level).HasMaxLength(40);
        b.Property(x => x.Description).HasMaxLength(2000);
        b.Property(x => x.Category).HasConversion<int>();
        b.HasIndex(x => x.Category);
        b.HasMany(x => x.Modules).WithOne().HasForeignKey(m => m.TemplateId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class CurriculumModuleConfiguration : IEntityTypeConfiguration<CurriculumModule>
{
    public void Configure(EntityTypeBuilder<CurriculumModule> b)
    {
        b.ToTable("curriculum_modules");
        b.HasKey(x => x.Id);
        b.Property(x => x.Title).IsRequired().HasMaxLength(200);
        b.HasIndex(x => new { x.TemplateId, x.Order });
        b.HasMany(x => x.Units).WithOne().HasForeignKey(u => u.ModuleId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class CurriculumUnitConfiguration : IEntityTypeConfiguration<CurriculumUnit>
{
    public void Configure(EntityTypeBuilder<CurriculumUnit> b)
    {
        b.ToTable("curriculum_units");
        b.HasKey(x => x.Id);
        b.Property(x => x.Title).IsRequired().HasMaxLength(200);
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.Icon).HasMaxLength(16);
        b.HasIndex(x => new { x.ModuleId, x.Order });
        b.HasMany(x => x.Lessons).WithOne().HasForeignKey(l => l.UnitId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class CurriculumLessonConfiguration : IEntityTypeConfiguration<CurriculumLesson>
{
    public void Configure(EntityTypeBuilder<CurriculumLesson> b)
    {
        b.ToTable("curriculum_lessons");
        b.HasKey(x => x.Id);
        b.Property(x => x.Title).IsRequired().HasMaxLength(300);
        b.Property(x => x.Objectives).HasMaxLength(2000);
        b.Property(x => x.HomeworkPlaceholder).HasMaxLength(1000);
        b.Property(x => x.MaterialsPlaceholder).HasMaxLength(1000);
        b.HasIndex(x => new { x.UnitId, x.Order });
    }
}

public sealed class LessonDefaultTaskConfiguration : IEntityTypeConfiguration<LessonDefaultTask>
{
    public void Configure(EntityTypeBuilder<LessonDefaultTask> b)
    {
        b.ToTable("lesson_default_tasks");
        b.HasKey(x => x.Id);
        b.Property(x => x.Title).IsRequired().HasMaxLength(256);
        b.HasIndex(x => new { x.CurriculumLessonId, x.Order });
        b.HasOne(x => x.CurriculumLesson).WithMany().HasForeignKey(x => x.CurriculumLessonId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

// ---- Template teaching plan + multi-lesson sessions (PR2 rails) -------------

public sealed class CurriculumPlanDayConfiguration : IEntityTypeConfiguration<CurriculumPlanDay>
{
    public void Configure(EntityTypeBuilder<CurriculumPlanDay> b)
    {
        b.ToTable("curriculum_plan_days");
        b.HasKey(x => x.Id);
        b.Property(x => x.Title).HasMaxLength(200);
        b.HasIndex(x => new { x.TemplateId, x.Order });
        b.HasOne<CurriculumTemplate>().WithMany().HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Lessons).WithOne().HasForeignKey(l => l.PlanDayId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class CurriculumPlanDayLessonConfiguration : IEntityTypeConfiguration<CurriculumPlanDayLesson>
{
    public void Configure(EntityTypeBuilder<CurriculumPlanDayLesson> b)
    {
        b.ToTable("curriculum_plan_day_lessons");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.PlanDayId, x.Order });
        b.HasIndex(x => new { x.PlanDayId, x.CurriculumLessonId }).IsUnique();
        b.HasOne<CurriculumLesson>().WithMany().HasForeignKey(x => x.CurriculumLessonId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ClassSessionLessonConfiguration : IEntityTypeConfiguration<ClassSessionLesson>
{
    public void Configure(EntityTypeBuilder<ClassSessionLesson> b)
    {
        b.ToTable("class_session_lessons");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.ClassSessionId, x.Order });
        b.HasIndex(x => new { x.ClassSessionId, x.CurriculumLessonId }).IsUnique();
        b.HasOne<ClassSession>().WithMany().HasForeignKey(x => x.ClassSessionId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<CurriculumLesson>().WithMany().HasForeignKey(x => x.CurriculumLessonId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ExamConfiguration : IEntityTypeConfiguration<Exam>
{
    public void Configure(EntityTypeBuilder<Exam> b)
    {
        b.ToTable("exams");
        b.HasKey(x => x.Id);
        b.Property(x => x.Title).IsRequired().HasMaxLength(256);
        b.Property(x => x.PassThresholdPercent).HasPrecision(5, 2);
        // One exam per exam-type curriculum lesson.
        b.HasIndex(x => x.CurriculumLessonId).IsUnique();
        b.HasOne(x => x.Class).WithMany().HasForeignKey(x => x.ClassId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.CurriculumLesson).WithMany().HasForeignKey(x => x.CurriculumLessonId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ExamSectionConfiguration : IEntityTypeConfiguration<ExamSection>
{
    public void Configure(EntityTypeBuilder<ExamSection> b)
    {
        b.ToTable("exam_sections");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).IsRequired().HasMaxLength(128);
        b.Property(x => x.MaxScore).HasPrecision(9, 2);
        b.HasIndex(x => new { x.ExamId, x.Order });
        b.HasOne(x => x.Exam).WithMany(e => e.Sections).HasForeignKey(x => x.ExamId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ExamResultConfiguration : IEntityTypeConfiguration<ExamResult>
{
    public void Configure(EntityTypeBuilder<ExamResult> b)
    {
        b.ToTable("exam_results");
        b.HasKey(x => x.Id);
        b.Property(x => x.OverallPercent).HasPrecision(5, 2);
        // One result per (exam, student) — drives the idempotent upsert.
        b.HasIndex(x => new { x.ExamId, x.StudentProfileId }).IsUnique();
        b.HasOne(x => x.Exam).WithMany().HasForeignKey(x => x.ExamId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.StudentProfile).WithMany().HasForeignKey(x => x.StudentProfileId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ExamSectionScoreConfiguration : IEntityTypeConfiguration<ExamSectionScore>
{
    public void Configure(EntityTypeBuilder<ExamSectionScore> b)
    {
        b.ToTable("exam_section_scores");
        b.HasKey(x => x.Id);
        b.Property(x => x.Score).HasPrecision(9, 2);
        b.HasIndex(x => new { x.ExamResultId, x.ExamSectionId }).IsUnique();
        b.HasOne(x => x.ExamResult).WithMany(r => r.SectionScores).HasForeignKey(x => x.ExamResultId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.ExamSection).WithMany().HasForeignKey(x => x.ExamSectionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
