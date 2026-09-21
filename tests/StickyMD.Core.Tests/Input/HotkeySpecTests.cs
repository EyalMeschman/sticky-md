using Shouldly;
using StickyMD.Core.Input;

namespace StickyMD.Core.Tests.Input;

public class HotkeySpecTests
{
    private static HotkeySpec Parse(string text)
    {
        HotkeySpec.TryParse(text, out var spec).ShouldBeTrue($"'{text}' should have parsed");
        return spec;
    }

    [Fact]
    public void The_default_new_note_hotkey_parses_to_ctrl_alt_and_the_letter_N()
    {
        var spec = Parse("Ctrl+Alt+N");

        spec.Modifiers.ShouldBe(HotkeySpec.ModControl | HotkeySpec.ModAlt);

        // VK_N is the ASCII code of 'N'. Pinned as the literal so a change to
        // the cast in TryKey cannot pass by agreeing with itself.
        spec.VirtualKey.ShouldBe(0x4Eu);
    }

    [Fact]
    public void The_default_show_hide_hotkey_parses()
    {
        var spec = Parse("Ctrl+Alt+S");

        spec.Modifiers.ShouldBe(HotkeySpec.ModControl | HotkeySpec.ModAlt);
        spec.VirtualKey.ShouldBe(0x53u);
    }

    [Theory]
    [InlineData("ctrl+alt+n")]
    [InlineData("CTRL+ALT+N")]
    [InlineData("Alt+Ctrl+N")]
    [InlineData("  ctrl + alt + n  ")]
    public void Case_order_and_spacing_all_reach_one_canonical_form(string text)
    {
        var spec = Parse(text);

        // The reason canonicalisation exists: two spellings of one hotkey in
        // settings.json look like two different settings, and the Settings
        // window would echo back whichever the user happened to type.
        spec.Canonical.ShouldBe("Ctrl+Alt+N");
        spec.Modifiers.ShouldBe(HotkeySpec.ModControl | HotkeySpec.ModAlt);
    }

    [Fact]
    public void Win_and_shift_are_understood()
    {
        var spec = Parse("Win+Shift+Space");

        spec.Modifiers.ShouldBe(HotkeySpec.ModWin | HotkeySpec.ModShift);
        spec.VirtualKey.ShouldBe(0x20u);
        spec.Canonical.ShouldBe("Shift+Win+Space");
    }

    [Theory]
    [InlineData("Ctrl+F1", 0x70u)]
    [InlineData("Ctrl+F12", 0x7Bu)]
    [InlineData("Ctrl+F24", 0x87u)]
    public void Function_keys_map_onto_VK_F1_upwards(string text, uint expected)
        => Parse(text).VirtualKey.ShouldBe(expected);

    [Fact]
    public void A_digit_key_maps_onto_its_ascii_code()
        => Parse("Ctrl+Alt+7").VirtualKey.ShouldBe(0x37u);

    [Fact]
    public void Prior_and_next_are_accepted_as_page_up_and_page_down()
    {
        // WPF's Key enum declares Prior and Next before PageUp and PageDown at
        // the same values, so Key.PageUp.ToString() is "Prior" -- which is
        // exactly the string the Settings capture box composes. Without these
        // aliases, pressing PageUp there would silently do nothing.
        Parse("Ctrl+Alt+Prior").VirtualKey.ShouldBe(0x21u);
        Parse("Ctrl+Alt+Next").VirtualKey.ShouldBe(0x22u);

        Parse("Ctrl+Alt+Prior").Canonical.ShouldBe("Ctrl+Alt+PageUp");
    }

    [Fact]
    public void A_bare_key_with_no_modifier_is_refused()
    {
        // NOT a style rule. RegisterHotKey accepts a bare key and then
        // swallows it system-wide for every other application, so a "hotkey" of
        // N would make the letter N unusable everywhere until StickyMD exits.
        HotkeySpec.TryParse("N", out _).ShouldBeFalse();
        HotkeySpec.TryParse("F5", out _).ShouldBeFalse();
    }

    [Fact]
    public void Modifiers_with_no_key_are_refused()
    {
        HotkeySpec.TryParse("Ctrl+Alt", out _).ShouldBeFalse();
        HotkeySpec.TryParse("Ctrl", out _).ShouldBeFalse();
    }

    [Fact]
    public void Two_non_modifier_keys_are_refused()
        => HotkeySpec.TryParse("Ctrl+A+B", out _).ShouldBeFalse();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl++N")]
    [InlineData("Ctrl+Alt+")]
    [InlineData("Ctrl+Alt+Tab")]
    [InlineData("Ctrl+Alt+F0")]
    [InlineData("Ctrl+Alt+F25")]
    [InlineData("Ctrl+Alt+Nope")]
    public void Unusable_strings_are_refused_without_throwing(string? text)
    {
        // Never a throw: the callers are a settings loader and a settings
        // dialog, and neither wants an exception for a value a human typed.
        HotkeySpec.TryParse(text, out var spec).ShouldBeFalse();
        spec.ShouldBeNull();
    }

    [Fact]
    public void No_repeat_is_not_part_of_a_parse()
    {
        // MOD_NOREPEAT is a registration concern, OR'd in by HotkeyManager. If
        // it leaked into Modifiers here, the canonical string and the parsed
        // flags would disagree about what the user asked for.
        (Parse("Ctrl+Alt+N").Modifiers & HotkeySpec.ModNoRepeat).ShouldBe(0u);
    }
}
