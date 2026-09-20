import type { AmenityResponse } from '../api/generated/api';
import { formatDuration } from '../format';

interface Props {
  amenities: AmenityResponse[];
  selectedId: string | null;
  onSelect: (id: string) => void;
}

export function AmenityList({ amenities, selectedId, onSelect }: Props) {
  return (
    <ul style={{ listStyle: 'none', padding: 0, margin: 0, display: 'grid', gap: '0.5rem' }}>
      {amenities.map((a) => {
        const selected = a.id === selectedId;
        return (
          <li key={a.id}>
            <button
              type="button"
              data-testid={`amenity-card-${a.id}`}
              aria-pressed={selected}
              onClick={() => onSelect(a.id)}
              style={{
                width: '100%',
                textAlign: 'left',
                cursor: 'pointer',
                font: 'inherit',
                background: selected ? '#eef4ff' : '#fff',
                border: `1px solid ${selected ? '#4c7cf0' : '#ddd'}`,
                borderRadius: 8,
                padding: '0.75rem 1rem',
              }}
            >
              <strong>{a.name}</strong>
              {a.description && <div style={{ color: '#555' }}>{a.description}</div>}
              <div style={{ fontSize: 13, color: '#777', marginTop: 4 }}>
                capacity {a.capacity} · up to {formatDuration(a.maxBookingMinutes)} per booking
              </div>
            </button>
          </li>
        );
      })}
    </ul>
  );
}
