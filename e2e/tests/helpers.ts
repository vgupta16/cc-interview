import { test as base, expect, type APIRequestContext, type Locator, type Page } from '@playwright/test';

// ---------------------------------------------------------------------------------------------
// Seed data (backend/Api/Store/SeedData.cs)
// ---------------------------------------------------------------------------------------------

export const BUILDING_A = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
export const BUILDING_B = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';

export const AMENITY = {
  partyRoomA: '11111111-1111-1111-1111-111111111111', // capacity 1, max 240 min
  gymA: '22222222-2222-2222-2222-222222222222', // capacity 2, max 90 min
  guestParkingA: '33333333-3333-3333-3333-333333333333', // capacity 1, max 1440 min
  partyRoomB: '44444444-4444-4444-4444-444444444444',
  gymB: '55555555-5555-5555-5555-555555555555',
  guestParkingB: '66666666-6666-6666-6666-666666666666',
} as const;

const AMENITIES_BY_BUILDING: Record<string, string[]> = {
  [BUILDING_A]: [AMENITY.partyRoomA, AMENITY.gymA, AMENITY.guestParkingA],
  [BUILDING_B]: [AMENITY.partyRoomB, AMENITY.gymB, AMENITY.guestParkingB],
};

export type ResidentId = 'alice' | 'bob' | 'carol';

// ---------------------------------------------------------------------------------------------
// Headers and time formatting
// ---------------------------------------------------------------------------------------------

export function headers(buildingId: string, residentId?: string, staff = false): Record<string, string> {
  const h: Record<string, string> = { 'X-Building-Id': buildingId };
  if (residentId) h['X-Resident-Id'] = residentId;
  if (staff) h['X-Resident-Roles'] = 'staff';
  return h;
}

const pad = (n: number) => String(n).padStart(2, '0');

/**
 * Value for an `<input type="datetime-local">`. The Playwright config pins the browser time zone
 * to UTC, so the wall-clock string typed in the form equals the UTC instant sent to the API.
 */
export function toDatetimeLocal(d: Date): string {
  return (
    `${d.getUTCFullYear()}-${pad(d.getUTCMonth() + 1)}-${pad(d.getUTCDate())}` +
    `T${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())}`
  );
}

export const toIso = (d: Date) => d.toISOString();

// ---------------------------------------------------------------------------------------------
// API helpers (setup, lookups, cleanup). Go through the Vite proxy, like the UI does.
// ---------------------------------------------------------------------------------------------

export interface ApiReservation {
  id: string;
  amenityId: string;
  residentId: string;
  startUtc: string;
  endUtc: string;
  status: string;
}

export async function apiList(
  request: APIRequestContext,
  buildingId: string,
  amenityId: string,
): Promise<ApiReservation[]> {
  const res = await request.get(`/api/amenities/${amenityId}/reservations`, { headers: headers(buildingId) });
  expect(res.ok(), `list reservations for ${amenityId}: ${res.status()}`).toBeTruthy();
  return (await res.json()) as ApiReservation[];
}

/** Book directly through the API (test setup). Returns the raw response so callers can assert on it. */
export async function apiBook(
  request: APIRequestContext,
  o: { building: string; resident: ResidentId; amenityId: string; start: Date; minutes: number },
) {
  return request.post('/api/reservations', {
    headers: headers(o.building, o.resident),
    data: {
      amenityId: o.amenityId,
      startUtc: toIso(o.start),
      endUtc: toIso(new Date(o.start.getTime() + o.minutes * 60_000)),
    },
  });
}

export async function apiCancel(request: APIRequestContext, buildingId: string, residentId: string, id: string) {
  return request.delete(`/api/reservations/${id}`, { headers: headers(buildingId, residentId) });
}

/** Poll the API until the resident's reservation starting at `start` exists; returns it. */
export async function findReservation(
  request: APIRequestContext,
  o: { building: string; amenityId: string; resident: ResidentId; start: Date },
): Promise<ApiReservation> {
  let found: ApiReservation | undefined;
  await expect
    .poll(async () => {
      const all = await apiList(request, o.building, o.amenityId);
      found = all.find((r) => r.residentId === o.resident && Date.parse(r.startUtc) === o.start.getTime());
      return found?.id;
    }, { message: `reservation of ${o.resident} at ${toIso(o.start)} should exist` })
    .toBeTruthy();
  return found!;
}

// ---------------------------------------------------------------------------------------------
// Per-test scenario fixture: unique far-future day + automatic cleanup
// ---------------------------------------------------------------------------------------------

/**
 * Test titles start with `S<n>` (e.g. "S3 gym ..."). Scenario n gets its own day, 40 + n days
 * from today, so tests never collide with each other or with earlier runs against a long-lived
 * backend. After each test, every reservation that starts on that day is cancelled via the API.
 */
export class Scenario {
  constructor(
    private readonly request: APIRequestContext,
    /** 00:00 UTC of this scenario's day. */
    readonly day: Date,
  ) {}

  /** A UTC instant on this scenario's day. */
  at(hour: number, minute = 0): Date {
    return new Date(this.day.getTime() + (hour * 60 + minute) * 60_000);
  }

  /** The same day, shifted, for "in the past" style checks. */
  static yesterdayAt(hour: number): Date {
    const d = new Date();
    d.setUTCDate(d.getUTCDate() - 1);
    d.setUTCHours(hour, 0, 0, 0);
    return d;
  }

  /** Cancel everything that starts on this scenario's day, in every seeded building. */
  async cleanup(): Promise<void> {
    const from = this.day.getTime();
    const to = from + 24 * 3_600_000;
    for (const [building, amenityIds] of Object.entries(AMENITIES_BY_BUILDING)) {
      for (const amenityId of amenityIds) {
        const list = await this.request.get(`/api/amenities/${amenityId}/reservations`, {
          headers: headers(building),
        });
        if (!list.ok()) continue;
        for (const r of (await list.json()) as ApiReservation[]) {
          const start = Date.parse(r.startUtc);
          if (start >= from && start < to) await apiCancel(this.request, building, r.residentId, r.id);
        }
      }
    }
  }
}

export const test = base.extend<{ scenario: Scenario }>({
  scenario: async ({ request }, use, testInfo) => {
    const n = Number(/^S(\d+)\b/.exec(testInfo.title)?.[1]);
    if (!Number.isInteger(n)) throw new Error(`Test title must start with "S<n>": ${testInfo.title}`);
    const day = new Date();
    day.setUTCDate(day.getUTCDate() + 40 + n);
    day.setUTCHours(0, 0, 0, 0);

    const scenario = new Scenario(request, day);
    await scenario.cleanup(); // leftovers from an aborted earlier run
    try {
      await use(scenario);
    } finally {
      await scenario.cleanup();
    }
  },
});

export { expect };

// ---------------------------------------------------------------------------------------------
// UI helpers (selectors come from the UI test contract in docs/design.md)
// ---------------------------------------------------------------------------------------------

export const ui = {
  buildingSwitcher: (page: Page) => page.getByTestId('building-switcher'),
  residentSwitcher: (page: Page) => page.getByTestId('resident-switcher'),
  amenityCard: (page: Page, amenityId: string) => page.getByTestId(`amenity-card-${amenityId}`),
  allAmenityCards: (page: Page) => page.locator('[data-testid^="amenity-card-"]'),
  bookError: (page: Page) => page.getByTestId('booking-error'),
  row: (page: Page, reservationId: string) => page.getByTestId(`reservation-row-${reservationId}`),
  allRows: (page: Page) => page.locator('[data-testid^="reservation-row-"]'),
  rowsOf: (page: Page, resident: ResidentId) =>
    page.locator('[data-testid^="reservation-row-"]').filter({ hasText: resident }),
  cancelButton: (page: Page, reservationId: string) => page.getByTestId(`cancel-${reservationId}`),
};

/** Load the app; defaults are Building A and alice. */
export async function gotoApp(page: Page): Promise<void> {
  await page.goto('/');
  await expect(ui.buildingSwitcher(page)).toBeVisible();
  await expect(ui.residentSwitcher(page)).toBeVisible();
}

export async function selectBuilding(page: Page, buildingId: string): Promise<void> {
  await ui.buildingSwitcher(page).selectOption(buildingId);
  await expect(ui.buildingSwitcher(page)).toHaveValue(buildingId);
}

export async function selectResident(page: Page, resident: ResidentId): Promise<void> {
  await ui.residentSwitcher(page).selectOption(resident);
  await expect(ui.residentSwitcher(page)).toHaveValue(resident);
}

/**
 * Click the amenity card and wait for the booking form. If `waitForList`, also wait for the
 * reservations request to finish, so a following "no rows" assertion isn't vacuous.
 */
export async function openAmenity(page: Page, amenityId: string, opts: { waitForList?: boolean } = {}): Promise<void> {
  const card = ui.amenityCard(page, amenityId);
  await expect(card).toBeVisible();
  if (opts.waitForList) {
    await Promise.all([
      page.waitForResponse((r) => r.url().includes(`/api/amenities/${amenityId}/reservations`) && r.ok()),
      card.click(),
    ]);
  } else {
    await card.click();
  }
  await expect(page.getByTestId('book-submit')).toBeVisible();
}

/**
 * After switching resident the selected amenity may or may not stay selected (the contract only
 * says a building switch clears it). Re-open it only when the booking form went away.
 */
export async function ensureAmenityOpen(page: Page, amenityId: string): Promise<void> {
  if (!(await page.getByTestId('book-submit').isVisible())) await openAmenity(page, amenityId);
}

/** Fill and submit the booking form (amenity must already be open). Does not assert the outcome. */
export async function submitBooking(page: Page, o: { start: Date; minutes: number }): Promise<void> {
  await page.getByTestId('book-start').fill(toDatetimeLocal(o.start));
  await page.getByTestId('book-duration').fill(String(o.minutes));
  await page.getByTestId('book-submit').click();
}

/** Submit and assert that no booking-error appears and a row for the resident shows up. */
export async function bookViaForm(
  page: Page,
  o: { start: Date; minutes: number; resident: ResidentId },
): Promise<void> {
  const before = await ui.rowsOf(page, o.resident).count();
  await submitBooking(page, o);
  await expect(ui.rowsOf(page, o.resident)).toHaveCount(before + 1);
  await expect(ui.bookError(page)).toHaveCount(0);
}

/** Assert a rejected booking: error visible with the given stable code. */
export async function expectBookingError(page: Page, code: string): Promise<Locator> {
  const err = ui.bookError(page);
  await expect(err).toBeVisible();
  await expect(err).toHaveAttribute('data-code', code);
  return err;
}
