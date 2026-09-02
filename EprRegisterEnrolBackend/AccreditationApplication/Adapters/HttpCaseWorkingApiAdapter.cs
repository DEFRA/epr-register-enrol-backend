using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolBackend.AccreditationApplication.Adapters;

public class HttpCaseWorkingApiAdapter(
    IHttpClientFactory httpClientFactory,
    IOptions<CaseWorkingApiConfig> config,
    ILogger<HttpCaseWorkingApiAdapter> logger
) : ICaseWorkingApiAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly CaseWorkingApiConfig _config = config.Value;

    // Named HttpClient registered in Program.cs (carries the explicit 15s timeout).
    private const string DefaultHttpClientName = "DefaultClient";

    // The response body is attached via BeginScope under a dotted key, not interpolated into
    // the message template: CDP's OpenSearch ingestion pipeline only recognises the specific
    // slash-nested ECS paths on its allow-list (for http/response it's only body/bytes size
    // fields, never body content), so a literally-dotted key like "http.response.body" is
    // guaranteed to be dropped before it reaches OpenSearch, while still landing in the raw
    // S3-stored logs for troubleshooting. Interpolating the body into the message string
    // instead would defeat that filtering entirely, since `message` is unconditionally on the
    // allow-list regardless of content — and this adapter carries the full accreditation
    // application, so a validation error can echo submitted applicant data back verbatim.
    private void LogManagementBeError(LogLevel level, int status, string endpoint, string body)
    {
        if (!logger.IsEnabled(level))
        {
            return;
        }

        using var _ = logger.BeginScope(
            new Dictionary<string, object?> { ["http.response.body"] = body }
        );
        logger.Log(level, "ManagementBe returned {Status} from {Endpoint}", status, endpoint);
    }

    public async Task<CaseWorkingSubmissionResult> SubmitApplicationAsync(
        AccreditationApplicationModel application,
        CancellationToken cancellationToken = default
    )
    {
        var url = _config.Url;
        if (string.IsNullOrWhiteSpace(url))
        {
            logger.LogError("CaseWorking API URL is not configured. Cannot submit work item.");
            throw new InvalidOperationException("CaseWorking API URL is not configured.");
        }

        // RA-318: ManagementBe owns applicationReference generation — it is never
        // supplied by the caller (ManagementBe ignores any value sent here) and is
        // read back from its response below rather than generated locally.
        var body = new CreateWorkItemRequest
        {
            TypeId = "re-accreditation",
            Source = "operator-fe",
            Payload = BuildPayload(application),
        };

        var endpoint = $"{url.TrimEnd('/')}/work-items";
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Submitting work item to ManagementBe at {Endpoint} for applicationId={ApplicationId} org={OrganisationId}",
                endpoint,
                application.ApplicationId,
                application.OrganisationId
            );
        }

        var userId = application.SubmittedBy?.Email ?? application.OrganisationId;
        var userName = application.SubmittedBy?.FullName;
        using var request = BuildRequest(HttpMethod.Post, endpoint, body, userId, userName);
        var client = httpClientFactory.CreateClient(DefaultHttpClientName);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        // The "DefaultClient" HttpClient carries an explicit 15s Timeout (Program.cs) so this
        // call fails fast rather than hanging up to .NET's 100s default. HttpClient reports its
        // own timeout as a TaskCanceledException indistinguishable by type from a caller-driven
        // cancellation — the `when` clause is what tells them apart (mirrors ReExClient's same
        // distinction). Surfaced as a dedicated exception type so the Submit endpoint can return
        // a clear, distinguishable response instead of a generic unhandled-exception 500 (RA-311).
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(
                ex,
                "Timed out waiting for ManagementBe at {Endpoint} (client timeout exceeded)",
                endpoint
            );
            throw new CaseWorkingApiTimeoutException(
                $"Timed out waiting for ManagementBe at {endpoint}.",
                ex
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reach ManagementBe at {Endpoint}", endpoint);
            throw;
        }

        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            LogManagementBeError(LogLevel.Error, (int)response.StatusCode, endpoint, responseBody);
            throw new HttpRequestException(
                $"ManagementBe work item submission failed: {(int)response.StatusCode}"
            );
        }

        WorkItemResponseDto? result;
        try
        {
            result = await response.Content.ReadFromJsonAsync<WorkItemResponseDto>(
                JsonOptions,
                cancellationToken
            );
        }
        catch (Exception ex)
        {
            // Unlike workItemId below, applicationReference has no local fallback — it is
            // ManagementBe-generated, so a response we can't parse means we have no valid
            // reference to persist and the submission must fail rather than proceed silently.
            logger.LogError(
                ex,
                "Failed to parse ManagementBe work item response from {Endpoint}; cannot obtain application reference",
                endpoint
            );
            throw new HttpRequestException(
                $"Failed to parse ManagementBe work item response from {endpoint}.",
                ex
            );
        }

        if (string.IsNullOrWhiteSpace(result?.ApplicationReference))
        {
            logger.LogError(
                "ManagementBe response from {Endpoint} did not include an application reference",
                endpoint
            );
            throw new HttpRequestException(
                $"ManagementBe work item response from {endpoint} did not include an application reference."
            );
        }

        // Guid.Empty means the "id" field was absent from the response body (not a parse
        // failure — System.Text.Json leaves missing value-type properties at their default).
        // Unlike applicationReference, workItemId is only ever an optional correlation aid,
        // so a missing id must not fail the submission.
        Guid? workItemId = result.Id == Guid.Empty ? null : result.Id;

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Work item created: workItemId={WorkItemId} applicationReference={ApplicationReference}",
                workItemId,
                result.ApplicationReference
            );
        }

        return new CaseWorkingSubmissionResult(result.ApplicationReference, workItemId);
    }

    private static readonly NotificationStatusResult s_emptyNotificationStatus = new(null, null);

    public async Task<NotificationStatusResult> GetNotificationStatusAsync(
        AccreditationApplicationModel application,
        CancellationToken cancellationToken = default
    )
    {
        if (application.CaseManagementWorkItemId is not { } workItemId)
        {
            return s_emptyNotificationStatus;
        }

        var url = _config.Url;
        if (string.IsNullOrWhiteSpace(url))
        {
            logger.LogWarning(
                "CaseWorking API URL is not configured. Cannot look up notification status for workItemId={WorkItemId}.",
                workItemId
            );
            return s_emptyNotificationStatus;
        }

        var endpoint = $"{url.TrimEnd('/')}/work-items/{workItemId}";

        try
        {
            var userId = application.SubmittedBy?.Email ?? application.OrganisationId;
            var userName = application.SubmittedBy?.FullName;
            using var request = BuildRequest(
                HttpMethod.Get,
                endpoint,
                userId: userId,
                userName: userName
            );
            var client = httpClientFactory.CreateClient(DefaultHttpClientName);

            var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "ManagementBe returned {Status} from {Endpoint}; notification status will not be captured",
                    (int)response.StatusCode,
                    endpoint
                );
                return s_emptyNotificationStatus;
            }

            var detail = await response.Content.ReadFromJsonAsync<WorkItemDetailResponseDto>(
                JsonOptions,
                cancellationToken
            );
            return new NotificationStatusResult(
                NotificationStatusResolver.Resolve(detail?.AuditLog),
                detail?.SlaDueDate
            );
        }
        catch (Exception ex)
        {
            // Must never fail the caller's GetById response — the work item id is only ever
            // an optional correlation aid, not something the operator's own data depends on.
            logger.LogWarning(
                ex,
                "Failed to look up notification status from ManagementBe at {Endpoint}",
                endpoint
            );
            return s_emptyNotificationStatus;
        }
    }

    public async Task<ResumeFromQueryResult> ResumeFromQueryAsync(
        AccreditationApplicationModel application,
        QuerySubmitterContactDetails contactDetails,
        IReadOnlyList<string> sectionKeys,
        CancellationToken cancellationToken = default
    )
    {
        if (application.CaseManagementWorkItemId is not { } workItemId)
        {
            logger.LogError(
                "Cannot resume-from-query: applicationId={ApplicationId} has no CaseManagementWorkItemId.",
                application.ApplicationId
            );
            return new ResumeFromQueryResult(false);
        }

        var url = _config.Url;
        if (string.IsNullOrWhiteSpace(url))
        {
            logger.LogError(
                "CaseWorking API URL is not configured. Cannot resume-from-query for workItemId={WorkItemId}.",
                workItemId
            );
            return new ResumeFromQueryResult(false);
        }

        var body = new ResumeFromQueryRequest
        {
            ResponderContactDetails = new
            {
                fullName = contactDetails.FullName,
                email = contactDetails.Email,
                role = contactDetails.Role,
            },
            SectionKeys = sectionKeys,
            Sections = BuildSectionsPayload(application, sectionKeys),
            // RA-316: recomputed from the application as it stands now, not frozen at submit.
            // Answering a query can change the planned tonnage band or the overseas-site count,
            // and duly making happens after the query is answered — so a charge frozen at the
            // original submit would put a stale number in front of the regulator at exactly the
            // moment they act on it. Sent unconditionally (rather than only when it differs from
            // last time) because this adapter holds no memory of what it last sent; resending an
            // unchanged value is idempotent on the consumer.
            //
            // These are siblings of sectionKeys/sections, NOT entries in the sections dictionary:
            // the sections dictionary is keyed by section name and its projections must stay
            // identical to BuildPayload's (RA-292 AC04), so a pseudo-section here would break
            // that contract. The charge is a whole-application value, not a section.
            ChargeAmountPence = AccreditationChargeCalculator.CalculateChargePence(application),
            // RA-503: the operator's real bank payment reference (see BuildPayload), persisted on
            // the model since the original Submit and still available here. Falls back to
            // ApplicationReference (unlike at initial Submit, already populated by the time a
            // resume-from-query round trip happens) for an application submitted before this
            // field existed - PaymentReference is a brand-new field with no backfill, so without
            // this fallback a pre-existing application would silently regress from sending its
            // ApplicationReference (the old behaviour) to sending nothing at all.
            PaymentReference = application.PaymentReference ?? application.ApplicationReference,
        };

        var endpoint =
            $"{url.TrimEnd('/')}/work-items/re-accreditation/{workItemId}/resume-from-query";

        try
        {
            var userId = contactDetails.Email;
            var userName = contactDetails.FullName;
            using var request = BuildRequest(HttpMethod.Post, endpoint, body, userId, userName);
            var client = httpClientFactory.CreateClient(DefaultHttpClientName);

            var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                LogManagementBeError(
                    LogLevel.Error,
                    (int)response.StatusCode,
                    endpoint,
                    responseBody
                );
                return new ResumeFromQueryResult(false);
            }

            return new ResumeFromQueryResult(true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reach ManagementBe at {Endpoint}", endpoint);
            return new ResumeFromQueryResult(false);
        }
    }

    public async Task<WithdrawResult> WithdrawApplicationAsync(
        AccreditationApplicationModel application,
        QuerySubmitterContactDetails contactDetails,
        string reason,
        CancellationToken cancellationToken = default
    )
    {
        if (application.CaseManagementWorkItemId is not { } workItemId)
        {
            logger.LogError(
                "Cannot withdraw: applicationId={ApplicationId} has no CaseManagementWorkItemId.",
                application.ApplicationId
            );
            return new WithdrawResult(false);
        }

        var url = _config.Url;
        if (string.IsNullOrWhiteSpace(url))
        {
            logger.LogError(
                "CaseWorking API URL is not configured. Cannot withdraw workItemId={WorkItemId}.",
                workItemId
            );
            return new WithdrawResult(false);
        }

        var body = new WithdrawWorkItemRequest { Reason = reason };

        var endpoint = $"{url.TrimEnd('/')}/work-items/re-accreditation/{workItemId}/withdraw";

        try
        {
            var userId = contactDetails.Email;
            var userName = contactDetails.FullName;
            using var request = BuildRequest(HttpMethod.Post, endpoint, body, userId, userName);
            var client = httpClientFactory.CreateClient(DefaultHttpClientName);

            var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                LogManagementBeError(
                    LogLevel.Error,
                    (int)response.StatusCode,
                    endpoint,
                    responseBody
                );
                return new WithdrawResult(false);
            }

            return new WithdrawResult(true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reach ManagementBe at {Endpoint}", endpoint);
            return new WithdrawResult(false);
        }
    }

    // Body must always carry an explicit "siteNumber": null for ORS sites rather than omitting
    // the key — ManagementBe's contract distinguishes "absent" from "not applicable" (RA-294
    // AC05 / RA-297 AC04) — so this uses its own options without the shared JsonOptions'
    // WhenWritingNull behaviour.
    private static readonly JsonSerializerOptions SiteAddedJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task NotifySiteAddedAsync(
        AccreditationApplicationModel application,
        string siteType,
        string orsId,
        string? siteNumber,
        bool isNewSite,
        CancellationToken cancellationToken = default
    )
    {
        if (application.CaseManagementWorkItemId is not { } workItemId)
        {
            return;
        }

        var url = _config.Url;
        if (string.IsNullOrWhiteSpace(url))
        {
            logger.LogWarning(
                "CaseWorking API URL is not configured. Cannot notify site-added for workItemId={WorkItemId}.",
                workItemId
            );
            return;
        }

        var endpoint = $"{url.TrimEnd('/')}/work-items/re-accreditation/{workItemId}/site-added";
        var body = new SiteAddedRequest
        {
            SiteType = siteType,
            OrsId = orsId,
            SiteNumber = siteNumber,
            IsNewSite = isNewSite,
        };

        try
        {
            var userId = application.SubmittedBy?.Email ?? application.OrganisationId;
            var userName = application.SubmittedBy?.FullName;
            using var request = BuildRequest(
                HttpMethod.Post,
                endpoint,
                body,
                userId,
                userName,
                SiteAddedJsonOptions
            );
            var client = httpClientFactory.CreateClient(DefaultHttpClientName);

            var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                LogManagementBeError(
                    LogLevel.Warning,
                    (int)response.StatusCode,
                    endpoint,
                    responseBody
                );
            }
        }
        catch (Exception ex)
        {
            // Courtesy notification only, not part of the save transaction — must never fail
            // the caller, mirroring GetNotificationStatusAsync's "must never fail the caller"
            // convention (RA102-j7s).
            logger.LogWarning(
                ex,
                "Failed to notify ManagementBe of site-added at {Endpoint}",
                endpoint
            );
        }
    }

    private static Dictionary<string, object?> BuildSectionsPayload(
        AccreditationApplicationModel application,
        IReadOnlyList<string> sectionKeys
    )
    {
        var payload = new Dictionary<string, object?>();
        var sections = sectionKeys
            .Select(key =>
                AccreditationApplicationSections.TryMapCaseManagementKeyToSection(
                    key,
                    out var section
                )
                    ? section
                    : (OperatorSection?)null
            )
            .Where(section => section is not null)
            .Select(section => section!.Value)
            .Distinct();

        foreach (var section in sections)
        {
            payload[section.ToString()] = section switch
            {
                OperatorSection.Prns => BuildPrnsSection(application),
                OperatorSection.BusinessPlan => new
                {
                    newInfrastructurePercent = application.BusinessPlan.NewInfrastructurePercent,
                    priceSupportPercent = application.BusinessPlan.PriceSupportPercent,
                    businessCollectionsPercent = application
                        .BusinessPlan
                        .BusinessCollectionsPercent,
                    communicationsPercent = application.BusinessPlan.CommunicationsPercent,
                    newMarketsPercent = application.BusinessPlan.NewMarketsPercent,
                    newUsesPercent = application.BusinessPlan.NewUsesPercent,
                    otherPercent = application.BusinessPlan.OtherPercent,
                    newInfrastructureDetail = application.BusinessPlan.NewInfrastructureDetail,
                    priceSupportDetail = application.BusinessPlan.PriceSupportDetail,
                    businessCollectionsDetail = application.BusinessPlan.BusinessCollectionsDetail,
                    communicationsDetail = application.BusinessPlan.CommunicationsDetail,
                    newMarketsDetail = application.BusinessPlan.NewMarketsDetail,
                    newUsesDetail = application.BusinessPlan.NewUsesDetail,
                    otherDetail = application.BusinessPlan.OtherDetail,
                },
                OperatorSection.SamplingPlan => new
                {
                    files = application
                        .SamplingPlan.Files.Select(f => new
                        {
                            fileId = f.FileId,
                            filename = f.Filename,
                            contentType = f.ContentType,
                            uploadedAt = f.UploadedAt,
                            scanStatus = f.ScanStatus.ToString(),
                            s3Key = f.S3Key,
                            s3Bucket = f.S3Bucket,
                        })
                        .ToArray(),
                },
                // RA-292 AC04: must be the identical projection BuildPayload sends. This used to
                // be a weaker copy that dropped orsId, isNewSite and the whole interimSite, so a
                // resubmit after a query silently destroyed the interim site data ManagementBe
                // already held.
                OperatorSection.OverseasSites => BuildOverseasSitesSection(application),
                OperatorSection.BesEvidence => new
                {
                    sectionStatus = application.BesEvidence?.SectionStatus.ToString(),
                },
                _ => null,
            };
        }

        return payload;
    }

    private static object BuildPayload(AccreditationApplicationModel application)
    {
        return new
        {
            organisationName = application.OrganisationName,
            registrationNumber = application.RegistrationReference,
            materialsHandled = new[] { application.MaterialType.ToString().ToLowerInvariant() },
            glassRecyclingProcess = application.GlassRecyclingProcess,
            material = application.MaterialType.ToString().ToLowerInvariant(),
            accreditationYear = application.Year,
            previousAccreditationYear = application.Year - 1,
            complianceIssuesReported = 0,
            siteAddress = application.SiteAddress,
            siteAddressPostcode = ExtractPostcode(application.SiteAddress),
            // RA-526: derived at Seed time from the registration's own regulator (see
            // RegulatorNationMapper) - the reliable replacement for ManagementBe's own
            // postcode-derived nation routing (ReAccreditationNationRoutingHook). Null when
            // this application predates that derivation; JsonOptions' WhenWritingNull drops it
            // entirely rather than sending an explicit null, so ManagementBe's own
            // default-to-England fallback applies exactly as it does for any other absent field.
            nation = application.Nation?.ToString(),
            companyRegisterAddressPostcode = application.CompanyRegisterAddressPostcode,
            companyRegisteredAddress = application.CompanyRegisteredAddress,
            companiesHouseNumber = application.CompaniesHouseNumber,
            permitNumbers = application.PermitNumbers,
            wasteProcessingType = application.WasteProcessingType,
            operatorApplicationId = application.ApplicationId,
            // RA-503: operatorOrganisationId is ReEx's internal ObjectId - kept unchanged here for
            // any existing consumer, but it must never be treated as the operator/regulator-facing
            // organisation number. operatorOrgNumber (below) is the new field carrying that value.
            operatorOrganisationId = application.OrganisationId,
            operatorOrgNumber = application.OrgId,
            operatorRegistrationId = application.RegistrationId,
            operatorEmail = application.SubmittedBy?.Email,
            // RA-316: the charge the operator was shown on the payment-details page, echoed so
            // Case Management's duly-making page displays that same number instead of recomputing
            // it and drifting. Null (band unset) is dropped entirely by JsonOptions'
            // WhenWritingNull, and ManagementBe renders a blank rather than rejecting.
            chargeAmountPence = AccreditationChargeCalculator.CalculateChargePence(application),
            // RA-503: the operator's real, nation-specific bank payment reference (built by
            // buildPaymentReference in epr-register-enrol-frontend, e.g. PR/PK/REP/500500) - the
            // exact string shown to the operator on their submit-confirmation and
            // view-payment-details pages, captured from SubmitRequest into
            // application.PaymentReference at Submit time. Null (a caller that predates this)
            // is dropped by JsonOptions' WhenWritingNull; ManagementBe falls back to its own
            // generated applicationReference when the field is absent (see WorkItemService.
            // SubmitAsync) rather than rejecting the submission.
            paymentReference = application.PaymentReference,
            submittedBy = application.SubmittedBy is null
                ? null
                : new
                {
                    fullName = application.SubmittedBy.FullName,
                    jobTitle = application.SubmittedBy.JobTitle,
                    email = application.SubmittedBy.Email,
                },
            submitterContactDetails = application.SubmitterContactDetails is null
                ? null
                : new
                {
                    fullName = application.SubmitterContactDetails.FullName,
                    email = application.SubmitterContactDetails.Email,
                    phone = application.SubmitterContactDetails.Phone,
                    jobTitle = application.SubmitterContactDetails.JobTitle,
                },
            prns = BuildPrnsSection(application),
            businessPlan = new
            {
                newInfrastructurePercent = application.BusinessPlan.NewInfrastructurePercent,
                priceSupportPercent = application.BusinessPlan.PriceSupportPercent,
                businessCollectionsPercent = application.BusinessPlan.BusinessCollectionsPercent,
                communicationsPercent = application.BusinessPlan.CommunicationsPercent,
                newMarketsPercent = application.BusinessPlan.NewMarketsPercent,
                newUsesPercent = application.BusinessPlan.NewUsesPercent,
                otherPercent = application.BusinessPlan.OtherPercent,
                newInfrastructureDetail = application.BusinessPlan.NewInfrastructureDetail,
                priceSupportDetail = application.BusinessPlan.PriceSupportDetail,
                businessCollectionsDetail = application.BusinessPlan.BusinessCollectionsDetail,
                communicationsDetail = application.BusinessPlan.CommunicationsDetail,
                newMarketsDetail = application.BusinessPlan.NewMarketsDetail,
                newUsesDetail = application.BusinessPlan.NewUsesDetail,
                otherDetail = application.BusinessPlan.OtherDetail,
            },
            samplingPlan = new
            {
                files = application
                    .SamplingPlan.Files.Select(f => new
                    {
                        fileId = f.FileId,
                        filename = f.Filename,
                        contentType = f.ContentType,
                        uploadedAt = f.UploadedAt,
                        scanStatus = f.ScanStatus.ToString(),
                        s3Key = f.S3Key,
                        s3Bucket = f.S3Bucket,
                    })
                    .ToArray(),
            },
            overseasSites = BuildOverseasSitesSection(application),
        };
    }

    // Single source of truth for the overseas-sites projection, shared by BuildPayload (initial
    // submit) and BuildSectionsPayload (resubmit after query) so the two can never drift.
    //
    // RA-292 AC01/AC02/AC04: carries the full ORS detail the regulator reviews, the isNewSite
    // flags behind the "new" markers, and the nested interim site. Every field added here is
    // optional on the consumer side — work items created before RA-292 simply do not have them,
    // and null-valued fields are dropped entirely by JsonOptions' WhenWritingNull.
    //
    // RA-483 AC01: the operator's "remove" journey deselects rather than deletes, so a removed ORS
    // stays in the application record with Selected = false. Filtering it out here is what keeps
    // it off the regulator's work-item screen. `selected` is still projected onto the sites that
    // do survive the filter so consumers can defend independently — the cross-repo contract is
    // "absent or true => visible, explicit false => removed", so this only ever emits true today
    // but pins the field name for management-fe and management-be.
    private static object BuildOverseasSitesSection(AccreditationApplicationModel application) =>
        new
        {
            sites = (application.OverseasSites?.Sites ?? [])
                .Where(s => s.Selected)
                .Select(s => new
                {
                    siteId = s.SiteId,
                    selected = s.Selected,
                    orsId = s.OrsId,
                    siteName = s.SiteName,
                    siteAddress = s.SiteAddress,
                    addressLine1 = s.AddressLine1,
                    addressLine2 = s.AddressLine2,
                    townOrCity = s.TownOrCity,
                    country = s.Country,
                    coordinates = s.Coordinates,
                    contactName = s.ContactName,
                    contactEmail = s.ContactEmail,
                    contactPhone = s.ContactPhone,
                    operationCodes = s.OperationCodes,
                    code1 = s.Code1,
                    code2 = s.Code2,
                    code3 = s.Code3,
                    repatriatedLoads = s.RepatriatedLoads,
                    conditionsOfExport = s.ConditionsOfExport,
                    isEu = s.IsEu,
                    isOecd = s.IsOecd,
                    isNewSite = s.IsNewSite,
                    registeredNowAccredited = s.RegisteredNowAccredited,
                    interimSite = s.InterimSite is null
                        ? null
                        : new
                        {
                            siteId = s.InterimSite.SiteId,
                            siteNumber = s.InterimSite.SiteNumber,
                            isNewSite = s.InterimSite.IsNewSite,
                            country = s.InterimSite.Country,
                            siteName = s.InterimSite.SiteName,
                            addressLine1 = s.InterimSite.AddressLine1,
                            addressLine2 = s.InterimSite.AddressLine2,
                            townOrCity = s.InterimSite.TownOrCity,
                            stateOrRegion = s.InterimSite.StateOrRegion,
                            postcode = s.InterimSite.Postcode,
                            contactName = s.InterimSite.ContactName,
                            contactEmail = s.InterimSite.ContactEmail,
                            contactPhone = s.InterimSite.ContactPhone,
                            operationCodes = s.InterimSite.OperationCodes,
                        },
                    besEvidence = new
                    {
                        files = (s.BesEvidence?.BesEvidenceUploads ?? [])
                            .Select(f => new
                            {
                                fileId = f.FileId,
                                filename = f.Filename,
                                contentType = f.ContentType,
                                uploadedAt = f.UploadedAt,
                                scanStatus = f.ScanStatus,
                                besEvidenceValidFromDate = f.BesEvidenceValidFromDate,
                                besEvidenceExpiryDate = f.BesEvidenceExpiryDate,
                                s3Key = f.S3Key,
                                s3Bucket = f.S3Bucket,
                            })
                            .ToArray(),
                    },
                })
                .ToArray(),
        };

    // Shared by BuildPayload and BuildSectionsPayload, as above.
    //
    // RA-292 AC03: isNew marks an authority-to-issue contact introduced during this application.
    // It is derived server-side by PrnsAuthoriserMerge when the section is written, never taken
    // from the client, so what ships here is always the server's own view.
    private static object BuildPrnsSection(AccreditationApplicationModel application) =>
        new
        {
            plannedTonnageBand = application.Prns.PlannedTonnageBand?.ToString(),
            authorisers = application
                .Prns.Authorisers.Select(a => new
                {
                    fullName = a.FullName,
                    email = a.Email,
                    isNew = a.IsNew,
                })
                .ToArray(),
        };

    internal static string? ExtractPostcode(string? siteAddress)
    {
        if (string.IsNullOrWhiteSpace(siteAddress))
            return null;

        var match = System.Text.RegularExpressions.Regex.Match(
            siteAddress,
            @"[A-Z]{1,2}\d[A-Z\d]?\s*\d[A-Z]{2}",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.RightToLeft,
            TimeSpan.FromMilliseconds(100)
        );
        return match.Success ? match.Value.ToUpperInvariant() : null;
    }

    private HttpRequestMessage BuildRequest(
        HttpMethod method,
        string url,
        object? body = null,
        string? userId = null,
        string? userName = null,
        JsonSerializerOptions? contentOptions = null
    )
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: contentOptions ?? JsonOptions);
        }

        request.Headers.Add("x-cdp-client-id", _config.ClientId);

        if (!string.IsNullOrEmpty(userId))
            request.Headers.Add("x-cdp-user-id", userId);
        if (!string.IsNullOrEmpty(userName))
            request.Headers.Add("x-cdp-user-name", userName);

        if (!string.IsNullOrEmpty(_config.SharedSecret))
        {
            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            var signature = ComputeSignature(
                _config.SharedSecret,
                _config.ClientId,
                userId,
                userName,
                timestamp,
                nonce
            );

            request.Headers.Add("x-cdp-auth-signature", signature);
            request.Headers.Add("x-cdp-auth-timestamp", timestamp);
            request.Headers.Add("x-cdp-auth-nonce", nonce);
        }

        return request;
    }

    // Port of ManagementBe's ClientIdAuthenticationHandler.ComputeSignature
    // (v3 canonical payload — see ManagementBe ADR-0005). Must stay in sync —
    // any change is a breaking change requiring a coordinated deploy.
    internal static string ComputeSignature(
        string sharedSecret,
        string clientId,
        string? userId,
        string? userName,
        string timestamp,
        string nonce
    )
    {
        var payload = string.Join(
            '\n',
            "v3",
            clientId,
            userId ?? string.Empty,
            userName ?? string.Empty,
            timestamp,
            nonce
        );
        var keyBytes = Encoding.UTF8.GetBytes(sharedSecret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var mac = HMACSHA256.HashData(keyBytes, payloadBytes);
        return Convert.ToBase64String(mac);
    }

    #region Internal DTOs

    internal sealed class CreateWorkItemRequest
    {
        public required string TypeId { get; init; }
        public required object Payload { get; init; }
        public string? Source { get; init; }
    }

    internal sealed class WorkItemResponseDto
    {
        public Guid Id { get; init; }
        public string? TypeId { get; init; }
        public string? StateId { get; init; }
        public JsonElement Payload { get; init; }
        public string? ApplicationReference { get; init; }
    }

    internal sealed class ResumeFromQueryRequest
    {
        public required object ResponderContactDetails { get; init; }
        public required IReadOnlyList<string> SectionKeys { get; init; }
        public required Dictionary<string, object?> Sections { get; init; }

        // RA-316. Both optional on the consumer; nulls are dropped by JsonOptions.
        public int? ChargeAmountPence { get; init; }
        public string? PaymentReference { get; init; }
    }

    internal sealed class WithdrawWorkItemRequest
    {
        public required string Reason { get; init; }
    }

    internal sealed class SiteAddedRequest
    {
        public required string SiteType { get; init; }
        public required string OrsId { get; init; }
        public string? SiteNumber { get; init; }
        public required bool IsNewSite { get; init; }
    }

    #endregion
}
