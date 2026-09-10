using Api.Domain;
using Api.Store;

namespace Api.Http;

/// <summary>
/// Resolves the caller. This is the single seam where identity enters the system, so replacing
/// the header shim with real authentication touches this file and nothing else.
/// </summary>
public static class RequestContext
{
    /// <summary>
    /// Stands in for an authenticated principal. In a real deployment the resident would come
    /// from a session or token; nothing downstream cares which, because everything downstream
    /// takes a resolved <see cref="Resident"/>.
    /// </summary>
    public const string ResidentHeaderName = "X-Resident-Id";

    /// <summary>
    /// Turns a header value into a known resident. An unparseable, missing, or unrecognized id
    /// all fail the same way — the endpoint has no resident, so it does nothing.
    /// </summary>
    public static bool TryResolveResident(this InMemoryStore store, Guid? residentId, out Resident resident)
    {
        var found = residentId is { } id ? store.FindResident(id) : null;
        resident = found!;
        return found is not null;
    }
}
