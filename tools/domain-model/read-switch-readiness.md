# Read-switch readiness (RA-705, phase 5)

Checks to run before `DomainModel:ReadFromModel` is switched on in an environment. With the flag on, the
accreditation-application routes and the service-client accreditation API serve the domain model:

- an application missing from the model answers 404 (there is no legacy fallback);
- the nation and organisation number shown come from the model's Registration and Organisation.

## Scripts

| Script | Writes? | Use |
|---|---|---|
| `read-switch-readiness-checks.js` | No | Safe in any environment. Run it before switching the flag on |
| `read-switch-readiness-test.js` | Yes: tagged fixtures, removed at the end | Dev, test or local only. Proves each check flags its case and clears once fixed |

Run either with:

```
mongosh "<connection string>/<database>" --file <script>
```

Both scripts are run in CI under Jint against Ephemeral Mongo, by `ReadSwitchReadinessScriptTests`, the same way
`legacy-data-survey.js` is. That makes them known to work, but they still need a run in a real environment.

## Acceptance criteria

| # | Criterion | Check | Expected |
|---|---|---|---|
| 1 | Every legacy accreditation application has a current Application version in the model | A | No rows |
| 2 | Every application whose legacy document holds a nation has it on the model's current Registration | B | `[]` |
| 3 | Every organisation whose legacy applications hold an organisation number has it on the model's Organisation | C | `[]` |
| 4 | Every legacy application carries a waste processing type (the model always serves one) | E | `[]` |
| 5 | Each check catches the case it exists for and clears once it is fixed | fixture test | `ALL PASS (9)` |
| 6 | With dual-write on, opening an application whose nation or number was missing removes it from B and C | B and C, before and after | It drops off |

Check D prints totals for context: legacy applications against current accreditation Application versions. Once the
migration has finished, the two should be equal.

## What only the API shows

Which source the JSON comes from can't be seen in Mongo. The automated tests cover it, for both flag states, against
a replica set:

- every route answers the same JSON with the flag off and on, over the same data;
- the golden-JSON files under `EprRegisterEnrolBackend.Test/DomainModel/ReadModel/Golden` are produced by legacy and
  by the model alike, in every status, for exporters and reprocessors.
