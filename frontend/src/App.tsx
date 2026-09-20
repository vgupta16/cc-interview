import { useEffect, useState } from 'react';
import {
  cancelReservation,
  getAmenities,
  getReservations,
  type AmenityResponse,
  type ReservationResponse,
} from './api/generated/api';
import { networkError, toDisplayError } from './api/errors';
import { formatName } from './format';
import { apiOptions, BUILDINGS, RESIDENTS, type Session } from './api/session';
import { AmenityList } from './components/AmenityList';
import { BookingForm } from './components/BookingForm';
import { ReservationList } from './components/ReservationList';
import { loadView, saveView } from './storage';

// Restore what the viewer had open before a refresh, ignoring anything that's no longer valid.
function restoreSession(): Session {
  const saved = loadView();
  return {
    buildingId: BUILDINGS.some((b) => b.id === saved.buildingId) ? saved.buildingId! : BUILDINGS[0].id,
    residentId: RESIDENTS.some((r) => r === saved.residentId) ? saved.residentId! : RESIDENTS[0],
  };
}

export function App() {
  const [session, setSession] = useState<Session>(restoreSession);
  const [amenities, setAmenities] = useState<AmenityResponse[]>([]);
  // Only trusted once the amenity list has loaded and contains it (see `selected` below).
  const [selectedId, setSelectedId] = useState<string | null>(() => loadView().selectedAmenityId ?? null);
  const [reservations, setReservations] = useState<ReservationResponse[]>([]);
  const [reservationsLoading, setReservationsLoading] = useState(true);
  const [reloadKey, setReloadKey] = useState(0);
  const [cancellingId, setCancellingId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  // Switching building clears the selection and the old building's bookings in the same update.
  // If they were cleared in an effect, one render would pair the new building's headers with
  // the old building's amenity id (a 404) or briefly show the wrong building's bookings.
  function changeBuilding(buildingId: string) {
    setSelectedId(null);
    setReservations([]);
    setReservationsLoading(true);
    setSession((s) => ({ ...s, buildingId }));
  }

  // An id restored from storage (or left over from another building) is ignored unless this
  // building's amenity list actually contains it.
  const selected = amenities.find((a) => a.id === selectedId) ?? null;
  const amenityName = (id: string) => amenities.find((a) => a.id === id)?.name ?? 'Amenity';

  useEffect(() => {
    saveView({ ...session, selectedAmenityId: selectedId });
  }, [session, selectedId]);

  // Amenities belong to a building: reload them when the building changes.
  useEffect(() => {
    let stale = false;
    setAmenities([]);
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

  // Every upcoming booking in the building, across amenities. reloadKey is bumped after a
  // booking or cancellation. Bookings that already ended are left out.
  useEffect(() => {
    let stale = false;
    getReservations({ from: new Date().toISOString() }, apiOptions(session))
      .then((res) => {
        if (stale) return;
        if (res.status === 200) setReservations(res.data);
        else setError(toDisplayError(res.status, res.data).message);
      })
      .catch((e) => !stale && setError(networkError(e).message))
      .finally(() => !stale && setReservationsLoading(false));
    return () => {
      stale = true;
    };
  }, [session.buildingId, reloadKey]);

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

  return (
    <main style={{ fontFamily: 'system-ui, sans-serif', maxWidth: 720, margin: '2rem auto', padding: '0 1rem' }}>
      <h1>Amenity Reservations</h1>

      <div style={{ display: 'flex', gap: '1rem', flexWrap: 'wrap', marginBottom: '1rem' }}>
        <label>
          Building{' '}
          <select
            data-testid="building-switcher"
            value={session.buildingId}
            onChange={(e) => changeBuilding(e.target.value)}
          >
            {BUILDINGS.map((b) => (
              <option key={b.id} value={b.id}>
                {b.name}
              </option>
            ))}
          </select>
        </label>
        <label title="Stand-in for real sign-in, so you can act as different residents">
          Demo: sign in as{' '}
          <select
            data-testid="resident-switcher"
            value={session.residentId}
            onChange={(e) => setSession({ ...session, residentId: e.target.value })}
          >
            {RESIDENTS.map((r) => (
              <option key={r} value={r}>
                {formatName(r)}
              </option>
            ))}
          </select>
        </label>
      </div>

      {error && <p style={{ color: '#b00020' }}>{error}</p>}

      <h2 style={{ marginBottom: '0.5rem' }}>Book an amenity</h2>
      <AmenityList amenities={amenities} selectedId={selected?.id ?? null} onSelect={setSelectedId} />

      {selected && (
        <BookingForm
          key={`${selected.id}:${session.residentId}`}
          amenity={selected}
          session={session}
          onBooked={() => setReloadKey((k) => k + 1)}
        />
      )}

      <section
        data-testid="reservations"
        data-loading={reservationsLoading}
        style={{ marginTop: '2rem' }}
      >
        <h2 style={{ marginBottom: '0.5rem' }}>Reservations</h2>
        {reservationsLoading ? (
          <p style={{ color: '#777' }}>Loading…</p>
        ) : (
          <ReservationList
            reservations={reservations}
            amenityName={amenityName}
            residentId={session.residentId}
            cancellingId={cancellingId}
            onCancel={cancel}
          />
        )}
      </section>
    </main>
  );
}
