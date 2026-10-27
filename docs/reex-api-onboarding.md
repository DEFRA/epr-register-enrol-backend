# Accreditation API: onboarding guide for service clients

A read-only API for services (first consumer: ReEx) that need accreditation data held by the
Operator Backend. It is an integration point and **will change** when the OpenAPI spec (RA-644)
and versioned collections (RA-677) land; paths live under `/v1`.

## Base URLs

| Environment | Base URL |
| --- | --- |
| dev | `https://epr-register-enrol-backend.dev.cdp-int.defra.cloud` |
| test | `https://epr-register-enrol-backend.test.cdp-int.defra.cloud` |
| ext-test | `https://epr-register-enrol-backend.ext-test.cdp-int.defra.cloud` |
| perf-test | `https://epr-register-enrol-backend.perf-test.cdp-int.defra.cloud` |
| prod | `https://epr-register-enrol-backend.prod.cdp-int.defra.cloud` |

## Credentials

Each consumer gets its own **client id** and **secret** per environment. ReEx's client id is
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

## Endpoints

All require a granted (approved) accreditation for the organisation, registration and year.
Accreditations for 2027 onwards are served.

| Route | Returns |
| --- | --- |
| `GET /v1/accreditations?year=2027&pageNumber=1&limit=20` | a page of accreditation summaries across all organisations (limit max 100; `year` optional) |
| `GET /v1/organisations/{organisationNumber}/registrations/{registrationNumber}/accreditations/{year}` | the accreditation, with status and tonnage band history and signatories |
| `…/accreditations/{year}/overseas-sites?pageNumber=1&limit=20&includeWithdrawn=false` | a page of the accredited overseas sites, each with its interim sites (limit max 100) |
| `…/accreditations/{year}/overseas-sites/{orsId}?includeWithdrawn=false` | one accredited overseas site (`orsId` is three digits, e.g. `001`) |

Notes: optional fields with no value are omitted. The shapes follow the v2 draft spec
(`docs/openapi/registration-accreditation-api.v2.yaml`). `status` is the latest `statusHistory`
entry effective today. Until effective-dated history is recorded, each history has one `approved`
entry, and `tonnageBand` is the operator's planned band. Signatories are returned only when getting
one accreditation, never in search. Only overseas sites included in the accreditation are served.
Withdrawn interim sites (`removedAt` set) are returned only with `includeWithdrawn=true`. BES
evidence is not served.

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

## Test data

The same four fixed records exist in dev, test and ext-test. They are test data, owned by us,
and are put back to this state whenever the service starts. Do not rely on them in prod.

| Organisation | Registration | Accreditation | Year | What it is |
| --- | --- | --- | --- | --- |
| `990001` | `R26EX9900010002PA` | `A27EX9900010002PA` | 2027 | exporter, paper, 1 overseas site (`001`) with 1 interim site |
| `990002` | `R26EX9900020002FB` | `A27EX9900020002FB` | 2027 | exporter, fibre, 10 overseas sites (`001` to `010`) each with 2 interim sites |
| `990003` | `R26ER9900030000PL` | `A27ER9900030000PL` | 2027 | reprocessor, plastic |
| `990004` | `R26ER9900040000ST` | `A27ER9900040000ST` | 2027 | reprocessor, steel (the metal example) |

For example: `GET /v1/organisations/990002/registrations/R26EX9900020002FB/accreditations/2027/overseas-sites`.

For a `404` example, use any registration number that does not exist, for example
`NO-SUCH-REGISTRATION`.

## Contact

ReEx team

## Consumer tests

`consumer-tests/` calls a deployed environment from outside the service using your own
credentials; see its README.
