import { describe, expect, it } from 'vitest';
import type { BusyWindow } from '../api/generated/api';
import { buildDaySlots, durationOptions, formatDuration, peakOccupancy } from './schedule';

const window = (startUtc: string, endUtc: string): BusyWindow => ({ startUtc, endUtc });
const at = (time: string) => Date.parse(`2026-06-15T${time}:00Z`);

describe('peakOccupancy', () => {
  it('is zero when nothing overlaps', () => {
    const busy = [window('2026-06-15T08:00:00Z', '2026-06-15T09:00:00Z')];

    expect(peakOccupancy(busy, at('10:00'), at('10:30'))).toBe(0);
  });

  it('ignores bookings that merely touch the range', () => {
    // Half-open windows, matching the server: a booking ending at 10:00 leaves 10:00 free.
    const busy = [
      window('2026-06-15T09:00:00Z', '2026-06-15T10:00:00Z'),
      window('2026-06-15T10:30:00Z', '2026-06-15T11:00:00Z'),
    ];

    expect(peakOccupancy(busy, at('10:00'), at('10:30'))).toBe(0);
  });

  /**
   * The same trap the server-side capacity check has. Both bookings touch the slot, but they are
   * never held at the same time, so the slot is one deep — not two.
   */
  it('counts simultaneous bookings, not overlapping ones', () => {
    const busy = [
      window('2026-06-15T09:45:00Z', '2026-06-15T10:15:00Z'),
      window('2026-06-15T10:15:00Z', '2026-06-15T10:45:00Z'),
    ];

    expect(peakOccupancy(busy, at('10:00'), at('10:30'))).toBe(1);
  });

  it('counts bookings that genuinely coincide', () => {
    const busy = [
      window('2026-06-15T09:45:00Z', '2026-06-15T10:30:00Z'),
      window('2026-06-15T10:00:00Z', '2026-06-15T11:00:00Z'),
    ];

    expect(peakOccupancy(busy, at('10:00'), at('10:30'))).toBe(2);
  });
});

describe('buildDaySlots', () => {
  const dayStart = new Date('2026-06-15T04:00:00Z');
  const dayEnd = new Date('2026-06-16T04:00:00Z');

  it('covers the whole local day in half-hour steps', () => {
    const slots = buildDaySlots([], 1, dayStart, dayEnd);

    expect(slots).toHaveLength(48);
    expect(slots[0].startUtc).toBe('2026-06-15T04:00:00.000Z');
    expect(slots[slots.length - 1].endUtc).toBe('2026-06-16T04:00:00.000Z');
  });

  it('produces one fewer slot on the day the clocks go forward', () => {
    const slots = buildDaySlots([], 1, new Date('2026-03-08T05:00:00Z'), new Date('2026-03-09T04:00:00Z'));

    expect(slots).toHaveLength(46);
  });

  it('marks a slot taken only when the amenity is at capacity', () => {
    const busy = [window('2026-06-15T20:00:00Z', '2026-06-15T22:00:00Z')];

    const exclusive = buildDaySlots(busy, 1, dayStart, dayEnd);
    const shared = buildDaySlots(busy, 2, dayStart, dayEnd);
    const slotAt2030 = (slots: ReturnType<typeof buildDaySlots>) =>
      slots.find((s) => s.startUtc === '2026-06-15T20:30:00.000Z')!;

    expect(slotAt2030(exclusive).isFree).toBe(false);
    expect(slotAt2030(shared).isFree).toBe(true);
    expect(slotAt2030(shared).held).toBe(1);
  });
});

describe('durationOptions', () => {
  it('never offers longer than the amenity allows', () => {
    expect(durationOptions(90)).toEqual([30, 60, 90]);
    expect(durationOptions(240)).toEqual([30, 60, 90, 120, 180, 240]);
  });

  it('includes an unusual maximum that is not one of the round numbers', () => {
    expect(durationOptions(45)).toEqual([30, 45]);
  });
});

describe('formatDuration', () => {
  it('reads the way a resident would say it', () => {
    expect(formatDuration(30)).toBe('30 min');
    expect(formatDuration(120)).toBe('2 hr');
    expect(formatDuration(90)).toBe('1 hr 30 min');
  });
});
