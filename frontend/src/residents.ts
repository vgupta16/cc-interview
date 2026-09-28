/**
 * Sample identities for the resident picker. Registered IDs match InMemoryStore's seed data;
 * the unknown choice deliberately has no backend resident so the UI can demonstrate a 401.
 * Labels exist only here. Building C has a resident but no amenities.
 */
export const SAMPLE_RESIDENTS = [
  {
    id: "a1a1a1a1-a1a1-a1a1-a1a1-a1a1a1a1a1a1",
    label: "Resident A1 (Building A)"
  },
  {
    id: "a2a2a2a2-a2a2-a2a2-a2a2-a2a2a2a2a2a2",
    label: "Resident A2 (Building A)"
  },
  {
    id: "b1b1b1b1-b1b1-b1b1-b1b1-b1b1b1b1b1b1",
    label: "Resident B1 (Building B)"
  },
  {
    id: "b2b2b2b2-b2b2-b2b2-b2b2-b2b2b2b2b2b2",
    label: "Resident B2 (Building B)"
  },
  {
    id: "c1c1c1c1-c1c1-c1c1-c1c1-c1c1c1c1c1c1",
    label: "Resident C1 (Building C — no amenities)"
  },
  {
    id: "d1d1d1d1-d1d1-d1d1-d1d1-d1d1d1d1d1d1",
    label: "Unknown resident (not registered)"
  }
];

/**
 * Request options that identify the resident. The generated client spreads `options.headers`
 * into its own headers, so this must be a plain object. A `Headers` instance would be dropped.
 */
export function asResident(residentId: string): RequestInit {
  return {
    headers: { ["X-RESIDENT-ID"]: residentId }
  };
}
