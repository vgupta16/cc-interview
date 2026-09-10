import { describe, expect, it } from 'vitest';
import { ApiError } from '../api/http';
import { describeFailure } from './errors';

describe('describeFailure', () => {
  it('singles out a double-booking so the UI can offer another time', () => {
    const conflicts = [{ startUtc: '2026-06-15T20:00:00Z', endUtc: '2026-06-15T22:00:00Z' }];
    const error = new ApiError(409, {
      title: 'Slot unavailable',
      detail: 'Party Room (Main) is already booked during that time.',
      code: 'slot_unavailable',
      conflicts,
    });

    const failure = describeFailure(error);

    expect(failure.kind).toBe('conflict');
    expect(failure.message).toBe('Party Room (Main) is already booked during that time.');
    expect(failure.conflicts).toEqual(conflicts);
  });

  it('branches on the code rather than the wording', () => {
    // Same status, different rule. Rewording the detail server-side must not change the branch.
    const tooLong = new ApiError(400, { detail: 'anything at all', code: 'exceeds_max_booking_length' });
    const inPast = new ApiError(400, { detail: 'anything at all', code: 'starts_in_past' });

    expect(describeFailure(tooLong).kind).toBe('validation');
    expect(describeFailure(inPast).kind).toBe('validation');
  });

  it('distinguishes a missing resource from a missing session', () => {
    expect(describeFailure(new ApiError(404, { code: 'amenity_not_found' })).kind).toBe('missing');
    expect(describeFailure(new ApiError(401, { code: 'resident_required' })).kind).toBe('signed-out');
  });

  it('never reports conflicts for anything but a conflict', () => {
    const failure = describeFailure(new ApiError(400, { code: 'invalid_interval' }));

    expect(failure.conflicts).toEqual([]);
  });

  it('explains a transport failure instead of leaking a raw fetch error', () => {
    const failure = describeFailure(new TypeError('Failed to fetch'));

    expect(failure.kind).toBe('unknown');
    expect(failure.message).toContain('localhost:5080');
  });

  it('falls back gracefully on a code it has never seen', () => {
    const failure = describeFailure(new ApiError(418, { detail: 'Teapot', code: 'something_new' }));

    expect(failure.kind).toBe('unknown');
    expect(failure.message).toBe('Teapot');
  });
});
