using Shouldly;
using StickyMD.Core.Persistence;
using StickyMD.Core.Theming;

namespace StickyMD.Core.Tests.Persistence;

public class StateValidatorTests
{
    private static readonly DateTime Now =
        new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);

    private static readonly AppSettings Defaults = new();

    private static NoteState Valid() => new(
        X: 100, Y: 100, W: 300, H: 340,
        Monitor: @"\\.\DISPLAY1",
        Color: NoteColor.Blue,
        Opacity: 0.9,
        AlwaysOnTop: true,
        IsOpen: true,
        LastOpenedUtc: Now.AddHours(-1));

    private static (NoteState State, List<ValidationIssue> Issues) Run(NoteState raw)
    {
        var issues = new List<ValidationIssue>();
        var state = StateValidator.ValidateNote(raw, Defaults, "note.md", Now, issues);
        return (state, issues);
    }

    [Fact]
    public void A_valid_state_passes_through_untouched_and_reports_nothing()
    {
        var (state, issues) = Run(Valid());

        state.ShouldBe(Valid());
        issues.ShouldBeEmpty();
    }

    [Fact]
    public void An_out_of_range_color_number_falls_back_to_the_default()
    {
        // This is the exact reported crash: {"color": 99} deserialises fine and
        // then throws ArgumentOutOfRangeException inside NotePalette.Get.
        var (state, issues) = Run(Valid() with { Color = (NoteColor)99 });

        state.Color.ShouldBe(Defaults.DefaultColor);
        issues.ShouldContain(i => i.Field == "color");
    }

    [Fact]
    public void The_defaulted_color_is_actually_usable_by_the_palette()
    {
        var (state, _) = Run(Valid() with { Color = (NoteColor)99 });

        Should.NotThrow(() => NotePalette.Get(state.Color, ThemeMode.Light));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.05)]
    [InlineData(-1.0)]
    public void An_opacity_below_the_floor_is_raised_to_it(double raw)
    {
        var (state, issues) = Run(Valid() with { Opacity = raw });

        state.Opacity.ShouldBe(StateValidator.MinOpacity);
        issues.ShouldContain(i => i.Field == "opacity");
    }

    [Fact]
    public void An_opacity_above_one_is_lowered_to_one()
    {
        var (state, issues) = Run(Valid() with { Opacity = 4.2 });

        state.Opacity.ShouldBe(1.0);
        issues.ShouldContain(i => i.Field == "opacity");
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void A_non_finite_opacity_falls_back_to_the_default(double raw)
    {
        // Math.Clamp(NaN, 0.2, 1.0) returns NaN, so clamping alone is not
        // enough -- Window.Opacity = NaN throws at assignment.
        var (state, issues) = Run(Valid() with { Opacity = raw });

        state.Opacity.ShouldBe(Defaults.DefaultOpacity);
        issues.ShouldContain(i => i.Field == "opacity");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-500)]
    public void A_non_positive_size_falls_back_to_the_default_size(int raw)
    {
        var (state, issues) = Run(Valid() with { W = raw, H = raw });

        state.W.ShouldBe(Defaults.DefaultWidth);
        state.H.ShouldBe(Defaults.DefaultHeight);
        issues.ShouldContain(i => i.Field == "w");
        issues.ShouldContain(i => i.Field == "h");
    }

    [Fact]
    public void A_size_below_the_minimum_is_raised_to_it()
    {
        var (state, issues) = Run(Valid() with { W = 12, H = 8 });

        state.W.ShouldBe(StateValidator.MinNoteWidth);
        state.H.ShouldBe(StateValidator.MinNoteHeight);
        issues.Count.ShouldBe(2);
    }

    [Fact]
    public void An_absurd_size_is_capped()
    {
        var (state, _) = Run(Valid() with { W = int.MaxValue, H = 999_999 });

        state.W.ShouldBe(StateValidator.MaxNoteEdge);
        state.H.ShouldBe(StateValidator.MaxNoteEdge);
    }

    [Fact]
    public void Coordinates_are_capped_so_the_placement_arithmetic_cannot_overflow()
    {
        // WindowPlacement computes Right = X + Width with int addition. A saved
        // int.MaxValue would overflow to a negative Right and the clamp would
        // silently place the note somewhere absurd.
        var (state, issues) = Run(Valid() with { X = int.MaxValue, Y = int.MinValue });

        state.X.ShouldBe(StateValidator.MaxCoordinate);
        state.Y.ShouldBe(-StateValidator.MaxCoordinate);
        issues.ShouldContain(i => i.Field == "x");
        issues.ShouldContain(i => i.Field == "y");
    }

    [Fact]
    public void Negative_coordinates_within_range_are_kept()
    {
        // A monitor left of the primary has negative coordinates. That is
        // normal, not invalid.
        var (state, issues) = Run(Valid() with { X = -1920, Y = -200 });

        state.X.ShouldBe(-1920);
        state.Y.ShouldBe(-200);
        issues.ShouldBeEmpty();
    }

    [Fact]
    public void A_whitespace_monitor_name_becomes_null_without_a_complaint()
    {
        var (state, issues) = Run(Valid() with { Monitor = "   " });

        state.Monitor.ShouldBeNull();
        issues.ShouldBeEmpty();
    }

    [Fact]
    public void A_non_utc_timestamp_is_reinterpreted_as_utc()
    {
        var unspecified = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Unspecified);

        var (state, _) = Run(Valid() with { LastOpenedUtc = unspecified });

        state.LastOpenedUtc.Kind.ShouldBe(DateTimeKind.Utc);
        state.LastOpenedUtc.ShouldBe(
            DateTime.SpecifyKind(unspecified, DateTimeKind.Utc));
    }

    [Fact]
    public void A_future_timestamp_is_pulled_back_to_now()
    {
        var (state, issues) = Run(Valid() with { LastOpenedUtc = DateTime.MaxValue });

        state.LastOpenedUtc.ShouldBe(Now);
        issues.ShouldContain(i => i.Field == "lastOpenedUtc");
    }

    [Fact]
    public void Every_issue_names_the_scope_it_came_from()
    {
        var (_, issues) = Run(Valid() with { Color = (NoteColor)99 });

        issues.ShouldAllBe(i => i.Scope == "note.md");
    }

    [Fact]
    public void Settings_with_an_unrooted_notes_root_fall_back_to_the_default()
    {
        var issues = new List<ValidationIssue>();

        var settings = StateValidator.ValidateSettings(
            new AppSettings { NotesRoot = "relative\\notes" }, issues);

        settings.NotesRoot.ShouldBe(AppSettings.DefaultNotesRoot);
        issues.ShouldContain(i => i.Field == "notesRoot");
    }

    [Fact]
    public void A_rooted_notes_root_is_canonicalised()
    {
        var issues = new List<ValidationIssue>();
        var messy = Path.Combine(Path.GetTempPath(), "sub", "..", "Notes");

        var settings = StateValidator.ValidateSettings(
            new AppSettings { NotesRoot = messy }, issues);

        settings.NotesRoot.ShouldBe(
            Path.Combine(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), "Notes"));
        issues.ShouldBeEmpty();
    }

    [Fact]
    public void An_out_of_range_theme_preference_falls_back_to_system()
    {
        var issues = new List<ValidationIssue>();

        var settings = StateValidator.ValidateSettings(
            new AppSettings { Theme = (ThemePreference)42 }, issues);

        settings.Theme.ShouldBe(ThemePreference.System);
        issues.ShouldContain(i => i.Field == "theme");
    }

    [Fact]
    public void An_out_of_range_default_color_falls_back_to_yellow()
    {
        var issues = new List<ValidationIssue>();

        var settings = StateValidator.ValidateSettings(
            new AppSettings { DefaultColor = (NoteColor)7 }, issues);

        settings.DefaultColor.ShouldBe(NoteColor.Yellow);
        issues.ShouldContain(i => i.Field == "defaultColor");
    }

    [Fact]
    public void A_blank_hotkey_falls_back_to_its_default_string()
    {
        var issues = new List<ValidationIssue>();

        var settings = StateValidator.ValidateSettings(
            new AppSettings { NewNoteHotkey = "  ", ShowHideHotkey = "" }, issues);

        settings.NewNoteHotkey.ShouldBe("Ctrl+Alt+N");
        settings.ShowHideHotkey.ShouldBe("Ctrl+Alt+S");
        issues.Count.ShouldBe(2);
    }

    [Fact]
    public void Valid_settings_report_nothing()
    {
        var issues = new List<ValidationIssue>();

        StateValidator.ValidateSettings(new AppSettings(), issues);

        issues.ShouldBeEmpty();
    }
}
