namespace StickyMD.Core.Input;

/// <summary>
/// A global hotkey parsed out of its settings.json string form.
/// </summary>
/// <remarks>
/// The numbers here are Win32's <c>MOD_*</c> flags and virtual-key codes, but
/// they are declared as plain <c>uint</c> constants on purpose: Core targets
/// net10.0 with no Win32 reference, and the compiler enforces that. App's
/// <c>HotkeyManager</c> passes them straight to <c>RegisterHotKey</c>.
///
/// Parsing lives here rather than in App because it is the one part of the
/// hotkey feature with branches in it, and because
/// <see cref="Persistence.StateValidator"/> has to reject an unusable string
/// at load time -- a hotkey that cannot be parsed is a hotkey that silently
/// never works, and "never die silently" covers that too.
/// </remarks>
public sealed record HotkeySpec(uint Modifiers, uint VirtualKey, string Canonical)
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    /// <summary>
    /// <c>MOD_NOREPEAT</c>. Not part of a parse -- OR'd in at registration.
    /// </summary>
    /// <remarks>
    /// Without it Windows repeats <c>WM_HOTKEY</c> while the combination is
    /// held, and a leant-on Ctrl+Alt+N creates notes as fast as the keyboard
    /// repeat rate. Every one of them is a real file in the notes root.
    /// </remarks>
    public const uint ModNoRepeat = 0x4000;

    /// <summary>
    /// The named keys worth supporting beyond letters, digits and F1-F24.
    /// </summary>
    /// <remarks>
    /// Deliberately short. A global hotkey has to be something the user can
    /// hit without looking, and every entry here is one more string the
    /// Settings box has to accept and echo back correctly.
    /// </remarks>
    private static readonly Dictionary<string, (uint Vk, string Name)> NamedKeys =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["space"] = (0x20, "Space"),
            ["enter"] = (0x0D, "Enter"),
            ["return"] = (0x0D, "Enter"),
            ["insert"] = (0x2D, "Insert"),
            ["delete"] = (0x2E, "Delete"),
            ["home"] = (0x24, "Home"),
            ["end"] = (0x23, "End"),
            ["pageup"] = (0x21, "PageUp"),
            ["pagedown"] = (0x22, "PageDown"),

            // WPF's Key enum declares Prior and Next BEFORE PageUp and
            // PageDown at the same values, so Key.PageUp.ToString() returns
            // "Prior". The Settings window's capture box composes its
            // candidate from that name, and without these two aliases pressing
            // PageUp there would silently do nothing.
            ["prior"] = (0x21, "PageUp"),
            ["next"] = (0x22, "PageDown"),

            ["up"] = (0x26, "Up"),
            ["down"] = (0x28, "Down"),
            ["left"] = (0x25, "Left"),
            ["right"] = (0x27, "Right"),
        };

    /// <summary>
    /// Parses <c>"Ctrl+Alt+N"</c>. False for anything unusable, with no
    /// exception -- callers are a settings loader and a settings dialog, and
    /// neither wants a throw for a value a human typed.
    /// </summary>
    public static bool TryParse(string? text, out HotkeySpec spec)
    {
        spec = null!;

        if (string.IsNullOrWhiteSpace(text)) return false;

        uint modifiers = 0;
        uint? vk = null;
        var keyName = string.Empty;

        foreach (var raw in text.Split('+'))
        {
            var token = raw.Trim();
            if (token.Length == 0) return false;

            switch (token.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= ModControl; continue;
                case "alt": modifiers |= ModAlt; continue;
                case "shift": modifiers |= ModShift; continue;
                case "win" or "windows": modifiers |= ModWin; continue;
            }

            // A second non-modifier token means something like "Ctrl+A+B",
            // which RegisterHotKey has no way to express.
            if (vk is not null) return false;

            if (!TryKey(token, out var code, out keyName)) return false;
            vk = code;
        }

        if (vk is null) return false;

        // At least one modifier, always. RegisterHotKey happily accepts a bare
        // key and then swallows that key system-wide for every other
        // application -- a "hotkey" of "N" makes the letter N unusable
        // everywhere until StickyMD exits.
        if (modifiers == 0) return false;

        spec = new HotkeySpec(modifiers, vk.Value, Format(modifiers, keyName));
        return true;
    }

    private static bool TryKey(string token, out uint vk, out string name)
    {
        vk = 0;
        name = string.Empty;

        if (token.Length == 1)
        {
            var c = char.ToUpperInvariant(token[0]);

            // VK_A..VK_Z and VK_0..VK_9 are the ASCII codes of the uppercase
            // characters, which is why this is a cast and not a table.
            if (c is >= 'A' and <= 'Z' || c is >= '0' and <= '9')
            {
                vk = c;
                name = c.ToString();
                return true;
            }

            return false;
        }

        if ((token[0] is 'F' or 'f')
            && int.TryParse(token[1..], out var fn)
            && fn is >= 1 and <= 24)
        {
            vk = (uint)(0x70 + fn - 1); // VK_F1 = 0x70
            name = $"F{fn}";
            return true;
        }

        if (NamedKeys.TryGetValue(token, out var named))
        {
            (vk, name) = named;
            return true;
        }

        return false;
    }

    /// <summary>
    /// One fixed modifier order, so a hotkey has exactly one displayed form.
    /// </summary>
    /// <remarks>
    /// "Alt+Ctrl+N" and "ctrl+alt+n" are the same hotkey, and the Settings box
    /// echoing back whichever the user happened to type would make two
    /// identical settings look different in settings.json.
    /// </remarks>
    private static string Format(uint modifiers, string keyName)
    {
        var parts = new List<string>(4);
        if ((modifiers & ModControl) != 0) parts.Add("Ctrl");
        if ((modifiers & ModAlt) != 0) parts.Add("Alt");
        if ((modifiers & ModShift) != 0) parts.Add("Shift");
        if ((modifiers & ModWin) != 0) parts.Add("Win");
        parts.Add(keyName);
        return string.Join('+', parts);
    }
}
