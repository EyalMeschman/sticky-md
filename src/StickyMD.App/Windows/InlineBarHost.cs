using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StickyMD.Core.Theming;

namespace StickyMD.App.Windows;

/// <param name="Id">
/// Identity, so the same condition re-reported replaces its own bar instead of
/// stacking a second copy. An external edit arriving three times in a second
/// must not produce three bars.
/// </param>
/// <param name="PrimaryKeepsBar">
/// True when the primary action only diagnoses the condition rather than
/// resolving it -- an action that must not remove the explanation of a
/// condition that is still true. Defaults to false: dismiss-first remains
/// correct for every action that DOES resolve its condition (Reload, Retry,
/// Recreate, Restore, Load remote images, ...), so this only needs setting
/// on the few bars where the button is a side-door, not a fix.
/// </param>
/// <param name="SecondaryKeepsBar">The same, for the secondary action.</param>
public sealed record InlineBarRequest(
    string Id,
    string Message,
    string? PrimaryAction = null,
    Action? OnPrimary = null,
    string? SecondaryAction = null,
    Action? OnSecondary = null,
    bool Dismissible = true,
    bool PrimaryKeepsBar = false,
    bool SecondaryKeepsBar = false);

/// <summary>
/// The note's message bars. One per condition, newest at the top.
/// </summary>
/// <remarks>
/// These overlay note content, which is only legitimate because
/// WebView2CompositionControl renders through D3DImage rather than an HwndHost
/// -- airspace does not apply. With the plain WebView2 the bar would be
/// painted over by the browser regardless of z-order.
/// </remarks>
public sealed class InlineBarHost(Panel host)
{
    private readonly Dictionary<string, FrameworkElement> _bars = new(StringComparer.Ordinal);

    private NoteTheme? _theme;

    public void ApplyTheme(NoteTheme theme)
    {
        _theme = theme;
        foreach (var bar in _bars.Values) Paint(bar, theme);
    }

    public void Show(InlineBarRequest request)
    {
        Dismiss(request.Id);

        var bar = Build(request);
        _bars[request.Id] = bar;
        host.Children.Insert(0, bar);
    }

    public void Dismiss(string id)
    {
        if (!_bars.Remove(id, out var bar)) return;
        host.Children.Remove(bar);
    }

    public void DismissAll()
    {
        foreach (var bar in _bars.Values) host.Children.Remove(bar);
        _bars.Clear();
    }

    public bool IsShowing(string id) => _bars.ContainsKey(id);

    private FrameworkElement Build(InlineBarRequest request)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };

        content.Children.Add(new TextBlock
        {
            Text = request.Message,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 8, 0),
            FontSize = 12,
        });

        AddAction(content, request.PrimaryAction, request.OnPrimary, request.Id, request.PrimaryKeepsBar);
        AddAction(content, request.SecondaryAction, request.OnSecondary, request.Id, request.SecondaryKeepsBar);

        if (request.Dismissible)
            AddAction(content, "Dismiss", null, request.Id, keepsBar: false);

        var bar = new Border
        {
            Padding = new Thickness(8, 5, 8, 5),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = content,
        };

        if (_theme is not null) Paint(bar, _theme);

        return bar;
    }

    private void AddAction(Panel content, string? label, Action? action, string id, bool keepsBar)
    {
        if (string.IsNullOrEmpty(label)) return;

        var button = new Button
        {
            Content = label,
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(7, 1, 7, 1),
            FontSize = 12,
        };

        button.Click += (_, _) =>
        {
            // Dismiss FIRST -- for an action that RESOLVES the condition it
            // describes. An action that opens a dialog or triggers a
            // re-render would otherwise leave its own bar behind after the
            // condition is already fixed.
            //
            // keepsBar is the escape hatch for the opposite case: an action
            // that only DIAGNOSES the condition (Show in folder, Save As...)
            // resolves nothing, so dismissing first would remove the
            // explanation of a condition that is still true.
            if (!keepsBar) Dismiss(id);
            action?.Invoke();
        };

        content.Children.Add(button);
    }

    private static void Paint(FrameworkElement element, NoteTheme theme)
    {
        if (element is not Border border) return;

        border.Background = new SolidColorBrush(Parse(theme.CodeBg));
        border.BorderBrush = new SolidColorBrush(Parse(theme.Border));

        if (border.Child is Panel panel)
        {
            foreach (var child in panel.Children)
                if (child is TextBlock text)
                    text.Foreground = new SolidColorBrush(Parse(theme.ContentFg));
        }
    }

    private static Color Parse(string hex)
        => (Color)ColorConverter.ConvertFromString(hex)!;
}
