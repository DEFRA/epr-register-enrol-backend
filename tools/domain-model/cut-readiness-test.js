// RA-706: fixture test for the cut readiness checks. Inserts tagged documents, proves each check in
// cut-readiness-checks.js flags the case it exists for (and stops flagging it once fixed), prints PASS/FAIL, and
// removes everything it inserted. Dev, test or local only: it writes to the database.
//   mongosh "<connection string>/<database>" --file cut-readiness-test.js

const TAG = "QA-RA706";
// ObjectIds print as hex. mongosh hands back ObjectId objects; the repo's test shim (Jint) hands back {$oid}.
const hex = (id) =>
    typeof id.toHexString === "function" ? id.toHexString() : id.$oid;
const results = [];
const ONE_MINUTE_MS = 60000;
// Hex ids compare as text; an explicit compare function keeps the order independent of the engine.
const byText = (a, b) => a.localeCompare(b);

function check(name, fn) {
    try {
        results.push([fn() ? "PASS" : "FAIL", name]);
    } catch (e) {
        results.push(["FAIL", name + " (" + e.message + ")"]);
    }
}

function cleanup() {
    db.getCollection("accreditationApplications").deleteMany({
        organisationId: TAG,
    });
    db.getCollection("applicationVersions").deleteMany({
        reExOrganisationId: TAG,
    });
    db.getCollection("accreditationApplicationsCutStaging").deleteMany({
        organisationId: TAG,
    });
}

// The same filters as the read-only checks, restricted to this script's fixtures.
const missing = [{ createdAt: { $exists: false } }, { createdAt: null }];

const versionsWithoutCreatedAt = () =>
    db
        .getCollection("applicationVersions")
        .aggregate([
            {
                $match: {
                    isCurrent: true,
                    applicationType: "Accreditation",
                    reExOrganisationId: TAG,
                    $or: missing,
                },
            },
        ])
        .toArray()
        .map((d) => hex(d.entityId));

const stagedCopies = () =>
    db
        .getCollection("accreditationApplicationsCutStaging")
        .aggregate([{ $match: { organisationId: TAG } }])
        .toArray()
        .map((d) => hex(d._id));

const legacyWrittenSince = (since) =>
    db
        .getCollection("accreditationApplications")
        .aggregate([
            {
                $match: {
                    organisationId: TAG,
                    $or: [
                        { updatedAt: { $gt: since } },
                        { createdAt: { $gt: since } },
                    ],
                },
            },
        ])
        .toArray()
        .map((d) => hex(d._id))
        .sort(byText);

const legacyWithoutCreatedAt = () =>
    db
        .getCollection("accreditationApplications")
        .aggregate([{ $match: { organisationId: TAG, $or: missing } }])
        .toArray()
        .map((d) => hex(d._id));

cleanup();
const now = new Date();
const withCreatedAt = new ObjectId();
const withoutCreatedAt = new ObjectId();
const tonnageChange = new ObjectId();

db.getCollection("accreditationApplications").insertMany([
    {
        _id: withCreatedAt,
        organisationId: TAG,
        applicationStatus: "Started",
        createdAt: now,
    },
    {
        _id: withoutCreatedAt,
        organisationId: TAG,
        applicationStatus: "Started",
    },
]);
db.getCollection("applicationVersions").insertMany([
    {
        entityId: withCreatedAt,
        version: 2,
        isCurrent: true,
        reExOrganisationId: TAG,
        applicationType: "Accreditation",
        createdAt: now,
        validFrom: now,
    },
    {
        entityId: withoutCreatedAt,
        version: 1,
        isCurrent: true,
        reExOrganisationId: TAG,
        applicationType: "Accreditation",
        validFrom: now,
    },
    // Other types have no legacy document to take createdAt from; A is about Accreditation versions only.
    {
        entityId: tonnageChange,
        version: 1,
        isCurrent: true,
        reExOrganisationId: TAG,
        applicationType: "TonnageChange",
        validFrom: now,
    },
]);

// C: a copy left in the staging collection. D: a legacy document written after the cut.
const cutAt = new Date(now.getTime() - ONE_MINUTE_MS);
const stagedLeftOver = new ObjectId();
db.getCollection("accreditationApplicationsCutStaging").insertOne({
    _id: stagedLeftOver,
    organisationId: TAG,
});
db.getCollection("accreditationApplications").updateOne(
    { _id: withCreatedAt },
    { $set: { updatedAt: now } },
);
db.getCollection("accreditationApplications").updateOne(
    { _id: withoutCreatedAt },
    { $set: { updatedAt: new Date(cutAt.getTime() - ONE_MINUTE_MS) } },
);

check("C flags the staged copy left over", () => {
    const flagged = stagedCopies();
    return flagged.length === 1 && flagged[0] === hex(stagedLeftOver);
});
// An insert stamps createdAt, not updatedAt.
const insertedAfter = new ObjectId();
db.getCollection("accreditationApplications").insertOne({
    _id: insertedAfter,
    organisationId: TAG,
    applicationStatus: "Started",
    createdAt: now,
});

check(
    "D flags the legacy applications updated or inserted after the cut, and not one written before it",
    () => {
        const flagged = legacyWrittenSince(cutAt);
        const expected = [hex(withCreatedAt), hex(insertedAfter)].sort(byText);
        return (
            flagged.length === 2 &&
            flagged[0] === expected[0] &&
            flagged[1] === expected[1]
        );
    },
);

check(
    "A flags the Accreditation version without createdAt, and nothing else",
    () => {
        const flagged = versionsWithoutCreatedAt();
        return flagged.length === 1 && flagged[0] === hex(withoutCreatedAt);
    },
);
check(
    "B flags the legacy application without createdAt, and nothing else",
    () => {
        const flagged = legacyWithoutCreatedAt();
        return flagged.length === 1 && flagged[0] === hex(withoutCreatedAt);
    },
);

// The fixes: the backfill's new version holds createdAt, and the legacy document is given one.
db.getCollection("applicationVersions").updateOne(
    { entityId: withoutCreatedAt },
    { $set: { createdAt: now } },
);
db.getCollection("accreditationApplications").updateOne(
    { _id: withoutCreatedAt },
    { $set: { createdAt: now } },
);

db.getCollection("accreditationApplicationsCutStaging").deleteMany({
    _id: stagedLeftOver,
});
check("C clears once the staged copy is gone", () => stagedCopies().length === 0);

check(
    "A clears once the version holds createdAt",
    () => versionsWithoutCreatedAt().length === 0,
);
check(
    "B clears once the legacy application holds createdAt",
    () => legacyWithoutCreatedAt().length === 0,
);

cleanup();
check(
    "cleanup: no fixtures left",
    () =>
        db
            .getCollection("accreditationApplications")
            .countDocuments({ organisationId: TAG }) +
            db
                .getCollection("applicationVersions")
                .countDocuments({ reExOrganisationId: TAG }) +
            db
                .getCollection("accreditationApplicationsCutStaging")
                .countDocuments({ organisationId: TAG }) ===
        0,
);

results.forEach(([r, name]) => print(r + "  " + name));
const failed = results.filter(([r]) => r === "FAIL").length;
print(
    failed === 0
        ? "ALL PASS (" + results.length + ")"
        : failed + " FAILED of " + results.length,
);
