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
| `Mongo__DatabaseUri` | No | `mongodb://127.0.0.1:27017` | MongoDB connection string (AWS IAM auth in deployed environments) |
| `Mongo__DatabaseName` | No | `epr` | MongoDB database name |
| `App__BaseUrl` | No | `http://localhost:5000` | This service's own public base URL, used for CDP callback/status URLs |
| `CdpUploader__Url` | No | `http://localhost:7337` | Base URL of the CDP Uploader service |
| `CdpUploader__SamplingPlanBucket` | No | `sampling-plans` | S3 bucket for sampling-plan uploads |
| `CdpUploader__BesEvidenceBucket` | No | `bes-evidence` | S3 bucket for BES-evidence uploads |
| `CdpUploader__GenericFilesBucket` | No | `file-uploads` | S3 bucket for other file uploads |
| `ReExApi__BaseUrl` | No | _(blank)_ | Base URL of the external ReEx accreditation/organisations API |
| `REEX_API_BASIC_AUTH_USERNAME` | **Yes** | _(blank — stub adapter used instead)_ | Basic Auth username for the ReEx API above |
| `REEX_API_BASIC_AUTH_PASSWORD` | **Yes** | _(blank)_ | Basic Auth password for the ReEx API above |
| `CaseWorking__Url` | No | `http://localhost:8085` | Base URL of `epr-register-enrol-management-be` |
| `CaseWorking__UseStub` | No | `false` (Development) | When `true`, submissions to management-be are stubbed instead of sent |
| `CASE_MANAGEMENT_API_SHARED_SECRET` | **Yes** | _(blank)_ | HMAC secret this service signs its outbound calls to management-be with — must match management-be's `AUTH_SHARED_SECRET__BACKEND` exactly |
| `CaseManagementAuth__ExpectedClientId` | No | `epr-register-enrol-management-be` | Expected `x-cdp-client-id` on inbound calls from management-be |
| `AUTH_SHARED_SECRET__MANAGEMENT_BE` | **Yes** | _(blank)_ | Verifies inbound calls from management-be — must match management-be's `OPERATOR_BACKEND_SHARED_SECRET` exactly |
| `AUTH_SHARED_SECRET__FRONTEND` | **Yes** | _(blank)_ | Verifies inbound calls from `epr-register-enrol-frontend` — must match frontend's `AUTH_SHARED_SECRET__BACKEND` exactly |

All five secret-shaped values above (`*_SHARED_SECRET*` and the two
`REEX_API_BASIC_AUTH_*` vars) are optional in Development — the affected
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

| Method   | Path                                               | Description                                                                                                      |
| -------- | -------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------- |
| `POST`   | `/{organisationId}/{registrationId}/{materialType}/seed` | Create a new application (optionally pre-populated from prior year via ReEx stub)                           |
| `GET`    | `/{organisationId}`                                | List all applications for an organisation                                                                        |
| `GET`    | `/{organisationId}/{applicationId}`                | Get a single application                                                                                         |
| `PATCH`  | `/{organisationId}/{applicationId}/prns`           | Update the PRNs section                                                                                          |
| `PATCH`  | `/{organisationId}/{applicationId}/business-plan`  | Update the business plan section                                                                                 |
| `PATCH`  | `/{organisationId}/{applicationId}/sampling-plan`  | Update the sampling plan section                                                                                 |
| `POST`   | `/{organisationId}/{applicationId}/files`          | Add a file to the sampling plan                                                                                  |
| `DELETE` | `/{organisationId}/{applicationId}/files/{fileId}` | Remove a file from the sampling plan                                                                             |
| `POST`   | `/{organisationId}/{applicationId}/submit`         | Submit the application (requires all sections `Completed`)                                                       |
| `POST`   | `/case-management/{workItemId}/status`             | Push a Case Management service work-item status change onto `ApplicationStatus` (Case Management service caller) |

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
