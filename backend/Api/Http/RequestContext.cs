using Api.Application;

namespace Api.Http;

/// <summary>The tenant and (optionally) the caller for one request.</summary>
public sealed record RequestContext(Guid BuildingId, Actor? Actor);

/// <summary>
/// The single place tenant and identity are resolved. Swapping headers for validated token claims
/// means replacing this one class; nothing downstream reads headers or trusts a request body.
/// </summary>
public interface IRequestContextResolver
{
    Result<RequestContext> Resolve(HttpRequest request, bool requireResident);
}

/// <summary>
/// Stand-in for real authentication: trusts <c>X-Building-Id</c>, <c>X-Resident-Id</c> and
/// <c>X-Resident-Roles</c> (comma separated; "staff" grants staff rights). Never use in production.
/// </summary>
public sealed class HeaderRequestContextResolver : IRequestContextResolver
{
    public const string BuildingHeader = "X-Building-Id";
    public const string ResidentHeader = "X-Resident-Id";
    public const string RolesHeader = "X-Resident-Roles";

    public Result<RequestContext> Resolve(HttpRequest request, bool requireResident)
    {
        if (!Guid.TryParse(request.Headers[BuildingHeader].ToString(), out var buildingId))
        {
            return Result<RequestContext>.Fail(new Failure(
                ErrorKind.Unauthorized, ErrorCodes.MissingBuilding, $"A valid {BuildingHeader} header is required."));
        }

        var residentId = request.Headers[ResidentHeader].ToString().Trim();
        if (residentId.Length == 0)
        {
            return requireResident
                ? Result<RequestContext>.Fail(new Failure(
                    ErrorKind.Unauthorized, ErrorCodes.MissingResident, $"A {ResidentHeader} header is required."))
                : Result<RequestContext>.Ok(new RequestContext(buildingId, null));
        }

        var isStaff = request.Headers[RolesHeader].ToString()
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Contains("staff", StringComparer.OrdinalIgnoreCase);

        return Result<RequestContext>.Ok(new RequestContext(buildingId, new Actor(residentId, isStaff)));
    }
}
