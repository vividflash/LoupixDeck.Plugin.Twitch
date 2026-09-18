namespace LoupixDeck.Plugin.Twitch.Twitch;

/// <summary>A Twitch (Helix or OAuth) call returned a non-success status.</summary>
public sealed class TwitchApiException : Exception
{
    public int StatusCode { get; }

    /// <summary>Optional short text for dial feedback (e.g. "Offline"); null means "Failed".</summary>
    public string? Feedback { get; }

    public TwitchApiException(int statusCode, string message, string? feedback = null) : base(message)
    {
        StatusCode = statusCode;
        Feedback = feedback;
    }
}

/// <summary>
/// No usable token: never signed in, the refresh token was rejected, or a
/// freshly refreshed token was still refused. The user has to sign in again.
/// </summary>
public sealed class TwitchAuthRequiredException : Exception
{
    public TwitchAuthRequiredException(string message) : base(message) { }
}
