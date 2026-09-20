import { useEffect, useState } from 'react';
import {
  cancelReservation,
  getAmenities,
  getAmenityReservations,
  type AmenityResponse,
  type ReservationResponse,
} from './api/generated/api';
import { networkError, toDisplayError } from './api/errors';
import { apiOptions, BUILDINGS, RESIDENTS, type Session } from './api/session';
import { AmenityList } from './components/AmenityList';
import { BookingForm } from './components/BookingForm';
import { ReservationList } from './components/ReservationList';

export function App() {
  const [session, setSession] = useState<Session>({ buildingId: BUILDINGS[0].id, residentId: RESIDENTS[0] });
  const [amenities, setAmenities] = useState<AmenityResponse[]>([]);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [reservations, setReservations] = useState<ReservationResponse[]>([]);
  const [reloadKey, setReloadKey] = useState(0);
  const [cancellingId, setCancellingId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  // Amenities belong to a building: reload (and drop the selection) when the building changes.
  useEffect(() => {
    let stale = false;
    setAmenities([]);
    setSelectedId(null);
    setError(null);
    getAmenities(apiOptions(session))
      .then((res) => {
        if (stale) return;
        if (res.status === 200) setAmenities(res.data);
        else setError(toDisplayError(res.status, res.data).message);
      })
      .catch((e) => !stale && setError(networkError(e).message));
    return () => {
      stale = true;
    };
    // Only the building scopes this list; the resident header doesn't change the result.
  }, [session.buildingId]);

  // Bookings for the selected amenity. reloadKey is bumped after a booking or cancellation.
  useEffect(() => {
    if (!selectedId) {
      setReservations([]);
      return;
    }
    let stale = false;
    getAmenityReservations(selectedId, undefined, apiOptions(session))
      .then((res) => {
        if (stale) return;
        if (res.status === 200) setReservations(res.data);
        else setError(toDisplayError(res.status, res.data).message);
      })
      .catch((e) => !stale && setError(networkError(e).message));
    return () => {
      stale = true;
    };
  }, [selectedId, session.buildingId, reloadKey]);

  async function cancel(id: string) {
    setError(null);
    setCancellingId(id);
    try {
      const res = await cancelReservation(id, apiOptions(session));
      if (res.status === 204) setReloadKey((k) => k + 1);
      else setError(toDisplayError(res.status, res.data).message);
    } catch (e) {
      setError(networkError(e).message);
    } finally {
      setCancellingId(null);
    }
  }

  const selected = amenities.find((a) => a.id === selectedId) ?? null;

  return (
    <main style={{ fontFamily: 'system-ui, sans-serif', maxWidth: 720, margin: '2rem auto', padding: '0 1rem' }}>
      <h1>Amenity Reservations</h1>

      <div style={{ display: 'flex', gap: '1rem', flexWrap: 'wrap', marginBottom: '1rem' }}>
        <label>
          Building{' '}
          <select
            data-testid="building-switcher"
            value={session.buildingId}
            onChange={(e) => setSession({ ...session, buildingId: e.target.value })}
          >
            {BUILDINGS.map((b) => (
              <option key={b.id} value={b.id}>
                {b.name}
              </option>
            ))}
          </select>
        </label>
        <label>
          Signed in as{' '}
          <select
            data-testid="resident-switcher"
            value={session.residentId}
            onChange={(e) => setSession({ ...session, residentId: e.target.value })}
          >
            {RESIDENTS.map((r) => (
              <option key={r} value={r}>
                {r}
              </option>
            ))}
          </select>
        </label>
      </div>

      {error && <p style={{ color: '#b00020' }}>{error}</p>}

      <AmenityList amenities={amenities} selectedId={selectedId} onSelect={setSelectedId} />

      {selected && (
        <section style={{ marginTop: '1.5rem' }}>
          <h2 style={{ marginBottom: '0.5rem' }}>{selected.name} bookings</h2>
          <ReservationList
            reservations={reservations}
            residentId={session.residentId}
            cancellingId={cancellingId}
            onCancel={cancel}
          />
          <BookingForm
            key={`${selected.id}:${session.residentId}`}
            amenity={selected}
            session={session}
            onBooked={() => setReloadKey((k) => k + 1)}
          />
        </section>
      )}
    </main>
  );
}
