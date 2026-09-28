// all times are entered and shown in UTC (DESIGN.md, A14).

const SLOT_MS = 30 * 60 * 1000;
const DAY_MS = 24 * 60 * 60 * 1000;
const MIN_LEAD_TIME_MS = 5 * 60 * 1000;
const DATE_FORMAT = /^\d{4}-\d{2}-\d{2}$/;

/**
 * How far ahead a reservation can start. The API enforces the same limit (DESIGN.md, A15).
 */
export const MAX_DAYS_AHEAD = 90;

/**
 * Every start time a resident can pick: each half hour of the day, "00:00" through "23:30" (A8).
 */
export const HALF_HOUR_TIMES = Array.from(
  { length: 48 },
  (_, i) => `${String(Math.floor(i / 2)).padStart(2, "0")}:${i % 2 === 0 ? "00" : "30"}`
);

/**
 * The start a resident picks, in UTC: a "YYYY-MM-DD" date and a half-hour "HH:mm" time. They're kept
 * apart, so clearing the date never disturbs the time, and are combined only for the API.
 */
export type StartChoice = { date: string; time: string };

/**
 * A default start for the form: the next 30-minute boundary at least 5 minutes from now, in UTC.
 * One clock reading gives both parts, so the date and time always belong together.
 */
export function defaultStart(): StartChoice {
  const start = new Date(Math.ceil((Date.now() + MIN_LEAD_TIME_MS) / SLOT_MS) * SLOT_MS).toISOString();

  return { date: start.slice(0, 10), time: start.slice(11, 16) };
}

/**
 * The last date a start can have ("YYYY-MM-DD"): the date of the last 30-minute boundary within 90
 * days of now. It's the start date picker's max.
 */
export function latestStartDate(): string {
  const latest = Math.floor((Date.now() + MAX_DAYS_AHEAD * DAY_MS) / SLOT_MS) * SLOT_MS;

  return new Date(latest).toISOString().slice(0, 10);
}

/**
 * Today's date in UTC ("YYYY-MM-DD"), the start date picker's min.
 */
export function todayUtcDate(): string {
  return new Date().toISOString().slice(0, 10);
}

/**
 * Combines a start into the UTC timestamp the API expects. The API rejects a timestamp without an
 * offset, so the Z is added explicitly. Returns null while the date is empty or partial.
 */
export function startUtcIso(start: StartChoice): string | null {
  return DATE_FORMAT.test(start.date) && HALF_HOUR_TIMES.includes(start.time)
    ? `${start.date}T${start.time}:00Z`
    : null;
}

/**
 * "2026-09-27 10:00–11:30 UTC", or "2026-09-27 23:00 – 2026-09-28 01:00 UTC" when the window
 * crosses midnight (A16).
 */
export function formatRange(startIso: string, endIso: string): string {
  const [startDate, startTime] = utcParts(startIso);
  const [endDate, endTime] = utcParts(endIso);

  return startDate === endDate
    ? `${startDate} ${startTime}–${endTime} UTC`
    : `${startDate} ${startTime} – ${endDate} ${endTime} UTC`;
}

/**
 * "30 min", "1 h", "1 h 30 min".
 */
export function formatDuration(minutes: number): string {
  const hours = Math.floor(minutes / 60);
  const rest = minutes % 60;

  if (hours === 0) {
    return `${rest} min`;
  }

  return rest === 0 ? `${hours} h` : `${hours} h ${rest} min`;
}

function utcParts(iso: string): [string, string] {
  const utc = new Date(iso).toISOString();

  return [utc.slice(0, 10), utc.slice(11, 16)];
}
