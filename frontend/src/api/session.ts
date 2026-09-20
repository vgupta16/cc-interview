// The building and resident are sent as headers on every call. This stands in for real
// authentication: the backend resolves tenant and identity in one place, so swapping this
// helper for a token is the only frontend change needed.

export const BUILDINGS = [
  { id: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', name: 'Maple Court' },
  { id: 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', name: 'Harbour Tower' },
] as const;

export const RESIDENTS = ['alice', 'bob', 'carol'] as const;

export interface Session {
  buildingId: string;
  residentId: string;
}

export function apiOptions(session: Session): RequestInit {
  return {
    headers: {
      'X-Building-Id': session.buildingId,
      'X-Resident-Id': session.residentId,
    },
  };
}
