using System.Net;
using LoupixDeck.Plugin.Twitch.Commands;
using LoupixDeck.Plugin.Twitch.Twitch;
using LoupixDeck.PluginSdk;
using Xunit;

namespace LoupixDeck.Plugin.Twitch.Tests;

public class TokenStoreTests
{
    [Fact]
    public void Token_is_stored_protected_not_in_plain_text_and_round_trips()
    {
        var settings = new FakeSettings();
        var store = new TokenStore(settings, new FakeProtector());
        store.Save(new TokenData { AccessToken = "secret-access", RefreshToken = "secret-refresh", ClientId = "c" });

        var blob = Assert.IsType<string>(settings.Values[TokenStore.SettingsKey]);
        Assert.DoesNotContain("secret-access", blob);
        Assert.DoesNotContain("secret-refresh", blob);

        var fresh = new TokenStore(settings, new FakeProtector());
        Assert.Equal("secret-refresh", fresh.Load()!.RefreshToken);
    }

    [Fact]
    public void Undecryptable_blob_counts_as_signed_out()
    {
        var settings = new FakeSettings();
        settings.Values[TokenStore.SettingsKey] = "garbage";
        var logger = new FakeLogger();

        var store = new TokenStore(settings, new FakeProtector(), logger);

        Assert.False(store.HasToken);
        Assert.Contains(logger.Lines, l => l.StartsWith("W "));
    }

    [Fact]
    public void Clear_removes_the_setting()
    {
        var settings = new FakeSettings();
        var store = new TokenStore(settings, new FakeProtector());
        store.Save(new TokenData { AccessToken = "a" });
        store.Clear();
        Assert.False(settings.Contains(TokenStore.SettingsKey));
        Assert.False(store.HasToken);
    }

    [Fact]
    public void Dpapi_protector_round_trips_for_current_user()
    {
        if (!OperatingSystem.IsWindows()) return;
        var p = new DpapiTokenProtector();
        var blob = p.Protect("hello-token");
        Assert.DoesNotContain("hello-token", blob);
        Assert.Equal("hello-token", p.Unprotect(blob));
    }
}

public class ViewerCountCacheTests
{
    private sealed class Clock
    {
        public DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    }

    [Fact]
    public async Task Fetches_at_most_once_per_interval_and_notifies_on_change()
    {
        var clock = new Clock();
        var calls = 0;
        var notified = 0;
        var cache = new ViewerCountCache(
            _ => { calls++; return Task.FromResult<int?>(1234); },
            () => notified++,
            new FakeLogger(),
            TimeSpan.FromSeconds(60),
            () => clock.Now);

        Assert.Equal("…", cache.GetText());
        await cache.CurrentFetch!;
        Assert.Equal("1,234", cache.GetText());
        clock.Now = clock.Now.AddSeconds(59);
        cache.GetText();
        await cache.CurrentFetch!;
        Assert.Equal(1, calls);
        Assert.Equal(1, notified);

        clock.Now = clock.Now.AddSeconds(1);
        cache.GetText();
        await cache.CurrentFetch!;
        Assert.Equal(2, calls);
        Assert.Equal(1, notified); // same text, no redundant refresh request
    }

    [Fact]
    public async Task No_GetText_calls_means_no_fetches()
    {
        var calls = 0;
        _ = new ViewerCountCache(_ => { calls++; return Task.FromResult<int?>(1); }, () => { }, new FakeLogger());
        await Task.Delay(50);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Offline_shows_offline()
    {
        var cache = new ViewerCountCache(_ => Task.FromResult<int?>(null), () => { }, new FakeLogger());
        cache.GetText();
        await cache.CurrentFetch!;
        Assert.Equal("offline", cache.GetText());
    }

    [Fact]
    public async Task Auth_failure_shows_sign_in_and_stops_polling_until_invalidated()
    {
        var clock = new Clock();
        var calls = 0;
        var cache = new ViewerCountCache(
            _ => { calls++; throw new TwitchAuthRequiredException("nope"); },
            () => { },
            new FakeLogger(),
            TimeSpan.FromSeconds(60),
            () => clock.Now);

        cache.GetText();
        await cache.CurrentFetch!;
        Assert.Equal("sign in", cache.GetText());

        clock.Now = clock.Now.AddMinutes(10);
        cache.GetText();
        Assert.Equal(1, calls);

        cache.Invalidate();
        cache.GetText();
        await cache.CurrentFetch!;
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Other_errors_show_error_and_retry_after_interval()
    {
        var clock = new Clock();
        var calls = 0;
        var cache = new ViewerCountCache(
            _ => { calls++; throw new HttpRequestException("down"); },
            () => { },
            new FakeLogger(),
            TimeSpan.FromSeconds(60),
            () => clock.Now);

        cache.GetText();
        await cache.CurrentFetch!;
        Assert.Equal("error", cache.GetText());
        clock.Now = clock.Now.AddSeconds(61);
        cache.GetText();
        await cache.CurrentFetch!;
        Assert.Equal(2, calls);
    }
}

public class CommandTests
{
    private static CommandContext Ctx(FakeHost host, ButtonTargets target, int? source = null, params string[] parameters) => new()
    {
        Parameters = parameters,
        Target = target,
        SourceIndex = source,
        Host = host
    };

    [Fact]
    public void Command_names_match_the_fixed_contract()
    {
        var rig = new Rig();
        var names = new IPluginCommand[]
        {
            new SendChatMessageCommand(rig.Helix, rig.Logger),
            new CreateClipCommand(rig.Helix, rig.Logger),
            new ViewerCountCommand(new ViewerCountCache(_ => Task.FromResult<int?>(0), () => { }, rig.Logger), rig.Logger),
            new ClearChatCommand(rig.Helix, rig.Logger),
            new ToggleSlowChatCommand(rig.Helix, rig.Logger, () => 30),
            new ToggleEmotesOnlyCommand(rig.Helix, rig.Logger),
            new RunCommercialCommand(rig.Helix, rig.Logger, () => 30),
            new CreateStreamMarkerCommand(rig.Helix, rig.Logger)
        }.Select(c => c.Descriptor.CommandName).ToArray();

        Assert.Equal(
            ["Twitch.SendChatMessage", "Twitch.CreateClip", "Twitch.ViewerCount", "Twitch.ClearChat", "Twitch.ToggleSlowChat", "Twitch.ToggleEmotesOnly",
             "Twitch.RunCommercial", "Twitch.CreateStreamMarker"],
            names);
    }

    [Theory]
    [InlineData(new[] { "hello" }, "hello")]
    [InlineData(new[] { "hi", "all" }, "hi, all")]
    [InlineData(new string[0], "")]
    public void Message_parameters_split_on_commas_are_rejoined(string[] parts, string expected)
    {
        Assert.Equal(expected, SendChatMessageCommand.MessageFrom(parts));
    }

    [Fact]
    public async Task Execute_never_throws_and_does_not_log()
    {
        var rig = new Rig(signedIn: false);
        var host = new FakeHost();
        var cmd = new ClearChatCommand(rig.Helix, rig.Logger);

        await cmd.Execute(Ctx(host, ButtonTargets.TouchButton));

        Assert.Empty(rig.Logger.Lines); // the badge is the feedback; the command does not also log
        Assert.Empty(host.Overlays); // touch presses carry no slot index
    }

    [Fact]
    public async Task Dial_press_flashes_feedback_on_neighbouring_touch_slot()
    {
        var rig = new Rig();
        rig.Handler.Respond(HttpStatusCode.NotFound, """{"status":404,"message":"Clipping is not possible for an offline channel."}""");
        var host = new FakeHost();

        await new CreateClipCommand(rig.Helix, rig.Logger).Execute(Ctx(host, ButtonTargets.RotaryEncoder, 2));

        Assert.Equal((12, "Offline"), Assert.Single(host.Overlays));
    }

    [Fact]
    public async Task Empty_message_is_not_sent()
    {
        var rig = new Rig();
        var host = new FakeHost();
        await new SendChatMessageCommand(rig.Helix, rig.Logger).Execute(Ctx(host, ButtonTargets.TouchButton));
        Assert.Empty(rig.Handler.Requests);
    }

    [Fact]
    public async Task ViewerCount_press_does_not_trigger_a_fetch()
    {
        var calls = 0;
        var cache = new ViewerCountCache(_ => { calls++; return Task.FromResult<int?>(5); }, () => { }, new FakeLogger());
        var cmd = new ViewerCountCommand(cache, new FakeLogger());
        await cmd.Execute(Ctx(new FakeHost(), ButtonTargets.TouchButton));
        Assert.Equal(0, calls);
    }
}
