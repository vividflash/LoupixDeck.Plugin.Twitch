using System.Net;
using LoupixDeck.Plugin.Twitch.Commands;
using LoupixDeck.PluginSdk;
using Xunit;

namespace LoupixDeck.Plugin.Twitch.Tests;

/// <summary>
/// Every <c>TwitchCommandBase</c>-derived command shows its dial-feedback text as a touch-button
/// badge too (<c>TwitchCommandBase.SetBadge</c>/<c>RenderImage</c>), not just Run Ad (covered
/// separately in <see cref="RunAdBadgeTests"/>). These tests exercise a representative sample of
/// the generalized mechanism: a static failure badge shared by several commands, a toggle's
/// state-dependent text, and per-button keying by the button's own parameters.
/// </summary>
public class TouchBadgeTests
{
    private static CommandContext TouchCtx(IPluginHost host, string[]? parameters = null) =>
        new() { Parameters = parameters ?? [], Target = ButtonTargets.TouchButton, Host = host };

    [Fact]
    public async Task CreateClip_offline_shows_badge_then_clears_exactly_once()
    {
        var rig = new Rig();
        rig.Handler.Respond(HttpStatusCode.NotFound, """{"status":404,"message":"channel is offline"}""");
        var cmd = new CreateClipCommand(rig.Helix, rig.Logger, () => rig.Now);
        var ctx = TouchCtx(new FakeHost());

        await cmd.Execute(ctx);

        var canvas = new FakeCanvas();
        Assert.True(cmd.RenderImage(ctx, canvas));
        Assert.Equal("Offline", Assert.Single(canvas.DrawnTexts));
        Assert.True(canvas.BandDrawn);

        rig.Now = rig.Now.AddSeconds(10);
        canvas = new FakeCanvas();
        Assert.True(cmd.RenderImage(ctx, canvas)); // one-time transparent clear
        Assert.Empty(canvas.DrawnTexts);
        Assert.False(cmd.RenderImage(ctx, new FakeCanvas())); // idle afterwards
    }

    [Fact]
    public async Task CreateStreamMarker_offline_shows_badge()
    {
        var rig = new Rig();
        rig.Handler.Respond(HttpStatusCode.NotFound,
            """{"error":"Not Found","status":404,"message":"user is not streaming live"}""");
        var cmd = new CreateStreamMarkerCommand(rig.Helix, rig.Logger, () => rig.Now);
        var ctx = TouchCtx(new FakeHost());

        await cmd.Execute(ctx);

        var canvas = new FakeCanvas();
        Assert.True(cmd.RenderImage(ctx, canvas));
        Assert.Equal("Offline", Assert.Single(canvas.DrawnTexts));
    }

    [Theory]
    [InlineData(false, "Emotes ON")]
    [InlineData(true, "Emotes OFF")]
    public async Task ToggleEmotesOnly_badge_shows_the_new_state(bool currentlyOn, string expected)
    {
        var rig = new Rig();
        rig.Handler.Respond(HttpStatusCode.OK,
            $$"""{"data":[{"emote_mode":{{(currentlyOn ? "true" : "false")}},"slow_mode":false}]}""");
        rig.Handler.Respond(HttpStatusCode.OK, "{}");
        var cmd = new ToggleEmotesOnlyCommand(rig.Helix, rig.Logger, () => rig.Now);
        var ctx = TouchCtx(new FakeHost());

        await cmd.Execute(ctx);

        var canvas = new FakeCanvas();
        Assert.True(cmd.RenderImage(ctx, canvas));
        Assert.Equal(expected, Assert.Single(canvas.DrawnTexts));
    }

    [Fact]
    public async Task SendChatMessage_badge_is_keyed_per_button_parameters()
    {
        var rig = new Rig();
        rig.Handler.Respond(HttpStatusCode.OK, """{"data":[{"is_sent":true}]}""");
        rig.Handler.Respond(HttpStatusCode.OK, """{"data":[{"is_sent":true}]}""");
        var cmd = new SendChatMessageCommand(rig.Helix, rig.Logger, () => rig.Now);
        var host = new FakeHost();
        var ctxHello = TouchCtx(host, ["hello"]);
        var ctxWorld = TouchCtx(host, ["world"]);

        await cmd.Execute(ctxHello);

        var helloCanvas = new FakeCanvas();
        Assert.True(cmd.RenderImage(ctxHello, helloCanvas));
        Assert.Equal("Sent", Assert.Single(helloCanvas.DrawnTexts));

        // A different button (different parameters) bound to the same command has no badge yet.
        Assert.False(cmd.RenderImage(ctxWorld, new FakeCanvas()));

        await cmd.Execute(ctxWorld);

        var worldCanvas = new FakeCanvas();
        Assert.True(cmd.RenderImage(ctxWorld, worldCanvas));
        Assert.Equal("Sent", Assert.Single(worldCanvas.DrawnTexts));

        // The first button's own badge is unaffected by the second button firing.
        var helloCanvas2 = new FakeCanvas();
        Assert.True(cmd.RenderImage(ctxHello, helloCanvas2));
        Assert.Equal("Sent", Assert.Single(helloCanvas2.DrawnTexts));
    }

    [Fact]
    public async Task Dial_feedback_still_works_alongside_the_badge()
    {
        var rig = new Rig();
        rig.Handler.Respond(HttpStatusCode.OK, """{"data":[{"id":"clip-1"}]}""");
        var host = new FakeHost();

        await new CreateClipCommand(rig.Helix, rig.Logger, () => rig.Now)
            .Execute(new CommandContext { Parameters = [], Target = ButtonTargets.RotaryEncoder, SourceIndex = 0, Host = host });

        Assert.Equal((10, "Clipped"), Assert.Single(host.Overlays));
    }
}
