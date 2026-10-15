# Data document to API mapping

Companion to [registration-accreditation-api.yaml](registration-accreditation-api.yaml). Every API
field is traced through three layers:

**API field (YAML)** → **C# property** → **Mongo field**

so the mapping layer, and any later data remodel, have one place to look.

Mongo names below are **derived from the code, not read from a live database**: the C# attributes
and the driver conventions registered in `Utils/Mongo/MongoDbClientFactory.cs`. Confirm against a
real document before relying on them (see [Open items](#open-items)).

## Source models

| Short name | C# model | In Mongo? |
| --- | --- | --- |
| **App** | `AccreditationApplication/Models/AccreditationApplicationModel.cs` and its sections (`OverseasSiteModel`, `InterimSiteModel`, …) | **Yes.** Collection `accreditationApplications`: one document per org + registration + year + material. |
| **Org** | `Organisation/Models/OrganisationModel.cs` (`RegistrationModel`, `AccreditationModel`) | **No.** A ReEx-shaped test fixture (`FakeOrganisationPersistence`). Real data comes from ReEx, so its Mongo column is the fixture's field name, not a stored one. |

## How C# becomes Mongo (registered conventions)

`MongoDbClientFactory` registers one convention pack for the whole process:

| Rule | Effect |
| --- | --- |
| `CamelCaseElementNameConvention` | Mongo field = C# property with its first letter lower-cased (`OrgId` → `orgId`, `IsEu` → `isEu`). Nested objects and list items follow the same rule. |
| `IgnoreIfNullConvention(true)` | A `null` property is **omitted** from the document, not stored as `null`. Non-null empty lists (`[]`) are stored. |
| `IgnoreExtraElementsConvention(true)` | Unknown stored fields are ignored on read. |

Exceptions, all on the App model:

| C# | Mongo |
| --- | --- |
| `Id` (`[BsonId]`, `ObjectId?`) | `_id` |
| `ApplicationId` (`[BsonIgnore]`, derived from `Id`) | not stored |
| `NotificationStatus`, `DueDate` (`[BsonIgnore]`) | not stored; derived at read time from the Case Management work item |
| `CaseManagementWorkItemId` (`Guid?`) | stored as a standard-representation UUID (BSON binary subtype 4) |
| `DateTime` properties | BSON date (UTC) |

Org fixture exceptions: `RegistrationModel.Id` and `AccreditationModel.Id` are `[BsonNoId]` with
`[BsonElement("id")]`, so the field is a plain `id` (ObjectId), not `_id`.

### Enum storage (affects the API transform)

`[JsonConverter(JsonStringEnumConverter)]` only controls the API's JSON. Mongo storage is set by
`[BsonRepresentation]`; with none, the driver stores the enum as **int32**.

| Enum | Mongo storage | API value | Transform needed |
| --- | --- | --- | --- |
| `Nation` | string (C# name) | same name | none |
| `MaterialType` | string (C# name) | same name | none |
| `ApplicationStatus` | string, see note | same name | none |
| `GlassRecyclingProcess` | string (C# name: `Remelt`, `Other`) | `glass_re_melt`, `glass_other` | **rename**: `Remelt` ↔ `glass_re_melt`, `Other` ↔ `glass_other` |
| `SectionStatus` | **int32** (`NotStarted`=0, `InProgress`=1, `Completed`=2, `Submitted`=3, `Queried`=4) | name | **int → name** |
| `PlannedTonnageBand` | **int32** (`UpTo500`=0, `UpTo5000`=1, `UpTo10000`=2, `Over10000`=3) | name | **int → name** |
| `FileScanStatus` | **int32** (`Pending`=0, `Clean`=1, `Infected`=2) | name | **int → name** |
| `AccreditationFileDocumentType` | **int32** (`SamplingPlan`=0, `SupportingEvidence`=1) | name | **int → name** |

Note: `ApplicationStatus` carries `[BsonRepresentation(BsonType.String)]` on the property. The other
section/file enums do not.

The Org fixture's `status`, `material`, `wasteProcessingType` and `glassRecyclingProcess` are plain
`string`s, not enums.

## Cross-cutting rules

- **No Mongo ObjectIds in the API.** Every path id is a business reference.
- **Never exposed:** App `_id`, `organisationId` (ReEx's internal ObjectId, RA-503), `version`,
  `caseManagementStatusUpdatedAt`, every `*.versions[]` snapshot list, `overseasSites.sites[].previousSites`,
  the legacy single `overseasSites.sites[].interimSite` mirror, S3 key and bucket fields, and the Org
  `formSubmissionRawDataId` on organisation, registration and accreditation.
- **Case:** API fields and Mongo fields are both camelCase, so a plain copy needs no rename. Renames
  are listed explicitly.

## Path identifiers

| Path parameter | C# | Mongo | Type |
| --- | --- | --- | --- |
| `organisationNumber` | `OrgId` | App `orgId` | int |
| `registrationNumber` | App `RegistrationReference`; Org `RegistrationModel.WasteRegistrationNumber` | App `registrationReference`; Org fixture `registrations[].wasteRegistrationNumber` | string |
| `accreditationYearOrReference` | App `Year` **or** `AccreditationReference` | App `year` (4 digits) **or** `accreditationReference` (12 chars) | string |
| `applicationReference` | App `ApplicationReference` | App `applicationReference` (not `_id`, not the derived `ApplicationId`) | string |
| `orsId` | `OverseasSiteModel.OrsId` | App `overseasSites.sites[].orsId` | string, e.g. `001` |
| `interimSiteId` | `InterimSiteModel.SiteId` | App `overseasSites.sites[].interimSites[].siteId` | int |

Assumption: a registration has at most one accreditation per year, so a year resolves to one
accreditation. Reference formats beyond length are not known.

## Shared shapes

### `Address`

Used for `siteAddress`, `noticeAddress`, `companyDetails.registeredAddress` (`RegisteredAddress`) and
the `address` of overseas and interim sites. Mongo paths are relative to the owning object.

| API | Org `SiteAddressModel` / `NoticeAddressModel` | Org `RegisteredAddressModel` | App `InterimSiteModel` | App `OverseasSiteModel` |
| --- | --- | --- | --- | --- |
| `line1` | `Line1` → `line1` | `Line1` → `line1` | `AddressLine1` → `addressLine1` | `AddressLine1` → `addressLine1` |
| `line2` | `Line2` → `line2` | `Line2` → `line2` | `AddressLine2` → `addressLine2` | `AddressLine2` → `addressLine2` |
| `town` | `Town` → `town` | `Town` → `town` | `TownOrCity` → `townOrCity` | `TownOrCity` → `townOrCity` |
| `county` | `County` → `county` | `County` → `county` | none | none |
| `region` | none | `Region` → `region` | `StateOrRegion` → `stateOrRegion` | none |
| `country` | `Country` → `country` | `Country` → `country` | `Country` → `country` | `Country` → `country` |
| `postcode` | `Postcode` → `postcode` | `Postcode` → `postcode` | `Postcode` → `postcode` | none |

For interim and overseas sites the address fields are **flat on the site document**, not an embedded
object, so the mapper assembles the API `address` object from them. Required lists: `RegisteredAddress`
needs `line1`, `town`, `postcode`; `InterimSite.address` needs `line1`, `town`, `country`.

### `ContactDetails`

One shape for every person or contact: `fullName`, `email`, `phone`, `role`, `title`.

| Used at (API) | C# | Mongo path | Field differences | Required |
| --- | --- | --- | --- | --- |
| Org `contactDetails`, `submitterContactDetails` (org, registration, accreditation) | `ContactDetailsModel` | fixture `…contactDetails.*`, `…submitterContactDetails.*` | none | none |
| Org `users[]`, Registration `approvedPersons[]`, `prnIssuance.signatories[]` | `PersonModel` | fixture `users[]`, `registrations[].approvedPersons[]`, `accreditations[].prnIssuance.signatories[]` | none | none |
| App `submitterContactDetails` | `SubmitterContactDetailsModel` | `submitterContactDetails.{fullName,email,phone,jobTitle}` | **`jobTitle` → `title`** | none |
| App `submittedBy` | `SubmittedByModel` | `submittedBy.{fullName,jobTitle,email}` | **`jobTitle` → `title`** | `fullName`, `title` |
| App `prns.authorisers[]` | `PrnsAuthoriser` | `prns.authorisers[].{fullName,email,isNew}` | `isNew` stays beside it | `fullName`, `email` |
| App `query.querySubmissions[].querySubmitterContactDetails` | `QuerySubmitterContactDetails` | `query.querySubmissions[].querySubmitterContactDetails.{fullName,email,role}` | none | `fullName`, `email`, `role` |
| `InterimSite.contact`, `OverseasSite.contact` | `ContactName`, `ContactEmail`, `ContactPhone` | `…contactName`, `…contactEmail`, `…contactPhone` (flat on the site) | **`contactName` → `fullName`, `contactEmail` → `email`, `contactPhone` → `phone`**, assembled into an object | Interim: all three. Overseas: none |

## Organisation

`GET /organisations` returns `OrganisationSummary`; `GET /organisations/{organisationNumber}` returns
`Organisation`. Source is the Org fixture (not stored); ReEx is the real source.

| API field | C# (Org) | Mongo field (fixture) | Notes |
| --- | --- | --- | --- |
| `organisationNumber` | `OrgId` | `orgId` | renamed in API |
| `wasteProcessingTypes[]` | `WasteProcessingTypes` | `wasteProcessingTypes` | |
| `reprocessingNations[]` | `ReprocessingNations` | `reprocessingNations` | |
| `businessType` | `BusinessType` | `businessType` | |
| `submittedToRegulator` | `SubmittedToRegulator` | `submittedToRegulator` | |
| `companyDetails.name` / `tradingName` / `registrationNumber` / `companiesHouseNumber` | `CompanyDetails.*` | `companyDetails.*` | |
| `companyDetails.registeredAddress` | `CompanyDetails.RegisteredAddress` | `companyDetails.registeredAddress.*` | `RegisteredAddress` |
| `partnership.type` / `partners[].name` / `partners[].type` | `Partnership.*` | `partnership.*` | |
| `contactDetails` | `ContactDetails` | `contactDetails` | `ContactDetails` |
| `schemaVersion`, `version` | `SchemaVersion`, `Version` | `schemaVersion`, `version` | `Organisation` only |
| `submitterContactDetails` | `SubmitterContactDetails` | `submitterContactDetails` | `Organisation` only |
| `users[]` | `Users` | `users` | `ContactDetails` |
| `registrations[]` | `Registrations` | `registrations` | see Registration |
| `accreditations[]` | `Accreditations` | `accreditations` | see Accreditation |
| none | `FormSubmissionRawDataId` | `formSubmissionRawDataId` | not exposed |

## Registration

Source: Org `RegistrationModel` (fixture, embedded at `registrations[]`). Related resources stay as
references, as stored.

| API field | C# | Mongo field (fixture) | Notes |
| --- | --- | --- | --- |
| `registrationNumber` | `WasteRegistrationNumber` | `registrations[].wasteRegistrationNumber` | renamed in API; also the path id |
| `siteId` | `SiteId` | `siteId` | |
| `formSubmissionTime` | `FormSubmissionTime` | `formSubmissionTime` | string |
| `status`, `material`, `wasteProcessingType` | same | same | strings |
| `glassRecyclingProcess` | `GlassRecyclingProcess` | `glassRecyclingProcess` | string as stored |
| `accreditationReference` | `AccreditationId` (`ObjectId?`) | `accreditationId` | **derived**: the ObjectId is resolved to the accreditation's business reference |
| `gridReference`, `suppliers`, `plantEquipmentDetails` | same | same | |
| `siteAddress`, `noticeAddress` | `SiteAddress`, `NoticeAddress` | `siteAddress`, `noticeAddress` | `Address` |
| `recyclingProcess[]`, `exportPorts[]`, `samplingInspectionPlan[]` | same | same | |
| `wasteManagementPermits[]` | `WasteManagementPermits` | `wasteManagementPermits` | `type`, `permitNumber`, `authorisedWeight`, `permitWindow`, `exemptions[]{reference,exemptionCode}` |
| `approvedPersons[]` | `ApprovedPersons` | `approvedPersons` | `ContactDetails` |
| `yearlyMetrics` | `YearlyMetrics` | `yearlyMetrics` | `year`, `metric`, `input{type,ukPackagingWaste,nonUkPackagingWaste,nonPackagingWaste}`, `output{type,sentToAnotherSite,contaminants,processLoss}` |
| `contactDetails`, `submitterContactDetails` | same | same | `ContactDetails` |
| `overseasSites[]` | `OverseasSites` (`List<string>`) | `overseasSites` | ORS ids such as `001` |
| none | `Id` | `id` | ObjectId, not exposed |
| none | `FormSubmissionRawDataId` | `formSubmissionRawDataId` | not exposed |

## Accreditation

Source: Org `AccreditationModel` (fixture, embedded at `accreditations[]`), **joined to App** for the
reference and year.

| API field | C# | Mongo field | Notes |
| --- | --- | --- | --- |
| `accreditationReference` | App `AccreditationReference` | App `accreditationReference` | **derived from App**; not on the Org model |
| `year` | App `Year` | App `year` | **derived from App**; not on the Org model |
| `registrationNumber` | parent registration | n/a | link back up the tree |
| `formSubmissionTime`, `status`, `material`, `wasteProcessingType`, `glassRecyclingProcess` | same | `accreditations[].…` same names | strings |
| `siteAddress` | `SiteAddress` | `siteAddress` | `Address` |
| `recyclingProcess[]`, `samplingInspectionPlan[]` | same | same | |
| `prnIssuance.plannedIssuance` | `PrnIssuance.PlannedIssuance` | `prnIssuance.plannedIssuance` | |
| `prnIssuance.signatories[]` | `PrnIssuance.Signatories` | `prnIssuance.signatories` | `ContactDetails` |
| `prnIssuance.prnIncomeBusinessPlan[]` | `PrnIssuance.PrnIncomeBusinessPlan` | `prnIssuance.prnIncomeBusinessPlan` | `PrnIncomeBusinessPlanItemModel`: `description`, `detailedDescription`, `percentSpent`, `percentIncomeSpent`, `usageDescription`, `detailedExplanation`. Under review |
| `businessPlan[]` | `BusinessPlan` | `businessPlan` | `BusinessPlanItemModel`: `description`, `detailedDescription`, `percentSpent`. Under review |
| `contactDetails`, `submitterContactDetails` | same | same | `ContactDetails` |
| `overseasSites[]` | `OverseasSites` (`List<string>`) | `overseasSites` | ORS ids |
| none | `Id` | `id` | not exposed |
| none | `FormSubmissionRawDataId` | `formSubmissionRawDataId` | not exposed |

## Accreditation application

Source: App, collection `accreditationApplications`. Mongo paths are top-level unless stated.

| API field | C# | Mongo field | Notes |
| --- | --- | --- | --- |
| `applicationReference` | `ApplicationReference` | `applicationReference` | path id |
| `organisationNumber` | `OrgId` | `orgId` | renamed in API |
| `organisationName` | `OrganisationName` | `organisationName` | |
| `year` | `Year` | `year` | |
| `registrationReference` | `RegistrationReference` | `registrationReference` | |
| `previousRegistrationNumbers[]` | `PreviousRegistrationNumbers` | `previousRegistrationNumbers` | |
| `accreditationReference` | `AccreditationReference` | `accreditationReference` | |
| `previousAccreditationNumbers[]` | `PreviousAccreditationNumbers` | `previousAccreditationNumbers` | |
| `isExporter` | `IsExporter` | `isExporter` | |
| `nation` | `Nation` | `nation` | string, same names |
| `siteAddress` | `SiteAddress` | `siteAddress` | pre-formatted string, not an `Address` |
| `companyRegisterAddressPostcode`, `companyRegisteredAddress`, `isUkRegisteredAddress`, `companiesHouseNumber` | same | same | |
| `permitNumbers[]` | `PermitNumbers` | `permitNumbers` | |
| `wasteProcessingType` | `WasteProcessingType` | `wasteProcessingType` | |
| `materialType` | `MaterialType` | `materialType` | string, same names |
| `glassRecyclingProcess` | `GlassRecyclingProcess` | `glassRecyclingProcess` | string `Remelt`/`Other` in Mongo → `glass_re_melt`/`glass_other` in API |
| `applicationStatus` | `ApplicationStatus` | `applicationStatus` | string, same names |
| `sourceReExAccreditationId`, `sourceYear` | same | same | |
| `paymentReference`, `caseManagementReference` | same | same | |
| `caseManagementWorkItemId` | `CaseManagementWorkItemId` | `caseManagementWorkItemId` | UUID (standard representation) |
| `submittedBy` | `SubmittedBy` | `submittedBy` | `ContactDetails`; `jobTitle` → `title` |
| `submitterContactDetails` | `SubmitterContactDetails` | `submitterContactDetails` | `ContactDetails`; `jobTitle` → `title` |
| `withdrawalReason` | `WithdrawalReason` | `withdrawalReason` | |
| `dateSent`, `dateLastEdited`, `createdAt`, `updatedAt` | same | same | BSON dates |
| `notificationStatus`, `dueDate` | `NotificationStatus`, `DueDate` | not stored | **derived** at read time from the Case Management work item |
| `prns.plannedTonnageBand` | `Prns.PlannedTonnageBand` | `prns.plannedTonnageBand` | **int32 → name** |
| `prns.authorisers[]` | `Prns.Authorisers` | `prns.authorisers` | `ContactDetails` + `isNew` |
| `prns.sectionStatus` | `Prns.SectionStatus` | `prns.sectionStatus` | **int32 → name** |
| `businessPlan.*Percent`, `businessPlan.*Detail` | `BusinessPlan.*` | `businessPlan.*` | seven pairs: `newInfrastructure`, `priceSupport`, `businessCollections`, `communications`, `newMarkets`, `newUses`, `other`. Under review |
| `businessPlan.sectionStatus` | `BusinessPlan.SectionStatus` | `businessPlan.sectionStatus` | **int32 → name** |
| `samplingPlan.files[]` | `SamplingPlan.Files` | `samplingPlan.files` | `UploadedFile`, below |
| `samplingPlan.sectionStatus` | `SamplingPlan.SectionStatus` | `samplingPlan.sectionStatus` | **int32 → name** |
| `overseasSites.sectionStatus` | `OverseasSites.SectionStatus` | `overseasSites.sectionStatus` | **int32 → name**; section status only, the sites are the sub-resource |
| `besEvidence.sectionStatus` | `BesEvidence.SectionStatus` | `besEvidence.sectionStatus` | **int32 → name** |
| `query.queryNote`, `query.queriedSectionKeys[]` | `Query.QueryNote`, `Query.QueriedSectionKeys` | `query.queryNote`, `query.queriedSectionKeys` | |
| `query.querySubmissions[]` | `Query.QuerySubmissions` | `query.querySubmissions` | `querySubmissionTime` (date), `sectionKeys[]`, `querySubmitterContactDetails` (`ContactDetails`) |
| none | `Id` | `_id` | not exposed |
| none | `OrganisationId` | `organisationId` | ReEx ObjectId, not exposed |
| none | `Version` | `version` | optimistic-concurrency token |
| none | `CaseManagementStatusUpdatedAt` | `caseManagementStatusUpdatedAt` | internal ordering guard |
| none | `*.Versions` | `prns.versions`, `businessPlan.versions`, `samplingPlan.versions`, `overseasSites.versions`, `besEvidence.versions` | snapshots, not exposed |

### `UploadedFile` (`samplingPlan.files[]`)

| API | C# | Mongo (`samplingPlan.files[].`) | Notes |
| --- | --- | --- | --- |
| `fileId`, `filename`, `contentType`, `uploadedAt`, `uploadedByUserId` | same | same | |
| `scanStatus` | `ScanStatus` | `scanStatus` | **int32 → name** |
| `documentType` | `DocumentType` | `documentType` | **int32 → name** |
| none | `S3Key`, `S3Bucket` | `s3Key`, `s3Bucket` | not exposed |

## Overseas sites

Source: App `OverseasSiteModel`, stored at `overseasSites.sites[]`. Registered sites use `OverseasSite`;
sites on an accreditation use `AccreditedOverseasSite` (`OverseasSite` plus the ORS-A fields).
Mongo paths below are relative to `overseasSites.sites[].`.

| API field | C# | Mongo field | Notes |
| --- | --- | --- | --- |
| `siteId` | `SiteId` | `siteId` | |
| `orsId` | `OrsId` | `orsId` | |
| `siteName` | `SiteName` | `siteName` | |
| `formattedAddress` | `SiteAddress` | `siteAddress` | **renamed** so it is not confused with `address` |
| `address.line1` / `line2` / `town` / `country` | `AddressLine1` / `AddressLine2` / `TownOrCity` / `Country` | `addressLine1` / `addressLine2` / `townOrCity` / `country` | **restructured** from flat fields, see `Address` |
| `coordinates` | `Coordinates` | `coordinates` | |
| `contact.fullName` / `email` / `phone` | `ContactName` / `ContactEmail` / `ContactPhone` | `contactName` / `contactEmail` / `contactPhone` | **restructured**, see `ContactDetails` |
| `operationCodes[]` | `OperationCodes` | `operationCodes` | |
| `code1`, `code2`, `code3` | `Code1`, `Code2`, `Code3` | `code1`, `code2`, `code3` | look like a legacy duplicate of `operationCodes` |
| `repatriatedLoads`, `conditionsOfExport`, `isEu`, `isOecd` | same | same | |
| `interimSiteIds[]` | `InterimSites` | `interimSites[].siteId` | **derived**: sites where `removedAt` is absent |
| `validFrom` | `ValidFrom` | `validFrom` | accredited variant only; raw ISO string |
| `selected` | `Selected` | `selected` | accredited variant only |
| `registeredNowAccredited`, `isNewSite` | same | same | accredited variant only |
| `besEvidence.doYouWantToUploadMoreEvidence` | `BesEvidence.DoYouWantToUploadMoreEvidence` | `besEvidence.doYouWantToUploadMoreEvidence` | accredited variant only |
| `besEvidence.besEvidenceUploads[]` | `BesEvidence.BesEvidenceUploads` | `besEvidence.besEvidenceUploads` | `fileId`, `filename`, `contentType`, `scanStatus` (string here), `besEvidenceValidFromDate`, `besEvidenceExpiryDate`, `uploadedAt`, `uploadedBy` |
| none | `BesEvidence…S3Key`, `S3Bucket` | `…s3Key`, `…s3Bucket` | not exposed |
| none | `PreviousSites` | `previousSites` | promote/revert undo stack |
| none | `InterimSite` | `interimSite` | legacy mirror of the first active interim site |

## Interim sites

Source: App `InterimSiteModel`, stored at `overseasSites.sites[].interimSites[]`. Mongo paths below are
relative to that.

| API field | C# | Mongo field | Notes |
| --- | --- | --- | --- |
| `siteId` | `SiteId` | `siteId` | path id |
| `siteNumber` | `SiteNumber` | `siteNumber` | |
| `siteName` | `SiteName` | `siteName` | |
| `address.line1` / `line2` / `town` / `region` / `country` / `postcode` | `AddressLine1` / `AddressLine2` / `TownOrCity` / `StateOrRegion` / `Country` / `Postcode` | `addressLine1` / `addressLine2` / `townOrCity` / `stateOrRegion` / `country` / `postcode` | **restructured** |
| `contact.fullName` / `email` / `phone` | `ContactName` / `ContactEmail` / `ContactPhone` | `contactName` / `contactEmail` / `contactPhone` | **restructured** |
| `operationCodes[]` | `OperationCodes` | `operationCodes` | |
| `isNewSite` | `IsNewSite` | `isNewSite` | |
| `createdAt` | `CreatedAt` | `createdAt` | absent on documents that predate RA-603; deliberately not backfilled |
| `removedAt` | `RemovedAt` | `removedAt` | absent means active; present means withdrawn |

List endpoints hide withdrawn sites unless `includeWithdrawn=true`; the single-site endpoint always
returns them. Because of `IgnoreIfNull`, `removedAt` and `createdAt` are missing from the document
rather than `null`.

## Open items

- **Verify against a real document.** The Mongo column comes from attributes and conventions. Check
  a stored `accreditationApplications` document, in particular the int32 enums (`sectionStatus`,
  `plannedTonnageBand`, `scanStatus`, `documentType`) and the `glassRecyclingProcess` string values.
- **Org-model data is fixture-only,** so the Organisation, Registration and Accreditation Mongo
  columns describe the fixture. The real source is ReEx until the data remodel.
- **Business plan items** (`BusinessPlanItemModel`, `PrnIncomeBusinessPlanItemModel`, and the App
  `businessPlan` section) are under review and may change.
- **`code1`–`code3`** on overseas sites may be removable.
- **Reference patterns** for accreditation and application references are not yet known.
