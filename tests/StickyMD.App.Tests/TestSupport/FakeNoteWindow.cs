using StickyMD.App.Windows;
using StickyMD.Core.Geometry;
using StickyMD.Core.Notes;
using StickyMD.Core.Persistence;
using StickyMD.Core.Theming;

namespace StickyMD.App.Tests.TestSupport;

/// <summary>
/// An <see cref="INoteWindow"/> with no WPF in it. A real Window cannot be
/// constructed on an xUnit thread, and the three-state model this stands in
/// for is the one piece of window logic that must be tested.
/// </summary>
public sealed class FakeNoteWindow(string notePath, NoteState state) : INoteWindow
{
    public string NotePath { get; private set; } = notePath;

    public NoteState State { get; private set; } = state;

    public PixelRect Bounds { get; set; } =
        new(state.X, state.Y, state.W, state.H);

    /// <summary>
    /// INoteWindow's, satisfied on the real window by Window.IsVisible. What
    /// the tray's left click reads to choose between Show All and Hide All.
    /// </summary>
    public bool IsVisible { get; private set; }
    public bool WasActivated { get; private set; }
    public bool WasFocused { get; private set; }
    public bool IsDisposed { get; private set; }
    public bool WasToldFileDeleted { get; private set; }
    public bool WasSaved { get; private set; }
    public bool EnteredEditMode { get; private set; }

    public RecoveryEnvelope? RecoveryOffered { get; private set; }

    /// <summary>
    /// When set, Dispose() raises StateChanged with the supplied state,
    /// simulating the real window's Dispose -> Close -> Closing ->
    /// StateChanged cascade so tests can pin WindowManager's guard against it.
    /// </summary>
    public NoteState? StateChangedOnDispose { get; set; }

    public List<string> ExternalContentApplied { get; } = [];
    public List<NoteState> StatesApplied { get; } = [];
    public List<NoteTheme> ThemesApplied { get; } = [];
    public List<string> RenamesApplied { get; } = [];
    public List<bool> RemoteImagePoliciesApplied { get; } = [];

    public event Action<string>? CloseRequested;
    public event Action<string>? DeleteRequested;
    public event Action<string, string>? RenameRequested;
    public event Action<string>? OpenNoteRequested;
    public event Action<string, NoteState>? StateChanged;

    public void ShowNote(bool activate)
    {
        IsVisible = true;
        if (activate) WasActivated = true;
    }

    public void HideNote() => IsVisible = false;

    public void FocusNote()
    {
        IsVisible = true;
        WasFocused = true;
    }

    public void EnterEditMode() => EnteredEditMode = true;

    public void ApplyState(NoteState state, NoteTheme theme, bool allowRemoteImages)
    {
        State = state;
        StatesApplied.Add(state);
        ThemesApplied.Add(theme);
        RemoteImagePoliciesApplied.Add(allowRemoteImages);
    }

    public void ApplyExternalContent(NoteContent content)
        => ExternalContentApplied.Add(content.Text);

    public void ShowRecovered(RecoveryEnvelope envelope) => RecoveryOffered = envelope;

    public void NotifyFileDeleted() => WasToldFileDeleted = true;

    public void NotifyRenamed(string canonicalPath)
    {
        NotePath = canonicalPath;
        RenamesApplied.Add(canonicalPath);
    }

    public void SaveNow() => WasSaved = true;

    public bool AutomaticSavesStopped { get; private set; }

    public void StopAutomaticSaves() => AutomaticSavesStopped = true;

    public void Dispose()
    {
        IsDisposed = true;

        if (StateChangedOnDispose is { } state) RaiseStateChanged(state);
    }

    // Test drivers.
    public void RaiseClose() => CloseRequested?.Invoke(NotePath);

    public void RaiseDelete() => DeleteRequested?.Invoke(NotePath);

    public void RaiseRename(string newName) => RenameRequested?.Invoke(NotePath, newName);

    public void RaiseOpenNote(string target) => OpenNoteRequested?.Invoke(target);

    public void RaiseStateChanged(NoteState state)
    {
        State = state;
        StateChanged?.Invoke(NotePath, state);
    }
}

/// <summary>Hands out <see cref="FakeNoteWindow"/>s and remembers them.</summary>
public sealed class FakeNoteWindowFactory : INoteWindowFactory
{
    public List<FakeNoteWindow> Created { get; } = [];

    /// <summary>The global remote-image setting each window was born with.</summary>
    public List<bool> CreatedWithRemoteImages { get; } = [];

    public INoteWindow Create(
        string canonicalPath, NoteState state, NoteTheme theme, bool allowRemoteImages)
    {
        var window = new FakeNoteWindow(canonicalPath, state);
        Created.Add(window);
        CreatedWithRemoteImages.Add(allowRemoteImages);
        return window;
    }

    public FakeNoteWindow For(string path)
        => Created.Single(w => NotePath.AreSame(w.NotePath, path));
}
