# epr-register-enrol-backend

Core delivery C# ASP.NET backend template.

- [Install MongoDB](#install-mongodb)
- [Inspect MongoDB](#inspect-mongodb)
- [Configuration](#configuration)
- [Testing](#testing)
- [Running](#running)
- [Accreditation Applications API](#accreditation-applications-api)
- [Dependabot](#dependabot)
- This is a test

### Docker Compose

A Docker Compose template is in [compose.yml](compose.yml).

A local environment with:

- Localstack for AWS services (S3, SQS)
- Redis
- MongoDB
- This service.
- A commented out frontend example.

```bash
docker compose up --build -d
```

A more extensive setup is available in [github.com/DEFRA/cdp-local-environment](https://github.com/DEFRA/cdp-local-environment)

### MongoDB

#### MongoDB via Docker

See above.

```
docker compose up -d mongodb
```

The compose `mongodb` service runs as a **single-node replica set** (`rs0`), because the versioned domain
model writes with multi-document transactions, which MongoDB only supports on a replica set. A healthcheck
initiates the set on first start. From the host, connect with `directConnection=true`, because the set
advertises its member as `mongodb:27017`, which only resolves inside the compose network:

```
mongosh "mongodb://127.0.0.1:27017/?directConnection=true"
```

Set `Mongo__DatabaseUri` to `mongodb://127.0.0.1:27017/?directConnection=true` when running the service
outside compose. An existing `mongodb-data` volume keeps its data; the healthcheck initiates the set on top of it.

#### MongoDB locally

Alternatively install MongoDB locally:

- Install [MongoDB](https://www.mongodb.com/docs/manual/tutorial/#installation) on your local machine
- Start MongoDB:

```bash
sudo mongod --dbpath ~/mongodb-cdp
```

#### MongoDB in CDP environments

In CDP environments a MongoDB instance is already set up
and the credentials exposed as enviromment variables.

#### Domain model flags

The domain model (`docs/analysis/register-enrol-domain-model-design.md`) ships in phases, each behind a
`DomainModel:*` flag that is off by default, except `LegacyWrites`, which is on by default and is turned off to make
the cut (phase 6). Phases 0 and 1 are released together, and so are phases 2 and 3:
`DualWriteSeed` and `DualWrite` are switched on at the same time in each environment, though either can be turned off
alone in a rollback. See §6 of the design doc for the release plan.

- `DomainModel__ExpandEnabled=true` (phase 1) builds the empty versioned collections and their indexes at
  startup and adds the `domain-model-schema` check to `/health/ready`. If Mongo is unreachable at startup,
  it retries in the background until the check passes, so readiness recovers on its own. `compose.yml`
  turns it on locally. Turning it off again is the rollback.
- `DomainModel__DualWriteSeed=true` (phase 2, needs `ExpandEnabled`) makes Seed also write the Organisation,
  Registration and overseas sites, and stamp their ids on the application. A failure is logged and counted,
  and the seed still succeeds. Turning it off is the rollback.
- `DomainModel__DualWrite=true` (phase 3, needs `ExpandEnabled`; the service refuses to start without it) makes
  every write path write the legacy application and the domain model in one transaction. A failed model write
  fails the request and rolls the legacy write back. Turning it off is the rollback; the model is then stale until
  it is truncated and the migration re-run.
- `DomainModel__MigrateEnabled=true` (phase 4, needs `DualWrite`; the service refuses to start without it) migrates
  every legacy application not yet in the domain model, one organisation and registration group per transaction, in
  the background at each startup. It skips what dual-write has already migrated and picks up where it stopped. A group
  it can't migrate shows as Degraded on `/health/ready` and is tried again at the next startup.
- `DomainModel__ReconcileEnabled=true` (phase 4, test environments only, needs `DualWrite`, and `MigrateEnabled` to have
  anything to report) compares every migrated
  application with its legacy document through the read model, at startup and every 6 hours, and logs lines starting
  `DomainModelReconciliation`: counts, field names and application ids, never values. It never runs when the CDP
  environment is `prod`, whatever the flag says.
- `DomainModel__ReadFromModel=true` (phase 5, needs `DualWrite`, `DualWriteSeed` and `ExpandEnabled` outside Development;
  the service refuses to start without them) serves every application read from the domain model instead of the legacy
  document. Turning it off is the rollback.
- `DomainModel__LegacyWrites=false` (phase 6, **the cut**; on by default) stops writing the legacy
  `accreditationApplications` collection: every application write changes the domain model only, and legacy is frozen
  as it stood at the cut. It needs `ReadFromModel`, `DualWrite`, `DualWriteSeed` and `ExpandEnabled` in every
  environment, Development included, and `FeatureFlags__InYearChangesEnabled`, `MigrateEnabled` and `ReconcileEnabled`
  off; the service refuses to start otherwise. **There is no rollback**: writes made after the cut exist only in the
  model, and nothing writes them back to legacy. Turn it off only after the soak and sign-off in
  `tools/domain-model/cut-readiness.md`, and run that runbook's checks first.
- `DomainModel__DiagnosticsEnabled=true` (test environments only) runs the diagnostics below.

#### Domain model diagnostics (test environments only)

Set `DomainModel__DiagnosticsEnabled=true` to make the service run, once at startup, a **transaction smoke test**
(does this Mongo support multi-document transactions?) and a **read-only survey** of `accreditationApplications`
for the legacy shapes the domain model migration must handle. Each logs one line, `DomainModelDiagnostics
transactions ...` and `DomainModelDiagnostics survey ...`, with counts only and no personal data. It is off by
default and is not meant for production, where this team cannot examine the data. For production, the same
survey is packaged as `tools/domain-model/legacy-data-survey.js`, which someone with production access runs with
`mongosh "<connection string>" --quiet --file tools/domain-model/legacy-data-survey.js`; it prints counts only. See
`docs/analysis/register-enrol-domain-model-design.md` (B0, B1).

### Inspect MongoDB

To inspect the Database and Collections locally:

```bash
mongosh
```

You can use the CDP Terminal to access the environments' MongoDB.

### Configuration

Config is loaded via ASP.NET Core's standard configuration providers
(`appsettings.json` → `appsettings.{Environment}.json` → environment
variables). Nested `Section:Key` config binds from the env var form
`SECTION__KEY` (double underscore); the CDP-secrets-tab items instead use a
flat `UPPER_SNAKE_CASE` name — see the comments in
[`EprRegisterEnrolBackend/Program.cs`](EprRegisterEnrolBackend/Program.cs)
for why.

| Variable | Secret? | Local default | Description |
| --- | --- | --- | --- |
| `Mongo__DatabaseUri` | No | `mongodb://127.0.0.1:27017/?directConnection=true` | MongoDB connection string (AWS IAM auth in deployed environments) |
| `Mongo__DatabaseName` | No | `epr` | MongoDB database name |
| `App__BaseUrl` | No | `http://localhost:5000` | This service's own public base URL, used for CDP callback/status URLs |
| `CdpUploader__Url` | No | `http://localhost:7337` | Base URL of the CDP Uploader service |
| `CdpUploader__SamplingPlanBucket` | No | `sampling-plans` | S3 bucket for sampling-plan uploads |
| `CdpUploader__BesEvidenceBucket` | No | `bes-evidence` | S3 bucket for BES-evidence uploads |
| `CdpUploader__GenericFilesBucket` | No | `file-uploads` | S3 bucket for other file uploads |
| `ReExApi__BaseUrl` | No | _(blank)_ | Base URL of the external ReEx accreditation/organisations API |
| `REEX_API_BASIC_AUTH_USERNAME` | **Yes** | _(blank — stub adapter used instead)_ | Basic Auth username for the ReEx API above |
| `REEX_API_BASIC_AUTH_PASSWORD` | **Yes** | _(blank)_ | Basic Auth password for the ReEx API above |
| `ReExApi__UseStub` | No | `false` | When `true` outside Development, every ReEx lookup is answered by the stub adapter (e.g. perf-test) |
| `TestOrganisations__Enabled` | No | `false` | Turns on E2E test organisations and the mgmt-tests caller (see below). The service refuses to start with it `true` when `ENVIRONMENT` is `prod` |
| `Migrations__Ra691AccreditationReferenceCorrection__Enabled` | No | `false` | RA-691 one-off: corrects the three incorrectly generated 2027 accreditation references (Infinity Plastic Solutions Ltd 500527, PLASGRAN LIMITED 500105, Kronospan Limited 500108) at startup. On its own it is a dry run that logs each `org: old -> new` change and writes nothing |
| `Migrations__Ra691AccreditationReferenceCorrection__Apply` | No | `false` | RA-691: with `Enabled`, writes the corrections. Has no effect (the run stays a dry run, with an error log) until `SpotCheckConfirmedBy` is also set |
| `Migrations__Ra691AccreditationReferenceCorrection__SpotCheckConfirmedBy` | No | _(blank)_ | RA-691: name of whoever reviewed the dry-run output against the live records. Required for `Apply`; logged with each applied change |
| `Migrations__Ra764OrganisationNameCorrection__Enabled` | No | `false` | RA-764 one-off: renames organisation 500069 from `Encirc Ltd` to `VIDRALA UK & IRELAND LIMITED` on its 2027 applications under both its glass remelt registrations, `R26NR5000690003GR` (NIEA) and `R26ER5002260055GR` (EA), and on its domain model organisation, if there is one, at startup. Any other registration, an accreditation not for its record's registration (`A27NR500069####GR` or `A27ER500226####GR`), another name or a second ReEx organisation id aborts the run before anything is written. Applications are also looked up by those two registrations, so one under them holding another OrgId or ReEx organisation id aborts rather than being missed. The completion line counts the applications per registration, so the spot check can confirm both are covered. On its own it is a dry run that logs each `"old" -> "new"` rename and writes nothing. It logs at `Information` (an override in `appsettings.json`, as the default is `Warning`); search Grafana with `log.logger:"EprRegisterEnrolBackend.AccreditationApplication.Startup.OrganisationNameCorrectionMigration"` |
| `Migrations__Ra764OrganisationNameCorrection__Apply` | No | `false` | RA-764: with `Enabled`, writes the renames. Has no effect (the run stays a dry run, with an error log) until `SpotCheckConfirmedBy` is also set. The legacy applications and the domain organisation are written separately: after a partial run, leave the flag on and re-run until the log shows the domain model organisation renamed - do not turn it off part-way |
| `Migrations__Ra764OrganisationNameCorrection__SpotCheckConfirmedBy` | No | _(blank)_ | RA-764: name of whoever reviewed the dry-run output against the live records. Required for `Apply`; logged with each applied change |
| `Migrations__Ra763LastYearsAccreditationBackfill__Enabled` | No | `false` | RA-784 one-off: gives every 2027 application last year's accreditation number from the 5 November 2026 public register, at startup, and reports issued 2027 numbers that don't fit the register (RA-785 corrects them). Never creates an application. On its own it is a dry run that logs each application's outcome and a report, and writes nothing. It logs at `Information` (an override in `appsettings.json`, as the default is `Warning`) |
| `Migrations__Ra763LastYearsAccreditationBackfill__Apply` | No | `false` | RA-784: with `Enabled`, writes the numbers, through the dual-write unit of work, so with `DomainModel__DualWrite` on the domain model takes them too. Has no effect (the run stays a dry run, with an error log) until `SpotCheckConfirmedBy` is also set |
| `Migrations__Ra763LastYearsAccreditationBackfill__SpotCheckConfirmedBy` | No | _(blank)_ | RA-784: name of whoever reviewed the dry-run output against the live records. Required for `Apply`; logged with each applied change |
| `Migrations__Ra788DuplicateApplicationDeletion__Enabled` | No | `false` | RA-788 one-off (INC2560371): finds duplicate live applications in the legacy `accreditationApplications` collection (same organisation id, organisation name, registration id, material and year; the registration number is not compared) and deletes the `Saved` drafts with no `orgId`, keeping the other record(s). On its own it is a dry run: it logs every group (with its organisation id and name), candidate and keeper a summary line first and an outcome line last (one line per group, with its candidates and keepers), and writes nothing. Every log line starts `RA-788` |
| `Migrations__Ra788DuplicateApplicationDeletion__Apply` | No | `false` | RA-788: with `Enabled`, deletes, one transaction per group, guarded on version, status and the group key. Has no effect (the run stays a dry run, with an error log) until `SpotCheckConfirmedBy` is set and both `ExpectedDeletions` and `ExpectedApplicationIds` equal the candidates found this run. With no candidates it is treated as already done (info log). Re-running is safe. Each group deleted logs one `RA-788 APPLIED` line. A domain-model copy of a deleted application is not deleted |
| `Migrations__Ra788DuplicateApplicationDeletion__SpotCheckConfirmedBy` | No | _(blank)_ | RA-788: name of whoever spot-checked this environment's dry run. Required for `Apply`. Not logged (it is a person's name); the authorisation is recorded in cdp-app-config's history |
| `Migrations__Ra788DuplicateApplicationDeletion__ExpectedDeletions` | No | _(unset)_ | RA-788: the candidate count from this environment's reviewed dry run (`candidates=` in its summary line). Required for `Apply`: if this run finds a different number it stays a dry run and logs an error |
| `Migrations__Ra788DuplicateApplicationDeletion__ExpectedApplicationIds` | No | _(unset)_ | RA-788: the candidate ids from this environment's reviewed dry run, comma separated (`candidate-ids=` in its summary line). Required for `Apply`: if this run's candidate ids are not exactly this set (a swapped id with the same count included) it stays a dry run, deletes nothing and logs an error |
| `Diagnostics__Ra292IsNewSiteAudit` | No | `false` | RA-794: read-only audit of overseas sites whose `isNewSite` may be a wrongly-defaulted `true`. Logs per-bucket counts and application ids at `Information` (an override in `appsettings.json`) and writes nothing. Needs `Diagnostics__Ra292DeployedAt`. See [docs/diagnostics/ra-794-isnewsite-correction.md](docs/diagnostics/ra-794-isnewsite-correction.md) |
| `Diagnostics__Ra292DeployedAt` | When either RA-794 flag is on | _(none)_ | RA-794: when RA-292 was deployed to this environment (ISO 8601, UTC). Only applications created before it are scanned. Missing or unparseable: the audit and correction refuse to run and log an error |
| `Diagnostics__Ra292CorrectIsNewSite` | No | `false` | RA-794: the correction. Sets `isNewSite` to `false` only on sites ReEx proves are carried over. Refuses (error log) without `Diagnostics__Ra292SpotCheckConfirmedBy`; a dry run unless `Diagnostics__Ra292CorrectIsNewSiteApply` is `true`. Run it to completion before management-be's correction |
| `Diagnostics__Ra292SpotCheckConfirmedBy` | With `Diagnostics__Ra292CorrectIsNewSite` | _(none)_ | RA-794: name of whoever spot-checked the audit output against the operator database; logged with each applied correction |
| `Diagnostics__Ra292CorrectIsNewSiteApply` | No | `false` | RA-794: with the correction on, writes the corrections. Otherwise it is a dry run that logs what it would change |
| `CaseWorking__Url` | No | `http://localhost:8085` | Base URL of `epr-register-enrol-management-be` |
| `CaseWorking__UseStub` | No | `false` (Development) | When `true`, submissions to management-be are stubbed instead of sent |
| `FeatureFlags__InYearChangesEnabled` | No | `false` | Maps the in-year changes routes (registration status and change requests); off, they 404. Flip together with the frontend's `IN_YEAR_CHANGES_ENABLED`. Outside Development the service refuses to start with it `true` unless `DomainModel__DualWrite` is `true` too: the routes read the domain model, which on CDP only dual-write fills. A registration only shows once dual-write has written it or `DomainModel__MigrateEnabled` has migrated it |
| `AccreditationChanges__SubmitToCaseManagement` | No | `false` (`true` in Development) | RA-492: when `true`, submitting an in-year change request also sends it to management-be, which creates one `add-overseas-site` work item per site (RA-739). Deploy a management-be that registers that work-item type **before** switching this on — an older management-be rejects unknown types. cdp-app-config declares it `false` in every CDP environment. `appsettings.Development.json` sets it `true` (RA-739), so running this service directly submits to the local management-be at `CaseWorking__Url`, as the monorepo's root compose stack does. If management-be isn't running, submitting a change request reopens it and returns 503. Set it `false` (or `CaseWorking__UseStub` `true`) to submit without management-be. This repo's own `compose.yml` has no management-be, so it keeps the flag `false` |
| `CASE_MANAGEMENT_API_SHARED_SECRET` | **Yes** | _(blank)_ | HMAC secret this service signs its outbound calls to management-be with — must match management-be's `AUTH_SHARED_SECRET__BACKEND` exactly |
| `CaseManagementAuth__ExpectedClientId` | No | `epr-register-enrol-management-be` | Expected `x-cdp-client-id` on inbound calls from management-be |
| `AUTH_SHARED_SECRET__MANAGEMENT_BE` | **Yes** | _(blank)_ | Verifies inbound calls from management-be — must match management-be's `OPERATOR_BACKEND_SHARED_SECRET` exactly |
| `AUTH_SHARED_SECRET__FRONTEND` | **Yes** | _(blank)_ | Verifies inbound calls from `epr-register-enrol-frontend` — must match frontend's `AUTH_SHARED_SECRET__BACKEND` exactly |
| `AUTH_SHARED_SECRET__MGMT_TESTS` | **Yes** | _(blank)_ | Optional in every environment: set only on non-prod environments running the E2E suite. A second accepted `Authorization: Bearer` on the frontend-authenticated routes, for the `epr-register-enrol-mgmt-tests` E2E suite — must match mgmt-tests' `OPERATOR_BACKEND_SHARED_SECRET`. Ignored (with a startup warning) unless `TestOrganisations__Enabled` is `true`; never set in prod |

The secret-shaped values above (`*_SHARED_SECRET*` and the two
`REEX_API_BASIC_AUTH_*` vars), except `AUTH_SHARED_SECRET__MGMT_TESTS`, which
is only ever set on non-prod environments running the E2E suite, are optional
in Development — the affected
handlers fall back to a stub adapter or header-trust mode when blank — but
required in every other environment. `GET /health/ready` reports any that
are missing by name via `RequiredConfigHealthCheck`.

`compose.yml` sets `REEX_API_BASIC_AUTH_USERNAME`/`REEX_API_BASIC_AUTH_PASSWORD`
to empty strings by default, so `docker compose up` runs against the stub
ReEx adapter; override them (and `ReExApi__BaseUrl`) in `compose.yml` or via
an env file for a real integration test. Example local/testing values:

```bash
CASE_MANAGEMENT_API_SHARED_SECRET=local-dev-case-management-secret-not-real
AUTH_SHARED_SECRET__MANAGEMENT_BE=local-dev-management-be-secret-not-real
AUTH_SHARED_SECRET__FRONTEND=local-dev-frontend-secret-not-real
REEX_API_BASIC_AUTH_USERNAME=local-dev-user
REEX_API_BASIC_AUTH_PASSWORD=local-dev-fake-password
```

#### E2E test organisations

With `TestOrganisations__Enabled=true`, an `organisationId` starting with the
lowercase prefix `e2e-` is a test organisation. Outside Development (where the
stub already answers everything), `TestOrganisationReExApiAdapter` sends every
ReEx lookup for such an organisation to `StubReExApiAdapter`, while every other
organisation still goes to the real ReEx API. For an `e2e-` organisation the stub
returns the name `E2E Organisation <organisationId>`, nation England, a stable
six-digit org number (900000–999999) and GOV.UK Notify's
`simulate-delivered@notifications.service.gov.uk` for every contact email.
`ENVIRONMENT` is the CDP platform environment name, and it is read straight
from the process environment.

### Logging

Every log line goes through Serilog (`Serilog` in `appsettings.json`) and out to stdout as an Elastic Common Schema
(ECS) JSON line, which CDP ships to OpenSearch. What CDP keeps of each line, and how to search it, is in
[docs/monitoring.md](docs/monitoring.md).

#### Burst smoothing

Lines don't go straight to the console. `BurstSmoothingSink`
([`Utils/Logging`](EprRegisterEnrolBackend/Utils/Logging/BurstSmoothingSink.cs)), the `SmoothBursts` entry in
`Serilog:WriteTo`, queues every line and passes it on to the console sink at a steady rate, in order. Logging never
waits for it.

Why: CDP's log shipping looks to drop bursts. What was seen on ext-test, which logs at `Information` (CDP's
`environments/ext-test/defaults.env` in cdp-app-config sets `Serilog__MinimumLevel__Default`, `__Override__Microsoft`
and `__Override__System` to `Information` for every service, over this repo's `Warning`):

- The 0.211.0 containers logged their start-up lines ("Now listening", "Application started", "Seeded 9 test
  accreditations", "seed complete").
- The 0.213.0 container ran the RA-784 backfill, which wrote about 400 lines (554 KB) in under 100 ms. Only its first
  line, `starting`, reached OpenSearch. None of the start-up lines logged in the same second did, although the container
  carried on serving requests and logged 256 request lines afterwards. None of them were in `cdp-broken-logs`, so they
  were lost before indexing.
- After #293 packed and paced the same backfill (50 lines, then a 500 ms pause), its next run on ext-test showed its
  results in OpenSearch.

The path is stdout, then Docker's `awsfirelens` log driver with its default buffer, then CDP's Fluent Bit sidecar (1 MB
chunks, two retries before a chunk is discarded), then OpenSearch, and AWS warns that Docker "might run out of buffer
memory and discard buffer messages". This is the working explanation, not a proven cause: the sidecar logs its drops
to a CloudWatch group we can't see, and nobody has checked CDP's raw copy of the logs in S3. If the lost lines turn out
to be in S3, they were lost after Fluent Bit, and smoothing stdout doesn't help. management-be got 534 lines through in
one second, so it is the burst, not the count, that looks to matter.

The settings are the `Args` of `Serilog:WriteTo:0` (`appsettings.json` sets them explicitly; the code falls back to the
same values). Override one with an environment variable such as `Serilog__WriteTo__0__Args__linesPerSecond`. A value
the sink can't run with (zero, negative, `burst` over `queueCapacity`, a flush timeout of zero or over 20 seconds) stops
the service starting, with an error naming the setting. A `TimeSpan` is read with `TimeSpan.Parse`, so write
`00:00:05`: a bare `5` means 5 days, and is refused.

The defaults are judgements, not measured limits. `burst` 50 is what #293 wrote at a time on ext-test, and got through.
`linesPerSecond` 200 is double #293's rate, to leave room for request logging, and well under the 534 lines a second
management-be got through. The sink counts lines, not bytes, because it can't know a line's size before the console sink
formats it. So a burst of 50 packed lines of about 10 KB is still about 500 KB at once, which is why one-off jobs must
keep their output small too (below).

| Setting | Default | What it does |
| --- | --- | --- |
| `linesPerSecond` | `200` | The most lines written a second, sustained |
| `burst` | `50` | Lines that go out at once after a quiet spell, before the rate applies (a token bucket). At most `queueCapacity` |
| `queueCapacity` | `10000` | Lines waiting to be written. A line that finds the queue full is dropped |
| `shutdownFlushTimeout` | `00:00:05` | How long shutdown waits for the queue to be written. At most `00:00:20`. Shutdown waits for the sink's reader for at most this plus 1 second (see below) |

Nothing is dropped silently while the service is running or stopping normally. Dropped lines are counted, and the next
line written is preceded by one Error line, `Dropped <n> log lines because the log queue was full or closed`. At shutdown, lines still queued after `shutdownFlushTimeout`
are reported, with any refused lines not yet reported, in one final Error line, `Dropped <n> log lines at shutdown: ...`.
Both have `log.logger` `EprRegisterEnrolBackend.Utils.Logging.BurstSmoothingSink`, and are at Error so that lost lines
are noticed: the existing `5xx and Errors` alert reports them. If, after `shutdownFlushTimeout`, the console sink is still stuck
writing a line 1 second after being told to stop (for example stdout has stopped draining), shutdown doesn't wait for
it: the final report goes to stderr through Serilog's `SelfLog` instead, and nothing more is written to the console. So
shutdown never waits for the sink's reader longer than `shutdownFlushTimeout` plus 1 second (6 seconds by default).
Writing the final report and closing the console sink after that aren't timed, the same as with a plain console sink.
The flush runs when the host disposes the logger, after the hosted services have stopped, inside ECS's 30-second stop
timeout. If the console sink itself fails on a line, the line is lost, the failure goes to stderr through Serilog's
`SelfLog`, and logging carries on.

The trade-off: a line is on stdout only once the reader has written it, not when the code logs it. If the process dies
without disposing the logger (an unhandled exception on a background thread, an out-of-memory kill, or ECS killing the
task after its stop timeout), whatever is still queued is lost with no report, and those can be the lines that explain
the crash. The queue is usually close to empty, so this is a few lines at most outside a burst.

The console sink formats each line on the reader, not on the thread that logged it. The ECS formatter would read the
trace and the thread from the thread formatting the line, so the formatter is `CdpEcsTextFormatter`
([`Utils/Logging`](EprRegisterEnrolBackend/Utils/Logging/CdpEcsTextFormatter.cs)). It writes `trace.id` and `span.id`
from what Serilog records on the line when it is logged, and `process.thread.id` and `process.thread.name` from the
thread the sink records then. Everything else is the stock `EcsTextFormatter`.

Development uses the same entry: `appsettings.Development.json` has no `WriteTo` of its own, because .NET merges arrays
by index and one would merge into this entry. So `docker compose` and the end-to-end stacks smooth bursts too.

#### Logging from one-off jobs, migrations and diagnostics

The sink caps the rate, not the number of lines, and a long burst still fills the queue. So a job that logs a lot:

- logs its summary (counts, outcome) first, so the result is in the log even if its detail is not;
- packs detail into fewer lines, several records to a line, keeping each line well under the 16 KB at which the
  container runtime splits a log line (aim for about 10 KB; the RA-784 backfill packs 40 register rows a line);
- never writes hundreds of lines back to back.

### Accreditation Applications API

RA-101 adds an `accreditationApplications` MongoDB collection and a full REST API for managing operator accreditation applications.

#### Collection setup

Create the collection with schema validation and indexes by running:

```js
import { createAccreditationApplicationsCollection } from "./EprRegisterEnrolBackend/Utils/Mongo/Scripts/create-accreditation-applications-collection.js";
await createAccreditationApplicationsCollection(
    "mongodb://localhost:27017",
    "epr",
    "accreditationApplications",
);
```

#### Endpoints

Base path: `api/v1/accreditation-applications/{organisationId}`

| Method   | Path                                                                      | Description                                                                                                                                                                                                                                                                             |
| -------- | ------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `POST`   | `/{organisationId}/{registrationId}/{materialType}/seed`                  | Create a new application, pre-populated via ReEx from the latest accreditation granted before that year (the year before when none is held here, RA-765)                                                                                                                                |
| `GET`    | `/{organisationId}`                                                       | List all applications for an organisation                                                                                                                                                                                                                                               |
| `GET`    | `/{organisationId}/{applicationId}`                                       | Get a single application                                                                                                                                                                                                                                                                |
| `PATCH`  | `/{organisationId}/{applicationId}/prns`                                  | Update the PRNs section                                                                                                                                                                                                                                                                 |
| `PATCH`  | `/{organisationId}/{applicationId}/business-plan`                         | Update the business plan section                                                                                                                                                                                                                                                        |
| `PATCH`  | `/{organisationId}/{applicationId}/sampling-plan`                         | Update the sampling plan section                                                                                                                                                                                                                                                        |
| `POST`   | `/{organisationId}/{applicationId}/files`                                 | Add a file to the sampling plan                                                                                                                                                                                                                                                         |
| `DELETE` | `/{organisationId}/{applicationId}/files/{fileId}`                        | Remove a file from the sampling plan                                                                                                                                                                                                                                                    |
| `POST`   | `/{organisationId}/{applicationId}/submit`                                | Submit the application (requires all sections `Completed`)                                                                                                                                                                                                                              |
| `POST`   | `/case-management/{workItemId}/status`                                    | Push a Case Management service work-item status change onto `ApplicationStatus` (Case Management service caller). 400 when `toStateId` is null or blank. A `toStateId` with no entry in `CaseManagementStatusMapping.StatusPushMap` sets no status and only advances the ordering watermark; it is logged at Error unless it is in `KnownIgnoredStateIds` (`queried`, which management-be pushes for a deadline change on a queried item)                                                                                                                                                                        |
| `GET`    | `/case-management/{organisationId}/{applicationId}/accreditation-summary` | Read-only accreditation summary of an Approved application for the Case Management service's Accreditation summary tab (RA-769): an allow-list of the application, selected overseas sites and their active interim sites, sampling plan and BES files (Case Management service caller) |

#### In-year change requests (RA-492)

Base path: `api/v1/accreditation-changes/{organisationId}/{applicationId}`, where `applicationId` is an **Approved** exporter accreditation application. A change request (`accreditationChangeRequests` collection, `InProgress` → `Submitted`) adds existing registered overseas sites (`selected: false`) to that accreditation; it reuses each site's `siteId` and ORS id and never modifies the approved application, so the sites stay registered until a regulator decision. At most one `InProgress` request exists per accreditation and it may hold several sites. The charge is £328 per site with no tonnage fee.

With `AccreditationChanges__SubmitToCaseManagement` on, submit sends the request to management-be as one `add-overseas-site` submission. management-be creates one work item per site in a single transaction and answers `{ submissionReference, workItems: [{ siteId, id, applicationReference }] }` (RA-739). Each site item stores its own `caseManagementReference` and `caseManagementWorkItemId`, matched by `siteId`, and the request stores the shared `submissionReference`, which the operator is not shown. The API returns `caseManagementReference` on each item, not on the request. A non-2xx response, a body that cannot be read, a missing submission reference or a submitted site without a reference is a failure: the request is reopened and submit returns 503. Unless management-be refused it with a 4xx (or Case Management is not configured), management-be may already hold the submission and replays a resubmit with the sites it first received, so the reopened request's sites are fixed (`sentToCaseManagement`): adding or removing one returns 409 until a resubmit succeeds. management-be owns these references. Each site's is an in-year change reference, `IYC-ORS-<yyyy>-<seq>` (for example `IYC-ORS-2026-00021`), not an application `AP…` reference, and the submission's is `SUB-<yyyy>-<seq>` (for example `SUB-2026-0001`). `<yyyy>` is the accreditation year, which a change request always has, with no fallback (RA-492). The in-year change `<seq>` is always five digits: it counts per change type per year from `00001`; past `99999` the number wraps to `00001` but management-be's unique index refuses the repeat, so in effect `99999` is a cap (RA-739). The submission `<seq>` counts per year from `0001` and grows past `9999` rather than wrapping. The source of truth for the formats is the "Application reference" section of management-be's [docs/work-items.md](https://github.com/DEFRA/epr-register-enrol-management-be/blob/main/docs/work-items.md). `StubCaseWorkingApiAdapter` (`CaseWorking__UseStub`) mirrors both formats with in-process counters that restart at 1 with the app.

| Method   | Path | Description |
| -------- | ---- | ----------- |
| `GET`    | _(base path)_ | Accreditation header, every site with `accreditationStatus`/`lockedFields`/pending request, the draft, every request, and `pendingOrsIds` |
| `POST`   | `/requests/current/sites` | Add a registered site (`{ "siteId": 1 }`) to the draft, creating it if needed; 409 if the site is accredited or already in a submitted request, or the draft's sites are fixed after a failed send |
| `PATCH`  | `/requests/current/sites/{siteId}` | Supply missing `country`, `coordinates`, `operationCodes`, `conditionsOfExport`; 400 if a field already has a value on the site (locked) |
| `DELETE` | `/requests/current/sites/{siteId}` | Remove a site: 200 with the draft, or 204 when the draft became empty and was deleted; 404 if the site isn't in the draft; 409 if the draft's sites are fixed after a failed send |
| `POST`   | `/requests/current/bes-evidence/initiate` | Start a CDP upload for BES evidence |
| `POST`   | `/requests/current/sites/{siteId}/bes-evidence/files` | Attach a scanned upload to a site in the draft, an add or (RA-682) an `update-overseas-site` item, which adds the file to the ORS's own on approval; 422 if the ORS takes no BES evidence (each item's `besEvidenceUploadAllowed`), 409 once Case Management may hold the item |
| `DELETE` | `/requests/current/sites/{siteId}/bes-evidence/files/{fileId}` | Remove a BES evidence file uploaded on the draft; 422 for a file the ORS already holds on an update item, which can't be removed |
| `POST`   | `/requests/current/submit` | Declaration (`fullName`, `jobTitle`, `paymentReference`); 400 without a payment reference (RA-739), 422 with per-site errors when incomplete, 503 if Case Management fails (the request is reopened), otherwise `Submitted` |
| `POST`   | `api/v1/accreditation-changes/case-management/{workItemId}/status` (full path, not under the base path) | Case Management service caller (RA-495): push a work item's status change onto its change request item. `approved` grants an add-ORS site in the same unit of work, accredited from the body's `effectiveDate` (`yyyy-MM-dd`, required on `approved`: 400 without it). 400 when `toStateId` is null or blank. A `toStateId` with no item status (`awaiting-decision`, `queried`, or one not in the table) changes no status; one not in the table and not known-ignored is logged at Error. The same body, auth and ordering guards as the accreditation application's `/case-management/{workItemId}/status` |

#### Seed request body

`registrationId` and `materialType` are route segments (see [Endpoints](#endpoints) above), not body fields:

```json
{ "year": 2025 }
```

Valid `materialType` route values: `Steel`, `Wood`, `Aluminium`, `Fibre`, `Glass`, `Paper`, `Plastic`

#### Application lifecycle

`Saved` → `Started` (on first section edit) → `Submitted` (on submit) → `Approved` or `Rejected` (`Queried` may occur after submission)

Each section tracks its own status: `NotStarted` → `InProgress` → `Completed` (`Submitted`/`Queried` reserved for future use)

#### External adapters

`IReExApiAdapter` and `ICaseWorkingApiAdapter` are wired to stub implementations locally. They log calls but do not make real HTTP requests. Replace with live implementations when integrating with ReEx and CaseWorking services.

Swagger UI is available at `/swagger` when running locally.

##### ReEx API (epr-backend) integration

`HttpReExApiAdapter` (`EprRegisterEnrolBackend/AccreditationApplication/Adapters/HttpReExApiAdapter.cs`) is the live implementation of `IReExApiAdapter`, used to seed a new accreditation application from an organisation's prior-year ReEx data (`POST /{organisationId}/{registrationId}/{materialType}/seed`, see [Endpoints](#endpoints) above). It calls two [epr-backend](https://github.com/DEFRA/epr-backend) endpoints, both via `ReExClient` (`EprRegisterEnrolBackend/ReEx/ReExClient.cs`):

| Method | Path | Called for | epr-backend source |
| --- | --- | --- | --- |
| `GET` | `/v1/organisations/{organisationId}` | Every seed: the full organisation record, resolved to the matching registration and its linked accreditation | [`src/routes/v1/organisations/get-by-id.js`](https://github.com/DEFRA/epr-backend/blob/main/src/routes/v1/organisations/get-by-id.js) |
| `GET` | `/v1/organisations/{organisationId}/registrations/{registrationId}/accreditations/{accreditationId}/overseas-sites` | Exporter registrations only, to populate `OverseasSites` on the seeded application | [`src/overseas-sites/routes/accreditation-list.js`](https://github.com/DEFRA/epr-backend/blob/main/src/overseas-sites/routes/accreditation-list.js) |

Seeding sequence (`HttpReExApiAdapter.GetAccreditationAsync`):

1. Fetch the organisation, locate the registration matching the requested `registrationId`, and derive whether it's a reprocessor or exporter registration.
2. Resolve the company's registered-office address (see the `ROA` comments in the adapter for the address/postcode fallback rules) and the registration's Regulator Nation, derived from that registration's own `SubmittedToRegulator` via `RegulatorNationMapper` (see the `RA-526` comments in the adapter and on `ReExAccreditationDto.Nation`) — unrecognised or missing regulator codes default to England.
3. Locate the accreditation linked to that registration and confirm it matches the requested year.
4. For exporter registrations only, call the overseas-sites endpoint and map the result onto the seeded application.
5. Map PRN issuance, business-plan, and contact-detail fields from the fetched organisation, together with the resolved address and Nation, into the returned `ReExAccreditationDto`.

`IReExApiAdapter.GetLinkedDefraOrganisationAsync` and `GetOrganisationNumberAsync` reuse the same organisation `GET` above; no other epr-backend endpoint is called.

`AccreditationApplicationEndpoints.Seed` then copies this `ReExAccreditationDto` field-for-field into the persisted `AccreditationApplicationModel`.

The full epr-backend API surface — including both endpoints above — is documented in its swagger definition, published as a static snapshot at [DEFRA/epr-re-ex-service: `docs/architecture/api-definitions/internal-api.yaml`](https://github.com/DEFRA/epr-re-ex-service/blob/main/docs/architecture/api-definitions/internal-api.yaml) (epr-backend itself serves the live version at `/swagger` when run locally; there's no swagger file checked into the epr-backend repo itself). Both endpoints above are documented there with full response schemas (`OrganisationResponse` and `AccreditationOverseasSitesResponse`).

### Service client APIs

Other services read this backend's data through two read-only APIs, both signed with a client id
and HMAC-SHA256 (`ServiceClientAuthenticationHandler`). Each client is configured as
`ServiceClients__Clients__N__Name` and `ServiceClients__Clients__N__ClientId` (non-secret config),
with its secret in CDP secrets as `AUTH_SHARED_SECRET__{NAME}` (and `AUTH_SHARED_SECRET__{NAME}_NEXT`
while it is rotated). A client with no secret is rejected. Both APIs share the per-client request
limit (`ServiceClients__RateLimitPermitsPerMinute`, 429), the database query time limit
(`ServiceClients__QueryTimeoutSeconds`, 503), the access log, and RFC 9457 problem errors.

| API | Paths | Who may call it | Contract |
| --- | --- | --- | --- |
| Accreditation API | `/v1/...` | Any configured service client (ReEx) | [`docs/openapi/registration-accreditation-api.v2.yaml`](docs/openapi/registration-accreditation-api.v2.yaml), [onboarding guide](docs/reex-api-onboarding.md). Before changing it, read [Changing the Accreditation API safely](docs/api-change-guide.md) |
| Admin API (RA-740) | `/admin/...` | Only the client named `admin` (`epr-register-enrol-admin`, secret `AUTH_SHARED_SECRET__ADMIN`); any other client gets 403 | [`docs/openapi/admin-api.yaml`](docs/openapi/admin-api.yaml) |

The Admin API is for the delivery team's support tool. `GET /admin/accreditation-applications`
searches every application in the Legacy `accreditationApplications` collection (any status, any
year), by `q` across the accreditation number, registration number, organisation number and
organisation name, and by `status` and `year`, most recently updated first. Each item is the
stored application, including personal and commercially sensitive data, so neither may be logged
by the caller.

For the Admin UI's case management search (RA-751), `GET /admin/accreditation-application-statuses`
gives the status of the Legacy application linked to each of up to 50 Case Management work items
(by `workItemId`, matched against the stored `caseManagementWorkItemId`), listing a work item with
no linked application as not found. `GET /admin/case-management-status-mapping` serves the table
the Case Management push receivers apply, which maps each pushed work item state to the
application status it sets.

### Testing

Tests run a full `WebApplication` backed by [Ephemeral MongoDB](https://github.com/asimmon/ephemeral-mongo). No mocking — tests read and write from an in-memory database.

```bash
dotnet test
```

The `AccreditationApplication` test suite covers:

- Endpoint integration (seed, CRUD, submit, approve, reject)
- Section status computation
- FluentValidation validators (PRNs, business plan, seed, submit)
- `ApplicationReferenceService`

### Running

Run CDP-Deployments application:

```bash
dotnet run --project EprRegisterEnrolBackend --launch-profile Development
```

### SonarCloud

Example SonarCloud configuration are available in the GitHub Action workflows.

### Dependabot

We have added an example dependabot configuration file to the repository. You can enable it by renaming
the [.github/example.dependabot.yml](.github/example.dependabot.yml) to `.github/dependabot.yml`

### About the licence

The Open Government Licence (OGL) was developed by the Controller of Her Majesty's Stationery Office (HMSO) to enable
information providers in the public sector to license the use and re-use of their information under a common open
licence.

It is designed to encourage use and re-use of information freely and flexibly, with only a few conditions.
