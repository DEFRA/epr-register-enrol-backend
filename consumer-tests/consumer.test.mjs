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

// The status on a date, from a statusTimeline, as the guide describes it. ISO dates compare as
// strings.
const statusOn = (timeline, date) =>
    timeline.find(
        (p) =>
            p.effectiveFromDate <= date &&
            (p.effectiveToDate === null || date <= p.effectiveToDate),
    )?.status ?? null;

// The same answer straight from the history: of the entries started by the date, the latest start,
// then the latest recordedAt. The history is oldest first, so a full tie goes to the later entry
// (sort is stable).
const statusFromHistory = (history, date) =>
    history
        .filter((e) => e.effectiveFromDate <= date)
        .sort(
            (a, b) =>
                a.effectiveFromDate.localeCompare(b.effectiveFromDate) ||
                Date.parse(a.recordedAt) - Date.parse(b.recordedAt),
        )
        .at(-1)?.status ?? null;

const addDays = (date, days) => {
    const day = new Date(`${date}T00:00:00Z`);
    day.setUTCDate(day.getUTCDate() + days);
    return day.toISOString().slice(0, 10);
};

// Every record with a statusHistory has a statusTimeline: periods in order, with no gaps or
// overlaps, only the last open-ended, that agree with the history and latestStatus.
function assertStatusTimeline(record, what) {
    const { statusHistory: history, statusTimeline: timeline } = record;
    assert.ok(history?.length >= 1, `${what} has no statusHistory`);
    assert.ok(timeline?.length >= 1, `${what} has no statusTimeline`);

    timeline.forEach((period, index) => {
        assert.ok(period.status, `${what} period has no status`);
        assert.match(period.effectiveFromDate, /^\d{4}-\d{2}-\d{2}$/, what);
        const next = timeline[index + 1];
        if (!next) {
            assert.equal(period.effectiveToDate, null, `${what} last period`);
            return;
        }
        assert.ok(
            period.effectiveFromDate <= period.effectiveToDate,
            `${what} period ends before it starts`,
        );
        assert.equal(
            next.effectiveFromDate,
            addDays(period.effectiveToDate, 1),
            `${what} periods have a gap or overlap`,
        );
        assert.notEqual(
            next.status,
            period.status,
            `${what} adjacent periods are not merged`,
        );
    });

    assert.equal(timeline.at(-1).status, record.latestStatus.status, what);

    // The status only changes where an entry or a period starts or ends, so check each of those
    // days and the days either side.
    const dates = [...history, ...timeline]
        .flatMap((e) => [e.effectiveFromDate, e.effectiveToDate])
        .filter(Boolean)
        .flatMap((date) => [-1, 0, 1].map((days) => addDays(date, days)));
    for (const date of dates) {
        assert.equal(
            statusOn(timeline, date),
            statusFromHistory(history, date),
            `${what} status on ${date}`,
        );
    }
}

function assertSiteTimelines(sites) {
    for (const site of sites) {
        assertStatusTimeline(site, `overseas site ${site.orsId}`);
        for (const interim of site.interimSites) {
            assertStatusTimeline(
                interim,
                `interim site ${interim.siteNumber} of ${site.orsId}`,
            );
        }
    }
}

test("returns the accreditation", live, async () => {
    const res = await get(path, mine());
    assert.equal(res.status, 200);
    const body = await res.json();
    assert.equal(body.registrationNumber, REG);
    assert.equal(body.accreditationYear, Number(YEAR));
    assert.equal(body.latestStatus.status, "approved");
    assert.ok(body.accreditationNumber);
    assertStatusTimeline(body, "accreditation");
    assertSiteTimelines(body.overseasSites ?? []);
});

test("searches accreditations as a page", live, async () => {
    const res = await get(`/v1/accreditations?year=${YEAR}`, mine());
    assert.equal(res.status, 200);
    const body = await res.json();
    assert.ok(Array.isArray(body.items));
    assert.equal(typeof body.totalItems, "number");
    assert.ok(body.items.every((item) => !("signatories" in item)));
    // A search item has no history of its own; its overseas sites do.
    assertSiteTimelines(body.items.flatMap((item) => item.overseasSites ?? []));
});

test("search filters by organisation and registration number", live, async () => {
    const res = await get(
        `/v1/accreditations?organisationNumber=${ORG}&registrationNumber=${REG}`,
        mine(),
    );
    assert.equal(res.status, 200);
    const body = await res.json();
    assert.ok(body.items.length >= 1);
    assert.ok(
        body.items.every(
            (item) =>
                String(item.organisationNumber) === String(ORG) &&
                item.registrationNumber === REG,
        ),
    );
});

test("a search filter that matches nothing is an empty page", live, async () => {
    const res = await get(
        "/v1/accreditations?registrationNumber=NO-SUCH-REG",
        mine(),
    );
    assert.equal(res.status, 200);
    const body = await res.json();
    assert.equal(body.totalItems, 0);
    assert.deepEqual(body.items, []);
});

test("an invalid search filter is 400", live, async () => {
    await assertProblem(
        await get("/v1/accreditations?organisationNumber=abc", mine()),
        400,
        "invalid_organisation_number",
    );
});

test("the accreditation carries its overseas sites only if it is an exporter", live, async () => {
    const res = await get(path, mine());
    assert.equal(res.status, 200);
    const body = await res.json();
    if (body.wasteProcessingType === "exporter") {
        assert.ok(Array.isArray(body.overseasSites));
        assert.ok(
            body.overseasSites.every(
                (site) =>
                    site.orsId &&
                    Array.isArray(site.statusHistory) &&
                    Array.isArray(site.interimSites),
            ),
        );
    } else {
        assert.ok(!("overseasSites" in body));
    }
});

test("lists overseas sites as a page, and gets each one", live, async () => {
    const res = await get(`${path}/overseas-sites`, mine());
    assert.equal(res.status, 200);
    const body = await res.json();
    assert.ok(Array.isArray(body.items));
    assert.equal(typeof body.totalItems, "number");
    assertSiteTimelines(body.items);

    // A reprocessor has no overseas sites to get.
    for (const site of body.items) {
        const one = await get(`${path}/overseas-sites/${site.orsId}`, mine());
        assert.equal(one.status, 200);
        assertSiteTimelines([await one.json()]);
    }
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
