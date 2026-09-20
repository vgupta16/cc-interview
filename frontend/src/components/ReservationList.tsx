import type { ReservationResponse } from '../api/generated/api';
import { durationBetween, formatName, formatWindow } from '../format';

interface Props {
  reservations: ReservationResponse[];
  amenityName: (amenityId: string) => string;
  residentId: string;
  cancellingId: string | null;
  onCancel: (id: string) => void;
}

/** Every upcoming booking in the building, across amenities, in start order. */
export function ReservationList({ reservations, amenityName, residentId, cancellingId, onCancel }: Props) {
  if (reservations.length === 0) return <p style={{ color: '#777' }}>No upcoming bookings.</p>;

  return (
    <ul style={{ listStyle: 'none', padding: 0, margin: 0, display: 'grid', gap: '0.4rem' }}>
      {reservations.map((r) => {
        const mine = r.residentId === residentId;
        return (
          <li
            key={r.id}
            data-testid={`reservation-row-${r.id}`}
            style={{
              display: 'flex',
              justifyContent: 'space-between',
              alignItems: 'center',
              gap: '0.75rem',
              border: '1px solid #ddd',
              borderRadius: 6,
              padding: '0.5rem 0.75rem',
              background: mine ? '#f4faf4' : '#fff',
            }}
          >
            <span>
              <strong>{amenityName(r.amenityId)}</strong>
              <span style={{ color: '#555' }}>
                {' '}
                · {formatName(r.residentId)}
                {mine && <span style={{ color: '#2e7d32' }}> (you)</span>}
              </span>
              <div style={{ fontSize: 14 }}>
                {formatWindow(r.startUtc, r.endUtc)}{' '}
                <span style={{ color: '#777' }}>({durationBetween(r.startUtc, r.endUtc)})</span>
              </div>
            </span>
            {mine && (
              <button
                type="button"
                data-testid={`cancel-${r.id}`}
                disabled={cancellingId === r.id}
                onClick={() => onCancel(r.id)}
              >
                Cancel
              </button>
            )}
          </li>
        );
      })}
    </ul>
  );
}
