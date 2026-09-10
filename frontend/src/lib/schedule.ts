import type { BusyWindow } from '../api/generated/api';

/**
 * Turns the API's busy windows into a grid the resident can read at a glance.
 *
 * The grid is a *display aid*, not an authority. The server re-checks capacity inside a lock
 * when the booking is submitted, so a slot shown as free can still come back 409 if someone
 * booked it a moment earlier. That is expected, and the UI surfaces the conflict rather than
 * pretending the grid was a reservation.
 *
 * Thirty-minute slots are a presentation choice, not an API constraint: the endpoint accepts
 * arbitrary start times and durations. Whether bookings should be forced onto a grid is an open
 * product question (see DESIGN.md §2).
 */

export const SLOT_MINUTES = 30;

export interface DaySlot {
  startUtc: string;
  endUtc: string;
  /** Peak number of bookings held simultaneously at some instant inside this slot. */
  held: number;
  isFree: boolean;
}

interface Interval {
  start: number;
  end: number;
}

const toIntervals = (windows: BusyWindow[]): Interval[] =>
  windows.map((w) => ({ start: Date.parse(w.startUtc), end: Date.parse(w.endUtc) }));

/**
 * The most bookings held at any single instant inside `[start, end)`.
 *
 * Mirrors the server's capacity check, and for the same reason: counting the windows that
 * overlap the range overstates occupancy. Two bookings of 9:45–10:15 and 10:15–10:45 both touch
 * a 10:00–10:30 slot but are never held at the same time, so the peak is 1, not 2.
 */
export function peakOccupancy(windows: BusyWindow[], start: number, end: number): number {
  const overlapping = toIntervals(windows).filter((w) => w.start < end && start < w.end);
  if (overlapping.length === 0) return 0;

  // Occupancy only rises at the start of a booking, so sampling the range's own start plus each
  // booking's start inside it finds the maximum exactly.
  const instants = new Set([start, ...overlapping.map((w) => w.start)].filter((t) => t >= start && t < end));

  let peak = 0;
  for (const instant of instants) {
    const held = overlapping.filter((w) => w.start <= instant && instant < w.end).length;
    if (held > peak) peak = held;
  }

  return peak;
}

export function buildDaySlots(
  busy: BusyWindow[],
  capacity: number,
  dayStart: Date,
  dayEnd: Date,
  slotMinutes: number = SLOT_MINUTES,
): DaySlot[] {
  const step = slotMinutes * 60_000;
  const slots: DaySlot[] = [];

  for (let start = dayStart.getTime(); start < dayEnd.getTime(); start += step) {
    const end = Math.min(start + step, dayEnd.getTime());
    const held = peakOccupancy(busy, start, end);

    slots.push({
      startUtc: new Date(start).toISOString(),
      endUtc: new Date(end).toISOString(),
      held,
      isFree: held < capacity,
    });
  }

  return slots;
}

/** Duration choices offered for an amenity, capped by its own rule. */
export function durationOptions(maxBookingMinutes: number): number[] {
  const candidates = [30, 60, 90, 120, 180, 240, 480, 720, 1440];
  const allowed = candidates.filter((minutes) => minutes <= maxBookingMinutes);

  // Always offer the amenity's own maximum, even when it isn't one of the round numbers.
  if (!allowed.includes(maxBookingMinutes)) allowed.push(maxBookingMinutes);

  return allowed.sort((a, b) => a - b);
}

export function formatDuration(minutes: number): string {
  if (minutes < 60) return `${minutes} min`;
  if (minutes % 60 === 0) return `${minutes / 60} hr`;
  return `${Math.floor(minutes / 60)} hr ${minutes % 60} min`;
}
