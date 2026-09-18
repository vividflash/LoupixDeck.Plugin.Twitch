using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Twitch.Twitch;

/// <summary>The OAuth grant plus the account it belongs to.</summary>
public sealed class TokenData
{
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }
    public string[] Scopes { get; set; } = [];

    /// <summary>Client id the grant was issued to. A grant only refreshes with the same app.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Twitch user id of the signed-in account, looked up once and cached.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Login name of the signed-in account (shown on the settings page).</summary>
    public string Login { get; set; } = string.Empty;
}

/// <summary>Encrypts/decrypts the serialized token blob.</summary>
public interface ITokenProtector
{
    string Protect(string plainText);
    string Unprotect(string protectedText);
}

/// <summary>
/// Windows DPAPI, CurrentUser scope: only the same Windows account on the same
/// machine can decrypt the blob, so copying settings.json elsewhere is useless.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiTokenProtector : ITokenProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LoupixDeck.Plugin.Twitch.token.v1");

    public string Protect(string plainText)
    {
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plainText), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public string Unprotect(string protectedText)
    {
        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedText), Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }
}

/// <summary>
/// Persists the token as an encrypted base64 blob inside the plugin's own
/// settings store (plugins/twitch/settings.json). That file is the one the host
/// preserves across plugin updates, so a separate token file would be lost on
/// every update. Decrypted data is cached in memory after the first read.
/// </summary>
public sealed class TokenStore
{
    internal const string SettingsKey = "twitch_token_v1";

    private readonly IPluginSettings _settings;
    private readonly ITokenProtector _protector;
    private readonly IPluginLogger? _logger;
    private readonly object _gate = new();
    private TokenData? _cached;
    private bool _loaded;

    public TokenStore(IPluginSettings settings, ITokenProtector protector, IPluginLogger? logger = null)
    {
        _settings = settings;
        _protector = protector;
        _logger = logger;
    }

    public bool HasToken => Load() != null;

    public TokenData? Load()
    {
        lock (_gate)
        {
            if (_loaded) return _cached;
            _loaded = true;

            var blob = _settings.Get<string>(SettingsKey);
            if (string.IsNullOrEmpty(blob)) return null;

            try
            {
                _cached = JsonSerializer.Deserialize<TokenData>(_protector.Unprotect(blob));
                if (_cached != null && string.IsNullOrEmpty(_cached.AccessToken)) _cached = null;
            }
            catch (Exception ex)
            {
                // Different Windows user/machine, or a corrupt value: treat as signed out.
                _logger?.Warn($"Twitch: stored token could not be decrypted, sign in again ({ex.GetType().Name}).");
                _cached = null;
            }

            return _cached;
        }
    }

    public void Save(TokenData data)
    {
        lock (_gate)
        {
            _settings.Set(SettingsKey, _protector.Protect(JsonSerializer.Serialize(data)));
            _settings.Save();
            _cached = data;
            _loaded = true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _settings.Remove(SettingsKey);
            _settings.Save();
            _cached = null;
            _loaded = true;
        }
    }
}
