import { useCallback, useEffect, useMemo, useState } from 'react';
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
 * The flow is buildings -> amenities -> reserve, which doubles as the isolation demo: switch
 * resident and the list of buildings changes with it, because the server derives access from
 * membership rather than trusting anything the client sends.
 */

/**
 * Identity is faked for this exercise. Switching resident stands in for logging in as someone
 * else; in production you are simply authenticated as one person and this control doesn't exist.
 *
 * Deliberately hardcoded rather than fetched: an endpoint listing every resident in the system
 * would be exactly the kind of unscoped read that the building-scoping work removed elsewhere.
 */
const DEMO_RESIDENTS = [
  { id: 'resident-jeremy', hint: 'belongs to two buildings' },
  { id: 'resident-dana', hint: 'belongs to one' },
] as const;

/** Every call carries the caller's identity. Under real auth this becomes an Authorization header. */
const asResident = (residentId: string): RequestInit => ({
  headers: { 'X-Resident-Id': residentId },
});

const pad = (n: number) => String(n).padStart(2, '0');

/** Format a Date the way `datetime-local` expects (local wall-clock, no zone). */
const toLocalInput = (d: Date) =>
  `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;

/** Format a Date the way `<input type="date">` expects. */
const toDateInput = (d: Date) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;

/** Half-open overlap, matching the server's rule exactly: A.start < B.end && B.start < A.end. */
const overlaps = (aStart: Date, aEnd: Date, bStart: Date, bEnd: Date) =>
  aStart < bEnd && bStart < aEnd;

const startOfDay = (day: string) => new Date(`${day}T00:00:00`);

const timeLabel = (d: Date) => d.toLocaleTimeString(undefined, { timeStyle: 'short' });

type Notice = { tone: 'ok' | 'bad'; text: string } | null;

export function App() {
  const [residentId, setResidentId] = useState<string>(DEMO_RESIDENTS[0].id);

  const [buildings, setBuildings] = useState<Building[]>([]);
  const [building, setBuilding] = useState<Building | null>(null);
  const [amenities, setAmenities] = useState<Amenity[]>([]);
  const [amenity, setAmenity] = useState<Amenity | null>(null);
  const [reservations, setReservations] = useState<Reservation[]>([]);

  const [day, setDay] = useState(() => toDateInput(new Date()));
  const [start, setStart] = useState('');
  const [end, setEnd] = useState('');

  /**
   * Hour-grid selection, as inclusive hour indices. `complete` distinguishes "one hour picked,
   * waiting to see if you extend" from "range settled", which is what makes the click cycle
   * pick -> extend -> clear work.
   */
  const [sel, setSel] = useState<{ from: number; to: number; complete: boolean } | null>(null);

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
        if (!cancelled) {
          setNotice({ tone: 'bad', text: 'Could not reach the API. Is the backend running on :5080?' });
        }
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

  /**
   * Hour-by-hour occupancy for the selected day.
   *
   * This is a *view* over arbitrary reservation intervals, not a model of them — the API accepts
   * any start/end, so an hour cell reports how many bookings overlap it rather than assuming
   * bookings are hour-aligned. Capacity above 1 means a cell is not simply free or taken; it has
   * a count remaining.
   *
   * Availability shown here is advisory. Someone else can take a slot between this render and a
   * click, which is exactly why the server does its own atomic check and why the 409 path below
   * still has to be handled. The client is never the authority on what's free.
   */
  const hours = useMemo(() => {
    if (!amenity) return [];
    const capacity = amenity.capacity!;
    const dayStart = startOfDay(day);

    const active = reservations.map((r) => ({
      residentId: r.residentId,
      start: new Date(r.startUtc).getTime(),
      end: new Date(r.endUtc).getTime(),
    }));

    return Array.from({ length: 24 }, (_, h) => {
      const cellStart = new Date(dayStart);
      cellStart.setHours(h);
      const cellEnd = new Date(cellStart);
      cellEnd.setHours(h + 1);
      const from = cellStart.getTime();
      const to = cellEnd.getTime();

      const touching = active.filter((a) => a.start < to && from < a.end);

      // Bookings do not respect hour boundaries — a 4:00–5:30 reservation leaves 5:30–6:00 free,
      // and reporting the 5:00 hour as simply "taken" would be a lie the resident can't act on.
      // So split the hour at every reservation boundary falling inside it and count how many
      // bookings cover each resulting slice.
      const cuts = [from, to];
      for (const a of touching) {
        if (a.start > from && a.start < to) cuts.push(a.start);
        if (a.end > from && a.end < to) cuts.push(a.end);
      }
      const bounds = [...new Set(cuts)].sort((x, y) => x - y);

      const segments = bounds.slice(0, -1).map((segStart, i) => {
        const segEnd = bounds[i + 1];
        const mid = (segStart + segEnd) / 2;
        const count = touching.filter((a) => a.start <= mid && mid < a.end).length;
        return { start: segStart, end: segEnd, count, free: count < capacity };
      });

      const freeMs = segments
        .filter((s) => s.free)
        .reduce((total, s) => total + (s.end - s.start), 0);

      // The free run touching the start of the hour, and the one touching its end. A range can
      // begin part-way into an hour (using its tail) and finish part-way into another (using its
      // head), so these are what make a partly-booked hour usable as a range edge.
      let headEnd: number | null = null;
      for (const s of segments) {
        if (!s.free) break;
        headEnd = s.end;
      }
      let tailStart: number | null = null;
      for (let i = segments.length - 1; i >= 0; i--) {
        if (!segments[i].free) break;
        tailStart = segments[i].start;
      }

      return {
        hour: h,
        cellStart,
        cellEnd,
        segments,
        freeMinutes: Math.round(freeMs / 60_000),
        /** Some capacity somewhere in the hour — i.e. the cell is worth clicking. */
        anyFree: segments.some((s) => s.free),
        /** Capacity free across the *whole* hour, so a range may span it. */
        bookableThroughout: segments.every((s) => s.free),
        /** Nothing booked at all, as opposed to "free but sharing with someone". */
        untouched: segments.every((s) => s.count === 0),
        busiest: segments.reduce((max, s) => Math.max(max, s.count), 0),
        firstFree: segments.find((s) => s.free) ?? null,
        headEnd,
        tailStart,
        mine: touching.some((a) => a.residentId === residentId),
      };
    });
  }, [amenity, day, reservations, residentId]);

  /** Reservations overlapping the selected day, for the list under the calendar. */
  const dayReservations = useMemo(() => {
    const dayStart = startOfDay(day);
    const dayEnd = new Date(dayStart);
    dayEnd.setDate(dayEnd.getDate() + 1);
    return reservations.filter((r) =>
      overlaps(dayStart, dayEnd, new Date(r.startUtc), new Date(r.endUtc)),
    );
  }, [reservations, day]);

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
    clearSelection();
    await refreshReservations(next);
  };

  /** Selections always fill the form rather than booking outright, so times stay editable. */
  const applyTimes = useCallback((from: number, to: number) => {
    setStart(toLocalInput(new Date(from)));
    setEnd(toLocalInput(new Date(to)));
  }, []);

  const clearSelection = useCallback(() => {
    setSel(null);
    setStart('');
    setEnd('');
  }, []);

  /**
   * Click cycle: first click anchors an hour, second click extends the range across everything
   * between, third click clears.
   *
   * Extending stops at the first fully-booked hour rather than jumping over it. Drawing a slot
   * as Full and then letting a selection swallow it would be incoherent — though note this is a
   * UI guard, not the enforcement. The server still validates whatever actually gets submitted.
   */
  const clickHour = (h: number) => {
    setNotice(null);
    const cell = hours[h];

    // Third click of the cycle.
    if (sel?.complete) {
      clearSelection();
      return;
    }

    // First click: anchor on this hour's first open window.
    if (!sel) {
      if (!cell.anyFree) return;
      setSel({ from: h, to: h, complete: false });
      applyTimes(cell.firstFree!.start, cell.firstFree!.end);
      return;
    }

    // Second click: grow the range toward h, keeping every minute inside it free.
    //
    // A partly-booked hour can sit at either edge of the range — the anchor contributes the free
    // run at its trailing edge, the far hour contributes the free run at its leading edge — but
    // it cannot be passed *through*, since that would swallow the booked part in the middle.
    const anchor = hours[sel.from];

    if (h === sel.from) {
      setSel({ from: h, to: h, complete: true });
      return;
    }

    const forward = h > sel.from;
    const edge = forward ? anchor.tailStart : anchor.headEnd;

    // The anchor's free window doesn't reach the side we're growing toward.
    if (edge === null) {
      setSel({ from: sel.from, to: sel.from, complete: true });
      return;
    }

    let reach = sel.from;
    let far = forward ? anchor.cellEnd.getTime() : anchor.cellStart.getTime();

    for (let i = sel.from + (forward ? 1 : -1); forward ? i <= h : i >= h; i += forward ? 1 : -1) {
      const next = forward ? hours[i].headEnd : hours[i].tailStart;
      if (next === null) break; // this hour is booked right where we'd enter it
      reach = i;
      far = next;
      if (!hours[i].bookableThroughout) break; // usable as an edge, not as a pass-through
    }

    if (reach === sel.from) {
      setSel({ from: sel.from, to: sel.from, complete: true });
      return;
    }

    setSel({
      from: Math.min(sel.from, reach),
      to: Math.max(sel.from, reach),
      complete: true,
    });
    applyTimes(forward ? edge : far, forward ? far : edge);
  };

  /** Summary reads off the form rather than the hour indices, so a snapped partial window
   *  (e.g. 5:30–6:00) is described accurately instead of as a whole hour. */
  const selectedMinutes =
    start && end ? Math.round((new Date(end).getTime() - new Date(start).getTime()) / 60_000) : 0;
  const overLimit = !!amenity && selectedMinutes > amenity.maxBookingMinutes!;

  const book = async (event: React.FormEvent) => {
    event.preventDefault();
    if (!amenity || !start || !end) return;

    setBusy(true);
    setNotice(null);
    try {
      const res = await createReservation(
        amenity.id,
        { startUtc: new Date(start).toISOString(), endUtc: new Date(end).toISOString() },
        asResident(residentId),
      );

      // The status is what distinguishes a booking from a rejection — see orval.config.ts for
      // why the generated client is configured to keep it.
      const status = res.status as number;
      if (status === 201) {
        setNotice({ tone: 'ok', text: 'Booked.' });
        clearSelection();
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
    setNotice(
      (res.status as number) === 204
        ? { tone: 'ok', text: 'Cancelled.' }
        : { tone: 'bad', text: 'That booking could not be cancelled.' },
    );
    await refreshReservations(amenity);
  };

  return (
    <main className="page">
      <h1 className="page__title">Amenity Reservations</h1>

      <fieldset className="identity">
        <legend className="identity__legend">Signed in as (stands in for auth)</legend>
        <div className="personas">
          {DEMO_RESIDENTS.map((person) => (
            <label className="persona" key={person.id}>
              <input
                type="radio"
                name="resident"
                value={person.id}
                checked={residentId === person.id}
                onChange={() => setResidentId(person.id)}
              />
              <span className="persona__name">{person.id}</span>
              <span className="persona__hint">{person.hint}</span>
            </label>
          ))}
        </div>
      </fieldset>

      {notice && <p className={`notice notice--${notice.tone}`}>{notice.text}</p>}

      {/* Screen 1 — buildings this resident belongs to */}
      {!building && (
        <section>
          <h2 className="section__title">My buildings</h2>
          <p className="section__meta">Only buildings you belong to are listed.</p>
          {buildings.length === 0 && <p className="empty">No buildings for this resident.</p>}
          <div className="stack">
            {buildings.map((b) => (
              <button key={b.id} className="card" onClick={() => openBuilding(b)}>
                <span className="card__name">{b.name}</span>
              </button>
            ))}
          </div>
        </section>
      )}

      {/* Screen 2 — amenities in the chosen building */}
      {building && !amenity && (
        <section>
          <button className="crumb" onClick={() => setBuilding(null)}>
            ← My buildings
          </button>
          <h2 className="section__title">{building.name}</h2>
          <p className="section__meta">
            {amenities.length} {amenities.length === 1 ? 'amenity' : 'amenities'}
          </p>
          <div className="stack">
            {amenities.map((a) => (
              <button key={a.id} className="card" onClick={() => openAmenity(a)}>
                <span className="card__name">{a.name}</span>
                {a.description && <div className="card__desc">{a.description}</div>}
                <div className="card__meta">
                  capacity {a.capacity} · up to {a.maxBookingMinutes} min per booking
                </div>
              </button>
            ))}
          </div>
        </section>
      )}

      {/* Screen 3 — day calendar + booking form */}
      {building && amenity && (
        <section>
          <button className="crumb" onClick={() => setAmenity(null)}>
            ← {building.name}
          </button>
          <h2 className="section__title">{amenity.name}</h2>
          <p className="section__meta">
            capacity {amenity.capacity} · up to {amenity.maxBookingMinutes} min per booking
          </p>

          <div className="daybar">
            <label className="field">
              Day
              <input
                type="date"
                value={day}
                onChange={(e) => {
                  setDay(e.target.value);
                  clearSelection();
                }}
              />
            </label>
            <p className="daybar__hint">
              Click an hour to start, click another to extend, click again to clear.
            </p>
          </div>

          <div className="slots">
            {hours.map((slot) => {
              const state = !slot.anyFree ? 'full' : slot.untouched ? 'free' : 'partial';
              const selected = !!sel && slot.hour >= sel.from && slot.hour <= sel.to;

              const label = !slot.anyFree
                ? 'Full'
                : slot.untouched
                  ? 'Open'
                  : slot.bookableThroughout
                    ? `${amenity.capacity! - slot.busiest} left`
                    : `${slot.freeMinutes} min free`;

              return (
                <button
                  key={slot.hour}
                  className={[
                    'slot',
                    `slot--${state}`,
                    slot.mine ? 'slot--mine' : '',
                    selected ? 'slot--selected' : '',
                  ]
                    .filter(Boolean)
                    .join(' ')}
                  aria-pressed={selected}
                  disabled={!slot.anyFree}
                  onClick={() => clickHour(slot.hour)}
                  title={
                    !slot.anyFree
                      ? 'Fully booked'
                      : slot.bookableThroughout
                        ? `${amenity.capacity! - slot.busiest} of ${amenity.capacity} available all hour`
                        : `Free ${slot.segments
                            .filter((s) => s.free)
                            .map((s) => `${timeLabel(new Date(s.start))}–${timeLabel(new Date(s.end))}`)
                            .join(', ')}`
                  }
                >
                  <span className="slot__hour">{timeLabel(slot.cellStart)}</span>
                  <span className="slot__state">{label}</span>
                  {/* Miniature timeline of the hour: which minutes are actually free. */}
                  <span className="slot__bar" aria-hidden="true">
                    {slot.segments.map((s) => (
                      <span
                        key={s.start}
                        className={`slot__seg slot__seg--${s.free ? 'free' : 'busy'}`}
                        style={{ flexGrow: s.end - s.start }}
                      />
                    ))}
                  </span>
                </button>
              );
            })}
          </div>

          {sel && start && end && (
            <p className={`selection${overLimit ? ' selection--warn' : ''}`}>
              Selected {timeLabel(new Date(start))} – {timeLabel(new Date(end))} ·{' '}
              {selectedMinutes >= 60 && `${Math.floor(selectedMinutes / 60)} hr `}
              {selectedMinutes % 60 > 0 && `${selectedMinutes % 60} min`}
              {overLimit && ` — over the ${amenity.maxBookingMinutes} minute maximum`}
            </p>
          )}

          <form className="form" onSubmit={book}>
            {/* Typing a time by hand drops the grid highlight rather than letting it keep
                claiming a range the form no longer holds. */}
            <label className="field">
              From
              <input
                type="datetime-local"
                value={start}
                onChange={(e) => {
                  setStart(e.target.value);
                  setSel(null);
                }}
                required
              />
            </label>
            <label className="field">
              To
              <input
                type="datetime-local"
                value={end}
                onChange={(e) => {
                  setEnd(e.target.value);
                  setSel(null);
                }}
                required
              />
            </label>
            <button type="submit" className="btn btn--primary" disabled={busy || !start || !end}>
              {busy ? 'Booking…' : 'Reserve'}
            </button>
          </form>

          <h3 className="subhead">
            Booked on {startOfDay(day).toLocaleDateString(undefined, { dateStyle: 'medium' })}
          </h3>
          {dayReservations.length === 0 && <p className="empty">Nothing booked this day.</p>}
          <ul className="stack">
            {dayReservations.map((r) => {
              const mine = r.residentId === residentId;
              return (
                <li key={r.id} className={`booking${mine ? ' booking--mine' : ''}`}>
                  <span>
                    {timeLabel(new Date(r.startUtc))} – {timeLabel(new Date(r.endUtc))}{' '}
                    <span className="booking__who">· {r.residentId}</span>
                  </span>
                  {mine && (
                    <button className="btn btn--quiet" onClick={() => cancel(r)}>
                      Cancel
                    </button>
                  )}
                </li>
              );
            })}
          </ul>
        </section>
      )}
    </main>
  );
}
