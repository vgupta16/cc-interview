import { useState, type FormEvent } from 'react';
import { createReservation, type AmenityResponse } from '../api/generated/api';
import { networkError, toDisplayError, type DisplayError } from '../api/errors';
import { apiOptions, type Session } from '../api/session';
import { formatDuration, formatName, toLocalInput } from '../format';

interface Props {
  amenity: AmenityResponse;
  session: Session;
  onBooked: () => void;
}

export function BookingForm({ amenity, session, onBooked }: Props) {
  const [start, setStart] = useState('');
  const [end, setEnd] = useState('');
  // Until the resident edits the end time themselves, it follows the start (start + default length).
  const [endTouched, setEndTouched] = useState(false);
  const [error, setError] = useState<DisplayError | null>(null);
  const [submitting, setSubmitting] = useState(false);

  const defaultMinutes = Math.min(60, amenity.maxBookingMinutes);

  function changeStart(value: string) {
    setStart(value);
    if (!endTouched) {
      const startDate = new Date(value);
      setEnd(Number.isNaN(startDate.getTime()) ? '' : toLocalInput(new Date(startDate.getTime() + defaultMinutes * 60_000)));
    }
  }

  const startDate = new Date(start);
  const endDate = new Date(end);
  const minutes = (endDate.getTime() - startDate.getTime()) / 60_000;
  const hint = Number.isFinite(minutes) && minutes > 0 ? `Duration: ${formatDuration(minutes)}` : null;

  async function submit(e: FormEvent) {
    e.preventDefault();
    setError(null);

    // datetime-local has no zone: it's the browser's local time. Convert to UTC instants for the API.
    if (!Number.isFinite(minutes)) {
      setError({ code: 'invalid_input', message: 'Choose a start and an end time.' });
      return;
    }

    setSubmitting(true);
    try {
      const res = await createReservation(
        { amenityId: amenity.id, startUtc: startDate.toISOString(), endUtc: endDate.toISOString() },
        apiOptions(session),
      );
      if (res.status === 201) {
        setStart('');
        setEnd('');
        setEndTouched(false);
        onBooked();
      } else {
        // Non-2xx responses don't throw with this client; the status tells us what happened.
        const shown = toDisplayError(res.status, res.data);
        // The server states the limit in minutes; show it the way the rest of the page does.
        if (shown.code === 'max_duration_exceeded') {
          shown.message = `${amenity.name} can be booked for at most ${formatDuration(amenity.maxBookingMinutes)} at a time.`;
        }
        setError(shown);
      }
    } catch (err) {
      setError(networkError(err));
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <form onSubmit={submit} style={{ display: 'grid', gap: '0.5rem', marginTop: '1rem' }}>
      <h3 style={{ margin: 0 }}>
        Book {amenity.name} as {formatName(session.residentId)}
      </h3>
      <label>
        Start{' '}
        <input
          type="datetime-local"
          data-testid="book-start"
          value={start}
          onChange={(e) => changeStart(e.target.value)}
        />
      </label>
      <label>
        End{' '}
        <input
          type="datetime-local"
          data-testid="book-end"
          value={end}
          onChange={(e) => {
            setEnd(e.target.value);
            setEndTouched(true);
          }}
        />
      </label>
      <div style={{ color: '#555', fontSize: 14 }}>
        {hint ?? 'Pick a start and end time.'} · up to {formatDuration(amenity.maxBookingMinutes)} per booking
      </div>
      <div>
        <button type="submit" data-testid="book-submit" disabled={submitting}>
          {submitting ? 'Booking…' : 'Book'}
        </button>
      </div>
      {error && (
        <p role="alert" data-testid="booking-error" data-code={error.code} style={{ color: '#b00020', margin: 0 }}>
          {error.message}
        </p>
      )}
    </form>
  );
}
