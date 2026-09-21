using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StickyMD.App.Services;
using StickyMD.Core.Input;
using StickyMD.Core.Persistence;
using StickyMD.Core.Theming;

namespace StickyMD.App.Windows;

/// <summary>
/// The Settings window: everything in <c>settings.json</c>, plus the Run-key
/// checkbox.
/// </summary>
/// <remarks>
/// SHOWN NON-MODAL. <c>ShowDialog</c> would spin a nested dispatcher frame and
/// freeze every open note -- including the file watcher's marshalled handlers
/// and the autosave ticks -- for as long as Settings was open. <c>App</c> keeps
/// the single instance and focuses it on a second request.
///
/// LAUNCH AT STARTUP IS NOT PART OF <c>AppSettings</c>, and must not become so.
/// Spec §5: the Run registry key is the single source of truth, and this
/// checkbox reads it back rather than trusting cached state, because cleanup
/// tools strip these entries behind the app's back. It is applied by
/// <see cref="StartupManager"/> on Save, not on toggle, so Cancel really does
/// cancel.
/// </remarks>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _original;
    // Fully qualified: .NET 10's WPF added System.Windows.ThemeMode, and
    // `using System.Windows` plus `using StickyMD.Core.Theming` makes the bare
    // name ambiguous. NoteWindow qualifies it the same way for the same reason.
    private readonly StickyMD.Core.Theming.ThemeMode _resolvedMode;
    private readonly StartupManager _startup;
    private readonly Func<AppSettings, string?> _apply;

    private NoteColor _colour;

    public SettingsWindow(
        AppSettings settings,
        StickyMD.Core.Theming.ThemeMode resolvedMode,
        StartupManager startup,
        IReadOnlyList<HotkeyFailure> hotkeyFailures,
        string? banner,
        Func<AppSettings, string?> apply)
    {
        InitializeComponent();

        _original = settings;
        _resolvedMode = resolvedMode;
        _startup = startup;
        _apply = apply;
        _colour = settings.DefaultColor;

        NotesRootBox.Text = settings.NotesRoot;
        WidthBox.Text = settings.DefaultWidth.ToString();
        HeightBox.Text = settings.DefaultHeight.ToString();
        RemoteImagesCheck.IsChecked = settings.AllowRemoteImages;
        StartHiddenCheck.IsChecked = settings.StartHidden;
        NewNoteHotkeyBox.Text = settings.NewNoteHotkey;
        ShowHideHotkeyBox.Text = settings.ShowHideHotkey;

        // The boxes keep their text when disabled, so re-ticking gives the
        // user back the combinations they had.
        HotkeysCheck.IsChecked = settings.HotkeysEnabled;
        HotkeyGrid.IsEnabled = settings.HotkeysEnabled;
        HotkeysCheck.Click += (_, _) => HotkeyGrid.IsEnabled = HotkeysCheck.IsChecked == true;

        OpacitySlider.Value = Math.Clamp(settings.DefaultOpacity * 100, 30, 100);
        OpacityReadout.Text = $"{OpacitySlider.Value:0}%";
        OpacitySlider.ValueChanged += (_, e) => OpacityReadout.Text = $"{e.NewValue:0}%";

        // Bounds from StateValidator, not from XAML: a slider that can produce
        // a value the validator corrects means the user watches their own
        // choice change by itself on the next start.
        FontSizeSlider.Minimum = StateValidator.MinFontSizePx;
        FontSizeSlider.Maximum = StateValidator.MaxFontSizePx;
        FontSizeSlider.Value = Math.Clamp(
            settings.DefaultFontSizePx, StateValidator.MinFontSizePx, StateValidator.MaxFontSizePx);
        FontSizeReadout.Text = $"{FontSizeSlider.Value:0}px";
        FontSizeSlider.ValueChanged += (_, e) => FontSizeReadout.Text = $"{e.NewValue:0}px";

        ThemeBox.ItemsSource = Enum.GetValues<ThemePreference>();
        ThemeBox.SelectedItem = settings.Theme;

        // The registry, not a remembered value. See the class remarks.
        StartupCheck.IsChecked = startup.IsEnabled;

        BuildSwatches();
        WireHotkeyCapture(NewNoteHotkeyBox);
        WireHotkeyCapture(ShowHideHotkeyBox);
        FlagFailedHotkeys(hotkeyFailures);

        if (banner is not null) ShowBanner(banner);

        BrowseButton.Click += (_, _) => BrowseForNotesRoot();
        SaveButton.Click += (_, _) => Save();
        CancelButton.Click += (_, _) => Close();

        // Escape closes, which IsCancel would have done on a modal window. On
        // the hotkey boxes it arrives here because their capture handler lets
        // Escape and Tab through on purpose.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        };
    }

    /// <summary>
    /// Spec §7: a hotkey that Windows refused is "flagged in Settings".
    /// </summary>
    /// <remarks>
    /// Matched on the COMBINATION rather than on which hotkey it was, because
    /// <see cref="HotkeyManager"/> reports what it tried to register and both
    /// boxes can legitimately hold the same text -- which is itself one of the
    /// ways a registration fails.
    /// </remarks>
    private void FlagFailedHotkeys(IReadOnlyList<HotkeyFailure> failures)
    {
        string ReasonFor(string combination) => failures.FirstOrDefault(
            f => string.Equals(f.Combination, combination, StringComparison.OrdinalIgnoreCase))
            ?.Reason ?? string.Empty;

        NewNoteHotkeyFlag.Text = ReasonFor(NewNoteHotkeyBox.Text);
        ShowHideHotkeyFlag.Text = ReasonFor(ShowHideHotkeyBox.Text);
    }

    /// <summary>
    /// The seven palette colours as clickable swatches.
    /// </summary>
    /// <remarks>
    /// Swatches rather than a list of colour names, for the same reason the
    /// <c>⋯</c> menu's Color submenu is swatches: a text list is the wrong
    /// control for choosing a colour, because you cannot see what you are
    /// picking. Each one paints <c>ContentBg</c> -- the large surface the user
    /// will actually be looking at -- read from <c>NotePalette</c> so nothing
    /// here invents a value that could drift from a real note's.
    /// </remarks>
    private void BuildSwatches()
    {
        ColorSwatches.Children.Clear();

        foreach (var colour in NotePalette.All)
        {
            var theme = NotePalette.Get(colour, _resolvedMode);
            var selected = colour == _colour;
            var chosen = colour;

            var swatch = new Border
            {
                Width = 24,
                Height = 24,
                Margin = new Thickness(0, 0, 4, 0),
                CornerRadius = new CornerRadius(4),
                Background = Hex.Brush(theme.ContentBg),
                BorderThickness = new Thickness(selected ? 2 : 1),
                BorderBrush = Hex.Brush(selected ? theme.Accent : theme.Border),
                Cursor = Cursors.Hand,
                ToolTip = colour.ToString(),
            };

            swatch.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                _colour = chosen;

                // Rebuilt rather than nudged: the selected ring moves, so two
                // swatches change at once and there is no partial state worth
                // tracking.
                BuildSwatches();
            };

            ColorSwatches.Children.Add(swatch);
        }
    }

    /// <summary>
    /// Turns a read-only box into a hotkey capture field.
    /// </summary>
    /// <remarks>
    /// A capture field rather than a free-text box because a hotkey typed as
    /// text is a hotkey the user has to spell the app's way -- and the first
    /// thing anyone tries is pressing the keys.
    ///
    /// Every candidate goes through <see cref="HotkeySpec.TryParse"/>, so a key
    /// StickyMD cannot register simply leaves the box as it was. That is the
    /// right answer and it needs no error message: nothing changed, so nothing
    /// broke.
    /// </remarks>
    private static void WireHotkeyCapture(TextBox box)
    {
        box.PreviewKeyDown += (_, e) =>
        {
            // Alt combinations arrive as Key.System with the real key in
            // SystemKey. Without this, every Alt hotkey reads as "System".
            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            // Tab must still move focus and Escape must still close the
            // window, or a click into this box is a trap.
            if (key is Key.Tab or Key.Escape) return;

            // Nothing else may reach a read-only text box: without this, keys
            // that do not form a hotkey still ring the system bell.
            e.Handled = true;

            if (key is Key.LeftCtrl or Key.RightCtrl
                or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift
                or Key.LWin or Key.RWin
                or Key.System or Key.None)
            {
                // A modifier on its own. Wait for the real key rather than
                // clearing what is there.
                return;
            }

            var candidate = Compose(Keyboard.Modifiers, key);

            if (HotkeySpec.TryParse(candidate, out var spec)) box.Text = spec.Canonical;
        };
    }

    private static string Compose(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>(4);
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyName(key));
        return string.Join('+', parts);
    }

    /// <remarks>
    /// WPF's <c>Key</c> names already match <see cref="HotkeySpec"/>'s
    /// vocabulary for almost everything it accepts -- "A", "F5", "Space",
    /// "Insert", "Home", "Up". The digits are the exception: they are
    /// <c>D0</c>..<c>D9</c>. <c>Prior</c>/<c>Next</c> are handled on the other
    /// side, in HotkeySpec's alias table, because WPF's enum declares them
    /// before PageUp/PageDown and <c>ToString()</c> returns the first name at
    /// a duplicated value.
    /// </remarks>
    private static string KeyName(Key key)
    {
        var name = key.ToString();

        return name.Length == 2 && name[0] == 'D' && char.IsAsciiDigit(name[1])
            ? name[1..]
            : name;
    }

    private void BrowseForNotesRoot()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose the notes folder",
            InitialDirectory = Directory.Exists(NotesRootBox.Text)
                ? NotesRootBox.Text
                : string.Empty,
        };

        if (dialog.ShowDialog() == true) NotesRootBox.Text = dialog.FolderName;
    }

    private void Save()
    {
        if (!int.TryParse(WidthBox.Text.Trim(), out var width)
            || !int.TryParse(HeightBox.Text.Trim(), out var height))
        {
            ShowBanner("Width and height have to be whole numbers of pixels.");
            return;
        }

        var root = NotesRootBox.Text.Trim();

        if (root.Length == 0 || !Path.IsPathRooted(root))
        {
            // The same rule StateValidator applies at load: a relative root
            // would resolve against the process working directory, which for a
            // startup launch is arbitrary, and notes would appear to vanish
            // depending on how the app started.
            ShowBanner("The notes folder has to be a full path, drive letter and all.");
            return;
        }

        var candidate = _original with
        {
            NotesRoot = root,
            DefaultColor = _colour,
            DefaultOpacity = OpacitySlider.Value / 100.0,
            DefaultWidth = width,
            DefaultHeight = height,
            DefaultFontSizePx = (int)Math.Round(FontSizeSlider.Value),
            Theme = (ThemePreference)ThemeBox.SelectedItem,
            NewNoteHotkey = NewNoteHotkeyBox.Text,
            ShowHideHotkey = ShowHideHotkeyBox.Text,
            HotkeysEnabled = HotkeysCheck.IsChecked == true,
            AllowRemoteImages = RemoteImagesCheck.IsChecked == true,
            StartHidden = StartHiddenCheck.IsChecked == true,
        };

        // Through the SAME validator the loader uses. Settings must not be able
        // to write a value settings.json would then quietly correct on the next
        // start -- the user would see their choice change by itself.
        candidate = StateValidator.ValidateSettings(candidate, []);

        var wantsStartup = StartupCheck.IsChecked == true;

        if (wantsStartup != _startup.IsEnabled && !_startup.TrySet(wantsStartup))
        {
            ShowBanner(
                "Windows refused to change the Launch at Startup entry. Nothing else was saved; "
                    + "the reason is in diagnostics.log.");
            return;
        }

        if (_apply(candidate) is { } failure)
        {
            ShowBanner(failure);
            return;
        }

        Close();
    }

    private void ShowBanner(string message)
    {
        BannerText.Text = message;
        Banner.Visibility = Visibility.Visible;
    }
}
