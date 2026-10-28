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

const surveyLegacyData = async (applications) => {
    const numeric = ["int", "long"];
    const count = (filter) => applications.countDocuments(filter);

    const groupsWithMoreThanOne = async (match, groupKey) => {
        const rows = await applications
            .aggregate([
                { $match: match },
                { $group: { _id: groupKey, n: { $sum: 1 } } },
                { $match: { n: { $gt: 1 } } },
                { $count: "groups" },
            ])
            .toArray();
        return rows.length === 0 ? 0 : rows[0].groups;
    };

    // Groups whose documents carry more than one distinct non-empty value of `field`.
    const conflictingValues = async (groupBy, field) => {
        const rows = await applications
            .aggregate([
                {
                    $match: {
                        [groupBy]: { $nin: [null] },
                        [field]: { $nin: [null, ""] },
                    },
                },
                {
                    $group: {
                        _id: "$" + groupBy,
                        values: { $addToSet: "$" + field },
                    },
                },
                { $match: { $expr: { $gt: [{ $size: "$values" }, 1] } } },
                { $count: "groups" },
            ])
            .toArray();
        return rows.length === 0 ? 0 : rows[0].groups;
    };

    return {
        applications: await count({}),
        int32Enums: await count({
            $or: [
                { applicationStatus: { $type: numeric } },
                { materialType: { $type: numeric } },
                { nation: { $type: numeric } },
            ],
        }),
        // The singular interimSite with no interimSites list: pre-RA-603 documents.
        interimSiteSingularOnly: await count({
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
        nullOrsIds: await count({
            "overseasSites.sites": { $elemMatch: { orsId: null } },
        }),
        legacySnInterimNumbers: await count({
            $or: [
                {
                    "overseasSites.sites": {
                        $elemMatch: {
                            interimSites: {
                                $elemMatch: { siteNumber: { $regex: "^SN-" } },
                            },
                        },
                    },
                },
                {
                    "overseasSites.sites": {
                        $elemMatch: {
                            "interimSite.siteNumber": { $regex: "^SN-" },
                        },
                    },
                },
            ],
        }),
        // The status filters assume statuses are strings; int32Enums counts the documents they cannot see.
        duplicateLiveApplicationGroups: await groupsWithMoreThanOne(
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
            "registrationId",
            "registrationReference",
        ),
        approvedWithoutAccreditationReference: await count({
            applicationStatus: "Approved",
            $or: [
                { accreditationReference: null },
                { accreditationReference: "" },
            ],
        }),
        caseManagementReferencePresent: await count({
            caseManagementReference: { $nin: [null, ""] },
        }),
        duplicateAccreditationReferenceGroups: await groupsWithMoreThanOne(
            { accreditationReference: { $nin: [null, ""] } },
            { ref: "$accreditationReference" },
        ),
    };
};

surveyLegacyData(db.getCollection("accreditationApplications")).then((report) =>
    printjson(report),
);
