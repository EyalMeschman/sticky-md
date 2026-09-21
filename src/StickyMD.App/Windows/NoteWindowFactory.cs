using StickyMD.Core.Notes;
using StickyMD.Core.Persistence;
using StickyMD.Core.Theming;

namespace StickyMD.App.Windows;

/// <summary>
/// The only place a <see cref="NoteWindow"/> is constructed. Exists so
/// <c>WindowManager</c> can be driven by fakes -- a WPF Window cannot be
/// created on an xUnit thread, and the manager's logic is the part that must
/// be tested.
/// </summary>
public interface INoteWindowFactory
{
    /// <param name="allowRemoteImages">
    /// Settings' global opt-in, as it stands at creation. Handed over here
    /// rather than pushed in afterwards because the note's first WebView shell
    /// -- and the CSP baked into it -- is built before anything else gets a
    /// chance to speak to the window.
    /// </param>
    INoteWindow Create(
        string canonicalPath, NoteState state, NoteTheme theme, bool allowRemoteImages);
}

/// <summary>
/// One ledger instance is shared by every note this factory creates -- that
/// is what makes self-write suppression work. A per-window ledger would make
/// every save look, from the watcher's point of view, like an external
/// change, and the note would reload itself in a loop.
/// </summary>
public sealed class NoteWindowFactory(IWriteLedger ledger) : INoteWindowFactory
{
    public INoteWindow Create(
        string canonicalPath, NoteState state, NoteTheme theme, bool allowRemoteImages)
        => new NoteWindow(canonicalPath, state, theme, ledger, allowRemoteImages);
}
