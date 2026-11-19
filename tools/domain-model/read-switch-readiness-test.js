// RA-705: fixture test for the read-switch readiness checks. Inserts tagged documents, proves each check in
// read-switch-readiness-checks.js flags the case it exists for (and stops flagging it once fixed), prints
// PASS/FAIL, and removes everything it inserted. Dev, test or local only: it writes to the database.
//   mongosh "<connection string>/<database>" --file read-switch-readiness-test.js

const TAG = "QA-RA705";
// ObjectIds print as hex. mongosh hands back ObjectId objects; the repo's test shim (Jint) hands back {$oid}.
const hex = (id) => (typeof id.toHexString === "function" ? id.toHexString() : id.$oid);
const results = [];

function check(name, fn) {
  try {
    results.push([fn() ? "PASS" : "FAIL", name]);
  } catch (e) {
    results.push(["FAIL", name + " (" + e.message + ")"]);
  }
}

function cleanup() {
  db.getCollection("accreditationApplications").deleteMany({ organisationId: TAG });
  db.getCollection("applicationVersions").deleteMany({ reExOrganisationId: TAG });
  db.getCollection("registrationVersions").deleteMany({ reExRegistrationId: TAG });
  db.getCollection("organisations").deleteMany({ reExOrganisationId: TAG });
}

// The same pipelines as the read-only checks, restricted to this script's fixtures.
function currentAccreditationVersion() {
  return {
    $lookup: {
      from: "applicationVersions",
      let: { id: "$_id" },
      pipeline: [
        { $match: { $expr: { $eq: ["$entityId", "$$id"] }, isCurrent: true } },
        { $project: { _id: 0, registrationId: 1 } },
      ],
      as: "model",
    },
  };
}

const missingFromModel = () =>
  db.getCollection("accreditationApplications")
    .aggregate([
      { $match: { organisationId: TAG } },
      currentAccreditationVersion(),
      { $match: { model: { $size: 0 } } },
    ])
    .toArray()
    .map((d) => hex(d._id));

const nationNotOnRegistration = () =>
  db.getCollection("accreditationApplications")
    .aggregate([
      { $match: { organisationId: TAG, nation: { $ne: null } } },
      currentAccreditationVersion(),
      { $unwind: "$model" },
      {
        $lookup: {
          from: "registrationVersions",
          let: { rid: "$model.registrationId" },
          pipeline: [
            {
              $match: {
                $expr: { $eq: ["$entityId", "$$rid"] },
                isCurrent: true,
              },
            },
            { $project: { _id: 0, nation: 1 } },
          ],
          as: "registration",
        },
      },
      { $unwind: "$registration" },
      { $match: { "registration.nation": null } },
    ])
    .toArray()
    .map((d) => hex(d._id));

const wasteProcessingTypeMissing = () =>
  db
    .getCollection("accreditationApplications")
    .aggregate([
      {
        $match: {
          organisationId: TAG,
          $or: [{ wasteProcessingType: null }, { wasteProcessingType: "" }],
        },
      },
    ])
    .toArray().length;

const orgNumberNotOnOrganisation = () =>
  db.getCollection("accreditationApplications")
    .aggregate([
      { $match: { organisationId: TAG, orgId: { $ne: null } } },
      {
        $lookup: {
          from: "organisations",
          localField: "organisationId",
          foreignField: "reExOrganisationId",
          as: "organisation",
        },
      },
      { $unwind: "$organisation" },
      { $match: { "organisation.organisationNumber": null } },
    ])
    .toArray().length;

cleanup(); // tidy up any earlier aborted run

const migratedId = new ObjectId();
const legacyOnlyId = new ObjectId();
const registrationId = new ObjectId();
const now = new Date();

db.getCollection("accreditationApplications").insertMany([
  {
    _id: migratedId,
    organisationId: TAG,
    year: 2027,
    orgId: 500705,
    nation: "Wales",
    applicationStatus: "Started",
  },
  {
    _id: legacyOnlyId,
    organisationId: TAG,
    year: 2027,
    applicationStatus: "Started",
  },
]);
db.getCollection("applicationVersions").insertOne({
  entityId: migratedId,
  version: 1,
  isCurrent: true,
  reExOrganisationId: TAG,
  registrationId: registrationId,
  applicationType: "Accreditation",
  validFrom: now,
});
db.getCollection("registrationVersions").insertOne({
  entityId: registrationId,
  version: 1,
  isCurrent: true,
  reExRegistrationId: TAG,
  nation: null,
  validFrom: now,
});
db.getCollection("organisations").insertOne({
  reExOrganisationId: TAG,
  organisationNumber: null,
});

check("A flags the legacy-only application, not the migrated one", () => {
  const ids = missingFromModel();
  return ids.length === 1 && ids[0] === hex(legacyOnlyId);
});
check(
  "B flags a nation held in legacy but not on the model Registration",
  () => {
    const ids = nationNotOnRegistration();
    return ids.length === 1 && ids[0] === hex(migratedId);
  },
);
check(
  "C flags an organisation number held in legacy but not on the model Organisation",
  () => orgNumberNotOnOrganisation() === 1,
);
check(
  "E flags every legacy application with no waste processing type",
  () => wasteProcessingTypeMissing() === 2,
);

// Fix each case the way the application does, and the checks clear.
db.getCollection("applicationVersions").insertOne({
  entityId: legacyOnlyId,
  version: 1,
  isCurrent: true,
  reExOrganisationId: TAG,
  applicationType: "Accreditation",
  validFrom: now,
});
db.getCollection("registrationVersions").updateOne(
  { entityId: registrationId },
  { $set: { nation: "Wales" } },
);
db.getCollection("organisations").updateOne(
  { reExOrganisationId: TAG },
  { $set: { organisationNumber: 500705 } },
);
db.getCollection("accreditationApplications").updateOne(
  { _id: migratedId },
  { $set: { wasteProcessingType: "exporter" } },
);
db.getCollection("accreditationApplications").updateOne(
  { _id: legacyOnlyId },
  { $set: { wasteProcessingType: "reprocessor" } },
);

check(
  "A clears once the application is in the model",
  () => missingFromModel().length === 0,
);
check(
  "B clears once the Registration holds the nation",
  () => nationNotOnRegistration().length === 0,
);
check(
  "C clears once the Organisation holds the number",
  () => orgNumberNotOnOrganisation() === 0,
);
check(
  "E clears once every application carries its waste processing type",
  () => wasteProcessingTypeMissing() === 0,
);

cleanup();
check(
  "cleanup: no fixtures left",
  () =>
    db.getCollection("accreditationApplications").countDocuments({ organisationId: TAG }) +
      db.getCollection("applicationVersions").countDocuments({ reExOrganisationId: TAG }) +
      db.getCollection("registrationVersions").countDocuments({ reExRegistrationId: TAG }) +
      db.getCollection("organisations").countDocuments({ reExOrganisationId: TAG }) ===
    0,
);

results.forEach(([r, name]) => print(r + "  " + name));
const failed = results.filter(([r]) => r === "FAIL").length;
print(
  failed === 0
    ? "ALL PASS (" + results.length + ")"
    : failed + " FAILED of " + results.length,
);
