using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.Entities;

namespace EprRegisterEnrolBackend.AdminApi.OperatorJourney;

// RA-753: one search result row per record type (docs/openapi/admin-api.yaml). Each carries what the
// Admin UI shows (AC09): the record's id and reference, status, organisation and last changed date. No
// contact details, addresses or users: the one personal-data field is the organisation's name (AC14).

/// <summary>The organisation a record belongs to, as <c>organisations</c> holds it now.</summary>
public sealed record OperatorJourneyOrganisationDto(string Id, string? Name, int? Number);

public sealed record OperatorJourneyAccreditationDto(
    string Id,
    string? AccreditationNumber,
    AccreditationStatus Status,
    int Year,
    TonnageBand? TonnageBand,
    string RegistrationId,
    string? RegistrationNumber,
    MaterialType Material,
    OperatorJourneyOrganisationDto Organisation,
    DateTime UpdatedAt
);

public sealed record OperatorJourneyApplicationDto(
    string Id,
    string? ApplicationReference,
    string? AccreditationNumber,
    ApplicationStatus Status,
    int Year,
    string? RegistrationId,
    string? RegistrationNumber,
    MaterialType? Material,
    Nation? Nation,
    OperatorJourneyOrganisationDto Organisation,
    DateTime UpdatedAt
);

public sealed record OperatorJourneyRegistrationDto(
    string Id,
    string? RegistrationNumber,
    RegistrationStatus Status,
    MaterialType Material,
    Nation? Nation,
    WasteProcessingKind WasteProcessingType,
    OperatorJourneyOrganisationDto Organisation,
    DateTime UpdatedAt
);

public sealed record OperatorJourneyOrganisationItemDto(
    string Id,
    OperatorJourneyOrganisationDto Organisation,
    int RegistrationCount,
    DateTime UpdatedAt
);

public sealed record OperatorJourneyOverseasSiteDto(
    string Id,
    string? OrsId,
    string? SiteName,
    string? Country,
    OverseasSiteStatus Status,
    string RegistrationId,
    string? RegistrationNumber,
    OperatorJourneyOrganisationDto Organisation,
    DateTime UpdatedAt
);

/// <summary>The countries current overseas sites hold, for the <c>country</c> filter.</summary>
public sealed record OverseasSiteCountriesDto(IReadOnlyList<string> Items);
