// Display formatting. Times are shown in the viewer's local time zone, never UTC; the API still
// speaks UTC instants and the conversion happens only at the edges (form input, display).

const whenFormat = new Intl.DateTimeFormat(undefined, {
  weekday: 'short',
  month: 'short',
  day: 'numeric',
  hour: 'numeric',
  minute: '2-digit',
});

const timeFormat = new Intl.DateTimeFormat(undefined, { hour: 'numeric', minute: '2-digit' });

const sameLocalDay = (a: Date, b: Date) =>
  a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();

/** "Thu, Oct 15, 10:00 AM – 11:00 AM": a same-day range shows the date once; otherwise both ends. */
export function formatWindow(startIso: string, endIso: string): string {
  const start = new Date(startIso);
  const end = new Date(endIso);
  const endText = sameLocalDay(start, end) ? timeFormat.format(end) : whenFormat.format(end);
  return `${whenFormat.format(start)} – ${endText}`;
}

/** Up to 90 minutes reads as minutes ("60 min"); longer reads as hours and minutes ("2 h 30 min"). */
export function formatDuration(totalMinutes: number): string {
  const minutes = Math.round(totalMinutes);
  if (minutes <= 90) return `${minutes} min`;
  const h = Math.floor(minutes / 60);
  const m = minutes % 60;
  return m === 0 ? `${h} h` : `${h} h ${m} min`;
}

export function durationBetween(startIso: string, endIso: string): string {
  return formatDuration((new Date(endIso).getTime() - new Date(startIso).getTime()) / 60_000);
}

/** Residents are identified by lowercase ids ("alice"); display them as names ("Alice"). */
export function formatName(id: string): string {
  return id
    .split(/[\s._-]+/)
    .filter(Boolean)
    .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
    .join(' ');
}

/** Formats a Date as the local "YYYY-MM-DDTHH:mm" string a datetime-local input expects. */
export function toLocalInput(d: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}
