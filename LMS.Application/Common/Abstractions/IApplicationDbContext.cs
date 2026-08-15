using LMS.Application.Common.Models;
using LMS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace LMS.Application.Common.Abstractions;

public interface IApplicationDbContext
{
    DbSet<User> Users { get; }
    DbSet<Role> Roles { get; }
    DbSet<Permission> Permissions { get; }
    DbSet<RolePermission> RolePermissions { get; }
    DbSet<UserRole> UserRoles { get; }
    DbSet<StaffProfile> StaffProfiles { get; }
    DbSet<StudentProfile> StudentProfiles { get; }
    DbSet<Course> Courses { get; }
    DbSet<Room> Rooms { get; }
    DbSet<Class> Classes { get; }
    DbSet<ClassResource> ClassResources { get; }
    DbSet<CurriculumTemplate> CurriculumTemplates { get; }
    DbSet<CurriculumModule> CurriculumModules { get; }
    DbSet<CurriculumUnit> CurriculumUnits { get; }
    DbSet<CurriculumLesson> CurriculumLessons { get; }
    DbSet<CurriculumPlanDay> CurriculumPlanDays { get; }
    DbSet<CurriculumPlanDayLesson> CurriculumPlanDayLessons { get; }
    DbSet<LessonDefaultTask> LessonDefaultTasks { get; }
    DbSet<Exam> Exams { get; }
    DbSet<ExamSection> ExamSections { get; }
    DbSet<ExamResult> ExamResults { get; }
    DbSet<ExamSectionScore> ExamSectionScores { get; }
    DbSet<Enrollment> Enrollments { get; }
    DbSet<ClassSession> ClassSessions { get; }
    DbSet<ClassSessionLesson> ClassSessionLessons { get; }
    DbSet<ClassSchedulePattern> ClassSchedulePatterns { get; }
    DbSet<Attendance> Attendance { get; }
    DbSet<Assignment> Assignments { get; }
    DbSet<Submission> Submissions { get; }
    DbSet<SubmissionFile> SubmissionFiles { get; }
    DbSet<SubmissionAudit> SubmissionAudits { get; }
    DbSet<LessonMaterial> LessonMaterials { get; }
    DbSet<LessonProgress> LessonProgress { get; }
    DbSet<Payment> Payments { get; }
    DbSet<Punishment> Punishments { get; }
    DbSet<TeacherSalaryConfig> TeacherSalaryConfigs { get; }
    DbSet<Conversation> Conversations { get; }
    DbSet<ConversationParticipant> ConversationParticipants { get; }
    DbSet<Message> Messages { get; }
    DbSet<Badge> Badges { get; }
    DbSet<StudentBadge> StudentBadges { get; }
    DbSet<XpLedger> XpLedger { get; }
    DbSet<ResultEntry> Results { get; }
    DbSet<ResultScoreBreakdown> ResultScoreBreakdowns { get; }
    DbSet<ResultImage> ResultImages { get; }
    DbSet<ResultView> ResultViews { get; }
    DbSet<VisitorMessage> VisitorMessages { get; }
    DbSet<Book> Books { get; }
    DbSet<AssignmentBook> AssignmentBooks { get; }
    DbSet<AssignmentFile> AssignmentFiles { get; }
    DbSet<AssignmentAssignee> AssignmentAssignees { get; }
    DbSet<LearningTask> LearningTasks { get; }
    DbSet<TaskSubmission> TaskSubmissions { get; }
    DbSet<LessonExercise> LessonExercises { get; }
    DbSet<LessonExerciseSubmission> LessonExerciseSubmissions { get; }
    DbSet<ExerciseSet> ExerciseSets { get; }
    DbSet<ExerciseSetClass> ExerciseSetClasses { get; }
    DbSet<Reminder> Reminders { get; }
    DbSet<Specialization> Specializations { get; }
    DbSet<StaffSpecialization> StaffSpecializations { get; }
    DbSet<Material> Materials { get; }
    DbSet<MaterialClass> MaterialClasses { get; }
    DbSet<OfficeInfo> OfficeInfo { get; }
    DbSet<Announcement> Announcements { get; }
    DbSet<MarketingCourse> MarketingCourses { get; }
    DbSet<MarketingVideo> MarketingVideos { get; }
    DbSet<MockTestSlot> MockTestSlots { get; }
    DbSet<MockTestRegistration> MockTestRegistrations { get; }
    DbSet<TelegramAccount> TelegramAccounts { get; }
    DbSet<TelegramSettings> TelegramSettings { get; }
    DbSet<TelegramDeepLinkToken> TelegramDeepLinkTokens { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Opens a DB transaction on the shared scoped context so a multi-step command
    /// (e.g. F3 GenerateCourse) can commit all of its SaveChanges atomically.
    /// NOTE: a bare call throws under the retry-on-failure execution strategy — use
    /// <see cref="ExecuteInTransactionAsync{T}"/> for multi-step transactional work.
    /// </summary>
    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Runs <paramref name="action"/> as a RETRIABLE transaction. The context enables
    /// EnableRetryOnFailure (NpgsqlRetryingExecutionStrategy), which forbids a bare
    /// user-initiated <see cref="BeginTransactionAsync"/>; this wraps the whole unit in
    /// the execution strategy so it re-executes atomically on a transient failure. The
    /// change tracker is reset on each attempt. Commits when the returned Result
    /// succeeds, rolls back otherwise.
    /// </summary>
    Task<Result<T>> ExecuteInTransactionAsync<T>(Func<Task<Result<T>>> action, CancellationToken cancellationToken);
}
