using LMS.Application.Common.Models;
using MediatR;

namespace LMS.Application.Features.Xp;

public sealed record XpLedgerDto(
    Guid Id,
    Guid StudentProfileId,
    int Amount,
    string SourceType,
    string? Note,
    DateTime CreatedAt);

public sealed record LeaderboardDto(Guid StudentProfileId, string? Name, string? Level, int Streak, int Xp);

public sealed record AddManualXpCommand(Guid StudentProfileId, int Amount, string? Note) : IRequest<Result>;

public sealed record GetStudentXpLedgerQuery(Guid StudentProfileId)
    : IRequest<Result<IReadOnlyCollection<XpLedgerDto>>>;

/// <summary>Top students by XP. Optionally scoped to one class (enrolled, non-dropped).</summary>
public sealed record GetLeaderboardQuery(int Top = 10, Guid? ClassId = null)
    : IRequest<Result<IReadOnlyCollection<LeaderboardDto>>>;