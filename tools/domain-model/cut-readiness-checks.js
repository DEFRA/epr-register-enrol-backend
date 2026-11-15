// RA-706: read-only checks that an environment is ready for DomainModel:LegacyWrites off (the cut). Safe in any
// environment.
//   mongosh "<connection string>/<database>" --file cut-readiness-checks.js
//
// With LegacyWrites off the legacy accreditationApplications collection is no longer written, so everything the
// service serves must already be in the model. These checks grow with each RA-706 change. Before the cut:
//   A must be 0 (every current Accreditation application version holds createdAt: the GET list's order),
//   B must be [] (every legacy application holds createdAt, so the backfill copies a real time, not "now").
// The read-switch checks (read-switch-readiness-checks.js) must also still pass.

const TAG = "QA-RA706"; // the fixture script's marker; ignored here

// A. Current Accreditation application versions with no createdAt. The createdAt backfill, which runs with the
// migration (DomainModel:MigrateEnabled), fills them from legacy; anything left needs a look before the cut.
print(
    "A. current Accreditation application versions without createdAt (expected: count 0 before the cut)",
);
printjson(
    db
        .getCollection("applicationVersions")
        .aggregate([
            {
                $match: {
                    isCurrent: true,
                    applicationType: "Accreditation",
                    reExOrganisationId: { $ne: TAG },
                    $or: [
                        { createdAt: { $exists: false } },
                        { createdAt: null },
                    ],
                },
            },
            {
                $group: {
                    _id: null,
                    n: { $sum: 1 },
                    sampleIds: { $push: "$entityId" },
                },
            },
            {
                $project: {
                    _id: 0,
                    n: 1,
                    sampleIds: { $slice: ["$sampleIds", 5] },
                },
            },
        ])
        .toArray(),
);

// B. Legacy applications with no createdAt. The service reads a missing createdAt as the time it reads the document,
// so the backfill would store that instead of when the application was created. Expect none.
print("B. legacy applications without createdAt (expected: [])");
printjson(
    db
        .getCollection("accreditationApplications")
        .aggregate([
            {
                $match: {
                    organisationId: { $ne: TAG },
                    $or: [
                        { createdAt: { $exists: false } },
                        { createdAt: null },
                    ],
                },
            },
            { $project: { _id: 1, applicationStatus: 1 } },
            { $limit: 20 },
        ])
        .toArray(),
);
