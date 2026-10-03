using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Twitch.Commands;

/// <summary>
/// The layers every Twitch command brings to a touch button when it is assigned: a Twitch-purple
/// icon and a short one-line caption. The host creates them once, at assignment, as ordinary
/// layers the user can edit; the plugin's own drawing still renders on top at runtime.
///
/// <para>
/// Icon and caption are centred on the key like the host's own plugins do it. The press commands'
/// result badge (bottom 26 px band, see <see cref="TwitchCommandBase"/>) covers the caption while
/// it shows. The viewer-count button has no badge: the host hands its count to the first text
/// layer on the button (the command owns it from then on), so that layer is the number itself.
/// Captions are translated by the caller; the host would otherwise fill in the untranslated
/// display name.
/// </para>
/// </summary>
internal static class TwitchButtonLayouts
{
    // Material Design Icons code points, one per command so a command and its layout cannot drift apart.
    public const string SendMessage = "\U000F0369"; // mdi-message-text
    public const string Clip = "\U000F0230";        // mdi-filmstrip
    public const string Viewers = "\U000F0849";     // mdi-account-group
    public const string ClearChat = "\U000F1411";   // mdi-chat-remove
    public const string SlowChat = "\U000F04C5";    // mdi-speedometer
    public const string EmotesOnly = "\U000F0C71";  // mdi-emoticon-happy
    public const string Ad = "\U000F192A";          // mdi-advertisements
    public const string Marker = "\U000F00C0";      // mdi-bookmark

    internal const string TwitchPurple = "#9146FF";

    // Pixel values for a 90 px key; the host scales them onto the key actually being written.
    private const double CaptionIconScale = 0.5;
    private const int CaptionIconOffsetY = -9;
    private const int CaptionSize = 11;
    private const int CaptionOffsetY = 27;
    private const int CaptionBoxWidth = 88;
    private const int CaptionBoxHeight = 22;

    private const double CountIconScale = 0.4;
    private const int CountIconOffsetY = -17;
    private const int CountTextSize = 22;
    private const int CountTextOffsetY = 19;
    private const int CountBoxWidth = 84;
    private const int CountBoxHeight = 30;

    /// <summary>Icon above a one-line caption, centred on the key.</summary>
    public static ButtonLayoutDescriptor IconWithCaption(string glyph, string caption) => new()
    {
        Mode = ButtonLayoutMode.Custom,
        Layers =
        [
            new ButtonLayerDescriptor
            {
                Kind = ButtonLayerKind.Symbol,
                Glyph = glyph,
                Color = TwitchPurple,
                IconScale = CaptionIconScale,
                OffsetY = CaptionIconOffsetY
            },
            new ButtonLayerDescriptor
            {
                Kind = ButtonLayerKind.Text,
                Text = caption,
                TextSize = CaptionSize,
                OffsetY = CaptionOffsetY,
                BoxWidth = CaptionBoxWidth,
                BoxHeight = CaptionBoxHeight
            }
        ]
    };

    /// <summary>Icon above the live number. The text layer's placeholder is replaced by the count
    /// on the first refresh.</summary>
    public static ButtonLayoutDescriptor IconWithCount(string glyph) => new()
    {
        Mode = ButtonLayoutMode.Custom,
        Layers =
        [
            new ButtonLayerDescriptor
            {
                Kind = ButtonLayerKind.Symbol,
                Glyph = glyph,
                Color = TwitchPurple,
                IconScale = CountIconScale,
                OffsetY = CountIconOffsetY
            },
            new ButtonLayerDescriptor
            {
                Kind = ButtonLayerKind.Text,
                Text = "-",
                TextSize = CountTextSize,
                OffsetY = CountTextOffsetY,
                BoxWidth = CountBoxWidth,
                BoxHeight = CountBoxHeight
            }
        ]
    };
}
