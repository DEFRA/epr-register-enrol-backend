import test from "node:test";
import assert from "node:assert/strict";
import { sign, signedHeaders } from "./signing.mjs";

const {
    BASE_URL,
    CLIENT_ID,
    CLIENT_SECRET,
    TEST_ORGANISATION_NUMBER: ORG,
    TEST_REGISTRATION_NUMBER: REG,
    TEST_YEAR: YEAR,
    OTHER_CLIENT_ID,
    OTHER_CLIENT_SECRET,
} = process.env;

test("worked example from the guide", () => {
    assert.equal(
        sign(
            "example-secret-do-not-use",
            "example-client",
            "2027-01-15T10:30:00Z",
            "bm9uY2UtZXhhbXBsZS0wMDAx",
        ),
        "mJPc+QOEbTNBzB60Pj2NjSOy8bOYmvoCjCwPQS2CSTk=",
    );
});

const configured = Boolean(
    BASE_URL && CLIENT_ID && CLIENT_SECRET && ORG && REG && YEAR,
);

test("live configuration is present", () => {
    assert.ok(
        configured,
        "Set BASE_URL, CLIENT_ID, CLIENT_SECRET, TEST_ORGANISATION_NUMBER, TEST_REGISTRATION_NUMBER and TEST_YEAR",
    );
});

const live = { skip: !configured && "live configuration missing" };
const path = `/v1/organisations/${ORG}/registrations/${REG}/accreditations/${YEAR}`;
const get = (p, headers) => fetch(`${BASE_URL}${p}`, { headers });
const mine = (overrides) => signedHeaders(CLIENT_ID, CLIENT_SECRET, overrides);

async function assertProblem(res, status, code) {
    assert.equal(res.status, status);
    assert.match(
        res.headers.get("content-type") ?? "",
        /application\/problem\+json/,
    );
    const body = await res.json();
    assert.equal(body.code, code);
    assert.ok(body.detail);
    assert.ok(body.traceId);
}

test("returns the accreditation", live, async () => {
    const res = await get(path, mine());
    assert.equal(res.status, 200);
    const body = await res.json();
    assert.equal(body.registrationNumber, REG);
    assert.equal(body.accreditationYear, Number(YEAR));
    assert.equal(body.status, "approved");
    assert.ok(body.accreditationNumber);
    assert.ok(
        Array.isArray(body.statusHistory) && body.statusHistory.length >= 1,
    );
});

test("searches accreditations as a page", live, async () => {
    const res = await get(`/v1/accreditations?year=${YEAR}`, mine());
    assert.equal(res.status, 200);
    const body = await res.json();
    assert.ok(Array.isArray(body.items));
    assert.equal(typeof body.totalItems, "number");
    assert.ok(body.items.every((item) => !("signatories" in item)));
});

test("lists overseas sites as a page", live, async () => {
    const res = await get(`${path}/overseas-sites`, mine());
    assert.equal(res.status, 200);
    const body = await res.json();
    assert.ok(Array.isArray(body.items));
    assert.equal(typeof body.totalItems, "number");
});

test("unknown accreditation is 404", live, async () => {
    const res = await get(
        `/v1/organisations/${ORG}/registrations/NO-SUCH-REG/accreditations/${YEAR}`,
        mine(),
    );
    await assertProblem(res, 404, "accreditation_not_found");
});

test("missing headers are 401", live, async () => {
    await assertProblem(await get(path, {}), 401, "missing_header");
});

test("wrong secret is 401", live, async () => {
    const res = await get(path, signedHeaders(CLIENT_ID, "not-the-secret"));
    await assertProblem(res, 401, "invalid_signature");
});

test("expired timestamp is 401", live, async () => {
    const old = new Date(Date.now() - 10 * 60 * 1000)
        .toISOString()
        .replace(/\.\d{3}Z$/, "Z");
    await assertProblem(
        await get(path, mine({ timestamp: old })),
        401,
        "expired_timestamp",
    );
});

test("reused nonce is 401", live, async () => {
    const headers = mine();
    assert.equal((await get(path, headers)).status, 200);
    await assertProblem(await get(path, headers), 401, "reused_nonce");
});

test("writes are refused", live, async () => {
    const res = await fetch(`${BASE_URL}${path}`, {
        method: "POST",
        headers: mine(),
    });
    assert.equal(res.status, 405);
});

test(
    "another client's secret cannot pass as this client",
    {
        skip:
            !(configured && OTHER_CLIENT_ID && OTHER_CLIENT_SECRET) &&
            "OTHER_CLIENT_* not set",
    },
    async () => {
        const res = await get(
            path,
            signedHeaders(CLIENT_ID, OTHER_CLIENT_SECRET),
        );
        await assertProblem(res, 401, "invalid_signature");
    },
);
