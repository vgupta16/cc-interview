// Checks the reservation API end to end over HTTP: identity, isolation, error order, time and
// duration rules, capacity, concurrency, and ownership. See DESIGN.md, Verification.
//
// Usage: node scripts/verify-api.mjs [apiUrl]      (default http://localhost:5080; Node 20+)
// Run it against a freshly started API: the checks assume an empty reservation store.

const API = (process.argv[2] ?? process.env.API_URL ?? "http://localhost:5080").replace(/\/$/, "");

const A1 = "a1a1a1a1-a1a1-a1a1-a1a1-a1a1a1a1a1a1";
const A2 = "a2a2a2a2-a2a2-a2a2-a2a2-a2a2a2a2a2a2";
const B1 = "b1b1b1b1-b1b1-b1b1-b1b1-b1b1b1b1b1b1";
const C1 = "c1c1c1c1-c1c1-c1c1-c1c1-c1c1c1c1c1c1";
const UNKNOWN_RESIDENT = "d1d1d1d1-d1d1-d1d1-d1d1-d1d1d1d1d1d1"; // the unregistered UI choice
const UNKNOWN_AMENITY = "99999999-9999-9999-9999-999999999999";
const PARTY_A = "11111111-1111-1111-1111-111111111111";
const GYM_A = "22222222-2222-2222-2222-222222222222";
const PARKING_A = "33333333-3333-3333-3333-333333333333";
const PARTY_B = "44444444-4444-4444-4444-444444444444";

const DAY_MS = 24 * 60 * 60 * 1000;
const SLOT_MS = 30 * 60 * 1000;
const MAX_DAYS_AHEAD = 90;
const utcDate = (daysFromToday) => new Date(Date.now() + daysFromToday * DAY_MS).toISOString().slice(0, 10);
const utcTime = (ms) => new Date(ms).toISOString().replace(".000Z", "Z");
const D = utcDate(1); // tomorrow (UTC)
const D2 = utcDate(2); // the day after
const PAST = `${utcDate(-1)}T10:00:00Z`;

let passed = 0;
let failed = 0;

function report(ok, name, detail) {
  ok ? passed++ : failed++;
  console.log(`${ok ? "PASS" : "FAIL"}  ${name}${ok ? "" : `: ${detail}`}`);
}

/** Sends a request and checks its status. Returns the parsed response for body checks. */
async function req(name, expected, path, { resident, body } = {}) {
  const headers = {};
  if (resident !== undefined) headers["X-RESIDENT-ID"] = resident;
  if (body !== undefined) headers["Content-Type"] = "application/json";
  const res = await fetch(API + path, {
    method: body === undefined ? "GET" : "POST",
    headers,
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  const text = await res.text();
  let json = null;
  try {
    json = text ? JSON.parse(text) : null;
  } catch {}
  const response = {
    status: res.status,
    type: res.headers.get("content-type") ?? "",
    json,
    text
  };
  report(res.status === expected, `${name} -> ${res.status}`, `expected ${expected}: ${text.slice(0, 200)}`);
  return response;
}

/** Checks a condition on a response body. */
function expect(name, condition, response) {
  report(Boolean(condition), `  ${name}`, (response?.text ?? "").slice(0, 200));
}

const avail = (amenityId, startUtc, durationMinutes) =>
  `/api/amenities/${amenityId}/availability?startUtc=${startUtc}&durationMinutes=${durationMinutes}`;
const create = (amenityId, startUtc, durationMinutes) => ({
  amenityId,
  startUtc,
  durationMinutes
});
const isProblem = (r) => r.type.startsWith("application/problem+json");
/** A response's list, or [] when the body isn't one, so a failed request fails its check instead of the run. */
const list = (r) => (Array.isArray(r.json) ? r.json : []);

console.log(`API: ${API}`);

try {
  console.log("== Identity (401 on every endpoint)");
  for (const [label, resident] of [
    ["missing", undefined],
    ["malformed", "nope"],
    ["unknown", UNKNOWN_RESIDENT]
  ]) {
    const r = await req(`amenities, ${label} header`, 401, "/api/amenities", {
      resident
    });
    expect("problem+json", isProblem(r) && r.json?.status === 401, r);
    await req(`availability, ${label} header`, 401, avail(GYM_A, `${D}T10:00:00Z`, 30), { resident });
    await req(`create, ${label} header`, 401, "/api/reservations", {
      resident,
      body: create(GYM_A, `${D}T09:00:00Z`, 30)
    });
    await req(`mine, ${label} header`, 401, "/api/reservations/mine", {
      resident
    });
  }

  console.log("== Scoping");
  let r = await req("amenities as A1", 200, "/api/amenities", { resident: A1 });
  expect(
    "only Building A's 3 amenities",
    r.json?.length === 3 && r.json.every((a) => a.buildingId.startsWith("aaaaaaaa")),
    r
  );
  r = await req("amenities as B1", 200, "/api/amenities", { resident: B1 });
  expect(
    "only Building B's 2 amenities",
    r.json?.length === 2 && r.json.every((a) => a.buildingId.startsWith("bbbbbbbb")),
    r
  );

  console.log("== Resident in a building with no amenities");
  r = await req("amenities as C1", 200, "/api/amenities", { resident: C1 });
  expect("empty amenities array", Array.isArray(r.json) && r.json.length === 0, r);
  r = await req("My Reservations as C1", 200, "/api/reservations/mine", { resident: C1 });
  expect("empty reservations array", Array.isArray(r.json) && r.json.length === 0, r);
  await req("C1 cannot read Building A's availability", 404, avail(GYM_A, `${D}T10:00:00Z`, 30), { resident: C1 });
  await req("C1 cannot reserve Building A's Gym", 404, "/api/reservations", {
    resident: C1,
    body: create(GYM_A, `${D}T10:00:00Z`, 30)
  });

  console.log("== Error order across buildings");
  const crossBuilding = await req("availability, B's Party Room as A1, invalid query", 404, avail(PARTY_B, "bad", 7), {
    resident: A1
  });
  expect(
    "amenity-not-found ProblemDetails",
    isProblem(crossBuilding) && crossBuilding.json?.title === "Amenity not found",
    crossBuilding
  );
  r = await req("availability, unknown amenity as A1", 404, avail(UNKNOWN_AMENITY, `${D}T10:00:00Z`, 30), {
    resident: A1
  });
  // Every ProblemDetails carries a random per-request traceId; everything else must match, so the cases can't be told apart.
  const withoutTraceId = (response) => JSON.stringify({ ...response.json, traceId: undefined });
  expect(
    "identical to the cross-building 404, apart from its per-request traceId",
    withoutTraceId(r) === withoutTraceId(crossBuilding),
    r
  );
  await req("create, B's Party Room as A1, invalid values", 404, "/api/reservations", {
    resident: A1,
    body: create(PARTY_B, "bad", 7)
  });
  r = await req("availability, malformed amenity ID in path", 404, "/api/amenities/not-a-guid/availability", {
    resident: A1
  });
  expect("empty body (routing, not the application)", r.text === "", r);

  console.log("== Framework errors (binding fails before the endpoint runs)");
  // Binding runs before the identity check, so these fail with 400 even without X-RESIDENT-ID. On the Development
  // backend that `dotnet run` starts, their bodies must be ProblemDetails JSON (AddProblemDetails), not text/plain
  // stack traces that make the generated client reject. (In Production they're empty, which the client also resolves.)
  r = await req("availability, non-integer durationMinutes, no header", 400, avail(GYM_A, `${D}T10:00:00Z`, "abc"));
  expect("ProblemDetails JSON, not a text stack trace", isProblem(r) && r.json?.status === 400, r);
  const malformed = await fetch(`${API}/api/reservations`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: '{"amenityId":'
  });
  const malformedType = malformed.headers.get("content-type") ?? "";
  const malformedText = await malformed.text();
  report(
    malformed.status === 400,
    `create, malformed JSON body, no header -> ${malformed.status}`,
    malformedText.slice(0, 200)
  );
  report(
    malformedType.startsWith("application/problem+json"),
    "  ProblemDetails JSON, not a text stack trace",
    malformedType
  );

  console.log("== Timestamp rules (availability, Gym)");
  r = await req("start without an offset", 400, avail(GYM_A, `${D}T10:00:00`, 30), { resident: A1 });
  expect("startUtc error", isProblem(r) && r.json?.errors?.startUtc, r);
  r = await req("start with +05:00 (encoded)", 400, avail(GYM_A, `${D}T10:00:00%2B05:00`, 30), { resident: A1 });
  expect("startUtc error", r.json?.errors?.startUtc, r);
  r = await req("start at 10:15", 400, avail(GYM_A, `${D}T10:15:00Z`, 30), {
    resident: A1
  });
  expect("boundary error", r.json?.errors?.startUtc?.[0]?.includes("boundary"), r);
  r = await req("start in the past", 400, avail(GYM_A, PAST, 30), {
    resident: A1
  });
  expect("future error", r.json?.errors?.startUtc?.[0]?.includes("future"), r);
  await req("start with +00:00 (encoded as %2B00:00)", 200, avail(GYM_A, `${D}T10:00:00%2B00:00`, 30), {
    resident: A1
  });
  await req("start with .000Z (toISOString)", 200, avail(GYM_A, `${D}T10:00:00.000Z`, 30), { resident: A1 });
  await req("start with a bare + (decoded as a space)", 400, avail(GYM_A, `${D}T10:00:00+00:00`, 30), { resident: A1 });
  console.log(`== Booking limit (a start at most ${MAX_DAYS_AHEAD} days ahead)`);
  // The last start within the limit moves at each 30-minute boundary. Wait out the last few seconds
  // before one, so this script and the API agree on it for the checks below.
  while (Date.now() % SLOT_MS > SLOT_MS - 5000) await new Promise((resolve) => setTimeout(resolve, 1000));
  const lastStart = Math.floor((Date.now() + MAX_DAYS_AHEAD * DAY_MS) / SLOT_MS) * SLOT_MS;
  r = await req("availability from the last start within the limit", 200, avail(GYM_A, utcTime(lastStart), 30), {
    resident: A1
  });
  expect(
    "one window: later ones would start past the limit",
    r.json?.length === 1 && Date.parse(r.json[0].startUtc) === lastStart,
    r
  );
  await req("reserve that window", 201, "/api/reservations", {
    resident: A1,
    body: create(GYM_A, utcTime(lastStart), 30)
  });
  r = await req("availability 30 minutes past the limit", 400, avail(GYM_A, utcTime(lastStart + SLOT_MS), 30), {
    resident: A1
  });
  expect("names the limit", r.json?.errors?.startUtc?.[0]?.includes(`${MAX_DAYS_AHEAD} days`), r);
  await req("create 30 minutes past the limit", 400, "/api/reservations", {
    resident: A1,
    body: create(GYM_A, utcTime(lastStart + SLOT_MS), 30)
  });
  await req("availability in year 9999 (400, not an overflow 500)", 400, avail(GYM_A, "9999-12-31T23:30:00Z", 30), {
    resident: A1
  });

  console.log("== Duration rules");
  r = await req("45 minutes", 400, avail(GYM_A, `${D}T10:00:00Z`, 45), {
    resident: A1
  });
  expect("durationMinutes error", r.json?.errors?.durationMinutes, r);
  r = await req("120 minutes on the Gym (max 90)", 400, avail(GYM_A, `${D}T10:00:00Z`, 120), { resident: A1 });
  expect("names the 90-minute max", r.json?.errors?.durationMinutes?.[0]?.includes("90"), r);
  r = await req("missing duration", 400, `/api/amenities/${GYM_A}/availability?startUtc=${D}T10:00:00Z`, {
    resident: A1
  });
  expect("durationMinutes required", JSON.stringify(r.json?.errors?.durationMinutes) === '["Required."]', r);
  await req("create with 45 minutes", 400, "/api/reservations", {
    resident: A1,
    body: create(GYM_A, `${D}T10:00:00Z`, 45)
  });
  await req("create starting in the past", 400, "/api/reservations", {
    resident: A1,
    body: create(GYM_A, PAST, 30)
  });

  console.log("== Availability shape");
  r = await req("Gym availability, 90 minutes", 200, avail(GYM_A, `${D}T10:00:00Z`, 90), { resident: A1 });
  expect(
    "five windows, 30 minutes apart, each 90 minutes, UTC",
    r.json?.length === 5 &&
      r.json.every((w) => w.startUtc.endsWith("+00:00") && w.endUtc.endsWith("+00:00") && w.remainingCapacity === 2) &&
      r.json.map((w) => w.startUtc.slice(11, 16)).join() === "10:00,10:30,11:00,11:30,12:00",
    r
  );

  console.log("== Capacity boundary (Gym, capacity 2)");
  r = await req("A1 10:00-10:30", 201, "/api/reservations", {
    resident: A1,
    body: create(GYM_A, `${D}T10:00:00Z`, 30)
  });
  expect("created with server times in UTC", r.json?.residentId === A1 && r.json?.startUtc?.endsWith("+00:00"), r);
  r = await req("A2 10:30-11:00", 201, "/api/reservations", {
    resident: A2,
    body: create(GYM_A, `${D}T10:30:00Z`, 30)
  });
  const a2GymId = r.json?.id;
  await req("A1 10:00-11:00 (one spot free in every slot)", 201, "/api/reservations", {
    resident: A1,
    body: create(GYM_A, `${D}T10:00:00Z`, 60)
  });
  const conflict = await req("A2 10:00-10:30 (now full)", 409, "/api/reservations", {
    resident: A2,
    body: create(GYM_A, `${D}T10:00:00Z`, 30)
  });
  expect("409 ProblemDetails", isProblem(conflict) && conflict.json?.title === "Reservation conflict", conflict);
  r = await req("Gym availability after, 30 minutes", 200, avail(GYM_A, `${D}T10:00:00Z`, 30), { resident: A1 });
  expect(
    "10:00 and 10:30 show 0, 11:00 shows 2",
    list(r)
      .map((w) => w.remainingCapacity)
      .join() === "0,0,2,2,2",
    r
  );
  await req("create with +00:00 inside JSON (no encoding needed)", 201, "/api/reservations", {
    resident: A1,
    body: create(PARTY_A, `${D}T15:00:00+00:00`, 30)
  });

  console.log("== Concurrency (20 parallel creates, Party Room, capacity 1)");
  const statuses = await Promise.all(
    Array.from({ length: 20 }, (_, i) =>
      fetch(`${API}/api/reservations`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "X-RESIDENT-ID": i % 2 === 0 ? A1 : A2
        },
        body: JSON.stringify(create(PARTY_A, `${D}T12:00:00Z`, 60))
      }).then((res) => res.status)
    )
  );
  const created = statuses.filter((s) => s === 201).length;
  const conflicts = statuses.filter((s) => s === 409).length;
  report(created === 1 && conflicts === 19, "exactly 1 x 201 and 19 x 409", `got ${statuses.join(", ")}`);

  console.log("== Crossing midnight (Guest Parking)");
  r = await req("A2 23:00 for 120 minutes", 201, "/api/reservations", {
    resident: A2,
    body: create(PARKING_A, `${D}T23:00:00Z`, 120)
  });
  expect("ends at 01:00 the next day", r.json?.endUtc?.startsWith(`${D2}T01:00`), r);
  r = await req("availability at 00:00 the next day", 200, avail(PARKING_A, `${D2}T00:00:00Z`, 30), { resident: A1 });
  expect(
    "00:00 and 00:30 show 0, 01:00 shows 1",
    r.json
      ?.slice(0, 3)
      .map((w) => w.remainingCapacity)
      .join() === "0,0,1",
    r
  );

  console.log("== Ownership");
  r = await req("My Reservations as A1", 200, "/api/reservations/mine", {
    resident: A1
  });
  expect(
    "only A1's, without A2's Gym reservation",
    r.json?.length > 0 && r.json.every((x) => x.residentId === A1) && !r.json.some((x) => x.id === a2GymId),
    r
  );
  expect(
    "ordered by start time",
    list(r)
      .map((x) => x.startUtc)
      .join() ===
      [...list(r)]
        .map((x) => x.startUtc)
        .sort()
        .join(),
    r
  );
  r = await req("My Reservations as A2", 200, "/api/reservations/mine", {
    resident: A2
  });
  expect(
    "includes A2's Gym reservation, only A2's",
    list(r).some((x) => x.id === a2GymId) && r.json.every((x) => x.residentId === A2),
    r
  );
  r = await req("My Reservations as B1", 200, "/api/reservations/mine", {
    resident: B1
  });
  expect("empty", Array.isArray(r.json) && r.json.length === 0, r);

  console.log(`\n409 body: ${conflict.text}`);
} catch (err) {
  // A check that throws (for example, reading a field an error body doesn't have) counts as a failure.
  report(false, "script ran to completion", err.stack ?? err.message);
}

console.log(`RESULT: ${passed} passed, ${failed} failed`);
process.exit(failed === 0 ? 0 : 1);
