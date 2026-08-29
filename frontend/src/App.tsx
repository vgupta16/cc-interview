import { useCallback, useEffect, useState } from 'react';
import {
  cancelReservation,
  createReservation,
  getAmenities,
  getAvailability,
  getResidents,
  type Amenity,
  type Resident,
  type SlotAvailability,
} from './api/generated/api';

const today = () => new Date().toISOString().slice(0, 10);
const hhmm = (iso: string) => new Date(iso).toISOString().slice(11, 16);

const plural = (n: number, one: string, many = `${one}s`) => `${n} ${n === 1 ? one : many}`;

/**
 * What to show beside the time: why the slot can't be booked if it can't, otherwise how much
 * room is left in whichever capacity dimensions this amenity tracks. Showing free capacity next
 * to a dead button reads as a bug — an elapsed slot has room, it just can't be booked.
 */
function slotLabel(slot: SlotAvailability): string {
  if (slot.unavailableReason) return slot.unavailableReason;

  const parts: string[] = [];
  if (slot.remainingSize != null) parts.push(plural(slot.remainingSize, 'space'));
  if (slot.remainingReservations != null) parts.push(plural(slot.remainingReservations, 'booking'));
  return parts.length ? parts.join(' · ') + ' left' : 'open';
}

/**
 * One screen for the whole reservation loop: pick who you are, pick an amenity, see a day of
 * hour-long slots with the room left in each, book one, cancel it.
 *
 * The resident picker is a stand-in for logging in. Switching residents across buildings is
 * also the tenancy demo — the amenity list changes with it.
 */
export function App() {
  const [residents, setResidents] = useState<Resident[]>([]);
  const [residentId, setResidentId] = useState('');

  const [amenities, setAmenities] = useState<Amenity[]>([]);
  /** Which resident the amenity list above was loaded for. See loadSlots. */
  const [amenitiesFor, setAmenitiesFor] = useState('');
  const [amenityId, setAmenityId] = useState('');

  const [date, setDate] = useState(today);
  const [slots, setSlots] = useState<SlotAvailability[]>([]);
  const [size, setSize] = useState(1);

  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const resident = residents.find((r) => r.id === residentId);
  const amenity = amenities.find((a) => a.id === amenityId);
  const maxSize = amenity?.maxSizePerReservation ?? 1;

  const fail = (e: unknown) => setError(e instanceof Error ? e.message : 'Request failed');

  // Who can log in.
  useEffect(() => {
    getResidents()
      .then(({ data }) => {
        setResidents(data);
        setResidentId((current) => current || data[0]?.id || '');
      })
      .catch(fail);
  }, []);

  // Amenities are scoped to the caller's building by the server, never by the client.
  useEffect(() => {
    if (!residentId) return;
    setError(null);
    getAmenities({ residentId })
      .then((res) => {
        if (res.status !== 200) {
          setAmenities([]);
          setError(res.data.detail ?? 'Could not load amenities');
          return;
        }
        setAmenities(res.data);
        setAmenityId(res.data[0]?.id ?? '');
        setAmenitiesFor(residentId);
      })
      .catch(fail);
  }, [residentId]);

  const loadSlots = useCallback(() => {
    // Wait until the amenity list belongs to the current resident. Right after switching
    // residents, amenityId still points at the previous building's amenity — asking for it
    // is a cross-building request that the server (correctly) 404s, which would flash a
    // bogus error. Resetting amenityId in the effect above lands a render too late, because
    // both effects run in the same commit.
    if (!amenityId || !residentId || amenitiesFor !== residentId) {
      setSlots([]);
      return;
    }
    getAvailability(amenityId, { residentId, date })
      .then((res) => {
        if (res.status !== 200) {
          setSlots([]);
          setError(res.data.detail ?? 'Could not load availability');
          return;
        }
        setSlots(res.data);
      })
      .catch(fail);
  }, [amenityId, residentId, date, amenitiesFor]);

  useEffect(loadSlots, [loadSlots]);

  // Clamp the party size when switching to an amenity with a smaller per-booking maximum.
  useEffect(() => setSize((s) => Math.min(Math.max(1, s), maxSize)), [maxSize]);

  async function book(slot: SlotAvailability) {
    setBusy(true);
    setError(null);
    try {
      const res = await createReservation({ residentId, amenityId, startUtc: slot.startUtc, size });
      // 400 = ask for less; 409 = someone got here first. Both carry a message worth showing.
      if (res.status !== 201) setError(res.data.detail ?? 'Could not book that slot');
      loadSlots();
    } catch (e) {
      fail(e);
    } finally {
      setBusy(false);
    }
  }

  async function cancel(reservationId: string) {
    setBusy(true);
    setError(null);
    try {
      const res = await cancelReservation(reservationId, { residentId });
      if (res.status !== 204) setError(res.data.detail ?? 'Could not cancel that booking');
      loadSlots();
    } catch (e) {
      fail(e);
    } finally {
      setBusy(false);
    }
  }

  return (
    <main style={{ fontFamily: 'system-ui, sans-serif', maxWidth: 720, margin: '2rem auto', padding: '0 1rem' }}>
      <h1>Amenity Reservations</h1>

      <div style={{ display: 'flex', gap: '0.75rem', flexWrap: 'wrap', alignItems: 'flex-end', marginBottom: '1rem' }}>
        <label style={label}>
          Resident
          <select value={residentId} onChange={(e) => setResidentId(e.target.value)} style={field}>
            {residents.map((r) => (
              <option key={r.id} value={r.id}>
                {r.name} — unit {r.unitNumber}
              </option>
            ))}
          </select>
        </label>

        <label style={label}>
          Amenity
          <select
            value={amenityId}
            onChange={(e) => {
              setError(null);
              setAmenityId(e.target.value);
            }}
            style={field}
          >
            {amenities.map((a) => (
              <option key={a.id} value={a.id}>
                {a.name}
              </option>
            ))}
          </select>
        </label>

        <label style={label}>
          Date (UTC)
          <input
            type="date"
            value={date}
            onChange={(e) => {
              setError(null);
              setDate(e.target.value);
            }}
            style={field}
          />
        </label>

        {maxSize > 1 && (
          <label style={label}>
            Party size
            <input
              type="number"
              min={1}
              max={maxSize}
              value={size}
              onChange={(e) => setSize(Number(e.target.value))}
              style={{ ...field, width: 70 }}
            />
          </label>
        )}
      </div>

      {resident && (
        <p style={{ color: '#777', fontSize: 13, marginTop: 0 }}>
          Showing amenities for building <code>{resident.buildingId.slice(0, 8)}</code>. Switch residents to
          confirm one building never sees another's.
        </p>
      )}

      {error && (
        <p style={{ background: '#fdeaea', color: '#b00020', padding: '0.6rem 0.8rem', borderRadius: 8 }}>{error}</p>
      )}

      {amenity && (
        <p style={{ color: '#555', fontSize: 13 }}>
          {amenity.description} Open {String(amenity.openingHourUtc).padStart(2, '0')}:00–
          {String(amenity.closingHourUtc).padStart(2, '0')}:00 UTC · max {maxSize} per booking
          {amenity.totalCapacity != null && ` · ${plural(amenity.totalCapacity, 'person', 'people')} at once`}
          {amenity.reservationCapacity != null &&
            ` · ${plural(amenity.reservationCapacity, 'booking')} at once`}
        </p>
      )}

      <ul style={{ listStyle: 'none', padding: 0 }}>
        {slots.map((slot) => {
          const mine = slot.myReservationId;
          return (
            <li
              key={slot.startUtc}
              style={{
                display: 'flex',
                alignItems: 'center',
                gap: '1rem',
                border: '1px solid #ddd',
                borderLeft: mine ? '4px solid #2b7' : '1px solid #ddd',
                borderRadius: 8,
                padding: '0.5rem 0.9rem',
                marginBottom: '0.5rem',
                opacity: slot.isAvailable || mine ? 1 : 0.5,
              }}
            >
              <strong style={{ width: 110 }}>
                {hhmm(slot.startUtc)}–{hhmm(slot.endUtc)}
              </strong>
              <span style={{ flex: 1, color: '#555', fontSize: 13 }}>{slotLabel(slot)}</span>

              {mine ? (
                <button onClick={() => cancel(mine)} disabled={busy} style={button}>
                  Cancel
                </button>
              ) : (
                <button onClick={() => book(slot)} disabled={busy || !slot.isAvailable} style={button}>
                  {slot.isAvailable ? `Book ${size > 1 ? `× ${size}` : ''}` : 'Unavailable'}
                </button>
              )}
            </li>
          );
        })}
      </ul>

      {amenityId && slots.length === 0 && <p style={{ color: '#777' }}>No slots for this day.</p>}
    </main>
  );
}

const label: React.CSSProperties = { display: 'flex', flexDirection: 'column', fontSize: 12, color: '#555', gap: 4 };
const field: React.CSSProperties = { font: 'inherit', padding: '0.35rem 0.4rem', borderRadius: 6, border: '1px solid #ccc' };
const button: React.CSSProperties = { font: 'inherit', padding: '0.35rem 0.8rem', borderRadius: 6, border: '1px solid #bbb', cursor: 'pointer' };
