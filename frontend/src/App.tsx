import { useEffect, useMemo, useState } from 'react';
import {
  getAmenities,
  getAmenityReservations,
  createReservation,
  cancelReservation,
  type Amenity,
  type Reservation,
} from './api/generated/api';

const RESIDENT_ID_KEY = 'amenity-reservations:residentId';

function readStoredResidentId(): string {
  const stored = localStorage.getItem(RESIDENT_ID_KEY);
  if (stored) return stored;
  const generated = `resident-${Math.random().toString(36).slice(2, 8)}`;
  localStorage.setItem(RESIDENT_ID_KEY, generated);
  return generated;
}

function formatTime(iso: string): string {
  return new Date(iso).toLocaleString(undefined, {
    month: 'short',
    day: 'numeric',
    hour: 'numeric',
    minute: '2-digit',
  });
}

// Defaults a fresh form to "tomorrow, 10:00-11:00 local" so there's a plausible
// booking one click away instead of an empty datetime-local input.
function defaultSlot(): { start: string; end: string } {
  const start = new Date();
  start.setDate(start.getDate() + 1);
  start.setHours(10, 0, 0, 0);
  const end = new Date(start);
  end.setHours(11, 0, 0, 0);
  const toLocalInput = (d: Date) => {
    const pad = (n: number) => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
  };
  return { start: toLocalInput(start), end: toLocalInput(end) };
}

/**
 * Amenity Reservations — pick an amenity, see its bookings, create one, cancel your own.
 *
 * No auth in this slice: "resident identity" is a random id stored in localStorage so you
 * can test double-booking / ownership rules across two browser profiles. See the design doc.
 */
export function App() {
  const [residentId, setResidentId] = useState(readStoredResidentId);
  const [amenities, setAmenities] = useState<Amenity[]>([]);
  const [selectedAmenityId, setSelectedAmenityId] = useState<string | null>(null);
  const [reservations, setReservations] = useState<Reservation[]>([]);
  const [loadingAmenities, setLoadingAmenities] = useState(true);
  const [loadingReservations, setLoadingReservations] = useState(false);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [formError, setFormError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [slot, setSlot] = useState(defaultSlot);

  useEffect(() => {
    localStorage.setItem(RESIDENT_ID_KEY, residentId);
  }, [residentId]);

  useEffect(() => {
    getAmenities()
      .then((data) => {
        setAmenities(data);
        if (data.length > 0) setSelectedAmenityId(data[0].id);
      })
      .catch((e) => setLoadError(e instanceof Error ? e.message : 'Failed to load amenities'))
      .finally(() => setLoadingAmenities(false));
  }, []);

  const selectedAmenity = useMemo(
    () => amenities.find((a) => a.id === selectedAmenityId) ?? null,
    [amenities, selectedAmenityId],
  );

  function refreshReservations(amenityId: string) {
    setLoadingReservations(true);
    getAmenityReservations(amenityId)
      .then((data) => setReservations(data))
      .catch((e) => setLoadError(e instanceof Error ? e.message : 'Failed to load reservations'))
      .finally(() => setLoadingReservations(false));
  }

  useEffect(() => {
    if (selectedAmenityId) refreshReservations(selectedAmenityId);
  }, [selectedAmenityId]);

  async function handleCreate(e: React.FormEvent) {
    e.preventDefault();
    if (!selectedAmenityId) return;
    setFormError(null);
    setSubmitting(true);
    try {
      const startUtc = new Date(slot.start).toISOString();
      const endUtc = new Date(slot.end).toISOString();
      // The generated fetch client (orval's default template) parses the response body
      // without checking res.ok, so a 4xx/409 error body comes back typed as `Reservation`
      // even though at runtime it's really `{ error, message }`. Detect that shape rather
      // than editing the generated file.
      const result = await createReservation({ amenityId: selectedAmenityId, residentId, startUtc, endUtc });
      if (!result.id) {
        const err = result as unknown as { error?: string; message?: string };
        throw new Error(err.message ?? err.error ?? 'Failed to create reservation');
      }
      refreshReservations(selectedAmenityId);
    } catch (err) {
      setFormError(err instanceof Error ? err.message : 'Failed to create reservation');
    } finally {
      setSubmitting(false);
    }
  }

  async function handleCancel(reservation: Reservation) {
    if (!selectedAmenityId) return;
    try {
      await cancelReservation(reservation.id, { residentId });
      refreshReservations(selectedAmenityId);
    } catch (err) {
      setFormError(err instanceof Error ? err.message : 'Failed to cancel reservation');
    }
  }

  const activeReservations = reservations.filter((r) => r.status === 0);
  const cancelledReservations = reservations.filter((r) => r.status !== 0);

  return (
    <main style={{ fontFamily: 'system-ui, sans-serif', maxWidth: 720, margin: '2rem auto', padding: '0 1rem' }}>
      <h1>Amenity Reservations</h1>

      <div style={{ background: '#f4f4f5', borderRadius: 8, padding: '0.75rem 1rem', marginBottom: '1.5rem', fontSize: 14 }}>
        Booking as{' '}
        <input
          value={residentId}
          onChange={(e) => setResidentId(e.target.value)}
          style={{ fontFamily: 'monospace', padding: '2px 6px' }}
        />{' '}
        <span style={{ color: '#666' }}>
          (no auth in this slice — change this to simulate a different resident, e.g. in a second tab)
        </span>
      </div>

      {loadError && <p style={{ color: '#b00020' }}>Couldn't reach the API ({loadError}). Is the backend running on <code>http://localhost:5080</code>?</p>}
      {loadingAmenities && <p>Loading amenities…</p>}

      {amenities.length > 0 && (
        <>
          <label style={{ display: 'block', marginBottom: 4, fontWeight: 600 }}>Amenity</label>
          <select
            value={selectedAmenityId ?? ''}
            onChange={(e) => setSelectedAmenityId(e.target.value)}
            style={{ padding: '6px 8px', marginBottom: '1rem', width: '100%' }}
          >
            {amenities.map((a) => (
              <option key={a.id} value={a.id}>
                {a.name} — capacity {a.capacity}, max {a.maxBookingMinutes} min
              </option>
            ))}
          </select>

          {selectedAmenity && (
            <form onSubmit={handleCreate} style={{ border: '1px solid #ddd', borderRadius: 8, padding: '1rem', marginBottom: '1.5rem' }}>
              <h3 style={{ marginTop: 0 }}>Book {selectedAmenity.name}</h3>
              <div style={{ display: 'flex', gap: '1rem', marginBottom: '0.75rem', flexWrap: 'wrap' }}>
                <label>
                  Start
                  <br />
                  <input
                    type="datetime-local"
                    value={slot.start}
                    onChange={(e) => setSlot((s) => ({ ...s, start: e.target.value }))}
                    required
                  />
                </label>
                <label>
                  End
                  <br />
                  <input
                    type="datetime-local"
                    value={slot.end}
                    onChange={(e) => setSlot((s) => ({ ...s, end: e.target.value }))}
                    required
                  />
                </label>
              </div>
              <button type="submit" disabled={submitting}>
                {submitting ? 'Booking…' : 'Book slot'}
              </button>
              {formError && <p style={{ color: '#b00020', marginBottom: 0 }}>{formError}</p>}
            </form>
          )}

          <h3>Reservations</h3>
          {loadingReservations && <p>Loading…</p>}
          {!loadingReservations && activeReservations.length === 0 && <p style={{ color: '#666' }}>No active reservations for this amenity.</p>}
          <ul style={{ listStyle: 'none', padding: 0 }}>
            {activeReservations.map((r) => (
              <li
                key={r.id}
                style={{ border: '1px solid #ddd', borderRadius: 8, padding: '0.75rem 1rem', marginBottom: '0.5rem', display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}
              >
                <div>
                  <strong>{formatTime(r.startUtc)}</strong> – {formatTime(r.endUtc)}
                  <div style={{ fontSize: 13, color: '#777' }}>{r.residentId}</div>
                </div>
                {r.residentId === residentId && (
                  <button onClick={() => handleCancel(r)} style={{ color: '#b00020' }}>
                    Cancel
                  </button>
                )}
              </li>
            ))}
          </ul>

          {cancelledReservations.length > 0 && (
            <details style={{ marginTop: '1rem' }}>
              <summary style={{ cursor: 'pointer', color: '#666' }}>{cancelledReservations.length} cancelled</summary>
              <ul style={{ listStyle: 'none', padding: 0 }}>
                {cancelledReservations.map((r) => (
                  <li key={r.id} style={{ color: '#999', fontSize: 13, padding: '4px 0' }}>
                    <s>{formatTime(r.startUtc)} – {formatTime(r.endUtc)}</s> ({r.residentId})
                  </li>
                ))}
              </ul>
            </details>
          )}
        </>
      )}
    </main>
  );
}
