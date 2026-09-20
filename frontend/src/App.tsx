import { useEffect, useState } from 'react';
import { getAmenities, type AmenityResponse } from './api/generated/api';

// Temporary: the UI rewrite (reservations) replaces this. Building A from the seed data.
const DEMO_HEADERS = { 'X-Building-Id': 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa' };

/**
 * Worked example: lists amenities from the backend through the generated client.
 *
 * TODO(candidate): build the reservation experience. Suggested (change it to fit your design):
 *   - pick an amenity and see its existing bookings
 *   - create a booking for a time slot (and surface a clear error on a double-book)
 *   - cancel your own booking
 * After you add backend endpoints, run `pnpm gen:api` to get typed hooks/functions here.
 */
export function App() {
  const [amenities, setAmenities] = useState<AmenityResponse[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    getAmenities({ headers: DEMO_HEADERS })
      .then((res) => {
        if (res.status !== 200) throw new Error(res.data.message);
        setAmenities(res.data);
      })
      .catch((e) => setError(e instanceof Error ? e.message : 'Failed to load amenities'))
      .finally(() => setLoading(false));
  }, []);

  return (
    <main style={{ fontFamily: 'system-ui, sans-serif', maxWidth: 640, margin: '2rem auto', padding: '0 1rem' }}>
      <h1>Amenity Reservations</h1>
      <p style={{ color: '#555' }}>
        This is the starter. The amenity list below is the worked example — now build reservations.
      </p>

      {loading && <p>Loading amenities…</p>}
      {error && (
        <p style={{ color: '#b00020' }}>
          Couldn’t reach the API ({error}). Is the backend running on <code>http://localhost:5080</code>?
        </p>
      )}

      <ul style={{ listStyle: 'none', padding: 0 }}>
        {amenities.map((a) => (
          <li key={a.id} style={{ border: '1px solid #ddd', borderRadius: 8, padding: '0.75rem 1rem', marginBottom: '0.75rem' }}>
            <strong>{a.name}</strong>
            {a.description && <div style={{ color: '#555' }}>{a.description}</div>}
            <div style={{ fontSize: 13, color: '#777', marginTop: 4 }}>
              capacity {a.capacity} · max {a.maxBookingMinutes} min
            </div>
          </li>
        ))}
      </ul>
    </main>
  );
}
