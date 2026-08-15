using LMS.Application.Common.Security;
using LMS.Application.Features.Auth;
using LMS.WebApi.Common;
using LMS.WebApi.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace LMS.WebApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class AuthController(ISender sender) : ControllerBase
{
    [HttpGet("ping")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<string>>> Ping(CancellationToken cancellationToken)
    {
        var result = await sender.Send(new AuthPingCommand(), cancellationToken);
        return Ok(ApiResponse<string>.Ok(result.Data, result.Message));
    }

    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting("auth-anon")]
    public async Task<ActionResult<ApiResponse<AuthTokensResponse>>> Register([FromBody] RegisterUserCommand command,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(command, cancellationToken);
        if (!result.Success)
            return BadRequest(ApiResponse<AuthTokensResponse>.Fail(result.Message ?? "Register failed",
                result.ValidationErrors));
        return Ok(ApiResponse<AuthTokensResponse>.Ok(result.Data, "Registered"));
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth-anon")]
    public async Task<ActionResult<ApiResponse<AuthTokensResponse>>> Login([FromBody] LoginCommand command,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(command, cancellationToken);
        if (!result.Success)
            return Unauthorized(ApiResponse<AuthTokensResponse>.Fail(result.Message ?? "Login failed"));
        return Ok(ApiResponse<AuthTokensResponse>.Ok(result.Data, "Logged in"));
    }

    [HttpPost("forgot-password")]
    [AllowAnonymous]
    [EnableRateLimiting("auth-anon")]
    public async Task<ActionResult<ApiResponse<object>>> ForgotPassword(
        [FromBody] ForgotPasswordCommand command, CancellationToken cancellationToken)
    {
        var result = await sender.Send(command, cancellationToken);
        // Always 200 with a generic message — never reveal whether the account exists.
        return Ok(ApiResponse<object>.Ok(null, result.Message ?? "If eligible, a code was sent."));
    }

    [HttpPost("reset-password")]
    [AllowAnonymous]
    [EnableRateLimiting("auth-anon")]
    public async Task<ActionResult<ApiResponse<object>>> ResetPassword(
        [FromBody] ResetPasswordCommand command, CancellationToken cancellationToken)
    {
        var result = await sender.Send(command, cancellationToken);
        if (!result.Success)
            return BadRequest(ApiResponse<object>.Fail(result.Message ?? "Reset failed"));
        return Ok(ApiResponse<object>.Ok(null, result.Message ?? "Password reset"));
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting("auth-anon")]
    public async Task<ActionResult<ApiResponse<AuthTokensResponse>>> Refresh([FromBody] RefreshTokenCommand command,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(command, cancellationToken);
        if (!result.Success)
            return Unauthorized(ApiResponse<AuthTokensResponse>.Fail(result.Message ?? "Refresh failed"));
        return Ok(ApiResponse<AuthTokensResponse>.Ok(result.Data, "Token refreshed"));
    }

    /// <summary>
    /// Grants a role to a user. The handler enforces additional rules: only a
    /// SuperAdmin may grant SuperAdmin, and you can't change your own roles.
    /// </summary>
    [HttpPost("assign-role")]
    [PermissionAuthorize(Permissions.Auth.AssignRole)]
    public async Task<ActionResult<ApiResponse<object>>> AssignRole([FromBody] AssignRoleCommand command,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(command, cancellationToken);
        if (!result.Success) return BadRequest(ApiResponse<object>.Fail(result.Message ?? "Assign role failed"));
        return Ok(ApiResponse<object>.Ok(new { }, result.Message));
    }

    /// <summary>
    /// Self-service password change. Caller is identified from the JWT — the
    /// body only carries the current + new passwords. Auth rate-limit policy
    /// applies so a brute-force on the current password is throttled.
    /// </summary>
    [HttpPost("change-password")]
    [Authorize]
    [EnableRateLimiting("auth-anon")]
    public async Task<ActionResult<ApiResponse<object>>> ChangePassword(
        [FromBody] ChangePasswordCommand command, CancellationToken cancellationToken)
    {
        var result = await sender.Send(command, cancellationToken);
        if (!result.Success)
            return BadRequest(ApiResponse<object>.Fail(result.Message ?? "Change password failed"));
        return Ok(ApiResponse<object>.Ok(new { }, result.Message));
    }
}
