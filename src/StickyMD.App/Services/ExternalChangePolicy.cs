namespace StickyMD.App.Services;

public enum ExternalChangeAction
{
    /// <summary>Take the file's version. Nothing is at risk.</summary>
    Reload,

    /// <summary>Show the "Changed on disk" bar and let the user choose.</summary>
    Ask,
}

/// <summary>
/// What to do when a note changed on disk under a live window.
/// </summary>
/// <remarks>
/// Never auto-clobber either side. A clean buffer has nothing to lose, so it
/// reloads silently -- that is what makes the 200ms external-edit criterion
/// feel like sync rather than like a prompt. A dirty buffer is a real
/// conflict and the user decides.
///
/// The one exception is a dirty buffer that already MATCHES disk: somebody
/// typed the same characters, or the app's own write arrived by a route the
/// ledger did not see. Asking there is a question with one answer.
/// </remarks>
public static class ExternalChangePolicy
{
    public static ExternalChangeAction Decide(
        bool bufferIsDirty, string? bufferHash, string diskHash)
    {
        if (!bufferIsDirty) return ExternalChangeAction.Reload;

        // Not knowing is not the same as matching. Asking costs a click;
        // guessing costs the text.
        if (bufferHash is null) return ExternalChangeAction.Ask;

        return string.Equals(bufferHash, diskHash, StringComparison.OrdinalIgnoreCase)
            ? ExternalChangeAction.Reload
            : ExternalChangeAction.Ask;
    }
}
