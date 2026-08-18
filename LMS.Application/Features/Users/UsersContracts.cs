using LMS.Application.Common.Models;
using LMS.Domain.Enums;
using MediatR;

namespace LMS.Application.Features.Users;

public sealed record UserDto(Guid Id, string Email, UserStatus Status, IReadOnlyCollection<string> Roles);

public sealed record GetUsersQuery(int Page = 1, int PageSize = 25, string? Search = null)
    : IRequest<Result<PagedResult<UserDto>>>;

public sealed record GetUserByIdQuery(Guid UserId) : IRequest<Result<UserDto>>;

public sealed record CreateUserCommand(string Email, string Password, UserStatus Status, string? Phone = null) : IRequest<Result<UserDto>>;

public sealed record UpdateUserCommand(Guid UserId, string Email, UserStatus Status) : IRequest<Result<UserDto>>;

public sealed record DeactivateUserCommand(Guid UserId) : IRequest<Result>;

/// <summary>Returns the currently authenticated user.</summary>
public sealed record GetMyUserQuery : IRequest<Result<UserDto>>;

/// <summary>Updates the currently authenticated user's profile.</summary>
public sealed record UpdateMyUserCommand(string Email) : IRequest<Result<UserDto>>;

/// <summary>Lets the current user change their own password.</summary>
public sealed record ChangeMyPasswordCommand(string CurrentPassword, string NewPassword) : IRequest<Result>;