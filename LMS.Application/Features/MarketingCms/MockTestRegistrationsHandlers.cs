using LMS.Application.Common.Abstractions;
using LMS.Application.Common.Models;
using LMS.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LMS.Application.Features.MarketingCms;

/// <summary>
/// Mock-test registration + results. Registering is open to anyone (public leads
/// and logged-in students, the latter linked via their profile). Listing and
/// setting results are admin-only (gated at the controller with Marketing.Manage).
/// </summary>
public sealed class MockTestRegistrationsHandlers(IApplicationDbContext db, ICurrentUserService currentUser) :
    IRequestHandler<RegisterForMockTestCommand, Result<MockTestRegistrationDto>>,
    IRequestHandler<GetMockTestRegistrationsQuery, Result<IReadOnlyCollection<MockTestRegistrationDto>>>,
    IRequestHandler<SetMockTestResultCommand, Result<MockTestRegistrationDto>>,
    IRequestHandler<SetMockTestAttendanceCommand, Result<MockTestRegistrationDto>>,
    IRequestHandler<DeleteMockTestRegistrationCommand, Result>
{
    public async Task<Result<MockTestRegistrationDto>> Handle(RegisterForMockTestCommand request, CancellationToken ct)
    {
        var slot = await db.MockTestSlots.FirstOrDefaultAsync(s => s.Id == request.SlotId, ct);
        if (slot is null)
            return Result<MockTestRegistrationDto>.Fail("NOT_FOUND", "Mock test not found.");
        if (!slot.IsActive)
            return Result<MockTestRegistrationDto>.Fail("CLOSED", "This mock test isn't open for registration.");

        // One registration per phone per slot. The booking widget can fire more
        // than once (double-tap, retry, homepage + mock page both submitting), so
        // this pre-check reuses an existing registration for the same phone
        // (matched on the last 9 digits). A DB unique index on (SlotId, phone
        // digits) is the race-safe backstop for near-simultaneous double-submits.
        var digits = new string((request.Phone ?? string.Empty).Where(char.IsDigit).ToArray());
        var tail = digits.Length >= 9 ? digits[^9..] : digits;

        async Task<MockTestRegistration?> FindExistingAsync() =>
            tail.Length >= 7
                ? await db.MockTestRegistrations.AsNoTracking().FirstOrDefaultAsync(
                    r => r.SlotId == slot.Id && r.Phone != null && r.Phone.Contains(tail), ct)
                : null;

        var dup = await FindExistingAsync();
        if (dup is not null)
            return Result<MockTestRegistrationDto>.Ok(ToDto(dup), "Already registered.");

        // A logged-in student is linked to their profile; anonymous visitors register as leads.
        var reg = new MockTestRegistration(
            slot.Id, request.FullName, request.Phone, request.Email, currentUser.StudentProfileId);
        await db.MockTestRegistrations.AddAsync(reg, ct);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost a race with a concurrent identical registration (unique index).
            // Return the winner instead of erroring.
            var winner = await FindExistingAsync();
            if (winner is not null)
                return Result<MockTestRegistrationDto>.Ok(ToDto(winner), "Already registered.");
            throw;
        }
        return Result<MockTestRegistrationDto>.Ok(ToDto(reg), "Registered.");
    }

    public async Task<Result<IReadOnlyCollection<MockTestRegistrationDto>>> Handle(
        GetMockTestRegistrationsQuery request, CancellationToken ct)
    {
        var rows = await db.MockTestRegistrations.AsNoTracking()
            .Where(r => r.SlotId == request.SlotId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(ct);
        return Result<IReadOnlyCollection<MockTestRegistrationDto>>.Ok(rows.Select(ToDto).ToList());
    }

    public async Task<Result<MockTestRegistrationDto>> Handle(SetMockTestResultCommand request, CancellationToken ct)
    {
        var reg = await db.MockTestRegistrations.FirstOrDefaultAsync(r => r.Id == request.RegistrationId, ct);
        if (reg is null)
            return Result<MockTestRegistrationDto>.Fail("NOT_FOUND", "Registration not found.");

        reg.SetResult(request.Listening, request.Reading, request.Writing,
            request.Speaking, request.Overall, request.Notes);
        await db.SaveChangesAsync(ct);
        return Result<MockTestRegistrationDto>.Ok(ToDto(reg), "Result saved.");
    }

    public async Task<Result<MockTestRegistrationDto>> Handle(SetMockTestAttendanceCommand request, CancellationToken ct)
    {
        var reg = await db.MockTestRegistrations.FirstOrDefaultAsync(r => r.Id == request.RegistrationId, ct);
        if (reg is null)
            return Result<MockTestRegistrationDto>.Fail("NOT_FOUND", "Registration not found.");

        reg.SetAttendance(request.Status);
        await db.SaveChangesAsync(ct);
        return Result<MockTestRegistrationDto>.Ok(ToDto(reg), "Attendance saved.");
    }

    public async Task<Result> Handle(DeleteMockTestRegistrationCommand request, CancellationToken ct)
    {
        var reg = await db.MockTestRegistrations.FirstOrDefaultAsync(r => r.Id == request.RegistrationId, ct);
        if (reg is null)
            return Result.Fail("NOT_FOUND", "Registration not found.");

        db.MockTestRegistrations.Remove(reg);
        await db.SaveChangesAsync(ct);
        return Result.Ok("Registration removed.");
    }

    private static MockTestRegistrationDto ToDto(MockTestRegistration r) => new(
        r.Id, r.SlotId, r.FullName, r.Phone, r.Email, r.StudentProfileId, r.CreatedAt,
        r.Listening, r.Reading, r.Writing, r.Speaking, r.Overall, r.ResultNotes, r.Attendance);
}
