using LMS.Application.Common.Models;
using MediatR;

namespace LMS.Application.Features.Auth;

public sealed record AuthPingCommand : IRequest<Result<string>>;

public sealed class AuthPingCommandHandler : IRequestHandler<AuthPingCommand, Result<string>>
{
    public Task<Result<string>> Handle(AuthPingCommand request, CancellationToken cancellationToken)
    {
        return Task.FromResult(Result<string>.Ok("Auth module ready"));
    }
}

public sealed record AuthTokensResponse(
    Guid UserId,
    string Email,
    string AccessToken,
    string RefreshToken,
    IReadOnlyCollection<string> Roles,
    IReadOnlyCollection<string> Permissions);

public sealed record RegisterUserCommand(string Email, string Password, string RoleCode, string? Phone = null)
    : IRequest<Result<AuthTokensResponse>>;

public sealed record LoginCommand(string Email, string Password) : IRequest<Result<AuthTokensResponse>>;

public sealed record RefreshTokenCommand(string RefreshToken) : IRequest<Result<AuthTokensResponse>>;

public sealed record AssignRoleCommand(Guid UserId, string RoleCode) : IRequest<Result>;

/// <summary>
/// Self-service password change. The current user id is taken from
/// <see cref="ICurrentUserService"/> on purpose — a request body shouldn't
/// be able to nominate a different user. Returns <see cref="Result"/> with
/// a one-line message; the new tokens are NOT issued here, the front-end
/// just calls /refresh (or re-logs in) after a successful change.
/// </summary>
public sealed record ChangePasswordCommand(string CurrentPassword, string NewPassword)
    : IRequest<Result>;