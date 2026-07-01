# ReEx `orgId` deserialization failure (CDP test environment)

## Symptom

`ReExClient` logs `Failed to deserialize ReEx API response (status 200)` in the `test`
environment, for calls made during `POST /api/v1/accreditation-applications/{organisationId}/{registrationId}/{materialType}/seed`.

```
System.Text.Json.JsonException: The JSON value could not be converted to System.String.
Path: $.orgId | LineNumber: 0 | BytePositionInLine: 65.
 ---> System.InvalidOperationException: Cannot get the value of a token type 'Number' as a string.
   ...
   at EprRegisterEnrolBackend.ReEx.ReExClient.MapResponseAsync[T](...) in ReExClient.cs:line 183
```

## Root cause

[`OrganisationDto.OrgId`](../EprRegisterEnrolBackend/ReEx/Dtos/OrganisationDto.cs#L10) is typed
as `string?`, but the real ReEx API returns `orgId` as a JSON **number**. STJ's default
`ObjectDefaultConverter` does not coerce number tokens into strings, so deserialization throws
a `JsonException`. `ReExClient.MapResponseAsync`
([ReExClient.cs:191-198](../EprRegisterEnrolBackend/ReEx/ReExClient.cs#L191-L198)) catches this
and returns a `DeserializationError` result, which propagates through
`HttpReExApiAdapter.GetAccreditationAsync`
([HttpReExApiAdapter.cs:46-60](../EprRegisterEnrolBackend/AccreditationApplication/Adapters/HttpReExApiAdapter.cs#L46-L60))
as a 502/503 from the `seed` endpoint.

This was a wrong assumption baked into the original RA-198 implementation, not an upstream
regression:

- Our own domain model (`OrganisationModel.OrgId`,
  [OrganisationModel.cs:14](../EprRegisterEnrolBackend/Organisation/Models/OrganisationModel.cs#L14))
  is `int`.
- The Mongo schema that mirrors ReEx's own data
  (`EprRegisterEnrolBackend/Utils/Mongo/Scripts/create-collection.js:28-31`) declares
  `orgId: { bsonType: "int" }`.
- `StubReExApiAdapter.cs:42` (`int.TryParse(organisationId, ...)`) already treats org IDs as
  numeric.
- The two `ReExClientTests.cs` fixtures (lines 31, 185) use `"orgId": "1234"` (quoted) —
  the same wrong assumption, never caught because nothing exercised a real numeric payload.

`OrgId` is currently **unused downstream** — `HttpReExApiAdapter` never reads `org.OrgId` — so
the blast radius of the fix is contained to the DTO and its tests.

## Fix

1. **`EprRegisterEnrolBackend/ReEx/Dtos/OrganisationDto.cs:10`** — change
   `public string? OrgId { get; init; }` to `public int? OrgId { get; init; }`, matching the
   confirmed `bsonType: "int"` contract and our own `OrganisationModel.OrgId`.

2. **`EprRegisterEnrolBackend.Test/ReEx/ReExClientTests.cs:31, 185`** — update both fixtures
   from `"orgId": "1234"` to `"orgId": 1234` (unquoted).

3. **Add a regression test** in `ReExClientTests.cs` asserting `GetOrganisationsAsync`
   succeeds against a payload with a numeric `orgId` and correctly populates `Value.OrgId` —
   this is the exact case that broke in CDP test.

## Verification

- `dotnet test` — confirm the two updated fixtures and the new regression test pass.
- Replay the failing payload locally: point `ReExApi:BaseUrl` at a local stub returning the
  exact CDP test-env body (numeric `orgId`), hit
  `POST /api/v1/accreditation-applications/{orgId}/{regId}/Aluminium/seed`, confirm `201`
  instead of `502`/`503`.
- After deploying to `test`, re-trigger the same seed call that produced the original error
  (org `6a2fcd74e16883c137d01188`) and confirm no `Failed to deserialize ReEx API response`
  error appears in CDP logs for `ReExClient`.
