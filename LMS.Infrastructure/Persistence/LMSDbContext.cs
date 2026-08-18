using LMS.Application.Common.Abstractions;
using LMS.Application.Common.Models;
using LMS.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace LMS.Infrastructure.Persistence;

public sealed class LMSDbContext : DbContext, IApplicationDbContext
{
    public LMSDbContext(DbContextOptions<LMSDbContext> options) : base(options)
    {
    }

    public Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction> BeginTransactionAsync(
        CancellationToken cancellationToken) => Database.BeginTransactionAsync(cancellationToken);

    public async Task<Result<T>> ExecuteInTransactionAsync<T>(
        Func<Task<Result<T>>> action, CancellationToken cancellationToken)
    {
        // EnableRetryOnFailure requires the manual transaction to run INSIDE the
        // execution strategy so the whole unit can be retried as one. The change
        // tracker is cleared each attempt so a retry never re-saves stale state.
        var strategy = Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            ChangeTracker.Clear();
            await using var tx = await Database.BeginTransactionAsync(cancellationToken);
            var result = await action();
            if (result.Success) await tx.CommitAsync(cancellationToken);
            else await tx.RollbackAsync(cancellationToken);
            return result;
        });
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<StaffProfile> StaffProfiles => Set<StaffProfile>();
    public DbSet<StudentProfile> StudentProfiles => Set<StudentProfile>();
    public DbSet<Course> Courses => Set<Course>();
    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<Class> Classes => Set<Class>();
    public DbSet<ClassResource> ClassResources => Set<ClassResource>();
    public DbSet<CurriculumTemplate> CurriculumTemplates => Set<CurriculumTemplate>();
    public DbSet<CurriculumModule> CurriculumModules => Set<CurriculumModule>();
    public DbSet<CurriculumUnit> CurriculumUnits => Set<CurriculumUnit>();
    public DbSet<CurriculumLesson> CurriculumLessons => Set<CurriculumLesson>();
    public DbSet<CurriculumPlanDay> CurriculumPlanDays => Set<CurriculumPlanDay>();
    public DbSet<CurriculumPlanDayLesson> CurriculumPlanDayLessons => Set<CurriculumPlanDayLesson>();
    public DbSet<LessonDefaultTask> LessonDefaultTasks => Set<LessonDefaultTask>();
    public DbSet<Exam> Exams => Set<Exam>();
    public DbSet<ExamSection> ExamSections => Set<ExamSection>();
    public DbSet<ExamResult> ExamResults => Set<ExamResult>();
    public DbSet<ExamSectionScore> ExamSectionScores => Set<ExamSectionScore>();
    public DbSet<Enrollment> Enrollments => Set<Enrollment>();
    public DbSet<ClassSession> ClassSessions => Set<ClassSession>();
    public DbSet<ClassSessionLesson> ClassSessionLessons => Set<ClassSessionLesson>();
    public DbSet<ClassSchedulePattern> ClassSchedulePatterns => Set<ClassSchedulePattern>();
    public DbSet<Attendance> Attendance => Set<Attendance>();
    public DbSet<Assignment> Assignments => Set<Assignment>();
    public DbSet<Submission> Submissions => Set<Submission>();
    public DbSet<SubmissionFile> SubmissionFiles => Set<SubmissionFile>();
    public DbSet<SubmissionAudit> SubmissionAudits => Set<SubmissionAudit>();
    public DbSet<LessonMaterial> LessonMaterials => Set<LessonMaterial>();
    public DbSet<LessonProgress> LessonProgress => Set<LessonProgress>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Punishment> Punishments => Set<Punishment>();
    public DbSet<TeacherSalaryConfig> TeacherSalaryConfigs => Set<TeacherSalaryConfig>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationParticipant> ConversationParticipants => Set<ConversationParticipant>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<Badge> Badges => Set<Badge>();
    public DbSet<StudentBadge> StudentBadges => Set<StudentBadge>();
    public DbSet<XpLedger> XpLedger => Set<XpLedger>();
    public DbSet<ResultEntry> Results => Set<ResultEntry>();
    public DbSet<ResultScoreBreakdown> ResultScoreBreakdowns => Set<ResultScoreBreakdown>();
    public DbSet<ResultImage> ResultImages => Set<ResultImage>();
    public DbSet<ResultView> ResultViews => Set<ResultView>();
    public DbSet<VisitorMessage> VisitorMessages => Set<VisitorMessage>();
    public DbSet<Book> Books => Set<Book>();
    public DbSet<AssignmentBook> AssignmentBooks => Set<AssignmentBook>();
    public DbSet<AssignmentFile> AssignmentFiles => Set<AssignmentFile>();
    public DbSet<AssignmentAssignee> AssignmentAssignees => Set<AssignmentAssignee>();
    public DbSet<LearningTask> LearningTasks => Set<LearningTask>();
    public DbSet<TaskSubmission> TaskSubmissions => Set<TaskSubmission>();
    public DbSet<LessonExercise> LessonExercises => Set<LessonExercise>();
    public DbSet<LessonExerciseSubmission> LessonExerciseSubmissions => Set<LessonExerciseSubmission>();
    public DbSet<ExerciseSet> ExerciseSets => Set<ExerciseSet>();
    public DbSet<ExerciseSetClass> ExerciseSetClasses => Set<ExerciseSetClass>();
    public DbSet<Reminder> Reminders => Set<Reminder>();
    public DbSet<Specialization> Specializations => Set<Specialization>();
    public DbSet<StaffSpecialization> StaffSpecializations => Set<StaffSpecialization>();
    public DbSet<Material> Materials => Set<Material>();
    public DbSet<MaterialClass> MaterialClasses => Set<MaterialClass>();
    public DbSet<OfficeInfo> OfficeInfo => Set<OfficeInfo>();
    public DbSet<Announcement> Announcements => Set<Announcement>();
    public DbSet<MarketingCourse> MarketingCourses => Set<MarketingCourse>();
    public DbSet<MarketingVideo> MarketingVideos => Set<MarketingVideo>();
    public DbSet<MockTestSlot> MockTestSlots => Set<MockTestSlot>();
    public DbSet<MockTestRegistration> MockTestRegistrations => Set<MockTestRegistration>();
    public DbSet<TelegramAccount> TelegramAccounts => Set<TelegramAccount>();
    public DbSet<TelegramSettings> TelegramSettings => Set<TelegramSettings>();
    public DbSet<TelegramDeepLinkToken> TelegramDeepLinkTokens => Set<TelegramDeepLinkToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(LMSDbContext).Assembly);
        SeedData.Apply(modelBuilder);
        base.OnModelCreating(modelBuilder);
    }
}
