using EprRegisterEnrolBackend.AccreditationApplication.Models;
using FluentValidation;

namespace EprRegisterEnrolBackend.AccreditationApplication.Validators;

public class SubmitRequestValidator : AbstractValidator<SubmitRequest>
{
    public SubmitRequestValidator()
    {
        RuleFor(r => r.FullName).NotEmpty().WithMessage("Full name is required.");
        RuleFor(r => r.JobTitle).NotEmpty().WithMessage("Job title is required.");
    }
}
