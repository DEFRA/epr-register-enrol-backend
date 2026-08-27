# epr-register-enrol-backend

Core delivery C# ASP.NET backend template.

- [Install MongoDB](#install-mongodb)
- [Inspect MongoDB](#inspect-mongodb)
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

| Method   | Path                                               | Description                                                                       |
| -------- | -------------------------------------------------- | --------------------------------------------------------------------------------- |
| `POST`   | `/{organisationId}/seed`                           | Create a new application (optionally pre-populated from prior year via ReEx stub) |
| `GET`    | `/{organisationId}`                                | List all applications for an organisation                                         |
| `GET`    | `/{organisationId}/{applicationId}`                | Get a single application                                                          |
| `PATCH`  | `/{organisationId}/{applicationId}/prns`           | Update the PRNs section                                                           |
| `PATCH`  | `/{organisationId}/{applicationId}/business-plan`  | Update the business plan section                                                  |
| `PATCH`  | `/{organisationId}/{applicationId}/sampling-plan`  | Update the sampling plan section                                                  |
| `POST`   | `/{organisationId}/{applicationId}/files`          | Add a file to the sampling plan                                                   |
| `DELETE` | `/{organisationId}/{applicationId}/files/{fileId}` | Remove a file from the sampling plan                                              |
| `POST`   | `/{organisationId}/{applicationId}/submit`         | Submit the application (requires all sections `Completed`)                        |
| `POST`   | `/case-management/{workItemId}/status`             | Push a Case Management service work-item status change onto `ApplicationStatus` (Case Management service caller) |

#### Seed request body

```json
{ "materialType": "Steel", "year": 2025, "siteId": "optional-site-id" }
```

Valid `materialType` values: `Steel`, `Wood`, `Aluminium`, `Fibre`, `Glass`, `Paper`, `Plastic`

#### Application lifecycle

`Saved` → `Started` (on first section edit) → `Submitted` (on submit) → `Approved` or `Rejected` (`Queried` may occur after submission)

Each section tracks its own status: `NotStarted` → `InProgress` → `Completed` (`Submitted`/`Queried` reserved for future use)

#### External adapters

`IReExApiAdapter` and `ICaseWorkingApiAdapter` are wired to stub implementations locally. They log calls but do not make real HTTP requests. Replace with live implementations when integrating with ReEx and CaseWorking services.

Swagger UI is available at `/swagger` when running locally.

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
