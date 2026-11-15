// RA-706: fixture test for the cut readiness checks. Inserts tagged documents, proves each check in
// cut-readiness-checks.js flags the case it exists for (and stops flagging it once fixed), prints PASS/FAIL, and
// removes everything it inserted. Dev, test or local only: it writes to the database.
//   mongosh "<connection string>/<database>" --file cut-readiness-test.js

const TAG = "QA-RA706";
// ObjectIds print as hex. mongosh hands back ObjectId objects; the repo's test shim (Jint) hands back {$oid}.
const hex = (id) =>
    typeof id.toHexString === "function" ? id.toHexString() : id.$oid;
const results = [];

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
                .countDocuments({ reExOrganisationId: TAG }) ===
        0,
);

results.forEach(([r, name]) => print(r + "  " + name));
const failed = results.filter(([r]) => r === "FAIL").length;
print(
    failed === 0
        ? "ALL PASS (" + results.length + ")"
        : failed + " FAILED of " + results.length,
);
