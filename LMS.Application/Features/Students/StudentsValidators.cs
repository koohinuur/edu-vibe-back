using FluentValidation;

namespace LMS.Application.Features.Students;

public sealed class RegisterStudentCommandValidator : AbstractValidator<RegisterStudentCommand>
{
    public RegisterStudentCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
    }
}

public sealed class UpdateStudentProfileCommandValidator : AbstractValidator<UpdateStudentProfileCommand>
{
    public UpdateStudentProfileCommandValidator()
    {
        RuleFor(x => x.StudentProfileId).NotEmpty();
        RuleFor(x => x.Xp).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Streak).GreaterThanOrEqualTo(0);
    }
}

public sealed class GetStudentDetailQueryValidator : AbstractValidator<GetStudentDetailQuery>
{
    public GetStudentDetailQueryValidator()
    {
        RuleFor(x => x.StudentProfileId).NotEmpty();
    }
}

public sealed class BulkImportStudentsCommandValidator : AbstractValidator<BulkImportStudentsCommand>
{
    public BulkImportStudentsCommandValidator()
    {
        RuleFor(x => x.ClassId).NotEmpty();
        RuleFor(x => x.Rows).NotNull();
    }
}