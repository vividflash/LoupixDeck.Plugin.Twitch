using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Twitch.Twitch;

/// <summary>
/// Lazily polled viewer count. There is no background loop: the host only calls
/// a display command's GetText for buttons on the active touch page, and each
/// such call may start at most one fetch per <see cref="MinInterval"/>. So the
/// channel is polled only while a ViewerCount button is visible, and never more
/// often than once a minute. When a fetch changes the text, <c>onChanged</c>
/// asks the host to redraw right away.
/// </summary>
public sealed class ViewerCountCache
{
    public static readonly TimeSpan DefaultMinInterval = TimeSpan.FromSeconds(60);

    private readonly Func<CancellationToken, Task<int?>> _fetch;
    private readonly Action _onChanged;
    private readonly IPluginLogger _logger;
    private readonly Func<DateTime> _utcNow;
    private readonly object _gate = new();
    private DateTime _lastFetchStartUtc = DateTime.MinValue;
    private bool _inFlight;
    private bool _authBlocked;
    private string _text = "…";

    public TimeSpan MinInterval { get; }

    /// <summary>The fetch currently running, if any (tests await it).</summary>
    internal Task? CurrentFetch { get; private set; }

    public ViewerCountCache(
        Func<CancellationToken, Task<int?>> fetch,
        Action onChanged,
        IPluginLogger logger,
        TimeSpan? minInterval = null,
        Func<DateTime>? utcNow = null)
    {
        _fetch = fetch;
        _onChanged = onChanged;
        _logger = logger;
        MinInterval = minInterval ?? DefaultMinInterval;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>Called from GetText: returns the cached text, starting a fetch if one is due.</summary>
    public string GetText()
    {
        lock (_gate)
        {
            var now = _utcNow();
            if (!_inFlight && !_authBlocked && now - _lastFetchStartUtc >= MinInterval)
            {
                _inFlight = true;
                _lastFetchStartUtc = now;
                CurrentFetch = Task.Run(FetchAsync);
            }

            return _text;
        }
    }

    /// <summary>Forget the throttle and any sign-in block (after sign-in/out or settings change).</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _authBlocked = false;
            _lastFetchStartUtc = DateTime.MinValue;
            _text = "…";
        }

        SafeNotify();
    }

    internal static string Format(int? viewers) => viewers is { } n ? n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) : "offline";

    private async Task FetchAsync()
    {
        string text;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            text = Format(await _fetch(cts.Token).ConfigureAwait(false));
        }
        catch (TwitchAuthRequiredException ex)
        {
            _logger.Warn($"Twitch.ViewerCount: {ex.Message}");
            lock (_gate) _authBlocked = true;
            text = "sign in";
        }
        catch (Exception ex)
        {
            _logger.Warn($"Twitch.ViewerCount: poll failed: {ex.Message}");
            text = "error";
        }

        bool changed;
        lock (_gate)
        {
            changed = text != _text;
            _text = text;
            _inFlight = false;
        }

        if (changed) SafeNotify();
    }

    private void SafeNotify()
    {
        try { _onChanged(); }
        catch { /* host not ready or no bound button */ }
    }
}
