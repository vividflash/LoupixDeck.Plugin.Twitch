using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Twitch;

/// <summary>
/// Thin wrapper around <see cref="IPluginHost.Tr"/> (added in SDK 1.24.0), used for descriptor
/// texts that embed a runtime value (a redirect URL, a port, an account name, ...): the host's own
/// exact-text lookup for descriptor fields only matches fixed strings, so those need the fixed
/// template translated first, then <see cref="string.Format"/> with the value.
/// </summary>
internal static class Localization
{
    private static IPluginHost? _host;

    /// <summary>Called from <see cref="TwitchPlugin.Initialize"/>.</summary>
    internal static void SetHost(IPluginHost? host) => _host = host;

    /// <summary>Test-only: undoes <see cref="SetHost"/>, so tests don't leak state into each other.</summary>
    internal static void ResetForTests() => _host = null;

    /// <summary>
    /// Translates <paramref name="english"/> for the host's current language. Returns
    /// <paramref name="english"/> unchanged when there is no host yet (unit tests, descriptors
    /// built before <c>Initialize</c>).
    /// </summary>
    internal static string Tr(string english) => _host?.Tr(english) ?? english;
}
