import type { BusyWindow } from '../api/generated/api';
import { ApiError } from '../api/http';

/**
 * Turns a thrown error into something worth showing a resident.
 *
 * The branch is on the problem `code` the API sends, not on its prose, so rewording a message
 * server-side never breaks the UI. A double-booking is called out separately from an ordinary
 * validation failure because it is the one failure a resident can act on immediately — by
 * picking a different time.
 */

export type FailureKind = 'conflict' | 'validation' | 'signed-out' | 'missing' | 'unknown';

export interface Failure {
  kind: FailureKind;
  message: string;
  /** Occupied windows to suggest around. Only ever populated for a conflict. */
  conflicts: BusyWindow[];
}

export function describeFailure(error: unknown): Failure {
  if (!(error instanceof ApiError)) {
    return {
      kind: 'unknown',
      message:
        error instanceof Error
          ? `Couldn't reach the API (${error.message}). Is the backend running on http://localhost:5080?`
          : 'Something went wrong.',
      conflicts: [],
    };
  }

  const message = error.message;

  switch (error.code) {
    case 'slot_unavailable':
      return { kind: 'conflict', message, conflicts: error.conflicts };
    case 'resident_required':
      return { kind: 'signed-out', message, conflicts: [] };
    case 'amenity_not_found':
    case 'reservation_not_found':
      return { kind: 'missing', message, conflicts: [] };
    case 'invalid_interval':
    case 'exceeds_max_booking_length':
    case 'starts_in_past':
    case 'beyond_booking_horizon':
      return { kind: 'validation', message, conflicts: [] };
    default:
      return { kind: 'unknown', message, conflicts: [] };
  }
}
