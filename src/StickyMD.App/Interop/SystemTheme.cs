using System.IO;
using Microsoft.Win32;
using StickyMD.Core.Persistence;
using StickyMD.Core.Theming;

namespace StickyMD.App.Interop;

/// <summary>
/// Resolves the user's <see cref="ThemePreference"/> into the
/// <see cref="ThemeMode"/> the palette needs, and reports OS theme changes.
/// </summary>
/// <remarks>
/// The two types are deliberately different. ThemePreference is what the user
/// chose and includes System; ThemeMode is the resolved light-or-dark that
/// NotePalette.Get takes. Collapsing them would force NotePalette to answer a
/// question whose input it cannot see -- it has no business reading the
/// registry.
/// </remarks>
public interface ISystemTheme
{
    ThemeMode Resolve(ThemePreference preference);

    /// <summary>
    /// Raised when the OS light/dark setting changes. Only meaningful while the
    /// preference is System, but raised regardless -- deciding what to do with
    /// it is the consumer's job.
    /// </summary>
    event Action? Changed;
}

public sealed class SystemTheme : ISystemTheme, IDisposable
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private const string AppsUseLightTheme = "AppsUseLightTheme";

    public event Action? Changed;

    public SystemTheme()
    {
        // SystemEvents rather than a hidden HwndSource listening for
        // WM_SETTINGCHANGE: it needs no message window, and Plan B has no
        // message window yet. Note that it raises on its own thread, so
        // consumers must marshal to the dispatcher.
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public ThemeMode Resolve(ThemePreference preference) => preference switch
    {
        ThemePreference.Light => ThemeMode.Light,
        ThemePreference.Dark => ThemeMode.Dark,
        _ => ReadOsTheme(),
    };

    private static ThemeMode ReadOsTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);

            // Absent means light. The value is missing on a fresh profile and
            // on editions that never had the setting, and light is what those
            // actually display.
            return key?.GetValue(AppsUseLightTheme) is int light && light == 0
                ? ThemeMode.Dark
                : ThemeMode.Light;
        }
        catch (System.Security.SecurityException) { return ThemeMode.Light; }
        catch (IOException) { return ThemeMode.Light; }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General
            or UserPreferenceCategory.VisualStyle
            or UserPreferenceCategory.Color)
        {
            Changed?.Invoke();
        }
    }

    public void Dispose()
        => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
}
