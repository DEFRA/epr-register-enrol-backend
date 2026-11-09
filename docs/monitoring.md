# Monitoring the accreditation API

RA-651. How to see, per consumer, how the accreditation API for service clients (ReEx and others)
is behaving, and what to alert on. Production is watched through logs and Grafana alerts, so
everything here comes from structured log lines. Nothing is exported as a metric.

Dashboards and alert rules are created in Grafana, not in this repo. This page is the definition to
build them from, and the list of log fields they depend on. Tests pin those fields
(`AccreditationApiEndpointTests`, `AccreditationApiMapperTests`,
`UnservableAccreditationCheckServiceTests`), so renaming one fails a build instead of quietly
emptying a panel.

## What the service logs

Structured values appear in the JSON log under `labels.<Name>`; the logger is `log.logger` and the
level is `log.level`.

| Event | Logger | Level | Fields (`labels.*`) |
| --- | --- | --- | --- |
| One line per authenticated service client request | `EprRegisterEnrolBackend.ServiceClientAccess` | Information | `ClientId`, `ClientName`, `Method`, `Route`, `OrganisationNumber`, `RegistrationNumber`, `Year`, `Status` (number), `DurationMs` (number) |
| A request refused at authentication, message starts `Service client auth failed` | `EprRegisterEnrolBackend.Auth.ServiceClientAuthenticationHandler` | Warning | `Code`, `Reason`, `ClientId` (as claimed, `(none)` if absent), `CorrelationId` |
| A record dropped from a response, message starts `Dropped a malformed accreditation` | `EprRegisterEnrolBackend.AccreditationApi` | Warning | `ApplicationId`, `Reason` |
| The unservable check found records, message starts `Approved accreditations the accreditation API cannot serve` | `EprRegisterEnrolBackend.AccreditationApi.UnservableAccreditationCheckService` | **Error** | `NoOrganisationNumber`, `NoRegistrationNumber`, `NoAccreditationNumber`, `FromYear` |
| The unservable check found nothing | same | Information | `FromYear` |

Auth failure `Code` values: `missing_header`, `invalid_signature`, `expired_timestamp`,
`reused_nonce` and the other codes in `docs/reex-api-onboarding.md`.

A request refused at authentication has no access-log line (the access log runs after
authentication), so auth failures and requests are counted from different events.

The unauthenticated `ClientId` is whatever the caller claimed. It is not a secret, and the secret,
signature and nonce are never logged.

## The check for records the API cannot serve

A granted application with no organisation, registration or accreditation number is left out of
every response by the query, with no line per request, so a consumer would just never see it. The
`UnservableAccreditationCheckService` counts them, by reason, 30 seconds after start-up and then
once a day, and logs the counts only, never anything about an application.

- Any finding is logged at **Error**, so the existing `5xx and Errors` alert reports it with no new
  rule. A repeat of the same finding each day is intended: the data is still wrong.
- The counts are per reason. An application missing two numbers is counted under both.
- It runs on every instance, so expect one line per instance per run.
- Settings, under `AccreditationApiMonitoring`: `Enabled` (default `true`), `InitialDelay`
  (default 30 seconds) and `Interval` (default 1 day). A failure to count is a Warning, not an
  Error, and the next run retries.
- Only an Error is produced if the data is genuinely wrong, so a clean environment has no start-up
  Error (see RA-733).

To act on an alert: the counts say how many and why. Find the applications in Mongo (approved,
year from 2027, missing the named reference) and fix the data, or tell the owner of the process that
produced them.

## Building it in CDP

Follows the CDP monitoring guide
(`portal.cdp-int.defra.cloud/documentation/how-to/monitoring.md`). Everything is built once, in the
**dev** Grafana, and promoted to the other environments from the Portal.

- **Dashboards.** In the playground folder `Playground/epr-register-enrol-backend-monitoring`, choose
  New, then New Dashboard, then Add visualization, and pick the OpenSearch datasource. Name it
  `epr-register-enrol-backend (custom)`: a dashboard that does not follow the `service-name (custom)`
  convention is not promoted to the other environments, and the name must be no longer than 255
  characters. Promote it with the Promote button in the Portal, under the service's Diagnostics tab.
- **Alerts.** In `metrics.dev.cdp-int.defra.cloud/alerting/list`, choose New alert rule and the
  OpenSearch datasource. Save it in the same playground folder with the evaluation group
  `epr-register-enrol-backend-advanced-alerts`, which the platform relies on. Promote one alert at a
  time from the Diagnostics tab. Do not rename an alert while promoting it, as that creates a
  duplicate. Once promoted it lives under `CDP_Tenants/epr-register-enrol-backend`.
- **Timing.** An alert has an evaluation interval (set by the group), a pending period (how long a
  breach must last before it fires) and a keep-firing period (how long it stays active after
  recovery). Use the pending period for the "for 5 minutes" conditions below.
- **Query language: Lucene.** OpenSearch defaults to DQL, but Grafana uses **Lucene**, so switch the
  query to Lucene by clicking the DQL link on the right of the query bar. A query copied from
  OpenSearch Dashboards will not work as it is.

Examples in Lucene, to check against the index in Explore (the field types decide whether the range
form works):

| Use | Lucene |
| --- | --- |
| Access log lines | `log.logger:"EprRegisterEnrolBackend.ServiceClientAccess"` |
| Server errors | `log.logger:"EprRegisterEnrolBackend.ServiceClientAccess" AND labels.Status:[500 TO *]` |
| Auth failures | `log.logger:"EprRegisterEnrolBackend.Auth.ServiceClientAuthenticationHandler" AND log.level:Warning` |
| Records the API cannot serve | `log.logger:"EprRegisterEnrolBackend.AccreditationApi.UnservableAccreditationCheckService" AND log.level:Error` |
| A record dropped from a response | `log.logger:"EprRegisterEnrolBackend.AccreditationApi" AND log.level:Warning AND message:"Dropped a malformed accreditation"` |

For the per-consumer panels, group by `labels.ClientName` (or `labels.ClientId` for auth failures).

## Dashboard: one row per consumer

Group every panel by `labels.ClientName`.

| Panel | Source | Calculation |
| --- | --- | --- |
| Request rate | access log | count of lines, per minute, by `ClientName` |
| Server errors | access log | count where `labels.Status` >= 500, and as a share of requests |
| Client errors | access log | count where `labels.Status` is 400 to 499, by `Status` (404 and 400 are normal for a consumer's mistakes, so keep them apart from 5xx) |
| Latency | access log | p50, p95, p99 of `labels.DurationMs`, by `ClientName`, and by `Route` |
| Slowest routes | access log | p95 of `DurationMs` by `Route` |
| Auth failures | auth failure line | count by `Code` and by `ClientId` |
| Unservable records | check line | latest `NoOrganisationNumber`, `NoRegistrationNumber`, `NoAccreditationNumber` |
| Dropped from a response | drop line | count by `Reason` |

## Alerts

Built the same way as the existing ones: named `epr-register-enrol-backend - <what>`, in the
`Playground/epr-register-enrol-backend-monitoring` folder, sent to the team Slack channel
(`#epr-register-enrol-team-alerts-non-prod` for dev, test and the other non-production
environments, and `#epr-register-enrol-team-alerts-prod` for production).
The existing rules use a sum over a window against a threshold, and fire at 1.

| Alert | Fires when | Notes |
| --- | --- | --- |
| `5xx and Errors` (exists) | any 5xx or any Error-level line | Also reports **records the API cannot serve**, since that check logs at Error |
| `Auth failures` (new) | more than 10 lines starting `Service client auth failed` in 5 minutes for one `ClientId` | A wrong secret, an expired timestamp, or a replay. A burst for one client usually means it was given the wrong secret or its clock is out |
| `Slow requests` (new) | p95 of `DurationMs` above 2000 for 5 minutes, with at least 20 requests, for a `ClientName` | Per consumer, so a slow client does not hide another |
| `Dropped records` (new, optional) | any line starting `Dropped a malformed accreditation` in 5 minutes | Rarely fires, because the query already leaves most such records out. The daily check is the main signal |

The thresholds above (10 failures, 2 seconds, 20 requests) are proposals to confirm, not measured
values. Tune them after a week of real traffic.

## Where the numbers come from

The access log line is enabled by an override in `appsettings.json`
(`EprRegisterEnrolBackend.ServiceClientAccess: Information`), because the default log level is
Warning. The check's Information heartbeat has its own override in the same place. Without them the
lines would not be written.
