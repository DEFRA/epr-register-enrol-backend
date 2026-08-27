import { MongoClient } from "mongodb";

export async function createAccreditationApplicationsCollection(
    uri,
    dbName,
    collectionName,
) {
    const client = new MongoClient(uri);

    try {
        await client.connect();
        const db = client.db(dbName);

        try {
            await db.collection(collectionName).drop();
            console.log("Dropped existing collection");
        } catch (_error) {
            console.log("Collection does not exist, creating new");
        }

        await db.createCollection(collectionName, {
            validator: {
                $jsonSchema: {
                    bsonType: "object",
                    required: [
                        "_id",
                        "organisationId",
                        "year",
                        "materialType",
                        "applicationStatus",
                    ],
                    properties: {
                        organisationId: {
                            bsonType: "string",
                            description: "Organisation identifier",
                        },
                        year: {
                            bsonType: "int",
                            minimum: 2024,
                            description: "Application year",
                        },
                        siteId: {
                            bsonType: ["string", "null"],
                        },
                        organisationName: {
                            bsonType: ["string", "null"],
                            description:
                                "Denormalised organisation display name",
                        },
                        siteAddress: {
                            bsonType: ["string", "null"],
                            description:
                                "Denormalised site address string for display",
                        },
                        materialType: {
                            bsonType: "string",
                            enum: [
                                "Steel",
                                "Wood",
                                "Aluminium",
                                "Fibre",
                                "Glass",
                                "Paper",
                                "Plastic",
                            ],
                        },
                        applicationStatus: {
                            bsonType: "string",
                            enum: [
                                "Saved",
                                "Started",
                                "Submitted",
                                "Queried",
                                "Approved",
                                "Rejected",
                            ],
                        },
                        sourceReExAccreditationId: {
                            bsonType: ["string", "null"],
                        },
                        sourceYear: {
                            bsonType: ["int", "null"],
                        },
                        applicationReference: {
                            bsonType: ["string", "null"],
                        },
                        submittedBy: {
                            bsonType: ["object", "null"],
                            properties: {
                                fullName: { bsonType: "string" },
                                jobTitle: { bsonType: "string" },
                                email: { bsonType: "string" },
                            },
                        },
                        dateSent: {
                            bsonType: ["date", "null"],
                        },
                        dateLastEdited: {
                            bsonType: "date",
                        },
                        createdAt: {
                            bsonType: "date",
                        },
                        updatedAt: {
                            bsonType: "date",
                        },
                        prns: {
                            bsonType: "object",
                            properties: {
                                plannedTonnageBand: {
                                    bsonType: ["string", "null"],
                                    enum: [
                                        "UpTo500",
                                        "UpTo5000",
                                        "UpTo10000",
                                        "Over10000",
                                        null,
                                    ],
                                },
                                authorisers: {
                                    bsonType: "array",
                                    items: {
                                        bsonType: "object",
                                        required: ["fullName", "email"],
                                        properties: {
                                            fullName: {
                                                bsonType: "string",
                                                minLength: 1,
                                            },
                                            email: {
                                                bsonType: "string",
                                                minLength: 1,
                                            },
                                        },
                                    },
                                },
                                sectionStatus: {
                                    bsonType: "string",
                                    enum: [
                                        "NotStarted",
                                        "InProgress",
                                        "Completed",
                                        "Submitted",
                                        "Queried",
                                    ],
                                },
                            },
                        },
                        businessPlan: {
                            bsonType: "object",
                            properties: {
                                newInfrastructurePercent: {
                                    bsonType: ["int", "null"],
                                },
                                priceSupportPercent: {
                                    bsonType: ["int", "null"],
                                },
                                businessCollectionsPercent: {
                                    bsonType: ["int", "null"],
                                },
                                communicationsPercent: {
                                    bsonType: ["int", "null"],
                                },
                                newMarketsPercent: {
                                    bsonType: ["int", "null"],
                                },
                                newUsesPercent: { bsonType: ["int", "null"] },
                                newInfrastructureDetail: {
                                    bsonType: ["string", "null"],
                                },
                                priceSupportDetail: {
                                    bsonType: ["string", "null"],
                                },
                                businessCollectionsDetail: {
                                    bsonType: ["string", "null"],
                                },
                                communicationsDetail: {
                                    bsonType: ["string", "null"],
                                },
                                newMarketsDetail: {
                                    bsonType: ["string", "null"],
                                },
                                newUsesDetail: { bsonType: ["string", "null"] },
                                sectionStatus: {
                                    bsonType: "string",
                                    enum: [
                                        "NotStarted",
                                        "InProgress",
                                        "Completed",
                                        "Submitted",
                                        "Queried",
                                    ],
                                },
                            },
                        },
                        samplingPlan: {
                            bsonType: "object",
                            properties: {
                                files: {
                                    bsonType: "array",
                                    items: {
                                        bsonType: "object",
                                        required: [
                                            "fileId",
                                            "filename",
                                            "contentType",
                                            "uploadedByUserId",
                                            "scanStatus",
                                        ],
                                        properties: {
                                            fileId: { bsonType: "string" },
                                            filename: { bsonType: "string" },
                                            contentType: { bsonType: "string" },
                                            uploadedAt: { bsonType: "date" },
                                            uploadedByUserId: {
                                                bsonType: "string",
                                            },
                                            scanStatus: {
                                                bsonType: "string",
                                                enum: [
                                                    "Pending",
                                                    "Clean",
                                                    "Infected",
                                                ],
                                            },
                                        },
                                    },
                                },
                                sectionStatus: {
                                    bsonType: "string",
                                    enum: [
                                        "NotStarted",
                                        "InProgress",
                                        "Completed",
                                        "Submitted",
                                        "Queried",
                                    ],
                                },
                            },
                        },
                    },
                },
            },
            validationLevel: "strict",
            validationAction: "error",
        });

        console.log(
            "accreditationApplications collection created with strict validation",
        );

        const collection = db.collection(collectionName);

        await Promise.all([
            collection.createIndex({ organisationId: 1 }),
            collection.createIndex({ applicationStatus: 1 }),
            collection.createIndex({ materialType: 1 }),
            collection.createIndex({ year: 1 }),
            collection.createIndex({ sourceReExAccreditationId: 1 }),
            collection.createIndex({
                organisationId: 1,
                materialType: 1,
                year: 1,
            }),
            collection.createIndex(
                { applicationReference: 1 },
                { unique: true, sparse: true },
            ),
            collection.createIndex(
                { caseManagementWorkItemId: 1 },
                { unique: true, sparse: true },
            ),
        ]);

        console.log("All indexes created successfully");
    } finally {
        await client.close();
    }
}
