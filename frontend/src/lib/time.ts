/**
 * Time conversion between UTC instants (what the API stores) and the building's local time
 * (what residents think in). Done with `Intl` rather than a date library so the exercise stays
 * dependency-free; the functions are pure, so they are cheap to test.
 */

/** How far ahead of UTC `timeZone` is at a given instant, in milliseconds. */
function zoneOffsetMs(instant: Date, timeZone: string): number {
  const parts = new Intl.DateTimeFormat('en-US', {
    timeZone,
    hour12: false,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  }).formatToParts(instant);

  const part = (type: Intl.DateTimeFormatPartTypes) =>
    Number(parts.find((p) => p.type === type)!.value);

  // Reinterpret the zone's wall-clock reading as if it were UTC; the gap is the offset.
  // `% 24` because some engines render midnight as hour 24.
  const asIfUtc = Date.UTC(
    part('year'),
    part('month') - 1,
    part('day'),
    part('hour') % 24,
    part('minute'),
    part('second'),
  );

  return asIfUtc - instant.getTime();
}

/**
 * Converts a wall-clock time in a building's zone to the UTC instant it names.
 *
 * @param dateIso `YYYY-MM-DD` as the building sees it
 * @param time `HH:mm` as the building sees it
 */
export function zonedToUtc(dateIso: string, time: string, timeZone: string): Date {
  const [year, month, day] = dateIso.split('-').map(Number);
  const [hour, minute] = time.split(':').map(Number);
  const naive = Date.UTC(year, month - 1, day, hour, minute);

  // The offset depends on the instant, and the instant is what we're solving for. Start from
  // the offset at the naive guess, then re-evaluate at the corrected instant — the second pass
  // is what gets the hours either side of a DST transition right.
  const firstPass = naive - zoneOffsetMs(new Date(naive), timeZone);
  return new Date(naive - zoneOffsetMs(new Date(firstPass), timeZone));
}

/** `YYYY-MM-DD` for the current date in a building's zone. */
export function todayInZone(timeZone: string): string {
  return new Intl.DateTimeFormat('en-CA', {
    timeZone,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
  }).format(new Date());
}

/** Shifts a `YYYY-MM-DD` string by whole days, without tripping over DST. */
export function addDays(dateIso: string, days: number): string {
  const [year, month, day] = dateIso.split('-').map(Number);
  return new Date(Date.UTC(year, month - 1, day + days)).toISOString().slice(0, 10);
}

/**
 * The UTC instants bounding one building-local calendar day. Derived from the next day's
 * midnight rather than by adding 24 hours, so days that gain or lose an hour still line up.
 */
export function localDayBounds(dateIso: string, timeZone: string): { from: Date; to: Date } {
  return {
    from: zonedToUtc(dateIso, '00:00', timeZone),
    to: zonedToUtc(addDays(dateIso, 1), '00:00', timeZone),
  };
}

export function formatTime(instant: string | Date, timeZone: string): string {
  return new Intl.DateTimeFormat(undefined, {
    timeZone,
    hour: 'numeric',
    minute: '2-digit',
  }).format(new Date(instant));
}

export function formatDate(instant: string | Date, timeZone: string): string {
  return new Intl.DateTimeFormat(undefined, {
    timeZone,
    weekday: 'short',
    month: 'short',
    day: 'numeric',
  }).format(new Date(instant));
}

export function formatWindow(startUtc: string, endUtc: string, timeZone: string): string {
  return `${formatTime(startUtc, timeZone)} – ${formatTime(endUtc, timeZone)}`;
}
