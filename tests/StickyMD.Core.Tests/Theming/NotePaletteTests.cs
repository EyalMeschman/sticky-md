using System.Text.RegularExpressions;
using Shouldly;
using StickyMD.Core.Theming;

namespace StickyMD.Core.Tests.Theming;

public class NotePaletteTests
{
    private static readonly Regex Hex = new("^#[0-9A-F]{6}$", RegexOptions.Compiled);

    [Fact]
    public void Exposes_exactly_seven_colors()
        => NotePalette.All.Count.ShouldBe(7);

    [Fact]
    public void All_matches_the_enum()
        => NotePalette.All.ShouldBe(Enum.GetValues<NoteColor>());

    [Fact]
    public void Every_color_and_mode_has_a_theme()
    {
        foreach (var color in NotePalette.All)
        foreach (var mode in Enum.GetValues<ThemeMode>())
            NotePalette.Get(color, mode).ShouldNotBeNull();
    }

    [Fact]
    public void Every_theme_value_is_an_uppercase_six_digit_hex()
    {
        foreach (var color in NotePalette.All)
        foreach (var mode in Enum.GetValues<ThemeMode>())
        {
            var theme = NotePalette.Get(color, mode);
            foreach (var value in Values(theme))
                Hex.IsMatch(value).ShouldBeTrue($"{color}/{mode} has bad hex '{value}'");
        }
    }

    [Fact]
    public void Light_and_dark_content_backgrounds_differ_for_every_color()
    {
        // Charcoal is included because its ContentBg does differ across modes, even though
        // several of its other fields (ChromeFg, ContentFg, Accent, Muted) are intentionally shared.
        foreach (var color in NotePalette.All)
            NotePalette.Get(color, ThemeMode.Light).ContentBg
                .ShouldNotBe(NotePalette.Get(color, ThemeMode.Dark).ContentBg, $"{color}");
    }

    [Fact]
    public void ToCssVariables_emits_all_eight_note_prefixed_keys()
    {
        var vars = NotePalette.ToCssVariables(
            NotePalette.Get(NoteColor.Yellow, ThemeMode.Light));

        vars.Keys.ShouldBe(new[]
        {
            "--note-chrome-bg", "--note-chrome-fg", "--note-border",
            "--note-content-bg", "--note-content-fg", "--note-accent",
            "--note-code-bg", "--note-muted",
        }, ignoreOrder: true);
    }

    [Fact]
    public void ToCssVariables_carries_the_theme_values_through()
    {
        var theme = NotePalette.Get(NoteColor.Blue, ThemeMode.Dark);

        var vars = NotePalette.ToCssVariables(theme);

        vars["--note-chrome-bg"].ShouldBe(theme.ChromeBg);
        vars["--note-chrome-fg"].ShouldBe(theme.ChromeFg);
        vars["--note-border"].ShouldBe(theme.Border);
        vars["--note-content-bg"].ShouldBe(theme.ContentBg);
        vars["--note-content-fg"].ShouldBe(theme.ContentFg);
        vars["--note-accent"].ShouldBe(theme.Accent);
        vars["--note-code-bg"].ShouldBe(theme.CodeBg);
        vars["--note-muted"].ShouldBe(theme.Muted);
    }

    [Fact]
    public void Get_stamps_the_theme_with_the_mode_it_was_resolved_for()
    {
        NotePalette.Get(NoteColor.Yellow, ThemeMode.Dark).Mode.ShouldBe(ThemeMode.Dark);
        NotePalette.Get(NoteColor.Yellow, ThemeMode.Light).Mode.ShouldBe(ThemeMode.Light);
    }

    private static IEnumerable<string> Values(NoteTheme t) =>
        [t.ChromeBg, t.ChromeFg, t.Border, t.ContentBg,
         t.ContentFg, t.Accent, t.CodeBg, t.Muted];
}
