using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteManager.Core.Status;

namespace RemoteManager.Core.RemoteTools;

/// <summary>One device of the TeamViewer account (<c>GET /api/v1/devices</c>).</summary>
/// <param name="RemoteId">TeamViewer ID without the "r" prefix ("123456789").</param>
public sealed record TeamViewerDevice(string DeviceId, string RemoteId, string Alias, string Description, string GroupId, OnlineStatus Online);

public sealed record TeamViewerGroup(string Id, string Name);

public enum TeamViewerApiError { Unauthorized, Forbidden, Http, Network, Format }

/// <summary>A failed API call. The message never contains the token.</summary>
public sealed class TeamViewerApiException(TeamViewerApiError error, string message, Exception? inner = null) : Exception(message, inner)
{
    public TeamViewerApiError Error { get; } = error;
}

/// <summary>
/// Reads devices and groups through the TeamViewer Web API with a script token (read rights for
/// "Computers &amp; Contacts" are enough). The token only goes into the Authorization header.
/// </summary>
public sealed class TeamViewerApiClient
{
    public static readonly Uri DefaultBaseAddress = new("https://webapi.teamviewer.com/api/v1/");

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient http;
    private readonly Uri baseAddress;

    public TeamViewerApiClient(HttpClient http, Uri? baseAddress = null)
    {
        this.http = http;
        this.baseAddress = baseAddress ?? DefaultBaseAddress;
    }

    public async Task<IReadOnlyList<TeamViewerDevice>> GetDevicesAsync(string token, CancellationToken cancellationToken = default)
    {
        DevicesDto dto = await GetAsync<DevicesDto>("devices", token, cancellationToken).ConfigureAwait(false);
        return [.. (dto.Devices ?? []).Where(d => d.RemoteControlId is { Length: > 0 }).Select(d => new TeamViewerDevice(
            d.DeviceId ?? "",
            StripPrefix(d.RemoteControlId!),
            d.Alias ?? "",
            d.Description ?? "",
            d.GroupId ?? "",
            d.OnlineState?.ToUpperInvariant() switch
            {
                "ONLINE" => OnlineStatus.Online,
                "OFFLINE" => OnlineStatus.Offline,
                _ => OnlineStatus.Unknown,
            }))];
    }

    public async Task<IReadOnlyList<TeamViewerGroup>> GetGroupsAsync(string token, CancellationToken cancellationToken = default)
    {
        GroupsDto dto = await GetAsync<GroupsDto>("groups", token, cancellationToken).ConfigureAwait(false);
        return [.. (dto.Groups ?? []).Where(g => g.Id is { Length: > 0 }).Select(g => new TeamViewerGroup(g.Id!, g.Name ?? ""))];
    }

    /// <summary>"r123456789" → "123456789"; the API prefixes remote-control IDs with "r".</summary>
    public static string StripPrefix(string remoteControlId) =>
        remoteControlId.Length > 1 && remoteControlId[0] is 'r' or 'R' && char.IsAsciiDigit(remoteControlId[1]) ? remoteControlId[1..] : remoteControlId;

    private async Task<T> GetAsync<T>(string path, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseAddress, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new TeamViewerApiException(TeamViewerApiError.Network, ex.Message, ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TeamViewerApiException(TeamViewerApiError.Network, "Timeout", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new TeamViewerApiException(TeamViewerApiError.Unauthorized, "401 Unauthorized");
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new TeamViewerApiException(TeamViewerApiError.Forbidden, "403 Forbidden");
            if (!response.IsSuccessStatusCode)
                throw new TeamViewerApiException(TeamViewerApiError.Http, $"{(int)response.StatusCode} {response.ReasonPhrase}");

            try
            {
                await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                return await JsonSerializer.DeserializeAsync<T>(body, Json, cancellationToken).ConfigureAwait(false)
                    ?? throw new TeamViewerApiException(TeamViewerApiError.Format, "Empty response");
            }
            catch (JsonException ex)
            {
                throw new TeamViewerApiException(TeamViewerApiError.Format, ex.Message, ex);
            }
        }
    }

    private sealed class DevicesDto
    {
        public List<DeviceDto>? Devices { get; set; }
    }

    private sealed class DeviceDto
    {
        [JsonPropertyName("remotecontrol_id")] public string? RemoteControlId { get; set; }
        [JsonPropertyName("device_id")] public string? DeviceId { get; set; }
        [JsonPropertyName("alias")] public string? Alias { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("groupid")] public string? GroupId { get; set; }
        [JsonPropertyName("online_state")] public string? OnlineState { get; set; }
    }

    private sealed class GroupsDto
    {
        public List<GroupDto>? Groups { get; set; }
    }

    private sealed class GroupDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
    }
}
