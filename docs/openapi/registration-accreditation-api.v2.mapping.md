# Data document to API mapping (v2)

Companion to [registration-accreditation-api.v2.yaml](registration-accreditation-api.v2.yaml). It
supersedes [the v0.1 mapping](registration-accreditation-api.mapping.md) for the v2 draft. The
conventions are unchanged (camelCase field names, null fields omitted, enums stored as int32 unless
marked otherwise), so see the v0.1 document for them.

Every API field is traced through three layers:

**API field (YAML)** → **C# property** → **Mongo field**

Several v2 fields have **no current source**, because the API is shaped to the target domain model
(Accreditation separate from Application, with status and tonnage band history). These fields are
marked **new**, with the entity they will come from.

The "Today" columns show where each value lives now, which is where the migration copies it from.
**The API itself never reads the App document.** It is built on the target model only and goes live
after the migration (domain model design, decision D3).

## Source models

| Short name | C# model | In Mongo? |
| --- | --- | --- |
| **App** | `AccreditationApplication/Models/AccreditationApplicationModel.cs` | **Yes.** Collection `accreditationApplications`. Today the only source for v2 fields, and the migration's source. Not read by the API. |
| **Org** | `Organisation/Models/OrganisationModel.cs` | **No.** ReEx-shaped fixture. Internal-only in v2: nothing is exposed directly. |
| **Accreditation** (target) | not built | **New.** Created when an application is approved. Owns its status and its tonnage band; `statusHistory` and `tonnageBandHistory` are both projected from its version history. |
| **OverseasSite** (target) | not built | **New.** One per registered overseas site. Its interim sites are embedded in it (`interimSites[]`), not held as separate entities. |

## What v2 removed, and why

| Removed | Reason (architecture review) |
| --- | --- |
| `/organisations`, `/organisations/{organisationNumber}` | We do not own Organisation. It is an internal data model only. |
| `/organisations/.../registrations[...]`, `/registrations` search | Registration endpoints are dropped for now. |
| Registered overseas-site and interim-site endpoints | Dropped until ORS management and its migration plan are worked out. |
| `.../applications[...]`, `AccreditationApplication` schema | Application is not for external use. Only the Accreditation is exposed. |
| `ApplicationStatus`, `applicationStatus` | Application-specific. Replaced by `Accreditation.status`. |
| `SectionStatus` | Application-specific, not accreditation-specific. |
| `UploadedFile`, `samplingPlan`, `besEvidence` | Uploaded files only matter to the application. |
| `samplingInspectionPlan` | Hidden. |
| `formSubmissionTime` | Application data. |
| `businessPlan`, `prnIssuance.prnIncomeBusinessPlan` | Commercially sensitive. Not available through this API at all. |
| Overseas site `code1`, `code2`, `code3` | Duplicates of `operationCodes`. |
| Overseas site `isNewSite`, `selected`, `registeredNowAccredited` | Application-time flags. The application holds every registered site, with `selected` marking those chosen for accreditation. The API returns only the chosen ones, so the flag is not needed. |
| Interim site `isNewSite` | Application-time flag. |
| Interim-site endpoints, `interimSiteIds` | Interim sites are returned inside their overseas site (`interimSites[]`). |
| `material` filter on search | Search is by year only. |
| `{accreditationYearOrReference}` path segment | Now `{year}` only. Agreed with the ReEx team, who address an accreditation by organisation, registration and year. May change later. |

## Path identifiers

| Path parameter | C# | Mongo | Type |
| --- | --- | --- | --- |
| `organisationNumber` | App `OrgId` | App `orgId` | integer |
| `registrationNumber` | App `RegistrationReference` | App `registrationReference` | string |
| `year` | App `Year` | App `year` | integer, four digits |
| `orsId` | `OverseasSiteModel.OrsId` | App `overseasSites.sites[].orsId` | string, e.g. `001` |

## Accreditation

`AccreditationSummary` is returned by search; `Accreditation` adds the two histories.

Search (`GET /v1/accreditations`) filters on `year`, `organisationNumber`, `registrationNumber` and
`status`. They map to App `year`, `orgId`, `registrationReference` and `applicationStatus`, and
combine with AND. A search that names an organisation or registration is covered by the
`(orgId, registrationReference, year)` index.

Results are ordered by accreditation number (App `accreditationReference`, then `_id`), so the order
is the same from one page to the next. The `granted_search_sort` index
`(applicationStatus, accreditationReference, _id)` backs that sort for a search across all
organisations.

`status` maps only for `approved` today: it is the only API status with stored data. `suspended`,
`cancelled` and `withdrawn` are valid values and always return an empty page (`totalItems` 0), until case management
sends them and the filter has a source for them.

`Accreditation` also carries `overseasSites[]` (exporters only, omitted for a reprocessor): every
accredited overseas site, as described in [Accredited overseas sites](#accredited-overseas-sites),
ordered by `orsId`, unpaged. `includeWithdrawn` applies to the interim sites inside them. A record
with no organisation, registration or accreditation number is dropped from the response and logged,
not served.

| API field | Today: C# → Mongo | Target source | Notes |
| --- | --- | --- | --- |
| `organisationNumber` | App `OrgId` → `orgId` | Accreditation → Registration → Organisation | reached via the registration |
| `registrationNumber` | App `RegistrationReference` → `registrationReference` | Accreditation → Registration | |
| `accreditationNumber` | App `AccreditationReference` → `accreditationReference` | Accreditation | **renamed** from `accreditationReference` |
| `accreditationYear` | App `Year` → `year` | Accreditation | **renamed** from `year` |
| `material` | App `MaterialType` → `materialType` | Accreditation (via Registration) | string, same names |
| `glassRecyclingProcess` | App `GlassRecyclingProcess` → `glassRecyclingProcess` | Accreditation | `Remelt`/`Other` → `glass_re_melt`/`glass_other` |
| `wasteProcessingType` | App `WasteProcessingType` → `wasteProcessingType` | Accreditation (via Registration) | |
| `statusHistory[]` | none | **new**: Accreditation status history | each entry is `status`, `effectiveFromDate`, `effectiveToDate` and `recordedAt`. The first entry is `approved`, written when the application is approved. `effectiveFromDate` is the later of 1 January of the accreditation year and the UK date the change was recorded; `effectiveToDate` is the day before the next entry's `effectiveFromDate`, and `null` on the latest. Where two entries share an `effectiveFromDate`, the later `recordedAt` takes precedence. Both dates are worked out when the response is built, not stored |
| `status` | none | **derived** from `statusHistory` | the latest entry whose `effectiveFromDate` is on or before today. Never stored, so a forward-dated change takes effect without a write |
| `tonnageBandHistory[]` | App `Prns.PlannedTonnageBand` → `prns.plannedTonnageBand` (single value, int32) | **new**: Accreditation `tonnageBand`, one entry per version where it changed | each entry is `tonnageBand` and `recordedAt`. No `effectiveDate`: a band cannot be forward-dated or backdated. First entry from the seed (ReEx `prnIssuance.tonnageBand` / Org `PrnIssuance.PlannedIssuance`) or the application; later entries from in-year changes |
| `tonnageBand` | as above | Accreditation `tonnageBand` | the current value, which is also the latest history entry. Included in search because it is on the public register |
| `signatories[]` | App `Prns.Authorisers` → `prns.authorisers` (`fullName`, `email`; `isNew` not exposed). Seeded: Org `PrnIssuance.Signatories` → `prnIssuance.signatories` | Accreditation | get-one only, never in search. Personal data. When both exist, the application's list replaces the seed's (no merge) |

### Tonnage band values

The API uses the ReEx values. App `PlannedTonnageBand` is stored as int32, so the mapper converts:

| Mongo (int32) | C# | API |
| --- | --- | --- |
| 0 | `UpTo500` | `up_to_500` |
| 1 | `UpTo5000` | `up_to_5000` |
| 2 | `UpTo10000` | `up_to_10000` |
| 3 | `Over10000` | `over_10000` |

### Business plan (owned, not exposed)

The accreditation owns the business plan entered in the frontend, submitted, and shown to the
regulator in management-fe: App `BusinessPlan` → `businessPlan`. The ReEx seed's
`prnIssuance.prnIncomeBusinessPlan` is not carried onto the accreditation. Neither is exposed by
this API.

### Seeding an accreditation from what exists today

For accreditations seeded from ReEx (or approved before the remodel), there is no effective-dated
history, so the migration writes one entry each:

- `statusHistory`: one entry with the ReEx status. `recordedAt` is ReEx `validFrom` if present,
  otherwise the approval date, and `effectiveFromDate` follows from it (see above).
- `tonnageBandHistory`: one entry from `prnIssuance.tonnageBand` (or `prns.plannedTonnageBand`),
  with `recordedAt` set to the migration time.
- Each accredited overseas site: one `approved` status entry, recorded at the site's `validFrom`
  when it has one.

## Accredited overseas sites

Source: App `OverseasSiteModel` at `overseasSites.sites[]`, filtered to sites included in the
accreditation (`selected == true`). Paths are relative to `overseasSites.sites[].`.

| API field | C# | Mongo field | Notes |
| --- | --- | --- | --- |
| `siteId` | `SiteId` | `siteId` | |
| `orsId` | `OrsId` | `orsId` | path id |
| `siteName` | `SiteName` | `siteName` | |
| `formattedAddress` | `SiteAddress` | `siteAddress` | **renamed** |
| `address.line1` / `line2` / `town` / `country` | `AddressLine1` / `AddressLine2` / `TownOrCity` / `Country` | `addressLine1` / `addressLine2` / `townOrCity` / `country` | **restructured** from flat fields |
| `coordinates` | `Coordinates` | `coordinates` | |
| `contact.fullName` / `email` / `phone` | `ContactName` / `ContactEmail` / `ContactPhone` | `contactName` / `contactEmail` / `contactPhone` | **restructured** |
| `operationCodes[]` | `OperationCodes` | `operationCodes` | `code1`–`code3` dropped |
| `repatriatedLoads`, `conditionsOfExport`, `isEu`, `isOecd` | same | same | |
| ~~`validFrom`~~ | `ValidFrom` | `validFrom` | **removed from the API**. Still stored as a raw ISO string, and used as when the site's `approved` status entry was recorded |
| `statusHistory[]` | none | **new**: overseas site status history | same entry shape and transition rules as the accreditation's, and the same status list (`approved`, `suspended`, `cancelled`, `withdrawn`), which interim sites use too. Lets one site be suspended without suspending the accreditation |
| `status` | none | **derived** from `statusHistory` | same projection rule as the accreditation's `status` |
| `interimSites[]` | `InterimSites` | `interimSites` | **inlined**, unpaged (a site only ever has a few); withdrawn sites (`removedAt` set) only with `includeWithdrawn=true`. See below |

## Interim sites

Source: App `InterimSiteModel` at `overseasSites.sites[].interimSites[]`, returned inside the
overseas site's `interimSites[]`. Unchanged from v0.1 except that `isNewSite` is dropped and there
are no separate endpoints. In the target model an interim site is part of its overseas site: it is
embedded in the OverseasSite's `interimSites[]`, and changes to it are versions of that overseas site.

| API field | C# | Mongo field | Notes |
| --- | --- | --- | --- |
| `siteId` | `SiteId` | `siteId` | |
| `siteNumber`, `siteName` | same | same | |
| `address.*` | `AddressLine1` / `AddressLine2` / `TownOrCity` / `StateOrRegion` / `Country` / `Postcode` | flat fields on the site | **restructured** |
| `contact.*` | `ContactName` / `ContactEmail` / `ContactPhone` | flat fields on the site | **restructured** |
| `operationCodes[]` | `OperationCodes` | `operationCodes` | |
| `createdAt` | `CreatedAt` | `createdAt` | absent before RA-603 |
| `removedAt` | `RemovedAt` | `removedAt` | present means withdrawn |

## Open items

- **Tonnage band dating (working assumption).** Bands cannot be forward-dated or backdated, so
  history entries carry only `recordedAt`. If the business later says otherwise, add
  `effectiveFromDate` and `effectiveToDate` as on status history.
- **Authentication** is still undefined. Expected to be HMAC request signing; details to follow.
