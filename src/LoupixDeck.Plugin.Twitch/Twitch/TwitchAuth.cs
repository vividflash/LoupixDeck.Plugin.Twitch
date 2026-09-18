using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Twitch.Twitch;

/// <summary>The user's own Twitch application, entered on the settings page.</summary>
public sealed record AppCredentials(string ClientId, string ClientSecret);

/// <summary>
/// Twitch OAuth authorization-code flow with a loopback redirect, plus token
/// refresh and revoke. The loopback listener only exists for the duration of a
/// single <see cref="SignInAsync"/> call.
/// </summary>
public sealed class TwitchAuth
{
    public const string AuthorizeEndpoint = "https://id.twitch.tv/oauth2/authorize";
    public const string TokenEndpoint = "https://id.twitch.tv/oauth2/token";
    public const string RevokeEndpoint = "https://id.twitch.tv/oauth2/revoke";

    public static readonly IReadOnlyList<string> RequiredScopes =
    [
        "user:write:chat",
        "clips:edit",
        "moderator:manage:chat_messages",
        "moderator:manage:chat_settings"
    ];

    internal static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(3);

    private readonly HttpClient _http;
    private readonly TokenStore _store;
    private readonly Func<AppCredentials> _credentials;
    private readonly IPluginLogger _logger;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public TwitchAuth(HttpClient http, TokenStore store, Func<AppCredentials> credentials, IPluginLogger logger)
    {
        _http = http;
        _store = store;
        _credentials = credentials;
        _logger = logger;
    }

    public static string RedirectUri(int port) => $"http://localhost:{port}";

    public static string BuildAuthorizeUrl(string clientId, string redirectUri, string state)
    {
        var scope = Uri.EscapeDataString(string.Join(' ', RequiredScopes));
        return $"{AuthorizeEndpoint}?response_type=code" +
               $"&client_id={Uri.EscapeDataString(clientId)}" +
               $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
               $"&scope={scope}" +
               $"&state={Uri.EscapeDataString(state)}" +
               "&force_verify=true";
    }

    /// <summary>True when something on this machine already listens on the TCP port.</summary>
    public static bool IsPortInUse(int port)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Any(ep => ep.Port == port);
        }
        catch
        {
            return false; // Can't tell; HttpListener.Start below still reports a real conflict.
        }
    }

    /// <summary>
    /// Runs the interactive sign-in. Returns a short success text; throws
    /// <see cref="InvalidOperationException"/> with a user-facing message on
    /// failure (the host shows exceptions from settings actions as failures).
    /// </summary>
    public async Task<string> SignInAsync(int port, Func<string, bool> openBrowser, CancellationToken ct = default)
    {
        var creds = _credentials();
        if (string.IsNullOrWhiteSpace(creds.ClientId)) throw new InvalidOperationException("Client ID is empty.");
        if (string.IsNullOrWhiteSpace(creds.ClientSecret)) throw new InvalidOperationException("Client Secret is empty.");
        if (port is <= 0 or > 65535) throw new InvalidOperationException($"Redirect port {port} is not a valid port.");

        var redirectUri = RedirectUri(port);

        if (IsPortInUse(port))
            throw new InvalidOperationException(
                $"Port {port} is already in use by another program. Close it and try again, " +
                "or pick another redirect port here AND in your Twitch app.");

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not listen on port {port}: {ex.Message}");
        }

        try
        {
            var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            if (!openBrowser(BuildAuthorizeUrl(creds.ClientId.Trim(), redirectUri, state)))
                throw new InvalidOperationException("Could not open the web browser.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(SignInTimeout);

            string code;
            while (true)
            {
                var contextTask = listener.GetContextAsync();
                var finished = await Task.WhenAny(contextTask, Task.Delay(Timeout.Infinite, timeout.Token))
                    .ConfigureAwait(false);
                if (finished != contextTask)
                    throw new InvalidOperationException("Timed out waiting for the Twitch sign-in to finish.");

                var context = await contextTask.ConfigureAwait(false);
                var query = context.Request.QueryString;
                var error = query.Get("error");
                var returnedCode = query.Get("code");

                if (error == null && returnedCode == null)
                {
                    // Favicon or some other stray request: answer and keep waiting.
                    await WriteResponse(context, 404, "Waiting for Twitch sign-in...").ConfigureAwait(false);
                    continue;
                }

                if (!string.Equals(query.Get("state"), state, StringComparison.Ordinal))
                {
                    await WriteResponse(context, 400, "Sign-in state mismatch. Start again from LoupixDeck.").ConfigureAwait(false);
                    throw new InvalidOperationException("Sign-in state mismatch (stale browser tab?). Try again.");
                }

                if (error != null)
                {
                    var description = query.Get("error_description") ?? error;
                    await WriteResponse(context, 200, $"Twitch sign-in failed: {description}. You can close this tab.").ConfigureAwait(false);
                    throw new InvalidOperationException($"Twitch refused the sign-in: {description}");
                }

                await WriteResponse(context, 200, "Twitch sign-in complete. You can close this tab and return to LoupixDeck.").ConfigureAwait(false);
                code = returnedCode!;
                break;
            }

            var token = await ExchangeCodeAsync(creds, code, redirectUri, ct).ConfigureAwait(false);
            _store.Save(token);
            return token.Login.Length > 0 ? $"Signed in as {token.Login}." : "Signed in.";
        }
        finally
        {
            try { listener.Stop(); } catch { /* best effort */ }
        }
    }

    /// <summary>Exchanges an authorization code for tokens and looks up the account once.</summary>
    internal async Task<TokenData> ExchangeCodeAsync(AppCredentials creds, string code, string redirectUri, CancellationToken ct = default)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = creds.ClientId.Trim(),
            ["client_secret"] = creds.ClientSecret.Trim(),
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectUri
        });

        using var resp = await _http.PostAsync(TokenEndpoint, content, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Token exchange failed ({(int)resp.StatusCode}): {HelixClient.ErrorMessage(body)}");

        var token = ParseTokenResponse(body, creds.ClientId.Trim());
        var (userId, login) = await FetchUserAsync(token.AccessToken, token.ClientId, ct).ConfigureAwait(false);
        token.UserId = userId;
        token.Login = login;
        return token;
    }

    /// <summary>
    /// Refreshes the stored grant. Serialized: if another caller refreshed while
    /// we waited (the stored access token differs from <paramref name="staleAccessToken"/>),
    /// the new token is returned without a second refresh. A rejected refresh
    /// token clears the store and throws <see cref="TwitchAuthRequiredException"/>.
    /// </summary>
    public async Task<TokenData> RefreshAsync(string staleAccessToken, CancellationToken ct = default)
    {
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = _store.Load() ?? throw new TwitchAuthRequiredException("Not signed in to Twitch.");
            if (!string.Equals(current.AccessToken, staleAccessToken, StringComparison.Ordinal))
                return current;

            if (string.IsNullOrEmpty(current.RefreshToken))
            {
                _store.Clear();
                throw new TwitchAuthRequiredException("Twitch session expired, sign in again.");
            }

            var creds = _credentials();
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = creds.ClientId.Trim(),
                ["client_secret"] = creds.ClientSecret.Trim(),
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = current.RefreshToken
            });

            using var resp = await _http.PostAsync(TokenEndpoint, content, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (resp.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _logger.Warn($"Twitch: token refresh rejected ({(int)resp.StatusCode}): {HelixClient.ErrorMessage(body)}");
                _store.Clear();
                throw new TwitchAuthRequiredException("Twitch session expired, sign in again.");
            }

            if (!resp.IsSuccessStatusCode)
                throw new TwitchApiException((int)resp.StatusCode, $"Token refresh failed: {HelixClient.ErrorMessage(body)}");

            var refreshed = ParseTokenResponse(body, current.ClientId);
            if (string.IsNullOrEmpty(refreshed.RefreshToken)) refreshed.RefreshToken = current.RefreshToken;
            refreshed.UserId = current.UserId;
            refreshed.Login = current.Login;
            _store.Save(refreshed);
            _logger.Info("Twitch: access token refreshed.");
            return refreshed;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>Best-effort revoke at Twitch, then always forgets the local token.</summary>
    public async Task SignOutAsync(CancellationToken ct = default)
    {
        var current = _store.Load();
        _store.Clear();
        if (current == null || string.IsNullOrEmpty(current.ClientId)) return;

        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = current.ClientId,
                ["token"] = current.AccessToken
            });
            using var resp = await _http.PostAsync(RevokeEndpoint, content, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Twitch: revoking the token failed ({ex.Message}); the local copy is deleted anyway.");
        }
    }

    internal static TokenData ParseTokenResponse(string json, string clientId)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var access = root.TryGetProperty("access_token", out var a) ? a.GetString() : null;
        if (string.IsNullOrEmpty(access))
            throw new InvalidOperationException("Twitch token response had no access_token.");

        var expiresIn = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var secs) ? secs : 3600;
        var scopes = root.TryGetProperty("scope", out var s) && s.ValueKind == JsonValueKind.Array
            ? s.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(x => x.Length > 0).ToArray()
            : [];

        return new TokenData
        {
            AccessToken = access,
            RefreshToken = root.TryGetProperty("refresh_token", out var r) ? r.GetString() ?? string.Empty : string.Empty,
            ExpiresAtUtc = DateTime.UtcNow.AddSeconds(expiresIn),
            Scopes = scopes,
            ClientId = clientId
        };
    }

    private async Task<(string Id, string Login)> FetchUserAsync(string accessToken, string clientId, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, HelixClient.BaseUrl + "users");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        req.Headers.Add("Client-Id", clientId);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Looking up the signed-in account failed ({(int)resp.StatusCode}): {HelixClient.ErrorMessage(body)}");
        return HelixClient.ParseUser(body);
    }

    private static async Task WriteResponse(HttpListenerContext context, int status, string message)
    {
        try
        {
            var html = "<!doctype html><html><head><meta charset='utf-8'><title>LoupixDeck Twitch</title></head>" +
                       "<body style='font-family:sans-serif;text-align:center;padding:3em'><h2>" +
                       WebUtility.HtmlEncode(message) + "</h2></body></html>";
            var bytes = Encoding.UTF8.GetBytes(html);
            context.Response.StatusCode = status;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            context.Response.OutputStream.Close();
        }
        catch
        {
            // The browser tab may already be gone; the sign-in result does not depend on it.
        }
    }
}
