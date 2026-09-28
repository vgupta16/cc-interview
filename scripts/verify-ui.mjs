// Drives the reservation screen in headless Chrome, Chromium, or Edge through the DevTools
// protocol (no packages needed): scoping, reserving until full, a stale-window 409, switching
// residents, error messages when the API fails, and the 90-day limit. See DESIGN.md, Verification.
//
// Usage: node scripts/verify-ui.mjs      (Node 20+)
// Needs a freshly started API (empty store) and the Vite dev server (`pnpm dev`).
// Environment: UI_URL (default http://localhost:5173), API_URL (default http://localhost:5080),
// CHROME (browser executable, if it isn't found automatically), SCREENSHOT_DIR (optional).

import { spawn } from "node:child_process";
import { existsSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { delimiter, join } from "node:path";

const UI_URL = process.env.UI_URL ?? "http://localhost:5173";
const API_URL = process.env.API_URL ?? "http://localhost:5080";
const SCREENSHOT_DIR = process.env.SCREENSHOT_DIR;

const A1 = "a1a1a1a1-a1a1-a1a1-a1a1-a1a1a1a1a1a1";
const A2 = "a2a2a2a2-a2a2-a2a2-a2a2-a2a2a2a2a2a2";
const B1 = "b1b1b1b1-b1b1-b1b1-b1b1-b1b1b1b1b1b1";
const C1 = "c1c1c1c1-c1c1-c1c1-c1c1-c1c1c1c1c1c1";
const UNKNOWN_RESIDENT = "d1d1d1d1-d1d1-d1d1-d1d1-d1d1d1d1d1d1";
const PARTY_A = "11111111-1111-1111-1111-111111111111";
const DAY_MS = 24 * 60 * 60 * 1000;
const SLOT_MS = 30 * 60 * 1000;
const MAX_DAYS_AHEAD = 90;

/** Finds a Chromium-based browser: $CHROME, then the usual install locations for each OS. */
function findBrowser() {
  if (process.env.CHROME) return process.env.CHROME;
  const candidates = {
    darwin: [
      "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
      "/Applications/Chromium.app/Contents/MacOS/Chromium",
      "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge"
    ],
    win32: [
      join(process.env.PROGRAMFILES ?? "C:\\Program Files", "Google\\Chrome\\Application\\chrome.exe"),
      join(process.env["PROGRAMFILES(X86)"] ?? "C:\\Program Files (x86)", "Google\\Chrome\\Application\\chrome.exe"),
      join(process.env.LOCALAPPDATA ?? "", "Google\\Chrome\\Application\\chrome.exe"),
      join(process.env["PROGRAMFILES(X86)"] ?? "C:\\Program Files (x86)", "Microsoft\\Edge\\Application\\msedge.exe")
    ]
  }[process.platform];
  const onPath = ["google-chrome", "google-chrome-stable", "chromium", "chromium-browser", "microsoft-edge"].flatMap(
    (name) => (process.env.PATH ?? "").split(delimiter).map((dir) => join(dir, name))
  );
  const found = [...(candidates ?? []), ...onPath].find((path) => path && existsSync(path));
  if (!found) {
    console.error("No Chrome, Chromium, or Edge found. Set CHROME to the browser executable.");
    process.exit(1);
  }
  return found;
}

const profileDir = mkdtempSync(join(tmpdir(), "verify-ui-"));
const browserArgs = [
  "--headless=new",
  "--remote-debugging-pipe",
  `--user-data-dir=${profileDir}`,
  "--no-first-run",
  "--window-size=1000,1500",
  "about:blank"
];
if (process.platform === "linux" && process.getuid?.() === 0) browserArgs.unshift("--no-sandbox");

// With --remote-debugging-pipe, the browser reads DevTools commands from file descriptor 3 and
// writes replies and events to descriptor 4. This needs no WebSocket, so it works on Node 20.
const browser = spawn(findBrowser(), browserArgs, {
  stdio: ["ignore", "ignore", "ignore", "pipe", "pipe"]
});
browser.on("error", (err) => {
  console.error(`Couldn't start the browser: ${err.message}. Set CHROME to the browser executable.`);
  process.exit(1);
});
const toBrowser = browser.stdio[3];
const fromBrowser = browser.stdio[4];

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
let passed = 0;
let failed = 0;
function check(name, ok, detail = "") {
  ok ? passed++ : failed++;
  console.log(`${ok ? "PASS" : "FAIL"}  ${name}${ok || !detail ? "" : `: ${detail}`}`);
}

let nextId = 0;
const pending = new Map();
const pageErrors = [];
let pageSession = null;
// null, 'http500' (all reads), 'mine500' (only My Reservations), 'refused', or 'hold' (keep
// requests pending until released, to inspect the screen mid-load).
let apiFailure = null;
const heldRequests = [];

// Messages on the pipe are JSON, each ending with a zero byte. Buffer the raw bytes so a multi-byte
// character split across two chunks decodes correctly.
let received = Buffer.alloc(0);
fromBrowser.on("data", (chunk) => {
  received = Buffer.concat([received, chunk]);
  let end;
  while ((end = received.indexOf(0)) !== -1) {
    const message = JSON.parse(received.subarray(0, end).toString("utf8"));
    received = received.subarray(end + 1);
    handleMessage(message);
  }
});

function handleMessage(msg) {
  if (msg.id && pending.has(msg.id)) {
    const { resolve, reject } = pending.get(msg.id);
    pending.delete(msg.id);
    msg.error ? reject(new Error(msg.error.message)) : resolve(msg.result);
  } else if (msg.method === "Fetch.requestPaused") {
    const { requestId } = msg.params;
    if (
      apiFailure === "http500" ||
      (apiFailure === "mine500" && new URL(msg.params.request.url).pathname === "/api/reservations/mine")
    ) {
      send("Fetch.fulfillRequest", {
        requestId,
        responseCode: 500,
        responseHeaders: [],
        body: ""
      });
    } else if (apiFailure === "hold") {
      heldRequests.push({ requestId, url: msg.params.request.url });
    } else if (apiFailure === "refused") {
      send("Fetch.failRequest", {
        requestId,
        errorReason: "ConnectionRefused"
      });
    } else {
      send("Fetch.continueRequest", { requestId });
    }
  } else if (msg.method === "Runtime.exceptionThrown") {
    pageErrors.push(msg.params.exceptionDetails.exception?.description ?? msg.params.exceptionDetails.text);
  } else if (msg.method === "Runtime.consoleAPICalled" && msg.params.type === "error") {
    pageErrors.push(msg.params.args.map((a) => a.value ?? a.description).join(" "));
  }
}

function sendToBrowser(method, params = {}, sessionId) {
  return new Promise((resolve, reject) => {
    const id = ++nextId;
    pending.set(id, { resolve, reject });
    toBrowser.write(`${JSON.stringify({ id, method, params, ...(sessionId ? { sessionId } : {}) })}\0`);
  });
}

/** Sends a command to the page under test. */
function send(method, params = {}) {
  return sendToBrowser(method, params, pageSession);
}

// Open a page and attach to it; page commands then carry its session ID.
const { targetId } = await sendToBrowser("Target.createTarget", {
  url: "about:blank"
});
pageSession = (await sendToBrowser("Target.attachToTarget", { targetId, flatten: true })).sessionId;

async function evaluate(expression) {
  const r = await send("Runtime.evaluate", {
    expression,
    awaitPromise: true,
    returnByValue: true
  });
  if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description ?? r.exceptionDetails.text);
  return r.result.value;
}

async function waitFor(expression, description, timeout = 8000) {
  const deadline = Date.now() + timeout;
  while (Date.now() < deadline) {
    if (await evaluate(`(() => { try { return !!(${expression}); } catch { return false; } })()`)) return true;
    await sleep(100);
  }
  check(`wait: ${description}`, false, "timed out");
  return false;
}

async function screenshot(name) {
  if (!SCREENSHOT_DIR) return;
  const { data } = await send("Page.captureScreenshot", {
    format: "png",
    captureBeyondViewport: true
  });
  writeFileSync(join(SCREENSHOT_DIR, name), Buffer.from(data, "base64"));
}

// Page-side helpers that read and drive the UI the way a user would.
const HELPERS = `window.__t = {
  listAfter(heading) {
    const h = [...document.querySelectorAll('h2')].find((e) => e.textContent.startsWith(heading));
    let el = h?.nextElementSibling;
    while (el && el.tagName !== 'UL') el = el.nextElementSibling;
    return el ? [...el.querySelectorAll('li')].map((li) => li.innerText.replace(/\\s*\\n\\s*/g, ' | ')) : null;
  },
  windows() { return this.listAfter('Availability') ?? []; },
  reserveButtons() { return [...document.querySelectorAll('section li button')]; },
  clickText(text) {
    const b = [...document.querySelectorAll('button')].find((e) => e.innerText.includes(text));
    if (!b) throw new Error('No button containing ' + text);
    b.click();
    return true;
  },
  setSelect(select, value) {
    Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value').set.call(select, value);
    select.dispatchEvent(new Event('change', { bubbles: true }));
    return true;
  },
  setInput(input, value) {
    Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(input, value);
    input.dispatchEvent(new Event('input', { bubbles: true }));
    return true;
  },
  residentSelect() { return document.querySelector('main > label select'); },
  durationSelect() { return document.querySelector('section select[name=duration]'); },
  startValue() {
    return document.querySelector('section input[name=startDate]').value + 'T' +
      document.querySelector('section select[name=startTime]').value;
  },
  setStart(value) {
    this.setInput(document.querySelector('section input[name=startDate]'), value.slice(0, 10));
    this.setSelect(document.querySelector('section select[name=startTime]'), value.slice(11, 16));
    return true;
  },
  pageBanner() { return document.querySelector('main > label + [role=alert]')?.innerText ?? null; },
  sectionAlert(heading) {
    let el = [...document.querySelectorAll('h2')].find((e) => e.textContent.startsWith(heading))?.nextElementSibling;
    while (el && el.tagName !== 'H2' && el.tagName !== 'SECTION') {
      if (el.getAttribute('role') === 'alert') return el.innerText;
      el = el.nextElementSibling;
    }
    return null;
  },
  messages() { return [...document.querySelectorAll('[role=alert], [role=status]')].map((p) => p.innerText).join(' || '); }
};`;

try {
  await send("Runtime.enable");
  await send("Page.enable");
  await send("Fetch.enable", { patterns: [{ urlPattern: "*/api/*" }] });
  await send("Page.navigate", { url: UI_URL });
  await sleep(500);
  await evaluate(HELPERS);

  console.log(`UI: ${UI_URL}   API: ${API_URL}`);
  console.log("== First load (Resident A1)");
  await waitFor(`__t.listAfter('Amenities')?.length === 3`, "three amenities");
  const amenities = await evaluate(`__t.listAfter('Amenities')`);
  check(
    "A1 sees only Building A's 3 amenities",
    amenities.every((a) => a.includes("Building A")),
    amenities.join(" // ")
  );
  check(
    "amenity details show capacity and max duration",
    amenities.some((a) => a.includes("2 spots") && a.includes("up to 1 h 30 min"))
  );
  check(
    "My Reservations starts empty",
    await waitFor(`document.body.innerText.includes('No upcoming reservations.')`, "empty My Reservations")
  );

  console.log("== Gym availability");
  await evaluate(`__t.clickText('Gym, Building A')`);
  await waitFor(`__t.windows().length === 5`, "five windows");
  const windows = await evaluate(`__t.windows()`);
  check(
    "five windows, all UTC, all with 2 of 2 spots open",
    windows.length === 5 && windows.every((w) => w.includes("UTC") && w.includes("2 of 2 spots open")),
    windows.join(" // ")
  );
  const durations = await evaluate(`[...__t.durationSelect().options].map((o) => o.textContent)`);
  check("Gym durations run 30 min to 1 h 30 min", durations.join() === "30 min,1 h,1 h 30 min", durations.join(", "));
  await evaluate(`__t.setSelect(__t.durationSelect(), '60')`);
  await waitFor(`__t.windows().length === 5 && __t.windows()[0].includes('2 of 2')`, "windows reload for 60 minutes");
  await screenshot("ui-1-gym-availability.png");

  console.log("== Reserve the first Gym window twice (capacity 2)");
  await evaluate(`__t.reserveButtons()[0].click()`);
  await waitFor(`__t.messages().includes('Reserved Gym, Building A')`, "success notice");
  check("success notice names the amenity and window", (await evaluate(`__t.messages()`)).includes("UTC"));
  await waitFor(`__t.listAfter('My Reservations')?.length === 1`, "My Reservations shows 1");
  check(
    "availability refreshed after the reservation",
    await waitFor(`__t.windows()[0]?.includes('1 of 2 spots open')`, "first window now 1 of 2")
  );
  await evaluate(`__t.reserveButtons()[0].click()`);
  await waitFor(`__t.windows()[0]?.toLowerCase().includes('full')`, "first window now full");
  check(
    "a full window shows Full and its Reserve button is disabled",
    await evaluate(`__t.windows()[0].toLowerCase().includes('full') && __t.reserveButtons()[0].disabled`)
  );
  check(
    "My Reservations shows both, named via the amenity list",
    (await evaluate(`__t.listAfter('My Reservations')`)).every((x) => x.startsWith("Gym, Building A"))
  );
  await screenshot("ui-2-gym-full.png");

  console.log("== Conflict: another resident takes the last Party Room spot after it was shown");
  await evaluate(`__t.clickText('Party Room, Building A')`);
  await waitFor(
    `__t.windows().length === 5 && __t.windows()[0].includes('1 of 1 spot open')`,
    "party room windows open"
  );
  const start = await evaluate(`__t.startValue()`);
  const duration = Number(await evaluate(`__t.durationSelect().value`));
  const direct = await fetch(`${API_URL}/api/reservations`, {
    method: "POST",
    headers: { "Content-Type": "application/json", "X-RESIDENT-ID": A2 },
    body: JSON.stringify({
      amenityId: PARTY_A,
      startUtc: `${start}:00Z`,
      durationMinutes: duration
    })
  });
  check("A2 reserves the same window directly through the API (201)", direct.status === 201, String(direct.status));
  check(
    'the screen still shows the stale "1 of 1 spot open"',
    (await evaluate(`__t.windows()[0]`)).includes("1 of 1 spot open")
  );
  await evaluate(`__t.reserveButtons()[0].click()`);
  check(
    "409 shows the conflict message",
    await waitFor(`__t.messages().includes('no longer has enough capacity')`, "conflict notice")
  );
  await waitFor(`__t.windows()[0]?.toLowerCase().includes('full')`, "availability refreshed to Full");
  check("selection kept and availability refreshed after 409", (await evaluate(`__t.windows().length`)) === 5);
  await screenshot("ui-3-conflict.png");

  console.log("== Switching residents");
  await evaluate(`__t.setSelect(__t.residentSelect(), '${B1}')`);
  await waitFor(`__t.listAfter('Amenities')?.length === 2`, "Building B's amenities");
  const bAmenities = await evaluate(`__t.listAfter('Amenities')`);
  check(
    "B1 sees only Building B's 2 amenities",
    bAmenities.every((a) => a.includes("Building B")),
    bAmenities.join(" // ")
  );
  check(
    "amenity selection and messages cleared",
    !(await evaluate(`!!document.querySelector('section')`)) && (await evaluate(`__t.messages()`)) === ""
  );
  check(
    "B1 sees none of Building A's reservations",
    await waitFor(`document.body.innerText.includes('No upcoming reservations.')`, "B1's empty list")
  );
  await evaluate(`__t.setSelect(__t.residentSelect(), '${A2}')`);
  await waitFor(`__t.listAfter('My Reservations')?.length === 1`, "A2's one reservation");
  check(
    "A2 sees only their own Party Room reservation, not A1's Gym ones",
    (await evaluate(`__t.listAfter('My Reservations')`))[0].startsWith("Party Room, Building A")
  );

  console.log("== Unknown resident and building with no amenities");
  await evaluate(`__t.setSelect(__t.residentSelect(), '${UNKNOWN_RESIDENT}')`);
  check(
    "unknown resident shows the API identity error",
    await waitFor(`__t.messages().includes('Send a known resident ID')`, "unknown-resident banner")
  );
  check(
    "the identity error shows once, under the resident picker, not in each list",
    await waitFor(
      `!document.body.innerText.includes('Loading') && (document.body.innerText.match(/Send a known resident ID/g) ?? []).length === 1 && __t.pageBanner()?.includes('Send a known resident ID')`,
      "single identity message"
    )
  );
  await screenshot("ui-unknown-resident.png");
  check(
    "unknown resident sees none of the previous resident's data",
    await evaluate(
      `__t.listAfter('Amenities').length === 0 && __t.listAfter('My Reservations').length === 0 && !document.querySelector('section')`
    )
  );
  check(
    "unknown resident does not get misleading empty-state text",
    await evaluate(
      `!document.body.innerText.includes('No amenities in this building.') && !document.body.innerText.includes('No upcoming reservations.')`
    )
  );
  await evaluate(`__t.setSelect(__t.residentSelect(), '${C1}')`);
  check(
    "C1 sees both empty states and no error or availability form",
    await waitFor(
      `document.body.innerText.includes('No amenities in this building.') && document.body.innerText.includes('No upcoming reservations.') && !document.querySelector('[role=alert], section')`,
      "Building C empty states"
    )
  );
  await evaluate(`__t.setSelect(__t.residentSelect(), '${A2}')`);
  check(
    "switching back to A2 restores amenities and their reservation without an error",
    await waitFor(
      `__t.listAfter('Amenities')?.length === 3 && __t.listAfter('My Reservations')?.length === 1 && __t.listAfter('My Reservations')[0].startsWith('Party Room, Building A') && !document.querySelector('[role=alert]')`,
      "A2's data after the sample cases"
    )
  );

  console.log("== Errors wait until both lists finish loading");
  // Hold the unknown resident's two reads, then release only the amenities one. Its 401 must not show in the
  // Amenities section while My Reservations is still loading; once both finish, it shows once in the page banner.
  apiFailure = "hold";
  await evaluate(`__t.setSelect(__t.residentSelect(), '${UNKNOWN_RESIDENT}')`);
  for (let i = 0; i < 50 && heldRequests.length < 2; i++) await sleep(100);
  apiFailure = null;
  const heldAmenities = heldRequests.find((r) => new URL(r.url).pathname === "/api/amenities");
  if (heldAmenities) send("Fetch.continueRequest", { requestId: heldAmenities.requestId });
  await sleep(500); // time for the released 401 to reach the page
  check(
    "while one list is still loading, the other list's error waits and its loading text stays",
    Boolean(heldAmenities) &&
      (await evaluate(
        `!document.querySelector('[role=alert]') && document.body.innerText.includes('Loading amenities...') && document.body.innerText.includes('Loading reservations...')`
      ))
  );
  for (const { requestId } of heldRequests.splice(0)) {
    if (requestId !== heldAmenities?.requestId) send("Fetch.continueRequest", { requestId });
  }
  check(
    "once both lists finish, the shared error shows once, in the page banner",
    await waitFor(
      `__t.pageBanner()?.includes('Send a known resident ID') && (document.body.innerText.match(/Send a known resident ID/g) ?? []).length === 1`,
      "banner after both lists settle"
    )
  );
  await evaluate(`__t.setSelect(__t.residentSelect(), '${A2}')`);
  await waitFor(
    `__t.listAfter('Amenities')?.length === 3 && __t.listAfter('My Reservations')?.length === 1`,
    "A2's data again"
  );

  console.log("== Switching residents while requests are pending or failing");
  // A2's data is on screen. Hold B1's requests so the screen can be inspected before any response.
  apiFailure = "hold";
  await evaluate(`__t.setSelect(__t.residentSelect(), '${B1}')`);
  for (let i = 0; i < 50 && heldRequests.length < 2; i++) await sleep(100);
  const whilePending = await evaluate(`__t.listAfter('Amenities').concat(__t.listAfter('My Reservations'))`);
  check(
    "while B1's requests are pending, none of A2's data is shown",
    whilePending.length === 0,
    whilePending.join(" // ")
  );
  check(
    "pending requests show loading text without empty-state messages",
    await evaluate(
      `document.body.innerText.includes('Loading amenities...') && document.body.innerText.includes('Loading reservations...') && !document.body.innerText.includes('No amenities in this building.') && !document.body.innerText.includes('No upcoming reservations.')`
    )
  );
  apiFailure = null;
  for (const { requestId } of heldRequests.splice(0)) send("Fetch.continueRequest", { requestId });
  await waitFor(`__t.listAfter('Amenities')?.length === 2`, "B1's amenities after the requests complete");
  check(
    "B1's data loads once its requests complete",
    (await evaluate(`__t.listAfter('Amenities')`)).every((a) => a.includes("Building B"))
  );

  // B1's data is on screen. Switch to A1 with the API unreachable: nothing of B1's may remain.
  apiFailure = "refused";
  await evaluate(`__t.setSelect(__t.residentSelect(), '${A1}')`);
  check(
    'an unreachable API shows "Couldn\'t reach the API"',
    await waitFor(
      `document.querySelector('[role=alert]')?.innerText.includes("Couldn't reach the API")`,
      "unreachable banner"
    )
  );
  const shownToA1 = await evaluate(`__t.listAfter('Amenities').concat(__t.listAfter('My Reservations'))`);
  check(
    "switching B1 to A1 while the API is unreachable shows nothing from Building B",
    !shownToA1.some((item) => item.includes("Building B")),
    shownToA1.join(" // ")
  );

  console.log("== API returning 500 (simulated in the browser)");
  apiFailure = "http500";
  await evaluate(`__t.setSelect(__t.residentSelect(), '${A2}')`);
  await waitFor(`document.querySelector('[role=alert]')?.innerText.includes('HTTP 500')`, "500 banner");
  const banner500 = await evaluate(`document.querySelector('[role=alert]')?.innerText ?? ''`);
  check(
    "a 500 (Vite with the backend down) shows an error banner, not a blank page",
    banner500.includes("localhost:5080") && (await evaluate(`!!document.querySelector('h1')`)),
    banner500
  );
  check(
    "failed lists show no misleading empty-state text",
    !(await evaluate(`document.body.innerText.includes('No amenities in this building.')`))
  );
  await screenshot("ui-4-api-500.png");
  console.log("== Only My Reservations fails");
  apiFailure = "mine500";
  await evaluate(`__t.setSelect(__t.residentSelect(), '${A1}')`);
  check(
    "amenities load while a failed reservation list shows an error, not an empty state",
    await waitFor(
      `__t.listAfter('Amenities')?.length === 3 && __t.messages().includes('HTTP 500') && !document.body.innerText.includes('Loading reservations...') && !document.body.innerText.includes('No upcoming reservations.')`,
      "partial reservation-list failure"
    )
  );
  check(
    "a failure in one list shows in that list's section, not the page banner",
    await evaluate(
      `__t.sectionAlert('My Reservations')?.includes('HTTP 500') && __t.pageBanner() === null && __t.sectionAlert('Amenities') === null`
    )
  );
  await evaluate(`__t.clickText('Gym, Building A')`);
  await waitFor(`__t.windows().length === 5`, "Gym availability after partial failure");
  check(
    "selecting an amenity preserves the reservation-list error and suppresses its empty state",
    await evaluate(
      `__t.messages().includes('HTTP 500') && !document.body.innerText.includes('No upcoming reservations.')`
    )
  );
  await evaluate(`__t.setSelect(__t.durationSelect(), '60')`);
  await waitFor(
    `__t.windows().length === 5 && !document.body.innerText.includes('Loading availability...')`,
    "availability after changing duration"
  );
  check(
    "changing duration also preserves the reservation-list failure",
    await evaluate(
      `__t.messages().includes('HTTP 500') && !document.body.innerText.includes('No upcoming reservations.')`
    )
  );
  apiFailure = null;
  await evaluate(`__t.reserveButtons().find(b => !b.disabled).click()`);
  check(
    "a successful reservation refresh clears its error and restores all three A1 reservations",
    await waitFor(
      `__t.listAfter('My Reservations')?.length === 3 && __t.messages().includes('Reserved Gym') && !document.querySelector('[role=alert]')`,
      "reservation-list recovery"
    )
  );

  console.log("== Default start and duration");
  // Gym was selected above, moments ago, so the start is its default; allow a minute for that gap.
  const MIN_LEAD_MS = 5 * 60 * 1000;
  const startMs = () => evaluate(`Date.parse(__t.startValue() + ':00Z')`);
  const durationValue = () => evaluate(`__t.durationSelect().value`);
  const asInput = (ms) => new Date(ms).toISOString().slice(0, 16);
  const isDefaultStart = (ms, now) =>
    ms % SLOT_MS === 0 && ms >= now + MIN_LEAD_MS - 60_000 && ms <= now + MIN_LEAD_MS + SLOT_MS;
  let shown = await startMs();
  check(
    "the default start is the next half hour at least 5 minutes away",
    isDefaultStart(shown, Date.now()),
    asInput(shown)
  );
  // Change both, then switch amenity: both go back to their defaults.
  await evaluate(`__t.setStart('${asInput(shown + DAY_MS)}')`);
  await evaluate(`__t.setSelect(__t.durationSelect(), '90')`);
  await evaluate(`__t.clickText('Party Room, Building A')`);
  await waitFor(
    `document.querySelector('section h2')?.textContent === 'Availability: Party Room, Building A'`,
    "Party Room selected"
  );
  shown = await startMs();
  let durationShown = await durationValue();
  check(
    "changing the amenity resets the start and duration to their defaults",
    isDefaultStart(shown, Date.now()) && durationShown === "30",
    `${asInput(shown)}, ${durationShown} minutes`
  );
  // Change both, then switch resident: A1's choices don't carry over to B1.
  await evaluate(`__t.setStart('${asInput(shown + DAY_MS)}')`);
  await evaluate(`__t.setSelect(__t.durationSelect(), '90')`);
  await evaluate(`__t.setSelect(__t.residentSelect(), '${B1}')`);
  await waitFor(`__t.listAfter('Amenities')?.length === 2`, "Building B's amenities");
  await evaluate(`__t.clickText('Party Room, Building B')`);
  await waitFor(
    `document.querySelector('section h2')?.textContent === 'Availability: Party Room, Building B'`,
    "Party Room B selected"
  );
  shown = await startMs();
  durationShown = await durationValue();
  check(
    "switching residents resets the start and duration to their defaults",
    isDefaultStart(shown, Date.now()) && durationShown === "30",
    `${asInput(shown)}, ${durationShown} minutes`
  );

  // Clearing and restoring the date must keep the chosen time and load availability again.
  const tomorrow = asInput(Date.now() + DAY_MS).slice(0, 10);
  await evaluate(`__t.setStart('${tomorrow}T12:30')`);
  await waitFor(
    `__t.windows().length === 5 && __t.windows()[0].includes('${tomorrow} 12:30')`,
    "windows at 12:30 tomorrow"
  );
  await evaluate(`__t.setInput(document.querySelector('section input[name=startDate]'), '')`);
  await waitFor(`__t.windows().length === 0`, "no windows while the date is cleared");
  await evaluate(`__t.setInput(document.querySelector('section input[name=startDate]'), '${tomorrow}')`);
  check(
    "clearing and restoring the date keeps the time, and availability loads again",
    await waitFor(
      `__t.startValue() === '${tomorrow}T12:30' && __t.windows().length === 5 && __t.windows()[0].includes('${tomorrow} 12:30')`,
      "availability after the date is restored"
    )
  );

  console.log(`== Booking limit (a start at most ${MAX_DAYS_AHEAD} days ahead)`);
  const times = await evaluate(`[...document.querySelector('section select[name=startTime]').options].map((o) => o.value)`);
  check(
    "the time picker offers only the 48 half hours of the day",
    times.length === 48 && times.every((t) => /^\d{2}:(00|30)$/.test(t)),
    times.join(", ")
  );
  // The date picker's range comes from the last render, so allow a minute of drift at either end.
  const [minDate, maxDate] = await evaluate(
    `(() => { const d = document.querySelector('section input[name=startDate]'); return [d.min, d.max]; })()`
  );
  const dateOf = (ms) => new Date(ms).toISOString().slice(0, 10);
  const checkedAt = Date.now();
  check(
    "the date picker runs from today to the last date within 90 days",
    [dateOf(checkedAt), dateOf(checkedAt - 60_000)].includes(minDate) &&
      [dateOf(checkedAt + MAX_DAYS_AHEAD * DAY_MS), dateOf(checkedAt - 60_000 + MAX_DAYS_AHEAD * DAY_MS)].includes(maxDate),
    `${minDate} to ${maxDate}`
  );
  // A day past the picker's max, so it's past the API's limit however the clock moved since.
  const pastLimit = `${dateOf(Date.parse(`${maxDate}T00:00:00Z`) + DAY_MS)}T12:00`;
  await evaluate(`__t.setStart('${pastLimit}')`);
  check(
    "a start past the limit shows the API's message and no windows",
    await waitFor(`__t.messages().includes('90 days') && __t.windows().length === 0`, "booking-limit banner")
  );

  const unexpected = pageErrors.filter((e) => !/Failed to load resource|500|ERR_CONNECTION_REFUSED/.test(e));
  check("no uncaught exceptions or console errors in the page", unexpected.length === 0, unexpected.join(" // "));
} catch (err) {
  check("script ran to completion", false, err.message);
} finally {
  console.log(`\nRESULT: ${passed} passed, ${failed} failed`);
  browser.kill();
  await sleep(300);
  try {
    rmSync(profileDir, { recursive: true, force: true });
  } catch {}
  process.exit(failed === 0 ? 0 : 1);
}
