namespace LoupixDeck.Plugin.Twitch.Twitch;

/// <summary>A Twitch (Helix or OAuth) call returned a non-success status.</summary>
public sealed class TwitchApiException : Exception
{
    public int StatusCode { get; }

    public TwitchApiException(int statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
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
