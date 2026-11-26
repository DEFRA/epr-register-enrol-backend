# Changing the Accreditation API safely

For developers changing anything a client of the Accreditation API (`/v1`) can see. Other services, first
ReEx, depend on it and release on their own schedule, so a change that passes our tests can still break them.

Items marked **(proposed)** are not yet agreed team rules. Everything else describes the repository as it is.
The consumer-facing promise is the [versioning and deprecation policy](reex-api-onboarding.md#versioning-and-deprecation)
in the onboarding guide. If this page and that policy disagree, the policy wins: fix this page.

The Admin API (`/admin`, [admin-api.yaml](openapi/admin-api.yaml)) has its own spec and consumer. This page does
not cover it.

## The contract

The contract is everything a client can rely on, not only the JSON fields:

- routes, query parameters, paging defaults and limits, and the order of results
- response fields: names, types, formats, whether each is always sent, and what it means
- enum values
- which records are served. Nothing before 2027 is served. The accreditation routes serve only granted
  accreditations; getting a registration serves every application for it, whatever its status
- status codes and problem `code` values
- authentication and request signing, rate limits and query time limits

It is written down in:

| Document | What it is for |
| --- | --- |
| [OpenAPI spec](openapi/registration-accreditation-api.v2.yaml) | the shapes and descriptions |
| [Onboarding guide](reex-api-onboarding.md) | what consumers read: policy, examples, errors, test data, "What's new" |
| [Mapping document](openapi/registration-accreditation-api.v2.mapping.md) | where each field comes from, and the rules behind it |

The "v2" in the file names is the second draft of the spec. The API's paths are `/v1`.

The Swagger page at `/swagger` is generated from the code and is **not** the contract. The `/v1` handlers don't
declare their response types, so don't use it to check a response shape.

## Breaking and safe changes

### The status of `/v1` today

The API is **provisional**: the spec is a draft (`0.2.0-draft`). Until the onboarding guide says the shape is
settled, `/v1` may take breaking changes, **but consumers must be told before the change is released**, through
"What's new" and the Slack channel named in the guide. Provisional does not mean unannounced.

Once the shape is settled, breaking changes are not made in `/v1`.

### Safe under the published policy

| Change | Check before you merge |
| --- | --- |
| New response field | Clients are told to ignore fields they don't know. Still announce it: a strict client may reject it. |
| New optional query parameter | Leaving it out must give the same result as before. |
| New route | It needs the same authorisation and filters: add it to the route group in `AccreditationApiEndpoints`. |
| New value of `status` or `tonnageBand` | See [Enum values](#enum-values). |
| Fixing a response that didn't match the guide | A client may rely on the wrong behaviour. **(proposed)** Announce it too. |

### Breaking under the published policy

- Removing or renaming a field.
- Changing a field's type or meaning.
- Removing a route.
- Rejecting input that was accepted before. This includes a new required parameter and tighter validation.

### Not covered by the policy (proposed: treat as breaking)

The policy doesn't mention these. Until the team decides, agree them with consumers before merging.

- Sometimes omitting a field that was always sent, or sending `null` where there was always a value.
- A new value of `material`, `glassRecyclingProcess`, `wasteProcessingType` or `applicationStatus`.
- Changing or removing a problem `code`, or the status code for a case. A new `code` for a new case needs
  at least an announcement.
- Changing the default or maximum `limit`, or the order of results.
- Serving fewer records, or different ones.
- Changing signing or authentication, lowering the rate limit or shortening the query time limit.
- Renaming an `operationId` or a schema in the spec, which can break generated clients.

A new field that holds **personal or commercially sensitive data** can be compatible and still be wrong to add.
Search results carry no signatories, and the business plan is not served at all. Getting a registration shows
applications that haven't been granted, so it deliberately returns little: don't add to it without agreement.
Raise it in review.

### Enum values

The policy allows new `status` and `tonnageBand` values, and tells clients to handle values they don't know.
But the spec lists them as closed `enum`s, so a client generated from it may reject a new one. Announce a new
value before it can be returned.

When you add one, update:

- the `enum` in the spec, and the onboarding guide
- the `status` search filter list (`SearchStatuses` in `AccreditationApiEndpoints`), for a status
- the mapper: it turns tonnage bands and glass processes into strings with a `switch`, and an unmapped value is
  left out of the response without an error
- the allowed values in the end-to-end contract tests

**Changing a C# enum can change the API:**

- `material` is the member name of `MaterialType`.
- `applicationStatus`, on a registration, is the member name of `ApplicationStatus`, which the whole application
  journey uses. Renaming or adding a member for the journey changes what this API returns.
- In histories built from the domain model, `status` is the member name of the status enum in lower case.

## Versioning

- The version is in the path. All routes start `/v1`, written out in `AccreditationApiEndpoints`. There is one
  version, and no version header or versioning library.
- The spec's `info.version` (`0.2.0-draft`) describes the document. It is not the path version, and we have no
  rule for when to change it.
- Once the shape is settled, a breaking change goes into a new version served **alongside** the old one. We don't
  switch a version off without telling consumers first, and the notice period is agreed with each one.

We have not built a second version yet. **(proposed)** When we first need one:

1. Agree the need, and the notice period for `/v1`, with consumers.
2. Add `/v2` routes with their own response types. Don't change the `/v1` types to suit `/v2`.
3. Give `/v2` its own spec file and tests, and keep the `/v1` tests running unchanged.
4. Remove `/v1` only after every consumer has moved and the notice period has ended.

## Keeping the spec and the code in step

**This is manual.** The code doesn't generate the spec, the spec doesn't generate the code, and no backend test
reads the YAML. The one test that reads a contract document, `OnboardingGuideExampleTests`, checks only the
worked signing example in the onboarding guide. A pull request can pass every check with a wrong spec.

So change the code and the documents **in the same pull request**:

| Update | When |
| --- | --- |
| `AccreditationApiDtos.cs`, `AccreditationApiMapper.cs`, `AccreditationApiEndpoints.cs` | the behaviour changes |
| `GrantedAccreditations.cs`, `RegistrationApplications.cs` | which records are served, or where they are read from, changes |
| OpenAPI spec, including `required` and descriptions | any visible change |
| Mapping document | a field's source or rule changes |
| Onboarding guide: field reference, JSON examples, errors, test data | anything it describes changes |
| Onboarding guide: **What's new** | any change a consumer can notice. Say what they must do, and mark **Breaking for clients that…** |
| Tests: backend, end-to-end contract tests, `consumer-tests/` | see below |

Points that are easy to miss:

- **Required and optional.** A field the spec marks `required` is always sent. An optional field we don't hold is
  left out (`JsonIgnore(Condition = WhenWritingNull)`), not sent as `null`. Some fields are always present and
  can be `null`, such as `effectiveToDate`. Keep the DTO and the spec the same.
- **Search items** share the summary type with the single accreditation. A change to it appears in both.
- **Two data sources.** `DomainModel:ReadFromModel` decides whether content comes from the legacy document or
  the domain model. A change must give the right response with the flag off and on.
- **Derived fields.** `latestStatus` and `statusTimeline` are both worked out from `statusHistory`
  (`StatusHistoryBuilder`, `StatusTimelineBuilder`) and must agree. They share one ordering rule,
  `InPrecedenceOrder`: change it there. A new record with a `statusHistory` needs both.
- **Code shared outside `/v1`.** Case management's accreditation summary uses the mapper's site order
  (`OrderedAccreditedSites`), exporter rule (`IsExporter`) and blank-value rule (`Blank`). Changing them changes
  that endpoint too.
- **Global JSON settings** in `Program.cs` (camelCase, enums as strings) affect response shapes.

## How tests protect consumers

There are three sets. Only the first can stop a merge.

| Tests | What they check | When they run |
| --- | --- | --- |
| Backend tests, `EprRegisterEnrolBackend.Test/AccreditationApi/` | behaviour, authentication, errors, parts of the response shape, both data sources | every pull request. Required to merge |
| End-to-end contract tests, `test/api/` in `epr-register-enrol-fe-tests` | response shapes, allowed enum values, removed fields, status history rules, errors, written by hand from the spec | against the `test` environment, in the full test suite run there or by hand. Skipped without the API client secret. **Not in backend pull request checks** |
| [`consumer-tests/`](../consumer-tests/README.md) | signing, basic calls to the accreditation routes and that each `statusTimeline` agrees with its history, from outside the service, with real credentials | **only by hand** |

What this means:

- These are tests **we** write about what consumers rely on. They are not consumer-driven contract tests: nothing
  here runs ReEx's expectations, so passing doesn't prove ReEx still works.
- The end-to-end contract tests find a problem **after** it is merged and deployed to `test`.
- The end-to-end jobs on a backend pull request run the browser journeys, not `test/api/`.
- The backend tests don't pin every field. One test lists the exact fields of a search item
  (`Search_ReturnsEveryFieldItReturnedBeforeAlongsideTheSites`). Elsewhere, a test notices a lost field only if
  it reads it.

### What to add with a change

- A backend test through the real endpoint, asserting the JSON, not only a mapper test. Assert field names as
  strings, so a renamed C# property fails the test.
- For a new or changed field, cover when it is present, left out and `null`, with both data sources.
- If you remove a field, assert that it is gone.
- Update `test/api/` in `epr-register-enrol-fe-tests`. It rejects an enum value it doesn't list, so a new value
  fails there until it is added. Give that pull request the same branch name as yours.
- Update `consumer-tests/` if the change touches what they call.

**A failing contract test is a question, not an obstacle.** Before you change what it expects, work out whether a
consumer expects the same thing.

### Running them

```bash
# Backend, from this repository
dotnet test

# End-to-end contract tests, from epr-register-enrol-fe-tests: seeds an organisation on test.
# Needs secrets, including ACCREDITATION_API_CLIENT_SECRET: see bin/integration-with-api.sh there.
npm run test:integration:api

# Consumer tests, against a deployed environment
cd consumer-tests
BASE_URL=https://... CLIENT_ID=... CLIENT_SECRET=... \
TEST_ORGANISATION_NUMBER=... TEST_REGISTRATION_NUMBER=... TEST_YEAR=2027 \
npm test
```

## Review steps for a contract change

### What GitHub enforces on `main`

- One approving review.
- The **Run Pull Request Checks** job passes: backend tests, with minimum line and branch coverage.
- The branch is up to date with `main`.

The end-to-end jobs also run, but are not required. No code owner review is required, and nothing detects a
contract change. The steps below depend on the author and the reviewer.

### Before writing code (proposed)

1. Classify the change with the tables above. If you're unsure, treat it as breaking.
2. For a breaking change, or one the policy doesn't cover, agree it with consumers first and record what was
   agreed on the story.

### Author (proposed)

Copy this into the pull request description:

```markdown
## API contract change
- [ ] Classification: safe / breaking / not covered by the policy
- [ ] Consumers told or agreement recorded (link), or not needed because:
- [ ] OpenAPI spec updated, including `required`
- [ ] Mapping document updated, or not affected
- [ ] Onboarding guide updated, including "What's new"
- [ ] Backend endpoint tests, with `DomainModel:ReadFromModel` off and on
- [ ] End-to-end contract tests and consumer tests updated, or not affected
- [ ] No new personal or commercially sensitive data, or it has been agreed
```

### Reviewer (proposed)

1. Read the response types, mapper and endpoint changes **against the spec**, field by field. No tool does this.
2. Check the classification. Look for hidden changes: enum renames, global JSON settings, which records are
   served, order, validation and errors.
3. Check that no existing assertion was weakened or removed without a reason.
4. Check that "What's new" makes sense to someone who can't see our code.
5. For a breaking change, get a second approval from someone who deals with the consumer.

### Release (proposed)

1. Tell consumers in Slack **before** a change they could notice is released.
2. Merge the matching end-to-end test change so the `test` run matches the deployed API.
3. After deployment to `test`, check that the contract tests pass, and run the consumer tests, before the change
   goes further.

## Known gaps

As of 26 November 2026. Remove each one when it is fixed.

- Nothing automatically compares the spec with the responses.
- The end-to-end contract tests and consumer tests don't run on backend pull requests.
- Neither the end-to-end contract tests nor the consumer tests call the route that gets a registration. Only the
  backend tests cover it.
- The end-to-end contract test `answers 404 for the registration endpoint` predates that route. It expects `404`,
  so it fails when the registration it asks for has an application from 2027 on.
- The spec's introduction still says no authentication scheme is defined. The API uses HMAC request signing, as
  the onboarding guide describes.
