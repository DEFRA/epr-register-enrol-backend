# Runbook: correcting wrongly-defaulted `isNewSite` in this service (epr-2uxy)

This service holds the operator's copy of each overseas reprocessing site (ORS). Some ReEx-sourced
sites still hold `isNewSite: true` from a model default bug. This runbook covers the read-only audit
and the gated correction for **this service's database**. management-be has its own audit and
correction for the copy frozen on each work item: see its
[`docs/diagnostics/ra292-isnewsite-audit.md`](https://github.com/DEFRA/epr-register-enrol-management-be/blob/main/docs/diagnostics/ra292-isnewsite-audit.md).

## Run this service's correction first

Run this correction **before** management-be's, or alongside it, never after it.

When an operator resubmits a queried overseas-sites or broadly-equivalent-standards section,
`HttpCaseWorkingApiAdapter.BuildOverseasSitesArray` sends this service's sites, `isNewSite`
included, and management-be's `ReAccreditationResumeService` replaces `payload.overseasSites.sites[]`
with them. So while this database still holds the wrong `true`, the next resubmit puts the "New"
badge back on a work item that management-be has corrected. `OverseasSiteMerge` keeps the stored
`isNewSite` on every PATCH, so nothing here fixes the value by itself.

management-be's window only covers work items submitted before `Ra292DeployedAt`. A work item
submitted after that date, from an application this service had not yet corrected, carries the wrong
`true` too and is not covered by management-be's migration. Correcting here first, and then
resubmitting or running management-be's audit again, is what reaches those.

## What went wrong

`OverseasSiteModel.IsNewSite` had a `= true` initializer from `423d27d` (2026-07-26) until RA-292
(`26218ca`, 2026-08-10). A missing BSON element keeps the initializer, so every site stored before
then read back as `true`. `UpdateAsync` replaces the whole document, so the first save in that window
stored `true` for good.

## How a site is classified

Only applications **created before `Diagnostics:Ra292DeployedAt`** are read: an application created
after the fix was seeded with `isNewSite: false` set explicitly.

| Bucket | Rule | Action |
| --- | --- | --- |
| **ALREADY-CORRECT** | `isNewSite: true` and an `orsId` | Left alone. Operator-added sites always carry one |
| **PROVABLY-CORRUPT** | `isNewSite: true`, no `orsId`, not promoted, and **ReEx holds the site** | Corrected |
| **PROMOTED-CORRECTABLE** | the same, but promoted (`registeredNowAccredited: true`) | Corrected |
| **AMBIGUOUS-REFUSED** | `isNewSite: true`, no `orsId`, and no proof from ReEx | Refused, with the reason |

Sites with `isNewSite: false` are counted as not flagged new and left alone.

**"ReEx holds the site"** is the positive proof. For each application with an unproven site, the
service makes the call the seed made (`GetAccreditationAsync` for the application's organisation,
registration, material and previous year). The site is proven when ReEx returns an ORS with the same
`siteId` (the seed sets `siteId` from ReEx's ORS key) and the same name, either the current name or
one on the site's edit or promotion history (`previousSites`). Case is ignored, and so is whitespace
at either end.

### Why "no `orsId`" alone is not enough here

management-be calls an `orsId`-less site provably corrupt unless it carries operator-entered detail.
This service does not, because until this change `PATCH .../overseas-sites` stored any site id it had
never seen as a new site, with whatever `orsId` the client sent, possibly none. A site created that
way looks exactly like a wrongly-defaulted ReEx site. The PATCH now rejects unknown site ids with a
400 (the frontend only ever sends back the application's own sites), but sites created that way before
the change may still be stored. So an `orsId`-less site is corrected only when ReEx proves it. Writing
`false` over a genuinely new site would hide it from the regulator, which is worse than the defect.

Operator-entered detail (contact details, codes, address lines) does not block a ReEx-proven site.
`UpdateOverseasSite` (RA-470) and promotion both write that detail onto ReEx sites, and ReEx holding
the ORS under this id and name already proves the site is not new.

### Why `orsId` still separates operator-added sites

- Operator-added sites always have one: `AddOverseasSiteRequest.OrsId` was required until RA-482 and
  the server has allocated it since.
- ReEx-seeded sites got one only from RA-507 (`67a70fb`, 2026-08-27), after the window closed. Those
  were seeded with `isNewSite: false`.
- Nothing writes an `orsId` onto a site already stored. `OverseasSiteMerge` restores the stored one
  on every PATCH, and the domain model seed and sync write only the domain model.

### Refused sites

Each refused site is logged with `refusedBecause`:

- `notInReEx`: ReEx returned the registration's sites and none matched.
- `reExUnavailable (...)`: ReEx could not be read (also logged at Error). Re-run when ReEx is back.
- the operator-entered fields the site carries, for whoever adjudicates.

If AMBIGUOUS-REFUSED is larger than the two correctable buckets together, the audit logs a warning.
Check the classifier before deciding the data cannot be corrected. If the same reason recurs across
many sites, the check is probably wrong rather than the data.

To adjudicate a refused site, check it by hand against ReEx and the operator's journey. Correct only
what is proven, and record the rest on `epr-2uxy`.

## Configuration

The keys match management-be's, so both services are run the same way. Use the `Diagnostics__...`
form for environment variables.

| Key | Effect |
| --- | --- |
| `Diagnostics:Ra292IsNewSiteAudit` | `true` runs the read-only audit at startup. Absent or `false`: off |
| `Diagnostics:Ra292DeployedAt` | When RA-292 was deployed to this environment, ISO 8601 UTC. **Required** whenever the audit or correction is on. Missing or unparseable: refuses and logs at Error |
| `Diagnostics:Ra292CorrectIsNewSite` | Gate 1. `true` runs the correction. Absent or `false`: does nothing at all |
| `Diagnostics:Ra292SpotCheckConfirmedBy` | Gate 2. Name of whoever spot-checked the audit output. Missing: refuses and logs at Error. Logged with every applied correction |
| `Diagnostics:Ra292CorrectIsNewSiteApply` | Gate 3. `true` writes. Absent or `false`: dry run, which logs what it would change and writes nothing |

Gate 4 is per site: only PROVABLY-CORRUPT and PROMOTED-CORRECTABLE sites are written.

Every gate is off by default, so the code does nothing in any environment until it is turned on
deliberately (as management-be's runbook says: "Off by default — no-op in every environment until
deliberately enabled"). A value that is set but is not `true` or `false` refuses the run and logs at
Error.

Both log at `Information` (`appsettings.json` has an override for each). In Grafana, search for
`log.logger:"EprRegisterEnrolBackend.AccreditationApplication.Startup.IsNewSiteAudit"` or
`"...IsNewSiteCorrectionMigration"`.

## Running it

```bash
# 1. Count (read-only). Boot, read the log, turn it off.
Diagnostics__Ra292IsNewSiteAudit=true
Diagnostics__Ra292DeployedAt=2026-08-11T09:00:00Z   # this environment's RA-292 deploy time

# 2. Spot-check a sample of the correctable sites against ReEx and the operator database.

# 3. Dry run. Writes nothing; logs each application and site it would correct.
Diagnostics__Ra292CorrectIsNewSite=true
Diagnostics__Ra292SpotCheckConfirmedBy="your.name"
Diagnostics__Ra292DeployedAt=2026-08-11T09:00:00Z

# 4. When the dry run shows what you expect:
Diagnostics__Ra292CorrectIsNewSiteApply=true

# 5. Turn all of it off. Then run management-be's audit and correction.
```

The correction is idempotent: a corrected site reads `false` and is not flagged new on the next run.

## How the write works

- Each application is re-read inside its write, and the planned sites must still be `true`, have no
  `orsId` and have the name ReEx matched. The site list is then set with a targeted `$set` guarded on
  the `version` it read. If anything changed in between, nothing is written to that application and
  the run logs an error. Re-run the dry run to see its current state.
- The write increases `version`, so an operator's stale whole-document save cannot write the old
  `true` back.
- Nothing on the site changes except `isNewSite`. History snapshots (`overseasSites.versions`) are
  not changed: they record what was submitted, and nothing reads `isNewSite` from them.
- There is no audit entry on the application, as with the RA-691 and RA-764 corrections. The log
  line for each applied correction names who confirmed the spot check.

## The domain model (dual-write)

Each correction runs through `IDualWriteUnitOfWork`, as every operator write does.

- **`DomainModel:DualWrite` on:** the unit of work makes sure the application is migrated, and after
  the write it syncs the application again. That rebuilds the Application version's
  `applicationOverseasSites[].isNewSite` from the corrected site. The ORS entity is not rebuilt this
  way, because `Origin`, `Status` and `ProposedByApplicationId` are set from `isNewSite` when the ORS
  is created and are only carried over after that. A site with no `orsId` has an ORS of its own, and
  the wrong `true` made it an operator proposal of this application. The correction sets it, in the
  same transaction, to what the migrator makes of a ReEx site: `ReEx`, `Active`, no proposer.
  `IsNewSiteCorrectionTests.Correction_WithDualWrite_CorrectsTheApplicationVersionAndTheOwnOrs`
  covers this.
- **`DomainModel:DualWrite` off:** only the legacy document is written. The migration needs dual-write
  to be on, so it has not run, and when it does it reads the corrected document.
- **After the cut (`LegacyWrites` off):** refuses to run. The domain model does not store
  `createdAt` yet, so the window cannot be applied. Run this before the cut.

## Accepted residual

A site created through the old PATCH, with no `orsId`, would be corrected only if ReEx also holds an
ORS with the same id and name on that registration. That means a client-chosen id and name matching a
registered site, which the spot check (gate 2) is there to catch.
