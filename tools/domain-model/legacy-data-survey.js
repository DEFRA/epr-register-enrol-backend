// B1: read-only survey of accreditationApplications for the legacy shapes the domain model migration has to
// handle (docs/analysis/register-enrol-domain-model-design.md, section 7 R1, Q30b, A20, Q34).
//
// For someone with production access to run, because this team cannot examine production data. It only counts:
// no names, addresses, contact details or free text leave the database, so the output is safe to hand back.
// It never writes.
//
// Run it from the CDP Terminal (or anywhere mongosh can reach the database):
//
//   mongosh "<connection string to the epr-register-enrol-backend database>" --quiet --file legacy-data-survey.js
//
// It prints one JSON object of counts. Each count is of documents (or groups) that have the shape.
//
// The same survey runs in test environments at startup (DomainModelDiagnosticsService, behind
// DomainModel:DiagnosticsEnabled). The queries mirror
// EprRegisterEnrolBackend/DomainModel/Diagnostics/LegacyDataSurvey.cs; change both together.

const numeric = ["int", "long"];
const legacySiteNumber = { $regex: "^SN-" };

const singleCount = async (applications, pipeline) => {
    const rows = await applications
        .aggregate([...pipeline, { $count: "groups" }])
        .toArray();
    return rows.length === 0 ? 0 : rows[0].groups;
};

const groupsWithMoreThanOne = (applications, match, groupKey) =>
    singleCount(applications, [
        { $match: match },
        { $group: { _id: groupKey, n: { $sum: 1 } } },
        { $match: { n: { $gt: 1 } } },
    ]);

// Groups whose documents carry more than one distinct non-empty value of `field`.
const conflictingValues = (applications, groupBy, field) =>
    singleCount(applications, [
        {
            $match: {
                [groupBy]: { $nin: [null] },
                [field]: { $nin: [null, ""] },
            },
        },
        { $group: { _id: "$" + groupBy, values: { $addToSet: "$" + field } } },
        { $match: { $expr: { $gt: [{ $size: "$values" }, 1] } } },
    ]);

const siteShapeCounts = async (applications) => ({
    // The singular interimSite with no interimSites list: pre-RA-603 documents.
    interimSiteSingularOnly: await applications.countDocuments({
        "overseasSites.sites": {
            $elemMatch: {
                interimSite: { $type: "object" },
                $or: [
                    { interimSites: { $exists: false } },
                    { interimSites: { $size: 0 } },
                ],
            },
        },
    }),
    nullOrsIds: await applications.countDocuments({
        "overseasSites.sites": { $elemMatch: { orsId: null } },
    }),
    legacySnInterimNumbers: await applications.countDocuments({
        $or: [
            {
                "overseasSites.sites": {
                    $elemMatch: {
                        interimSites: {
                            $elemMatch: { siteNumber: legacySiteNumber },
                        },
                    },
                },
            },
            {
                "overseasSites.sites": {
                    $elemMatch: { "interimSite.siteNumber": legacySiteNumber },
                },
            },
        ],
    }),
});

// The status filters assume statuses are strings; int32Enums counts the documents they cannot see.
const referenceCounts = async (applications) => ({
    duplicateLiveApplicationGroups: await groupsWithMoreThanOne(
        applications,
        {
            applicationStatus: { $nin: ["Withdrawn", "Rejected"] },
            registrationId: { $nin: [null] },
        },
        {
            registrationId: "$registrationId",
            year: "$year",
            materialType: "$materialType",
        },
    ),
    conflictingRegistrationNumberGroups: await conflictingValues(
        applications,
        "registrationId",
        "registrationReference",
    ),
    approvedWithoutAccreditationReference: await applications.countDocuments({
        applicationStatus: "Approved",
        $or: [{ accreditationReference: null }, { accreditationReference: "" }],
    }),
    caseManagementReferencePresent: await applications.countDocuments({
        caseManagementReference: { $nin: [null, ""] },
    }),
    duplicateAccreditationReferenceGroups: await groupsWithMoreThanOne(
        applications,
        { accreditationReference: { $nin: [null, ""] } },
        { ref: "$accreditationReference" },
    ),
});

const surveyLegacyData = async (applications) => ({
    applications: await applications.countDocuments({}),
    int32Enums: await applications.countDocuments({
        $or: [
            { applicationStatus: { $type: numeric } },
            { materialType: { $type: numeric } },
            { nation: { $type: numeric } },
        ],
    }),
    ...(await siteShapeCounts(applications)),
    ...(await referenceCounts(applications)),
});

printjson(
    await surveyLegacyData(db.getCollection("accreditationApplications")),
);
