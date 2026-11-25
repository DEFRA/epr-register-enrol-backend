# Accreditation API: onboarding guide for service clients

A read-only API for services (first consumer: ReEx) that need accreditation data held by the
Operator Backend. Paths live under `/v1`. **The API is provisional**: the spec is still a draft, and
the API may change after ReEx has tried it. The data behind it will also move to a new data model,
which we do not intend to change what the API returns. See the
[versioning and deprecation policy](#versioning-and-deprecation) below.

## Base URLs

| Environment | Base URL |
| --- | --- |
| dev | `https://epr-register-enrol-backend.dev.cdp-int.defra.cloud` |
| test | `https://epr-register-enrol-backend.test.cdp-int.defra.cloud` |
| ext-test | `https://epr-register-enrol-backend.ext-test.cdp-int.defra.cloud` |
| perf-test | `https://epr-register-enrol-backend.perf-test.cdp-int.defra.cloud` |
| prod | `https://epr-register-enrol-backend.prod.cdp-int.defra.cloud` |

## Credentials

Each consumer gets its own **client id** and **secret** per environment. To get them for a new
consumer, ask the Reg-Acc team (see Contact). ReEx's client id is
`epr-ReEx-Client` (case-sensitive, the same in every environment). Secrets are created and
rotated in CDP secrets. Never send a secret over Slack or email. To rotate, we add a `_NEXT`
secret; both work until you have switched, then the old one is removed.

## Signing a request

Send these headers on every request (all requests are `GET`):

| Header | Value |
| --- | --- |
| `x-cdp-client-id` | your client id |
| `x-cdp-auth-timestamp` | current UTC time, `yyyy-MM-ddTHH:mm:ssZ`; must be within 5 minutes of our clock |
| `x-cdp-auth-nonce` | a new random value per request (e.g. 16 random bytes, base64); a nonce can be used once |
| `x-cdp-auth-signature` | base64 of `HMAC-SHA256(secret, payload)` |

The payload is these lines joined by a newline (`\n`), with no trailing newline. The two empty
lines are the user id and user name, which service clients leave empty:

```
v3
{clientId}
(empty)
(empty)
{timestamp}
{nonce}
```

### Worked example (not a real credential)

| | |
| --- | --- |
| client id | `example-client` |
| secret | `example-secret-do-not-use` |
| timestamp | `2027-01-15T10:30:00Z` |
| nonce | `bm9uY2UtZXhhbXBsZS0wMDAx` |
| signature | `mJPc+QOEbTNBzB60Pj2NjSOy8bOYmvoCjCwPQS2CSTk=` |

Shell:

```bash
printf 'v3\n%s\n\n\n%s\n%s' "$CLIENT_ID" "$TS" "$NONCE" | openssl dgst -sha256 -hmac "$SECRET" -binary | base64
```

Node:

```js
import { createHmac } from 'node:crypto'
const payload = ['v3', clientId, '', '', timestamp, nonce].join('\n')
const signature = createHmac('sha256', secret).update(payload).digest('base64')
```

## Retrying a request

Sign every attempt afresh, with a new timestamp and a new nonce. Sending the same signed request
twice is refused as `reused_nonce`.

- Retry a `GET` that fails with a network error, a timeout or a `5xx`, with a growing delay (for
  example 1, 2 then 4 seconds) and a limit of a few attempts.
- Do not retry a `400` or a `404`: the same request gets the same answer.
- On `expired_timestamp`, correct your clock, then sign a new request.
- On a `429` (`rate_limited`), wait for the number of seconds in `Retry-After`, then sign a new
  request. On a `503` (`query_timeout`), retry after a short delay, with a narrower search if you can.
- Keep your call rate sensible, and avoid tight polling loops: each client has a request limit (see
  Endpoints).

## Endpoints

All require a granted (approved) accreditation for the organisation, registration and year.
Accreditations for 2027 onwards are served.

| Route | Returns |
| --- | --- |
| `GET /v1/accreditations?year=2027&organisationNumber=500027&registrationNumber=R27ER5000270036PL&pageNumber=1&limit=20` | a page of accreditations across all organisations (limit max 100). An exporter's item carries its `overseasSites`, as getting one accreditation does; a reprocessor's has none. `year`, `organisationNumber`, `registrationNumber` and `status` (`approved`, `suspended`, `cancelled` or `withdrawn`) are optional filters that combine with AND. A filter that matches nothing is an empty page, not a 404. Results are ordered by accreditation number |
| `GET /v1/organisations/{organisationNumber}/registrations/{registrationNumber}/accreditations/{year}` | the complete accreditation: status and tonnage band history, signatories and, for an exporter, every accredited overseas site with its interim sites and status history (ordered by `orsId`, not paged). A reprocessor has no `overseasSites` |
| `…/accreditations/{year}/overseas-sites?pageNumber=1&limit=20` | a page of the accredited overseas sites, each with its interim sites (limit max 100) |
| `…/accreditations/{year}/overseas-sites/{orsId}` | one accredited overseas site (`orsId` is three digits, e.g. `001`) |

Notes: the shapes follow the v2 draft spec (`docs/openapi/registration-accreditation-api.v2.yaml`).
`tonnageBand` is the operator's planned band. Signatories are returned only when getting one
accreditation, never in search. Search items carry `overseasSites` but no status or tonnage band history. BES evidence is not served. The
[Response reference](#response-reference) and [Common questions](#common-questions) below give the fields and rules, including which overseas
sites and interim sites are returned.

## Response reference

Lists are wrapped in a page: `pageNumber`, `limit`, `totalItems` and `items`. Fields with no value
are left out. The order of fields is not significant. **Ignore fields you do not know.**

### Accreditation (search items, and the base of the detail response)

Search items also carry `overseasSites` for an exporter: see the table below.

| Field | Meaning |
| --- | --- |
| `organisationNumber` | the numeric organisation number |
| `registrationNumber` | the registration's number, for example `R26EX9900010002PA` |
| `accreditationNumber` | for example `A27EX9900010002PA` |
| `accreditationYear` | the scheme year, 2027 or later |
| `material` | `Steel`, `Wood`, `Aluminium`, `Fibre`, `Glass`, `Paper` or `Plastic` |
| `glassRecyclingProcess` | glass only: `glass_re_melt` or `glass_other` |
| `wasteProcessingType` | `reprocessor` or `exporter` |
| `latestStatus` | the latest entry of `statusHistory`, in the same shape (`status`, `effectiveFromDate`, `effectiveToDate`, `recordedAt`). It can be in the future: see below |
| `tonnageBand` | `up_to_500`, `up_to_5000`, `up_to_10000` or `over_10000` |
| `overseasSites` | **exporters only**: the accredited overseas sites, in the same shape and order (by `orsId`) as getting one accreditation. `[]` when none are selected; left out for a reprocessor |

### Accreditation detail: extra fields

| Field | Meaning |
| --- | --- |
| `statusHistory` | every status change, oldest first: `status`, `effectiveFromDate`, `effectiveToDate`, `recordedAt` |
| `statusTimeline` | the history as periods that don't overlap, so each date has one status: `status`, `effectiveFromDate`, `effectiveToDate` (`null` on the last only). Use it for the status on a date |
| `tonnageBandHistory` | the band over time: `tonnageBand`, `recordedAt` |
| `signatories` | the PRN signatories: `fullName`, `email` |
| `overseasSites` | **exporters only**: the accredited overseas sites, ordered by `orsId` (see below) |

An overseas site has `siteId`, `orsId`, `siteName`, `formattedAddress`, `address`, `contact`,
`operationCodes`, `isEu`, `isOecd`, `latestStatus`, `statusHistory`, `statusTimeline` and `interimSites`. An
interim site has `siteId`, `siteNumber`, `siteName`, `address`, `contact`, `operationCodes`,
`createdAt`, `latestStatus`, `statusHistory` and `statusTimeline`.

An interim site's status is `approved` from when its overseas site was approved, and `withdrawn` from
when it was withdrawn. Withdrawn interim sites are always returned: read `latestStatus` to tell
them from current ones. A withdrawn interim site that is restored is the same interim site, so its
history reads approved, withdrawn, approved. A new interim site added in place of a withdrawn one is a
separate interim site, with its own history. The dates follow the rules in "Status and dates" below.

`siteId` identifies a site within its accreditation. It is unique there, across overseas and interim
sites, and is never reused after a site is removed. It is not unique across accreditations, so
identify a site by organisation, registration, year and `siteId` together. It is not a database id.

**Personal data.** `signatories` hold a person's name and email, and every `contact` a name, email
and phone number. Treat them as personal data.

### Status and dates

- `latestStatus` is the latest entry of the record's `statusHistory`: the latest
  `effectiveFromDate`, and the later `recordedAt` where two share one. It is the same entry as in the
  history, so its dates always match. Today every accreditation has the one status `approved`. The
  API is built to carry `suspended`, `cancelled` and `withdrawn`, which will appear when case
  management can send them. Overseas sites use the same list of statuses. **Handle a status value
  you do not know.** The same goes for `tonnageBand`.
- `latestStatus` can be in the future. A 2027 accreditation approved in November 2026 has a
  `latestStatus` of `approved` with an `effectiveFromDate` of `2027-01-01`: it has been granted, but
  the status does not apply until that date. **Use the effective dates to tell whether it is in
  effect today.** For the status on a given day, use `statusTimeline` (see "Was this accreditation
  approved on date X?").
- `effectiveFromDate` is the first day a status applies, and `effectiveToDate` the last. Both are UK
  calendar dates (`yyyy-MM-dd`, Europe/London). `effectiveFromDate` is the later of 1 January of the
  accreditation year and the UK date the change was recorded, so a status recorded before the year
  starts with the year, and one recorded during the year starts on the day it was recorded. It may be
  in the future. `effectiveToDate` is the day before the next entry's `effectiveFromDate`, and is
  `null` on the latest entry. Where two entries have the same `effectiveFromDate`, the one with the
  later `recordedAt` takes precedence, and the earlier one ends on that same date
  (`effectiveToDate` equals `effectiveFromDate`). If they also share a `recordedAt` (an interim site
  withdrawn before its overseas site was approved has both entries at the approval), the entry later
  in the array takes precedence. `recordedAt` and `createdAt` are UTC times.
- Until effective-dated history is recorded, each history has one `approved` entry. For an
  accreditation approved before that, its date is when case management last changed it, or the last
  edit, so treat an older approval date as approximate.

### Example: an exporter's accreditation

`GET /v1/organisations/990001/registrations/R26EX9900010002PA/accreditations/2027`, shown
grouped for reading:

```json
{
  "organisationNumber": 990001,
  "registrationNumber": "R26EX9900010002PA",
  "accreditationNumber": "A27EX9900010002PA",
  "accreditationYear": 2027,
  "material": "Paper",
  "wasteProcessingType": "exporter",
  "latestStatus": { "status": "approved", "effectiveFromDate": "2027-01-01", "effectiveToDate": null, "recordedAt": "2026-10-01T00:00:00Z" },
  "tonnageBand": "up_to_5000",
  "statusHistory": [
    { "status": "approved", "effectiveFromDate": "2027-01-01", "effectiveToDate": null, "recordedAt": "2026-10-01T00:00:00Z" }
  ],
  "statusTimeline": [
    { "status": "approved", "effectiveFromDate": "2027-01-01", "effectiveToDate": null }
  ],
  "tonnageBandHistory": [
    { "tonnageBand": "up_to_5000", "recordedAt": "2026-10-01T00:00:00Z" }
  ],
  "signatories": [
    { "fullName": "Test Data Authoriser", "email": "test.authoriser@example.com" }
  ],
  "overseasSites": [
    {
      "siteId": 99000101,
      "orsId": "001",
      "siteName": "Test Data Overseas Site 001",
      "formattedAddress": "1 Hafenstrasse, Hamburg, Germany",
      "address": { "line1": "1 Hafenstrasse", "town": "Hamburg", "country": "Germany" },
      "contact": {
        "fullName": "Test Data Site Contact",
        "email": "test.site@example.com",
        "phone": "+49 40 000000"
      },
      "operationCodes": ["R3"],
      "isEu": true,
      "isOecd": true,
      "latestStatus": { "status": "approved", "effectiveFromDate": "2027-01-01", "effectiveToDate": null, "recordedAt": "2026-10-01T00:00:00Z" },
      "statusHistory": [
        { "status": "approved", "effectiveFromDate": "2027-01-01", "effectiveToDate": null, "recordedAt": "2026-10-01T00:00:00Z" }
      ],
      "statusTimeline": [
        { "status": "approved", "effectiveFromDate": "2027-01-01", "effectiveToDate": null }
      ],
      "interimSites": [
        {
          "siteId": 990001011,
          "siteNumber": "TD-990001-001-1",
          "siteName": "Test Data Interim Site 001-1",
          "address": {
            "line1": "1 Tussenstraat",
            "town": "Rotterdam",
            "country": "Netherlands",
            "postcode": "3011 AA"
          },
          "contact": {
            "fullName": "Test Data Interim Contact",
            "email": "test.interim@example.com",
            "phone": "+31 10 000 0000"
          },
          "operationCodes": ["R12"],
          "createdAt": "2026-10-01T00:00:00Z"
        }
      ]
    }
  ]
}
```

A reprocessor's detail is the same without `overseasSites`
(`GET /v1/organisations/990003/registrations/R26ER9900030000PL/accreditations/2027`).

## Common questions

### Was this accreditation approved on date X?

1. Fetch the accreditation (the detail route above).
2. Read `statusTimeline`. Its periods don't overlap, and are ordered by `effectiveFromDate`.
3. The answer is the `status` of the period where `effectiveFromDate` is on or before X and
   `effectiveToDate` is `null` or on or after X.
4. If X is before the first period, there is no status on X: treat `null` as "not yet effective on
   X". `latestStatus` is still set: it is the latest, not necessarily the one in effect on X.

```js
// timeline: the accreditation's statusTimeline. date: 'yyyy-MM-dd', a UK calendar date.
// ISO dates compare correctly as strings. Returns null if the date is before the first period.
function statusOn(timeline, date) {
  const period = timeline.find(
    (p) => p.effectiveFromDate <= date && (p.effectiveToDate === null || date <= p.effectiveToDate)
  )
  return period ? period.status : null
}
```

For the exporter above, whose timeline is one `approved` period effective from `2027-01-01`:
`statusOn(timeline, '2027-03-01')` is `approved`, and `statusOn(timeline, '2026-12-31')` is `null`.
To compare an instant (a UTC time), convert it to the UK calendar date first.

### What accreditations does an organisation hold?

`GET /v1/accreditations?organisationNumber=990006` returns one item per accreditation, here two
for one registration:

```json
{
  "pageNumber": 1,
  "limit": 20,
  "totalItems": 2,
  "items": [
    {
      "organisationNumber": 990006,
      "registrationNumber": "R26ER9900060000AL",
      "accreditationNumber": "A27ER9900060000AL",
      "accreditationYear": 2027,
      "material": "Aluminium",
      "wasteProcessingType": "reprocessor",
      "latestStatus": { "status": "approved", "effectiveFromDate": "2027-01-01", "effectiveToDate": null, "recordedAt": "2026-10-01T00:00:00Z" },
      "tonnageBand": "up_to_5000"
    },
    {
      "organisationNumber": 990006,
      "registrationNumber": "R26ER9900060000AL",
      "accreditationNumber": "A28ER9900060000AL",
      "accreditationYear": 2028,
      "material": "Aluminium",
      "wasteProcessingType": "reprocessor",
      "latestStatus": { "status": "approved", "effectiveFromDate": "2028-01-01", "effectiveToDate": null, "recordedAt": "2026-10-01T00:00:00Z" },
      "tonnageBand": "up_to_5000"
    }
  ]
}
```

Add `year=2028` to narrow it, or `registrationNumber=R26ER9900060000AL` to see one registration
across years. The filters combine. An organisation, registration or year with nothing in the data
gives an **empty page** (`totalItems: 0`), not a `404`. A `404` comes only from the detail and site
routes.

### How do I read every accreditation?

Request pages with `pageNumber=1,2,3…` and `limit=100` until you have `totalItems` items. Results
are ordered by accreditation number, so the order is stable between pages.

### Which overseas sites are accredited for a registration?

For an exporter, `overseasSites` on each search item and in the detail response lists them. Use the site routes to page
them or to fetch one by `orsId`. Only sites included in the accreditation are returned: a site the
organisation registered but did not accredit is a `404`. Withdrawn interim sites are included: see
their `latestStatus`.

### Is a registration or accreditation I cannot find really missing?

A `404` with the code `accreditation_not_found` means there is no **granted** accreditation for
that organisation, registration and year. That covers an accreditation that is still an
application, one approved before 2027, and one that does not exist. The test data has an example of
each.

Only accreditation years from 2027 are served. A search for an earlier year (`year=2026`) is an
empty page, even if one was granted, and getting one for an earlier year is a `404`
(`accreditation_not_found`). Only `approved` accreditations are served today, so a search with
`status=suspended`, `status=cancelled` or `status=withdrawn` is valid and finds none.

**Limits.** Each client may make 600 requests a minute; past that, a request is answered `429`
(`rate_limited`) with a `Retry-After` in seconds. A query the database stops for taking too long is
answered `503` (`query_timeout`). Searching by `organisationNumber` is fast; a search across all
organisations takes longer as the data grows, so narrow it when you can.

## Errors

Errors are `application/problem+json` with `status`, `detail`, a stable `code` and a `traceId`
(quote it when asking for support).

| Status | `code` | Meaning |
| --- | --- | --- |
| 401 | `missing_header` | a required header is missing |
| 401 | `unknown_client` | client id not recognised |
| 401 | `invalid_timestamp` | timestamp not in the required format |
| 401 | `expired_timestamp` | timestamp more than 5 minutes from our clock |
| 401 | `invalid_signature` | wrong secret, or payload signed differently |
| 401 | `reused_nonce` | this nonce was already used |
| 403 | `forbidden` | the client is known but not allowed to make this request |
| 404 | `accreditation_not_found` | no granted accreditation for that organisation, registration and year |
| 404 | `overseas_site_not_found` | no such overseas site on the accreditation |
| 404 | `not_found` | no such route |
| 400 | `invalid_organisation_number`, `invalid_registration_number`, `invalid_year`, `invalid_ors_id`, `invalid_paging`, `invalid_status` | bad input |
| 405 | `method_not_allowed` | any method other than GET |
| 429 | `rate_limited` | too many requests: wait for the `Retry-After` seconds |
| 503 | `query_timeout` | the database stopped a query for taking too long: retry, with a narrower search if you can |

An unknown route (404 `not_found`) or a method other than GET (405) is answered before
authentication, because it reveals nothing beyond the published API. Every route that returns
data checks the signature first.

401 troubleshooting: check clock skew first, then that the payload has exactly two empty lines
and no trailing newline, then that you use the secret for this client and environment.

## Versioning and deprecation

The version is in the path, so the routes above are version 1.

**Provisional for now.** The spec is a draft (`0.2.0-draft`). Until ReEx has consumed the API and
we have settled its shape with them, we may make breaking changes to `/v1`, including to the fields
and routes in this guide. We will tell you about any such change before it is released, in the
places listed under "How you will hear about changes" below. When the shape is settled, this
section will say so, and the rules below will apply.

**Changes we can make to `/v1` at any time, without a new version:**

- new fields in a response
- new optional query parameters
- new routes
- new values of `status` and `tonnageBand`
- fixing a response that did not match this guide

This is why your client must ignore fields it does not know and handle values it does not know.

**Breaking changes**, once the shape is settled, are not made in `/v1`. These are removing or renaming a field, changing its
type or meaning, removing a route, and rejecting input that was accepted before. They arrive as a
new version, served alongside the old one. We do not switch a version off without telling you
first, and the notice period is agreed with each consumer.

**How you will hear about changes:** the "What's new" section below, and the Slack channel in
Contact. Contact the Reg-Acc team if a change would be a problem for you.

## What's new

- Every `statusHistory` (accreditation, overseas site and interim site) now has a `statusTimeline`
  beside it: the history as periods that don't overlap, so each date has exactly one status, and where
  entries start on the same date the one recorded later wins. Use it for the status on a date. If your
  client rejects fields it does not know, allow `statusTimeline`. `statusHistory` and `latestStatus`
  are unchanged.
- Each interim site has `statusHistory` and `latestStatus`, in the same shape as an accreditation's
  and an overseas site's. It is `approved` from when its overseas site was approved and `withdrawn`
  from when it was withdrawn. Withdrawn interim sites are always returned, so `removedAt` and the
  `includeWithdrawn` query parameter are removed (a request that still sends `includeWithdrawn` is
  answered as if it did not, and is no longer a `400`). **Breaking for clients that read `removedAt`
  or send `includeWithdrawn`.**
- `status` (a string) on an accreditation, in search results, and on each overseas site is replaced
  by `latestStatus`: the latest entry of that record's `statusHistory`, in the same shape
  (`status`, `effectiveFromDate`, `effectiveToDate`, `recordedAt`). It always matches the history,
  and it can be in the future, so use its effective dates to tell whether it is in effect.
  **Breaking for clients that read `status`.**
- Each `statusHistory` entry now has `effectiveFromDate` and `effectiveToDate` (replacing
  `effectiveDate`), so you can see the period a status applies. `effectiveFromDate` is the later
  of 1 January of the accreditation year and the UK date the change was recorded;
  `effectiveToDate` is the day before the next entry starts, and `null` on the latest. Where two
  entries start on the same date, the one recorded later takes precedence. `withdrawn` is a new
  status value, and an overseas site's `validFrom` is removed. **Breaking for clients that read
  `effectiveDate` or `validFrom`.**
- `GET /v1/accreditations` can be filtered by `status`. Only `approved` is served today, so
  `suspended`, `cancelled` and `withdrawn` return an empty page until they can be sent. Results are in a stable
  order (accreditation number), and the search is indexed.
- A request limit for each client (`429`, `rate_limited`, with `Retry-After`) and a time limit on
  queries (`503`, `query_timeout`).
- Every error from `/v1`, including an unknown route (`404`, `not_found`) and a method other than
  GET (`405`, `method_not_allowed`), is `application/problem+json` with a `code` and a `traceId`.
- A search for a year before 2027 is an empty page, and getting one is a `404`. This is now
  documented.

- `GET /v1/accreditations` can be filtered by `organisationNumber` and `registrationNumber` as well
  as `year`. The filters combine with AND.
- Getting an accreditation now returns the complete accreditation: an exporter's accredited
  overseas sites, with their interim sites and status history, are in the same response under
  `overseasSites`, so one request is enough. The separate overseas-site routes are unchanged.
- Each exporter item in `GET /v1/accreditations` now carries `overseasSites`, the same sites, order
  and fields (with interim sites and status histories) as getting one accreditation. An exporter with
  no selected sites has `[]`; a reprocessor item has no `overseasSites`. Paging through search once
  therefore gives every accreditation with its sites, with no extra call per accreditation. Pages
  can be larger, so use a smaller `limit` if you hit `503` (`query_timeout`).
- A record that cannot be addressed by its organisation, registration and accreditation numbers is
  left out of the response and logged by us, so one bad record does not fail a page.

Existing calls keep working. If your client rejects fields it does not know, allow `overseasSites`
on the accreditation and on search items.

## Test data

The same nine fixed records exist in dev, test and ext-test. They are test data, owned by us,
and are put back to this state whenever the service starts. Do not rely on them in prod.

| Organisation | Registration | Accreditation | Year | What it is |
| --- | --- | --- | --- | --- |
| `990001` | `R26EX9900010002PA` | `A27EX9900010002PA` | 2027 | exporter, paper, 1 overseas site (`001`) with 1 interim site |
| `990002` | `R26EX9900020002FB` | `A27EX9900020002FB` | 2027 | exporter, fibre, 10 overseas sites (`001` to `010`) each with 2 interim sites |
| `990003` | `R26ER9900030000PL` | `A27ER9900030000PL` | 2027 | reprocessor, plastic |
| `990004` | `R26ER9900040000ST` | `A27ER9900040000ST` | 2027 | reprocessor, steel (the metal example) |
| `990005` | `R26EX9900050002WD` | `A27EX9900050002WD` | 2027 | exporter, wood, 3 registered overseas sites of which only `001` and `002` are accredited, and a withdrawn interim site on `001`. `003` is a 404 |
| `990006` | `R26ER9900060000AL` | `A27ER9900060000AL` and `A28ER9900060000AL` | 2027 and 2028 | reprocessor, aluminium, one registration accredited in two years. Search by `registrationNumber` or `organisationNumber` returns both |
| `990007` | `R26ER9900070000PL` | none | 2027 | an application that was submitted but never granted. It is not served: a lookup is a 404 and a search finds nothing |
| `990008` | `R26ER9900080000ST` | `A26ER9900080000ST` | 2026 | approved, but before 2027, so not served: a lookup is a 404 |

For example: `GET /v1/organisations/990002/registrations/R26EX9900020002FB/accreditations/2027/overseas-sites`.

For a `404` example, use any registration number that does not exist, for example
`NO-SUCH-REGISTRATION`.

## Contact

The Reg-Acc team owns this API. Ask them for access (a client id and secret), for help, and to report
a problem. The Slack channel for questions from API clients is **#ws1-ws2-ws3-joint-project**.

When you report a problem, include the `traceId` from the error response, the environment, the
request path and the time of the request (UTC).

## Consumer tests

`consumer-tests/` calls a deployed environment from outside the service using your own
credentials; see its README.
