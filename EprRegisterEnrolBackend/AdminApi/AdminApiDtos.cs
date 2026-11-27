using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.AdminApi;

/// <summary>RA-751: one item per requested work item, in the order requested.</summary>
public sealed record LegacyApplicationStatusesDto(IReadOnlyList<LegacyApplicationStatusDto> Items);

/// <summary>
/// RA-751: the Legacy application linked to a work item. <see cref="Found"/> is false, with no
/// application id or status, when no Legacy application is linked to it.
/// </summary>
public sealed record LegacyApplicationStatusDto(
    Guid WorkItemId,
    bool Found,
    string? ApplicationId,
    ApplicationStatus? ApplicationStatus
);

/// <summary>RA-751: <c>CaseManagementStatusMapping</c>, as the admin API serves it.</summary>
public sealed record CaseManagementStatusMappingDto(
    IReadOnlyList<StatusPushMappingDto> StatusPush,
    QueryPushMappingDto QueryPush
);

public sealed record StatusPushMappingDto(string StateId, ApplicationStatus ApplicationStatus);

public sealed record QueryPushMappingDto(ApplicationStatus ApplicationStatus);
