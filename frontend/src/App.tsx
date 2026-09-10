import { useEffect, useMemo, useState } from 'react';
import {
  cancelReservation,
  createReservation,
  getAmenities,
  getAmenitySchedule,
  getMyReservations,
  getResidents,
  type Amenity,
  type AmenityScheduleResponse,
  type MyReservation,
  type ResidentSummary,
} from './api/generated/api';
import { setCurrentResident } from './api/http';
import { describeFailure, type Failure } from './lib/errors';
import {
  buildDaySlots,
  durationOptions,
  formatDuration,
  SLOT_MINUTES,
  type DaySlot,
} from './lib/schedule';
import { formatDate, formatTime, formatWindow, localDayBounds, todayInZone, zonedToUtc } from './lib/time';

const palette = {
  border: '#dcdfe4',
  muted: '#5c6370',
  free: '#e8f5ec',
  taken: '#f3f4f6',
  danger: '#b00020',
  dangerBg: '#fdecef',
  accent: '#1f6feb',
};

export function App() {
  const [residents, setResidents] = useState<ResidentSummary[]>([]);
  const [residentId, setResidentId] = useState<string | null>(null);

  const [amenities, setAmenities] = useState<Amenity[]>([]);
  const [amenityId, setAmenityId] = useState<string | null>(null);

  const [dateIso, setDateIso] = useState<string>('');
  const [schedule, setSchedule] = useState<AmenityScheduleResponse | null>(null);
  const [mine, setMine] = useState<MyReservation[]>([]);

  const [startTime, setStartTime] = useState('18:00');
  const [durationMinutes, setDurationMinutes] = useState(60);

  const [failure, setFailure] = useState<Failure | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [refreshKey, setRefreshKey] = useState(0);

  const resident = residents.find((r) => r.id === residentId) ?? null;
  const timeZone = resident?.buildingTimeZoneId ?? null;
  const amenity = amenities.find((a) => a.id === amenityId) ?? null;

  // Duration choices depend on the amenity's own rule, so the current pick is clamped rather
  // than kept in state — switching from the party room to the gym can't leave 4 hours selected.
  const durations = durationOptions(amenity?.maxBookingMinutes ?? 120);
  const duration = durations.includes(durationMinutes) ? durationMinutes : durations[0];

  // Who am I? No auth in this exercise, so the roster comes from the dev endpoint.
  useEffect(() => {
    getResidents()
      .then((list) => {
        setResidents(list);
        setResidentId(list[0]?.id ?? null);
      })
      .catch((e) => setFailure(describeFailure(e)));
  }, []);

  // Acting as a different resident re-scopes everything: their building's amenities, their
  // building's time zone, their bookings.
  useEffect(() => {
    if (!residentId) return;
    const current = residents.find((r) => r.id === residentId);
    if (!current) return;

    setCurrentResident(residentId);
    setFailure(null);
    setSchedule(null);
    setDateIso(todayInZone(current.buildingTimeZoneId));

    let active = true;
    Promise.all([getAmenities(), getMyReservations()])
      .then(([list, bookings]) => {
        if (!active) return;
        setAmenities(list);
        setMine(bookings);
        setAmenityId(list[0]?.id ?? null);
      })
      .catch((e) => active && setFailure(describeFailure(e)));

    return () => {
      active = false;
    };
  }, [residentId, residents]);

  // One building-local day of bookings for the selected amenity.
  useEffect(() => {
    if (!amenityId || !timeZone || !dateIso) return;
    const { from, to } = localDayBounds(dateIso, timeZone);

    let active = true;
    getAmenitySchedule(amenityId, { fromUtc: from.toISOString(), toUtc: to.toISOString() })
      .then((data) => active && setSchedule(data))
      .catch((e) => active && setFailure(describeFailure(e)));

    return () => {
      active = false;
    };
  }, [amenityId, dateIso, timeZone, refreshKey]);

  const slots = useMemo<DaySlot[]>(() => {
    if (!schedule || !timeZone || !dateIso) return [];
    const { from, to } = localDayBounds(dateIso, timeZone);
    return buildDaySlots(schedule.busy, schedule.capacity, from, to);
  }, [schedule, timeZone, dateIso]);

  const refresh = async () => {
    setMine(await getMyReservations());
    setRefreshKey((k) => k + 1);
  };

  const book = async () => {
    if (!amenity || !timeZone) return;
    setSubmitting(true);
    setFailure(null);

    const start = zonedToUtc(dateIso, startTime, timeZone);
    const end = new Date(start.getTime() + duration * 60_000);

    try {
      await createReservation({
        amenityId: amenity.id,
        startUtc: start.toISOString(),
        endUtc: end.toISOString(),
      });
      await refresh();
    } catch (e) {
      setFailure(describeFailure(e));
    } finally {
      setSubmitting(false);
    }
  };

  const cancel = async (reservationId: string) => {
    setFailure(null);
    try {
      await cancelReservation(reservationId);
      await refresh();
    } catch (e) {
      setFailure(describeFailure(e));
    }
  };

  const amenityName = (id: string) => amenities.find((a) => a.id === id)?.name ?? 'Amenity';

  return (
    <main style={{ fontFamily: 'system-ui, sans-serif', maxWidth: 960, margin: '2rem auto', padding: '0 1rem' }}>
      <header style={{ display: 'flex', alignItems: 'baseline', justifyContent: 'space-between', gap: '1rem', flexWrap: 'wrap' }}>
        <h1 style={{ margin: 0 }}>Amenity Reservations</h1>
        <label style={{ fontSize: 14, color: palette.muted }}>
          Signed in as{' '}
          <select value={residentId ?? ''} onChange={(e) => setResidentId(e.target.value)} style={{ padding: '0.3rem' }}>
            {residents.map((r) => (
              <option key={r.id} value={r.id}>
                {r.displayName} · {r.buildingName}
                {r.unitNumber ? ` #${r.unitNumber}` : ''}
              </option>
            ))}
          </select>
        </label>
      </header>

      <p style={{ color: palette.muted, fontSize: 14 }}>
        {resident
          ? `Showing ${resident.buildingName}. Times are ${resident.buildingTimeZoneId.replace('_', ' ')}.`
          : 'Loading…'}
      </p>

      {failure && <FailureBanner failure={failure} timeZone={timeZone} />}

      <div style={{ display: 'grid', gridTemplateColumns: 'minmax(200px, 1fr) 2fr', gap: '1.5rem', alignItems: 'start' }}>
        <section>
          <h2 style={{ fontSize: 15, textTransform: 'uppercase', letterSpacing: 0.5, color: palette.muted }}>Amenities</h2>
          <ul style={{ listStyle: 'none', padding: 0, margin: 0 }}>
            {amenities.map((a) => {
              const selected = a.id === amenityId;
              return (
                <li key={a.id} style={{ marginBottom: '0.5rem' }}>
                  <button
                    onClick={() => setAmenityId(a.id)}
                    style={{
                      width: '100%',
                      textAlign: 'left',
                      cursor: 'pointer',
                      padding: '0.6rem 0.75rem',
                      borderRadius: 8,
                      border: `1px solid ${selected ? palette.accent : palette.border}`,
                      background: selected ? '#eef4ff' : '#fff',
                    }}
                  >
                    <strong>{a.name}</strong>
                    <div style={{ fontSize: 12, color: palette.muted, marginTop: 2 }}>
                      {(a.capacity ?? 1) > 1 ? `${a.capacity} at a time` : 'One at a time'} · up to{' '}
                      {formatDuration(a.maxBookingMinutes ?? 120)}
                    </div>
                  </button>
                </li>
              );
            })}
          </ul>
        </section>

        <section>
          {amenity && timeZone ? (
            <>
              <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: '1rem', flexWrap: 'wrap' }}>
                <h2 style={{ margin: 0, fontSize: 20 }}>{amenity.name}</h2>
                <input
                  type="date"
                  value={dateIso}
                  onChange={(e) => setDateIso(e.target.value)}
                  style={{ padding: '0.35rem', border: `1px solid ${palette.border}`, borderRadius: 6 }}
                />
              </div>

              <p style={{ color: palette.muted, fontSize: 13, marginTop: '0.5rem' }}>
                {dateIso && formatDate(`${dateIso}T12:00:00Z`, 'UTC')} · grey is taken. Bookings by other residents
                are shown as busy time only.
              </p>

              <SlotGrid
                slots={slots}
                timeZone={timeZone}
                capacity={schedule?.capacity ?? 1}
                onPick={(slot) => setStartTime(formatHHmm(slot.startUtc, timeZone))}
              />

              <div
                style={{
                  display: 'flex',
                  gap: '0.75rem',
                  alignItems: 'flex-end',
                  flexWrap: 'wrap',
                  marginTop: '1rem',
                  padding: '0.9rem',
                  border: `1px solid ${palette.border}`,
                  borderRadius: 8,
                }}
              >
                <label style={{ fontSize: 13, color: palette.muted }}>
                  Start
                  <br />
                  <input
                    type="time"
                    step={SLOT_MINUTES * 60}
                    value={startTime}
                    onChange={(e) => setStartTime(e.target.value)}
                    style={{ padding: '0.35rem', border: `1px solid ${palette.border}`, borderRadius: 6 }}
                  />
                </label>

                <label style={{ fontSize: 13, color: palette.muted }}>
                  For
                  <br />
                  <select
                    value={duration}
                    onChange={(e) => setDurationMinutes(Number(e.target.value))}
                    style={{ padding: '0.4rem', border: `1px solid ${palette.border}`, borderRadius: 6 }}
                  >
                    {durations.map((m) => (
                      <option key={m} value={m}>
                        {formatDuration(m)}
                      </option>
                    ))}
                  </select>
                </label>

                <button
                  onClick={book}
                  disabled={submitting}
                  style={{
                    padding: '0.5rem 1rem',
                    borderRadius: 6,
                    border: 'none',
                    background: palette.accent,
                    color: '#fff',
                    cursor: submitting ? 'progress' : 'pointer',
                  }}
                >
                  {submitting ? 'Booking…' : 'Book'}
                </button>
              </div>
            </>
          ) : (
            <p style={{ color: palette.muted }}>Select an amenity.</p>
          )}
        </section>
      </div>

      <section style={{ marginTop: '2rem' }}>
        <h2 style={{ fontSize: 15, textTransform: 'uppercase', letterSpacing: 0.5, color: palette.muted }}>My bookings</h2>
        {mine.length === 0 && <p style={{ color: palette.muted }}>Nothing booked yet.</p>}
        <ul style={{ listStyle: 'none', padding: 0, margin: 0 }}>
          {mine.map((r) => (
            <li
              key={r.id}
              style={{
                display: 'flex',
                justifyContent: 'space-between',
                alignItems: 'center',
                gap: '1rem',
                border: `1px solid ${palette.border}`,
                borderRadius: 8,
                padding: '0.6rem 0.8rem',
                marginBottom: '0.5rem',
              }}
            >
              <span>
                <strong>{amenityName(r.amenityId)}</strong>
                <span style={{ color: palette.muted }}>
                  {' '}
                  · {timeZone && formatDate(r.startUtc, timeZone)}, {timeZone && formatWindow(r.startUtc, r.endUtc, timeZone)}
                </span>
              </span>
              <button
                onClick={() => cancel(r.id)}
                style={{ padding: '0.35rem 0.7rem', borderRadius: 6, border: `1px solid ${palette.border}`, cursor: 'pointer' }}
              >
                Cancel
              </button>
            </li>
          ))}
        </ul>
      </section>
    </main>
  );
}

function SlotGrid({
  slots,
  timeZone,
  capacity,
  onPick,
}: {
  slots: DaySlot[];
  timeZone: string;
  capacity: number;
  onPick: (slot: DaySlot) => void;
}) {
  if (slots.length === 0) return <p style={{ color: palette.muted }}>Loading availability…</p>;

  return (
    <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(92px, 1fr))', gap: 4, marginTop: '0.75rem' }}>
      {slots.map((slot) => (
        <button
          key={slot.startUtc}
          onClick={() => onPick(slot)}
          disabled={!slot.isFree}
          title={slot.isFree ? 'Use as start time' : 'Fully booked'}
          style={{
            padding: '0.4rem 0.2rem',
            fontSize: 12,
            borderRadius: 6,
            cursor: slot.isFree ? 'pointer' : 'not-allowed',
            border: `1px solid ${palette.border}`,
            background: slot.isFree ? palette.free : palette.taken,
            color: slot.isFree ? '#14532d' : '#9096a1',
          }}
        >
          {formatTime(slot.startUtc, timeZone)}
          {capacity > 1 && (
            <div style={{ fontSize: 10, opacity: 0.8 }}>
              {capacity - slot.held} of {capacity} free
            </div>
          )}
        </button>
      ))}
    </div>
  );
}

function FailureBanner({ failure, timeZone }: { failure: Failure; timeZone: string | null }) {
  const isConflict = failure.kind === 'conflict';

  return (
    <div
      role="alert"
      style={{
        border: `1px solid ${isConflict ? '#f0b3bf' : palette.border}`,
        background: isConflict ? palette.dangerBg : '#fff',
        color: isConflict ? palette.danger : '#333',
        borderRadius: 8,
        padding: '0.7rem 0.9rem',
        margin: '0.5rem 0 1rem',
        fontSize: 14,
      }}
    >
      {failure.message}
      {failure.conflicts.length > 0 && timeZone && (
        <div style={{ marginTop: 4, color: palette.muted }}>
          Already booked: {failure.conflicts.map((c) => formatWindow(c.startUtc, c.endUtc, timeZone)).join(', ')}
        </div>
      )}
    </div>
  );
}

/** `HH:mm` in the building's zone, for prefilling the time input from a clicked slot. */
function formatHHmm(instant: string, timeZone: string): string {
  return new Intl.DateTimeFormat('en-GB', {
    timeZone,
    hour: '2-digit',
    minute: '2-digit',
    hour12: false,
  }).format(new Date(instant));
}
