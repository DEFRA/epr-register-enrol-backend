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
- Keep your call rate sensible. There is no rate limit today, but we may add a per-client one, so avoid tight polling loops.

## Endpoints

All require a granted (approved) accreditation for the organisation, registration and year.
Accreditations for 2027 onwards are served.

| Route | Returns |
| --- | --- |
| `GET /v1/accreditations?year=2027&organisationNumber=500027&registrationNumber=R27ER5000270036PL&pageNumber=1&limit=20` | a page of accreditation summaries across all organisations (limit max 100). `year`, `organisationNumber` and `registrationNumber` are optional filters that combine with AND. A filter that matches nothing is an empty page, not a 404 |
| `GET /v1/organisations/{organisationNumber}/registrations/{registrationNumber}/accreditations/{year}?includeWithdrawn=false` | the complete accreditation: status and tonnage band history, signatories and, for an exporter, every accredited overseas site with its interim sites and status history (ordered by `orsId`, not paged). A reprocessor has no `overseasSites` |
| `…/accreditations/{year}/overseas-sites?pageNumber=1&limit=20&includeWithdrawn=false` | a page of the accredited overseas sites, each with its interim sites (limit max 100) |
| `…/accreditations/{year}/overseas-sites/{orsId}?includeWithdrawn=false` | one accredited overseas site (`orsId` is three digits, e.g. `001`) |

Notes: the shapes follow the v2 draft spec (`docs/openapi/registration-accreditation-api.v2.yaml`).
`tonnageBand` is the operator's planned band. Signatories are returned only when getting one
accreditation, never in search. BES evidence is not served. The
[Response reference](#response-reference) and [Common questions](#common-questions) below give the fields and rules, including which overseas
sites and interim sites are returned.

## Response reference

Lists are wrapped in a page: `pageNumber`, `limit`, `totalItems` and `items`. Fields with no value
are left out. The order of fields is not significant. **Ignore fields you do not know.**

### Accreditation (search items, and the base of the detail response)

| Field | Meaning |
| --- | --- |
| `organisationNumber` | the numeric organisation number |
| `registrationNumber` | the registration's number, for example `R26EX9900010002PA` |
| `accreditationNumber` | for example `A27EX9900010002PA` |
| `accreditationYear` | the scheme year, 2027 or later |
| `material` | `Steel`, `Wood`, `Aluminium`, `Fibre`, `Glass`, `Paper` or `Plastic` |
| `glassRecyclingProcess` | glass only: `glass_re_melt` or `glass_other` |
| `wasteProcessingType` | `reprocessor` or `exporter` |
| `status` | the status today (see below) |
| `tonnageBand` | `up_to_500`, `up_to_5000`, `up_to_10000` or `over_10000` |

### Accreditation detail: extra fields

| Field | Meaning |
| --- | --- |
| `statusHistory` | every status change, oldest first: `status`, `effectiveDate`, `recordedAt` |
| `tonnageBandHistory` | the band over time: `tonnageBand`, `recordedAt` |
| `signatories` | the PRN signatories: `fullName`, `email` |
| `overseasSites` | **exporters only**: the accredited overseas sites, ordered by `orsId` (see below) |

An overseas site has `siteId`, `orsId`, `siteName`, `formattedAddress`, `address`, `contact`,
`operationCodes`, `isEu`, `isOecd`, `validFrom`, `status`, `statusHistory` and `interimSites`. An
interim site has `siteId`, `siteNumber`, `siteName`, `address`, `contact`, `operationCodes`,
`createdAt` and, if it was withdrawn, `removedAt`.

**Personal data.** `signatories` hold a person's name and email, and every `contact` a name, email
and phone number. Treat them as personal data.

### Status and dates

- `status` is the latest `statusHistory` entry whose `effectiveDate` is on or before today. Today
  every accreditation has the one status `approved`. The API is built to carry `suspended` and
  `cancelled`, which will appear when case management can send them. **Handle a status value you do
  not know.** The same goes for `tonnageBand`.
- If no entry is effective yet (for example the only entry is effective in the future), `status`
  is still `approved`: an accreditation that is served has been granted. In that case `status` and
  `statusHistory` disagree, so use `statusHistory` for "what was it on date X".
- `effectiveDate` is a UK calendar date (`yyyy-MM-dd`, Europe/London). `recordedAt`, `validFrom`
  and `createdAt` are UTC times.
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
  "status": "approved",
  "tonnageBand": "up_to_5000",
  "statusHistory": [
    { "status": "approved", "effectiveDate": "2026-10-01", "recordedAt": "2026-10-01T00:00:00Z" }
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
      "validFrom": "2027-01-01T00:00:00Z",
      "status": "approved",
      "statusHistory": [
        { "status": "approved", "effectiveDate": "2027-01-01", "recordedAt": "2026-10-01T00:00:00Z" }
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
2. Read `statusHistory`, which is oldest first.
3. Take the latest entry whose `effectiveDate` is on or before X. Its `status` is the answer.
4. If no entry is on or before X, the history has no status for that date. The accreditation's
   current `status` still reads `approved` if its only entry starts later, as the example's overseas
   site does (effective `2027-01-01`), so treat `null` as "not yet effective on X".

```js
// history: the accreditation's statusHistory. date: 'yyyy-MM-dd', a UK calendar date.
// ISO dates compare correctly as strings. Returns null if no entry is effective on that date,
// even when the response's current `status` says approved (see Status and dates).
function statusOn(history, date) {
  const applicable = history.filter((entry) => entry.effectiveDate <= date)
  return applicable.length ? applicable[applicable.length - 1].status : null
}
```

For the exporter above, whose history is one `approved` entry effective `2026-10-01`:
`statusOn(history, '2026-12-01')` is `approved`, and `statusOn(history, '2026-09-30')` is `null`.
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
      "status": "approved",
      "tonnageBand": "up_to_5000"
    },
    {
      "organisationNumber": 990006,
      "registrationNumber": "R26ER9900060000AL",
      "accreditationNumber": "A28ER9900060000AL",
      "accreditationYear": 2028,
      "material": "Aluminium",
      "wasteProcessingType": "reprocessor",
      "status": "approved",
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

For an exporter, `overseasSites` in the detail response lists them. Use the site routes to page
them or to fetch one by `orsId`. Only sites included in the accreditation are returned: a site the
organisation registered but did not accredit is a `404`. Withdrawn interim sites are left out
unless you add `includeWithdrawn=true`.

### Is a registration or accreditation I cannot find really missing?

A `404` with the code `accreditation_not_found` means there is no **granted** accreditation for
that organisation, registration and year. That covers an accreditation that is still an
application, one approved before 2027, and one that does not exist. The test data has an example of
each.

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
| 404 | `accreditation_not_found` | no granted accreditation for that organisation, registration and year |
| 404 | `overseas_site_not_found` | no such overseas site on the accreditation |
| 400 | `invalid_organisation_number`, `invalid_registration_number`, `invalid_year`, `invalid_ors_id`, `invalid_paging`, `invalid_include_withdrawn` | bad input |
| 405 | | any method other than GET |

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

- `GET /v1/accreditations` can be filtered by `organisationNumber` and `registrationNumber` as well
  as `year`. The filters combine with AND.
- Getting an accreditation now returns the complete accreditation: an exporter's accredited
  overseas sites, with their interim sites and status history, are in the same response under
  `overseasSites`, so one request is enough. `includeWithdrawn=true` includes withdrawn interim
  sites. The separate overseas-site routes are unchanged.
- A record that cannot be addressed by its organisation, registration and accreditation numbers is
  left out of the response and logged by us, so one bad record does not fail a page.

Existing calls keep working. If your client rejects fields it does not know, allow `overseasSites`
on the accreditation.

## Test data

The same nine fixed records exist in dev, test and ext-test. They are test data, owned by us,
and are put back to this state whenever the service starts. Do not rely on them in prod.

| Organisation | Registration | Accreditation | Year | What it is |
| --- | --- | --- | --- | --- |
| `990001` | `R26EX9900010002PA` | `A27EX9900010002PA` | 2027 | exporter, paper, 1 overseas site (`001`) with 1 interim site |
| `990002` | `R26EX9900020002FB` | `A27EX9900020002FB` | 2027 | exporter, fibre, 10 overseas sites (`001` to `010`) each with 2 interim sites |
| `990003` | `R26ER9900030000PL` | `A27ER9900030000PL` | 2027 | reprocessor, plastic |
| `990004` | `R26ER9900040000ST` | `A27ER9900040000ST` | 2027 | reprocessor, steel (the metal example) |
| `990005` | `R26EX9900050002WD` | `A27EX9900050002WD` | 2027 | exporter, wood, 3 registered overseas sites of which only `001` and `002` are accredited, and a withdrawn interim site on `001` (returned only with `includeWithdrawn=true`). `003` is a 404 |
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
