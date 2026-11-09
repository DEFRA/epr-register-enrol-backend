# Analysis & plan: split the register/enrol aggregate into a proper domain model

## Role and outcome
Act as a senior domain modeller and backend architect. The job is **analysis and planning only**. Do not write production code or change any branch. The result is one design document plus a set of beads issues that the team can implement from.

## Context
- **Repo in scope:** `epr-register-enrol-backend` (.NET, **MongoDB**). This service owns the entities below. Today they are seeded from the ReEx API (`epr-backend`, read-only for reference). In future they will also be created and changed here, through re-registrations, new registrations, in-year changes and updates.
- **Current shape:** one Mongo document covers Organisation, Registration, Accreditation, ORSs and Interim Sites. It serves as the seeded data, the application, the case being processed and the accreditation. It was built quickly to meet a deadline.
- **What exposed the gaps:** the YAML and data mappings in PR https://github.com/DEFRA/epr-register-enrol-backend/pull/210. That PR defines a **new contract for new use cases**. It does not replace the existing API.

## Target lifecycle
seed → **Application** (for an accreditation for one material and one year) → enriched by the frontend → submitted to case management → processing, queries and responses (repeats) → **approved** or **rejected**
- On reject: mark the Application rejected.
- On approve: mark the Application approved **and create an Accreditation**. An Accreditation exists only after an approval.

## Fixed modelling decisions (do not reopen)
1. Organisation becomes its own entity.
2. Registration links to Organisation.
3. Accreditation links to Registration, and reaches Organisation **only through Registration**. One reason: an accreditation can cover part of a registered business.
4. An Accreditation links to the ORSs and Interim Sites from its Application. Those sites are a subset of the ORSs on the Registration.
5. **Suspension:** assume an Accreditation as a whole, and an individual ORS within it, can be suspended and unsuspended. This is pending confirmation from product, so record it as an assumption. Also list whether Interim Sites and Registrations can be suspended as open questions.
6. **History is system-time (audit) only.** The time a change is recorded is treated as its effective time. Use SQL Server system-versioned temporal tables as the **model for the behaviour**: each record has a current version, closed earlier versions with `ValidFrom`/`ValidTo`, and "as of a date" queries. **Implement this in MongoDB.** Do not propose moving to a relational database. Apply it to Organisation, Registration, Accreditation, ORS and Interim Site.

## Hard constraints
- The existing endpoints and use cases must keep working throughout.
- **No downtime and no data loss.** Data exists in deployed environments and must be migrated in place.
- Out of scope for design work: management-be, management-fe and frontend. Still **list** every consumer-facing effect you find.

## What to do
1. **Current state:** read the current document model, the seeding path and the existing endpoints. Then read PR #210's YAML and mappings. Cite file paths.
2. **Target domain model:** define each entity's identity and key fields, its relationships, its lifecycle or state machine (Application statuses, Accreditation active/suspended, ORS active/suspended) and who owns it. Include a Mermaid ER diagram and a state diagram.
3. **History design in Mongo:** compare at least two options, for example a current document plus a history collection, or versioned documents with `validFrom`/`validTo`. Cover how atomic each write is (transactions), how to run "as of a date" and "all versions" queries, the indexes needed, and how suspend and unsuspend appear in history. Recommend one option.
4. **Gap analysis:** map every field of today's document, and every element of PR #210, to the target model. Flag mismatches, fields with no clear home, and data that will need enriching later.
5. **Migration plan:** use phases with no downtime, for example expand → dual-write or backfill → switch reads → contract. Show how existing endpoints keep working at each step (facade or read-model). State how each step is checked and rolled back, and how an Application that is mid-process when the migration runs is handled.
6. **Beads breakdown:** create one beads (`bd`) issue per independently shippable step, with its dependencies and a priority from 0 to 4. Show the proposed issues **before** creating them.

## Output
A single markdown design document with these sections: Summary · Current state · Target model (with diagrams) · History design and recommendation · Gap analysis (table) · Migration plan · Risks · **Assumptions** · **Open questions for product** · Proposed beads issues.
Ask where to save the document before writing it. Stop after step 6 and wait for review.

## Done when
Every field in the current document and in PR #210 has a destination or an explicit "drop" decision. Every migration phase keeps the existing endpoints working. Every assumption is listed and none is buried in the text.
