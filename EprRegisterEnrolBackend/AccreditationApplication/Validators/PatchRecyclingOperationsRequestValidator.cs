using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.Utils;
using FluentValidation;

namespace EprRegisterEnrolBackend.AccreditationApplication.Validators;

// RA-469 AC10/AC12, reworked by RA-486: stateless, request-shape-only checks - this validator has
// no access to the application's MaterialType, so it cannot enforce the material-type-
// applicability rule; that is checked in the endpoint once the application/site are loaded (see
// AccreditationApplicationEndpoints.PatchRecyclingOperations). RA-486 removed the old
// InterimSite-presence rule (AC11) entirely - R12/R13 no longer require an attached interim site.
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
            .Must(RecyclingOperationCodes.HasMandatoryOrsCode)
            .WithMessage(
                $"OperationCodes must include at least one of: {string.Join(", ", RecyclingOperationCodes.MaterialCodes)}."
            );
    }
}
