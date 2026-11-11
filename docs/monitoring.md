# Monitoring the accreditation API

RA-651. How to see, per consumer, how the accreditation API for service clients (ReEx and others)
is behaving, and what to alert on. Production is watched through logs and Grafana alerts, so
everything here comes from log lines. Nothing is exported as a metric.

Dashboards and alert rules are created in Grafana, not in this repo. This page is the definition to
build them from.

## What CDP keeps from a log line

CDP does not index every field the service logs. It keeps a fixed, trimmed set of ECS fields
(see the CDP "Logging" guide); anything else, including our own structured values such as
`ClientName`, is **dropped** before it reaches OpenSearch. The values we log as `labels.*` and
`metadata.*` therefore **cannot be queried**. Only the fields below can, plus the rendered
`message` text, which is always indexed.

Checked against a real export of the access log from dev (the fields in the first column are what
is actually there).

| Field | Holds | Notes |
| --- | --- | --- |
| `log.logger` | the logger name | filter by it to pick an event |
| `log.level` | the level, in lower case | `info` is confirmed in dev; check the exact value for warnings and errors in Explore (probably `warn` and `error`) |
| `message` | the rendered log line | searchable as text |
| `user.id` | the client id, for example `epr-ReEx-Client` | **the way to split by consumer** |
| `http.response.status_code` | the response status, a number | reliable on the access log line only |
| `http.request.method`, `url.path` | the request | `url.path` contains the organisation and registration numbers |
| `event.duration` | the time taken, in **nanoseconds**, a number | on the access log line once RA-651 is deployed (see below) |
| `service.version`, `trace.id`, `span.id`, `container_name` | the build and the trace | |

Not available, so do not build on them: `labels.*`, `metadata.*`, `ClientName`, `Route`, a duration
or status field of our own. The client name is also partly masked in the message text.

## What the service logs

| Event | `log.logger` | Level | What to filter on |
| --- | --- | --- | --- |
| One line per authenticated service client request | `EprRegisterEnrolBackend.ServiceClientAccess` | info | `user.id`, `http.response.status_code`, `event.duration`; message `Service client request: clientId=... route=... status=200 durationMs=4` |
| A request refused at authentication | `EprRegisterEnrolBackend.Auth.ServiceClientAuthenticationHandler` | warning | message starts `Service client auth failed:` and carries the code and the claimed `clientId=`. It has **no** `user.id` (the caller is not authenticated) and its status field is not reliable |
| A record dropped from a response | `EprRegisterEnrolBackend.AccreditationApi` | warning | message starts `Dropped a malformed accreditation from the response:` with the application id and the reason |
| The unservable-records check found records | `EprRegisterEnrolBackend.AccreditationApi.UnservableAccreditationCheckService` | **error** | message `Approved accreditations the accreditation API cannot serve: noOrganisationNumber=N noRegistrationNumber=N noAccreditationNumber=N fromYear=2027` |
| The check found nothing | same | info | message `Every approved accreditation from 2027 can be served...` |
| The check itself failed (for example the database was unreachable) | same | warning | message starts `Checking for approved accreditations the accreditation API cannot serve failed`. This is **not** a data problem, and the next run retries, so keep it out of the "cannot serve" alert; it shares the logger and the words "cannot serve" with the error line |
| A CDP Uploader scan callback for a `fileUploadId` with no pending-upload record | `EprRegisterEnrolBackend.CdpUploader.Services.PendingUploadService` | **error** | message starts `Upload <fileUploadId> completion rejected: no pending upload record`. The record expired (24 hours after the upload started) or the call was not a real CDP callback; nothing is stored. The callback is still acknowledged with 200 so CDP Uploader does not retry it, so this logs once per callback; the existing `5xx and Errors` alert reports it |

Auth failure codes: `missing_header`, `invalid_signature`, `expired_timestamp`, `reused_nonce` and
the others in `docs/reex-api-onboarding.md`. They appear in the message text.

A request refused at authentication has no access log line (the access log runs after
authentication), so auth failures and requests are counted from different events.

### Latency: `event.duration`

The access log line writes the time taken as a property named `ElapsedMilliseconds`. The ECS log
formatter turns exactly that name into `event.duration`, in nanoseconds, which is a field CDP
keeps. Any other name would be dropped. A test (`AccessLogEcsMappingTests`) pins the name, so
renaming it fails a build instead of silently emptying a latency panel.

Lines logged before this change is deployed have the duration only inside the message text
(`durationMs=4`), so they cannot be charted. The latency panels fill in from the first request
after the deploy. In Grafana set the unit to nanoseconds (or convert: 2 seconds is 2,000,000,000).

## The check for records the API cannot serve

A granted application with no organisation, registration or accreditation number is left out of
every response by the query, with no line per request, so a consumer would just never see it. The
`UnservableAccreditationCheckService` counts them, by reason, 30 seconds after start-up and then
once a day, and logs the counts only, never anything about an application.

- Any finding is logged at **error**, so the existing `5xx and Errors` alert reports it with no new
  rule. A repeat of the same finding each day is intended: the data is still wrong.
- The counts are in the message text. An application missing two numbers is counted under both.
- It runs on every instance, so expect one Error line per instance per run.
- If the count cannot run (for example the database is unreachable) it logs a **warning**, not the
  error, so that does not read as a data problem. A setting out of range is replaced by its default
  with a warning, and never stops the host.
- Settings, under `AccreditationApiMonitoring` (the defaults are in `appsettings.json`; to switch the
  check off set the environment variable `AccreditationApiMonitoring__Enabled=false`): `Enabled` (default `true`), `InitialDelay`
  (default 30 seconds) and `Interval` (default 1 day). A failure to count is a warning, not an
  error, and the next run retries.
- A clean environment has no start-up error (see RA-733).

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

Queries in Lucene, using only fields CDP keeps. Check them in Explore first.

| Use | Lucene |
| --- | --- |
| Access log lines | `log.logger:"EprRegisterEnrolBackend.ServiceClientAccess"` |
| Server errors | `log.logger:"EprRegisterEnrolBackend.ServiceClientAccess" AND http.response.status_code:[500 TO 599]` |
| Client errors | `log.logger:"EprRegisterEnrolBackend.ServiceClientAccess" AND http.response.status_code:[400 TO 499]` |
| One consumer | add `AND user.id:"epr-ReEx-Client"` |
| Auth failures | `log.logger:"EprRegisterEnrolBackend.Auth.ServiceClientAuthenticationHandler" AND message:"Service client auth failed"` |
| One auth failure code | add `AND message:"invalid_signature"` (or `expired_timestamp`, `reused_nonce`, `missing_header`) |
| Auth failures for one client | add `AND message:"epr-ReEx-Client"` |
| A record dropped from a response | `log.logger:"EprRegisterEnrolBackend.AccreditationApi" AND message:"Dropped a malformed accreditation"` |
| Records the API cannot serve | `log.logger:"EprRegisterEnrolBackend.AccreditationApi.UnservableAccreditationCheckService" AND message:"noOrganisationNumber"` (the counts are only in the error line, so this does not match the failure warning; add `AND log.level:error` once you have confirmed the exact value) |
| The check failed to run | `log.logger:"EprRegisterEnrolBackend.AccreditationApi.UnservableAccreditationCheckService" AND message:"Checking for approved accreditations"` |

## Dashboard: one row per consumer

Split every request panel by `user.id`.

| Panel | Query | Metric and grouping |
| --- | --- | --- |
| Request rate | access log lines | Count, group by Terms `user.id`, then Date histogram |
| Server errors | server errors | Count, same grouping |
| Client errors | client errors | Count, group by Terms `http.response.status_code`. 404 and 400 are normal for a consumer's mistakes, so keep them apart from 5xx |
| Latency | access log lines | Percentiles (50, 95, 99) of `event.duration`, group by Terms `user.id`, then Date histogram. Unit nanoseconds. Fills in after the deploy |
| Auth failures | auth failures | Count, then Date histogram; add a second panel per code if useful |
| Unservable records | the check's lines | Logs visualisation, to read the counts in the message |
| Dropped from a response | dropped record lines | Logs visualisation, to read the reason |

## Alerts

Built the same way as the existing ones: named `epr-register-enrol-backend - <what>`, in the
`Playground/epr-register-enrol-backend-monitoring` folder. They go to the team Slack channels:
`#epr-register-enrol-team-alerts-non-prod` for dev, test and the other non-production environments,
and `#epr-register-enrol-team-alerts-prod` for production. The existing rules use a sum over a
window against a threshold, and fire at 1.

| Alert | Fires when | Notes |
| --- | --- | --- |
| `5xx and Errors` (exists) | any 5xx or any error-level line | Also reports **records the API cannot serve**, since that check logs at error |
| `Auth failures` (new) | more than 10 auth failure lines in 5 minutes | Filter by `message:"epr-ReEx-Client"` for one consumer. A burst usually means a wrong secret or a clock that is out |
| `Slow requests` (new) | p95 of `event.duration` above 2,000,000,000 (2 seconds) for 5 minutes, per `user.id` | Only works after the deploy. Add a count condition (at least 20 requests) so a quiet period does not trigger it |
| `Dropped records` (new, optional) | any dropped record line in 5 minutes | Rarely fires, because the query already leaves most such records out. The daily check is the main signal |

The thresholds (10 failures, 2 seconds, 20 requests) are proposals to confirm, not measured
values. Tune them after a week of real traffic.

## Where the lines are enabled

The access log line is enabled by an override in `appsettings.json`
(`EprRegisterEnrolBackend.ServiceClientAccess: Information`), because the default log level is
Warning. The check's heartbeat has its own override in the same place. Without them the lines would
not be written.
