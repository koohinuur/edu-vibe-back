using LMS.Application.Common.Models;
using LMS.Domain.Entities;
using MediatR;

namespace LMS.Application.Features.MarketingCms;

public sealed record MockTestSlotDto(
    Guid Id,
    string Title,
    DateTime StartsAt,
    string? DurationText,
    int Capacity,
    int AvailableSeats,
    int SortOrder,
    bool IsActive);

/// <summary>Admin listing — pass <c>onlyActive</c> to preview what the marketing site shows.</summary>
public sealed record GetMockTestSlotsQuery(bool OnlyActive = false)
    : IRequest<Result<IReadOnlyCollection<MockTestSlotDto>>>;

/// <summary>Public list: active + upcoming only, soonest first.</summary>
public sealed record GetPublicMockTestSlotsQuery
    : IRequest<Result<IReadOnlyCollection<MockTestSlotDto>>>;

public sealed record CreateMockTestSlotCommand(
    string Title, DateTime StartsAt, string? DurationText,
    int Capacity, int AvailableSeats, int SortOrder, bool IsActive)
    : IRequest<Result<MockTestSlotDto>>;

public sealed record UpdateMockTestSlotCommand(
    Guid SlotId,
    string Title, DateTime StartsAt, string? DurationText,
    int Capacity, int AvailableSeats, int SortOrder, bool IsActive)
    : IRequest<Result<MockTestSlotDto>>;

public sealed record DeleteMockTestSlotCommand(Guid SlotId) : IRequest<Result>;

// ---- Registration + results -------------------------------------------------

public sealed record MockTestRegistrationDto(
    Guid Id,
    Guid SlotId,
    string FullName,
    string? Phone,
    string? Email,
    Guid? StudentProfileId,
    DateTime RegisteredAt,
    decimal? Listening,
    decimal? Reading,
    decimal? Writing,
    decimal? Speaking,
    decimal? Overall,
    string? ResultNotes,
    MockTestAttendance Attendance);

/// <summary>
/// Register for a slot. Public leads pass name + phone/email; a logged-in student
/// is linked automatically (StudentProfileId resolved from the JWT). SlotId is
/// taken from the route.
/// </summary>
public sealed record RegisterForMockTestCommand(Guid SlotId, string FullName, string? Phone, string? Email)
    : IRequest<Result<MockTestRegistrationDto>>;

/// <summary>Admin: everyone registered for a slot (newest first).</summary>
public sealed record GetMockTestRegistrationsQuery(Guid SlotId)
    : IRequest<Result<IReadOnlyCollection<MockTestRegistrationDto>>>;

/// <summary>Admin: attach/update the per-section scores + overall band for a registration.</summary>
public sealed record SetMockTestResultCommand(
    Guid RegistrationId,
    decimal? Listening,
    decimal? Reading,
    decimal? Writing,
    decimal? Speaking,
    decimal? Overall,
    string? Notes) : IRequest<Result<MockTestRegistrationDto>>;

/// <summary>Admin: mark a registrant attended / no-show (or back to unknown).</summary>
public sealed record SetMockTestAttendanceCommand(Guid RegistrationId, MockTestAttendance Status)
    : IRequest<Result<MockTestRegistrationDto>>;

/// <summary>Admin: remove a registration from a slot's roster.</summary>
public sealed record DeleteMockTestRegistrationCommand(Guid RegistrationId) : IRequest<Result>;
