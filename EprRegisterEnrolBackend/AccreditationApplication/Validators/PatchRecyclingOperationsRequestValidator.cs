using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.Utils;
using FluentValidation;

namespace EprRegisterEnrolBackend.AccreditationApplication.Validators;

// RA-469 AC10/AC12: stateless, request-shape-only checks - this validator has no access to the
// application's MaterialType, so it cannot enforce the material-type-applicability rule; that,
// and the InterimSite-presence rule (AC11), are checked in the endpoint once the application/site
// are loaded (see AccreditationApplicationEndpoints.PatchRecyclingOperations).
public class PatchRecyclingOperationsRequestValidator
    : AbstractValidator<PatchRecyclingOperationsRequest>
{
    public PatchRecyclingOperationsRequestValidator()
    {
        RuleFor(r => r.OperationCodes).NotEmpty().WithMessage("OperationCodes must not be empty.");
        RuleForEach(r => r.OperationCodes)
            .Must(c => RecyclingOperationCodes.AllCodes.Contains(c))
            .WithMessage(
                $"OperationCodes must each be one of: {string.Join(", ", RecyclingOperationCodes.AllCodes)}."
            );
        RuleFor(r => r.OperationCodes)
            .Must(codes => !RecyclingOperationCodes.RequiresAccompanyingCode(codes))
            .WithMessage(
                "R12 and R13 cannot be selected in isolation and must be accompanied by at least one other applicable R code."
            );
    }
}
