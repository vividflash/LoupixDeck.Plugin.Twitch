using System.Runtime.CompilerServices;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Twitch;

/// <summary>
/// Thin wrapper around <see cref="IPluginHost.Tr"/> (added in SDK 1.24.0), used for descriptor
/// texts that embed a runtime value (a redirect URL, a port, an account name, ...): the host's own
/// exact-text lookup for descriptor fields only matches fixed strings, so those need the fixed
/// template translated first, then <see cref="string.Format"/> with the value.
///
/// <para>
/// Only the SDK major version is checked at load, so a plugin built against 1.24.0 still loads on
/// an older host, where <see cref="IPluginHost"/> has no <c>Tr</c> member at all and the call
/// throws <see cref="MissingMethodException"/>. That is caught once, in <see cref="TryTr"/>, and
/// remembered so later calls skip straight to the English fallback instead of failing again.
/// </para>
/// </summary>
internal static class Localization
{
    private static IPluginHost? _host;
    private static bool _hostTrUnavailable;

    /// <summary>Called from <see cref="TwitchPlugin.Initialize"/>.</summary>
    internal static void SetHost(IPluginHost? host) => _host = host;

    /// <summary>Test-only: undoes <see cref="SetHost"/> and forgets any remembered
    /// <see cref="MissingMethodException"/>, so tests don't leak state into each other.</summary>
    internal static void ResetForTests()
    {
        _host = null;
        _hostTrUnavailable = false;
    }

    /// <summary>
    /// Translates <paramref name="english"/> for the host's current language. Returns
    /// <paramref name="english"/> unchanged when there is no host yet (unit tests, descriptors
    /// built before <c>Initialize</c>) or the host predates <c>Tr</c>.
    /// </summary>
    internal static string Tr(string english)
    {
        if (_host == null || _hostTrUnavailable) return english;
        try
        {
            return TryTr(_host, english);
        }
        catch (MissingMethodException)
        {
            // The runtime may throw while JIT-compiling TryTr, i.e. before its own catch is live.
            _hostTrUnavailable = true;
            return english;
        }
    }

    // Kept in its own, never-inlined method so a MissingMethodException on a host without Tr is
    // isolated here rather than failing whatever JITs the caller.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string TryTr(IPluginHost host, string english)
    {
        try
        {
            return host.Tr(english);
        }
        catch (MissingMethodException)
        {
            _hostTrUnavailable = true;
            return english;
        }
    }
}
