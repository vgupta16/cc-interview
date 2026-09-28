import { useEffect, useState } from "react";
import {
  createReservation,
  getAmenities,
  getAmenityAvailability,
  getMyReservations,
  type Amenity,
  type AvailabilityWindow,
  type Reservation
} from "./api/generated/api";
import { SAMPLE_RESIDENTS, asResident } from "./residents";
import {
  HALF_HOUR_TIMES,
  MAX_DAYS_AHEAD,
  defaultStart,
  formatDuration,
  formatRange,
  latestStartDate,
  todayUtcDate,
  startUtcIso,
  type StartChoice
} from "./time";

const API_UNREACHABLE = "Couldn't reach the API. Please verify the backend is available.";

// one 30-minute slot: the shortest reservation for every amenity (DESIGN.md, A8), and the default.
const MIN_DURATION_MINUTES = 30;

type Notice = { kind: "success" | "conflict"; text: string };

/**
 * The reservation screen: pick a sample resident, pick an amenity in their building, see up
 * to five candidate windows with remaining capacity, reserve one, and see "My Reservations".
 */
export function App() {
  const [residentId, setResidentId] = useState<string>(SAMPLE_RESIDENTS[0].id);
  const [amenities, setAmenities] = useState<Amenity[]>([]);
  const [amenitiesLoading, setAmenitiesLoading] = useState<boolean>(true);
  const [reservations, setReservations] = useState<Reservation[]>([]);
  const [reservationsLoading, setReservationsLoading] = useState<boolean>(true);
  const [reservationsVersion, setReservationsVersion] = useState<number>(0);
  const [selectedAmenityId, setSelectedAmenityId] = useState<string | null>(null);
  const [start, setStart] = useState<StartChoice>(defaultStart);
  const [durationMinutes, setDurationMinutes] = useState<number>(MIN_DURATION_MINUTES);
  const [windows, setWindows] = useState<AvailabilityWindow[]>([]);
  const [windowsLoading, setWindowsLoading] = useState<boolean>(false);
  const [availabilityVersion, setAvailabilityVersion] = useState<number>(0);
  const [submitting, setSubmitting] = useState<boolean>(false);
  const [notice, setNotice] = useState<Notice | null>(null);
  const [amenitiesError, setAmenitiesError] = useState<string | null>(null);
  const [reservationsError, setReservationsError] = useState<string | null>(null);
  const [availabilityError, setAvailabilityError] = useState<string | null>(null);
  const [submissionError, setSubmissionError] = useState<string | null>(null);

  // a failure both lists share, such as an unknown resident or an unreachable API, is page-wide:
  // it shows once under the resident picker; otherwise, each error shows in its own section.
  // neither section's error shows until both sections return, so an error never appears in its section
  // and then moves to the banner; until then, a section that already failed keeps its loading text.

  const isLoaded = !amenitiesLoading && !reservationsLoading;
  const pageError = isLoaded && amenitiesError !== null && amenitiesError === reservationsError ? amenitiesError : null;
  const amenitiesSectionError = isLoaded && !pageError ? amenitiesError : null;
  const reservationsSectionError = isLoaded && !pageError ? reservationsError : null;
  const amenitiesPending = amenitiesLoading || (amenitiesError !== null && !isLoaded);
  const reservationsPending = reservationsLoading || (reservationsError !== null && !isLoaded);

  const selectedAmenity = amenities.find((amenity) => amenity.id === selectedAmenityId) ?? null;
  const amenityNames = new Map(amenities.map((amenity) => [amenity.id, amenity.name]));
  const startUtc = startUtcIso(start);

  // each read below ignores a response that arrives after its inputs changed, so one resident's
  // data never renders under another. reads may run twice under StrictMode; writes never run here.

  useEffect(() => {
    let ignore = false;

    setAmenitiesLoading(true);
    getAmenities(asResident(residentId))
      .then((response) => {
        if (ignore) {
          return;
        }

        if (response.status === 200) {
          setAmenities(response.data);
          setAmenitiesError(null);
        } else {
          setAmenities([]);
          setAmenitiesError(describeProblem(response.status, response.data));
        }
      })
      .catch(() => {
        if (!ignore) {
          setAmenitiesError(API_UNREACHABLE);
        }
      })
      .finally(() => {
        if (!ignore) {
          setAmenitiesLoading(false);
        }
      });
    return () => {
      ignore = true;
    };
  }, [residentId]);

  useEffect(() => {
    let ignore = false;

    setReservationsLoading(true);
    getMyReservations(asResident(residentId))
      .then((response) => {
        if (ignore) {
          return;
        }

        if (response.status === 200) {
          setReservations(response.data);
          setReservationsError(null);
        } else {
          setReservations([]);
          setReservationsError(describeProblem(response.status, response.data));
        }
      })
      .catch(() => {
        if (!ignore) {
          setReservationsError(API_UNREACHABLE);
        }
      })
      .finally(() => {
        if (!ignore) {
          setReservationsLoading(false);
        }
      });
    return () => {
      ignore = true;
    };
  }, [residentId, reservationsVersion]);

  useEffect(() => {
    setWindows([]);
    setAvailabilityError(null);

    if (!selectedAmenityId || !startUtc) {
      setWindowsLoading(false);
      return;
    }

    let ignore = false;

    setWindowsLoading(true);
    getAmenityAvailability(selectedAmenityId, { startUtc, durationMinutes }, asResident(residentId))
      .then((response) => {
        if (ignore) {
          return;
        }

        if (response.status === 200) {
          setWindows(response.data);
        } else {
          setAvailabilityError(describeProblem(response.status, response.data));
        }
      })
      .catch(() => {
        if (!ignore) {
          setAvailabilityError(API_UNREACHABLE);
        }
      })
      .finally(() => {
        if (!ignore) {
          setWindowsLoading(false);
        }
      });
    return () => {
      ignore = true;
    };
  }, [residentId, selectedAmenityId, startUtc, durationMinutes, availabilityVersion]);

  /**
   * Form actions clear submission feedback, not failures from unrelated reads.
   */
  function clearSubmissionMessages() {
    setNotice(null);
    setSubmissionError(null);
  }

  function changeResident(id: string) {
    if (id === residentId) {
      return;
    }

    setAmenitiesLoading(true);
    setReservationsLoading(true);
    setAmenitiesError(null);
    setReservationsError(null);
    setAvailabilityError(null);
    setResidentId(id);
    setAmenities([]);
    setReservations([]);
    setWindows([]);
    setSelectedAmenityId(null);

    // the form starts fresh for a different resident, so one resident's choices never carry over.
    setStart(defaultStart());
    setDurationMinutes(MIN_DURATION_MINUTES);

    clearSubmissionMessages();
  }

  function selectAmenity(amenity: Amenity) {
    if (amenity.id === selectedAmenityId) {
      return;
    }

    // a different amenity starts from the defaults: the next start and the shortest duration.
    setSelectedAmenityId(amenity.id);
    setStart(defaultStart());
    setDurationMinutes(MIN_DURATION_MINUTES);
    clearSubmissionMessages();
  }

  // the only write. it runs from a click, never from an effect, and is never retried automatically:
  // request deduplication is deferred, so an uncertain result means checking My Reservations first.

  async function reserve(candidate: AvailabilityWindow) {
    if (!selectedAmenity) {
      return;
    }

    setSubmitting(true);
    clearSubmissionMessages();

    try {
      const response = await createReservation(
        {
          amenityId: selectedAmenity.id,
          startUtc: candidate.startUtc,
          durationMinutes
        },
        asResident(residentId)
      );

      if (response.status === 201) {
        // confirm first; a failed refresh afterwards shows in its own section, not in this notice.
        setNotice({
          kind: "success",
          text: `Reserved ${selectedAmenity.name}, ${formatRange(response.data.startUtc, response.data.endUtc)}.`
        });
        setReservationsVersion((v) => v + 1);
        setAvailabilityVersion((v) => v + 1);
      } else if (response.status === 409) {
        setNotice({
          kind: "conflict",
          text: response.data.detail ?? "That window is no longer available."
        });
        setAvailabilityVersion((v) => v + 1);
      } else {
        setSubmissionError(describeProblem(response.status, response.data));
      }
    } catch {
      setSubmissionError(
        `${API_UNREACHABLE} The reservation may not have been saved; check My Reservations before trying again.`
      );
      setReservationsVersion((v) => v + 1);
    } finally {
      setSubmitting(false);
    }
  }

  const durationOptions = selectedAmenity
    ? Array.from({ length: selectedAmenity.maxBookingMinutes / 30 }, (_, i) => (i + 1) * 30)
    : [];

  return (
    <main
      style={{
        fontFamily: "system-ui, sans-serif",
        maxWidth: 720,
        margin: "2rem auto",
        padding: "0 1rem"
      }}
    >
      <h1>Amenity Reservations</h1>

      <label style={{ display: "block", marginBottom: "1rem" }}>
        Resident:{" "}
        <select value={residentId} disabled={submitting} onChange={(e) => changeResident(e.target.value)}>
          {SAMPLE_RESIDENTS.map((r) => (
            <option key={r.id} value={r.id}>
              {r.label}
            </option>
          ))}
        </select>
        <span style={{ marginLeft: 8, fontSize: 13, color: "#777" }}>
          Sample identity (in place of real authentication).
        </span>
      </label>

      {pageError && <ErrorMessage text={pageError} />}

      <h2>Amenities</h2>
      {amenitiesPending && <p>Loading amenities...</p>}
      {amenitiesSectionError && <ErrorMessage text={amenitiesSectionError} />}
      {!amenitiesLoading && !amenitiesError && amenities.length === 0 && <p>No amenities in this building.</p>}
      <ul style={{ listStyle: "none", padding: 0 }}>
        {amenities.map((a) => (
          <li key={a.id} style={{ marginBottom: "0.5rem" }}>
            <button
              type="button"
              disabled={submitting}
              onClick={() => selectAmenity(a)}
              style={{
                ...card,
                borderColor: a.id === selectedAmenityId ? "#1565c0" : "#ddd"
              }}
            >
              <strong>{a.name}</strong>
              {a.description && <div style={{ color: "#555" }}>{a.description}</div>}
              <div style={{ fontSize: 13, color: "#777", marginTop: 4 }}>
                {a.capacity} {a.capacity === 1 ? "spot" : "spots"} · up to {formatDuration(a.maxBookingMinutes)}
              </div>
            </button>
          </li>
        ))}
      </ul>

      {selectedAmenity && (
        <section>
          <h2>Availability: {selectedAmenity.name}</h2>
          <div
            style={{
              display: "flex",
              gap: "1rem",
              flexWrap: "wrap",
              marginBottom: "0.75rem"
            }}
          >
            <label>
              Start Date:{" "}
              <input
                type="date"
                name="startDate"
                min={todayUtcDate()}
                max={latestStartDate()}
                value={start.date}
                disabled={submitting}
                onChange={(e) => {
                  // functional updates, so a date and a time chosen in quick succession both apply.
                  const date = e.target.value;

                  setStart((current) => ({ ...current, date }));
                  clearSubmissionMessages();
                }}
              />
            </label>
            <label>
              Time:{" "}
              <select
                name="startTime"
                value={start.time}
                disabled={submitting}
                onChange={(e) => {
                  const time = e.target.value;

                  setStart((current) => ({ ...current, time }));
                  clearSubmissionMessages();
                }}
              >
                {HALF_HOUR_TIMES.map((time) => (
                  <option key={time} value={time}>
                    {time}
                  </option>
                ))}
              </select>
              <span style={{ marginLeft: 8, fontSize: 13, color: "#777" }}>
                (UTC, up to {MAX_DAYS_AHEAD} days ahead)
              </span>
            </label>
            <label>
              Duration{" "}
              <select
                name="duration"
                value={durationMinutes}
                disabled={submitting}
                onChange={(e) => {
                  setDurationMinutes(Number(e.target.value));
                  clearSubmissionMessages();
                }}
              >
                {durationOptions.map((minutes) => (
                  <option key={minutes} value={minutes}>
                    {formatDuration(minutes)}
                  </option>
                ))}
              </select>
            </label>
          </div>
          {notice && (
            <p
              role="status"
              style={{
                ...banner,
                background: notice.kind === "success" ? "#e8f5e9" : "#fff4e5",
                color: notice.kind === "success" ? "#1b5e20" : "#8a4b00"
              }}
            >
              {notice.text}
            </p>
          )}
          {submissionError && <ErrorMessage text={submissionError} />}
          {availabilityError && <ErrorMessage text={availabilityError} />}
          {windowsLoading && <p>Loading availability...</p>}
          <ul style={{ listStyle: "none", padding: 0 }}>
            {windows.map((w) => (
              <li key={w.startUtc} style={{ ...row, opacity: w.remainingCapacity === 0 ? 0.6 : 1 }}>
                <span style={{ flex: 1 }}>{formatRange(w.startUtc, w.endUtc)}</span>
                <span
                  style={{
                    width: 150,
                    color: w.remainingCapacity === 0 ? "#b00020" : "#1b5e20"
                  }}
                >
                  {w.remainingCapacity === 0
                    ? "FULL"
                    : `${w.remainingCapacity} of ${selectedAmenity.capacity} ${selectedAmenity.capacity === 1 ? "spot" : "spots"} open`}
                </span>
                <button
                  type="button"
                  disabled={submitting || w.remainingCapacity === 0}
                  onClick={() => void reserve(w)}
                >
                  Reserve
                </button>
              </li>
            ))}
          </ul>
        </section>
      )}

      <h2>My Reservations</h2>
      {reservationsPending && <p>Loading reservations...</p>}
      {reservationsSectionError && <ErrorMessage text={reservationsSectionError} />}
      {!reservationsLoading && !reservationsError && reservations.length === 0 && <p>No upcoming reservations.</p>}
      <ul style={{ listStyle: "none", padding: 0 }}>
        {reservations.map((r) => (
          <li key={r.id} style={row}>
            <strong style={{ flex: 1 }}>{amenityNames.get(r.amenityId) ?? r.amenityId}</strong>
            <span>{formatRange(r.startUtc, r.endUtc)}</span>
          </li>
        ))}
      </ul>
    </main>
  );
}

/**
 * An error message, announced to screen readers, shown where the failure applies.
 */
function ErrorMessage({ text }: { text: string }) {
  return (
    <p role="alert" style={{ ...banner, background: "#fdecea", color: "#b00020" }}>
      {text}
    </p>
  );
}

/**
 * Turns a non-success response into one error message: validation errors, a ProblemDetails
 * detail or title, or a generic line for statuses the API doesn't declare.
 */
function describeProblem(status: number, data: unknown): string {
  if (data && typeof data === "object") {
    const problem = data as {
      title?: string | null;
      detail?: string | null;
      errors?: Record<string, string[]>;
    };

    if (problem.errors) {
      return Object.values(problem.errors).flat().join(" ");
    }

    if (problem.detail) {
      return problem.detail;
    }

    if (problem.title) {
      return problem.title;
    }
  }
  return status >= 500
    ? `The API returned an error (HTTP ${status}). Is the backend running on http://localhost:5080?`
    : `Unexpected response from the API (HTTP ${status}).`;
}

const banner = { padding: "0.5rem 0.75rem", borderRadius: 6 };
const card = {
  width: "100%",
  textAlign: "left" as const,
  background: "white",
  border: "1px solid #ddd",
  borderRadius: 8,
  padding: "0.75rem 1rem",
  cursor: "pointer",
  font: "inherit"
};
const row = {
  display: "flex",
  gap: "0.75rem",
  alignItems: "center",
  padding: "0.5rem 0",
  borderBottom: "1px solid #eee"
};
