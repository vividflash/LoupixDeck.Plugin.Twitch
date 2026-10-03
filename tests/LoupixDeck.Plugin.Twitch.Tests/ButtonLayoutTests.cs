using LoupixDeck.Plugin.Twitch.Commands;
using LoupixDeck.Plugin.Twitch.Twitch;
using LoupixDeck.PluginSdk;
using Xunit;

namespace LoupixDeck.Plugin.Twitch.Tests;

public class ButtonLayoutTests
{
    private static IPluginCommand[] AllCommands()
    {
        var rig = new Rig();
        return
        [
            new SendChatMessageCommand(rig.Helix, rig.Logger),
            new CreateClipCommand(rig.Helix, rig.Logger),
            new ViewerCountCommand(new ViewerCountCache(_ => Task.FromResult<int?>(0), () => { }, rig.Logger), rig.Logger),
            new ClearChatCommand(rig.Helix, rig.Logger),
            new ToggleSlowChatCommand(rig.Helix, rig.Logger, () => 30),
            new ToggleEmotesOnlyCommand(rig.Helix, rig.Logger),
            new RunCommercialCommand(rig.Helix, rig.Logger, () => 30),
            new CreateStreamMarkerCommand(rig.Helix, rig.Logger)
        ];
    }

    [Fact]
    public void Every_command_declares_a_custom_layout_with_an_icon()
    {
        foreach (var command in AllCommands())
        {
            var d = command.Descriptor;
            Assert.NotNull(d.ButtonLayout);
            Assert.Equal(ButtonLayoutMode.Custom, d.ButtonLayout!.Mode);
            Assert.False(string.IsNullOrEmpty(d.Icon), d.CommandName);
            Assert.Contains(d.ButtonLayout.Layers, l => l.Kind == ButtonLayerKind.Symbol && l.Glyph == d.Icon);
        }
    }

    [Fact]
    public void Glyphs_are_single_mdi_code_points_and_unique_per_command()
    {
        var glyphs = AllCommands().Select(c => c.Descriptor.Icon!).ToList();

        foreach (var glyph in glyphs)
        {
            var codePoint = char.ConvertToUtf32(glyph, 0);
            Assert.Equal(glyph.Length, char.IsSurrogatePair(glyph, 0) ? 2 : 1);
            Assert.InRange(codePoint, 0xF0001, 0xFFFFD); // MDI private-use plane
        }

        Assert.Equal(glyphs.Count, glyphs.Distinct().Count());
    }

    [Fact]
    public void Layers_are_twitch_purple_icons_with_unique_names_and_valid_scale()
    {
        foreach (var command in AllCommands())
        {
            var layers = command.Descriptor.ButtonLayout!.Layers;
            var names = layers.Where(l => l.Name != null).Select(l => l.Name).ToList();
            Assert.Equal(names.Count, names.Distinct().Count());

            foreach (var symbol in layers.Where(l => l.Kind == ButtonLayerKind.Symbol))
            {
                Assert.Equal("#9146FF", symbol.Color);
                Assert.InRange(symbol.IconScale, 0.1, 1.0);
                Assert.Null(symbol.ImageData); // nothing embedded
            }
        }
    }

    [Fact]
    public void Viewer_count_layout_has_exactly_one_text_layer_for_the_host_to_hand_the_count_to()
    {
        var count = AllCommands().OfType<ViewerCountCommand>().Single();

        var texts = count.Descriptor.ButtonLayout!.Layers.Where(l => l.Kind == ButtonLayerKind.Text).ToList();

        Assert.Single(texts);
        Assert.False(string.IsNullOrEmpty(texts[0].Text));
    }

    [Fact]
    public void Press_commands_carry_a_short_one_word_caption()
    {
        foreach (var command in AllCommands().Where(c => c is TwitchCommandBase))
        {
            var caption = command.Descriptor.ButtonLayout!.Layers.Single(l => l.Kind == ButtonLayerKind.Text);
            Assert.False(string.IsNullOrEmpty(caption.Text), command.Descriptor.CommandName);
            Assert.True(caption.Text!.Length <= 9, caption.Text);
            Assert.DoesNotContain(' ', caption.Text);
        }
    }
}
