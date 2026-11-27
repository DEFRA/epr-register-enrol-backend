using FluentValidation;

namespace EprRegisterEnrolBackend.AccreditationApplication.Validators;

/// <summary>
/// The ORS journey's rules for each overseas site detail, shared by <see cref="OverseasSiteRequestValidatorBase{T}"/>
/// and RA-682's in-year update PATCH (<c>PatchChangeRequestSiteRequestValidator</c>), so the two can't drift apart.
/// A caller adds its own condition, such as checking a PATCH field only when it is sent.
/// </summary>
internal static class OverseasSiteFieldRules
{
    public static IRuleBuilderOptions<T, string?> OverseasSiteName<T>(this IRuleBuilder<T, string?> rule) =>
        rule.NotEmpty().MaximumLength(200);

    public static IRuleBuilderOptions<T, string?> OverseasSiteAddressLine1<T>(this IRuleBuilder<T, string?> rule) =>
        rule.NotEmpty().MaximumLength(200);

    public static IRuleBuilderOptions<T, string?> OverseasSiteAddressLine2<T>(this IRuleBuilder<T, string?> rule) =>
        rule.MaximumLength(200);

    public static IRuleBuilderOptions<T, string?> OverseasSiteTownOrCity<T>(this IRuleBuilder<T, string?> rule) =>
        rule.NotEmpty().MaximumLength(100);

    public static IRuleBuilderOptions<T, string?> OverseasSiteCountry<T>(this IRuleBuilder<T, string?> rule) =>
        rule.NotEmpty().MaximumLength(100);

    public static IRuleBuilderOptions<T, string?> OverseasSiteContactName<T>(this IRuleBuilder<T, string?> rule) =>
        rule.NotEmpty().MaximumLength(200);

    public static IRuleBuilderOptions<T, string?> OverseasSiteContactEmail<T>(this IRuleBuilder<T, string?> rule) =>
        rule.NotEmpty()
            .MaximumLength(254)
            .Matches(OverseasSiteEmail.Pattern)
            .WithMessage("ContactEmail must be a valid email address.");

    public static IRuleBuilderOptions<T, string?> OverseasSiteContactPhone<T>(this IRuleBuilder<T, string?> rule) =>
        rule.MaximumLength(30);

    public static IRuleBuilderOptions<T, string?> OverseasSiteRepatriatedLoads<T>(this IRuleBuilder<T, string?> rule) =>
        rule.NotEmpty().MaximumLength(5000);
}
