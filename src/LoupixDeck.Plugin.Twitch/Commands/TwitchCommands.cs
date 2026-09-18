using LoupixDeck.Plugin.Twitch.Twitch;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Twitch.Commands;

/// <summary>
/// Shared Execute plumbing: every command body runs inside try/catch, failures
/// go to the host log, and when the press came from a dial (the only case where
/// the host tells the plugin which control fired) a short text is flashed on the
/// touch slot next to it.
/// </summary>
internal abstract class TwitchCommandBase : IPluginCommand
{
    internal const string GroupName = "Twitch";
    private static readonly TimeSpan FeedbackDuration = TimeSpan.FromMilliseconds(1800);

    protected readonly HelixClient Helix;
    protected readonly IPluginLogger Logger;

    protected TwitchCommandBase(HelixClient helix, IPluginLogger logger)
    {
        Helix = helix;
        Logger = logger;
    }

    public abstract CommandDescriptor Descriptor { get; }
    public virtual ButtonTargets SupportedTargets => ButtonTargets.All;

    public async Task Execute(CommandContext ctx)
    {
        try
        {
            var done = await Run(ctx).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(done)) Feedback(ctx, done);
        }
        catch (TwitchAuthRequiredException ex)
        {
            Logger.Warn($"{Descriptor.CommandName}: {ex.Message}");
            Feedback(ctx, "Sign in");
        }
        catch (TwitchApiException ex)
        {
            Logger.Warn($"{Descriptor.CommandName}: Twitch returned {ex.StatusCode}: {ex.Message}");
            Feedback(ctx, ex.StatusCode == 404 && Descriptor.CommandName == "Twitch.CreateClip" ? "Offline" : "Failed");
        }
        catch (ArgumentException ex)
        {
            Logger.Warn($"{Descriptor.CommandName}: {ex.Message}");
            Feedback(ctx, "Failed");
        }
        catch (Exception ex)
        {
            Logger.Error($"{Descriptor.CommandName}: unexpected error", ex);
            Feedback(ctx, "Failed");
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
}

internal sealed class SendChatMessageCommand(HelixClient helix, IPluginLogger logger) : TwitchCommandBase(helix, logger)
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
        Logger.Info("Twitch.SendChatMessage: sent.");
        return "Sent";
    }
}

internal sealed class CreateClipCommand(HelixClient helix, IPluginLogger logger) : TwitchCommandBase(helix, logger)
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
        var id = await Helix.CreateClipAsync().ConfigureAwait(false);
        Logger.Info($"Twitch.CreateClip: clip created ({id}).");
        return "Clipped";
    }
}

internal sealed class ClearChatCommand(HelixClient helix, IPluginLogger logger) : TwitchCommandBase(helix, logger)
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
        Logger.Info("Twitch.ClearChat: chat cleared.");
        return "Cleared";
    }
}

internal sealed class ToggleSlowChatCommand(HelixClient helix, IPluginLogger logger, Func<int> defaultWaitSeconds)
    : TwitchCommandBase(helix, logger)
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
        Logger.Info($"Twitch.ToggleSlowChat: slow mode {(on ? "on" : "off")}.");
        return on ? "Slow ON" : "Slow OFF";
    }
}

internal sealed class ToggleEmotesOnlyCommand(HelixClient helix, IPluginLogger logger) : TwitchCommandBase(helix, logger)
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
        Logger.Info($"Twitch.ToggleEmotesOnly: emote-only {(on ? "on" : "off")}.");
        return on ? "Emotes ON" : "Emotes OFF";
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
