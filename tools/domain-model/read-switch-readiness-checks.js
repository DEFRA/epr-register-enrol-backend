// RA-705: read-only checks that an environment is ready for DomainModel:ReadFromModel. Safe in any environment.
//   mongosh "<connection string>/<database>" --file read-switch-readiness-checks.js
//
// With ReadFromModel on, the accreditation-application routes and the service-client accreditation API serve the
// domain model. An application missing from the model answers 404 (decided 2026-11-13: no legacy fallback), and the
// Nation and organisation number shown come from the model's Registration and Organisation. So before the flag goes on:
//   A must be 0 (every legacy application is in the model),
//   B and C must be 0 (the GetById backfills reached the model; this PR routes them through dual-write),
//   E must be 0 (every legacy application carries the waste processing type the model will serve).

const TAG = "QA-RA705"; // the fixture script's marker; ignored here
// ObjectIds print as hex. mongosh hands back ObjectId objects; the repo's test shim (Jint) hands back {$oid}.
const hex = (id) => (typeof id.toHexString === "function" ? id.toHexString() : id.$oid);
const legacy = db.getCollection("accreditationApplications");
const real = { organisationId: { $ne: TAG } };

function currentAccreditationVersion(localField) {
  return {
    $lookup: {
      from: "applicationVersions",
      let: { id: localField },
      pipeline: [
        {
          $match: {
            $expr: { $eq: ["$entityId", "$$id"] },
            isCurrent: true,
          },
        },
        { $project: { _id: 0, registrationId: 1 } },
      ],
      as: "model",
    },
  };
}

// A. Legacy applications with no current application version: with ReadFromModel on these answer 404.
print(
  "A. legacy applications missing from the model (expected: count 0 before ReadFromModel goes on)",
);
printjson(
  legacy
    .aggregate([
      { $match: real },
      currentAccreditationVersion("$_id"),
      { $match: { model: { $size: 0 } } },
      {
        $group: {
          _id: "$applicationStatus",
          n: { $sum: 1 },
          sampleIds: { $push: "$_id" },
        },
      },
      { $project: { n: 1, sampleIds: { $slice: ["$sampleIds", 5] } } },
    ])
    .toArray(),
);

// B. Legacy holds a nation but the model's current Registration doesn't: the response would show no nation, so the
// frontend would pick the wrong bank details. After this PR a GetById backfill (or any write) fills it.
print("B. nation in legacy but not on the model Registration (expected: [])");
printjson(
  legacy
    .aggregate([
      { $match: { ...real, nation: { $ne: null } } },
      currentAccreditationVersion("$_id"),
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
      { $project: { _id: 1, legacyNation: "$nation" } },
      { $limit: 20 },
    ])
    .toArray(),
);

// C. Legacy holds the organisation number (orgId) but the model's Organisation doesn't.
print(
  "C. organisation number in legacy but not on the model Organisation (expected: [])",
);
printjson(
  legacy
    .aggregate([
      { $match: { ...real, orgId: { $ne: null } } },
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
      {
        $group: {
          _id: "$organisationId",
          legacyOrgId: { $first: "$orgId" },
          applications: { $sum: 1 },
        },
      },
      { $limit: 20 },
    ])
    .toArray(),
);

// E. Legacy applications with no waste processing type. The model's Registration always holds one (taken from
// isExporter at migration), so with ReadFromModel on these would be served with "exporter" or "reprocessor" where the
// legacy response has none. Seed always sets it, so expect none; any listed need the value set before the flag goes on.
print("E. legacy applications with no wasteProcessingType (expected: [])");
printjson(
  legacy
    .aggregate([
      { $match: { ...real, $or: [{ wasteProcessingType: null }, { wasteProcessingType: "" }] } },
      { $project: { _id: 1, isExporter: 1, applicationStatus: 1 } },
      { $limit: 20 },
    ])
    .toArray(),
);

// D. Totals, for context.
print("D. totals");
printjson({
  legacyApplications: legacy.countDocuments(real),
  currentAccreditationApplicationVersions:
    db.getCollection("applicationVersions").countDocuments({
      isCurrent: true,
      applicationType: "Accreditation",
    }),
});
