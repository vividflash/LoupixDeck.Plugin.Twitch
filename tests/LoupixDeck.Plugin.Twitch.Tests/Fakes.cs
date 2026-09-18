using System.Net;
using System.Text;
using LoupixDeck.Plugin.Twitch.Twitch;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Twitch.Tests;

/// <summary>One captured outgoing request (body read eagerly, before disposal).</summary>
public sealed record SentRequest(HttpMethod Method, Uri Url, string? Bearer, string? ClientId, string Body);

/// <summary>Scripted HttpMessageHandler: no real network, answers from a queue.</summary>
public sealed class FakeHandler : HttpMessageHandler
{
    private readonly Queue<Func<SentRequest, HttpResponseMessage>> _responses = new();
    public List<SentRequest> Requests { get; } = [];

    public FakeHandler Respond(HttpStatusCode status, string body = "")
    {
        _responses.Enqueue(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
        var sent = new SentRequest(
            request.Method,
            request.RequestUri!,
            request.Headers.Authorization?.Parameter,
            request.Headers.TryGetValues("Client-Id", out var ids) ? ids.FirstOrDefault() : null,
            body);
        Requests.Add(sent);

        if (_responses.Count == 0)
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        return _responses.Dequeue()(sent);
    }
}

public sealed class FakeSettings : IPluginSettings
{
    public Dictionary<string, object?> Values { get; } = new();
    public int SaveCount { get; private set; }

    public T? Get<T>(string key, T? defaultValue = default) =>
        Values.TryGetValue(key, out var v) && v is T t ? t : defaultValue;

    public void Set<T>(string key, T value) => Values[key] = value;
    public bool Contains(string key) => Values.ContainsKey(key);
    public void Remove(string key) => Values.Remove(key);
    public IEnumerable<string> Keys => Values.Keys;
    public void Save() => SaveCount++;
}

/// <summary>Reversible, obviously-not-plaintext stand-in for DPAPI.</summary>
public sealed class FakeProtector : ITokenProtector
{
    public string Protect(string plainText) => "enc:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(new string(plainText.Reverse().ToArray())));

    public string Unprotect(string protectedText)
    {
        if (!protectedText.StartsWith("enc:")) throw new FormatException("not protected");
        return new string(Encoding.UTF8.GetString(Convert.FromBase64String(protectedText[4..])).Reverse().ToArray());
    }
}

public sealed class FakeLogger : IPluginLogger
{
    public List<string> Lines { get; } = [];
    public void Info(string message) => Lines.Add("I " + message);
    public void Warn(string message) => Lines.Add("W " + message);
    public void Error(string message, Exception? exception = null) => Lines.Add("E " + message + " " + exception?.Message);
}

public sealed class FakeHost : IPluginHost
{
    public FakeLogger FakeLog { get; } = new();
    public FakeSettings FakeSettings { get; } = new();
    public List<(int Slot, string Text)> Overlays { get; } = [];
    public List<string> Refreshes { get; } = [];

    public IPluginLogger Logger => FakeLog;
    public IPluginSettings Settings => FakeSettings;
    public FolderGridInfo FolderGrid => new(5, 3, 0);
    public DeviceInfo? ActiveDevice => null;
    public bool IsInExclusiveMode => false;

    public void RequestButtonRefresh(string commandName) => Refreshes.Add(commandName);
    public void ExecuteCommand(string command) { }
    public void OpenFolder(IFolderProvider provider) { }
    public bool OpenBrowser(string url) => false;
    public void OverlayTouchText(int slot, string text, TimeSpan duration) => Overlays.Add((slot, text));
    public int GetTouchSlotForRotary(int rotaryIndex) => rotaryIndex + 10;
    public bool RequestExclusiveMode(IExclusiveModeProvider provider) => false;
    public void ReleaseExclusiveMode(IExclusiveModeProvider provider) { }
    public IFullDisplayRenderSession? RequestFullDisplayRenderer(IFullDisplayRenderer renderer) => null;
    public IReadOnlyList<string> GetButtonStates(string commandName) => [];
    public string? GetActiveButtonState(string commandName) => null;
    public bool SetActiveButtonState(string commandName, string stateNameOrId) => false;
}

/// <summary>Wires store + auth + helix around a fake handler, optionally pre-signed-in.</summary>
public sealed class Rig
{
    public const string ClientId = "test-client-id";
    public const string ClientSecret = "test-client-secret";
    public const string UserId = "12345";

    public FakeHandler Handler { get; } = new();
    public FakeSettings Settings { get; } = new();
    public FakeLogger Logger { get; } = new();
    public TokenStore Store { get; }
    public TwitchAuth Auth { get; }
    public HelixClient Helix { get; }

    public Rig(bool signedIn = true, bool withUserId = true, DateTime? expiresAtUtc = null)
    {
        var http = new HttpClient(Handler);
        Store = new TokenStore(Settings, new FakeProtector(), Logger);
        Auth = new TwitchAuth(http, Store, () => new AppCredentials(ClientId, ClientSecret), Logger);
        Helix = new HelixClient(http, Store, Auth);

        if (signedIn)
        {
            Store.Save(new TokenData
            {
                AccessToken = "access-1",
                RefreshToken = "refresh-1",
                ExpiresAtUtc = expiresAtUtc ?? DateTime.UtcNow.AddHours(2),
                ClientId = ClientId,
                UserId = withUserId ? UserId : string.Empty,
                Login = withUserId ? "yourchannel" : string.Empty
            });
        }
    }

    public static string TokenJson(string access, string refresh) =>
        $$"""{"access_token":"{{access}}","refresh_token":"{{refresh}}","expires_in":14400,"scope":["user:write:chat"],"token_type":"bearer"}""";
}
