using System.Net;
using System.Text.Json;
using LoupixDeck.Plugin.Twitch.Commands;
using LoupixDeck.Plugin.Twitch.Twitch;
using LoupixDeck.PluginSdk;
using Xunit;

namespace LoupixDeck.Plugin.Twitch.Tests;

public class RunCommercialTests
{
    private const string Started = """{"data":[{"length":60,"message":"","retry_after":480}]}""";

    private static JsonElement Json(string body) => JsonDocument.Parse(body).RootElement;

    [Fact]
    public async Task Posts_broadcaster_and_length_and_returns_status()
    {
        var rig = new Rig();
        rig.Handler.Respond(HttpStatusCode.OK, Started);

        var result = await rig.Helix.RunCommercialAsync(60);

        var req = Assert.Single(rig.Handler.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://api.twitch.tv/helix/channels/commercial", req.Url.ToString());
        Assert.Equal("access-1", req.Bearer);
        Assert.Equal(Rig.UserId, Json(req.Body).GetProperty("broadcaster_id").GetString());
        Assert.Equal(60, Json(req.Body).GetProperty("length").GetInt32());
        Assert.Equal(new CommercialResult(60, 480, ""), result);
    }

    [Theory]
    [InlineData(0, 30)]     // below min
    [InlineData(1, 30)]
    [InlineData(30, 30)]    // exact
    [InlineData(150, 150)]
    [InlineData(180, 180)]
    [InlineData(240, 180)]  // above max (Helix caps a request at 180 s)
    [InlineData(500, 180)]
    [InlineData(70, 60)]    // between, closer to 60
    [InlineData(80, 90)]    // between, closer to 90
    [InlineData(45, 30)]    // tie: lower
    [InlineData(105, 90)]   // tie: lower
    public async Task Length_snaps_to_twitch_ad_lengths(int input, int sent)
    {
        var rig = new Rig();
        rig.Handler.Respond(HttpStatusCode.OK, Started);
        await rig.Helix.RunCommercialAsync(input);
        Assert.Equal(sent, Json(rig.Handler.Requests[0].Body).GetProperty("length").GetInt32());
    }

    [Fact]
    public async Task Not_live_gives_readable_offline_error()
    {
        var rig = new Rig();
        rig.Handler.Respond(HttpStatusCode.BadRequest,
            """{"error":"Bad Request","status":400,"message":"To start a commercial, the broadcaster must be streaming live."}""");

        var ex = await Assert.ThrowsAsync<TwitchApiException>(() => rig.Helix.RunCommercialAsync(30));
        Assert.Equal("Cannot run an ad: the channel is not live.", ex.Message);
        Assert.Equal("Offline", ex.Feedback);
    }

    [Fact]
    public async Task Cooldown_after_a_started_ad_says_how_long_is_left()
    {
        var rig = new Rig();
        rig.Handler
            .Respond(HttpStatusCode.OK, Started)
            .Respond(HttpStatusCode.TooManyRequests,
                """{"error":"Too Many Requests","status":429,"message":"The broadcaster may not run another commercial until the cooldown period expires."}""");

        await rig.Helix.RunCommercialAsync(60);
        rig.Now = rig.Now.AddSeconds(100); // 480 - 100 = 380 s left

        var ex = await Assert.ThrowsAsync<TwitchApiException>(() => rig.Helix.RunCommercialAsync(60));
        Assert.Equal(429, ex.StatusCode);
        Assert.Contains("cooldown", ex.Message);
        Assert.Contains("6 min 20 s", ex.Message);
        Assert.Equal("Cooldown", ex.Feedback);
    }

    [Fact]
    public async Task Cooldown_reported_as_400_without_known_retry_is_still_readable()
    {
        var rig = new Rig();
        rig.Handler.Respond(HttpStatusCode.BadRequest,
            """{"status":400,"message":"The broadcaster may not run another commercial until the cooldown period expires."}""");

        var ex = await Assert.ThrowsAsync<TwitchApiException>(() => rig.Helix.RunCommercialAsync(30));
        Assert.Contains("Try again later", ex.Message);
        Assert.Equal("Cooldown", ex.Feedback);
    }

    [Fact]
    public async Task Token_without_commercial_scope_asks_for_new_sign_in_without_a_request()
    {
        var rig = new Rig(scopes: ["user:write:chat", "clips:edit"]);

        var ex = await Assert.ThrowsAsync<TwitchAuthRequiredException>(() => rig.Helix.RunCommercialAsync(30));
        Assert.Contains("channel:edit:commercial", ex.Message);
        Assert.Contains("Sign in again", ex.Message);
        Assert.Empty(rig.Handler.Requests);
        Assert.NotNull(rig.Store.Load()); // other commands keep working
    }

    [Fact]
    public async Task Twitch_missing_scope_401_does_not_refresh_or_clear_the_token()
    {
        var rig = new Rig(); // scopes unknown, so Twitch decides
        rig.Handler.Respond(HttpStatusCode.Unauthorized,
            """{"error":"Unauthorized","status":401,"message":"Missing scope: channel:edit:commercial"}""");

        var ex = await Assert.ThrowsAsync<TwitchAuthRequiredException>(() => rig.Helix.RunCommercialAsync(30));
        Assert.Contains("Sign in again", ex.Message);
        Assert.Single(rig.Handler.Requests);
        Assert.NotNull(rig.Store.Load());
    }

    [Fact]
    public async Task Command_uses_length_from_settings_and_flashes_result_on_dial()
    {
        var rig = new Rig(scopes: [.. TwitchAuth.RequiredScopes]);
        rig.Handler.Respond(HttpStatusCode.OK, """{"data":[{"length":90,"message":"","retry_after":480}]}""");
        var host = new FakeHost();

        await new RunCommercialCommand(rig.Helix, rig.Logger, () => 90)
            .Execute(new CommandContext { Parameters = [], Target = ButtonTargets.RotaryEncoder, SourceIndex = 0, Host = host });

        Assert.Equal(90, Json(rig.Handler.Requests[0].Body).GetProperty("length").GetInt32());
        Assert.Equal((10, "Ad 90s"), Assert.Single(host.Overlays));
    }

    [Fact]
    public async Task Command_shows_offline_feedback_when_not_live()
    {
        var rig = new Rig();
        rig.Handler.Respond(HttpStatusCode.BadRequest, """{"status":400,"message":"To start a commercial, the broadcaster must be streaming live."}""");
        var host = new FakeHost();

        await new RunCommercialCommand(rig.Helix, rig.Logger, () => 30)
            .Execute(new CommandContext { Parameters = [], Target = ButtonTargets.RotaryEncoder, SourceIndex = 1, Host = host });

        Assert.Equal((11, "Offline"), Assert.Single(host.Overlays));
        // The button badge is the feedback for this command; it does not also log.
        Assert.DoesNotContain(rig.Logger.Lines, l => l.StartsWith("W Twitch.RunCommercial"));
        Assert.DoesNotContain(rig.Logger.Lines, l => l.StartsWith("E Twitch.RunCommercial"));
    }
}

public class StreamMarkerTests
{
    [Fact]
    public async Task Posts_user_id_without_description()
    {
        var rig = new Rig();
        rig.Handler.Respond(HttpStatusCode.OK,
            """{"data":[{"id":"marker-1","created_at":"2026-09-18T20:00:00Z","description":"","position_seconds":244}]}""");

        var id = await rig.Helix.CreateStreamMarkerAsync();

        var req = Assert.Single(rig.Handler.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://api.twitch.tv/helix/streams/markers", req.Url.ToString());
        var body = JsonDocument.Parse(req.Body).RootElement;
        Assert.Equal(Rig.UserId, body.GetProperty("user_id").GetString());
        Assert.False(body.TryGetProperty("description", out _));
        Assert.Equal("marker-1", id);
    }

    [Fact]
    public async Task Not_live_says_stream_offline_no_marker()
    {
        var rig = new Rig();
        rig.Handler.Respond(HttpStatusCode.NotFound, """{"error":"Not Found","status":404,"message":"user is not streaming live"}""");

        var ex = await Assert.ThrowsAsync<TwitchApiException>(() => rig.Helix.CreateStreamMarkerAsync());
        Assert.StartsWith("Stream offline, no marker", ex.Message);
        Assert.Equal("Offline", ex.Feedback);
    }

    [Fact]
    public async Task Token_without_broadcast_scope_asks_for_new_sign_in()
    {
        var rig = new Rig(scopes: ["user:write:chat", "channel:edit:commercial"]);
        var ex = await Assert.ThrowsAsync<TwitchAuthRequiredException>(() => rig.Helix.CreateStreamMarkerAsync());
        Assert.Contains("channel:manage:broadcast", ex.Message);
        Assert.Empty(rig.Handler.Requests);
    }

    [Fact]
    public async Task Description_over_140_chars_is_rejected_without_a_request()
    {
        var rig = new Rig();
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Helix.CreateStreamMarkerAsync(new string('m', 141)));
        Assert.Empty(rig.Handler.Requests);
    }

    [Fact]
    public async Task Command_flashes_marked_on_dial()
    {
        var rig = new Rig();
        rig.Handler.Respond(HttpStatusCode.OK, """{"data":[{"id":"m2"}]}""");
        var host = new FakeHost();

        await new CreateStreamMarkerCommand(rig.Helix, rig.Logger)
            .Execute(new CommandContext { Parameters = [], Target = ButtonTargets.RotaryEncoder, SourceIndex = 0, Host = host });

        Assert.Equal((10, "Marked"), Assert.Single(host.Overlays));
    }
}

public class CredentialValidationTests
{
    private const string GoodId = "abcdefghij0123456789klmnopqrst";
    private const string GoodSecret = "0123456789abcdefghij0123456789";

    [Fact]
    public void Real_shaped_values_pass()
    {
        Assert.Null(TwitchAuth.ValidateCredentials(new AppCredentials(GoodId, GoodSecret)));
    }

    [Fact]
    public void Pasted_whitespace_and_quotes_are_removed()
    {
        Assert.Equal(GoodId, TwitchAuth.CleanCredential("  \"" + GoodId + "\"\r\n"));
        Assert.Equal(GoodSecret, TwitchAuth.CleanCredential("0123456789 abcdefghij\t0123456789"));
        Assert.Null(TwitchAuth.ValidateCredentials(new AppCredentials(" " + GoodId + " ", "'" + GoodSecret + "'\n")));
    }

    [Fact]
    public void Pasted_command_as_secret_is_rejected_with_length_in_message()
    {
        var pasted = "dotnet user-secrets set Twitch:ClientSecret " + GoodSecret + " --project src/whatever.csproj";
        var problem = TwitchAuth.ValidateCredentials(new AppCredentials(GoodId, pasted));
        Assert.NotNull(problem);
        Assert.StartsWith("Client Secret does not look like a Twitch value", problem);
        Assert.Contains("30 lowercase letters and digits", problem);
    }

    [Theory]
    [InlineData("ABCDEFGHIJ0123456789KLMNOPQRST")] // uppercase
    [InlineData("abcdefghij0123456789klmnopqrs")]  // 29 chars
    [InlineData("abcdefghij0123456789klmnopqrs-")] // bad char
    public void Wrong_client_id_shapes_are_rejected(string id)
    {
        Assert.StartsWith("Client ID does not look like", TwitchAuth.ValidateCredentials(new AppCredentials(id, GoodSecret)));
    }

    [Fact]
    public void Empty_values_name_the_missing_field()
    {
        Assert.Equal("Client ID is empty.", TwitchAuth.ValidateCredentials(new AppCredentials(" ", GoodSecret)));
        Assert.Equal("Client Secret is empty.", TwitchAuth.ValidateCredentials(new AppCredentials(GoodId, "")));
    }

    [Fact]
    public async Task Sign_in_with_bad_secret_fails_before_listening_or_opening_browser()
    {
        var handler = new FakeHandler();
        var store = new TokenStore(new FakeSettings(), new FakeProtector());
        var auth = new TwitchAuth(new HttpClient(handler), store,
            () => new AppCredentials(GoodId, "way too long " + GoodSecret + GoodSecret), new FakeLogger());
        var opened = false;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => auth.SignInAsync(3000, _ => opened = true));
        Assert.StartsWith("Client Secret does not look like", ex.Message);
        Assert.False(opened);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void Saving_settings_stores_the_cleaned_values()
    {
        var settings = new FakeSettings();
        settings.Set(TwitchPlugin.SettingClientId, " " + GoodId + "\n");
        settings.Set(TwitchPlugin.SettingClientSecret, GoodSecret);

        Assert.True(TwitchPlugin.NormalizeCredentials(settings));
        Assert.Equal(GoodId, settings.Get<string>(TwitchPlugin.SettingClientId));
        Assert.Equal(GoodSecret, settings.Get<string>(TwitchPlugin.SettingClientSecret));
        Assert.Equal(1, settings.SaveCount);
        Assert.False(TwitchPlugin.NormalizeCredentials(settings));
    }

    [Fact]
    public void Account_status_names_missing_scopes_and_credential_problems()
    {
        var text = TwitchPlugin.AccountStatus("yourchannel", "Client ID is empty.", ["channel:edit:commercial"]);
        Assert.Contains("Signed in as yourchannel", text);
        Assert.Contains("channel:edit:commercial", text);
        Assert.Contains("Sign in again", text);
        Assert.EndsWith("Problem: Client ID is empty.", text);
    }
}

public class StepSnappingTests
{
    [Fact]
    public void Step_lists_match_twitch()
    {
        Assert.Equal([3, 5, 10, 20, 30, 60, 120], TwitchSteps.SlowModeWaitSeconds);
        Assert.Equal([30, 60, 90, 120, 150, 180], TwitchSteps.AdLengthSeconds);
        Assert.Contains(TwitchSteps.DefaultSlowModeWait, TwitchSteps.SlowModeWaitSeconds);
        Assert.Contains(TwitchSteps.DefaultAdLength, TwitchSteps.AdLengthSeconds);
    }

    [Theory]
    [InlineData(-5, 30)]    // below min
    [InlineData(29, 30)]
    [InlineData(60, 60)]    // exact
    [InlineData(74, 60)]    // between, closer to lower
    [InlineData(76, 90)]    // between, closer to upper
    [InlineData(75, 60)]    // tie: lower
    [InlineData(165, 150)]  // tie: lower
    [InlineData(181, 180)]  // above max
    [InlineData(long.MaxValue, 180)]
    public void Ad_length_snapping(long input, int expected)
    {
        Assert.Equal(expected, TwitchSteps.SnapAdLength(input));
    }

    [Theory]
    [InlineData(-1, 3)]     // below min
    [InlineData(4, 3)]      // tie between 3 and 5: lower
    [InlineData(15, 10)]    // tie between 10 and 20: lower
    [InlineData(16, 20)]
    [InlineData(20, 20)]    // exact
    [InlineData(121, 120)]  // above max
    public void Slow_mode_wait_snapping(long input, int expected)
    {
        Assert.Equal(expected, TwitchSteps.SnapSlowModeWait(input));
    }

    [Fact]
    public void Describe_lists_the_steps()
    {
        Assert.Equal("30, 60, 90, 120, 150, 180", TwitchSteps.Describe(TwitchSteps.AdLengthSeconds));
    }
}

public class ViewerCountBehaviourTests
{
    // Owner decision for 1.1.0: keep the 1.0 display, a number when live and "offline" when not.
    [Theory]
    [InlineData(0, "0")]
    [InlineData(42, "42")]
    [InlineData(1234, "1,234")]
    [InlineData(null, "offline")]
    public void Live_shows_number_offline_shows_offline(int? viewers, string expected)
    {
        Assert.Equal(expected, ViewerCountCache.Format(viewers));
    }

    [Fact]
    public async Task Cache_switches_from_live_number_to_offline()
    {
        int? viewers = 57;
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var cache = new ViewerCountCache(_ => Task.FromResult(viewers), () => { }, new FakeLogger(),
            TimeSpan.FromSeconds(60), () => now);

        cache.GetText();
        await cache.CurrentFetch!;
        Assert.Equal("57", cache.GetText());

        viewers = null;
        now = now.AddSeconds(60);
        cache.GetText();
        await cache.CurrentFetch!;
        Assert.Equal("offline", cache.GetText());
    }
}
