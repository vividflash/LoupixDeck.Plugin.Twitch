using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LoupixDeck.Plugin.Twitch.Twitch;

/// <summary>Chat settings fields the toggles care about.</summary>
public sealed record ChatSettings(bool SlowMode, int? SlowModeWaitTime, bool EmoteMode);

/// <summary>
/// Minimal Twitch Helix client acting as the signed-in broadcaster. Every call
/// goes through <see cref="SendAsync"/>: an expired token is refreshed first,
/// and a 401 triggers exactly one refresh + retry; a second 401 clears the
/// token and asks for a new sign-in. No background or validation traffic.
/// </summary>
public sealed class HelixClient
{
    public const string BaseUrl = "https://api.twitch.tv/helix/";
    public const int MaxChatMessageLength = 500;
    public const int MinSlowWait = 3;
    public const int MaxSlowWait = 120;

    private readonly HttpClient _http;
    private readonly TokenStore _store;
    private readonly TwitchAuth _auth;

    public HelixClient(HttpClient http, TokenStore store, TwitchAuth auth)
    {
        _http = http;
        _store = store;
        _auth = auth;
    }

    // ---------------- endpoints ----------------

    /// <summary>The signed-in account's user id; looked up once (GET /users) and cached with the token.</summary>
    public async Task<string> GetUserIdAsync(CancellationToken ct = default)
    {
        var token = _store.Load() ?? throw NotSignedIn();
        if (!string.IsNullOrEmpty(token.UserId)) return token.UserId;

        var body = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, BaseUrl + "users"), ct).ConfigureAwait(false);
        var (id, login) = ParseUser(body);

        var latest = _store.Load() ?? throw NotSignedIn();
        latest.UserId = id;
        latest.Login = login;
        _store.Save(latest);
        return id;
    }

    public async Task SendChatMessageAsync(string message, CancellationToken ct = default)
    {
        message = message.Trim();
        if (message.Length == 0) throw new ArgumentException("Chat message is empty.");
        if (message.Length > MaxChatMessageLength)
            throw new ArgumentException($"Chat message is {message.Length} characters, Twitch allows {MaxChatMessageLength}.");

        var id = await GetUserIdAsync(ct).ConfigureAwait(false);
        var payload = new JsonObject
        {
            ["broadcaster_id"] = id,
            ["sender_id"] = id,
            ["message"] = message
        };

        var body = await SendAsync(() => JsonRequest(HttpMethod.Post, BaseUrl + "chat/messages", payload), ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(body);
        var first = FirstData(doc);
        if (first is { } item && item.TryGetProperty("is_sent", out var sent) && sent.ValueKind == JsonValueKind.False)
        {
            var reason = item.TryGetProperty("drop_reason", out var dr) && dr.ValueKind == JsonValueKind.Object &&
                         dr.TryGetProperty("message", out var m)
                ? m.GetString()
                : "unknown reason";
            throw new TwitchApiException(200, $"Twitch dropped the message: {reason}");
        }
    }

    /// <summary>Creates a clip; returns the clip id. A 404 means the channel is offline.</summary>
    public async Task<string> CreateClipAsync(CancellationToken ct = default)
    {
        var id = await GetUserIdAsync(ct).ConfigureAwait(false);
        string body;
        try
        {
            body = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post,
                $"{BaseUrl}clips?broadcaster_id={Uri.EscapeDataString(id)}"), ct).ConfigureAwait(false);
        }
        catch (TwitchApiException ex) when (ex.StatusCode == 404)
        {
            throw new TwitchApiException(404, "Cannot clip: the channel is offline.");
        }

        using var doc = JsonDocument.Parse(body);
        return FirstData(doc) is { } item && item.TryGetProperty("id", out var clipId)
            ? clipId.GetString() ?? string.Empty
            : string.Empty;
    }

    /// <summary>Current viewer count, or null when the channel is not live.</summary>
    public async Task<int?> GetViewerCountAsync(CancellationToken ct = default)
    {
        var id = await GetUserIdAsync(ct).ConfigureAwait(false);
        var body = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get,
            $"{BaseUrl}streams?user_id={Uri.EscapeDataString(id)}"), ct).ConfigureAwait(false);
        return ParseViewerCount(body);
    }

    public async Task ClearChatAsync(CancellationToken ct = default)
    {
        var id = await GetUserIdAsync(ct).ConfigureAwait(false);
        await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete,
            $"{BaseUrl}moderation/chat?{BroadcasterAndModerator(id)}"), ct).ConfigureAwait(false);
    }

    public async Task<ChatSettings> GetChatSettingsAsync(CancellationToken ct = default)
    {
        var id = await GetUserIdAsync(ct).ConfigureAwait(false);
        var body = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get,
            $"{BaseUrl}chat/settings?{BroadcasterAndModerator(id)}"), ct).ConfigureAwait(false);
        return ParseChatSettings(body);
    }

    /// <summary>Flips slow mode (GET then PATCH). Returns the new state.</summary>
    public async Task<bool> ToggleSlowModeAsync(int defaultWaitSeconds, CancellationToken ct = default)
    {
        var current = await GetChatSettingsAsync(ct).ConfigureAwait(false);
        var patch = BuildSlowModePatch(current, defaultWaitSeconds);
        await PatchChatSettingsAsync(patch, ct).ConfigureAwait(false);
        return !current.SlowMode;
    }

    /// <summary>Flips emote-only mode (GET then PATCH). Returns the new state.</summary>
    public async Task<bool> ToggleEmoteOnlyAsync(CancellationToken ct = default)
    {
        var current = await GetChatSettingsAsync(ct).ConfigureAwait(false);
        await PatchChatSettingsAsync(BuildEmoteModePatch(current), ct).ConfigureAwait(false);
        return !current.EmoteMode;
    }

    private async Task PatchChatSettingsAsync(JsonObject patch, CancellationToken ct)
    {
        var id = await GetUserIdAsync(ct).ConfigureAwait(false);
        await SendAsync(() => JsonRequest(HttpMethod.Patch,
            $"{BaseUrl}chat/settings?{BroadcasterAndModerator(id)}", patch), ct).ConfigureAwait(false);
    }

    // ---------------- pure helpers (unit tested) ----------------

    internal static JsonObject BuildSlowModePatch(ChatSettings current, int defaultWaitSeconds)
    {
        if (current.SlowMode)
            return new JsonObject { ["slow_mode"] = false };

        return new JsonObject
        {
            ["slow_mode"] = true,
            ["slow_mode_wait_time"] = ClampSlowWait(defaultWaitSeconds)
        };
    }

    internal static JsonObject BuildEmoteModePatch(ChatSettings current) =>
        new() { ["emote_mode"] = !current.EmoteMode };

    internal static int ClampSlowWait(long seconds) => (int)Math.Clamp(seconds, MinSlowWait, MaxSlowWait);

    internal static string BroadcasterAndModerator(string id)
    {
        var e = Uri.EscapeDataString(id);
        return $"broadcaster_id={e}&moderator_id={e}";
    }

    internal static (string Id, string Login) ParseUser(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var item = FirstData(doc) ?? throw new InvalidOperationException("Twitch returned no user for this token.");
        var id = item.GetProperty("id").GetString() ?? throw new InvalidOperationException("Twitch user has no id.");
        var login = item.TryGetProperty("login", out var l) ? l.GetString() ?? string.Empty : string.Empty;
        return (id, login);
    }

    internal static int? ParseViewerCount(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (FirstData(doc) is not { } item) return null;
        if (item.TryGetProperty("type", out var t) && t.GetString() is { Length: > 0 } type && type != "live") return null;
        return item.TryGetProperty("viewer_count", out var v) && v.TryGetInt32(out var n) ? n : 0;
    }

    internal static ChatSettings ParseChatSettings(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var item = FirstData(doc) ?? throw new InvalidOperationException("Twitch returned no chat settings.");
        var slow = item.TryGetProperty("slow_mode", out var s) && s.ValueKind == JsonValueKind.True;
        int? wait = item.TryGetProperty("slow_mode_wait_time", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetInt32() : null;
        var emote = item.TryGetProperty("emote_mode", out var e) && e.ValueKind == JsonValueKind.True;
        return new ChatSettings(slow, wait, emote);
    }

    /// <summary>Extracts Twitch's "message" field from an error body, or a trimmed raw body.</summary>
    internal static string ErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "(empty response)";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("message", out var m) && m.GetString() is { Length: > 0 } msg)
                return msg;
        }
        catch (JsonException) { }

        return body.Length > 200 ? body[..200] : body;
    }

    private static JsonElement? FirstData(JsonDocument doc)
    {
        if (doc.RootElement.ValueKind == JsonValueKind.Object &&
            doc.RootElement.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Array &&
            data.GetArrayLength() > 0)
            return data[0];
        return null;
    }

    // ---------------- transport ----------------

    private static HttpRequestMessage JsonRequest(HttpMethod method, string url, JsonNode payload) =>
        new(method, url)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };

    private static TwitchAuthRequiredException NotSignedIn() =>
        new("Not signed in to Twitch. Open Plugins > Twitch and click 'Sign in with Twitch'.");

    /// <summary>
    /// Sends a Helix request (built fresh per attempt) and returns the body.
    /// Expired token: refresh first. 401: refresh once and retry once. A second
    /// 401 clears the stored token and throws <see cref="TwitchAuthRequiredException"/>.
    /// </summary>
    internal async Task<string> SendAsync(Func<HttpRequestMessage> build, CancellationToken ct)
    {
        var token = _store.Load() ?? throw NotSignedIn();

        if (token.ExpiresAtUtc <= DateTime.UtcNow.AddSeconds(30))
            token = await _auth.RefreshAsync(token.AccessToken, ct).ConfigureAwait(false);

        for (var attempt = 0; ; attempt++)
        {
            using var req = build();
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
            req.Headers.Add("Client-Id", token.ClientId);

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = resp.Content == null ? string.Empty : await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                if (attempt == 0)
                {
                    token = await _auth.RefreshAsync(token.AccessToken, ct).ConfigureAwait(false);
                    continue;
                }

                _store.Clear();
                throw new TwitchAuthRequiredException(
                    $"Twitch rejected the refreshed token ({ErrorMessage(body)}). Sign in again.");
            }

            if (!resp.IsSuccessStatusCode)
                throw new TwitchApiException((int)resp.StatusCode, ErrorMessage(body));

            return body;
        }
    }
}
