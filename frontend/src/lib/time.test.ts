import { describe, expect, it } from 'vitest';
import { addDays, formatTime, localDayBounds, zonedToUtc } from './time';

const NY = 'America/New_York';
const CHICAGO = 'America/Chicago';

describe('zonedToUtc', () => {
  it('resolves a wall-clock time to the instant that zone means by it', () => {
    // Eastern Daylight Time is UTC-4 in June.
    expect(zonedToUtc('2026-06-15', '16:00', NY).toISOString()).toBe('2026-06-15T20:00:00.000Z');
    // Central is an hour behind Eastern, which is the whole point of tracking it per building.
    expect(zonedToUtc('2026-06-15', '16:00', CHICAGO).toISOString()).toBe('2026-06-15T21:00:00.000Z');
  });

  it('follows the zone across a standard-time boundary', () => {
    // Eastern Standard Time is UTC-5 in January.
    expect(zonedToUtc('2026-01-15', '16:00', NY).toISOString()).toBe('2026-01-15T21:00:00.000Z');
  });

  it('resolves times on both sides of a spring-forward transition', () => {
    // US DST begins 2026-03-08 at 02:00 local. 01:30 is still EST (-5), 03:30 is EDT (-4).
    expect(zonedToUtc('2026-03-08', '01:30', NY).toISOString()).toBe('2026-03-08T06:30:00.000Z');
    expect(zonedToUtc('2026-03-08', '03:30', NY).toISOString()).toBe('2026-03-08T07:30:00.000Z');
  });
});

describe('localDayBounds', () => {
  it('spans one building-local day rather than one UTC day', () => {
    const { from, to } = localDayBounds('2026-06-15', NY);

    expect(from.toISOString()).toBe('2026-06-15T04:00:00.000Z');
    expect(to.toISOString()).toBe('2026-06-16T04:00:00.000Z');
  });

  it('is 23 hours long on the day the clocks go forward', () => {
    const { from, to } = localDayBounds('2026-03-08', NY);

    // Deriving the end from the next local midnight rather than adding 24 hours is what keeps
    // the grid aligned to the day residents actually experience.
    expect((to.getTime() - from.getTime()) / 3_600_000).toBe(23);
  });

  it('is 25 hours long on the day the clocks go back', () => {
    const { from, to } = localDayBounds('2026-11-01', NY);

    expect((to.getTime() - from.getTime()) / 3_600_000).toBe(25);
  });
});

describe('addDays', () => {
  it('rolls over month and year boundaries', () => {
    expect(addDays('2026-01-31', 1)).toBe('2026-02-01');
    expect(addDays('2026-12-31', 1)).toBe('2027-01-01');
    expect(addDays('2026-03-01', -1)).toBe('2026-02-28');
  });
});

describe('formatTime', () => {
  it('renders the same instant differently for each building', () => {
    const instant = '2026-06-15T20:00:00Z';

    expect(formatTime(instant, NY)).toContain('4:00');
    expect(formatTime(instant, CHICAGO)).toContain('3:00');
  });
});
