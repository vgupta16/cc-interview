import { useCallback, useEffect, useState } from 'react';
import {
  cancelReservation,
  createReservation,
  getAmenityReservations,
  getBuildingAmenities,
  getBuildings,
  type Amenity,
  type Building,
  type Reservation,
} from './api/generated/api';

/**
 * Amenity reservations, end to end: React -> generated client -> minimal API -> in-memory store.
 *
 * The flow is buildings -> amenities -> reserve, which doubles as the isolation demo: change the
 * resident id and the list of buildings changes with it, because the server derives access from
 * membership rather than trusting anything the client sends.
 *
 * Identity is faked. `X-Resident-Id` stands in for what would be a bearer token; seeded residents
 * are `resident-jeremy` (both buildings) and `resident-dana` (Main Street only).
 */

/** Every call carries the caller's identity. Under real auth this becomes an Authorization header. */
const asResident = (residentId: string): RequestInit => ({
  headers: { 'X-Resident-Id': residentId },
});

/** `datetime-local` gives back local wall-clock time; the API works in UTC. */
const toUtcIso = (localValue: string) => new Date(localValue).toISOString();

const formatSlot = (r: Reservation) => {
  const opts: Intl.DateTimeFormatOptions = { dateStyle: 'medium', timeStyle: 'short' };
  return `${new Date(r.startUtc).toLocaleString(undefined, opts)} – ${new Date(
    r.endUtc,
  ).toLocaleTimeString(undefined, { timeStyle: 'short' })}`;
};

/** Default the form to the next whole hour, in the format `datetime-local` expects. */
const nextHourLocal = (offsetHours: number) => {
  const d = new Date();
  d.setMinutes(0, 0, 0);
  d.setHours(d.getHours() + 1 + offsetHours);
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(
    d.getMinutes(),
  )}`;
};

type Notice = { tone: 'ok' | 'bad'; text: string } | null;

export function App() {
  const [residentId, setResidentId] = useState('resident-jeremy');

  const [buildings, setBuildings] = useState<Building[]>([]);
  const [building, setBuilding] = useState<Building | null>(null);
  const [amenities, setAmenities] = useState<Amenity[]>([]);
  const [amenity, setAmenity] = useState<Amenity | null>(null);
  const [reservations, setReservations] = useState<Reservation[]>([]);

  const [start, setStart] = useState(() => nextHourLocal(0));
  const [end, setEnd] = useState(() => nextHourLocal(1));

  const [notice, setNotice] = useState<Notice>(null);
  const [busy, setBusy] = useState(false);

  // Changing the resident starts the whole flow over — different person, different access.
  useEffect(() => {
    let cancelled = false;
    setBuilding(null);
    setAmenity(null);
    setAmenities([]);
    setReservations([]);
    setNotice(null);

    getBuildings(asResident(residentId))
      .then((res) => {
        if (!cancelled) setBuildings(res.status === 200 ? res.data : []);
      })
      .catch(() => {
        if (!cancelled) setNotice({ tone: 'bad', text: 'Could not reach the API. Is the backend running on :5080?' });
      });

    return () => {
      cancelled = true;
    };
  }, [residentId]);

  const refreshReservations = useCallback(
    async (target: Amenity) => {
      const res = await getAmenityReservations(target.id, asResident(residentId));
      setReservations(res.status === 200 ? res.data : []);
    },
    [residentId],
  );

  const openBuilding = async (next: Building) => {
    setBuilding(next);
    setAmenity(null);
    setReservations([]);
    setNotice(null);
    const res = await getBuildingAmenities(next.id, asResident(residentId));
    setAmenities(res.status === 200 ? res.data : []);
  };

  const openAmenity = async (next: Amenity) => {
    setAmenity(next);
    setNotice(null);
    await refreshReservations(next);
  };

  const book = async (event: React.FormEvent) => {
    event.preventDefault();
    if (!amenity) return;

    setBusy(true);
    setNotice(null);
    try {
      const res = await createReservation(
        amenity.id,
        { startUtc: toUtcIso(start), endUtc: toUtcIso(end) },
        asResident(residentId),
      );

      // The status is what distinguishes a booking from a rejection — see orval.config.ts for
      // why the generated client is configured to keep it.
      const status = res.status as number;
      if (status === 201) {
        setNotice({ tone: 'ok', text: 'Booked.' });
        await refreshReservations(amenity);
      } else if (status === 409) {
        setNotice({ tone: 'bad', text: 'That time is no longer available. Someone booked it first.' });
        await refreshReservations(amenity);
      } else if (status === 403) {
        setNotice({ tone: 'bad', text: "You don't have access to this amenity." });
      } else {
        const problem = res.data as unknown as { error?: string };
        setNotice({ tone: 'bad', text: problem?.error ?? 'That booking was rejected.' });
      }
    } catch {
      setNotice({ tone: 'bad', text: 'Could not reach the API.' });
    } finally {
      setBusy(false);
    }
  };

  const cancel = async (reservation: Reservation) => {
    if (!amenity) return;
    const res = await cancelReservation(reservation.id, asResident(residentId));
    const status = res.status as number;
    setNotice(
      status === 204
        ? { tone: 'ok', text: 'Cancelled.' }
        : { tone: 'bad', text: 'That booking could not be cancelled.' },
    );
    await refreshReservations(amenity);
  };

  return (
    <main style={{ fontFamily: 'system-ui, sans-serif', maxWidth: 640, margin: '2rem auto', padding: '0 1rem' }}>
      <h1>Amenity Reservations</h1>

      <label style={{ display: 'block', marginBottom: '1.5rem', fontSize: 14 }}>
        <span style={{ color: '#555' }}>Signed in as (stands in for auth)</span>
        <input
          value={residentId}
          onChange={(e) => setResidentId(e.target.value)}
          style={{ display: 'block', marginTop: 4, padding: '0.4rem', width: 240, fontFamily: 'inherit' }}
        />
        <span style={{ color: '#777', fontSize: 12 }}>
          Try <code>resident-jeremy</code> (two buildings) or <code>resident-dana</code> (one).
        </span>
      </label>

      {notice && (
        <p
          style={{
            padding: '0.6rem 0.9rem',
            borderRadius: 8,
            background: notice.tone === 'ok' ? '#e7f6ec' : '#fdecef',
            color: notice.tone === 'ok' ? '#1b5e35' : '#b00020',
          }}
        >
          {notice.text}
        </p>
      )}

      {/* Screen 1 — buildings this resident belongs to */}
      {!building && (
        <section>
          <h2 style={{ fontSize: 18 }}>My buildings</h2>
          {buildings.length === 0 && <p style={{ color: '#777' }}>No buildings for this resident.</p>}
          {buildings.map((b) => (
            <button key={b.id} onClick={() => openBuilding(b)} style={cardStyle}>
              <strong>{b.name}</strong>
            </button>
          ))}
        </section>
      )}

      {/* Screen 2 — amenities in the chosen building */}
      {building && !amenity && (
        <section>
          <Crumb onBack={() => setBuilding(null)} label="My buildings" />
          <h2 style={{ fontSize: 18 }}>{building.name}</h2>
          {amenities.map((a) => (
            <button key={a.id} onClick={() => openAmenity(a)} style={cardStyle}>
              <strong>{a.name}</strong>
              {a.description && <div style={{ color: '#555' }}>{a.description}</div>}
              <div style={{ fontSize: 13, color: '#777', marginTop: 4 }}>
                capacity {a.capacity} · max {a.maxBookingMinutes} min
              </div>
            </button>
          ))}
        </section>
      )}

      {/* Screen 3 — existing bookings + the reservation form */}
      {building && amenity && (
        <section>
          <Crumb onBack={() => setAmenity(null)} label={building.name} />
          <h2 style={{ fontSize: 18 }}>{amenity.name}</h2>
          <p style={{ fontSize: 13, color: '#777', marginTop: -8 }}>
            capacity {amenity.capacity} · max {amenity.maxBookingMinutes} min per booking
          </p>

          <form onSubmit={book} style={{ display: 'flex', gap: '0.5rem', alignItems: 'end', flexWrap: 'wrap' }}>
            <label style={{ fontSize: 13 }}>
              From
              <input
                type="datetime-local"
                value={start}
                onChange={(e) => setStart(e.target.value)}
                style={{ display: 'block', padding: '0.35rem', fontFamily: 'inherit' }}
              />
            </label>
            <label style={{ fontSize: 13 }}>
              To
              <input
                type="datetime-local"
                value={end}
                onChange={(e) => setEnd(e.target.value)}
                style={{ display: 'block', padding: '0.35rem', fontFamily: 'inherit' }}
              />
            </label>
            <button type="submit" disabled={busy} style={{ padding: '0.45rem 1rem' }}>
              {busy ? 'Booking…' : 'Reserve'}
            </button>
          </form>

          <h3 style={{ fontSize: 15, marginTop: '1.5rem' }}>Booked</h3>
          {reservations.length === 0 && <p style={{ color: '#777' }}>Nothing booked yet.</p>}
          <ul style={{ listStyle: 'none', padding: 0 }}>
            {reservations.map((r) => (
              <li
                key={r.id}
                style={{
                  border: '1px solid #ddd',
                  borderRadius: 8,
                  padding: '0.6rem 0.9rem',
                  marginBottom: '0.5rem',
                  display: 'flex',
                  justifyContent: 'space-between',
                  alignItems: 'center',
                  gap: '1rem',
                }}
              >
                <span>
                  {formatSlot(r)}
                  <span style={{ color: '#777', fontSize: 13 }}> · {r.residentId}</span>
                </span>
                {r.residentId === residentId && (
                  <button onClick={() => cancel(r)} style={{ padding: '0.3rem 0.7rem' }}>
                    Cancel
                  </button>
                )}
              </li>
            ))}
          </ul>
        </section>
      )}
    </main>
  );
}

const cardStyle: React.CSSProperties = {
  display: 'block',
  width: '100%',
  textAlign: 'left',
  border: '1px solid #ddd',
  borderRadius: 8,
  padding: '0.75rem 1rem',
  marginBottom: '0.75rem',
  background: 'white',
  cursor: 'pointer',
  fontFamily: 'inherit',
  fontSize: 15,
};

function Crumb({ onBack, label }: { onBack: () => void; label: string }) {
  return (
    <button
      onClick={onBack}
      style={{
        border: 'none',
        background: 'none',
        padding: 0,
        color: '#0b62d0',
        cursor: 'pointer',
        fontFamily: 'inherit',
        fontSize: 14,
      }}
    >
      ← {label}
    </button>
  );
}
