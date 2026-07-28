using EprRegisterEnrolBackend.AccreditationApplication.Models;
using FluentValidation;

namespace EprRegisterEnrolBackend.AccreditationApplication.Validators;

public class WithdrawRequestValidator : AbstractValidator<WithdrawRequest>
{
    private const int MaxReasonWords = 200;

    public WithdrawRequestValidator()
    {
        RuleFor(r => r.Reason)
            .NotEmpty()
            .WithMessage("Enter a reason for the withdrawal.")
            .Must(HaveAtMostMaxReasonWords)
            .WithMessage("Reason must be 200 words or fewer.");
    }

    private static bool HaveAtMostMaxReasonWords(string reason) =>
        reason.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length <= MaxReasonWords;
}
