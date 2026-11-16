// RA-706: read-only checks that an environment is ready for DomainModel:LegacyWrites off (the cut). Safe in any
// environment.
//   mongosh "<connection string>/<database>" --file cut-readiness-checks.js
//
// With LegacyWrites off the legacy accreditationApplications collection is no longer written, so everything the
// service serves must already be in the model. These checks grow with each RA-706 change. Before the cut:
//   A must be 0 (every current Accreditation application version holds createdAt: the GET list's order),
//   B must be [] (every legacy application holds createdAt, so the backfill copies a real time, not "now"),
//   C must be [] (nothing is staged).
// After the cut:
//   C must be [] (no staged copies left),
//   D must be [] (no legacy applications written after the cut, with CUT_AT set), and its total unchanged.
// See cut-readiness.md for details.

const TAG = "QA-RA706"; // the fixture script's marker; ignored here
const CUT_AT = null; // after the cut: the time LegacyWrites went off, e.g. ISODate("2027-01-04T09:00:00Z")

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

// C. Staged copies left in the accreditationApplicationsCutStaging collection.
// These should be flushed and deleted during the cut process. Expect none before and after.
print("C. staged copies left in accreditationApplicationsCutStaging (expected: [])");
printjson(
    db
        .getCollection("accreditationApplicationsCutStaging")
        .aggregate([
            {
                $match: {
                    organisationId: { $ne: TAG },
                },
            },
            { $project: { _id: 1, organisationId: 1 } },
            { $limit: 20 },
        ])
        .toArray(),
);

// D. Legacy applications written after the cut.
// Once LegacyWrites is off, the legacy collection is frozen. Any write is a defect.
const legacyTotal = db.getCollection("accreditationApplications").countDocuments({
    organisationId: { $ne: TAG },
});
print("D. legacy applications in total: " + legacyTotal + " (record it at the cut; after it, it must not change)");

if (CUT_AT === null) {
    print("D. skipped: set CUT_AT at the top of this script to the time LegacyWrites went off");
} else {
    print("D. legacy applications updated or inserted after " + CUT_AT.toISOString() + " (expected: [])");
    printjson(
        db
            .getCollection("accreditationApplications")
            .aggregate([
                {
                    $match: {
                        organisationId: { $ne: TAG },
                        $or: [
                            { updatedAt: { $gt: CUT_AT } },
                            { createdAt: { $gt: CUT_AT } },
                        ],
                    },
                },
                { $project: { _id: 1, applicationStatus: 1, updatedAt: 1, createdAt: 1 } },
                { $limit: 20 },
            ])
            .toArray(),
    );
}
