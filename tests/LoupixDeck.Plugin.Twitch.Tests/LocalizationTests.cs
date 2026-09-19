using System.Text.Json;
using System.Text.RegularExpressions;
using LoupixDeck.Plugin.Twitch.Commands;
using LoupixDeck.PluginSdk;
using Xunit;

namespace LoupixDeck.Plugin.Twitch.Tests;

/// <summary>
/// Localization.Tr (see Localization.cs) is the one place every runtime-composed descriptor and
/// badge text goes through. These tests drive its fallback paths directly: no host set, a host
/// whose SDK predates Tr (MissingMethodException), and a host that actually translates. Every
/// test resets the static host afterward (Localization holds it in a static field) so it can't
/// leak into other tests; Fakes.cs also disables cross-class parallelization for the assembly
/// for the same reason.
/// </summary>
public class LocalizationTests : IDisposable
{
    public LocalizationTests() => Localization.ResetForTests();
    public void Dispose() => Localization.ResetForTests();

    [Fact]
    public void No_host_set_returns_english_unchanged()
    {
        Assert.Equal("Sign in", Localization.Tr("Sign in"));
    }

    [Fact]
    public void Host_without_Tr_falls_back_to_english_and_is_not_retried()
    {
        var calls = 0;
        var host = new FakeHost { Translate = _ => { calls++; throw new MissingMethodException(); } };
        Localization.SetHost(host);

        // First call hits the host and fails; later calls must skip straight to English instead
        // of calling (and failing on) host.Tr again.
        Assert.Equal("Sign in", Localization.Tr("Sign in"));
        Assert.Equal("Failed", Localization.Tr("Failed"));

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Host_translation_is_used_when_available()
    {
        Localization.SetHost(new FakeHost { Translate = s => "[DE] " + s });

        Assert.Equal("[DE] Sign in", Localization.Tr("Sign in"));
    }
}

/// <summary>
/// A fake host that returns a visibly prefixed translation is enough to prove the plumbing from
/// command/plugin code down to <see cref="IPluginHost.Tr"/> works, without needing real German or
/// Spanish text (that correctness is covered by <see cref="StringsFileConsistencyTests"/>).
/// </summary>
public class TranslatedDisplayTests : IDisposable
{
    public TranslatedDisplayTests() => Localization.ResetForTests();
    public void Dispose() => Localization.ResetForTests();

    [Fact]
    public async Task Touch_badge_text_is_translated()
    {
        var host = new FakeHost { Translate = s => "[DE] " + s };
        Localization.SetHost(host);

        var rig = new Rig();
        rig.Handler.Respond(System.Net.HttpStatusCode.OK, """{"data":[{"is_sent":true}]}""");
        var cmd = new SendChatMessageCommand(rig.Helix, rig.Logger, () => rig.Now);
        var ctx = new CommandContext { Parameters = ["hi"], Target = ButtonTargets.TouchButton, Host = host };

        await cmd.Execute(ctx);

        var canvas = new FakeCanvas();
        Assert.True(cmd.RenderImage(ctx, canvas));
        Assert.Equal("[DE] Sent", Assert.Single(canvas.DrawnTexts));
    }

    [Fact]
    public async Task Dial_feedback_text_is_translated()
    {
        var host = new FakeHost { Translate = s => "[DE] " + s };
        Localization.SetHost(host);

        var rig = new Rig();
        rig.Handler.Respond(System.Net.HttpStatusCode.OK, """{"data":[{"id":"clip-1"}]}""");
        var ctx = new CommandContext { Parameters = [], Target = ButtonTargets.RotaryEncoder, SourceIndex = 0, Host = host };

        await new CreateClipCommand(rig.Helix, rig.Logger, () => rig.Now).Execute(ctx);

        Assert.Equal((10, "[DE] Clipped"), Assert.Single(host.Overlays));
    }

    [Fact]
    public void AccountStatus_description_is_translated()
    {
        Localization.SetHost(new FakeHost { Translate = s => "[DE] " + s });

        var text = TwitchPlugin.AccountStatus("yourchannel", null, []);

        Assert.Equal("[DE] Signed in as yourchannel. The token is stored encrypted for your Windows user.", text);
    }

    [Fact]
    public void RunAd_success_feedback_is_translated()
    {
        Localization.SetHost(new FakeHost { Translate = s => "[DE] " + s });

        Assert.Equal("[DE] Ad 30s", string.Format(Localization.Tr("Ad {0}s"), 30));
    }
}

/// <summary>
/// Every <c>Localization.Tr("...")</c> call with a literal English argument (i.e. every fixed
/// template, not the handful of calls that pass through an already-computed variable, such as the
/// shared touch-badge/dial-feedback text) must have a matching key in both string files, and both
/// files must offer exactly the same set of keys. Source files are read directly (not from the
/// test's own output) so this catches a missing translation without needing a matching runtime
/// path exercised elsewhere.
/// </summary>
public class StringsFileConsistencyTests
{
    // Matches Localization.Tr(...) where the argument is one or more "..." literals joined by +
    // (i.e. never an interpolated or variable argument - see Localization.cs remarks).
    private static readonly Regex TrCall = new(
        @"Localization\.Tr\(\s*((?:""(?:[^""\\]|\\.)*""\s*\+?\s*)+)\)", RegexOptions.Compiled);

    private static readonly Regex StringLiteral = new(@"""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LoupixDeck.Plugin.Twitch.slnx")))
            dir = dir.Parent;
        if (dir == null)
            throw new InvalidOperationException($"Could not find the repo root above {AppContext.BaseDirectory}.");
        return dir.FullName;
    }

    private static string SrcDir => Path.Combine(FindRepoRoot(), "src", "LoupixDeck.Plugin.Twitch");

    /// <summary>Every literal key that <c>Localization.Tr("...")</c> is called with, across every
    /// .cs file under the plugin's src directory.</summary>
    private static HashSet<string> LiteralTrKeysInSource()
    {
        var keys = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(SrcDir, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (Match call in TrCall.Matches(text))
            {
                var key = string.Concat(StringLiteral.Matches(call.Groups[1].Value)
                    .Select(m => m.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\")));
                keys.Add(key);
            }
        }

        return keys;
    }

    private static Dictionary<string, string> LoadStrings(string fileName) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(SrcDir, fileName)))!;

    [Fact]
    public void Every_literal_Tr_key_found_in_source_has_a_German_and_Spanish_translation()
    {
        var keys = LiteralTrKeysInSource();
        Assert.NotEmpty(keys); // sanity: the scan itself must find something

        var de = LoadStrings("strings.de.json");
        var es = LoadStrings("strings.es.json");

        foreach (var key in keys)
        {
            Assert.True(de.ContainsKey(key), $"strings.de.json is missing key: {key}");
            Assert.True(es.ContainsKey(key), $"strings.es.json is missing key: {key}");
        }
    }

    [Fact]
    public void German_and_Spanish_string_files_have_identical_key_sets()
    {
        var de = LoadStrings("strings.de.json");
        var es = LoadStrings("strings.es.json");

        var onlyInDe = de.Keys.Except(es.Keys).ToArray();
        var onlyInEs = es.Keys.Except(de.Keys).ToArray();

        Assert.True(onlyInDe.Length == 0, $"Keys only in strings.de.json: {string.Join(", ", onlyInDe)}");
        Assert.True(onlyInEs.Length == 0, $"Keys only in strings.es.json: {string.Join(", ", onlyInEs)}");
    }
}

/// <summary>
/// The touch-button badge shrinks its font (12pt down to 7pt, see
/// <c>TwitchCommandBase.FitFontSize</c>) rather than truncating, so a translated word only risks
/// looking cramped, never cut off. These checks use the same deterministic
/// <see cref="FakeCanvas.MeasureText"/> stand-in as every other badge test to confirm the longest
/// new German/Spanish badge texts still fit comfortably above the 7pt floor.
/// </summary>
public class BadgeFitTests
{
    private static readonly FakeCanvas Canvas = new();
    private const float MaxWidth = 90 - TwitchCommandBase.BadgeMargin * 2 - TwitchCommandBase.BadgeTextPadding; // 76

    [Theory]
    [InlineData("Emotes AUS")] // German, longest fixed badge word
    [InlineData("Clip creado")] // Spanish, longest fixed badge word
    [InlineData("Iniciar sesión")] // Spanish "Sign in" badge
    [InlineData("Cooldown 99:59")] // worst-case dynamic countdown text
    [InlineData("Werbung 180s")] // German "Ad {0}s" at the longest ad length
    [InlineData("Anuncio 180s")] // Spanish "Ad {0}s" at the longest ad length
    public void Longest_translated_badge_text_stays_well_above_the_minimum_font_size(string text)
    {
        var size = TwitchCommandBase.FitFontSize(Canvas, text, MaxWidth);

        Assert.True(size > TwitchCommandBase.MinFontSize + 1,
            $"'{text}' only fits at {size}pt, too close to the {TwitchCommandBase.MinFontSize}pt floor.");
    }
}
