import type { ReservationResponse } from '../api/generated/api';

interface Props {
  reservations: ReservationResponse[];
  residentId: string;
  cancellingId: string | null;
  onCancel: (id: string) => void;
}

const fmt = (iso: string) => new Date(iso).toISOString().slice(0, 16).replace('T', ' ');

export function ReservationList({ reservations, residentId, cancellingId, onCancel }: Props) {
  if (reservations.length === 0) return <p style={{ color: '#777' }}>No bookings yet.</p>;

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
              border: '1px solid #ddd',
              borderRadius: 6,
              padding: '0.5rem 0.75rem',
              background: mine ? '#f4faf4' : '#fff',
            }}
          >
            <span>
              <strong>{r.residentId}</strong>
              {mine && <span style={{ color: '#2e7d32' }}> (you)</span>} · {fmt(r.startUtc)} → {fmt(r.endUtc)} UTC
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
