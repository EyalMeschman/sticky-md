using System.Windows.Media;

namespace StickyMD.App.Windows;

/// <summary>The palette's "#RRGGBB" strings as brushes and colours.</summary>
internal static class Hex
{
    public static Color Color(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;

    public static SolidColorBrush Brush(string hex) => new(Color(hex));

    /// <summary>The WebView2 backdrop is a GDI colour, not a WPF one.</summary>
    public static System.Drawing.Color Gdi(string hex) => System.Drawing.ColorTranslator.FromHtml(hex);
}
