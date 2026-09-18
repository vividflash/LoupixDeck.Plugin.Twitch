using System.Net;
using System.Net.Sockets;
using LoupixDeck.Plugin.Twitch.Twitch;
using Xunit;

namespace LoupixDeck.Plugin.Twitch.Tests;

public class AuthAndRefreshTests
{
    [Fact]
    public async Task Unauthorized_triggers_one_refresh_then_retries_with_new_token()
    {
        var rig = new Rig();
        rig.Handler
            .Respond(HttpStatusCode.Unauthorized, """{"error":"Unauthorized","status":401,"message":"Invalid OAuth token"}""")
            .Respond(HttpStatusCode.OK, Rig.TokenJson("access-2", "refresh-2"))
            .Respond(HttpStatusCode.NoContent);

        await rig.Helix.ClearChatAsync();

        var reqs = rig.Handler.Requests;
        Assert.Equal(3, reqs.Count);
        Assert.Equal("access-1", reqs[0].Bearer);

        Assert.Equal(TwitchAuth.TokenEndpoint, reqs[1].Url.ToString());
        Assert.Contains("grant_type=refresh_token", reqs[1].Body);
        Assert.Contains("refresh_token=refresh-1", reqs[1].Body);
        Assert.Contains("client_id=" + Rig.ClientId, reqs[1].Body);
        Assert.Contains("client_secret=" + Rig.ClientSecret, reqs[1].Body);

        Assert.Equal("access-2", reqs[2].Bearer);
        var stored = rig.Store.Load()!;
        Assert.Equal("access-2", stored.AccessToken);
        Assert.Equal("refresh-2", stored.RefreshToken);
        Assert.Equal(Rig.UserId, stored.UserId); // account info survives refresh
    }

    [Fact]
    public async Task Second_unauthorized_after_refresh_clears_token_and_requires_sign_in()
    {
        var rig = new Rig();
        rig.Handler
            .Respond(HttpStatusCode.Unauthorized)
            .Respond(HttpStatusCode.OK, Rig.TokenJson("access-2", "refresh-2"))
            .Respond(HttpStatusCode.Unauthorized);

        await Assert.ThrowsAsync<TwitchAuthRequiredException>(() => rig.Helix.ClearChatAsync());

        Assert.Equal(3, rig.Handler.Requests.Count); // exactly one refresh, no loop
        Assert.False(rig.Store.HasToken);
    }

    [Fact]
    public async Task Rejected_refresh_token_clears_store_and_requires_sign_in()
    {
        var rig = new Rig();
        rig.Handler
            .Respond(HttpStatusCode.Unauthorized)
            .Respond(HttpStatusCode.BadRequest, """{"status":400,"message":"Invalid refresh token"}""");

        await Assert.ThrowsAsync<TwitchAuthRequiredException>(() => rig.Helix.ClearChatAsync());

        Assert.Equal(2, rig.Handler.Requests.Count);
        Assert.False(rig.Store.HasToken);
    }

    [Fact]
    public async Task Expired_token_is_refreshed_before_the_call()
    {
        var rig = new Rig(expiresAtUtc: DateTime.UtcNow.AddMinutes(-5));
        rig.Handler
            .Respond(HttpStatusCode.OK, Rig.TokenJson("access-2", "refresh-2"))
            .Respond(HttpStatusCode.NoContent);

        await rig.Helix.ClearChatAsync();

        Assert.Equal(TwitchAuth.TokenEndpoint, rig.Handler.Requests[0].Url.ToString());
        Assert.Equal("access-2", rig.Handler.Requests[1].Bearer);
    }

    [Fact]
    public async Task Refresh_keeps_old_refresh_token_when_response_has_none()
    {
        var rig = new Rig();
        rig.Handler.Respond(HttpStatusCode.OK, """{"access_token":"access-2","expires_in":100}""");

        var t = await rig.Auth.RefreshAsync("access-1");

        Assert.Equal("access-2", t.AccessToken);
        Assert.Equal("refresh-1", t.RefreshToken);
    }

    [Fact]
    public async Task Concurrent_refresh_with_stale_token_does_not_refresh_twice()
    {
        var rig = new Rig();
        rig.Handler.Respond(HttpStatusCode.OK, Rig.TokenJson("access-2", "refresh-2"));

        var first = await rig.Auth.RefreshAsync("access-1");
        var second = await rig.Auth.RefreshAsync("access-1"); // same stale token, already refreshed

        Assert.Equal("access-2", first.AccessToken);
        Assert.Equal("access-2", second.AccessToken);
        Assert.Single(rig.Handler.Requests);
    }

    [Fact]
    public async Task Code_exchange_posts_form_and_looks_up_account_once()
    {
        var rig = new Rig(signedIn: false);
        rig.Handler
            .Respond(HttpStatusCode.OK, Rig.TokenJson("access-9", "refresh-9"))
            .Respond(HttpStatusCode.OK, """{"data":[{"id":"777","login":"yourchannel"}]}""");

        var token = await rig.Auth.ExchangeCodeAsync(
            new AppCredentials(Rig.ClientId, Rig.ClientSecret), "the-code", "http://localhost:3000");

        var form = rig.Handler.Requests[0].Body;
        Assert.Contains("grant_type=authorization_code", form);
        Assert.Contains("code=the-code", form);
        Assert.Contains("redirect_uri=http%3A%2F%2Flocalhost%3A3000", form);
        Assert.Equal("access-9", rig.Handler.Requests[1].Bearer);
        Assert.Equal(Rig.ClientId, rig.Handler.Requests[1].ClientId);
        Assert.Equal("777", token.UserId);
        Assert.Equal("yourchannel", token.Login);
        Assert.Equal(Rig.ClientId, token.ClientId);
        Assert.Equal(["user:write:chat"], token.Scopes);
    }

    [Fact]
    public void Authorize_url_carries_client_redirect_state_and_all_scopes()
    {
        var url = TwitchAuth.BuildAuthorizeUrl("abc", TwitchAuth.RedirectUri(3000), "STATE1");

        Assert.StartsWith("https://id.twitch.tv/oauth2/authorize?response_type=code", url);
        Assert.Contains("client_id=abc", url);
        Assert.Contains("redirect_uri=http%3A%2F%2Flocalhost%3A3000", url);
        Assert.Contains("state=STATE1", url);
        Assert.Contains("scope=user%3Awrite%3Achat%20clips%3Aedit%20moderator%3Amanage%3Achat_messages%20moderator%3Amanage%3Achat_settings", url);
    }

    [Fact]
    public async Task Sign_in_fails_fast_with_clear_message_when_port_is_busy()
    {
        var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        try
        {
            var port = ((IPEndPoint)blocker.LocalEndpoint).Port;
            Assert.True(TwitchAuth.IsPortInUse(port));

            var rig = new Rig(signedIn: false);
            var browserOpened = false;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => rig.Auth.SignInAsync(port, _ => browserOpened = true));

            Assert.Contains($"Port {port} is already in use", ex.Message);
            Assert.False(browserOpened);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
            Assert.Empty(rig.Handler.Requests);
        }
        finally
        {
            blocker.Stop();
        }
    }

    [Fact]
    public async Task Sign_in_without_credentials_fails_before_listening()
    {
        var handler = new FakeHandler();
        var settings = new FakeSettings();
        var store = new TokenStore(settings, new FakeProtector());
        var auth = new TwitchAuth(new HttpClient(handler), store, () => new AppCredentials("", ""), new FakeLogger());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => auth.SignInAsync(3000, _ => true));
        Assert.Contains("Client ID", ex.Message);
    }
}
