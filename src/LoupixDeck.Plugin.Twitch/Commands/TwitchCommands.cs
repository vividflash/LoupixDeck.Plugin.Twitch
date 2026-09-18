using System.Collections.Concurrent;
using LoupixDeck.Plugin.Twitch.Twitch;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Twitch.Commands;

/// <summary>
/// Shared Execute plumbing: every command body runs inside try/catch, and when the press
/// came from a dial (the only case where the host tells the plugin which control fired) a
/// short text is flashed on the touch slot next to it.
///
/// <para>
/// Touch and simple buttons never tell the plugin which one fired, so on top of that this base
/// class implements <see cref="IDisplayImageCommand"/> and draws the same short result text as a
/// small badge over the bottom of the button's own image (a rounded, semi-transparent dark band,
/// white bold centered text), for a few seconds after every press. By default the badge shows
/// exactly the text flashed on a dial (<see cref="OnSuccess"/>/<see cref="OnFailure"/> both call
/// <see cref="SetBadge(CommandContext,string,TimeSpan?)"/>); a command can override either hook
/// for different on-button behaviour, e.g. a countdown (see <c>RunCommercialCommand</c>). State is
/// keyed by the button's own parsed parameters, not by command name alone, so e.g. several
/// SendChatMessage buttons with different fixed messages each keep their own badge.
/// </para>
/// </summary>
internal abstract class TwitchCommandBase : IDisplayImageCommand
{
    internal const string GroupName = "Twitch";
    private static readonly TimeSpan FeedbackDuration = TimeSpan.FromMilliseconds(1800);
    private static readonly TimeSpan DefaultBadgeDuration = TimeSpan.FromSeconds(10);

    // Bottom band the badge is drawn into, within the 90x90 touch canvas.
    private const int BadgeHeight = 26;
    private const int BadgeMargin = 3;
    private const int BadgeRadius = 8;
    private const int BadgeTextPadding = 8;
    private const float MaxFontSize = 12f;
    private const float MinFontSize = 7f;
    private static readonly PluginColor BandColor = new(0, 0, 0, 170);

    protected readonly HelixClient Helix;
    protected readonly IPluginLogger Logger;
    protected readonly Func<DateTime> UtcNow;

    private readonly ConcurrentDictionary<string, BadgeSlot> _badges = new();

    protected TwitchCommandBase(HelixClient helix, IPluginLogger logger, Func<DateTime>? utcNow = null)
    {
        Helix = helix;
        Logger = logger;
        UtcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public abstract CommandDescriptor Descriptor { get; }
    public virtual ButtonTargets SupportedTargets => ButtonTargets.All;

    // Countdown/expiry granularity; the host polls this while a bound touch button is visible.
    public TimeSpan UpdateInterval => TimeSpan.FromSeconds(1);

    /// <summary>Called after a successful run with the dial-feedback text. Default: show the
    /// same text as a touch-button badge for a few seconds. Override for on-button behaviour
    /// that differs from the static dial text (e.g. a countdown).</summary>
    protected virtual void OnSuccess(CommandContext ctx, string feedback) => SetBadge(ctx, feedback);

    /// <summary>Called after a failure, with the same short text used for dial feedback.
    /// Default: show the same text as a touch-button badge for a few seconds. Override to
    /// react to the outcome differently (e.g. a known cooldown countdown).</summary>
    protected virtual void OnFailure(CommandContext ctx, string feedback, Exception ex) => SetBadge(ctx, feedback);

    public async Task Execute(CommandContext ctx)
    {
        try
        {
            var done = await Run(ctx).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(done))
            {
                Feedback(ctx, done);
                OnSuccess(ctx, done);
            }
        }
        catch (TwitchAuthRequiredException ex)
        {
            Feedback(ctx, "Sign in");
            OnFailure(ctx, "Sign in", ex);
        }
        catch (TwitchApiException ex)
        {
            var feedback = ex.Feedback ?? "Failed";
            Feedback(ctx, feedback);
            OnFailure(ctx, feedback, ex);
        }
        catch (ArgumentException ex)
        {
            Feedback(ctx, "Failed");
            OnFailure(ctx, "Failed", ex);
        }
        catch (Exception ex)
        {
            Feedback(ctx, "Failed");
            OnFailure(ctx, "Failed", ex);
        }
    }

    /// <summary>Runs the command; returns an optional short success text for dial feedback.</summary>
    protected abstract Task<string?> Run(CommandContext ctx);

    private static void Feedback(CommandContext ctx, string text)
    {
        try
        {
            if (ctx.Target != ButtonTargets.RotaryEncoder || ctx.SourceIndex is not { } rotary) return;
            var slot = ctx.Host.GetTouchSlotForRotary(rotary);
            if (slot >= 0) ctx.Host.OverlayTouchText(slot, text, FeedbackDuration);
        }
        catch
        {
            // Feedback is cosmetic.
        }
    }

    // ---------------- touch-button badge (shared by every command above) ----------------

    private sealed class BadgeSlot
    {
        public readonly object Gate = new();
        public Func<TimeSpan, string>? TextFor;
        public DateTime EndUtc;
        public bool Active;
    }

    /// <summary>Shows <paramref name="text"/> as a touch-button badge for <paramref name="duration"/>
    /// (10 s by default), then clears.</summary>
    protected void SetBadge(CommandContext ctx, string text, TimeSpan? duration = null) =>
        SetBadge(ctx, UtcNow() + (duration ?? DefaultBadgeDuration), _ => text);

    /// <summary>Shows a touch-button badge until <paramref name="endUtc"/>, recomputing its text
    /// from the remaining time on every poll (e.g. a countdown). <paramref name="textFor"/> is
    /// not called again after expiry.</summary>
    protected void SetBadge(CommandContext ctx, DateTime endUtc, Func<TimeSpan, string> textFor)
    {
        var slot = _badges.GetOrAdd(KeyFor(ctx), static _ => new BadgeSlot());
        lock (slot.Gate)
        {
            slot.TextFor = textFor;
            slot.EndUtc = endUtc;
            slot.Active = true;
        }

        try
        {
            ctx.Host.RequestButtonRefresh(Descriptor.CommandName);
        }
        catch
        {
            // Refresh is cosmetic; the next poll picks up the new state anyway.
        }
    }

    /// <summary>
    /// Draws the current badge onto the host's transparent per-frame canvas, on top of the
    /// user's own button image. Returns false while idle (nothing to draw, previous content
    /// untouched) and true whenever something was drawn, including exactly once right after the
    /// badge expires so the host clears the last drawing (nothing is painted on that call; the
    /// canvas starts fully transparent).
    /// </summary>
    public bool RenderImage(CommandContext ctx, IRenderCanvas canvas)
    {
        if (!_badges.TryGetValue(KeyFor(ctx), out var slot)) return false;

        Func<TimeSpan, string>? textFor;
        DateTime endUtc;
        bool active;
        lock (slot.Gate)
        {
            active = slot.Active;
            textFor = slot.TextFor;
            endUtc = slot.EndUtc;
        }

        if (!active || textFor == null) return false;

        var now = UtcNow();
        if (now >= endUtc)
        {
            // One-time clear: flip back to idle so the next poll returns false. Nothing is
            // drawn here; the canvas is already transparent, which removes the badge.
            lock (slot.Gate)
            {
                if (slot.Active && slot.EndUtc == endUtc) slot.Active = false;
            }

            return true;
        }

        DrawBadge(canvas, textFor(endUtc - now));
        return true;
    }

    /// <summary>Keys badge state per button rather than per command: several buttons bound to the
    /// same command name but different parameters (e.g. two SendChatMessage buttons with
    /// different fixed texts) each get their own badge. The render-path CommandContext carries
    /// the same parsed Parameters as the execute-path one for the same button.</summary>
    private static string KeyFor(CommandContext ctx) => string.Join('', ctx.Parameters);

    private static void DrawBadge(IRenderCanvas canvas, string text)
    {
        var width = canvas.Width - BadgeMargin * 2;
        var y = canvas.Height - BadgeHeight - BadgeMargin;
        canvas.FillRoundedRectangle(BadgeMargin, y, width, BadgeHeight, BadgeRadius, BandColor);

        var fontSize = FitFontSize(canvas, text, width - BadgeTextPadding);
        canvas.DrawText(text, BadgeMargin, y, width, BadgeHeight, PluginColor.White, fontSize,
            bold: true, centered: true);
    }

    private static float FitFontSize(IRenderCanvas canvas, string text, float maxWidth)
    {
        var size = MaxFontSize;
        while (size > MinFontSize && canvas.MeasureText(text, size, bold: true) > maxWidth)
            size -= 0.5f;
        return size;
    }
}

internal sealed class SendChatMessageCommand(HelixClient helix, IPluginLogger logger, Func<DateTime>? utcNow = null)
    : TwitchCommandBase(helix, logger, utcNow)
{
    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "Twitch.SendChatMessage",
        DisplayName = "Send chat message",
        Group = GroupName,
        Description = "Sends a fixed message to your channel chat",
        ParameterTemplate = "({Message})",
        Parameters = [new CommandParameter("Message", typeof(string)) { DefaultValue = "" }]
    };

    /// <summary>
    /// The host splits the parameter list on commas and trims each piece, so a
    /// message containing commas arrives as several parameters. Join them back.
    /// </summary>
    internal static string MessageFrom(string[] parameters) => string.Join(", ", parameters).Trim();

    protected override async Task<string?> Run(CommandContext ctx)
    {
        var message = MessageFrom(ctx.Parameters);
        if (message.Length == 0)
            throw new ArgumentException("No message set on this button.");

        await Helix.SendChatMessageAsync(message).ConfigureAwait(false);
        return "Sent";
    }
}

internal sealed class CreateClipCommand(HelixClient helix, IPluginLogger logger, Func<DateTime>? utcNow = null)
    : TwitchCommandBase(helix, logger, utcNow)
{
    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "Twitch.CreateClip",
        DisplayName = "Create clip",
        Group = GroupName,
        Description = "Clips your live stream (fails when offline)"
    };

    protected override async Task<string?> Run(CommandContext ctx)
    {
        await Helix.CreateClipAsync().ConfigureAwait(false);
        return "Clipped";
    }
}

internal sealed class ClearChatCommand(HelixClient helix, IPluginLogger logger, Func<DateTime>? utcNow = null)
    : TwitchCommandBase(helix, logger, utcNow)
{
    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "Twitch.ClearChat",
        DisplayName = "Clear chat",
        Group = GroupName,
        Description = "Removes all messages from your chat"
    };

    protected override async Task<string?> Run(CommandContext ctx)
    {
        await Helix.ClearChatAsync().ConfigureAwait(false);
        return "Cleared";
    }
}

internal sealed class ToggleSlowChatCommand(
    HelixClient helix,
    IPluginLogger logger,
    Func<int> defaultWaitSeconds,
    Func<DateTime>? utcNow = null)
    : TwitchCommandBase(helix, logger, utcNow)
{
    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "Twitch.ToggleSlowChat",
        DisplayName = "Toggle slow chat",
        Group = GroupName,
        Description = "Turns slow mode on or off (wait time set in plugin settings)"
    };

    protected override async Task<string?> Run(CommandContext ctx)
    {
        var on = await Helix.ToggleSlowModeAsync(defaultWaitSeconds()).ConfigureAwait(false);
        return on ? "Slow ON" : "Slow OFF";
    }
}

internal sealed class ToggleEmotesOnlyCommand(HelixClient helix, IPluginLogger logger, Func<DateTime>? utcNow = null)
    : TwitchCommandBase(helix, logger, utcNow)
{
    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "Twitch.ToggleEmotesOnly",
        DisplayName = "Toggle emote-only chat",
        Group = GroupName,
        Description = "Turns emote-only mode on or off"
    };

    protected override async Task<string?> Run(CommandContext ctx)
    {
        var on = await Helix.ToggleEmoteOnlyAsync().ConfigureAwait(false);
        return on ? "Emotes ON" : "Emotes OFF";
    }
}

/// <summary>
/// Starts an ad break. The length comes from the plugin settings (30 to 180 s in 30 s steps).
///
/// Uses the shared touch-button badge from <see cref="TwitchCommandBase"/>, but with its own
/// on-button behaviour instead of the default static text: a successful start shows the
/// remaining seconds counting down ("30s", "29s", ...) until the ad ends, and a cooldown refusal
/// shows "Cooldown m:ss" counting down when the remaining time is known (this client started the
/// ad whose cooldown is running), falling back to the shared base behaviour (a static "Cooldown"
/// badge for a few seconds) when it is not. Every other outcome (Offline / Sign in / Failed) uses
/// the shared base behaviour unchanged.
/// </summary>
internal sealed class RunCommercialCommand(
    HelixClient helix,
    IPluginLogger logger,
    Func<int> adLengthSeconds,
    Func<DateTime>? utcNow = null)
    : TwitchCommandBase(helix, logger, utcNow)
{
    public const string Name = "Twitch.RunCommercial";

    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = Name,
        DisplayName = "Run Ad",
        Group = GroupName,
        Description = "Starts an ad break (length set in plugin settings)"
    };

    protected override async Task<string?> Run(CommandContext ctx)
    {
        var result = await Helix.RunCommercialAsync(adLengthSeconds()).ConfigureAwait(false);
        SetBadge(ctx, UtcNow().AddSeconds(result.Length), remaining => $"{Seconds(remaining)}s");
        return $"Ad {result.Length}s";
    }

    // Run() already set the countdown badge; the shared default (static dial text) would replace it.
    protected override void OnSuccess(CommandContext ctx, string feedback)
    {
    }

    protected override void OnFailure(CommandContext ctx, string feedback, Exception ex)
    {
        // Known only when this client itself started the ad whose cooldown is running.
        if (feedback == "Cooldown" && Helix.AdCooldownUntilUtc is { } until && until > UtcNow())
        {
            SetBadge(ctx, until, remaining => $"Cooldown {Seconds(remaining) / 60}:{Seconds(remaining) % 60:D2}");
            return;
        }

        // Offline / Sign in / Failed / unknown-remaining Cooldown: shared static badge.
        base.OnFailure(ctx, feedback, ex);
    }

    private static int Seconds(TimeSpan remaining) => Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
}

/// <summary>Adds a stream marker at the current position (live only). No description.</summary>
internal sealed class CreateStreamMarkerCommand(HelixClient helix, IPluginLogger logger, Func<DateTime>? utcNow = null)
    : TwitchCommandBase(helix, logger, utcNow)
{
    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "Twitch.CreateStreamMarker",
        DisplayName = "Set Marker",
        Group = GroupName,
        Icon = "\U000F00C0", // mdi-bookmark
        Description = "Marks the current moment of your live stream for highlights"
    };

    protected override async Task<string?> Run(CommandContext ctx)
    {
        await Helix.CreateStreamMarkerAsync().ConfigureAwait(false);
        return "Marked";
    }
}

/// <summary>Live text display: viewer count, "offline" when not live.</summary>
internal sealed class ViewerCountCommand(ViewerCountCache cache, IPluginLogger logger) : IDisplayCommand
{
    public const string Name = "Twitch.ViewerCount";

    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = Name,
        DisplayName = "Viewer count",
        Group = TwitchCommandBase.GroupName,
        Description = "Shows your current viewer count (updates at most once a minute)"
    };

    public ButtonTargets SupportedTargets => ButtonTargets.TouchButton;

    // Cheap: GetText only reads the cache; the cache itself throttles network calls.
    public TimeSpan UpdateInterval => TimeSpan.FromSeconds(5);

    public string GetText(CommandContext ctx)
    {
        try { return cache.GetText(); }
        catch (Exception ex)
        {
            logger.Error("Twitch.ViewerCount: GetText failed", ex);
            return "error";
        }
    }

    // Pressing the button does nothing on purpose: the count refreshes on its own at
    // most once a minute while visible, and a press must not bypass that limit.
    public Task Execute(CommandContext ctx) => Task.CompletedTask;
}
