// Per-browser convenience: remember what the viewer had open so an accidental refresh doesn't
// drop them back to the defaults. Reads and writes are wrapped because storage can be blocked
// (private windows, disabled site data); the app works the same without it.

const KEY = 'amenity-reservations:view:v1';

export interface PersistedView {
  buildingId?: string;
  residentId?: string;
  selectedAmenityId?: string | null;
}

export function loadView(): PersistedView {
  try {
    const parsed: unknown = JSON.parse(localStorage.getItem(KEY) ?? '{}');
    return parsed && typeof parsed === 'object' ? (parsed as PersistedView) : {};
  } catch {
    return {};
  }
}

export function saveView(view: PersistedView): void {
  try {
    localStorage.setItem(KEY, JSON.stringify(view));
  } catch {
    /* storage unavailable: nothing to do */
  }
}
