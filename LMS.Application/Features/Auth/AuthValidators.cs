using FluentValidation;

namespace LMS.Application.Features.Auth;

public sealed class RegisterUserCommandValidator : AbstractValidator<RegisterUserCommand>
{
    public RegisterUserCommandValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Password).NotEmpty().MinimumLength(8);
        RuleFor(x => x.RoleCode).NotEmpty();
    }
}

public sealed class LoginCommandValidator : AbstractValidator<LoginCommand>
{
    public LoginCommandValidator()
    {
        // Identifier can be an email or a phone, so don't force email format here
        // — just require something. Credential mismatches return "invalid".
        RuleFor(x => x.Email).NotEmpty();
        // Login must NOT enforce complexity/length — that belongs on register /
        // change-password. Requiring a minimum length here rejects any account
        // whose real password is shorter with a confusing 400 instead of letting
        // the credential check return a proper "invalid credentials". Only require
        // that a password was actually supplied.
        RuleFor(x => x.Password).NotEmpty();
    }
}

public sealed class RefreshTokenCommandValidator : AbstractValidator<RefreshTokenCommand>
{
    public RefreshTokenCommandValidator()
    {
        RuleFor(x => x.RefreshToken).NotEmpty();
    }
}