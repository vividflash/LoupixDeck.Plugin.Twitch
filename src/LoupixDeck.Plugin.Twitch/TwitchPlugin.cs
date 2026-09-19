using System.Runtime.Versioning;
using LoupixDeck.Plugin.Twitch.Commands;
using LoupixDeck.Plugin.Twitch.Twitch;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Twitch;

/// <summary>
/// Entry point. Owns one HttpClient, the encrypted token store, the OAuth flow,
/// the Helix client and the lazily polled viewer-count cache. Everything acts
/// as the account that signed in (the broadcaster).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TwitchPlugin : LoupixPlugin, IPluginSettingsPage
{
    internal const string SettingClientId = "client_id";
    internal const string SettingClientSecret = "client_secret";
    internal const string SettingRedirectPort = "redirect_port";
    internal const string SettingSlowWait = "slow_mode_wait_seconds";
    internal const string SettingAdLength = "ad_length_seconds";
    internal const int DefaultRedirectPort = 3000;
    internal const int DefaultSlowWait = TwitchSteps.DefaultSlowModeWait;
    internal const int DefaultAdLength = TwitchSteps.DefaultAdLength;

    private IPluginHost _host = null!;
    private HttpClient _http = null!;
    private TokenStore _tokens = null!;
    private TwitchAuth _auth = null!;
    private HelixClient _helix = null!;
    private ViewerCountCache _viewers = null!;
    private List<IPluginCommand> _commands = [];

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "twitch",
        Name = "Twitch",
        Version = new Version(1, 2, 1),
        SdkVersion = new Version(1, 23, 0),
        Author = "vividflash",
        Description = "Send chat messages, create clips, run ads, set stream markers, clear chat, toggle slow and emote-only mode, and show the live viewer count."
    };

    public override void Initialize(IPluginHost host)
    {
        _host = host;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("LoupixDeck.Plugin.Twitch/1.1");

        _tokens = new TokenStore(host.Settings, new DpapiTokenProtector(), host.Logger);
        _auth = new TwitchAuth(_http, _tokens, ReadCredentials, host.Logger);
        _helix = new HelixClient(_http, _tokens, _auth);
        _viewers = new ViewerCountCache(
            ct => _helix.GetViewerCountAsync(ct),
            () => _host.RequestButtonRefresh(ViewerCountCommand.Name),
            host.Logger);

        _commands =
        [
            new SendChatMessageCommand(_helix, host.Logger),
            new CreateClipCommand(_helix, host.Logger),
            new ViewerCountCommand(_viewers, host.Logger),
            new ClearChatCommand(_helix, host.Logger),
            new ToggleSlowChatCommand(_helix, host.Logger, ReadSlowWait),
            new ToggleEmotesOnlyCommand(_helix, host.Logger),
            new RunCommercialCommand(_helix, host.Logger, ReadAdLength),
            new CreateStreamMarkerCommand(_helix, host.Logger)
        ];
    }

    public override IEnumerable<IPluginCommand> GetCommands() => _commands;

    public override IReadOnlyList<CommandGroupDescriptor> GetCommandGroups() =>
    [
        new CommandGroupDescriptor
        {
            Group = TwitchCommandBase.GroupName,
            Description = "Chat, clips, ads, markers and viewer count",
            Section = CommandGroupSection.Plugins
        }
    ];

    public override void Shutdown()
    {
        try { _http?.Dispose(); } catch { /* shutting down */ }
    }

    // ---------------- settings ----------------

    private AppCredentials ReadCredentials() => new(
        TwitchAuth.CleanCredential(_host.Settings.Get<string>(SettingClientId)),
        TwitchAuth.CleanCredential(_host.Settings.Get<string>(SettingClientSecret)));

    private int ReadPort()
    {
        var port = _host.Settings.Get<long>(SettingRedirectPort, DefaultRedirectPort);
        return port is > 0 and <= 65535 ? (int)port : DefaultRedirectPort;
    }

    private int ReadSlowWait() =>
        TwitchSteps.SnapSlowModeWait(_host.Settings.Get<long>(SettingSlowWait, DefaultSlowWait));

    private int ReadAdLength() =>
        TwitchSteps.SnapAdLength(_host.Settings.Get<long>(SettingAdLength, DefaultAdLength));

    /// <summary>
    /// Stores pasted credentials without stray whitespace/quotes, so the saved
    /// value is the one that gets used. Returns true when something changed.
    /// </summary>
    internal static bool NormalizeCredentials(IPluginSettings settings)
    {
        var changed = false;
        foreach (var key in new[] { SettingClientId, SettingClientSecret })
        {
            var raw = settings.Get<string>(key);
            if (raw == null) continue;
            var clean = TwitchAuth.CleanCredential(raw);
            if (clean == raw) continue;
            settings.Set(key, clean);
            changed = true;
        }

        if (changed) settings.Save();
        return changed;
    }

    public IReadOnlyList<PluginSettingDescriptor> SettingsSchema
    {
        get
        {
            var redirect = _host == null ? TwitchAuth.RedirectUri(DefaultRedirectPort) : TwitchAuth.RedirectUri(ReadPort());
            var signedInAs = _tokens?.Load() is { } t ? (t.Login.Length > 0 ? t.Login : "your account") : null;
            var creds = _host == null ? new AppCredentials("", "") : ReadCredentials();
            var credsEntered = creds.ClientId.Length > 0 || creds.ClientSecret.Length > 0;
            var credsProblem = credsEntered ? TwitchAuth.ValidateCredentials(creds) : null;
            var missingScopes = _tokens?.Load() is { Scopes.Length: > 0 } granted
                ? TwitchAuth.RequiredScopes.Where(s => !granted.Scopes.Contains(s)).ToArray()
                : [];

            return
            [
                new PluginSettingDescriptor
                {
                    Key = "__heading_app",
                    Label = "Twitch application",
                    Kind = PluginSettingKind.Heading,
                    Description = "Register an application at dev.twitch.tv/console/apps (Client Type: Confidential). " +
                                  $"Its OAuth Redirect URL must be exactly: {redirect}",
                    DefaultValue = string.Empty
                },
                new PluginSettingDescriptor
                {
                    Key = SettingClientId,
                    Label = "Client ID",
                    Kind = PluginSettingKind.Password,
                    DefaultValue = string.Empty
                },
                new PluginSettingDescriptor
                {
                    Key = SettingClientSecret,
                    Label = "Client Secret",
                    Kind = PluginSettingKind.Password,
                    DefaultValue = string.Empty
                },
                new PluginSettingDescriptor
                {
                    Key = SettingRedirectPort,
                    Label = "Redirect port",
                    Kind = PluginSettingKind.Number,
                    Description = $"Local port used only during sign-in. Redirect URL: {redirect}",
                    DefaultValue = (long)DefaultRedirectPort
                },
                new PluginSettingDescriptor
                {
                    Key = "__heading_chat",
                    Label = "Chat",
                    Kind = PluginSettingKind.Heading,
                    DefaultValue = string.Empty
                },
                new PluginSettingDescriptor
                {
                    Key = SettingSlowWait,
                    Label = "Slow mode wait (seconds)",
                    Kind = PluginSettingKind.Number,
                    Description = "Used when 'Toggle slow chat' turns slow mode on. Twitch offers " +
                                  $"{TwitchSteps.Describe(TwitchSteps.SlowModeWaitSeconds)} seconds; other values " +
                                  $"snap to the nearest of these (default {DefaultSlowWait}).",
                    DefaultValue = (long)DefaultSlowWait
                },
                new PluginSettingDescriptor
                {
                    Key = "__heading_ads",
                    Label = "Ads",
                    Kind = PluginSettingKind.Heading,
                    DefaultValue = string.Empty
                },
                new PluginSettingDescriptor
                {
                    Key = SettingAdLength,
                    Label = "Ad length (seconds)",
                    Kind = PluginSettingKind.Number,
                    Description = $"Used by 'Run Ad'. Twitch offers {TwitchSteps.Describe(TwitchSteps.AdLengthSeconds)} " +
                                  $"seconds; other values snap to the nearest of these (default {DefaultAdLength}).",
                    DefaultValue = (long)DefaultAdLength
                },
                new PluginSettingDescriptor
                {
                    Key = "__heading_account",
                    Label = "Account",
                    Kind = PluginSettingKind.Heading,
                    Description = AccountStatus(signedInAs, credsProblem, missingScopes),
                    DefaultValue = string.Empty
                }
            ];
        }
    }

    internal static string AccountStatus(string? signedInAs, string? credsProblem, IReadOnlyCollection<string> missingScopes)
    {
        var text = signedInAs != null
            ? $"Signed in as {signedInAs}. The token is stored encrypted for your Windows user."
            : "Not signed in. Fill in Client ID and Client Secret, then click 'Sign in with Twitch'.";
        if (signedInAs != null && missingScopes.Count > 0)
            text += $" This sign-in is missing permissions for newer commands ({string.Join(", ", missingScopes)}): click 'Sign in again'.";
        if (credsProblem != null)
            text += " Problem: " + credsProblem;
        return text;
    }

    public IReadOnlyList<PluginSettingAction> SettingsActions =>
        _tokens?.HasToken == true
            ? [SignInAction("Sign in again"), SignOutAction()]
            : [SignInAction("Sign in with Twitch")];

    public void OnSettingsSaved()
    {
        if (_host != null && NormalizeCredentials(_host.Settings))
            _host.Logger.Info("Twitch: removed spaces/quotes from the pasted Client ID or Client Secret.");
        _viewers?.Invalidate();
    }

    private PluginSettingAction SignInAction(string label) => new()
    {
        Label = label,
        Invoke = async () =>
        {
            try
            {
                var result = await _auth.SignInAsync(ReadPort(), url => _host.OpenBrowser(url));
                _host.Logger.Info($"Twitch: {result}");
                return result;
            }
            catch (InvalidOperationException ex)
            {
                _host.Logger.Warn($"Twitch sign-in failed: {ex.Message}");
                throw; // host shows it as a failure on the settings page
            }
            catch (Exception ex)
            {
                _host.Logger.Error("Twitch sign-in failed", ex);
                throw new InvalidOperationException(ex.Message, ex);
            }
            finally
            {
                _viewers.Invalidate();
            }
        }
    };

    private PluginSettingAction SignOutAction() => new()
    {
        Label = "Sign out",
        Invoke = async () =>
        {
            await _auth.SignOutAsync();
            _viewers.Invalidate();
            return "Signed out, token deleted.";
        }
    };
}
