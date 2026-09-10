using System.Text.Json;

namespace Api;

/// <summary>
/// The JSON settings responses are serialized with. Minimal APIs use
/// <see cref="JsonSerializerDefaults.Web"/>; naming it here lets tests serialize a response
/// exactly the way the wire would, which is what makes assertions against the raw payload
/// meaningful.
/// </summary>
public static class ApiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
