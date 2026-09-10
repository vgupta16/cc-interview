import type { BusyWindow, ProblemDetails } from './generated/api';

/**
 * The fetch layer every generated client function goes through (wired up as orval's `mutator`).
 *
 * Two things the stock generated client gets wrong for this API:
 *
 *  1. It parses any response body into the success type, so a 409 conflict would come back
 *     looking like a reservation. Surfacing double-booking clearly is the whole point of this
 *     feature, so failures have to be distinguishable.
 *  2. It drops header parameters, even though `X-Resident-Id` is documented in the OpenAPI spec.
 */

/** Stands in for an auth token. See `setCurrentResident`. */
const RESIDENT_HEADER = 'X-Resident-Id';

let currentResidentId: string | null = null;

/**
 * Sets who subsequent requests are made as.
 *
 * Session identity is ambient by nature — this is the same place a real app would attach a
 * bearer token — so it lives here rather than being threaded through every call site. Swapping
 * the header for real auth is a change to this module alone.
 */
export function setCurrentResident(residentId: string | null): void {
  currentResidentId = residentId;
}

/** RFC 9457 problem response, plus the extensions this API adds. */
export interface ApiProblem extends ProblemDetails {
  /** Stable machine-readable reason, e.g. `slot_unavailable`. Branch on this, not on prose. */
  code?: string;
  /** Occupied windows a rejected booking ran into. Present only on `slot_unavailable`. */
  conflicts?: BusyWindow[];
}

export class ApiError extends Error {
  readonly status: number;
  readonly problem: ApiProblem;

  constructor(status: number, problem: ApiProblem) {
    super(problem.detail ?? problem.title ?? `Request failed (${status})`);
    this.name = 'ApiError';
    this.status = status;
    this.problem = problem;
  }

  get code(): string | undefined {
    return this.problem.code;
  }

  get conflicts(): BusyWindow[] {
    return this.problem.conflicts ?? [];
  }
}

export const apiFetch = async <T>(url: string, options: RequestInit = {}): Promise<T> => {
  const headers = new Headers(options.headers);
  if (currentResidentId) {
    headers.set(RESIDENT_HEADER, currentResidentId);
  }

  const response = await fetch(url, { ...options, headers });

  const raw = [204, 205, 304].includes(response.status) ? '' : await response.text();
  const body: unknown = raw ? JSON.parse(raw) : undefined;

  if (!response.ok) {
    throw new ApiError(response.status, (body ?? {}) as ApiProblem);
  }

  return body as T;
};
