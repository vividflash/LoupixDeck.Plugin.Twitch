using System.Net;
using System.Text.Json;
using LoupixDeck.Plugin.Twitch.Twitch;
using Xunit;

namespace LoupixDeck.Plugin.Twitch.Tests;

public class HelixClientTests
{
    private const string UsersJson = """{"data":[{"id":"12345","login":"yourchannel","display_name":"YourChannel"}]}""";

    private static JsonElement Json(string body) => JsonDocument.Parse(body).RootElement;

    [Fact]
    public async Task SendChatMessage_posts_broadcaster_and_sender_as_self_with_auth_headers()
    {
        var rig = new Rig();
        rig.Handler.Respond(HttpStatusCode.OK, """{"data":[{"message_id":"m1","is_sent":true}]}""");

        await rig.Helix.SendChatMessageAsync("  hello chat  ");

        var req = Assert.Single(rig.Handler.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://api.twitch.tv/helix/chat/messages", req.Url.ToString());
        Assert.Equal("access-1", req.Bearer);
        Assert.Equal(Rig.ClientId, req.ClientId);
        var body = Json(req.Body);
        Assert.Equal(Rig.UserId, body.GetProperty("broadcaster_id").GetString());
        Assert.Equal(Rig.UserId, body.GetProperty("sender_id").GetString());
        Assert.Equal("hello chat", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task User_id_is_looked_up_once_then_cached()
    {
        var rig = new Rig(withUserId: false);
        rig.Handler
            .Respond(HttpStatusCode.OK, UsersJson)
            .Respond(HttpStatusCode.OK, """{"data":[{"is_sent":true}]}""")
            .Respond(HttpStatusCode.OK, """{"data":[{"is_sent":true}]}""");

        await rig.Helix.SendChatMessageAsync("one");
        await rig.Helix.SendChatMessageAsync("two");

        Assert.Equal(3, rig.Handler.Requests.Count);
        Assert.Equal("https://api.twitch.tv/helix/users", rig.Handler.Requests[0].Url.ToString());
        Assert.Equal(1, rig.Handler.Requests.Count(r => r.Url.AbsolutePath.EndsWith("/users")));
        Assert.Equal(Rig.UserId, rig.Store.Load()!.UserId);
        Assert.Equal("yourchannel", rig.Store.Load()!.Login);
    }

    [Fact]
    public async Task SendChatMessage_over_500_chars_is_rejected_without_a_request()
    {
        var rig = new Rig();
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Helix.SendChatMessageAsync(new string('a', 501)));
        Assert.Empty(rig.Handler.Requests);
    }

    [Fact]
    public async Task SendChatMessage_exactly_500_chars_is_sent()
    {
        var rig = new Rig();
        rig.Handler.Respond(HttpStatusCode.OK, """{"data":[{"is_sent":true}]}""");
        await rig.Helix.SendChatMessageAsync(new string('a', 500));
        Assert.Single(rig.Handler.Requests);
    }

    [Fact]
    public async Task SendChatMessage_dropped_by_twitch_throws_with_reason()
    {
        var rig = new Rig();
        rig.Handler.Respond(HttpStatusCode.OK,
            """{"data":[{"message_id":"","is_sent":false,"drop_reason":{"code":"msg_duplicate","message":"duplicate message"}}]}""");

        var ex = await Assert.ThrowsAsync<TwitchApiException>(() => rig.Helix.SendChatMessageAsync("hi"));
        Assert.Contains("duplicate message", ex.Message);
    }

    [Fact]
    public async Task CreateClip_posts_with_broadcaster_id_and_returns_clip_id()
    {
        var rig = new Rig();
        rig.Handler.Respond(HttpStatusCode.Accepted, """{"data":[{"id":"ClipId123","edit_url":"https://clips.twitch.tv/ClipId123/edit"}]}""");

        var id = await rig.Helix.CreateClipAsync();

        Assert.Equal("ClipId123", id);
        var req = Assert.Single(rig.Handler.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://api.twitch.tv/helix/clips?broadcaster_id=12345", req.Url.ToString());
    }

    [Fact]
    public async Task CreateClip_when_offline_reports_offline()
    {
        var rig = new Rig();
        rig.Handler.Respond(HttpStatusCode.NotFound, """{"error":"Not Found","status":404,"message":"Clipping is not possible for an offline channel."}""");

        var ex = await Assert.ThrowsAsync<TwitchApiException>(() => rig.Helix.CreateClipAsync());
        Assert.Equal(404, ex.StatusCode);
        Assert.Contains("offline", ex.Message);
    }

    [Fact]
    public async Task ViewerCount_live_and_offline()
    {
        var rig = new Rig();
        rig.Handler
            .Respond(HttpStatusCode.OK, """{"data":[{"type":"live","viewer_count":42}],"pagination":{}}""")
            .Respond(HttpStatusCode.OK, """{"data":[],"pagination":{}}""");

        Assert.Equal(42, await rig.Helix.GetViewerCountAsync());
        Assert.Null(await rig.Helix.GetViewerCountAsync());
        Assert.All(rig.Handler.Requests, r =>
        {
            Assert.Equal(HttpMethod.Get, r.Method);
            Assert.Equal("https://api.twitch.tv/helix/streams?user_id=12345", r.Url.ToString());
        });
    }

    [Fact]
    public async Task ClearChat_deletes_with_broadcaster_and_moderator_both_self()
    {
        var rig = new Rig();
        rig.Handler.Respond(HttpStatusCode.NoContent);

        await rig.Helix.ClearChatAsync();

        var req = Assert.Single(rig.Handler.Requests);
        Assert.Equal(HttpMethod.Delete, req.Method);
        Assert.Equal("https://api.twitch.tv/helix/moderation/chat?broadcaster_id=12345&moderator_id=12345", req.Url.ToString());
    }

    [Fact]
    public async Task ToggleSlowMode_off_to_on_uses_configured_wait()
    {
        var rig = new Rig();
        rig.Handler
            .Respond(HttpStatusCode.OK, """{"data":[{"broadcaster_id":"12345","slow_mode":false,"slow_mode_wait_time":null,"emote_mode":false}]}""")
            .Respond(HttpStatusCode.OK, """{"data":[{"slow_mode":true,"slow_mode_wait_time":45}]}""");

        var on = await rig.Helix.ToggleSlowModeAsync(45);

        Assert.True(on);
        Assert.Equal(2, rig.Handler.Requests.Count);
        Assert.Equal(HttpMethod.Get, rig.Handler.Requests[0].Method);
        Assert.Equal("https://api.twitch.tv/helix/chat/settings?broadcaster_id=12345&moderator_id=12345", rig.Handler.Requests[0].Url.ToString());
        var patch = rig.Handler.Requests[1];
        Assert.Equal(HttpMethod.Patch, patch.Method);
        Assert.Equal("https://api.twitch.tv/helix/chat/settings?broadcaster_id=12345&moderator_id=12345", patch.Url.ToString());
        Assert.True(Json(patch.Body).GetProperty("slow_mode").GetBoolean());
        Assert.Equal(45, Json(patch.Body).GetProperty("slow_mode_wait_time").GetInt32());
    }

    [Fact]
    public async Task ToggleSlowMode_on_to_off_sends_only_slow_mode_false()
    {
        var rig = new Rig();
        rig.Handler
            .Respond(HttpStatusCode.OK, """{"data":[{"slow_mode":true,"slow_mode_wait_time":30,"emote_mode":false}]}""")
            .Respond(HttpStatusCode.OK, """{"data":[{"slow_mode":false}]}""");

        var on = await rig.Helix.ToggleSlowModeAsync(30);

        Assert.False(on);
        Assert.Equal("""{"slow_mode":false}""", rig.Handler.Requests[1].Body);
    }

    [Theory]
    [InlineData(30, 30)]
    [InlineData(1, 3)]
    [InlineData(0, 3)]
    [InlineData(500, 120)]
    [InlineData(120, 120)]
    public void Slow_wait_is_clamped_to_twitch_range(long input, int expected)
    {
        Assert.Equal(expected, HelixClient.ClampSlowWait(input));
        var patch = HelixClient.BuildSlowModePatch(new ChatSettings(false, null, false), (int)Math.Min(input, int.MaxValue));
        Assert.Equal(expected, patch["slow_mode_wait_time"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ToggleEmoteOnly_flips_current_state(bool current, bool expected)
    {
        var rig = new Rig();
        rig.Handler
            .Respond(HttpStatusCode.OK, $$"""{"data":[{"slow_mode":false,"emote_mode":{{(current ? "true" : "false")}}}]}""")
            .Respond(HttpStatusCode.OK, """{"data":[{}]}""");

        var result = await rig.Helix.ToggleEmoteOnlyAsync();

        Assert.Equal(expected, result);
        Assert.Equal(HttpMethod.Patch, rig.Handler.Requests[1].Method);
        Assert.Equal(expected, Json(rig.Handler.Requests[1].Body).GetProperty("emote_mode").GetBoolean());
        Assert.False(Json(rig.Handler.Requests[1].Body).TryGetProperty("slow_mode", out _));
    }

    [Fact]
    public async Task Not_signed_in_throws_auth_required_without_any_request()
    {
        var rig = new Rig(signedIn: false);
        await Assert.ThrowsAsync<TwitchAuthRequiredException>(() => rig.Helix.ClearChatAsync());
        Assert.Empty(rig.Handler.Requests);
    }

    [Fact]
    public async Task Helix_error_message_is_surfaced()
    {
        var rig = new Rig();
        rig.Handler.Respond(HttpStatusCode.Forbidden, """{"error":"Forbidden","status":403,"message":"Missing scope: moderator:manage:chat_messages"}""");

        var ex = await Assert.ThrowsAsync<TwitchApiException>(() => rig.Helix.ClearChatAsync());
        Assert.Equal(403, ex.StatusCode);
        Assert.Contains("Missing scope", ex.Message);
    }
}
