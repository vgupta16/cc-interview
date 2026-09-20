import { useState, type FormEvent } from 'react';
import { createReservation, type AmenityResponse } from '../api/generated/api';
import { networkError, toDisplayError, type DisplayError } from '../api/errors';
import { apiOptions, type Session } from '../api/session';

interface Props {
  amenity: AmenityResponse;
  session: Session;
  onBooked: () => void;
}

export function BookingForm({ amenity, session, onBooked }: Props) {
  const [start, setStart] = useState('');
  const [duration, setDuration] = useState(60);
  const [error, setError] = useState<DisplayError | null>(null);
  const [submitting, setSubmitting] = useState(false);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setError(null);

    // datetime-local has no zone: it's the browser's local time. Convert to a UTC instant.
    const startDate = new Date(start);
    if (Number.isNaN(startDate.getTime()) || !Number.isFinite(duration) || duration <= 0) {
      setError({ code: 'invalid_input', message: 'Choose a start time and a positive duration.' });
      return;
    }
    const endDate = new Date(startDate.getTime() + duration * 60_000);

    setSubmitting(true);
    try {
      const res = await createReservation(
        { amenityId: amenity.id, startUtc: startDate.toISOString(), endUtc: endDate.toISOString() },
        apiOptions(session),
      );
      if (res.status === 201) {
        setStart('');
        onBooked();
      } else {
        // Non-2xx responses don't throw with this client; the status tells us what happened.
        setError(toDisplayError(res.status, res.data));
      }
    } catch (err) {
      setError(networkError(err));
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <form onSubmit={submit} style={{ display: 'grid', gap: '0.5rem', marginTop: '1rem' }}>
      <h3 style={{ margin: 0 }}>Book {amenity.name}</h3>
      <label>
        Start (your local time){' '}
        <input
          type="datetime-local"
          data-testid="book-start"
          value={start}
          onChange={(e) => setStart(e.target.value)}
        />
      </label>
      <label>
        Duration (minutes, max {amenity.maxBookingMinutes}){' '}
        <input
          type="number"
          data-testid="book-duration"
          min={1}
          step={1}
          value={duration}
          onChange={(e) => setDuration(Number(e.target.value))}
          style={{ width: 80 }}
        />
      </label>
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
