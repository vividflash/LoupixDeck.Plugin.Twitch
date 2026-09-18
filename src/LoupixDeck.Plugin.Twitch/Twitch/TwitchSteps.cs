namespace LoupixDeck.Plugin.Twitch.Twitch;

/// <summary>
/// The fixed values Twitch offers for slow mode and ad breaks. The settings page
/// only has free number fields, so entered values are snapped to these steps.
/// </summary>
public static class TwitchSteps
{
    /// <summary>Slow mode wait times offered by Twitch, in seconds.</summary>
    public static readonly IReadOnlyList<int> SlowModeWaitSeconds = [3, 5, 10, 20, 30, 60, 120];

    /// <summary>Ad lengths in seconds. Helix caps a Start Commercial request at 180 s.</summary>
    public static readonly IReadOnlyList<int> AdLengthSeconds = [30, 60, 90, 120, 150, 180];

    public const int DefaultSlowModeWait = 30;
    public const int DefaultAdLength = 30;

    /// <summary>
    /// Returns the step closest to <paramref name="value"/>; on a tie the lower step wins.
    /// Values below the first or above the last step go to that step.
    /// </summary>
    public static int Snap(long value, IReadOnlyList<int> steps)
    {
        value = Math.Clamp(value, steps[0], steps[^1]); // also keeps the subtraction below from overflowing
        var best = steps[0];
        foreach (var step in steps)
        {
            // Strictly closer only, so the lower of two equally close steps is kept.
            if (Math.Abs(value - step) < Math.Abs(value - best))
                best = step;
        }

        return best;
    }

    public static int SnapSlowModeWait(long seconds) => Snap(seconds, SlowModeWaitSeconds);

    public static int SnapAdLength(long seconds) => Snap(seconds, AdLengthSeconds);

    /// <summary>"3, 5, 10" style list for setting descriptions.</summary>
    public static string Describe(IReadOnlyList<int> steps) => string.Join(", ", steps);
}
