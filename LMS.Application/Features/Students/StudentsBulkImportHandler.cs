using System.Net.Mail;
using LMS.Application.Common.Abstractions;
using LMS.Application.Common.Models;
using LMS.Application.Common.Security;
using LMS.Domain.Entities;
using LMS.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
// Disambiguate from the UserStatus-adjacent Enums.UserRole enum.
using UserRole = LMS.Domain.Entities.UserRole;

namespace LMS.Application.Features.Students;

/// <summary>
/// Bulk-enrols students into a class from a list of emails (parsed from the
/// uploaded workbook by the controller). Rules:
///   • Empty rows are ignored before this handler runs (blank cells dropped).
///   • Duplicate emails within the batch are flagged Failed (only the first is
///     processed).
///   • An existing user is reused — never re-created — and simply enrolled.
///   • An unknown email provisions a new student (Student role + profile) with a
///     generated password, then enrols them.
///   • Each row runs in its OWN transaction so a mid-row failure (e.g. a full
///     class) rolls back just that row and processing continues.
/// </summary>
public sealed class BulkImportStudentsCommandHandler(IApplicationDbContext db, IPasswordHasher hasher)
    : IRequestHandler<BulkImportStudentsCommand, Result<BulkImportStudentsResult>>
{
    public async Task<Result<BulkImportStudentsResult>> Handle(
        BulkImportStudentsCommand request, CancellationToken ct)
    {
        var cls = await db.Classes.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == request.ClassId, ct);
        if (cls is null)
            return Result<BulkImportStudentsResult>.Fail("NOT_FOUND", "Class not found.");

        var studentRole = await db.Roles.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Code == RoleCodes.Student, ct);
        if (studentRole is null)
            return Result<BulkImportStudentsResult>.Fail("CONFIG", "Student role is not configured.");

        var rows = new List<BulkImportStudentRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in request.Emails)
        {
            var email = raw?.Trim().ToLowerInvariant() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(email)) continue; // ignore empty rows

            if (!seen.Add(email))
            {
                rows.Add(new BulkImportStudentRow(email, null, BulkImportStatus.Failed, "Duplicate email in file."));
                continue;
            }

            if (!IsValidEmail(email))
            {
                rows.Add(new BulkImportStudentRow(email, null, BulkImportStatus.Failed, "Invalid email format."));
                continue;
            }

            BulkImportStudentRow row;
            try
            {
                // Per-row transaction: the helper clears the change tracker and
                // commits only on a successful Result, so a failed row rolls back
                // cleanly and never leaks half-created entities into the next row.
                var outcome = await db.ExecuteInTransactionAsync(
                    () => ProcessOneAsync(request.ClassId, cls.MaxStudents, email, studentRole.Id, ct), ct);

                row = outcome.Success
                    ? outcome.Data!
                    : new BulkImportStudentRow(email, null, BulkImportStatus.Failed, outcome.Message ?? "Failed.");
            }
            catch (Exception ex)
            {
                row = new BulkImportStudentRow(email, null, BulkImportStatus.Failed, Summarize(ex));
            }

            rows.Add(row);
        }

        var created = rows.Count(r => r.Status == BulkImportStatus.Created);
        var existing = rows.Count(r => r.Status == BulkImportStatus.ExistingUserAdded);
        var failed = rows.Count(r => r.Status == BulkImportStatus.Failed);

        return Result<BulkImportStudentsResult>.Ok(
            new BulkImportStudentsResult(rows.Count, created, existing, failed, rows),
            $"Processed {rows.Count} row(s): {created} created, {existing} existing added, {failed} failed.");
    }

    /// <summary>
    /// Processes a single email inside the ambient transaction. Returns a success
    /// Result (commit) carrying the Created / Existing row, or a failure Result
    /// (rollback) whose message becomes the row's failure reason.
    /// </summary>
    private async Task<Result<BulkImportStudentRow>> ProcessOneAsync(
        Guid classId, int maxStudents, string email, Guid studentRoleId, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

        if (user is not null)
        {
            // Reuse the existing account — never create a second user.
            var isStaff = await db.StaffProfiles.AnyAsync(s => s.UserId == user.Id, ct);
            if (isStaff)
                return Result<BulkImportStudentRow>.Fail("ROW", "User exists as a staff member — not enrolled as a student.");

            var profile = await db.StudentProfiles.FirstOrDefaultAsync(s => s.UserId == user.Id, ct);
            if (profile is null)
            {
                profile = new StudentProfile(user.Id, user);
                await db.StudentProfiles.AddAsync(profile, ct);
            }
            // Ensure the Student role so the account behaves as a student.
            if (!await db.UserRoles.AnyAsync(ur => ur.UserId == user.Id && ur.RoleId == studentRoleId, ct))
                await db.UserRoles.AddAsync(new UserRole(user.Id, studentRoleId), ct);

            var enrolled = await EnrollAsync(classId, maxStudents, profile.Id, ct);
            if (!enrolled.Success)
                return Result<BulkImportStudentRow>.Fail(enrolled.ErrorCode ?? "ROW", enrolled.Message ?? "Enrollment failed.");

            await db.SaveChangesAsync(ct);
            return Result<BulkImportStudentRow>.Ok(
                new BulkImportStudentRow(email, null, BulkImportStatus.ExistingUserAdded, enrolled.Message));
        }

        // Brand-new student: create account + profile + role, then enrol.
        var password = PasswordGenerator.Generate();
        var newUser = new User(email, hasher.Hash(password));
        await db.Users.AddAsync(newUser, ct);
        await db.UserRoles.AddAsync(new UserRole(newUser.Id, studentRoleId), ct);
        var newProfile = new StudentProfile(newUser.Id, newUser);
        await db.StudentProfiles.AddAsync(newProfile, ct);

        var newEnroll = await EnrollAsync(classId, maxStudents, newProfile.Id, ct);
        if (!newEnroll.Success)
            return Result<BulkImportStudentRow>.Fail(newEnroll.ErrorCode ?? "ROW", newEnroll.Message ?? "Enrollment failed.");

        await db.SaveChangesAsync(ct);
        return Result<BulkImportStudentRow>.Ok(
            new BulkImportStudentRow(email, password, BulkImportStatus.Created, null));
    }

    /// <summary>
    /// Enrolment mirroring <see cref="LMS.Application.Features.Classes"/>' rules:
    /// respects the class cap, reactivates a dropped row instead of inserting a
    /// duplicate, and is idempotent for an already-active student. Adds the row to
    /// the context but leaves the SaveChanges to the caller (one commit per row).
    /// </summary>
    private async Task<Result> EnrollAsync(Guid classId, int maxStudents, Guid studentProfileId, CancellationToken ct)
    {
        var existing = await db.Enrollments
            .FirstOrDefaultAsync(e => e.ClassId == classId && e.StudentProfileId == studentProfileId, ct);
        if (existing is not null)
        {
            if (existing.Status == EnrollmentStatus.Active)
                return Result.Ok("Already enrolled.");
            existing.Activate();
            return Result.Ok("Re-enrolled.");
        }

        var activeCount = await db.Enrollments
            .CountAsync(e => e.ClassId == classId && e.Status == EnrollmentStatus.Active, ct);
        if (activeCount >= maxStudents)
            return Result.Fail("FULL", "Class is full.");

        var enrollment = Enrollment.Create(classId, studentProfileId, Array.Empty<Enrollment>());
        await db.Enrollments.AddAsync(enrollment, ct);
        return Result.Ok(null);
    }

    private static bool IsValidEmail(string email)
    {
        // Reject the obvious (spaces, missing @) then defer to the framework parser.
        if (email.Contains(' ') || !email.Contains('@')) return false;
        try
        {
            var addr = new MailAddress(email);
            return addr.Address == email;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    // Keep the report reason short + safe — never surface a raw stack trace.
    private static string Summarize(Exception ex) =>
        ex is Domain.Exceptions.DomainException ? ex.Message : "Unexpected error while processing this row.";
}
