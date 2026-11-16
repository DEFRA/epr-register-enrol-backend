# Cut readiness (RA-706, phase 6)

Checks and steps for switching `DomainModel:LegacyWrites` off in an environment. With it off, the legacy
`accreditationApplications` collection is no longer written. Every application write changes the domain model only,
and the legacy collection is frozen as it stood at the cut.

**After the cut there is no rollback.** Writes made after the cut exist only in the model. Turning `LegacyWrites` back
on would serve and write a legacy document that has missed them. Getting back would need a model-to-legacy projection
run, and none exists. This is why the soak comes first.

## Before the cut

### Soak (runbook only)

There is no code check for the soak (decided 2026-11-15). Before the cut, confirm and record:

- reads have come from the model (`ReadFromModel` on) for the agreed soak period;
- reconciliation has stayed at zero diffs for that period;
- the owners have acknowledged that rollback is lost after the cut.

### Configuration

The host refuses to start with `LegacyWrites` off unless the following hold. This is checked in every environment,
Development included.

| Setting | Value | Why |
|---|---|---|
| `DomainModel:ReadFromModel` | on | Reads must already come from the model |
| `DomainModel:DualWrite` | on | Every write goes through the unit of work, which flushes the cut |
| `DomainModel:DualWriteSeed` | on | Seed writes the model's Organisation, Registration and sites |
| `DomainModel:ExpandEnabled` | on | The model's collections and indexes |
| `DomainModel:MigrateEnabled` | **off** | The migration reads legacy, which is frozen after the cut |
| `DomainModel:ReconcileEnabled` | **off** | Reconciliation compares legacy with the model, so it would report every write since the cut as drift |
| `FeatureFlags:InYearChangesEnabled` | off | In-year changes still read legacy |

Turn the migration and reconciliation off only after the last reconciliation report has come back at zero.

### Data checks

Run `cut-readiness-checks.js` (read-only). Before the cut:

| # | Criterion | Check | Expected |
|---|---|---|---|
| 1 | Every current Accreditation application version holds `createdAt` (the GET list's order; one without it serves its `validFrom` and logs a warning) | A | count 0 |
| 2 | Every legacy application holds `createdAt`, so the backfill copied a real time | B | `[]` |
| 3 | Nothing is staged | C | `[]` |
| 4 | The legacy total, recorded at the moment `LegacyWrites` goes off | D's total | written down |
| 5 | The read-switch checks still pass | `read-switch-readiness-checks.js` | as in `read-switch-readiness.md` |

## After the cut

Set `CUT_AT` at the top of `cut-readiness-checks.js` to the time `LegacyWrites` went off, then run it again:

| # | Criterion | Check | Expected |
|---|---|---|---|
| 6 | Legacy is frozen: no legacy application was updated or inserted after the cut | D | `[]` |
| 7 | Legacy is frozen: the legacy total is the one recorded at the cut (a delete, an upsert or the foreign-key stamp leaves no timestamp, so only the total shows it) | D's total | unchanged |
| 8 | No staged copy is left behind (each is flushed and deleted in its own transaction) | C | `[]` |
| 9 | A write through the frontend changes the model's current version, and the application's legacy document keeps its `updatedAt` | by hand, one application | as stated |

A staged copy left behind, or a legacy document written after the cut, is a defect: stop and raise it.

## Scripts

| Script | Writes? | Use |
|---|---|---|
| `cut-readiness-checks.js` | No | Safe in any environment. Before the cut, and again after it with `CUT_AT` set |
| `cut-readiness-test.js` | Yes: tagged fixtures, removed at the end | Dev, test or local only. Proves each check flags its case and clears once fixed: `ALL PASS (8)` |

Run either with:

```
mongosh "<connection string>/<database>" --file <script>
```

Both scripts run in CI under Jint against Ephemeral Mongo, by `CutReadinessScriptTests`. That makes them known to
work, but they still need a run in a real environment.

## What only the API shows

The automated tests cover what Mongo can't show, against a replica set with the flag off:

- every write route (the site, interim-site, file, number, status, query, submit, withdraw and recycling-operations
  routes) changes the model and leaves the legacy document's version and `updatedAt` as they were;
- seed writes the same model as before the cut and no legacy document;
- RA-646's fixed test accreditations seed, reseed and reset in the model.
