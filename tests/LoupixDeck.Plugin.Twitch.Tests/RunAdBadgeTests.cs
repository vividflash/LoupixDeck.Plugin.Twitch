using System.Net;
using LoupixDeck.Plugin.Twitch.Commands;
using LoupixDeck.Plugin.Twitch.Twitch;
using LoupixDeck.PluginSdk;
using Xunit;

namespace LoupixDeck.Plugin.Twitch.Tests;

/// <summary>
/// Touch buttons never tell the plugin which one fired, so Run Ad draws its own result as a
/// badge over the user's button image (<see cref="RunCommercialCommand.RenderImage"/>) instead of
/// only logging. These tests drive the state machine through <see cref="RunCommercialCommand"/>'s
/// public surface (Execute + RenderImage) with a shared fake clock, exactly as the host would:
/// press, then poll RenderImage on <see cref="RunCommercialCommand.UpdateInterval"/>.
/// </summary>
public class RunAdBadgeTests
{
    private static CommandContext TouchCtx(IPluginHost host) =>
        new() { Parameters = [], Target = ButtonTargets.TouchButton, Host = host };

    [Fact]
    public void Idle_before_any_press_draws_nothing()
    {
        var rig = new Rig();
        var cmd = new RunCommercialCommand(rig.Helix, rig.Logger, () => 30, () => rig.Now);
        var canvas = new FakeCanvas();

        var drawn = cmd.RenderImage(TouchCtx(new FakeHost()), canvas);

        Assert.False(drawn);
        Assert.False(canvas.BandDrawn);
        Assert.Empty(canvas.DrawnTexts);
    }

    [Fact]
    public async Task Successful_ad_counts_down_then_clears_exactly_once()
    {
        var rig = new Rig(scopes: [.. TwitchAuth.RequiredScopes]);
        rig.Handler.Respond(HttpStatusCode.OK, """{"data":[{"length":30,"message":"","retry_after":0}]}""");
        var cmd = new RunCommercialCommand(rig.Helix, rig.Logger, () => 30, () => rig.Now);
        var host = new FakeHost();
        var ctx = TouchCtx(host);

        await cmd.Execute(ctx);
        Assert.Contains(RunCommercialCommand.Name, host.Refreshes);

        var canvas = new FakeCanvas();
        Assert.True(cmd.RenderImage(ctx, canvas));
        Assert.Equal("30s", Assert.Single(canvas.DrawnTexts));
        Assert.True(canvas.BandDrawn);

        rig.Now = rig.Now.AddSeconds(10);
        canvas = new FakeCanvas();
        Assert.True(cmd.RenderImage(ctx, canvas));
        Assert.Equal("20s", Assert.Single(canvas.DrawnTexts));

        rig.Now = rig.Now.AddSeconds(20); // ad over (30s total)
        canvas = new FakeCanvas();
        Assert.True(cmd.RenderImage(ctx, canvas)); // exactly one transparent clear
        Assert.Empty(canvas.DrawnTexts);
        Assert.False(canvas.BandDrawn);

        Assert.False(cmd.RenderImage(ctx, new FakeCanvas())); // idle afterwards
    }

    [Fact]
    public async Task Cooldown_known_counts_down_using_the_ad_this_client_started()
    {
        var rig = new Rig(scopes: [.. TwitchAuth.RequiredScopes]);
        rig.Handler
            .Respond(HttpStatusCode.OK, """{"data":[{"length":60,"message":"","retry_after":480}]}""")
            .Respond(HttpStatusCode.TooManyRequests,
                """{"error":"Too Many Requests","status":429,"message":"The broadcaster may not run another commercial until the cooldown period expires."}""");
        var cmd = new RunCommercialCommand(rig.Helix, rig.Logger, () => 60, () => rig.Now);
        var ctx = TouchCtx(new FakeHost());

        await cmd.Execute(ctx); // starts the 60s ad; Helix now remembers cooldown until +480s
        rig.Now = rig.Now.AddSeconds(100);
        await cmd.Execute(ctx); // refused: cooldown, 380s left

        var canvas = new FakeCanvas();
        Assert.True(cmd.RenderImage(ctx, canvas));
        Assert.Equal("Cooldown 6:20", Assert.Single(canvas.DrawnTexts));

        rig.Now = rig.Now.AddSeconds(100); // 280s left
        canvas = new FakeCanvas();
        Assert.True(cmd.RenderImage(ctx, canvas));
        Assert.Equal("Cooldown 4:40", Assert.Single(canvas.DrawnTexts));

        rig.Now = rig.Now.AddSeconds(280); // cooldown over
        canvas = new FakeCanvas();
        Assert.True(cmd.RenderImage(ctx, canvas)); // one-time clear
        Assert.Empty(canvas.DrawnTexts);
        Assert.False(cmd.RenderImage(ctx, new FakeCanvas()));
    }

    [Fact]
    public async Task Cooldown_unknown_shows_plain_text_for_ten_seconds()
    {
        // Fresh client: it never started an ad itself, so it cannot know when the cooldown ends.
        var rig = new Rig(scopes: [.. TwitchAuth.RequiredScopes]);
        rig.Handler.Respond(HttpStatusCode.BadRequest,
            """{"status":400,"message":"The broadcaster may not run another commercial until the cooldown period expires."}""");
        var cmd = new RunCommercialCommand(rig.Helix, rig.Logger, () => 30, () => rig.Now);
        var ctx = TouchCtx(new FakeHost());

        await cmd.Execute(ctx);

        var canvas = new FakeCanvas();
        Assert.True(cmd.RenderImage(ctx, canvas));
        Assert.Equal("Cooldown", Assert.Single(canvas.DrawnTexts));

        rig.Now = rig.Now.AddSeconds(10);
        canvas = new FakeCanvas();
        Assert.True(cmd.RenderImage(ctx, canvas)); // one-time clear
        Assert.Empty(canvas.DrawnTexts);
        Assert.False(cmd.RenderImage(ctx, new FakeCanvas()));
    }

    [Fact]
    public async Task Offline_shows_badge_for_ten_seconds_and_does_not_log()
    {
        var rig = new Rig();
        rig.Handler.Respond(HttpStatusCode.BadRequest,
            """{"status":400,"message":"To start a commercial, the broadcaster must be streaming live."}""");
        var cmd = new RunCommercialCommand(rig.Helix, rig.Logger, () => 30, () => rig.Now);
        var ctx = TouchCtx(new FakeHost());

        await cmd.Execute(ctx);

        var canvas = new FakeCanvas();
        Assert.True(cmd.RenderImage(ctx, canvas));
        Assert.Equal("Offline", Assert.Single(canvas.DrawnTexts));
        Assert.Empty(rig.Logger.Lines);

        rig.Now = rig.Now.AddSeconds(10);
        Assert.True(cmd.RenderImage(ctx, new FakeCanvas())); // one-time clear
        Assert.False(cmd.RenderImage(ctx, new FakeCanvas()));
    }

    [Fact]
    public async Task Missing_scope_shows_sign_in_badge()
    {
        var rig = new Rig(scopes: ["user:write:chat", "clips:edit"]);
        var cmd = new RunCommercialCommand(rig.Helix, rig.Logger, () => 30, () => rig.Now);
        var ctx = TouchCtx(new FakeHost());

        await cmd.Execute(ctx);

        var canvas = new FakeCanvas();
        Assert.True(cmd.RenderImage(ctx, canvas));
        Assert.Equal("Sign in", Assert.Single(canvas.DrawnTexts));
        Assert.Empty(rig.Logger.Lines);
    }

    [Fact]
    public async Task Unexpected_failure_shows_failed_badge_and_does_not_log()
    {
        var rig = new Rig(scopes: [.. TwitchAuth.RequiredScopes]);
        var cmd = new RunCommercialCommand(rig.Helix, rig.Logger, () => throw new InvalidOperationException("boom"), () => rig.Now);
        var ctx = TouchCtx(new FakeHost());

        await cmd.Execute(ctx);

        var canvas = new FakeCanvas();
        Assert.True(cmd.RenderImage(ctx, canvas));
        Assert.Equal("Failed", Assert.Single(canvas.DrawnTexts));
        Assert.Empty(rig.Logger.Lines);
    }
}
