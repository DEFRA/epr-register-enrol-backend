using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using FluentValidation;

namespace EprRegisterEnrolBackend.AccreditationApplication.Validators;

public class QueryFromCaseManagementRequestValidator
    : AbstractValidator<QueryFromCaseManagementRequest>
{
    public QueryFromCaseManagementRequestValidator()
    {
        RuleFor(r => r.SectionKeys).NotEmpty().WithMessage("At least one section key is required.");

        RuleForEach(r => r.SectionKeys)
            .Must(key => AccreditationApplicationSections.AllCmSectionKeys.Contains(key))
            .WithMessage("Section key is not one of the recognised CM section keys.");
    }
}
