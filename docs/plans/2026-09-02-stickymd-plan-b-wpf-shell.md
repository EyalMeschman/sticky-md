# StickyMD Plan B — WPF Shell Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build `StickyMD.App` — the WPF shell that turns the tested `StickyMD.Core` library into a usable sticky note on screen: a transparent, resizable, always-on-top note window rendering Markdown through `WebView2CompositionControl`, editable in place, autosaving to a real `.md` file, and staying in sync with external edits.

**Architecture:** `StickyMD.App` targets `net10.0-windows10.0.17763.0` and owns everything platform-bound: window chrome, Win32 interop, WebView2 hosting, and the Recycle Bin. Every piece of it that carries logic sits behind a seam interface (`IMonitorProvider`, `ISystemTheme`, `IFileDeletionService`, `INoteWindow`, `INoteWindowFactory`, `INoteFileGateway`) so the logic is reachable from a headless test runner while the WPF and Win32 implementations stay thin enough to verify by hand. Two pieces properly belong in Core and are written here because this is where their only consumer appears: `HtmlDocumentBuilder` and `NavigationPolicy`.

**Tech Stack:** .NET 10 (LTS), C#, WPF, `Microsoft.Web.WebView2` 1.0.4129.50, Markdig, xUnit, Shouldly.

**Spec:** `docs/specs/2026-08-22-stickymd-design.md`

**Measured findings this plan is built on:** `docs/spikes/2026-08-22-spike-0-transparency.md`

**Predecessor:** `docs/plans/2026-08-22-stickymd-plan-a-core.md` — complete, 252 tests passing.

---

## Plan Sequence

| Plan              | Scope                                                                    | Ends with            |
| ----------------- | ------------------------------------------------------------------------ | -------------------- |
| A                 | Spike 0, solution scaffold, all of `StickyMD.Core`                       | A tested library     |
| **B (this plan)** | WPF shell: chrome, WebView2 host, mode toggle, checkbox bridge, deletion | A usable sticky note |
| C                 | Tray, hotkeys, startup registry, single instance, Settings, distribution | The finished app     |

### Read this before starting

**Plan C is not written.** Do not go looking for `plan-c-*.md`, and do not invent it. The table above is a roadmap, not an index.

**Start from Spike 0, not from intuition.** The transparency and WebView2 hosting approach was settled empirically, and re-deriving it from first principles reproduces two traps that produce no error message:

1. `WS_EX_LAYERED` **cannot** be added to an existing WPF window on Windows 11 26200. `SetWindowLong` reports success (`lastErr = 0`, returns the prior ex-style) and changes nothing. The only route to a layered WPF window is `AllowsTransparency=True`, which makes WPF apply the style at `CreateWindowEx` — the one moment Windows permits it.
2. A `Window`-level `MouseLeftButtonDown` → `DragMove()` handler **silently steals every click from the WebView**. Checkboxes stop toggling, scrollbar drags move the window, text selection dies. No exception, no warning, no crash.

**What is explicitly NOT in this plan**, so nobody adds it: the tray icon and Recent Notes list, global hotkeys, the HKCU Run key, the single-instance mutex and pipe, the Settings window, `--startup` argument handling, and the distribution build. All of that is Plan C. Where Plan B needs a stand-in for one of them, the stand-in is labelled `TEMPORARY (Plan B only)` in the code and is removed in Plan C.

---

## Global Constraints

Every task's requirements implicitly include this section. Values are copied verbatim from the spec and from Spike 0.

- **Target frameworks.** `StickyMD.Core` stays on `net10.0`. `StickyMD.App` and `StickyMD.App.Tests` target **`net10.0-windows10.0.17763.0`**. The Windows version suffix is **not optional**: plain `net10.0-windows` throws `FileNotFoundException: Microsoft.Windows.SDK.NET, Version=10.0.17763.10` from inside `WebView2CompositionControl.TryInitializeD3DImage()`, and the stack trace points at D3D while never mentioning the target framework.
- **Core purity is unchanged.** `StickyMD.Core` must still reference **zero** WPF, WinForms, or Win32 APIs. `System.IO`, `System.Text.Json`, and `System.Security.Cryptography` are permitted; `System.Windows.*`, `System.Drawing.*`, and `[DllImport]` are not. The Core TFM enforces this — keep it that way.
- **NuGet.** Package sources are disabled machine-wide (`%APPDATA%\NuGet\nuget.config` has an emptied `<packageSources>`, a deliberate posture that stays untouched). The repo-scoped `NuGet.config` at the solution root already enables `nuget.org` for this repository only. Without it every `dotnet add package` fails with "There are no versions available".
- **Window configuration (Spike 0, non-negotiable).** `AllowsTransparency = true`, `WindowStyle = None`, `ShowInTaskbar = false`, opacity via **`Window.Opacity`** and never `SetLayeredWindowAttributes`, content via **`WebView2CompositionControl`** and never the plain `WebView2`, `DefaultBackgroundColor` **opaque**, `WindowChrome { CaptionHeight = 0, ResizeBorderThickness = 6, GlassFrameThickness = 0 }`, drag handler on the **header element only**.
- **Per-pixel transparency is not available.** The WebView backdrop must be opaque, so the WPF layer cannot show through the note content. Non-rectangular notes are out; rounded corners come from `DwmSetWindowAttribute` + `DWMWA_WINDOW_CORNER_PREFERENCE` only.
- **Geometry is stored and restored in physical screen pixels**, via `GetWindowRect` and `SetWindowPos`. Never WPF's `Left`/`Top`/`Width`/`Height`, which are DIPs relative to the primary monitor's DPI. The app manifests as PerMonitorV2.
- **`MonitorInfo.Dpi` is a raw DPI value** — 96, 120, 144 — not a scale factor. Plan A's tests use `Dpi: 96` and `Dpi: 144`.
- **Markdig `SourceSpan.End` is INCLUSIVE** — the index of the last character, not one past it. A span of `Start=2, End=4` covers three characters. Verified empirically three times; it holds for CJK, accented text, and emoji.
- **`NotePalette` is the single source of truth for all seven colours.** WPF chrome and rendered HTML both read from it. `ToCssVariables` is the interface `HtmlDocumentBuilder` consumes.
- **Every save is recorded into `IWriteLedger`.** That is what lets `NoteWatcher` tell the app's own writes from an external edit. Identity is the SHA-256 content hash plus byte length — never a timestamp.
- **`NoteWatcher.Recovered` must be handled.** When the underlying watcher dies and is recreated, `NoteWatcher` deliberately does not decide which notes to re-read — it has no idea which are open. `WindowManager` owns that.
- **`ThemePreference` and `ThemeMode` are different types on purpose.** `ThemePreference` is what the user chose and includes `System`; `ThemeMode` is the resolved light-or-dark that `NotePalette` needs. The App resolves one into the other by reading the OS setting.
- **Three states, not two.** `isOpen: true` means "belongs on my desktop and returns next startup". **Only an explicit user Close Note (`✕`) may set `isOpen = false`.** Application exit, Windows logoff, and internal window disposal must never do so.
- **Nothing app-owned is ever written into the notes root.** Only `.md` files the user created. The HTML shell is delivered by `NavigateToString`, not by dropping a file next to the notes.
- **Virtual host mapping** uses `CoreWebView2HostResourceAccessKind.DenyCors`, never `Allow`.
- **Remote images are blocked by default.** `allowRemoteImages` defaults to `false`; the CSP is built from the effective setting.
- **Watcher debounce 150ms. Autosave debounce 500ms. Save retries 3× at 100/300/900ms.**
- **The `⋯` menu is exactly:** Rename…, Color ▸, Opacity ▸, Always on Top ☑, ─, Delete. Nothing else belongs there.
- **`✕` never deletes a file.** Deletion is `⋯ → Delete`, which sends the file to the Recycle Bin.
- **Governing rule for every error path:** never lose text, never destroy a file, never die silently.

---

## Before You Start: Git

This plan commits after every task, and **the assistant must not run git write commands.** `git add` is deny-listed in this repository's settings. Every commit step below prints the exact commands; hand them to the user to paste and run, then wait for confirmation before starting the next task.

Read-only git (`git status`, `git diff`, `git log`) is fine to run directly.

**Commit message rule for this repository:** no tooling attribution of any kind. No `Co-Authored-By`, no generator footers, no tool names in the subject or body. Commit messages explain the _why_ behind non-obvious code, in the voice of the repository's sole author.

---

## The Two Open Findings — resolved in Tasks 2 and 3

Plan A's whole-branch review left two cross-component questions deliberately unanswered, because neither has a right answer until the shell exists. **They are resolved here, in the first two Core tasks, before any WPF code is written** — every later task depends on the answers.

### Finding 1 → Task 2: four components disagreed about what a note's path is

`NoteRepository.EnumerateRoot` returned verbatim-joined paths; `NoteWatcher` emitted `Path.GetFullPath`-canonicalised ones; `RecoveryStore` hashed a lowercased normalised form; `NoteIndexStore` accepted whatever key happened to be in the JSON file. A path from one would not compare equal to a path from another, so `WindowManager`'s open-notes map would silently miss and open a second window on a file that is already open.

**The decision:**

> **Canonical form** is `NotePath.Canonical(p)` = `Path.TrimEndingDirectorySeparator(Path.GetFullPath(p))`. Case is **preserved**, because the on-disk case is what the user sees in Explorer and in `notes.json`.
>
> **Comparison** is _always_ `NotePath.Comparer` = `StringComparer.OrdinalIgnoreCase`. Every dictionary, set, and equality test keyed by a note path uses it.
>
> **Canonicalise at production, not at consumption.** Every API that _hands out_ a note path returns the canonical form. No consumer is ever responsible for normalising an input it was handed.

Case is preserved rather than lowercased for two reasons. Lowercasing would write `c:\users\eyal\stickymd notes\standup.md` into `notes.json`, which is user-visible and wrong; and it would make the index key useless as a display source. Because comparison is always case-insensitive, preserving case costs nothing.

`Path.GetFullPath` deliberately does _not_ resolve 8.3 short names, symlinks, or the true on-disk casing — those need an open handle and Win32 (`GetFinalPathNameByHandle`), which Core may not touch and which fails outright for a file that does not exist yet. Canonicalisation must work for the path of a note that is about to be created, so `GetFullPath` is the correct ceiling. The residual gap — the same file reached through a symlink and through its target — is recorded as a known limitation in Task 2, not silently ignored.

### Finding 2 → Task 3: deserialization checked shape, not values

`{"color": 99}` loaded cleanly and then crashed `NotePalette.Get` with `ArgumentOutOfRangeException`. `System.Text.Json`'s `JsonStringEnumConverter` accepts numeric enum values and does not range-check them, so `(NoteColor)99` reached the palette. The same held for out-of-range geometry, an opacity of `0.0` (an invisible, unclickable note), a `NaN` opacity, and a `lastOpenedUtc` in the year 9999 that would pin a note to the top of Recent Notes forever.

**The decision:**

> **Values are validated on load, clamped or defaulted, and every correction is reported.** Clamping without reporting would be dying silently, which the governing rule forbids.
>
> **`notes.json` entries deserialize one at a time.** A single unparseable entry costs that one note's geometry, not the whole file's. Today an unknown enum _string_ anywhere in the file throws `JsonException`, which sends the entire index to `notes.json.corrupt-N` and loses every note's placement.
>
> **The report surfaces through `LastLoadIssues`** on each store — mirroring the existing `LastCorruptBackupPath` property — and the bootstrapper appends it to `%LOCALAPPDATA%\StickyMD\diagnostics.log`. Plan C turns the same list into a tray balloon; Plan B's obligation is that the information exists on disk rather than in nobody's hands.

---

## File Structure

### New in `StickyMD.Core` (written here because this is where their only consumers appear)

| File                              | Responsibility                                          |
| --------------------------------- | ------------------------------------------------------- |
| `Notes/NotePath.cs`               | The one canonical path form and the one comparer        |
| `Persistence/ValidationIssue.cs`  | One reported correction: scope, field, what happened    |
| `Persistence/StateValidator.cs`   | Clamp/default `NoteState` and `AppSettings` on load     |
| `Diagnostics/DiagnosticsLog.cs`   | Append-only, size-capped, best-effort text log          |
| `Markdown/HtmlDocumentBuilder.cs` | Shell HTML: CSP, theme CSS variables, the bridge script |
| `Markdown/NavigationPolicy.cs`    | Pure allowlist decision for a clicked link              |

### Modified in `StickyMD.Core`

| File                            | Change                                                                             |
| ------------------------------- | ---------------------------------------------------------------------------------- |
| `Notes/WriteLedger.cs`          | `Normalize` delegates to `NotePath.Canonical`; comparer from `NotePath`            |
| `Notes/NoteRepository.cs`       | `NotesRoot`, `EnumerateRoot`, `CreateNewRecorded`, `Rename` return canonical paths |
| `Notes/NoteWatcher.cs`          | Emits canonical paths; tolerates uncanonicalisable ones                            |
| `Persistence/NoteIndexStore.cs` | Per-entry deserialization, canonical re-keying, validation, `LastLoadIssues`       |
| `Persistence/SettingsStore.cs`  | Validation, `LastLoadIssues`                                                       |
| `Persistence/AppPaths.cs`       | Adds `DiagnosticsFile` and `WebViewUserDataDir`                                    |
| `Markdown/MarkdownRenderer.cs`  | `RenderResult` gains `BlockedRemoteImages`                                         |

### New in `StickyMD.App`

| File                                   | Responsibility                                                              |
| -------------------------------------- | --------------------------------------------------------------------------- |
| `StickyMD.App.csproj`                  | TFM `net10.0-windows10.0.17763.0`, `UseWPF`, WebView2 package               |
| `app.manifest`                         | PerMonitorV2 DPI awareness                                                  |
| `App.xaml` / `App.xaml.cs`             | `ShutdownMode=OnExplicitShutdown`, bootstrap, `SessionEnding`               |
| `Interop/NativeMethods.cs`             | Every `[DllImport]` in one file                                             |
| `Interop/MonitorEnumerator.cs`         | `EnumDisplayMonitors` → `MonitorInfo[]`, plus a pure mapping seam           |
| `Interop/SystemTheme.cs`               | `AppsUseLightTheme` → `ThemeMode`, change notifications                     |
| `Interop/WindowGeometry.cs`            | `GetWindowRect` / `SetWindowPos` in physical pixels                         |
| `Interop/DwmCorners.cs`                | Rounded corners via `DWMWA_WINDOW_CORNER_PREFERENCE`                        |
| `Services/RecycleBinService.cs`        | `IFileDeletionService` via `SHFileOperationW` with `FOF_ALLOWUNDO`          |
| `Services/WebViewEnvironment.cs`       | The one shared `CoreWebView2Environment`; runtime-presence check            |
| `Services/WebViewHost.cs`              | One note's WebView: lockdown, shell, virtual host, messages, crash recovery |
| `Services/NoteFileGateway.cs`          | `INoteFileGateway` — read/write/ledger behind one seam                      |
| `Services/SaveCoordinator.cs`          | Debounce, retry 3×, recovery snapshot, ledger record                        |
| `Services/WindowManager.cs`            | Three-state model, geometry, watcher wiring, rename, delete                 |
| `Windows/NoteWindow.xaml` / `.xaml.cs` | The note itself: chrome, header, body, bars, mode toggle                    |
| `Windows/NoteWindowFactory.cs`         | `INoteWindowFactory` — the only place a `NoteWindow` is constructed         |
| `Windows/InlineBarHost.cs`             | The bar stack: show/replace/dismiss one message at a time                   |
| `Messaging/WebMessages.cs`             | The host↔page message contract as records                                   |

### New in `tests/StickyMD.App.Tests`

| File                                 | Covers                                                         |
| ------------------------------------ | -------------------------------------------------------------- |
| `Interop/MonitorMappingTests.cs`     | Raw monitor data → `MonitorInfo`, DPI convention, primary flag |
| `Messaging/WebMessagesTests.cs`      | The exact JSON the page sends and receives                     |
| `Services/SaveCoordinatorTests.cs`   | Debounce, retry schedule, recovery snapshot, ledger record     |
| `Services/WindowManagerTests.cs`     | The three-state model, path identity, watcher recovery         |
| `TestSupport/FakeNoteWindow.cs`      | `INoteWindow` with no WPF                                      |
| `TestSupport/FakeNoteFileGateway.cs` | Scripted read/write failures                                   |
| `TestSupport/FakeMonitorProvider.cs` | Synthetic monitor sets                                         |

### Task map

| Task | Deliverable                                                        | Verified by                |
| ---- | ------------------------------------------------------------------ | -------------------------- |
| 1    | `StickyMD.App` + `StickyMD.App.Tests` build and run                | `dotnet build`, smoke test |
| 2    | **Finding 1 resolved** — one canonical path form at every boundary | Core tests                 |
| 3    | **Finding 2 resolved** — validation on load + diagnostics log      | Core tests                 |
| 4    | `HtmlDocumentBuilder` — shell, CSP, theme vars, bridge script      | Core tests                 |
| 5    | `NavigationPolicy` — the §6 allowlist as a pure function           | Core tests                 |
| 6    | `MonitorEnumerator` + `SystemTheme`                                | App tests + manual         |
| 7    | `IFileDeletionService` / `RecycleBinService`                       | Manual (real Recycle Bin)  |
| 8    | `WebViewHost` + shared `CoreWebView2Environment`                   | Manual                     |
| 9    | `NoteWindow` chrome — a real note on screen                        | Manual smoke               |
| 10   | Mode toggle, editor conveniences, `SaveCoordinator`                | App tests + manual         |
| 11   | Checkbox, link, and edit-request message bridge                    | Manual                     |
| 12   | The inline bars — every §8 condition the window owns               | Manual                     |
| 13   | `WindowManager` — three states, geometry, watcher, rename, delete  | App tests                  |
| 14   | Real bootstrap, `STATUS.md`, full manual smoke checklist           | The checklist              |

---

## Task 1: `StickyMD.App` and `StickyMD.App.Tests` scaffold

The only task in this plan with no test of its own worth writing beyond "it builds and the runner finds it". Everything after this depends on the target framework being exactly right, so it comes first and alone.

**Files:**

- Create: `src/StickyMD.App/StickyMD.App.csproj`
- Create: `src/StickyMD.App/app.manifest`
- Create: `src/StickyMD.App/App.xaml`
- Create: `src/StickyMD.App/App.xaml.cs`
- Create: `tests/StickyMD.App.Tests/StickyMD.App.Tests.csproj`
- Create: `tests/StickyMD.App.Tests/ScaffoldTests.cs`
- Modify: `StickyMD.sln`

**Interfaces:**

- Consumes: `StickyMD.Core` (project reference).
- Produces: two buildable projects. No types yet.

- [ ] **Step 1: Create the App project file**

`src/StickyMD.App/StickyMD.App.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <!--
      The Windows version suffix is REQUIRED, not stylistic.
      WebView2CompositionControl renders through D3DImage and needs the Windows
      SDK WinRT projections, which only flow in from a Windows-version-specific
      TFM. Plain net10.0-windows throws FileNotFoundException for
      Microsoft.Windows.SDK.NET from inside TryInitializeD3DImage(); a stack
      trace that points at D3D and never mentions the target framework.
    -->
    <TargetFramework>net10.0-windows10.0.17763.0</TargetFramework>
    <OutputType>WinExe</OutputType>
    <UseWPF>true</UseWPF>
    <RootNamespace>StickyMD.App</RootNamespace>
    <AssemblyName>StickyMD</AssemblyName>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <PlatformTarget>x64</PlatformTarget>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Web.WebView2" Version="1.0.4129.50" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\StickyMD.Core\StickyMD.Core.csproj" />
  </ItemGroup>

</Project>
```

`PlatformTarget` is x64 because the WebView2 runtime verified in Spike 0 is 64-bit and the spike process was 64-bit. An AnyCPU build that happened to launch as x86 would go looking for a browser that is not installed.

- [ ] **Step 2: Create the DPI manifest**

`src/StickyMD.App/app.manifest`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.0.0.0" name="StickyMD.app" />

  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <!--
        PerMonitorV2, because note geometry is stored in PHYSICAL screen pixels
        and restored with SetWindowPos. Under system DPI virtualisation Windows
        would lie to us about both the window rect we read back and the monitor
        bounds we clamp against, and a note saved on a 150% display would come
        back in the wrong place on a 100% one.
      -->
      <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/pm</dpiAware>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
      <longPathAware xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">true</longPathAware>
    </windowsSettings>
  </application>

  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
    <application>
      <!-- Windows 10 / 11 -->
      <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}" />
    </application>
  </compatibility>
</assembly>
```

- [ ] **Step 3: Create the application entry point**

`src/StickyMD.App/App.xaml`:

```xml
<Application x:Class="StickyMD.App.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             ShutdownMode="OnExplicitShutdown" />
```

There is deliberately no `StartupUri`. Notes are opened by code, not by XAML, and `ShutdownMode="OnExplicitShutdown"` is required by the spec: hiding or closing the last note must not exit the app.

`src/StickyMD.App/App.xaml.cs`:

```csharp
using System.Windows;

namespace StickyMD.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // TEMPORARY (Plan B only). Task 9 replaces this with a real note window
        // and Task 14 with the real bootstrap. Until then the process starts,
        // proves the target framework resolves, and exits.
        MessageBox.Show(
            "StickyMD shell scaffold. No notes yet.",
            "StickyMD",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        Shutdown();
    }
}
```

- [ ] **Step 4: Create the App test project**

`tests/StickyMD.App.Tests/StickyMD.App.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <!--
      Must MATCH the App TFM. A test project on plain net10.0 cannot reference a
      net10.0-windows10.0.17763.0 project at all, and one on net10.0-windows
      would resolve a different SDK projection set than the app it is testing.
    -->
    <TargetFramework>net10.0-windows10.0.17763.0</TargetFramework>
    <UseWPF>true</UseWPF>
    <PlatformTarget>x64</PlatformTarget>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="coverlet.collector" Version="6.0.4" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="Shouldly" Version="4.3.0" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\StickyMD.App\StickyMD.App.csproj" />
  </ItemGroup>

</Project>
```

`UseWPF=true` is needed in the test project too: several App types this plan produces mention WPF types in their signatures, and without `UseWPF` the test assembly cannot compile against them.

`tests/StickyMD.App.Tests/ScaffoldTests.cs`:

```csharp
using Shouldly;
using StickyMD.Core.Theming;

namespace StickyMD.App.Tests;

public class ScaffoldTests
{
    [Fact]
    public void The_app_assembly_is_referenced_and_loadable()
        => typeof(StickyMD.App.App).Assembly.GetName().Name.ShouldBe("StickyMD");

    [Fact]
    public void Core_is_reachable_from_the_app_test_project()
        => NotePalette.All.Count.ShouldBe(7);
}
```

- [ ] **Step 5: Add both projects to the solution**

```bash
dotnet sln StickyMD.sln add src/StickyMD.App/StickyMD.App.csproj
dotnet sln StickyMD.sln add tests/StickyMD.App.Tests/StickyMD.App.Tests.csproj
```

- [ ] **Step 6: Restore and build, and expect to have to deal with warnings-as-errors**

```bash
dotnet restore
dotnet build
```

Expected: build succeeds with **zero warnings**. `Directory.Build.props` sets `TreatWarningsAsErrors=true` and `EnforceCodeStyleInBuild=true` repo-wide, and this is the first WPF project to live under it.

**If XAML code generation produces analyzer errors** (`obj/**/*.g.i.cs`, `GeneratedInternalTypeHelper`), do **not** weaken the repo-wide setting. Add a scoped suppression to `src/StickyMD.App/StickyMD.App.csproj` only, naming the specific IDs the build actually reported:

```xml
  <PropertyGroup>
    <!--
      Scoped to this project. XAML code generation emits files this repo's style
      rules were not written for; suppressing them here keeps
      TreatWarningsAsErrors meaningful for the code we actually write. Add IDs
      only as the build reports them; never a blanket NoWarn.
    -->
    <NoWarn>$(NoWarn);IDE0079</NoWarn>
  </PropertyGroup>
```

- [ ] **Step 7: Run the full test suite**

```bash
dotnet test
```

Expected: `failed: 0`, with a total of Plan A's 252 plus the 2 scaffold tests. **If any of Plan A's 252 now fail, stop and fix that before continuing** — the scaffold must not disturb Core.

- [ ] **Step 8: Run the app once by hand**

```bash
dotnet run --project src/StickyMD.App/StickyMD.App.csproj
```

Expected: a message box titled "StickyMD" appears; dismissing it exits the process. This proves the TFM resolves and WPF starts. **If it throws `FileNotFoundException: Microsoft.Windows.SDK.NET`, the target framework is wrong** — look for a stray `net10.0-windows` and re-read the Global Constraints.

- [ ] **Step 9: Commit**

Hand these to the user; do not run them:

```bash
git add src/StickyMD.App tests/StickyMD.App.Tests StickyMD.sln
git commit -m "Add the StickyMD.App WPF project and its test project

The App targets net10.0-windows10.0.17763.0 rather than plain
net10.0-windows. The suffix is load-bearing: WebView2CompositionControl
renders through D3DImage and needs the Windows SDK WinRT projections,
which only a Windows-version-specific TFM brings in. Without it the
control throws FileNotFoundException for Microsoft.Windows.SDK.NET from
inside TryInitializeD3DImage(), and the stack trace points at D3D while
never mentioning the framework -- an hour lost to the wrong suspect.

The manifest declares PerMonitorV2 because note geometry is stored in
physical screen pixels. Under DPI virtualisation Windows would report a
scaled window rect and scaled monitor bounds, and a note saved on a 150%
display would restore in the wrong place on a 100% one.

The test project matches the App TFM exactly and sets UseWPF, since it
compiles against App types whose signatures mention WPF."
```

---

## Task 2: `NotePath` — one canonical form at every boundary

**Resolves open finding 1.** Everything after this task keys dictionaries by note path, so the disagreement has to be gone before any of it is written.

**Files:**

- Create: `src/StickyMD.Core/Notes/NotePath.cs`
- Test: `tests/StickyMD.Core.Tests/Notes/NotePathTests.cs`
- Test: `tests/StickyMD.Core.Tests/Notes/PathIdentityTests.cs`
- Modify: `src/StickyMD.Core/Notes/WriteLedger.cs`
- Modify: `src/StickyMD.Core/Notes/NoteRepository.cs`
- Modify: `src/StickyMD.Core/Notes/NoteWatcher.cs`
- Modify: `src/StickyMD.Core/Persistence/RecoveryStore.cs`

**Interfaces:**

- Consumes: nothing new.
- Produces:
  - `static string NotePath.Canonical(string path)`
  - `static bool NotePath.TryCanonical(string? path, out string canonical)`
  - `static StringComparer NotePath.Comparer` — `StringComparer.OrdinalIgnoreCase`
  - `static bool NotePath.AreSame(string? a, string? b)`
  - `static bool NotePath.IsCanonical(string path)`
  - `static Dictionary<string, TValue> NotePath.NewMap<TValue>()`

Tasks 8, 11, 12, 13 and 14 all key state by note path and must use `NotePath.NewMap` / `NotePath.Comparer`.

- [ ] **Step 1: Write the failing tests for `NotePath`**

`tests/StickyMD.Core.Tests/Notes/NotePathTests.cs`:

```csharp
using Shouldly;
using StickyMD.Core.Notes;
using StickyMD.Core.Tests.TestSupport;

namespace StickyMD.Core.Tests.Notes;

public class NotePathTests
{
    [Fact]
    public void Canonical_makes_a_relative_path_absolute()
    {
        var canonical = NotePath.Canonical("a.md");

        Path.IsPathRooted(canonical).ShouldBeTrue();
        canonical.ShouldEndWith("a.md");
    }

    [Fact]
    public void Canonical_collapses_dot_and_dotdot_segments()
    {
        using var dir = new TempDir();
        var messy = Path.Combine(dir.Path, "sub", "..", ".", "note.md");

        NotePath.Canonical(messy).ShouldBe(Path.Combine(dir.Path, "note.md"));
    }

    [Fact]
    public void Canonical_preserves_case_exactly_as_given()
    {
        using var dir = new TempDir();
        var mixed = Path.Combine(dir.Path, "StandUp.MD");

        NotePath.Canonical(mixed).ShouldBe(mixed);
    }

    [Fact]
    public void Canonical_trims_a_trailing_separator()
    {
        using var dir = new TempDir();

        NotePath.Canonical(dir.Path + Path.DirectorySeparatorChar)
            .ShouldBe(dir.Path.TrimEnd(Path.DirectorySeparatorChar));
    }

    [Fact]
    public void Canonical_leaves_a_drive_root_alone()
    {
        // Path.TrimEndingDirectorySeparator must NOT turn "C:\" into "C:",
        // which is drive-RELATIVE and names a completely different location.
        var root = Path.GetPathRoot(Path.GetTempPath())!;

        NotePath.Canonical(root).ShouldBe(root);
    }

    [Fact]
    public void Canonical_is_idempotent()
    {
        using var dir = new TempDir();
        var once = NotePath.Canonical(Path.Combine(dir.Path, "sub", "..", "n.md"));

        NotePath.Canonical(once).ShouldBe(once);
    }

    [Fact]
    public void Canonical_rejects_null_and_empty()
    {
        Should.Throw<ArgumentException>(() => NotePath.Canonical(null!));
        Should.Throw<ArgumentException>(() => NotePath.Canonical(""));
        Should.Throw<ArgumentException>(() => NotePath.Canonical("   "));
    }

    [Fact]
    public void TryCanonical_returns_false_instead_of_throwing_on_an_unusable_path()
    {
        NotePath.TryCanonical(null, out _).ShouldBeFalse();
        NotePath.TryCanonical("", out _).ShouldBeFalse();
        NotePath.TryCanonical("C:\\a\0b.md", out _).ShouldBeFalse();
    }

    [Fact]
    public void TryCanonical_yields_the_canonical_form_on_success()
    {
        using var dir = new TempDir();
        var messy = Path.Combine(dir.Path, ".", "n.md");

        NotePath.TryCanonical(messy, out var canonical).ShouldBeTrue();
        canonical.ShouldBe(Path.Combine(dir.Path, "n.md"));
    }

    [Fact]
    public void Comparer_is_case_insensitive()
    {
        NotePath.Comparer.Equals(@"C:\N\A.md", @"c:\n\a.md").ShouldBeTrue();
        NotePath.Comparer.GetHashCode(@"C:\N\A.md")
            .ShouldBe(NotePath.Comparer.GetHashCode(@"c:\n\a.md"));
    }

    [Fact]
    public void AreSame_canonicalises_both_sides_before_comparing()
    {
        using var dir = new TempDir();
        var a = Path.Combine(dir.Path, "sub", "..", "Note.md");
        var b = Path.Combine(dir.Path, "note.MD");

        NotePath.AreSame(a, b).ShouldBeTrue();
    }

    [Fact]
    public void AreSame_is_false_when_either_side_is_unusable()
    {
        NotePath.AreSame(null, null).ShouldBeFalse();
        NotePath.AreSame(@"C:\n\a.md", null).ShouldBeFalse();
    }

    [Fact]
    public void IsCanonical_recognises_its_own_output()
    {
        using var dir = new TempDir();
        var messy = Path.Combine(dir.Path, ".", "n.md");

        NotePath.IsCanonical(messy).ShouldBeFalse();
        NotePath.IsCanonical(NotePath.Canonical(messy)).ShouldBeTrue();
    }

    [Fact]
    public void NewMap_finds_an_entry_stored_under_a_different_case()
    {
        var map = NotePath.NewMap<int>();
        map[@"C:\Notes\Standup.md"] = 7;

        map[@"c:\notes\standup.md"].ShouldBe(7);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

```bash
dotnet test --filter FullyQualifiedName~NotePathTests
```

Expected: FAIL to compile — `NotePath` does not exist.

- [ ] **Step 3: Write `NotePath`**

`src/StickyMD.Core/Notes/NotePath.cs`:

```csharp
namespace StickyMD.Core.Notes;

/// <summary>
/// The one canonical form of a note path, and the one comparer for it.
/// </summary>
/// <remarks>
/// Plan A shipped four disagreeing answers to "what is a note's path?":
/// NoteRepository handed out verbatim-joined paths, NoteWatcher emitted
/// GetFullPath-normalised ones, RecoveryStore hashed a lowercased variant, and
/// NoteIndexStore accepted whatever key was in the JSON. A path from one would
/// not compare equal to a path from another, so any map keyed by note path --
/// the window manager's open-notes map above all -- would silently miss and
/// open a second window on a file that was already open.
///
/// THE RULES:
///  1. Canonical form is GetFullPath, with any trailing separator trimmed.
///  2. Case is PRESERVED. The on-disk case is what the user sees in Explorer
///     and in notes.json; lowercasing it would write a wrong-looking path into
///     a user-facing file and destroy the only display-quality name we have.
///  3. Comparison is ALWAYS <see cref="Comparer"/>, which is case-insensitive.
///     That is what makes rule 2 free.
///  4. Canonicalise where a path is PRODUCED, never where it is consumed. Any
///     API handing out a note path returns canonical form, so no caller has to
///     remember to normalise an input it was given.
///
/// KNOWN LIMIT: GetFullPath does not resolve symlinks, junctions, 8.3 short
/// names, or the true on-disk casing. Those need an open handle and Win32
/// (GetFinalPathNameByHandle), which Core may not touch and which fails for a
/// file that does not exist yet -- and canonicalising the path of a note about
/// to be CREATED is a requirement here. So the same file reached through a
/// symlink and through its target still compares unequal. Accepted for v1, and
/// recorded rather than hidden.
/// </remarks>
public static class NotePath
{
    /// <summary>
    /// The only comparer any note-path-keyed dictionary, set, or equality test
    /// may use. Windows filesystems are case-insensitive in every configuration
    /// StickyMD supports.
    /// </summary>
    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    public static string Canonical(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A note path cannot be empty.", nameof(path));

        // TrimEndingDirectorySeparator, not TrimEnd('\\'): it deliberately
        // leaves a ROOT alone, so "C:\" survives intact. TrimEnd would produce
        // "C:", which is drive-relative and names a different location.
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    /// <summary>
    /// Canonicalises without throwing. Used on paths arriving from outside the
    /// app -- FileSystemWatcher event arguments, a hand-edited notes.json --
    /// where an unusable value must cost one entry, not the whole operation.
    /// </summary>
    public static bool TryCanonical(string? path, out string canonical)
    {
        canonical = string.Empty;

        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            canonical = Canonical(path);
            return true;
        }
        catch (ArgumentException) { return false; }      // embedded NUL, bad chars
        catch (NotSupportedException) { return false; }  // e.g. a stray colon
        catch (IOException) { return false; }            // covers PathTooLongException
        catch (System.Security.SecurityException) { return false; }
    }

    /// <summary>True when both sides name the same note.</summary>
    public static bool AreSame(string? a, string? b)
        => TryCanonical(a, out var ca)
        && TryCanonical(b, out var cb)
        && Comparer.Equals(ca, cb);

    /// <summary>
    /// True when <paramref name="path"/> is already canonical. Used by tests to
    /// assert the "canonicalise at production" rule at every boundary.
    /// </summary>
    public static bool IsCanonical(string path)
        => TryCanonical(path, out var canonical)
        && string.Equals(path, canonical, StringComparison.Ordinal);

    /// <summary>A dictionary keyed by note path, with the right comparer.</summary>
    public static Dictionary<string, TValue> NewMap<TValue>() => new(Comparer);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test --filter FullyQualifiedName~NotePathTests
```

Expected: PASS, 14 tests.

- [ ] **Step 5: Route `WriteLedger` through `NotePath`**

In `src/StickyMD.Core/Notes/WriteLedger.cs`, replace the dictionary construction and the `Normalize` method:

```csharp
    private readonly Dictionary<string, WriteFingerprint> _entries =
        NotePath.NewMap<WriteFingerprint>();

    private readonly object _gate = new();

    /// <summary>
    /// Kept under its historical name; <see cref="NotePath.Canonical"/> is the
    /// one definition. Two normalisation functions is exactly how Plan A's
    /// identity mismatch happened, so there is only one now.
    /// </summary>
    public static string Normalize(string path) => NotePath.Canonical(path);
```

- [ ] **Step 6: Make `NoteRepository` hand out canonical paths**

Three edits in `src/StickyMD.Core/Notes/NoteRepository.cs`.

The `NotesRoot` property — canonicalise the root once, so every `Path.Combine` off it is canonical by construction:

```csharp
    /// <summary>
    /// Canonical. Every path this class produces is built by combining onto
    /// this, which is what makes them canonical without a second pass.
    /// </summary>
    public string NotesRoot { get; } = NotePath.Canonical(notesRoot);
```

`EnumerateRoot` — canonicalise each result and drop any that cannot be:

```csharp
    public IReadOnlyList<string> EnumerateRoot()
    {
        if (!Directory.Exists(NotesRoot)) return [];

        // Combining onto a canonical root already yields canonical paths, but
        // canonicalise explicitly anyway: this method is the boundary the rest
        // of the app trusts, and a filename the filesystem accepts while
        // GetFullPath rejects must cost that one entry, not the listing.
        var notes = new List<string>();

        foreach (var path in Directory.EnumerateFiles(
            NotesRoot, "*.md", SearchOption.TopDirectoryOnly))
        {
            if (NotePath.TryCanonical(path, out var canonical))
                notes.Add(canonical);
        }

        notes.Sort(NotePath.Comparer);
        return notes;
    }
```

`Rename` — return canonical, and stop returning the caller's spelling:

```csharp
        var fullCurrent = NotePath.Canonical(currentPath);

        var directory = Path.GetDirectoryName(fullCurrent) ?? NotesRoot;
        var target = NotePath.Canonical(Path.Combine(directory, name));

        // Truly identical, case included: nothing to do. Return the CANONICAL
        // form, not the caller's spelling -- a caller that passed a relative or
        // dot-laden path must not get it back and then key a map with it.
        if (string.Equals(fullCurrent, target, StringComparison.Ordinal))
            return fullCurrent;
```

The remainder of `Rename` is unchanged; it already ends `File.Move(fullCurrent, target); return target;`, and `target` is now canonical.

`CreateNewRecorded` needs no code change — `Path.Combine(NotesRoot, …)` off a canonical root is canonical. Add a one-line comment saying exactly that, so the next reader does not "fix" it:

```csharp
        // Canonical by construction: NotesRoot is canonical and Combine only
        // appends a bare filename. No second normalisation pass needed.
        var path = Path.Combine(NotesRoot, $"{date}-{UntitledStem}.md");
```

- [ ] **Step 7: Route `NoteWatcher` and `RecoveryStore` through `NotePath`**

In `src/StickyMD.Core/Notes/NoteWatcher.cs`:

`_pending` becomes `NotePath.NewMap<long>()`.

`Enqueue` canonicalises defensively instead of relying on its callers' try/catch:

```csharp
    private void Enqueue(string fullPath)
    {
        // TryCanonical rather than Canonical: these paths come straight from
        // FileSystemWatcher, and one unusable name must cost one event, never
        // the watcher.
        if (!NotePath.TryCanonical(fullPath, out var canonical)) return;

        lock (_gate) _pending[canonical] = Environment.TickCount64 + _debounceMs;
    }
```

`OnRenamed`'s genuine-rename branch canonicalises both sides and skips the event if either fails:

```csharp
            if (!NotePath.TryCanonical(e.OldFullPath, out var oldCanonical)) return;
            if (!NotePath.TryCanonical(e.FullPath, out var newCanonical)) return;

            Renamed?.Invoke(oldCanonical, newCanonical);
```

In `src/StickyMD.Core/Persistence/RecoveryStore.cs`, `FileNameFor` swaps `WriteLedger.Normalize` for `NotePath.Canonical`. The `ToLowerInvariant()` **stays** — it looks like it contradicts the preserve-case rule, so say why:

```csharp
    public static string FileNameFor(string notePath)
    {
        // Lowercased on purpose, and it does NOT violate NotePath's
        // preserve-case rule: this is a HASH KEY and is never shown to anyone.
        // Note identity is case-insensitive, and a hash cannot be made
        // case-insensitive after the fact -- so the folding has to happen
        // before hashing, or "Standup.md" and "standup.md" would get two
        // snapshots for one note.
        var normalized = NotePath.Canonical(notePath).ToLowerInvariant();
```

- [ ] **Step 8: Write the cross-boundary identity tests**

These are the tests that would have caught the original finding.

`tests/StickyMD.Core.Tests/Notes/PathIdentityTests.cs`:

```csharp
using Shouldly;
using StickyMD.Core.Notes;
using StickyMD.Core.Persistence;
using StickyMD.Core.Tests.TestSupport;

namespace StickyMD.Core.Tests.Notes;

/// <summary>
/// The invariant that resolves Plan A's open finding 1: every boundary that
/// hands out a note path hands out the SAME form, so a map keyed by one
/// component's path finds an entry stored under another's.
/// </summary>
public class PathIdentityTests
{
    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }

    private static IClock Clock()
        => new FixedClock(new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void EnumerateRoot_returns_canonical_paths()
    {
        using var dir = new TempDir();
        dir.WriteFile("a.md", "a");
        dir.WriteFile("B.md", "b");

        // Deliberately hand the repository a NON-canonical root.
        var repo = new NoteRepository(
            Path.Combine(dir.Path, "sub", ".."), Clock());

        foreach (var path in repo.EnumerateRoot())
            NotePath.IsCanonical(path).ShouldBeTrue(path);
    }

    [Fact]
    public void NotesRoot_is_canonical_even_when_the_constructor_was_given_junk()
    {
        using var dir = new TempDir();

        var repo = new NoteRepository(
            Path.Combine(dir.Path, ".", "sub", ".."), Clock());

        repo.NotesRoot.ShouldBe(dir.Path.TrimEnd(Path.DirectorySeparatorChar));
    }

    [Fact]
    public void CreateNewRecorded_returns_a_canonical_path()
    {
        using var dir = new TempDir();
        var repo = new NoteRepository(Path.Combine(dir.Path, "x", ".."), Clock());

        var (path, _) = repo.CreateNewRecorded();

        NotePath.IsCanonical(path).ShouldBeTrue(path);
    }

    [Fact]
    public void Rename_returns_a_canonical_path()
    {
        using var dir = new TempDir();
        dir.WriteFile("old.md", "x");
        var repo = new NoteRepository(dir.Path, Clock());

        var renamed = repo.Rename(Path.Combine(dir.Path, ".", "old.md"), "new");

        NotePath.IsCanonical(renamed).ShouldBeTrue(renamed);
        renamed.ShouldBe(Path.Combine(dir.Path, "new.md"));
    }

    [Fact]
    public void A_repository_path_and_a_watcher_path_land_on_the_same_map_entry()
    {
        using var dir = new TempDir();
        dir.WriteFile("standup.md", "x");
        var repo = new NoteRepository(dir.Path, Clock());

        var fromRepo = repo.EnumerateRoot()[0];

        // What NoteWatcher emits for the same file, reached through a different
        // spelling than the repository's.
        var fromWatcher = NotePath.Canonical(
            Path.Combine(dir.Path, "sub", "..", "STANDUP.MD"));

        var map = NotePath.NewMap<string>();
        map[fromRepo] = "window";

        map.ContainsKey(fromWatcher).ShouldBeTrue(
            $"repo gave '{fromRepo}', watcher gave '{fromWatcher}'");
    }

    [Fact]
    public void WriteLedger_Normalize_and_NotePath_Canonical_agree()
    {
        using var dir = new TempDir();
        var messy = Path.Combine(dir.Path, ".", "n.md");

        WriteLedger.Normalize(messy).ShouldBe(NotePath.Canonical(messy));
    }

    [Fact]
    public void The_write_ledger_suppresses_a_write_looked_up_by_a_different_spelling()
    {
        using var dir = new TempDir();
        var ledger = new WriteLedger();

        var written = Path.Combine(dir.Path, "Note.md");
        var outcome = NoteFile.AtomicWrite(written, "hello", NoteFormat.Canonical);
        ledger.Record(written, outcome);

        var lookedUpAs = Path.Combine(dir.Path, "sub", "..", "note.MD");

        ledger.IsOwnWrite(lookedUpAs, outcome.Size, outcome.ContentHash)
            .ShouldBeTrue();
    }

    [Fact]
    public void A_recovery_snapshot_is_found_through_a_different_spelling()
    {
        using var dir = new TempDir();
        var store = new RecoveryStore(dir.Path);

        var saved = Path.Combine(dir.Path, "Standup.md");
        store.Save(new RecoveryEnvelope(
            saved, "unsaved", new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc), "hash"));

        var lookedUpAs = Path.Combine(dir.Path, ".", "standup.MD");

        store.TryLoad(lookedUpAs).ShouldNotBeNull();
    }
}
```

- [ ] **Step 9: Run the identity tests, then the whole suite**

```bash
dotnet test --filter FullyQualifiedName~PathIdentityTests
dotnet test
```

Expected: the 8 identity tests pass, and the full suite reports `failed: 0`.

**Read the runner's total and record it in the commit message — do not compute it.** What matters is `failed: 0` and that **none of Plan A's 252 regressed**. If `NoteRepositoryTests` or `NoteWatcherTests` fail, canonicalisation changed an assertion Plan A relied on: fix the production code to preserve the documented behaviour, and only change a Plan A test if its expectation was itself the non-canonical form.

- [ ] **Step 10: Commit**

Hand these to the user:

```bash
git add src/StickyMD.Core tests/StickyMD.Core.Tests
git commit -m "Give a note path one canonical form at every boundary

Four components disagreed about what a note's path is. NoteRepository
handed out verbatim-joined paths, NoteWatcher emitted GetFullPath-
normalised ones, RecoveryStore hashed a lowercased variant, and
NoteIndexStore took whatever key the JSON held. A path from one would not
compare equal to a path from another, so the window manager's open-notes
map would have missed and opened a second window on a file that was
already open -- a bug that looks like it lives in the window code.

NotePath is now the single definition: GetFullPath with any trailing
separator trimmed, case preserved, compared case-insensitively, and
applied where a path is PRODUCED rather than where it is consumed. That
last rule is the one that matters -- it means no caller has to remember.

Case is preserved rather than folded because notes.json keys and the
window title are user-facing, and the on-disk case is the only
display-quality name available. RecoveryStore still lowercases before
hashing, which is not a contradiction: a hash cannot be made
case-insensitive after the fact, and that filename is never shown.

Known limit, recorded in NotePath's remarks: GetFullPath does not resolve
symlinks or 8.3 names. Doing so needs an open handle and Win32, which
Core may not touch, and fails for a note that does not exist yet."
```

---

## Task 3: Validation on load, and somewhere for the report to go

**Resolves open finding 2.** `{"color": 99}` currently loads cleanly and then crashes `NotePalette.Get`. This task clamps values on load, reports every correction, and gives the report a destination.

**Files:**

- Create: `src/StickyMD.Core/Persistence/ValidationIssue.cs`
- Create: `src/StickyMD.Core/Persistence/StateValidator.cs`
- Create: `src/StickyMD.Core/Diagnostics/DiagnosticsLog.cs`
- Test: `tests/StickyMD.Core.Tests/Persistence/StateValidatorTests.cs`
- Test: `tests/StickyMD.Core.Tests/Persistence/NoteIndexStoreValidationTests.cs`
- Test: `tests/StickyMD.Core.Tests/Diagnostics/DiagnosticsLogTests.cs`
- Modify: `src/StickyMD.Core/Persistence/NoteIndexStore.cs`
- Modify: `src/StickyMD.Core/Persistence/SettingsStore.cs`
- Modify: `src/StickyMD.Core/Persistence/AppPaths.cs`

**Interfaces:**

- Consumes: `NotePath` (Task 2), `NoteState`, `AppSettings`, `NoteColor`, `ThemePreference`.
- Produces:
  - `sealed record ValidationIssue(string Scope, string Field, string Detail)`
  - `static class StateValidator` with:
    - `const int MinNoteWidth = 160`, `MinNoteHeight = 120`, `MaxNoteEdge = 8192`, `MaxCoordinate = 65536`
    - `const double MinOpacity = 0.20`, `MaxOpacity = 1.0`
    - `NoteState ValidateNote(NoteState raw, AppSettings defaults, string scope, DateTime nowUtc, List<ValidationIssue> issues)`
    - `AppSettings ValidateSettings(AppSettings raw, List<ValidationIssue> issues)`
  - `static class DiagnosticsLog` with `void Write(string path, string message)` and `void WriteAll(string path, string heading, IEnumerable<string> lines)`
  - `NoteIndexStore.LastLoadIssues` and `SettingsStore.LastLoadIssues`, both `IReadOnlyList<ValidationIssue>`
  - `AppPaths.DiagnosticsFile`, `AppPaths.WebViewUserDataDir`

Task 14's bootstrapper consumes `LastLoadIssues` from both stores and hands them to `DiagnosticsLog`. Task 8 consumes `AppPaths.WebViewUserDataDir`.

### The validation rules, and why each bound is where it is

| Field                     | Rule                                                                | Why this bound                                                                                    |
| ------------------------- | ------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------- |
| `Color`                   | `Enum.IsDefined` else `defaults.DefaultColor`                       | `{"color": 99}` is the reported crash. `JsonStringEnumConverter` accepts numbers unchecked        |
| `Opacity`                 | `NaN`/infinity → `defaults.DefaultOpacity`; else clamp to 0.20–1.0  | Below ~0.20 a note is effectively invisible and cannot be found with the mouse to be fixed        |
| `W`                       | `<= 0` → `defaults.DefaultWidth`; else clamp to 160–8192            | 160px is the narrowest width the header buttons fit in; 8192 covers an 8K display                 |
| `H`                       | `<= 0` → `defaults.DefaultHeight`; else clamp to 120–8192           | Same reasoning, vertically                                                                        |
| `X`, `Y`                  | Clamp to ±65536                                                     | `PixelRect.Right`/`Bottom` are `int` addition; a saved `int.MaxValue` overflows `WindowPlacement` |
| `Monitor`                 | Whitespace → `null`. No issue reported                              | Written from v1 but not yet read; an empty string and absent mean the same thing                  |
| `LastOpenedUtc`           | Non-UTC `Kind` → `SpecifyKind`/`ToUniversalTime`; future → `nowUtc` | A year-9999 timestamp would pin a note to the top of Recent Notes forever                         |
| `IsOpen`, `AlwaysOnTop`   | Untouched                                                           | `bool` has no invalid value                                                                       |
| `NotesRoot` (settings)    | Not rooted, or `GetFullPath` throws → default; else canonical       | A relative notes root would resolve against the process working directory                         |
| `Theme` (settings)        | `Enum.IsDefined` else `System`                                      | Same numeric-enum hole as `Color`                                                                 |
| Hotkey strings (settings) | Whitespace → the default string                                     | Plan C parses them; an empty string there would register nothing and report nothing               |

**X and Y are deliberately _not_ clamped to a monitor here.** That is `WindowPlacement.Clamp`'s job and it needs the real monitor list, which Core cannot see. Validation only stops the arithmetic from overflowing.

- [ ] **Step 1: Write the failing tests for `StateValidator`**

`tests/StickyMD.Core.Tests/Persistence/StateValidatorTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run them to verify they fail**

```bash
dotnet test --filter FullyQualifiedName~StateValidatorTests
```

Expected: FAIL to compile — `ValidationIssue` and `StateValidator` do not exist.

- [ ] **Step 3: Write `ValidationIssue` and `StateValidator`**

`src/StickyMD.Core/Persistence/ValidationIssue.cs`:

```csharp
namespace StickyMD.Core.Persistence;

/// <summary>
/// One correction made while loading persisted state.
/// </summary>
/// <param name="Scope">
/// What was being loaded -- a note path, or "settings.json". Enough for the
/// user to know which of their notes moved.
/// </param>
/// <param name="Field">The JSON property name, so it can be hand-corrected.</param>
/// <param name="Detail">What was found and what was used instead.</param>
/// <remarks>
/// This type exists because clamping silently would break the governing rule.
/// "Never die silently" covers quiet recovery too: a note that moves or
/// changes colour on its own, with nothing anywhere saying why, is a bug
/// report nobody can act on.
/// </remarks>
public sealed record ValidationIssue(string Scope, string Field, string Detail)
{
    public override string ToString() => $"{Scope}: {Field} -- {Detail}";
}
```

`src/StickyMD.Core/Persistence/StateValidator.cs`:

```csharp
using StickyMD.Core.Notes;
using StickyMD.Core.Theming;

namespace StickyMD.Core.Persistence;

/// <summary>
/// Clamps or defaults persisted values that deserialise cleanly but are not
/// usable, reporting every correction.
/// </summary>
/// <remarks>
/// Deserialization checks SHAPE, not VALUES. System.Text.Json's
/// JsonStringEnumConverter accepts numeric enum values and does not
/// range-check them, so {"color": 99} produced (NoteColor)99 and crashed
/// NotePalette.Get with ArgumentOutOfRangeException -- a crash on startup,
/// after a successful load, pointing at the theming code.
///
/// Every rule here is a clamp or a default, never a rejection. Losing a note's
/// remembered position is acceptable; refusing to open the note is not.
/// </remarks>
public static class StateValidator
{
    /// <summary>Narrowest width the header buttons still fit in.</summary>
    public const int MinNoteWidth = 160;

    /// <summary>Shortest height that leaves a usable body under the header.</summary>
    public const int MinNoteHeight = 120;

    /// <summary>Generous enough for an 8K display, small enough to be sane.</summary>
    public const int MaxNoteEdge = 8192;

    /// <summary>
    /// WindowPlacement computes Right = X + Width with int addition. A saved
    /// int.MaxValue would overflow to a negative Right, and the clamp would
    /// then place the note somewhere absurd instead of rejecting it.
    /// </summary>
    public const int MaxCoordinate = 65536;

    /// <summary>
    /// Below roughly this, a note is invisible and cannot be found with the
    /// mouse in order to be fixed. An unrecoverable UI state is a lost note.
    /// </summary>
    public const double MinOpacity = 0.20;

    public const double MaxOpacity = 1.0;

    public static NoteState ValidateNote(
        NoteState raw,
        AppSettings defaults,
        string scope,
        DateTime nowUtc,
        List<ValidationIssue> issues)
    {
        void Report(string field, string detail)
            => issues.Add(new ValidationIssue(scope, field, detail));

        var color = raw.Color;
        if (!Enum.IsDefined(color))
        {
            Report("color", $"'{(int)raw.Color}' is not a note colour; used {defaults.DefaultColor}.");
            color = defaults.DefaultColor;
        }

        var opacity = raw.Opacity;
        if (double.IsNaN(opacity) || double.IsInfinity(opacity))
        {
            // Math.Clamp(NaN, …) returns NaN, so the clamp below would let it
            // through -- and Window.Opacity = NaN throws at assignment.
            Report("opacity", $"'{raw.Opacity}' is not a number; used {defaults.DefaultOpacity}.");
            opacity = defaults.DefaultOpacity;
        }
        else if (opacity < MinOpacity || opacity > MaxOpacity)
        {
            var clamped = Math.Clamp(opacity, MinOpacity, MaxOpacity);
            Report("opacity", $"{opacity} is outside {MinOpacity}-{MaxOpacity}; used {clamped}.");
            opacity = clamped;
        }

        var width = ClampEdge(raw.W, defaults.DefaultWidth, MinNoteWidth, "w", Report);
        var height = ClampEdge(raw.H, defaults.DefaultHeight, MinNoteHeight, "h", Report);

        var x = ClampCoordinate(raw.X, "x", Report);
        var y = ClampCoordinate(raw.Y, "y", Report);

        var monitor = string.IsNullOrWhiteSpace(raw.Monitor) ? null : raw.Monitor;

        var lastOpened = ToUtc(raw.LastOpenedUtc);
        if (lastOpened > nowUtc)
        {
            // A future timestamp would sort this note to the top of Recent
            // Notes permanently, and Recent means recently OPENED.
            Report("lastOpenedUtc", $"{lastOpened:O} is in the future; used now.");
            lastOpened = nowUtc;
        }

        return raw with
        {
            X = x,
            Y = y,
            W = width,
            H = height,
            Monitor = monitor,
            Color = color,
            Opacity = opacity,
            LastOpenedUtc = lastOpened,
        };
    }

    public static AppSettings ValidateSettings(
        AppSettings raw, List<ValidationIssue> issues)
    {
        const string scope = "settings.json";
        var defaults = new AppSettings();

        void Report(string field, string detail)
            => issues.Add(new ValidationIssue(scope, field, detail));

        var notesRoot = raw.NotesRoot;
        if (!Path.IsPathRooted(notesRoot)
            || !NotePath.TryCanonical(notesRoot, out notesRoot))
        {
            // A relative notes root would resolve against the process working
            // directory, which for a shortcut or a startup launch is arbitrary
            // -- notes would appear to vanish depending on how the app started.
            Report("notesRoot", $"'{raw.NotesRoot}' is not an absolute path; used the default.");
            notesRoot = defaults.NotesRoot;
        }

        var defaultColor = raw.DefaultColor;
        if (!Enum.IsDefined(defaultColor))
        {
            Report("defaultColor", $"'{(int)raw.DefaultColor}' is not a note colour; used {defaults.DefaultColor}.");
            defaultColor = defaults.DefaultColor;
        }

        var theme = raw.Theme;
        if (!Enum.IsDefined(theme))
        {
            Report("theme", $"'{(int)raw.Theme}' is not a theme preference; used {defaults.Theme}.");
            theme = defaults.Theme;
        }

        var opacity = raw.DefaultOpacity;
        if (double.IsNaN(opacity) || double.IsInfinity(opacity)
            || opacity < MinOpacity || opacity > MaxOpacity)
        {
            var replacement = double.IsFinite(opacity)
                ? Math.Clamp(opacity, MinOpacity, MaxOpacity)
                : defaults.DefaultOpacity;
            Report("defaultOpacity", $"'{opacity}' is unusable; used {replacement}.");
            opacity = replacement;
        }

        var width = ClampEdge(
            raw.DefaultWidth, defaults.DefaultWidth, MinNoteWidth, "defaultWidth", Report);
        var height = ClampEdge(
            raw.DefaultHeight, defaults.DefaultHeight, MinNoteHeight, "defaultHeight", Report);

        var newNote = raw.NewNoteHotkey;
        if (string.IsNullOrWhiteSpace(newNote))
        {
            Report("newNoteHotkey", $"was blank; used '{defaults.NewNoteHotkey}'.");
            newNote = defaults.NewNoteHotkey;
        }

        var showHide = raw.ShowHideHotkey;
        if (string.IsNullOrWhiteSpace(showHide))
        {
            Report("showHideHotkey", $"was blank; used '{defaults.ShowHideHotkey}'.");
            showHide = defaults.ShowHideHotkey;
        }

        return raw with
        {
            NotesRoot = notesRoot,
            DefaultColor = defaultColor,
            Theme = theme,
            DefaultOpacity = opacity,
            DefaultWidth = width,
            DefaultHeight = height,
            NewNoteHotkey = newNote,
            ShowHideHotkey = showHide,
        };
    }

    private static int ClampEdge(
        int raw, int fallback, int minimum, string field, Action<string, string> report)
    {
        if (raw <= 0)
        {
            report(field, $"{raw} is not a size; used {fallback}.");
            return fallback;
        }

        var clamped = Math.Clamp(raw, minimum, MaxNoteEdge);
        if (clamped != raw) report(field, $"{raw} is outside {minimum}-{MaxNoteEdge}; used {clamped}.");
        return clamped;
    }

    private static int ClampCoordinate(
        int raw, string field, Action<string, string> report)
    {
        var clamped = Math.Clamp(raw, -MaxCoordinate, MaxCoordinate);
        if (clamped != raw) report(field, $"{raw} is beyond +/-{MaxCoordinate}; used {clamped}.");
        return clamped;
    }

    /// <summary>
    /// A round-tripped DateTime can come back Unspecified or Local. Everything
    /// downstream -- Recent Notes ordering, the recovery envelope -- assumes
    /// UTC, so settle it here rather than at each comparison.
    /// </summary>
    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
```

- [ ] **Step 4: Run the validator tests to verify they pass**

```bash
dotnet test --filter FullyQualifiedName~StateValidatorTests
```

Expected: PASS, 22 tests.

- [ ] **Step 5: Write the failing tests for per-entry index loading**

`tests/StickyMD.Core.Tests/Persistence/NoteIndexStoreValidationTests.cs`:

```csharp
using Shouldly;
using StickyMD.Core.Notes;
using StickyMD.Core.Persistence;
using StickyMD.Core.Theming;
using StickyMD.Core.Tests.TestSupport;

namespace StickyMD.Core.Tests.Persistence;

public class NoteIndexStoreValidationTests
{
    private static NoteIndexStore StoreWith(TempDir dir, string json)
        => new(dir.WriteFile("notes.json", json));

    [Fact]
    public void An_out_of_range_color_number_loads_and_is_defaulted()
    {
        using var dir = new TempDir();
        var store = StoreWith(dir, """
        {
          "version": 1,
          "notes": {
            "C:\\Notes\\a.md": {
              "x": 10, "y": 10, "w": 300, "h": 340,
              "monitor": null, "color": 99, "opacity": 1.0,
              "alwaysOnTop": false, "isOpen": true,
              "lastOpenedUtc": "2026-09-01T10:00:00Z"
            }
          }
        }
        """);

        var index = store.Load();

        index.Notes.Count.ShouldBe(1);
        var state = index.Notes.Values.Single();
        Enum.IsDefined(state.Color).ShouldBeTrue();
        store.LastLoadIssues.ShouldContain(i => i.Field == "color");
        store.LastCorruptBackupPath.ShouldBeNull("one bad value is not a corrupt file");
    }

    [Fact]
    public void One_unparseable_entry_costs_only_that_entry()
    {
        using var dir = new TempDir();
        var store = StoreWith(dir, """
        {
          "version": 1,
          "notes": {
            "C:\\Notes\\good.md": {
              "x": 10, "y": 10, "w": 300, "h": 340,
              "color": "blue", "opacity": 1.0,
              "alwaysOnTop": false, "isOpen": true,
              "lastOpenedUtc": "2026-09-01T10:00:00Z"
            },
            "C:\\Notes\\bad.md": {
              "x": 10, "y": 10, "w": 300, "h": 340,
              "color": "banana", "opacity": 1.0,
              "alwaysOnTop": false, "isOpen": true,
              "lastOpenedUtc": "2026-09-01T10:00:00Z"
            }
          }
        }
        """);

        var index = store.Load();

        // Before per-entry deserialization, the unknown enum STRING threw
        // JsonException, the whole file went to notes.json.corrupt-1, and every
        // note on the desktop lost its position. One bad value must cost one
        // note's geometry.
        index.Notes.Count.ShouldBe(1);
        index.Notes.Keys.Single().ShouldEndWith("good.md");
        store.LastLoadIssues.ShouldContain(i => i.Scope.EndsWith("bad.md"));
        store.LastCorruptBackupPath.ShouldBeNull();
    }

    [Fact]
    public void Keys_are_re_keyed_to_canonical_form()
    {
        using var dir = new TempDir();
        var store = StoreWith(dir, """
        {
          "version": 1,
          "notes": {
            "C:\\Notes\\sub\\..\\a.md": {
              "x": 10, "y": 10, "w": 300, "h": 340,
              "color": "blue", "opacity": 1.0,
              "alwaysOnTop": false, "isOpen": true,
              "lastOpenedUtc": "2026-09-01T10:00:00Z"
            }
          }
        }
        """);

        var index = store.Load();

        var key = index.Notes.Keys.Single();
        NotePath.IsCanonical(key).ShouldBeTrue(key);
        key.ShouldBe(@"C:\Notes\a.md");
    }

    [Fact]
    public void An_unusable_key_is_dropped_with_an_issue_rather_than_throwing()
    {
        using var dir = new TempDir();
        var store = StoreWith(dir, """
        {
          "version": 1,
          "notes": {
            "": {
              "x": 10, "y": 10, "w": 300, "h": 340,
              "color": "blue", "opacity": 1.0,
              "alwaysOnTop": false, "isOpen": true,
              "lastOpenedUtc": "2026-09-01T10:00:00Z"
            }
          }
        }
        """);

        var index = store.Load();

        index.Notes.ShouldBeEmpty();
        store.LastLoadIssues.ShouldNotBeEmpty();
    }

    [Fact]
    public void Two_keys_differing_only_in_case_keep_the_more_recently_opened_one()
    {
        using var dir = new TempDir();
        var store = StoreWith(dir, """
        {
          "version": 1,
          "notes": {
            "C:\\Notes\\a.md": {
              "x": 1, "y": 1, "w": 300, "h": 340,
              "color": "blue", "opacity": 1.0,
              "alwaysOnTop": false, "isOpen": true,
              "lastOpenedUtc": "2026-01-01T10:00:00Z"
            },
            "C:\\NOTES\\A.MD": {
              "x": 2, "y": 2, "w": 300, "h": 340,
              "color": "green", "opacity": 1.0,
              "alwaysOnTop": false, "isOpen": true,
              "lastOpenedUtc": "2026-09-01T10:00:00Z"
            }
          }
        }
        """);

        var index = store.Load();

        // Last-wins would depend on JSON document order, which nothing
        // guarantees. Newest-lastOpenedUtc-wins is deterministic and is also
        // the entry the user last actually used.
        index.Notes.Count.ShouldBe(1);
        index.Notes.Values.Single().Color.ShouldBe(NoteColor.Green);
        store.LastLoadIssues.ShouldNotBeEmpty();
    }

    [Fact]
    public void A_genuinely_unparseable_file_still_gets_backed_up()
    {
        using var dir = new TempDir();
        var store = StoreWith(dir, "{ this is not json");

        var index = store.Load();

        index.Notes.ShouldBeEmpty();
        store.LastCorruptBackupPath.ShouldNotBeNull();
    }

    [Fact]
    public void A_clean_file_reports_no_issues()
    {
        using var dir = new TempDir();
        var store = new NoteIndexStore(dir.File("notes.json"));

        var index = new NoteIndex();
        index.Notes[Path.Combine(dir.Path, "a.md")] = new NoteState(
            10, 10, 300, 340, null, NoteColor.Yellow, 1.0, false, true,
            new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc));
        store.Save(index);

        var reloaded = store.Load();

        reloaded.Notes.Count.ShouldBe(1);
        store.LastLoadIssues.ShouldBeEmpty();
        store.LastCorruptBackupPath.ShouldBeNull();
    }

    [Fact]
    public void Settings_load_reports_its_issues_too()
    {
        using var dir = new TempDir();
        var path = dir.WriteFile("settings.json", """
        { "notesRoot": "relative\\path", "defaultOpacity": 0.0, "theme": "Dark" }
        """);

        var store = new SettingsStore(path);
        var settings = store.Load();

        settings.NotesRoot.ShouldBe(AppSettings.DefaultNotesRoot);
        settings.DefaultOpacity.ShouldBe(StateValidator.MinOpacity);
        settings.Theme.ShouldBe(ThemePreference.Dark);
        store.LastLoadIssues.Count.ShouldBe(2);
    }
}
```

- [ ] **Step 6: Run them to verify they fail**

```bash
dotnet test --filter FullyQualifiedName~NoteIndexStoreValidationTests
```

Expected: FAIL to compile — `LastLoadIssues` does not exist on either store.

- [ ] **Step 7: Rewrite `NoteIndexStore.Load` for per-entry tolerance**

Replace the whole of `src/StickyMD.Core/Persistence/NoteIndexStore.cs`:

```csharp
using System.Text.Json;
using StickyMD.Core.Notes;

namespace StickyMD.Core.Persistence;

/// <summary>
/// Loads and saves notes.json. Load never throws: an unusable file is moved
/// aside and an empty index returned, and an unusable ENTRY costs that one
/// note's geometry rather than the whole file's.
/// </summary>
/// <remarks>
/// Entries are deserialised one at a time on purpose. A single unknown enum
/// string anywhere in the file used to throw JsonException from the top-level
/// Deserialize, which sent the entire index to notes.json.corrupt-N and lost
/// every note's position -- a hand edit to one note taking out the desktop.
///
/// Keys are re-keyed to NotePath canonical form, because a hand-written or
/// older file may hold a non-canonical path, and the window manager looks
/// entries up by what NoteRepository and NoteWatcher produce.
/// </remarks>
public sealed class NoteIndexStore(string filePath)
{
    public string FilePath { get; } = filePath;

    public string? LastCorruptBackupPath { get; private set; }

    /// <summary>
    /// Corrections made by the most recent <see cref="Load"/>. Empty after a
    /// clean load. The bootstrapper writes these to the diagnostics log; Plan C
    /// also surfaces them in a tray balloon.
    /// </summary>
    public IReadOnlyList<ValidationIssue> LastLoadIssues { get; private set; } = [];

    /// <param name="defaults">
    /// Supplies the fallback colour, size, and opacity for entries that carry
    /// unusable ones. Pass the loaded AppSettings, or <c>new()</c>.
    /// </param>
    /// <param name="nowUtc">
    /// Used to pull future lastOpenedUtc values back. Injected so the behaviour
    /// is testable without waiting for a clock.
    /// </param>
    public NoteIndex Load(AppSettings? defaults = null, DateTime? nowUtc = null)
    {
        LastCorruptBackupPath = null;
        var issues = new List<ValidationIssue>();
        LastLoadIssues = issues;

        var effectiveDefaults = defaults ?? new AppSettings();
        var now = nowUtc ?? DateTime.UtcNow;

        if (!File.Exists(FilePath)) return new NoteIndex();

        var raw = JsonFile.TryRead<RawIndex>(FilePath);

        if (raw is null)
        {
            LastCorruptBackupPath = JsonFile.BackupCorrupt(FilePath, "corrupt");
            return new NoteIndex();
        }

        if (raw.Version != NoteIndex.CurrentVersion)
        {
            // Not corrupt -- just a schema this build does not understand. Never
            // half-parse it, but do not label the user's file as damaged either.
            var direction = raw.Version > NoteIndex.CurrentVersion ? "newer" : "older";
            LastCorruptBackupPath =
                JsonFile.BackupCorrupt(FilePath, $"v{raw.Version}-{direction}");
            return new NoteIndex();
        }

        var index = new NoteIndex { Version = raw.Version };

        foreach (var entry in raw.Notes ?? [])
        {
            if (!NotePath.TryCanonical(entry.Key, out var key))
            {
                issues.Add(new ValidationIssue(
                    entry.Key ?? "(empty key)", "key",
                    "is not a usable path; the entry was dropped."));
                continue;
            }

            NoteState? state;

            try
            {
                state = entry.Value.Deserialize<NoteState>(JsonFile.Options);
            }
            catch (JsonException ex)
            {
                // Per-entry: an unknown enum string or a wrong-typed field here
                // loses ONE note's geometry, not the file.
                issues.Add(new ValidationIssue(key, "(entry)",
                    $"could not be read ({ex.Message.Split('.')[0]}); the entry was dropped."));
                continue;
            }

            if (state is null)
            {
                issues.Add(new ValidationIssue(key, "(entry)",
                    "was null; the entry was dropped."));
                continue;
            }

            var validated = StateValidator.ValidateNote(
                state, effectiveDefaults, key, now, issues);

            if (index.Notes.TryGetValue(key, out var existing))
            {
                // Two keys differing only in case. Keeping the LAST one seen
                // would depend on JSON document order, which nothing
                // guarantees; keeping the most recently opened is
                // deterministic and is the entry the user actually used last.
                issues.Add(new ValidationIssue(key, "key",
                    "appeared twice under different casing; kept the most recently opened."));

                if (existing.LastOpenedUtc >= validated.LastOpenedUtc) continue;
            }

            index.Notes[key] = validated;
        }

        return index;
    }

    public void Save(NoteIndex index) => JsonFile.Write(FilePath, index);

    /// <summary>
    /// The on-disk shape, with note entries left as raw JSON so each can be
    /// deserialised -- and fail -- on its own.
    /// </summary>
    private sealed class RawIndex
    {
        public int Version { get; set; } = NoteIndex.CurrentVersion;

        public Dictionary<string, JsonElement>? Notes { get; set; }
    }
}
```

**`JsonFile.Options` must become accessible.** It is currently `internal static readonly`, which is fine — `NoteIndexStore` is in the same assembly. No change needed; confirm the build agrees.

`Load()`'s new optional parameters keep every Plan A call site compiling.

- [ ] **Step 8: Add validation to `SettingsStore`**

In `src/StickyMD.Core/Persistence/SettingsStore.cs`, add the property and run the validator:

```csharp
    public IReadOnlyList<ValidationIssue> LastLoadIssues { get; private set; } = [];

    public AppSettings Load()
    {
        LastCorruptBackupPath = null;
        var issues = new List<ValidationIssue>();
        LastLoadIssues = issues;

        if (!File.Exists(FilePath)) return new AppSettings();

        var settings = JsonFile.TryRead<AppSettings>(FilePath);

        if (settings is null)
        {
            LastCorruptBackupPath = JsonFile.BackupCorrupt(FilePath, "corrupt");
            return new AppSettings();
        }

        // A JSON null overwrites a property initializer, so a hand-edited or
        // truncated file can leave a non-nullable string null at runtime. Fill
        // those in BEFORE validating, or the validator dereferences a null the
        // type system says cannot exist.
        var defaults = new AppSettings();

        var filled = settings with
        {
            NotesRoot = settings.NotesRoot ?? defaults.NotesRoot,
            NewNoteHotkey = settings.NewNoteHotkey ?? defaults.NewNoteHotkey,
            ShowHideHotkey = settings.ShowHideHotkey ?? defaults.ShowHideHotkey,
        };

        return StateValidator.ValidateSettings(filled, issues);
    }
```

- [ ] **Step 9: Write the failing tests for `DiagnosticsLog`**

`tests/StickyMD.Core.Tests/Diagnostics/DiagnosticsLogTests.cs`:

```csharp
using Shouldly;
using StickyMD.Core.Diagnostics;
using StickyMD.Core.Tests.TestSupport;

namespace StickyMD.Core.Tests.Diagnostics;

public class DiagnosticsLogTests
{
    [Fact]
    public void Write_creates_the_file_and_the_directory_under_it()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "nested", "diagnostics.log");

        DiagnosticsLog.Write(path, "hello");

        File.ReadAllText(path).ShouldContain("hello");
    }

    [Fact]
    public void Write_appends_rather_than_replacing()
    {
        using var dir = new TempDir();
        var path = dir.File("diagnostics.log");

        DiagnosticsLog.Write(path, "first");
        DiagnosticsLog.Write(path, "second");

        var text = File.ReadAllText(path);
        text.ShouldContain("first");
        text.ShouldContain("second");
    }

    [Fact]
    public void Each_line_carries_a_utc_timestamp()
    {
        using var dir = new TempDir();
        var path = dir.File("diagnostics.log");

        DiagnosticsLog.Write(path, "hello");

        File.ReadAllText(path).ShouldContain("Z ");
    }

    [Fact]
    public void WriteAll_writes_a_heading_and_one_line_per_item()
    {
        using var dir = new TempDir();
        var path = dir.File("diagnostics.log");

        DiagnosticsLog.WriteAll(path, "notes.json corrections", ["a", "b"]);

        var lines = File.ReadAllLines(path);
        lines.Length.ShouldBe(3);
        lines[0].ShouldContain("notes.json corrections");
        lines[1].ShouldContain("a");
        lines[2].ShouldContain("b");
    }

    [Fact]
    public void WriteAll_with_no_items_writes_nothing_at_all()
    {
        using var dir = new TempDir();
        var path = dir.File("diagnostics.log");

        DiagnosticsLog.WriteAll(path, "nothing to say", []);

        File.Exists(path).ShouldBeFalse();
    }

    [Fact]
    public void An_oversized_log_is_rotated_rather_than_growing_without_bound()
    {
        using var dir = new TempDir();
        var path = dir.File("diagnostics.log");
        File.WriteAllText(path, new string('x', DiagnosticsLog.MaxBytes + 1));

        DiagnosticsLog.Write(path, "after rotation");

        File.Exists(path + ".1").ShouldBeTrue();
        File.ReadAllText(path).ShouldContain("after rotation");
        File.ReadAllText(path).Length.ShouldBeLessThan(DiagnosticsLog.MaxBytes);
    }

    [Fact]
    public void A_write_to_an_impossible_path_is_swallowed()
    {
        // This is the LAST-RESORT reporting channel. If it throws, it takes out
        // whatever error path was trying to report through it.
        Should.NotThrow(() => DiagnosticsLog.Write("C:\\a\0b\\log.txt", "x"));
    }
}
```

- [ ] **Step 10: Run them to verify they fail, then write `DiagnosticsLog`**

```bash
dotnet test --filter FullyQualifiedName~DiagnosticsLogTests
```

Expected: FAIL to compile.

`src/StickyMD.Core/Diagnostics/DiagnosticsLog.cs`:

```csharp
using System.Text;

namespace StickyMD.Core.Diagnostics;

/// <summary>
/// Append-only text log for things the user should be able to find out about
/// after the fact: validation corrections, exhausted save retries, WebView2
/// process failures.
/// </summary>
/// <remarks>
/// This is the "never die silently" backstop. Plan B has no tray icon, so
/// there is nowhere to show a balloon; without this, a clamped note colour or
/// a dropped index entry would be a change with no explanation anywhere. Plan
/// C surfaces the same information interactively, and this file remains the
/// record.
///
/// EVERY operation is best-effort and swallows its exceptions. This is the
/// channel error paths report THROUGH -- if it can throw, it converts a
/// handled problem into an unhandled one.
/// </remarks>
public static class DiagnosticsLog
{
    /// <summary>
    /// Rotation threshold. Small on purpose: this log is read by a human
    /// diagnosing one incident, not mined.
    /// </summary>
    public const int MaxBytes = 256 * 1024;

    public static void Write(string path, string message)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            RotateIfOversized(path);

            File.AppendAllText(
                path,
                $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ} {message}{Environment.NewLine}",
                new UTF8Encoding(false));
        }
        catch (IOException) { /* last-resort channel: never throw */ }
        catch (UnauthorizedAccessException) { }
        catch (ArgumentException) { }
        catch (NotSupportedException) { }
    }

    /// <summary>
    /// Writes a heading followed by one line per item, or nothing at all when
    /// there are no items -- so a clean startup leaves no trace.
    /// </summary>
    public static void WriteAll(string path, string heading, IEnumerable<string> lines)
    {
        var items = lines as IReadOnlyCollection<string> ?? lines.ToList();
        if (items.Count == 0) return;

        Write(path, heading);
        foreach (var line in items) Write(path, "  " + line);
    }

    private static void RotateIfOversized(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= MaxBytes) return;

            var previous = path + ".1";
            if (File.Exists(previous)) File.Delete(previous);
            File.Move(path, previous);
        }
        catch (IOException) { /* keep appending to an oversized file rather than losing the write */ }
        catch (UnauthorizedAccessException) { }
    }
}
```

- [ ] **Step 11: Add the two new `AppPaths` entries**

In `src/StickyMD.Core/Persistence/AppPaths.cs`:

```csharp
    public static string DiagnosticsFile { get; } = Path.Combine(AppData, "diagnostics.log");

    /// <summary>
    /// The WebView2 user-data folder, shared by every note. Under LOCALAPPDATA
    /// alongside the rest of StickyMD's state, and never inside the notes root
    /// -- nothing app-owned goes there.
    /// </summary>
    public static string WebViewUserDataDir { get; } = Path.Combine(AppData, "WebView2");
```

- [ ] **Step 12: Run everything**

```bash
dotnet test
```

Expected: `failed: 0`. Record the total.

**Plan A's `NoteIndexStoreTests` and `SettingsStoreTests` must still pass.** Two of them are likely to need attention, and both are legitimate changes rather than broken expectations — check them specifically:

- A test asserting that two case-differing keys resolve last-wins now resolves newest-`lastOpenedUtc`-wins. Update the test and its name to state the new rule.
- A test asserting that `SettingsStore.Load` returns a hand-written value verbatim may now see it clamped. If the value was legal, the validator has a bug; if it was not, the test was asserting the finding.

- [ ] **Step 13: Commit**

Hand these to the user:

```bash
git add src/StickyMD.Core tests/StickyMD.Core.Tests
git commit -m "Validate persisted values on load instead of trusting their shape

Deserialization checked shape, not values. {\"color\": 99} loaded cleanly
and then threw ArgumentOutOfRangeException from NotePalette.Get -- a
startup crash pointing at the theming code, after a load that reported
success. The same hole let through zero and NaN opacity (an invisible,
unrecoverable note; Window.Opacity = NaN throws at assignment), sizes of
zero, coordinates that overflow WindowPlacement's int arithmetic, and a
lastOpenedUtc in year 9999 that would pin a note to the top of Recent
Notes forever.

StateValidator clamps or defaults each of those and REPORTS every
correction. Reporting is the part that matters: a note that silently
changes colour or moves on its own is a bug report nobody can act on, and
quiet recovery is still dying silently.

notes.json entries now deserialise one at a time. An unknown enum string
anywhere in the file used to throw from the top-level Deserialize, send
the whole index to notes.json.corrupt-N, and lose every note's position
-- one hand edit taking out the desktop. One bad value now costs one
note's geometry.

Duplicate keys differing only in case keep the most recently opened
entry rather than the last one parsed, because JSON document order
guarantees nothing.

DiagnosticsLog gives the report somewhere to go. Plan B has no tray icon,
so without it these corrections would happen with no explanation
anywhere. Every operation in it is best-effort and swallows its
exceptions -- it is the channel other error paths report through, so it
must not be able to convert a handled problem into an unhandled one."
```

---

## Task 4: `HtmlDocumentBuilder` — the shell, the CSP, and the bridge script

Core code, written here because Plan B is where its only consumer appears. This is the static page each note's WebView navigates to **once**; content arrives afterwards by `postMessage`.

**Files:**

- Create: `src/StickyMD.Core/Markdown/HtmlDocumentBuilder.cs`
- Test: `tests/StickyMD.Core.Tests/Markdown/HtmlDocumentBuilderTests.cs`
- Modify: `src/StickyMD.Core/Markdown/MarkdownRenderer.cs` (count blocked remote images)
- Test: `tests/StickyMD.Core.Tests/Markdown/MarkdownRendererTests.cs` (add cases)

**Interfaces:**

- Consumes: `NotePalette.ToCssVariables` (Task from Plan A), `NoteTheme`, `MarkdownRenderer.BlockedScheme`.
- Produces:
  - `sealed record HtmlShellOptions(NoteTheme Theme, bool AllowRemoteImages, string VirtualHost = "note.local", string FontFamily = "…", int FontSizePx = 14)`
  - `static string HtmlDocumentBuilder.BuildShell(HtmlShellOptions options)`
  - `static string HtmlDocumentBuilder.BuildCsp(bool allowRemoteImages, string virtualHost)`
  - `static string HtmlDocumentBuilder.BuildStyleBlock(NoteTheme theme, string nonce, string fontFamily, int fontSizePx)`
  - `MarkdownRenderer.RenderResult.BlockedRemoteImages` (int)

Task 8 (`WebViewHost`) calls `BuildShell` and `NavigateToString`. Task 11 relies on the bridge script's message names. Task 12 reads `BlockedRemoteImages` to decide whether to show the remote-images bar.

### Two design decisions this task settles

**1. The shell is delivered by `NavigateToString`, not by a file.**

The spec says "navigate once to a static shell". It must not be a file in the notes root — nothing app-owned goes there. It could be a second virtual-host mapping onto an app asset folder, but that is a second mapping, a second origin, and an installed-file dependency for no gain. `NavigateToString` puts the shell in memory. Its document gets an opaque origin, which is fine: image loads from `https://note.local/...` are ordinary no-CORS subresource requests, and the renderer already rewrites every image `src` to an absolute URL, so the missing `baseURI` costs nothing.

**2. A CSP change requires re-navigating the shell; a content change does not.**

The spec says the CSP is built per render from the effective remote-image setting, and also that the shell is navigated once. Both are right, but they interact: a `<meta http-equiv="Content-Security-Policy">` cannot be changed after parse. So:

> Content updates → `PostWebMessageAsJson({type:"render", …})`. No navigation, no flash, scroll preserved.
>
> The remote-image setting changing → rebuild the shell with the new CSP and `NavigateToString` again, then render. This happens when the user clicks "Load remote images" or flips the global setting — rare, deliberate, and a one-frame flash there is acceptable.

Task 8 implements exactly that split, and `WebViewHost` records the CSP it last navigated with so it knows when a re-navigation is required.

- [ ] **Step 1: Write the failing tests**

`tests/StickyMD.Core.Tests/Markdown/HtmlDocumentBuilderTests.cs`:

```csharp
using System.Text.RegularExpressions;
using Shouldly;
using StickyMD.Core.Markdown;
using StickyMD.Core.Theming;

namespace StickyMD.Core.Tests.Markdown;

public class HtmlDocumentBuilderTests
{
    private static readonly NoteTheme Theme =
        NotePalette.Get(NoteColor.Yellow, ThemeMode.Light);

    private static HtmlShellOptions Options(bool allowRemote = false)
        => new(Theme, allowRemote);

    [Fact]
    public void The_default_csp_allows_only_the_virtual_host_and_data_images()
    {
        var csp = HtmlDocumentBuilder.BuildCsp(allowRemoteImages: false, "note.local");

        csp.ShouldContain("img-src https://note.local data:");
        csp.ShouldNotContain("https:;");
        csp.ShouldContain("default-src 'none'");
    }

    [Fact]
    public void Enabling_remote_images_adds_https_to_img_src_only()
    {
        var csp = HtmlDocumentBuilder.BuildCsp(allowRemoteImages: true, "note.local");

        csp.ShouldContain("img-src https://note.local data: https:");
        csp.ShouldContain("default-src 'none'");
        csp.ShouldContain("connect-src 'none'");
    }

    [Fact]
    public void The_csp_forbids_everything_the_note_has_no_use_for()
    {
        var csp = HtmlDocumentBuilder.BuildCsp(allowRemoteImages: true, "note.local");

        // A note renders text and images. It never fetches, submits, frames, or
        // resolves a relative URL -- and a synced note is exactly the place a
        // hostile payload would try to.
        csp.ShouldContain("connect-src 'none'");
        csp.ShouldContain("form-action 'none'");
        csp.ShouldContain("frame-ancestors 'none'");
        csp.ShouldContain("base-uri 'none'");
        csp.ShouldContain("object-src 'none'");
    }

    [Fact]
    public void The_shell_embeds_the_csp_as_a_meta_tag()
    {
        var shell = HtmlDocumentBuilder.BuildShell(Options());

        shell.ShouldContain("http-equiv=\"Content-Security-Policy\"");
        shell.ShouldContain(HtmlDocumentBuilder.BuildCsp(false, "note.local"));
    }

    [Fact]
    public void The_shell_carries_every_note_css_variable()
    {
        var shell = HtmlDocumentBuilder.BuildShell(Options());

        foreach (var (name, value) in NotePalette.ToCssVariables(Theme))
            shell.ShouldContain($"{name}: {value}");
    }

    [Fact]
    public void The_shell_uses_the_palette_rather_than_a_hardcoded_colour()
    {
        var yellow = HtmlDocumentBuilder.BuildShell(Options());
        var charcoal = HtmlDocumentBuilder.BuildShell(
            new HtmlShellOptions(
                NotePalette.Get(NoteColor.Charcoal, ThemeMode.Dark), false));

        yellow.ShouldNotBe(charcoal);
        charcoal.ShouldContain(
            NotePalette.Get(NoteColor.Charcoal, ThemeMode.Dark).ContentBg);
    }

    [Fact]
    public void Script_and_style_run_under_a_nonce_rather_than_unsafe_inline()
    {
        var shell = HtmlDocumentBuilder.BuildShell(Options());

        shell.ShouldNotContain("'unsafe-inline'");

        var nonce = Regex.Match(shell, "nonce-([A-Za-z0-9+/=]+)").Groups[1].Value;
        nonce.ShouldNotBeNullOrEmpty();

        shell.ShouldContain($"<style nonce=\"{nonce}\">");
        shell.ShouldContain($"<script nonce=\"{nonce}\">");
    }

    [Fact]
    public void Every_shell_gets_a_fresh_nonce()
    {
        var a = Regex.Match(HtmlDocumentBuilder.BuildShell(Options()), "nonce-([^']+)").Groups[1].Value;
        var b = Regex.Match(HtmlDocumentBuilder.BuildShell(Options()), "nonce-([^']+)").Groups[1].Value;

        a.ShouldNotBe(b);
    }

    [Fact]
    public void The_shell_declares_the_content_container_the_bridge_writes_into()
    {
        HtmlDocumentBuilder.BuildShell(Options())
            .ShouldContain("id=\"content\"");
    }

    [Fact]
    public void The_bridge_script_handles_the_render_message()
    {
        var shell = HtmlDocumentBuilder.BuildShell(Options());

        shell.ShouldContain("'render'");
        shell.ShouldContain("scrollTop");
        shell.ShouldContain("innerHTML");
    }

    [Fact]
    public void The_bridge_script_posts_the_four_outbound_message_types()
    {
        var shell = HtmlDocumentBuilder.BuildShell(Options());

        shell.ShouldContain("'ready'");
        shell.ShouldContain("'toggleTask'");
        shell.ShouldContain("'link'");
        shell.ShouldContain("'requestEdit'");
    }

    [Fact]
    public void The_bridge_script_sends_the_render_token_back_with_a_toggle()
    {
        // The token is what makes a stale click safe to refuse rather than
        // apply to the wrong task.
        var shell = HtmlDocumentBuilder.BuildShell(Options());

        shell.ShouldContain("spanStart");
        shell.ShouldContain("spanEnd");
        shell.ShouldContain("token");
    }

    [Fact]
    public void The_bridge_script_cancels_the_checkbox_default_action()
    {
        // The checkbox must not appear to toggle before the host has decided.
        // A refused click that already flipped visually is a checkbox that
        // lies about the file.
        HtmlDocumentBuilder.BuildShell(Options())
            .ShouldContain("preventDefault");
    }

    [Fact]
    public void The_bridge_script_sends_the_raw_href_not_the_resolved_one()
    {
        // getAttribute('href'), never a.href: the shell has an opaque origin,
        // so a.href resolves "notes.md" against about:blank and the host would
        // receive something it cannot map back to a file.
        HtmlDocumentBuilder.BuildShell(Options())
            .ShouldContain("getAttribute('href')");
    }

    [Fact]
    public void The_bridge_script_handles_anchors_itself()
    {
        // Fragment navigation inside an opaque-origin document does not
        // reliably reach NavigationStarting, so in-note anchors are scrolled
        // in the page rather than round-tripped through the host.
        HtmlDocumentBuilder.BuildShell(Options())
            .ShouldContain("scrollIntoView");
    }

    [Fact]
    public void Blocked_images_are_replaced_with_a_placeholder_by_the_script()
    {
        var shell = HtmlDocumentBuilder.BuildShell(Options());

        shell.ShouldContain(MarkdownRenderer.BlockedScheme);
        shell.ShouldContain("blocked-image");
    }

    [Fact]
    public void The_shell_is_small_enough_to_navigate_to_as_a_string()
    {
        // NavigateToString has a 2MB limit. This is a guard against someone
        // pasting a stylesheet in here later.
        HtmlDocumentBuilder.BuildShell(Options()).Length.ShouldBeLessThan(64 * 1024);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

```bash
dotnet test --filter FullyQualifiedName~HtmlDocumentBuilderTests
```

Expected: FAIL to compile — `HtmlDocumentBuilder` does not exist.

- [ ] **Step 3: Write `HtmlDocumentBuilder`**

`src/StickyMD.Core/Markdown/HtmlDocumentBuilder.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using StickyMD.Core.Theming;

namespace StickyMD.Core.Markdown;

/// <param name="Theme">
/// From <see cref="NotePalette"/>. The same instance the WPF chrome uses, so a
/// note's window and its content cannot disagree about what "yellow" is.
/// </param>
/// <param name="AllowRemoteImages">
/// The EFFECTIVE setting -- the global preference OR this note's per-session
/// opt-in. It changes the CSP, which means changing it requires a fresh
/// navigation; see <see cref="HtmlDocumentBuilder"/>.
/// </param>
public sealed record HtmlShellOptions(
    NoteTheme Theme,
    bool AllowRemoteImages,
    string VirtualHost = "note.local",
    string FontFamily = "'Segoe UI Variable Text', 'Segoe UI', system-ui, sans-serif",
    int FontSizePx = 14);

/// <summary>
/// Builds the static page each note's WebView2 navigates to exactly once.
/// Rendered Markdown arrives afterwards by postMessage.
/// </summary>
/// <remarks>
/// DELIVERY. The shell goes in via NavigateToString rather than a file. A file
/// in the notes root is forbidden -- nothing app-owned goes there -- and a
/// second virtual-host mapping onto an app asset folder would buy a second
/// origin and an installed-file dependency for nothing. The resulting document
/// has an opaque origin, which costs nothing here: images are absolute
/// https://note.local/... URLs by the time the renderer is done, so the missing
/// baseURI is never consulted.
///
/// CSP LIFETIME. A meta-tag CSP is fixed at parse time. So content updates go
/// by postMessage with no navigation, but a change to the remote-image setting
/// requires rebuilding this shell and navigating again. That is the reading
/// that satisfies both "the CSP is built per render from the effective
/// remote-image setting" and "navigate once to a static shell": the CSP is
/// per-SHELL, and a new CSP means a new shell. Enabling remote images is a
/// deliberate click, so one frame of flash there is acceptable; an external
/// edit arriving every 200ms is not, which is why content never re-navigates.
///
/// NONCE, NOT UNSAFE-INLINE. The shell's own style and script are the only
/// inline code the page ever runs, so they get a per-shell nonce. Markdig runs
/// with DisableHtml(), so nothing the user typed can reach the DOM as markup --
/// but 'unsafe-inline' would remove the guarantee that stays true if that ever
/// changes.
/// </remarks>
public static class HtmlDocumentBuilder
{
    public static string BuildCsp(bool allowRemoteImages, string virtualHost)
    {
        // Blocking remote images by default matters because these files sync. A
        // shared note containing ![](https://example.com/tracker?id=123) must
        // not make a network request just because StickyMD rendered it.
        var img = $"img-src https://{virtualHost} data:";
        if (allowRemoteImages) img += " https:";

        return string.Join("; ",
            "default-src 'none'",
            img,
            "style-src 'nonce-{0}'",
            "script-src 'nonce-{0}'",
            "font-src 'none'",
            "connect-src 'none'",
            "object-src 'none'",
            "form-action 'none'",
            "frame-ancestors 'none'",
            "base-uri 'none'");
    }

    public static string BuildStyleBlock(
        NoteTheme theme, string nonce, string fontFamily, int fontSizePx)
    {
        var variables = new StringBuilder();
        foreach (var (name, value) in NotePalette.ToCssVariables(theme))
            variables.Append("      ").Append(name).Append(": ").Append(value).AppendLine(";");

        return $$"""
            <style nonce="{{nonce}}">
              :root {
            {{variables.ToString().TrimEnd()}}
              }
              html, body {
                margin: 0;
                padding: 0;
                background: var(--note-content-bg);
                color: var(--note-content-fg);
                font-family: {{fontFamily}};
                font-size: {{fontSizePx}}px;
                line-height: 1.45;
                overflow-wrap: break-word;
              }
              #content { padding: 8px 12px 16px 12px; }
              #content > :first-child { margin-top: 0; }
              h1, h2, h3, h4, h5, h6 { margin: 0.8em 0 0.35em; line-height: 1.25; }
              h1 { font-size: 1.35em; }
              h2 { font-size: 1.2em; }
              h3 { font-size: 1.08em; }
              p, ul, ol, blockquote, pre, table { margin: 0.5em 0; }
              a { color: var(--note-accent); }
              hr { border: none; border-top: 1px solid var(--note-muted); }
              blockquote {
                margin-left: 0;
                padding-left: 10px;
                border-left: 3px solid var(--note-muted);
                color: var(--note-muted);
              }
              code, pre {
                font-family: 'Cascadia Mono', Consolas, monospace;
                font-size: 0.92em;
                background: var(--note-code-bg);
              }
              code { padding: 0.1em 0.3em; border-radius: 3px; }
              pre { padding: 8px 10px; border-radius: 4px; overflow-x: auto; }
              pre code { background: none; padding: 0; }
              table { border-collapse: collapse; }
              th, td { border: 1px solid var(--note-muted); padding: 3px 7px; }
              ul, ol { padding-left: 1.4em; }
              li { margin: 0.15em 0; }
              /* Task lists read as checklists, not as bulleted checkboxes. */
              li:has(> input[type="checkbox"]) { list-style: none; margin-left: -1.2em; }
              input[type="checkbox"] { accent-color: var(--note-accent); margin-right: 0.4em; }
              img { max-width: 100%; height: auto; }
              .blocked-image {
                display: inline-block;
                padding: 2px 8px;
                border: 1px dashed var(--note-muted);
                border-radius: 4px;
                color: var(--note-muted);
                font-size: 0.85em;
                cursor: default;
              }
              ::selection { background: var(--note-accent); color: var(--note-content-bg); }
            </style>
            """;
    }

    public static string BuildShell(HtmlShellOptions options)
    {
        // A fresh nonce per shell. Reusing one across notes would make the
        // value predictable, which is the whole point of a nonce.
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

        var csp = string.Format(
            BuildCsp(options.AllowRemoteImages, options.VirtualHost), nonce);

        var style = BuildStyleBlock(
            options.Theme, nonce, options.FontFamily, options.FontSizePx);

        var script = BuildBridgeScript(nonce);

        return $"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="{csp}">
            <title>note</title>
            {style}
            </head>
            <body>
            <div id="content"></div>
            {script}
            </body>
            </html>
            """;
    }

    /// <summary>
    /// The host-page contract. Inbound: <c>render</c>. Outbound: <c>ready</c>,
    /// <c>toggleTask</c>, <c>link</c>, <c>requestEdit</c>.
    /// </summary>
    private static string BuildBridgeScript(string nonce) => $$"""
        <script nonce="{{nonce}}">
        (function () {
          var content = document.getElementById('content');
          var token = '';

          function post(message) {
            window.chrome.webview.postMessage(message);
          }

          // An image the resource policy refused. Its src carries an unknown
          // scheme, so it never reached the network -- replace it with a
          // placeholder so the note does not show a broken-image icon.
          function replaceBlockedImages() {
            var blocked = content.querySelectorAll('img[src^="{{MarkdownRenderer.BlockedScheme}}"]');
            for (var i = 0; i < blocked.length; i++) {
              var img = blocked[i];
              var span = document.createElement('span');
              span.className = 'blocked-image';
              span.textContent = 'image blocked';
              span.title = img.getAttribute('src').substring({{MarkdownRenderer.BlockedScheme.Length}});
              if (img.parentNode) img.parentNode.replaceChild(span, img);
            }
          }

          window.chrome.webview.addEventListener('message', function (event) {
            var message = event.data;
            if (!message || message.type !== 'render') return;

            // Preserve scroll across the swap. Re-navigating per update would
            // reset it, which is most noticeable when a long note reloads from
            // an external edit.
            var scroll = document.documentElement.scrollTop || document.body.scrollTop || 0;

            token = message.token;
            content.innerHTML = message.html;
            replaceBlockedImages();

            document.documentElement.scrollTop = scroll;
            document.body.scrollTop = scroll;
          });

          content.addEventListener('click', function (event) {
            var target = event.target;

            if (target && target.matches('input[type="checkbox"]')) {
              // Cancel the default toggle. The visual state must come from the
              // re-render the host sends back, or a refused click leaves a
              // checkbox showing something the file does not say.
              event.preventDefault();
              post({
                type: 'toggleTask',
                spanStart: parseInt(target.getAttribute('data-span-start'), 10),
                spanEnd: parseInt(target.getAttribute('data-span-end'), 10),
                token: token
              });
              return;
            }

            var anchor = target && target.closest ? target.closest('a[href]') : null;
            if (!anchor) return;

            event.preventDefault();
            var href = anchor.getAttribute('href');

            // In-note anchors are handled here. Fragment navigation inside an
            // opaque-origin document does not reliably raise NavigationStarting,
            // so routing it through the host would sometimes do nothing at all.
            if (href && href.charAt(0) === '#') {
              var anchorName = decodeURIComponent(href.substring(1));
              var destination = document.getElementById(anchorName)
                || content.querySelector('[name="' + CSS.escape(anchorName) + '"]');
              if (destination) destination.scrollIntoView();
              return;
            }

            // The RAW attribute, never anchor.href. With an opaque origin,
            // anchor.href resolves "other.md" against about:blank and the host
            // receives something it cannot map back to a file.
            post({ type: 'link', href: href });
          });

          document.addEventListener('dblclick', function (event) {
            // Only empty space enters edit mode. Double-clicking rendered text,
            // a link, code, or a checkbox keeps native word selection.
            if (event.target !== document.body && event.target !== content) return;
            post({ type: 'requestEdit' });
          });

          post({ type: 'ready' });
        })();
        </script>
        """;
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test --filter FullyQualifiedName~HtmlDocumentBuilderTests
```

Expected: PASS, 17 tests. If `Script_and_style_run_under_a_nonce…` fails on the regex, check that `BuildCsp`'s `{0}` placeholders survived `string.Format` — the raw string in `BuildStyleBlock` uses `{{`/`}}` escaping and is easy to get wrong.

- [ ] **Step 5: Add the blocked-image count to `RenderResult`**

Task 12 needs to know whether a rendered note _had_ any remote image blocked, so it can offer the "Load remote images" bar only when there is something to load. Searching the HTML for the scheme would work but ties a UI decision to a string match.

In `src/StickyMD.Core/Markdown/MarkdownRenderer.cs`:

```csharp
/// <param name="Token">
/// SHA-256 of the markdown at render time. A checkbox click carries it back so
/// a stale click is rejected rather than misapplied.
/// </param>
/// <param name="BlockedRemoteImages">
/// How many remote images the resource policy refused. Drives the per-note
/// "Load remote images" bar, which must not appear for a note that has none.
/// </param>
public sealed record RenderResult(string Html, string Token, int BlockedRemoteImages = 0);
```

`Render` and `RewriteImageUrls` count them:

```csharp
    public RenderResult Render(string markdown, RenderOptions options)
    {
        markdown ??= string.Empty;

        var document = Markdig.Markdown.Parse(markdown, Pipeline);
        var blockedRemote = RewriteImageUrls(document, options);

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        renderer.ObjectRenderers.Replace<HtmlTaskListRenderer>(new SourceSpanTaskListRenderer());
        renderer.Render(document);
        writer.Flush();

        return new RenderResult(writer.ToString(), ComputeToken(markdown), blockedRemote);
    }

    /// <returns>How many REMOTE images were blocked. A traversal-blocked local
    /// image is not counted: enabling remote images would not make it load, so
    /// offering that bar for one would be a lie.</returns>
    private static int RewriteImageUrls(MarkdownDocument document, RenderOptions options)
    {
        var blockedRemote = 0;

        foreach (var link in document.Descendants<LinkInline>())
        {
            if (!link.IsImage) continue;

            var original = link.Url;
            link.Url = ResolveImageUrl(original, options);

            if (link.Url.StartsWith(BlockedScheme, StringComparison.Ordinal)
                && IsRemote(original))
            {
                blockedRemote++;
            }
        }

        return blockedRemote;
    }

    private static bool IsRemote(string? url)
    {
        var trimmed = url?.Trim() ?? string.Empty;
        return trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }
```

- [ ] **Step 6: Add the tests for the count**

Append to `tests/StickyMD.Core.Tests/Markdown/MarkdownRendererTests.cs`:

```csharp
    [Fact]
    public void A_clean_note_reports_no_blocked_remote_images()
    {
        var result = new MarkdownRenderer().Render(
            "# hi\n\n![local](pic.png)", new RenderOptions(AllowRemoteImages: false));

        result.BlockedRemoteImages.ShouldBe(0);
    }

    [Fact]
    public void Blocked_remote_images_are_counted()
    {
        var result = new MarkdownRenderer().Render(
            "![a](https://example.com/a.png)\n\n![b](http://example.com/b.png)",
            new RenderOptions(AllowRemoteImages: false));

        result.BlockedRemoteImages.ShouldBe(2);
    }

    [Fact]
    public void Allowed_remote_images_are_not_counted_as_blocked()
    {
        var result = new MarkdownRenderer().Render(
            "![a](https://example.com/a.png)",
            new RenderOptions(AllowRemoteImages: true));

        result.BlockedRemoteImages.ShouldBe(0);
    }

    [Fact]
    public void A_traversal_blocked_local_image_is_not_counted_as_a_remote_one()
    {
        // Enabling remote images would not make ../secret.png load, so
        // offering the "Load remote images" bar for it would be a lie.
        var result = new MarkdownRenderer().Render(
            "![x](../outside/secret.png)",
            new RenderOptions(AllowRemoteImages: false));

        result.Html.ShouldContain(MarkdownRenderer.BlockedScheme);
        result.BlockedRemoteImages.ShouldBe(0);
    }
```

- [ ] **Step 7: Run the full suite**

```bash
dotnet test
```

Expected: `failed: 0`. Plan A's `MarkdownRendererTests` must all still pass — the third positional record parameter has a default, so existing construction and deconstruction still compile.

- [ ] **Step 8: Commit**

Hand these to the user:

```bash
git add src/StickyMD.Core/Markdown tests/StickyMD.Core.Tests/Markdown
git commit -m "Add HtmlDocumentBuilder: the note shell, its CSP, and the bridge

The shell is delivered with NavigateToString rather than as a file. A file
in the notes root is forbidden -- nothing app-owned goes there -- and a
second virtual-host mapping onto an asset folder would buy a second origin
and an installed-file dependency for nothing. The resulting opaque origin
costs nothing, because the renderer has already rewritten every image src
to an absolute https://note.local/... URL, so baseURI is never consulted.

The spec asks for a CSP built per render AND for navigating once. Both are
right and they interact, because a meta-tag CSP is fixed at parse time.
The resolution: the CSP is per-SHELL. Content updates go by postMessage
with no navigation, so scroll survives and an external edit arriving every
200ms does not flash. Changing the remote-image setting rebuilds the shell
and navigates again -- a deliberate click, where one frame is acceptable.

Inline style and script run under a per-shell nonce rather than
'unsafe-inline'. Markdig runs with DisableHtml() so nothing the user typed
can reach the DOM as markup today; the nonce is what keeps that true if
that ever changes.

Two things in the bridge script look like style choices and are not. It
reads getAttribute('href') rather than anchor.href, because with an opaque
origin anchor.href resolves 'other.md' against about:blank and the host
receives something it cannot map back to a file. And it calls
preventDefault on a checkbox click, because the visual state has to come
from the re-render the host sends back -- a refused click that already
flipped would leave a checkbox contradicting the file.

RenderResult now counts blocked REMOTE images, so the 'Load remote images'
bar can be offered only where enabling it would actually change something.
A traversal-blocked local image is deliberately not counted."
```

---

## Task 5: `NavigationPolicy` — the §6 allowlist as a pure function

Core code, pure, no `Process.Start` — this decides, and the App acts. The spec's table becomes a function so every row is a test.

**Files:**

- Create: `src/StickyMD.Core/Markdown/NavigationPolicy.cs`
- Test: `tests/StickyMD.Core.Tests/Markdown/NavigationPolicyTests.cs`

**Interfaces:**

- Consumes: `NotePath` (Task 2).
- Produces:
  - `enum NavigationAction { Block, OpenInBrowser, OpenNote, AllowShellLoad }`
  - `sealed record NavigationDecision(NavigationAction Action, string? Target, string Reason)`
  - `static NavigationDecision NavigationPolicy.DecideLinkClick(string? href, string noteDirectory)`
  - `static NavigationDecision NavigationPolicy.DecideNavigation(string? uri, bool shellLoaded)`

Task 8 wires `DecideNavigation` into `NavigationStarting`. Task 11 wires `DecideLinkClick` into the `link` message.

### Why there are two decision functions

The spec says "all navigation is intercepted before the WebView2 acts on it" and "`NavigationStarting` and `NewWindowRequested` cancel unconditionally". Taken literally, that cancels the shell's own `NavigateToString` load and the note renders nothing — a trap worth naming, because the symptom is a permanently blank note with no error.

So the two paths are separated:

| Path                                             | Function           | Role                                                                      |
| ------------------------------------------------ | ------------------ | ------------------------------------------------------------------------- |
| A clicked link, arriving as a `link` web message | `DecideLinkClick`  | The real allowlist. Sees the **raw** `href` and the note's directory      |
| `NavigationStarting` / `NewWindowRequested`      | `DecideNavigation` | Defensive backstop. Allows the initial shell load, blocks everything else |

The primary channel is the web message, because the shell's opaque origin makes `NavigationStarting`'s resolved URI useless for relative links and unreliable for fragments. `NavigationStarting` remains as the thing that catches anything the script did not — a `<meta refresh>`, a scheme nobody anticipated — and it never allows.

- [ ] **Step 1: Write the failing tests**

`tests/StickyMD.Core.Tests/Markdown/NavigationPolicyTests.cs`:

```csharp
using Shouldly;
using StickyMD.Core.Markdown;

namespace StickyMD.Core.Tests.Markdown;

public class NavigationPolicyTests
{
    private const string NoteDir = @"C:\Notes";

    private static NavigationDecision Click(string? href)
        => NavigationPolicy.DecideLinkClick(href, NoteDir);

    [Fact]
    public void An_https_link_opens_in_the_default_browser()
    {
        var decision = Click("https://example.com/page");

        decision.Action.ShouldBe(NavigationAction.OpenInBrowser);
        decision.Target.ShouldBe("https://example.com/page");
    }

    [Fact]
    public void An_http_link_opens_in_the_default_browser()
        => Click("http://example.com").Action.ShouldBe(NavigationAction.OpenInBrowser);

    [Fact]
    public void A_relative_markdown_link_opens_as_a_note()
    {
        var decision = Click("other.md");

        decision.Action.ShouldBe(NavigationAction.OpenNote);
        decision.Target.ShouldBe(@"C:\Notes\other.md");
    }

    [Fact]
    public void A_relative_markdown_link_with_a_leading_dot_slash_opens_as_a_note()
        => Click("./other.md").Target.ShouldBe(@"C:\Notes\other.md");

    [Fact]
    public void A_percent_encoded_markdown_link_is_decoded_before_resolving()
    {
        Click("my%20note.md").Target.ShouldBe(@"C:\Notes\my note.md");
    }

    [Fact]
    public void A_markdown_link_with_a_fragment_opens_the_file_and_drops_the_fragment()
    {
        // v1 has no scroll-to-heading-in-another-note. Opening the file is the
        // useful part; silently doing nothing would not be.
        Click("other.md#section").Target.ShouldBe(@"C:\Notes\other.md");
    }

    [Fact]
    public void A_relative_link_that_is_not_markdown_is_blocked()
    {
        // Handing an arbitrary relative path to the shell would make any file
        // in the note directory launchable from a synced note.
        Click("script.ps1").Action.ShouldBe(NavigationAction.Block);
        Click("thing.pdf").Action.ShouldBe(NavigationAction.Block);
    }

    [Theory]
    [InlineData("../outside/x.md")]
    [InlineData("..\\outside\\x.md")]
    [InlineData("sub/../../x.md")]
    [InlineData("%2e%2e%2fx.md")]
    [InlineData("/absolute.md")]
    [InlineData("\\absolute.md")]
    [InlineData("C:\\elsewhere\\x.md")]
    public void A_link_escaping_the_note_directory_is_blocked(string href)
    {
        // %2e%2e%2f is the reason decoding happens BEFORE the traversal test --
        // a literal ".." check sails straight past it.
        Click(href).Action.ShouldBe(NavigationAction.Block);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JavaScript:alert(1)")]
    [InlineData("vbscript:x")]
    [InlineData("data:text/html,<script>x</script>")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("ms-msdt:/id")]
    [InlineData("search-ms:query=x")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("ftp://example.com/x")]
    public void Every_other_scheme_is_blocked(string href)
    {
        // Nothing is allowed by default, so a link carrying an unexpected or
        // hostile scheme is rejected rather than executed. mailto and ftp are
        // blocked too: harmless-looking, but neither is in the spec's table,
        // and an allowlist that grows by sympathy is not an allowlist.
        Click(href).Action.ShouldBe(NavigationAction.Block);
    }

    [Fact]
    public void The_virtual_host_is_blocked_as_a_navigation_target()
    {
        // note.local exists for subresource loads. A NAVIGATION to it would
        // leave the shell and its CSP behind.
        Click("https://note.local/x.md").Action.ShouldBe(NavigationAction.Block);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_href_is_blocked_without_throwing(string? href)
        => Click(href).Action.ShouldBe(NavigationAction.Block);

    [Fact]
    public void A_bare_fragment_is_blocked_here_because_the_page_handles_it()
    {
        // The bridge script scrolls in-note anchors itself and never posts
        // them, so one reaching the host means something unexpected happened.
        Click("#section").Action.ShouldBe(NavigationAction.Block);
    }

    [Fact]
    public void Every_decision_carries_a_reason()
    {
        Click("javascript:alert(1)").Reason.ShouldNotBeNullOrWhiteSpace();
        Click("https://example.com").Reason.ShouldNotBeNullOrWhiteSpace();
        Click("other.md").Reason.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void The_returned_note_path_is_canonical()
    {
        var target = Click("sub/../other.md").Target;

        target.ShouldBeNull("that link escapes and re-enters, and is blocked");
    }

    [Fact]
    public void The_initial_shell_load_is_allowed()
    {
        // Cancelling this one is how a note ends up permanently blank with no
        // error anywhere.
        NavigationPolicy.DecideNavigation("about:blank", shellLoaded: false)
            .Action.ShouldBe(NavigationAction.AllowShellLoad);
    }

    [Fact]
    public void A_second_about_blank_navigation_after_the_shell_loaded_is_blocked()
    {
        NavigationPolicy.DecideNavigation("about:blank", shellLoaded: true)
            .Action.ShouldBe(NavigationAction.Block);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("file:///C:/x")]
    [InlineData("javascript:x")]
    [InlineData(null)]
    public void Every_other_navigation_is_blocked_in_both_states(string? uri)
    {
        NavigationPolicy.DecideNavigation(uri, shellLoaded: false)
            .Action.ShouldBe(NavigationAction.Block);
        NavigationPolicy.DecideNavigation(uri, shellLoaded: true)
            .Action.ShouldBe(NavigationAction.Block);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

```bash
dotnet test --filter FullyQualifiedName~NavigationPolicyTests
```

Expected: FAIL to compile.

- [ ] **Step 3: Write `NavigationPolicy`**

`src/StickyMD.Core/Markdown/NavigationPolicy.cs`:

```csharp
using StickyMD.Core.Notes;

namespace StickyMD.Core.Markdown;

public enum NavigationAction
{
    /// <summary>Refuse it. The default for anything not explicitly listed.</summary>
    Block,

    /// <summary>Hand it to the OS default browser. <c>Target</c> is the URL.</summary>
    OpenInBrowser,

    /// <summary>Open it as a StickyMD note. <c>Target</c> is a canonical path.</summary>
    OpenNote,

    /// <summary>The shell's own initial load. Must not be cancelled.</summary>
    AllowShellLoad,
}

/// <param name="Target">
/// A URL for <see cref="NavigationAction.OpenInBrowser"/>, a canonical note
/// path for <see cref="NavigationAction.OpenNote"/>, null otherwise.
/// </param>
/// <param name="Reason">
/// Why, in a form worth writing to the diagnostics log. A blocked link that
/// leaves no trace is indistinguishable from a broken one.
/// </param>
public sealed record NavigationDecision(
    NavigationAction Action, string? Target, string Reason);

/// <summary>
/// The spec's resource-and-navigation allowlist, as a pure decision. Nothing
/// here launches anything; the App layer performs the action.
/// </summary>
/// <remarks>
/// TWO ENTRY POINTS, ON PURPOSE.
///
/// <see cref="DecideLinkClick"/> is the real allowlist. Links arrive as a
/// 'link' web message carrying the RAW href attribute, because the shell is
/// loaded by NavigateToString and therefore has an opaque origin: the
/// browser's own resolution of "other.md" against about:blank is useless, and
/// fragment navigation there does not reliably raise NavigationStarting at
/// all.
///
/// <see cref="DecideNavigation"/> is the defensive backstop on
/// NavigationStarting and NewWindowRequested. It allows exactly one thing --
/// the shell's own initial load -- and blocks everything else. The spec's
/// "cancel unconditionally" is right in spirit and wrong in detail: cancelling
/// the NavigateToString load leaves the note permanently blank, with no
/// exception and nothing in any log to say why.
///
/// Nothing is allowed by default in either function, so a Markdown link
/// carrying an unexpected or hostile scheme is rejected rather than executed
/// inside the WebView.
///
/// KNOWN v1 LIMIT (spec): a path escaping the note directory
/// (../shared/x.png) does not resolve, and is blocked here.
/// </remarks>
public static class NavigationPolicy
{
    public static NavigationDecision DecideNavigation(string? uri, bool shellLoaded)
    {
        // The one allowance. NavigateToString produces an about:-scheme
        // navigation; letting it through once is what makes the note render.
        if (!shellLoaded
            && uri is not null
            && uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
        {
            return new NavigationDecision(
                NavigationAction.AllowShellLoad, null, "The note shell's initial load.");
        }

        return new NavigationDecision(
            NavigationAction.Block,
            null,
            $"Navigation to '{uri ?? "(null)"}' was refused; the note shell never navigates.");
    }

    public static NavigationDecision DecideLinkClick(string? href, string noteDirectory)
    {
        if (string.IsNullOrWhiteSpace(href))
            return Blocked(href, "the link had no target");

        var trimmed = href.Trim();

        if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            // note.local is for subresource loads only. Navigating to it would
            // leave the shell -- and its CSP -- behind.
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var url)
                && url.Host.Equals("note.local", StringComparison.OrdinalIgnoreCase))
            {
                return Blocked(trimmed, "the virtual host is not a navigation target");
            }

            return new NavigationDecision(
                NavigationAction.OpenInBrowser, trimmed, "Opened in the default browser.");
        }

        // Any other scheme -- javascript:, data:, file:, ms-msdt:, mailto:,
        // and everything nobody has thought of -- is refused. An allowlist
        // that grows by sympathy is not an allowlist.
        if (HasScheme(trimmed))
            return Blocked(trimmed, "its scheme is not allowed");

        // The bridge script scrolls in-note anchors itself and never posts
        // them, so one arriving here means something unexpected happened.
        if (trimmed.StartsWith('#'))
            return Blocked(trimmed, "in-note anchors are handled in the page");

        // Decode ONCE before the traversal test -- that is the depth the
        // filesystem resolves at, and without it "%2e%2e%2f" sails past a
        // literal ".." check. Mirrors MarkdownRenderer.ResolveImageUrl.
        string decoded;
        try { decoded = Uri.UnescapeDataString(trimmed); }
        catch (UriFormatException) { return Blocked(trimmed, "it is not a decodable path"); }

        // Drop a fragment: v1 cannot scroll to a heading in another note, and
        // opening the file is still the useful half.
        var hash = decoded.IndexOf('#');
        if (hash >= 0) decoded = decoded[..hash];

        if (decoded.Length == 0)
            return Blocked(trimmed, "the link resolved to nothing");

        if (!decoded.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            return Blocked(trimmed, "only .md links open as notes");

        if (EscapesDirectory(decoded))
            return Blocked(trimmed, "it points outside the note's folder");

        if (!NotePath.TryCanonical(Path.Combine(noteDirectory, decoded), out var target))
            return Blocked(trimmed, "it does not resolve to a usable path");

        // Belt and braces: even after the segment test, confirm the resolved
        // path really is inside the note directory. The string test can be
        // fooled by something the segment split did not anticipate; this
        // cannot.
        if (!NotePath.TryCanonical(noteDirectory, out var root)
            || !target.StartsWith(root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            return Blocked(trimmed, "it resolved outside the note's folder");
        }

        return new NavigationDecision(
            NavigationAction.OpenNote, target, "Opened as a StickyMD note.");
    }

    private static NavigationDecision Blocked(string? href, string why)
        => new(NavigationAction.Block, null, $"Blocked '{href ?? "(null)"}': {why}.");

    private static bool HasScheme(string url)
        => url.Contains("://", StringComparison.Ordinal)
        || System.Text.RegularExpressions.Regex.IsMatch(
            url, @"^[A-Za-z][A-Za-z0-9+.\-]*:");

    /// <summary>
    /// Mirrors <c>MarkdownRenderer.EscapesNoteDirectory</c>. A ".." SEGMENT
    /// escapes; two dots merely inside a filename ("notes..final.md") do not,
    /// and blocking those was wrong.
    /// </summary>
    private static bool EscapesDirectory(string url)
    {
        if (url.StartsWith('/') || url.StartsWith('\\')) return true;
        if (url.Length > 1 && url[1] == ':') return true;

        foreach (var segment in url.Split('/', '\\'))
            if (segment == "..") return true;

        return false;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test --filter FullyQualifiedName~NavigationPolicyTests
```

Expected: PASS, 35 tests (the theories expand).

- [ ] **Step 5: Run the full suite and confirm Core is still pure**

```bash
dotnet test
grep -rn "System.Windows\|System.Drawing\|DllImport" src/StickyMD.Core/
```

Expected: `failed: 0`, and the grep returns **nothing**. Core stayed platform-free through three tasks of Plan B; the compiler enforces most of it, but `[DllImport]` would compile fine on `net10.0` and must be caught by eye.

- [ ] **Step 6: Commit**

Hand these to the user:

```bash
git add src/StickyMD.Core/Markdown tests/StickyMD.Core.Tests/Markdown
git commit -m "Add NavigationPolicy: the link allowlist as a pure decision

The spec says navigation is intercepted before the WebView acts on it, and
that NavigationStarting cancels unconditionally. Taken literally that
cancels the shell's own NavigateToString load, and the note renders
nothing -- permanently blank, no exception, nothing in any log. So the two
paths are split.

DecideLinkClick is the real allowlist. Links arrive as a web message
carrying the RAW href, because the shell has an opaque origin: the
browser's resolution of 'other.md' against about:blank is useless, and
fragment navigation there does not reliably raise NavigationStarting at
all. DecideNavigation is the backstop -- it allows exactly the initial
shell load and blocks everything else, catching whatever the script did
not.

Nothing is allowed by default. mailto: and ftp: are blocked along with
javascript:, data:, file: and ms-msdt:, not because they are dangerous but
because they are not in the spec's table, and an allowlist that grows by
sympathy is not an allowlist.

Decoding happens before the traversal test, because %2e%2e%2f sails
straight past a literal '..' check -- the same bypass
MarkdownRenderer.ResolveImageUrl already guards. The resolved path is then
re-checked against the note directory, since a string-segment test can be
fooled by something the split did not anticipate and a prefix comparison
on the canonical result cannot."
```

---

## Task 6: `MonitorEnumerator` and `SystemTheme` — the two OS probes

Both read OS state through Win32 and both need a seam so their consumers are testable. Grouped into one task because they carry the same reviewer concern — P/Invoke correctness plus a fake-able interface — and both are small.

**Files:**

- Create: `src/StickyMD.App/Interop/NativeMethods.cs`
- Create: `src/StickyMD.App/Interop/MonitorEnumerator.cs`
- Create: `src/StickyMD.App/Interop/SystemTheme.cs`
- Create: `src/StickyMD.App/Interop/WindowGeometry.cs`
- Create: `src/StickyMD.App/Interop/DwmCorners.cs`
- Test: `tests/StickyMD.App.Tests/Interop/MonitorMappingTests.cs`
- Test: `tests/StickyMD.App.Tests/TestSupport/FakeMonitorProvider.cs`

**Interfaces:**

- Consumes: `MonitorInfo`, `PixelRect` (Core `Geometry`), `ThemeMode`, `ThemePreference` (Core).
- Produces:
  - `interface IMonitorProvider { IReadOnlyList<MonitorInfo> GetMonitors(); }`
  - `sealed record RawMonitor(PixelRect Bounds, PixelRect WorkArea, uint DpiX, bool IsPrimary, string DeviceName)`
  - `static IReadOnlyList<MonitorInfo> MonitorEnumerator.Map(IReadOnlyList<RawMonitor> raw)`
  - `sealed class MonitorEnumerator : IMonitorProvider`
  - `interface ISystemTheme { ThemeMode Resolve(ThemePreference preference); event Action? Changed; }`
  - `sealed class SystemTheme : ISystemTheme, IDisposable`
  - `static PixelRect WindowGeometry.GetBounds(IntPtr hwnd)` / `static void WindowGeometry.SetBounds(IntPtr hwnd, PixelRect rect)`
  - `static void DwmCorners.Round(IntPtr hwnd)`

Task 9 calls `WindowGeometry` and `DwmCorners` from `NoteWindow`. Task 13 takes `IMonitorProvider` and `ISystemTheme` in `WindowManager`'s constructor.

- [ ] **Step 1: Write the failing tests for the pure mapping**

The `EnumDisplayMonitors` call itself cannot be faked, so it is separated from the mapping, and the mapping is what carries the bugs — the DPI convention above all.

`tests/StickyMD.App.Tests/Interop/MonitorMappingTests.cs`:

```csharp
using Shouldly;
using StickyMD.App.Interop;
using StickyMD.Core.Geometry;

namespace StickyMD.App.Tests.Interop;

public class MonitorMappingTests
{
    private static RawMonitor Raw(
        int x = 0, int y = 0, int w = 2560, int h = 1440,
        uint dpi = 96, bool primary = true, string device = @"\\.\DISPLAY1")
        => new(
            new PixelRect(x, y, w, h),
            new PixelRect(x, y, w, h - 48),
            dpi,
            primary,
            device);

    [Fact]
    public void Dpi_is_carried_through_as_a_raw_value_not_a_scale_factor()
    {
        // MonitorInfo.Dpi is 96 / 120 / 144, matching Plan A's
        // WindowPlacementTests which construct Dpi: 96 and Dpi: 144. A scale
        // factor here would make every DPI-derived calculation 96x too small
        // and nothing would fail loudly.
        var mapped = MonitorEnumerator.Map([Raw(dpi: 144)]);

        mapped[0].Dpi.ShouldBe(144);
    }

    [Fact]
    public void Bounds_and_work_area_are_carried_through_unchanged()
    {
        var raw = Raw(x: 2560, y: 0);

        var mapped = MonitorEnumerator.Map([raw])[0];

        mapped.Bounds.ShouldBe(raw.Bounds);
        mapped.WorkArea.ShouldBe(raw.WorkArea);
    }

    [Fact]
    public void The_primary_flag_and_device_name_survive()
    {
        var mapped = MonitorEnumerator.Map(
        [
            Raw(primary: true, device: @"\\.\DISPLAY1"),
            Raw(x: 2560, primary: false, device: @"\\.\DISPLAY2"),
        ]);

        mapped[0].IsPrimary.ShouldBeTrue();
        mapped[0].DeviceName.ShouldBe(@"\\.\DISPLAY1");
        mapped[1].IsPrimary.ShouldBeFalse();
        mapped[1].DeviceName.ShouldBe(@"\\.\DISPLAY2");
    }

    [Fact]
    public void The_primary_monitor_is_listed_first()
    {
        // WindowPlacement.PickTarget falls back to FirstOrDefault(IsPrimary)
        // and then to monitors[0]. Ordering primary first makes those two
        // fallbacks agree instead of quietly disagreeing.
        var mapped = MonitorEnumerator.Map(
        [
            Raw(x: 2560, primary: false, device: @"\\.\DISPLAY2"),
            Raw(primary: true, device: @"\\.\DISPLAY1"),
        ]);

        mapped[0].DeviceName.ShouldBe(@"\\.\DISPLAY1");
    }

    [Fact]
    public void A_zero_dpi_is_replaced_with_96()
    {
        // GetDpiForMonitor can fail; the caller records 0 rather than
        // guessing. A Dpi of 0 downstream is a division by zero waiting to
        // happen, so it is settled here.
        MonitorEnumerator.Map([Raw(dpi: 0)])[0].Dpi.ShouldBe(96);
    }

    [Fact]
    public void An_empty_device_name_becomes_a_stable_placeholder()
    {
        // notes.json stores the device name. An empty string there would be
        // indistinguishable from "not recorded", and the field is written from
        // v1 specifically so prefer-original-monitor can land later.
        MonitorEnumerator.Map([Raw(device: "")])[0]
            .DeviceName.ShouldBe("(unknown)");
    }

    [Fact]
    public void No_monitors_maps_to_an_empty_list_rather_than_throwing()
    {
        // WindowPlacement.Clamp already returns the saved rect untouched for an
        // empty list. This just must not be the thing that throws.
        MonitorEnumerator.Map([]).ShouldBeEmpty();
    }

    [Fact]
    public void Mapped_monitors_feed_WindowPlacement_unchanged()
    {
        var monitors = MonitorEnumerator.Map(
        [
            Raw(primary: true),
            Raw(x: 2560, primary: false, device: @"\\.\DISPLAY2"),
        ]);

        var offScreen = new PixelRect(9000, 9000, 300, 340);

        var clamped = WindowPlacement.Clamp(offScreen, monitors);

        clamped.ShouldNotBe(offScreen);
        monitors.ShouldContain(m =>
            clamped.X >= m.WorkArea.X && clamped.Right <= m.WorkArea.Right);
    }
}
```

`tests/StickyMD.App.Tests/TestSupport/FakeMonitorProvider.cs`:

```csharp
using StickyMD.App.Interop;
using StickyMD.Core.Geometry;

namespace StickyMD.App.Tests.TestSupport;

/// <summary>A fixed monitor set. Task 13's tests clamp against these.</summary>
public sealed class FakeMonitorProvider(params MonitorInfo[] monitors) : IMonitorProvider
{
    public IReadOnlyList<MonitorInfo> Monitors { get; set; } = monitors;

    public int CallCount { get; private set; }

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        CallCount++;
        return Monitors;
    }

    /// <summary>Two 2560x1440 displays at 100%, matching the dev machine.</summary>
    public static FakeMonitorProvider TwoAt100Percent() => new(
        new MonitorInfo(
            new PixelRect(0, 0, 2560, 1440),
            new PixelRect(0, 0, 2560, 1392),
            96, true, @"\\.\DISPLAY1"),
        new MonitorInfo(
            new PixelRect(2560, 0, 2560, 1440),
            new PixelRect(2560, 0, 2560, 1392),
            96, false, @"\\.\DISPLAY2"));

    /// <summary>A single display, for the "monitor unplugged" path.</summary>
    public static FakeMonitorProvider OnlyPrimary() => new(
        new MonitorInfo(
            new PixelRect(0, 0, 2560, 1440),
            new PixelRect(0, 0, 2560, 1392),
            96, true, @"\\.\DISPLAY1"));
}
```

- [ ] **Step 2: Run them to verify they fail**

```bash
dotnet test --filter FullyQualifiedName~MonitorMappingTests
```

Expected: FAIL to compile.

- [ ] **Step 3: Write `NativeMethods`**

Every `[DllImport]` in the app lives in this one file, so the Win32 surface is a single thing to review.

`src/StickyMD.App/Interop/NativeMethods.cs`:

```csharp
using System.Runtime.InteropServices;

namespace StickyMD.App.Interop;

/// <summary>
/// The app's entire Win32 surface. One file so it can be reviewed as a whole,
/// and so nothing outside Interop needs a using for InteropServices.
/// </summary>
internal static class NativeMethods
{
    // ---- Rectangles -------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    // ---- Monitors ---------------------------------------------------------

    internal const int MONITORINFOF_PRIMARY = 0x00000001;

    /// <summary>MDT_EFFECTIVE_DPI -- the DPI the app should render at.</summary>
    internal const int MDT_EFFECTIVE_DPI = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MONITORINFOEXW
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;

        // CCHDEVICENAME is 32. ByValTStr with CharSet.Unicode marshals it as
        // 32 UTF-16 units, which is what the struct actually contains -- an
        // ANSI marshalling here reads half the struct as garbage and the
        // failure looks like a corrupt device name rather than a wrong CharSet.
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    internal delegate bool MonitorEnumProc(
        IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayMonitors(
        IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEXW lpmi);

    [DllImport("shcore.dll")]
    internal static extern int GetDpiForMonitor(
        IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    // ---- Window geometry --------------------------------------------------

    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    // ---- DWM --------------------------------------------------------------

    internal const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    /// <summary>DWMWCP_ROUND.</summary>
    internal const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    // ---- Shell file operations -------------------------------------------

    internal const uint FO_DELETE = 0x0003;
    internal const ushort FOF_SILENT = 0x0004;
    internal const ushort FOF_NOCONFIRMATION = 0x0010;
    internal const ushort FOF_ALLOWUNDO = 0x0040;
    internal const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 0)]
    internal struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHFileOperationW(ref SHFILEOPSTRUCTW lpFileOp);
}
```

- [ ] **Step 4: Write `MonitorEnumerator`**

`src/StickyMD.App/Interop/MonitorEnumerator.cs`:

```csharp
using StickyMD.Core.Geometry;

namespace StickyMD.App.Interop;

/// <summary>
/// Monitor discovery. Injected so <c>WindowManager</c> can be tested against
/// synthetic monitor sets -- unplugging a display in a unit test is otherwise
/// not an option.
/// </summary>
public interface IMonitorProvider
{
    IReadOnlyList<MonitorInfo> GetMonitors();
}

/// <summary>
/// One monitor exactly as Win32 reported it, before any interpretation.
/// </summary>
/// <param name="DpiX">
/// Raw effective DPI, or 0 when <c>GetDpiForMonitor</c> failed. Recorded as 0
/// rather than guessed, so the mapping decides in one place.
/// </param>
public sealed record RawMonitor(
    PixelRect Bounds,
    PixelRect WorkArea,
    uint DpiX,
    bool IsPrimary,
    string DeviceName);

/// <summary>
/// Win32 monitor discovery feeding <see cref="WindowPlacement.Clamp"/>.
/// Discovery lives in App; the placement math lives in Core.
/// </summary>
public sealed class MonitorEnumerator : IMonitorProvider
{
    /// <summary>
    /// The interpretation half, separated from the P/Invoke half so it is
    /// testable. This is where the bugs are: the DPI convention, the ordering
    /// WindowPlacement's fallbacks assume, and the zero-value guards.
    /// </summary>
    public static IReadOnlyList<MonitorInfo> Map(IReadOnlyList<RawMonitor> raw)
    {
        var mapped = new List<MonitorInfo>(raw.Count);

        foreach (var monitor in raw)
        {
            mapped.Add(new MonitorInfo(
                monitor.Bounds,
                monitor.WorkArea,
                // RAW DPI -- 96, 120, 144 -- never a scale factor. Plan A's
                // WindowPlacementTests construct Dpi: 96 and Dpi: 144, and a
                // scale factor here would be 96x off with nothing failing
                // loudly. Zero means GetDpiForMonitor failed; 96 is the only
                // safe answer, and a Dpi of 0 downstream is a division waiting
                // to happen.
                monitor.DpiX == 0 ? 96 : monitor.DpiX,
                monitor.IsPrimary,
                // notes.json stores this. An empty string would be
                // indistinguishable from "not recorded", and the field exists
                // from v1 so prefer-original-monitor can land later.
                string.IsNullOrWhiteSpace(monitor.DeviceName)
                    ? "(unknown)"
                    : monitor.DeviceName));
        }

        // Primary first. WindowPlacement.PickTarget falls back to
        // FirstOrDefault(IsPrimary) and then to monitors[0]; ordering primary
        // first makes those two fallbacks agree rather than quietly disagree
        // depending on enumeration order.
        mapped.Sort((a, b) => b.IsPrimary.CompareTo(a.IsPrimary));

        return mapped;
    }

    public IReadOnlyList<MonitorInfo> GetMonitors() => Map(Enumerate());

    private static List<RawMonitor> Enumerate()
    {
        var raw = new List<RawMonitor>();

        // The callback is held in a local so the GC cannot collect the
        // delegate while native code is still calling it. Passing a lambda
        // directly is the classic crash here, and it only reproduces under
        // memory pressure.
        NativeMethods.MonitorEnumProc callback = (hMonitor, _, _, _) =>
        {
            var info = new NativeMethods.MONITORINFOEXW
            {
                cbSize = System.Runtime.InteropServices.Marshal
                    .SizeOf<NativeMethods.MONITORINFOEXW>(),
            };

            if (!NativeMethods.GetMonitorInfoW(hMonitor, ref info)) return true;

            uint dpi = 0;
            if (NativeMethods.GetDpiForMonitor(
                    hMonitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0)
            {
                dpi = dpiX;
            }

            raw.Add(new RawMonitor(
                ToRect(info.rcMonitor),
                ToRect(info.rcWork),
                dpi,
                (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0,
                info.szDevice ?? string.Empty));

            return true;
        };

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);

        return raw;
    }

    private static PixelRect ToRect(NativeMethods.RECT r)
        => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
}
```

- [ ] **Step 5: Write `WindowGeometry` and `DwmCorners`**

`src/StickyMD.App/Interop/WindowGeometry.cs`:

```csharp
using StickyMD.Core.Geometry;

namespace StickyMD.App.Interop;

/// <summary>
/// Reads and writes a window's rectangle in PHYSICAL screen pixels.
/// </summary>
/// <remarks>
/// WPF's Left/Top/Width/Height are device-independent units scaled by the
/// PRIMARY monitor's DPI, so saving a note on a 150% display and restoring it
/// on a 100% one puts the window in the wrong place -- the exact failure
/// success criterion 3 rules out. GetWindowRect and SetWindowPos speak physical
/// pixels on a PerMonitorV2 process, and the index stores physical pixels, so
/// there is no conversion anywhere and therefore no conversion to get wrong.
/// </remarks>
public static class WindowGeometry
{
    public static PixelRect GetBounds(IntPtr hwnd)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var r))
            return new PixelRect(0, 0, 0, 0);

        return new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    public static void SetBounds(IntPtr hwnd, PixelRect rect)
    {
        // NOACTIVATE so restoring a screenful of notes at startup does not
        // fight the logon sequence for focus, and NOZORDER so it does not
        // undo Topmost.
        NativeMethods.SetWindowPos(
            hwnd, IntPtr.Zero,
            rect.X, rect.Y, rect.Width, rect.Height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }
}
```

`src/StickyMD.App/Interop/DwmCorners.cs`:

```csharp
namespace StickyMD.App.Interop;

/// <summary>
/// Rounded window corners via DWM.
/// </summary>
/// <remarks>
/// This is the ONLY route to rounded corners here. Per-pixel transparency is
/// unavailable: the WebView2 backdrop must be opaque, because with alpha 0 the
/// composition surface composites against black rather than against the WPF
/// layer beneath it. So the WPF layer cannot show through the note content, and
/// a clipped-geometry approach would round the chrome while leaving the note
/// body square. Verified working in Spike 0 (hr = 0).
/// </remarks>
public static class DwmCorners
{
    public static void Round(IntPtr hwnd)
    {
        var preference = NativeMethods.DWMWCP_ROUND;

        // Ignore the HRESULT. On a build without corner preferences this
        // returns a failure and the window is square -- cosmetic, and not
        // worth failing to open a note over.
        _ = NativeMethods.DwmSetWindowAttribute(
            hwnd,
            NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE,
            ref preference,
            sizeof(int));
    }
}
```

- [ ] **Step 6: Write `SystemTheme`**

`src/StickyMD.App/Interop/SystemTheme.cs`:

```csharp
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
```

- [ ] **Step 7: Run the tests**

```bash
dotnet test --filter FullyQualifiedName~MonitorMappingTests
dotnet test
```

Expected: 8 mapping tests pass, full suite `failed: 0`.

- [ ] **Step 8: Verify the real enumeration by hand**

`MonitorEnumerator.Map` is tested; `Enumerate` is not, and cannot be. Check it against the known machine configuration — two 2560×1440 displays, both at 100%, DISPLAY2 at x=2560.

Add this temporarily to `App.OnStartup`, run, then remove it:

```csharp
        var monitors = new StickyMD.App.Interop.MonitorEnumerator().GetMonitors();
        MessageBox.Show(
            string.Join("\n", monitors.Select(m =>
                $"{m.DeviceName} bounds={m.Bounds} work={m.WorkArea} dpi={m.Dpi} primary={m.IsPrimary}")),
            "Monitors");
```

Expected output on the dev machine:

```
\\.\DISPLAY1 bounds=PixelRect { X = 0, Y = 0, Width = 2560, Height = 1440 } … dpi=96 primary=True
\\.\DISPLAY2 bounds=PixelRect { X = 2560, Y = 0, … } … dpi=96 primary=False
```

**Check three things specifically:**

1. `dpi=96`, not `dpi=1`. A `1` means the DPI convention was misread as a scale factor.
2. The device names are readable, not mojibake. Garbage means the `MONITORINFOEXW` `CharSet` is wrong.
3. `DISPLAY1` is listed first even though enumeration order is not guaranteed.

Then **remove the temporary code** before committing.

- [ ] **Step 9: Commit**

Hand these to the user:

```bash
git add src/StickyMD.App/Interop tests/StickyMD.App.Tests
git commit -m "Add monitor enumeration, theme resolution, and window geometry

MonitorEnumerator is split into a P/Invoke half and a pure Map half,
because the interpretation is where the bugs are and the enumeration
cannot be faked. Map settles three things a reader would otherwise get
wrong. MonitorInfo.Dpi is a RAW DPI value -- 96, 120, 144 -- matching
Plan A's WindowPlacementTests; a scale factor there would be 96x off with
nothing failing loudly. A GetDpiForMonitor failure is recorded as 0 by the
caller and becomes 96 here, so no downstream division can hit zero. And
the primary monitor is sorted first, because WindowPlacement.PickTarget
falls back to FirstOrDefault(IsPrimary) and then to monitors[0], and
ordering makes those two agree rather than depend on enumeration order.

The EnumDisplayMonitors callback is held in a local. Passing a lambda
directly lets the GC collect the delegate while native code is still
calling it, and it only reproduces under memory pressure.

WindowGeometry works in physical pixels via GetWindowRect and
SetWindowPos rather than WPF's Left/Top. WPF's are DIPs scaled by the
PRIMARY monitor's DPI, so a note saved on a 150% display would restore in
the wrong place on a 100% one. The index stores physical pixels, so with
these there is no conversion anywhere -- and therefore none to get wrong.

SystemTheme keeps ThemePreference and ThemeMode apart. Collapsing them
would force NotePalette to resolve System itself, which means reading the
registry from a type that has no business doing so. It listens through
SystemEvents rather than a hidden message window, since Plan B has none
yet -- with the caveat, noted at the handler, that those events arrive on
their own thread."
```

---

## Task 7: `IFileDeletionService` — deletion that can be undone

The `⋯ → Delete` menu item's destination. `NoteRepository` has no delete method precisely so this can live in App, where Win32 is allowed.

**Files:**

- Create: `src/StickyMD.App/Services/RecycleBinService.cs`
- Test: `tests/StickyMD.App.Tests/Services/RecycleBinServiceTests.cs`

**Interfaces:**

- Consumes: `NotePath` (Task 2), `NativeMethods` (Task 6).
- Produces:
  - `enum DeletionOutcome { Deleted, NotFound, Cancelled, Failed }`
  - `sealed record DeletionResult(DeletionOutcome Outcome, string? Message)`
  - `interface IFileDeletionService { DeletionResult SendToRecycleBin(string path); }`
  - `sealed class RecycleBinService : IFileDeletionService`

Task 13's `WindowManager` takes `IFileDeletionService` and calls it from `DeleteNote`.

- [ ] **Step 1: Write the failing tests**

`tests/StickyMD.App.Tests/Services/RecycleBinServiceTests.cs`:

```csharp
using Shouldly;
using StickyMD.App.Services;

namespace StickyMD.App.Tests.Services;

/// <summary>
/// These touch the real Recycle Bin, on purpose. A faked shell call would
/// verify nothing about the one thing that matters -- that the file is
/// RECOVERABLE afterwards.
/// </summary>
public class RecycleBinServiceTests
{
    private static string TempNote(string content = "# note")
    {
        var dir = Path.Combine(Path.GetTempPath(), "stickymd-recycle", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "note.md");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void A_real_file_is_removed_from_disk()
    {
        var path = TempNote();

        var result = new RecycleBinService().SendToRecycleBin(path);

        result.Outcome.ShouldBe(DeletionOutcome.Deleted);
        File.Exists(path).ShouldBeFalse();
    }

    [Fact]
    public void A_missing_file_reports_NotFound_rather_than_failing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.md");

        new RecycleBinService().SendToRecycleBin(path)
            .Outcome.ShouldBe(DeletionOutcome.NotFound);
    }

    [Fact]
    public void A_non_canonical_path_is_still_deleted()
    {
        var path = TempNote();
        var messy = Path.Combine(Path.GetDirectoryName(path)!, ".", "note.md");

        new RecycleBinService().SendToRecycleBin(messy)
            .Outcome.ShouldBe(DeletionOutcome.Deleted);
    }

    [Fact]
    public void A_locked_file_reports_Failed_with_a_message_and_leaves_it_alone()
    {
        var path = TempNote();

        using (var hold = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = new RecycleBinService().SendToRecycleBin(path);

            result.Outcome.ShouldBe(DeletionOutcome.Failed);
            result.Message.ShouldNotBeNullOrWhiteSpace();
        }

        File.Exists(path).ShouldBeTrue("never destroy a file");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unusable_path_reports_Failed_without_throwing(string? path)
        => new RecycleBinService().SendToRecycleBin(path!)
            .Outcome.ShouldBe(DeletionOutcome.Failed);
}
```

A test asserting the file is actually _in_ the Recycle Bin is deliberately absent: enumerating it needs the Shell COM namespace, and `FOF_ALLOWUNDO` plus a passing manual check is a better trade than a fragile COM test. The manual checklist in Task 14 covers it.

- [ ] **Step 2: Run them to verify they fail**

```bash
dotnet test --filter FullyQualifiedName~RecycleBinServiceTests
```

Expected: FAIL to compile.

- [ ] **Step 3: Write `RecycleBinService`**

`src/StickyMD.App/Services/RecycleBinService.cs`:

```csharp
using StickyMD.App.Interop;
using StickyMD.Core.Notes;

namespace StickyMD.App.Services;

public enum DeletionOutcome
{
    /// <summary>Gone from its folder and recoverable from the Recycle Bin.</summary>
    Deleted,

    /// <summary>Already absent. Not an error -- the desired end state holds.</summary>
    NotFound,

    /// <summary>The shell aborted it.</summary>
    Cancelled,

    /// <summary>It is still there. <c>Message</c> says why.</summary>
    Failed,
}

/// <param name="Message">
/// Non-null on <see cref="DeletionOutcome.Failed"/>. Shown in an inline bar
/// and written to the diagnostics log.
/// </param>
public sealed record DeletionResult(DeletionOutcome Outcome, string? Message);

/// <summary>
/// Sends a note file to the Recycle Bin. Injected so <c>WindowManager</c>'s
/// delete path is testable without destroying anything.
/// </summary>
public interface IFileDeletionService
{
    DeletionResult SendToRecycleBin(string path);
}

/// <summary>
/// Recycle Bin deletion via the shell.
/// </summary>
/// <remarks>
/// FOF_ALLOWUNDO is the entire point. File.Delete is permanent, and this app's
/// governing rule is "never destroy a file" -- a mis-clicked Delete has to be
/// recoverable from the place users already know to look.
///
/// SHFileOperationW is deprecated in favour of the IFileOperation COM
/// interface, and is used anyway: it needs no COM apartment management, no
/// interop assembly, and no reference beyond shell32, and for a single-file
/// delete-to-bin it does exactly one thing. If it ever stops working,
/// IFileOperation is the replacement -- not File.Delete.
///
/// pFrom is DOUBLE-NULL-terminated. It is a list, not a string, and a single
/// terminator makes the shell read past the end of the buffer for the next
/// entry. LPWStr marshalling adds one terminator, so the extra "\0" is added
/// here.
/// </remarks>
public sealed class RecycleBinService : IFileDeletionService
{
    public DeletionResult SendToRecycleBin(string path)
    {
        if (!NotePath.TryCanonical(path, out var canonical))
            return new DeletionResult(DeletionOutcome.Failed, "That path is not usable.");

        if (!File.Exists(canonical))
            return new DeletionResult(DeletionOutcome.NotFound, null);

        var operation = new NativeMethods.SHFILEOPSTRUCTW
        {
            hwnd = IntPtr.Zero,
            wFunc = NativeMethods.FO_DELETE,
            pFrom = canonical + "\0",
            pTo = null,
            fFlags = (ushort)(
                NativeMethods.FOF_ALLOWUNDO
                | NativeMethods.FOF_NOCONFIRMATION
                | NativeMethods.FOF_SILENT
                | NativeMethods.FOF_NOERRORUI),
        };

        int result;

        try
        {
            result = NativeMethods.SHFileOperationW(ref operation);
        }
        catch (System.Runtime.InteropServices.SEHException ex)
        {
            return new DeletionResult(DeletionOutcome.Failed, ex.Message);
        }

        if (operation.fAnyOperationsAborted)
            return new DeletionResult(DeletionOutcome.Cancelled, null);

        if (result != 0)
        {
            // SHFileOperation's codes are its own, not Win32 error codes, so
            // FormatMessage would produce something misleading. Report the
            // number and the observable fact.
            return new DeletionResult(
                DeletionOutcome.Failed,
                $"Windows could not delete the file (shell error {result}).");
        }

        // Confirm rather than trust. A zero return with the file still present
        // would otherwise close the note and leave the file behind, and the
        // window would be gone before anyone noticed.
        if (File.Exists(canonical))
        {
            return new DeletionResult(
                DeletionOutcome.Failed,
                "Windows reported success but the file is still there.");
        }

        return new DeletionResult(DeletionOutcome.Deleted, null);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test --filter FullyQualifiedName~RecycleBinServiceTests
```

Expected: PASS, 7 tests.

If `A_locked_file_reports_Failed…` instead reports `Deleted`, the shell moved the file despite the lock — check that `FOF_NOERRORUI` did not suppress a genuine failure into a success code, and rely on the `File.Exists` confirmation.

- [ ] **Step 5: Verify recoverability by hand**

Automated tests prove the file left its folder. Only a human can confirm it landed somewhere recoverable.

1. Create `%USERPROFILE%\StickyMD Notes\delete-me.md` with any content.
2. In a scratch console or via the temporary startup code, call `new RecycleBinService().SendToRecycleBin(thatPath)`.
3. Open the Recycle Bin. **`delete-me.md` must be listed.**
4. Restore it and confirm it returns to `%USERPROFILE%\StickyMD Notes\`.

**If it is not in the Recycle Bin, `FOF_ALLOWUNDO` is not taking effect and the file was destroyed.** Stop and fix that before anything wires this up to a menu item.

- [ ] **Step 6: Commit**

Hand these to the user:

```bash
git add src/StickyMD.App/Services tests/StickyMD.App.Tests/Services
git commit -m "Add Recycle Bin deletion behind IFileDeletionService

NoteRepository has no delete method by design: sending a file to the
Recycle Bin is platform behaviour and belongs where Win32 is allowed.

FOF_ALLOWUNDO is the whole point. File.Delete is permanent and the
governing rule is never destroy a file, so a mis-clicked Delete has to be
recoverable from the place users already know to look.

SHFileOperationW is deprecated in favour of IFileOperation and is used
anyway -- no COM apartment management, no interop assembly, nothing beyond
shell32, and for one delete-to-bin it does exactly one thing. If it ever
stops working, IFileOperation is the replacement, not File.Delete.

pFrom gets an extra terminator because it is a LIST, not a string: LPWStr
marshalling supplies one null and the shell needs two, or it reads past
the buffer looking for the next entry.

Success is confirmed with File.Exists rather than trusted from the return
code. A zero return with the file still present would otherwise close the
note and leave the file behind -- and the window would be gone before
anyone noticed the file was not.

The tests use the real Recycle Bin. A faked shell call would verify
nothing about the only property that matters, which is that the file can
be recovered; asserting it is actually IN the bin needs the Shell COM
namespace and is left to the manual checklist."
```

---

## Task 8: `WebViewHost` and the one shared `CoreWebView2Environment`

Everything Spike 0 measured lands here. Nothing in this task may be simplified without re-reading the spike.

**Files:**

- Create: `src/StickyMD.App/Messaging/WebMessages.cs`
- Create: `src/StickyMD.App/Services/WebViewEnvironment.cs`
- Create: `src/StickyMD.App/Services/WebViewHost.cs`
- Test: `tests/StickyMD.App.Tests/Messaging/WebMessagesTests.cs`

**Interfaces:**

- Consumes: `HtmlDocumentBuilder`, `HtmlShellOptions` (Task 4), `NavigationPolicy` (Task 5), `AppPaths.WebViewUserDataDir` (Task 3), `NoteTheme`, `RenderResult`.
- Produces:
  - `sealed record RenderMessage(string Type, string Html, string Token)` — outbound to the page
  - `sealed record InboundMessage(string Type, int SpanStart, int SpanEnd, string? Token, string? Href)`
  - `static class WebMessages` with `string Render(RenderResult result)` and `InboundMessage? Parse(string json)`
  - `static class WebViewEnvironment` with `Task<CoreWebView2Environment> GetAsync()` and `static string? DetectRuntimeVersion()`
  - `sealed class WebViewHost : IDisposable` with:
    - `WebView2CompositionControl Control { get; }`
    - `Task InitializeAsync(string noteDirectory, NoteTheme theme, bool allowRemoteImages, System.Drawing.Color backdrop)`
    - `Task RenderAsync(RenderResult result)`
    - `Task SetThemeAsync(NoteTheme theme, bool allowRemoteImages, System.Drawing.Color backdrop)`
    - `void RemapNoteDirectory(string noteDirectory)`
    - `event Action<int, int, string?>? TaskToggleRequested`
    - `event Action<string?>? LinkClicked`
    - `event Action? EditRequested`
    - `event Action<string>? FellBackToPlainText`

Task 9 hosts `Control` in `NoteWindow`. Task 11 subscribes to the three request events. Task 12 handles `FellBackToPlainText`.

### What Spike 0 fixed, and what happens if it is undone

| Setting                  | Value                        | Undoing it produces                                                                   |
| ------------------------ | ---------------------------- | ------------------------------------------------------------------------------------- |
| Control type             | `WebView2CompositionControl` | The plain `WebView2` is an `HwndHost` and renders **nothing** in a transparent window |
| `DefaultBackgroundColor` | Opaque note `ContentBg`      | Alpha 0 composites against **black**, not the WPF layer                               |
| Environment              | One shared instance          | A separate browser process tree per note                                              |
| Shell delivery           | `NavigateToString`, once     | A file in the notes root, or a flash and lost scroll on every update                  |
| `NavigationStarting`     | Cancel except the shell load | Cancelling unconditionally leaves the note permanently blank, silently                |

- [ ] **Step 1: Write the failing tests for the message contract**

The WebView cannot be unit tested; the JSON crossing the boundary can, and it is where a silent mismatch would live — the page sends camelCase, C# defaults to PascalCase.

`tests/StickyMD.App.Tests/Messaging/WebMessagesTests.cs`:

```csharp
using Shouldly;
using StickyMD.App.Messaging;
using StickyMD.Core.Markdown;

namespace StickyMD.App.Tests.Messaging;

public class WebMessagesTests
{
    [Fact]
    public void The_render_message_uses_the_names_the_bridge_script_reads()
    {
        // The script reads message.type, message.html and message.token.
        // PascalCase here would make every render silently do nothing.
        var json = WebMessages.Render(new RenderResult("<p>hi</p>", "ABC123"));

        json.ShouldContain("\"type\":\"render\"");
        json.ShouldContain("\"html\":");
        json.ShouldContain("\"token\":\"ABC123\"");
    }

    [Fact]
    public void The_render_message_escapes_html_safely_for_json()
    {
        var json = WebMessages.Render(
            new RenderResult("<p>a \"quoted\" \\ backslash</p>", "T"));

        // Round-trips: whatever this produces must parse back to the original.
        var parsed = System.Text.Json.JsonDocument.Parse(json);
        parsed.RootElement.GetProperty("html").GetString()
            .ShouldBe("<p>a \"quoted\" \\ backslash</p>");
    }

    [Fact]
    public void A_toggleTask_message_parses_with_its_span_and_token()
    {
        var message = WebMessages.Parse(
            """{"type":"toggleTask","spanStart":2,"spanEnd":4,"token":"ABC"}""");

        message.ShouldNotBeNull();
        message.Type.ShouldBe("toggleTask");
        message.SpanStart.ShouldBe(2);
        message.SpanEnd.ShouldBe(4);
        message.Token.ShouldBe("ABC");
    }

    [Fact]
    public void A_link_message_parses_with_its_raw_href()
    {
        var message = WebMessages.Parse("""{"type":"link","href":"other.md"}""");

        message!.Type.ShouldBe("link");
        message.Href.ShouldBe("other.md");
    }

    [Fact]
    public void A_requestEdit_message_parses()
        => WebMessages.Parse("""{"type":"requestEdit"}""")!.Type.ShouldBe("requestEdit");

    [Fact]
    public void A_ready_message_parses()
        => WebMessages.Parse("""{"type":"ready"}""")!.Type.ShouldBe("ready");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("\"a bare string\"")]
    [InlineData("{}")]
    public void Anything_unrecognisable_parses_to_null_rather_than_throwing(string json)
    {
        // These arrive from a renderer process. A parse failure must never
        // reach the message handler as an exception -- WebMessageReceived runs
        // on the UI thread and an unhandled throw there takes down the app.
        WebMessages.Parse(json).ShouldBeNull();
    }

    [Fact]
    public void A_toggleTask_missing_its_span_parses_to_a_message_with_an_invalid_span()
    {
        // Parsing succeeds; the span is then rejected by TaskListToggler,
        // which is the component that owns span validation. Two places
        // validating spans is how they end up disagreeing.
        var message = WebMessages.Parse("""{"type":"toggleTask","token":"ABC"}""");

        message.ShouldNotBeNull();
        message.SpanStart.ShouldBeLessThan(0);
    }

    [Fact]
    public void A_span_arriving_as_a_json_string_still_parses()
    {
        // parseInt in the bridge yields a number, but a hand-crafted or future
        // message might not. Tolerating it costs nothing and the span is
        // validated downstream anyway.
        var message = WebMessages.Parse(
            """{"type":"toggleTask","spanStart":"2","spanEnd":"4","token":"A"}""");

        message!.SpanStart.ShouldBe(2);
        message.SpanEnd.ShouldBe(4);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

```bash
dotnet test --filter FullyQualifiedName~WebMessagesTests
```

Expected: FAIL to compile.

- [ ] **Step 3: Write `WebMessages`**

`src/StickyMD.App/Messaging/WebMessages.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using StickyMD.Core.Markdown;

namespace StickyMD.App.Messaging;

/// <summary>What the host pushes into the page.</summary>
public sealed record RenderMessage(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("html")] string Html,
    [property: JsonPropertyName("token")] string Token);

/// <summary>
/// What the page posts back, flattened into one shape. The four message types
/// share so few fields that separate records would buy nothing but a
/// discriminated parse.
/// </summary>
/// <param name="SpanStart">-1 when absent. Validated by TaskListToggler, not here.</param>
public sealed record InboundMessage(
    string Type,
    int SpanStart,
    int SpanEnd,
    string? Token,
    string? Href);

/// <summary>
/// The host-page message contract.
/// </summary>
/// <remarks>
/// The property names are camelCase because that is what the bridge script
/// reads. A PascalCase render message is not an error -- the script's
/// `message.type !== 'render'` check simply never matches, and every note
/// stays blank with nothing in any log. That is why the names are asserted in
/// tests rather than trusted to a serializer default.
/// </remarks>
public static class WebMessages
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string Render(RenderResult result)
        => JsonSerializer.Serialize(
            new RenderMessage("render", result.Html, result.Token), Options);

    /// <summary>
    /// Parses a message from the page. Returns null for anything
    /// unrecognisable.
    /// </summary>
    /// <remarks>
    /// NEVER throws. WebMessageReceived is raised on the UI thread, and an
    /// unhandled exception there takes the process down -- so a malformed
    /// message from a renderer process must degrade to "ignored", not to a
    /// crash.
    /// </remarks>
    public static InboundMessage? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;

            if (!document.RootElement.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var typeName = type.GetString();
            if (string.IsNullOrEmpty(typeName)) return null;

            return new InboundMessage(
                typeName,
                ReadInt(document.RootElement, "spanStart"),
                ReadInt(document.RootElement, "spanEnd"),
                ReadString(document.RootElement, "token"),
                ReadString(document.RootElement, "href"));
        }
        catch (JsonException) { return null; }
    }

    /// <summary>-1 for absent or unusable, which no valid span can be.</summary>
    private static int ReadInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return -1;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            // parseInt in the bridge yields a number, but tolerating a string
            // costs nothing and the span is validated downstream regardless.
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => -1,
        };
    }

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
```

- [ ] **Step 4: Run the message tests to verify they pass**

```bash
dotnet test --filter FullyQualifiedName~WebMessagesTests
```

Expected: PASS, 14 tests (the theory expands).

- [ ] **Step 5: Write `WebViewEnvironment`**

`src/StickyMD.App/Services/WebViewEnvironment.cs`:

```csharp
using Microsoft.Web.WebView2.Core;
using StickyMD.Core.Persistence;

namespace StickyMD.App.Services;

/// <summary>
/// The one <see cref="CoreWebView2Environment"/> every note shares.
/// </summary>
/// <remarks>
/// SHARING IS NOT AN OPTIMISATION. Each environment owns a browser process
/// tree; one per note would mean a dozen sticky notes costing a dozen
/// browsers. And CreateAsync with the same user-data folder but different
/// options throws, so "create one per note and hope" fails on the second note
/// as soon as anything differs.
///
/// The user-data folder is under LOCALAPPDATA with the rest of StickyMD's
/// state, never inside the notes root -- nothing app-owned goes there.
/// </remarks>
public static class WebViewEnvironment
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static CoreWebView2Environment? _environment;

    /// <summary>
    /// The runtime version string, or null when the Evergreen runtime is
    /// absent. Call this BEFORE opening any note: the failure mode otherwise
    /// is a note window that never paints.
    /// </summary>
    public static string? DetectRuntimeVersion()
    {
        try
        {
            return CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            // Some SDK versions return null, others throw. Treat both as
            // absent, because a caller that handles only one of them will
            // eventually meet the other.
            return null;
        }
        catch (DllNotFoundException)
        {
            return null;
        }
    }

    public static async Task<CoreWebView2Environment> GetAsync()
    {
        if (_environment is not null) return _environment;

        await Gate.WaitAsync().ConfigureAwait(true);

        try
        {
            // Re-check inside the gate. Two notes opening at once would
            // otherwise both create an environment, and the loser's browser
            // process tree would be orphaned.
            if (_environment is not null) return _environment;

            Directory.CreateDirectory(AppPaths.WebViewUserDataDir);

            _environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: AppPaths.WebViewUserDataDir,
                options: null).ConfigureAwait(true);

            return _environment;
        }
        finally
        {
            Gate.Release();
        }
    }
}
```

- [ ] **Step 6: Write `WebViewHost`**

`src/StickyMD.App/Services/WebViewHost.cs`:

```csharp
using System.Drawing;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using StickyMD.App.Messaging;
using StickyMD.Core.Diagnostics;
using StickyMD.Core.Markdown;
using StickyMD.Core.Notes;
using StickyMD.Core.Persistence;
using StickyMD.Core.Theming;

namespace StickyMD.App.Services;

/// <summary>
/// One note's WebView2: the locked-down browser, the static shell, the
/// note-directory mapping, the message bridge, and crash recovery.
/// </summary>
/// <remarks>
/// SPIKE 0 SETTLED THE FOLLOWING. None of it is stylistic.
///
/// WebView2CompositionControl, never the plain WebView2. The plain control is
/// an HwndHost and renders NOTHING inside a window with
/// AllowsTransparency=True. The composition control renders via D3DImage,
/// which is also why airspace does not apply and WPF adornments -- the inline
/// bars -- may legitimately overlay note content.
///
/// DefaultBackgroundColor must be OPAQUE. With alpha 0 the composition surface
/// composites against BLACK rather than against the WPF layer beneath it. That
/// is why per-pixel transparency is unavailable and rounded corners come from
/// DWM. Setting it to the note colour also removes the white flash on first
/// paint.
///
/// The shell is navigated to ONCE. Content arrives by postMessage so scroll
/// survives and an external edit does not flash. The exception is a CSP
/// change: a meta-tag CSP is fixed at parse time, so flipping the
/// remote-image setting rebuilds the shell and navigates again.
///
/// NavigationStarting cancels everything EXCEPT the shell's own load. The spec
/// says cancel unconditionally, which would leave the note permanently blank
/// with no exception anywhere -- see NavigationPolicy.DecideNavigation.
/// </remarks>
public sealed class WebViewHost : IDisposable
{
    private readonly string _notePath;

    private WebView2CompositionControl _control = new();
    private string _noteDirectory = string.Empty;
    private string _mappedDirectory = string.Empty;
    private bool _shellLoaded;
    private bool _allowRemoteImages;
    private NoteTheme? _theme;
    private Color _backdrop;
    private RenderResult? _pendingRender;
    private int _processFailures;
    private bool _disposed;

    /// <param name="notePath">
    /// Canonical, and used only for diagnostics -- so a logged WebView2 crash
    /// names the note it happened in.
    /// </param>
    public WebViewHost(string notePath) => _notePath = notePath;

    public WebView2CompositionControl Control => _control;

    /// <summary>Span start, span end (INCLUSIVE), render token.</summary>
    public event Action<int, int, string?>? TaskToggleRequested;

    /// <summary>The RAW href attribute, unresolved.</summary>
    public event Action<string?>? LinkClicked;

    public event Action? EditRequested;

    /// <summary>
    /// The renderer failed twice. The note must fall back to a plain-text pane
    /// so it stays readable. The argument is a message for the inline bar.
    /// </summary>
    public event Action<string>? FellBackToPlainText;

    public async Task InitializeAsync(
        string noteDirectory, NoteTheme theme, bool allowRemoteImages, Color backdrop)
    {
        _noteDirectory = noteDirectory;
        _theme = theme;
        _allowRemoteImages = allowRemoteImages;
        _backdrop = backdrop;

        // BEFORE EnsureCoreWebView2Async, so the very first frame is the note
        // colour. Setting it afterwards shows a white flash first.
        _control.DefaultBackgroundColor = backdrop;

        var environment = await WebViewEnvironment.GetAsync().ConfigureAwait(true);
        await _control.EnsureCoreWebView2Async(environment).ConfigureAwait(true);

        var core = _control.CoreWebView2;

        ApplyLockdown(core.Settings);

        core.WebMessageReceived += OnWebMessageReceived;
        core.NavigationStarting += OnNavigationStarting;
        core.NewWindowRequested += OnNewWindowRequested;
        core.ProcessFailed += OnProcessFailed;

        MapNoteDirectory(core, noteDirectory);
        NavigateShell();
    }

    private static void ApplyLockdown(CoreWebView2Settings settings)
    {
        // A note is a text renderer. Everything below is a browser affordance
        // it has no use for, and every one of them is a way for a synced note
        // to do something the user did not ask for.
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.IsBuiltInErrorPageEnabled = false;
        settings.IsPinchZoomEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;

        // Scripting stays ON: the bridge script is the only script that can
        // run, since Markdig runs with DisableHtml() and the CSP admits only
        // the shell's nonce.
        settings.IsScriptEnabled = true;
        settings.IsWebMessageEnabled = true;
    }

    private void MapNoteDirectory(CoreWebView2 core, string noteDirectory)
    {
        if (!Directory.Exists(noteDirectory)) return;

        // DenyCors, not Allow. Notes need ordinary subresource loads -- <img
        // src> -- and nothing more. Allow would additionally permit
        // cross-origin fetch/XHR against the mapped note directory, which
        // nothing in this design requires. A stronger boundary at zero cost.
        core.SetVirtualHostNameToFolderMapping(
            "note.local", noteDirectory, CoreWebView2HostResourceAccessKind.DenyCors);

        _mappedDirectory = noteDirectory;
    }

    /// <summary>
    /// Points <c>note.local</c> at a new directory after the note moved or was
    /// renamed. Without this, images in a moved note silently stop loading.
    /// </summary>
    public void RemapNoteDirectory(string noteDirectory)
    {
        _noteDirectory = noteDirectory;

        var core = _control.CoreWebView2;
        if (core is null) return;

        if (_mappedDirectory.Length > 0)
            core.ClearVirtualHostNameToFolderMapping("note.local");

        MapNoteDirectory(core, noteDirectory);
    }

    private void NavigateShell()
    {
        var core = _control.CoreWebView2;
        if (core is null || _theme is null) return;

        _shellLoaded = false;

        core.NavigateToString(
            HtmlDocumentBuilder.BuildShell(
                new HtmlShellOptions(_theme, _allowRemoteImages)));
    }

    /// <summary>
    /// Pushes rendered content into the page. Queued until the page reports
    /// <c>ready</c>, so a render racing the shell load is not lost.
    /// </summary>
    public Task RenderAsync(RenderResult result)
    {
        if (!_shellLoaded || _control.CoreWebView2 is null)
        {
            _pendingRender = result;
            return Task.CompletedTask;
        }

        _control.CoreWebView2.PostWebMessageAsJson(WebMessages.Render(result));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Applies a new theme, and a new remote-image setting.
    /// </summary>
    /// <remarks>
    /// A theme change alone could be a postMessage that rewrote the CSS
    /// variables. A remote-image change cannot: the CSP lives in a meta tag,
    /// fixed at parse time, so the shell has to be rebuilt and navigated
    /// again. Both go down the same path because the one-frame flash on a
    /// deliberate colour change is not worth two code paths -- and content
    /// updates, which happen constantly, still never navigate.
    /// </remarks>
    public Task SetThemeAsync(NoteTheme theme, bool allowRemoteImages, Color backdrop)
    {
        _theme = theme;
        _allowRemoteImages = allowRemoteImages;
        _backdrop = backdrop;
        _control.DefaultBackgroundColor = backdrop;

        NavigateShell();
        return Task.CompletedTask;
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // This runs on the UI thread. Nothing in here may throw, or a
        // malformed message from a renderer process takes the app down.
        InboundMessage? message;

        try { message = WebMessages.Parse(e.WebMessageAsJson); }
        catch (ArgumentException) { return; }

        if (message is null) return;

        switch (message.Type)
        {
            case "ready":
                _shellLoaded = true;
                _processFailures = 0;

                // Flush a render that arrived while the shell was still
                // loading -- otherwise the first paint of a note opened at
                // startup is empty until something else triggers a re-render.
                if (_pendingRender is not null)
                {
                    var pending = _pendingRender;
                    _pendingRender = null;
                    _ = RenderAsync(pending);
                }

                break;

            case "toggleTask":
                TaskToggleRequested?.Invoke(
                    message.SpanStart, message.SpanEnd, message.Token);
                break;

            case "link":
                LinkClicked?.Invoke(message.Href);
                break;

            case "requestEdit":
                EditRequested?.Invoke();
                break;

            default:
                DiagnosticsLog.Write(
                    AppPaths.DiagnosticsFile,
                    $"{_notePath}: ignored an unknown web message '{message.Type}'.");
                break;
        }
    }

    private void OnNavigationStarting(
        object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        var decision = NavigationPolicy.DecideNavigation(e.Uri, _shellLoaded);

        if (decision.Action == NavigationAction.AllowShellLoad) return;

        e.Cancel = true;

        DiagnosticsLog.Write(
            AppPaths.DiagnosticsFile, $"{_notePath}: {decision.Reason}");
    }

    private void OnNewWindowRequested(
        object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // Handled, never opened. A link that wants a new window is still just
        // a link, and it goes through the same allowlist as any other -- via
        // the page's 'link' message, not here.
        e.Handled = true;

        DiagnosticsLog.Write(
            AppPaths.DiagnosticsFile,
            $"{_notePath}: refused a new-window request for '{e.Uri}'.");
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        DiagnosticsLog.Write(
            AppPaths.DiagnosticsFile,
            $"{_notePath}: WebView2 {e.ProcessFailedKind} (failure {_processFailures + 1}).");

        _processFailures++;

        if (_processFailures >= 2)
        {
            // Recreating a second time risks a crash loop. A plain-text pane
            // keeps the note READABLE, which is the property that matters --
            // and the text is already in memory, so nothing is lost.
            FellBackToPlainText?.Invoke(
                "The renderer stopped twice. Showing the note as plain text.");
            return;
        }

        _ = RecreateAsync();
    }

    private async Task RecreateAsync()
    {
        if (_disposed || _theme is null) return;

        var previous = _control;

        try
        {
            _control = new WebView2CompositionControl();

            // The caller must re-parent Control; NoteWindow does that in its
            // FellBackToPlainText / recreate handling.
            await InitializeAsync(
                _noteDirectory, _theme, _allowRemoteImages, _backdrop)
                .ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            FellBackToPlainText?.Invoke(
                "The renderer could not be restarted. Showing the note as plain text.");
        }
        finally
        {
            try { previous.Dispose(); } catch (InvalidOperationException) { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var core = _control.CoreWebView2;

        if (core is not null)
        {
            core.WebMessageReceived -= OnWebMessageReceived;
            core.NavigationStarting -= OnNavigationStarting;
            core.NewWindowRequested -= OnNewWindowRequested;
            core.ProcessFailed -= OnProcessFailed;

            if (_mappedDirectory.Length > 0)
            {
                try { core.ClearVirtualHostNameToFolderMapping("note.local"); }
                catch (InvalidOperationException) { /* already torn down */ }
            }
        }

        // Disposing the control is what releases the renderer process for this
        // note. Closing a note without it leaves a browser process behind, and
        // it only shows up after the tenth note.
        try { _control.Dispose(); } catch (InvalidOperationException) { }
    }
}
```

- [ ] **Step 7: Build and run the whole suite**

```bash
dotnet build
dotnet test
```

Expected: build clean, `failed: 0`.

`WebViewHost` itself has no unit tests, by design — spec §9 lists WebView2 among the things covered by the manual checklist. Its two testable extractions (`WebMessages`, `NavigationPolicy`) are tested; the rest is verified in Task 9, where there is finally a window to put it in.

- [ ] **Step 8: Commit**

Hand these to the user:

```bash
git add src/StickyMD.App tests/StickyMD.App.Tests
git commit -m "Add WebViewHost and the one shared WebView2 environment

Everything Spike 0 measured lands here, and none of it is stylistic.

The control is WebView2CompositionControl. The plain WebView2 is an
HwndHost and renders NOTHING inside a window with AllowsTransparency=True.
The composition control renders via D3DImage, which is also why airspace
does not apply -- WPF adornments may overlay note content, which is what
the inline bars need.

DefaultBackgroundColor is opaque and is set BEFORE
EnsureCoreWebView2Async. With alpha 0 the composition surface composites
against black rather than the WPF layer beneath it, which is why per-pixel
transparency is unavailable; setting it early also removes the white flash
on first paint.

One environment, shared. Each one owns a browser process tree, so a dozen
notes would otherwise mean a dozen browsers -- and CreateAsync with the
same user-data folder but different options throws, so create-per-note
fails on the second note as soon as anything differs. The double-checked
gate matters: two notes opening at once would both create one and orphan
the loser's process tree.

NavigationStarting cancels everything EXCEPT the shell's own load. The
spec says cancel unconditionally, which cancels NavigateToString and
leaves the note permanently blank with no exception and nothing in any
log.

Renders arriving before the page reports ready are queued rather than
dropped, or the first paint of a note restored at startup stays empty
until something else happens to trigger a re-render.

The message property names are camelCase because that is what the bridge
script reads. PascalCase is not an error -- the script's type check simply
never matches and every note stays blank, which is why the names are
asserted in tests rather than left to a serializer default. Parse never
throws, because WebMessageReceived is raised on the UI thread and an
unhandled exception there ends the process.

A second renderer failure falls back to plain text rather than recreating
again. Readable beats a crash loop, and the text is already in memory."
```

---

## Task 9: `NoteWindow` chrome — a real note on screen

The first task that produces something to look at. It ends with a note visible on the desktop, rendering Markdown, resizable, draggable, and fading — verified by hand, because spec §9 puts windows and WebView2 in the manual checklist.

**Files:**

- Create: `src/StickyMD.App/Windows/NoteWindow.xaml`
- Create: `src/StickyMD.App/Windows/NoteWindow.xaml.cs`
- Create: `src/StickyMD.App/Windows/InlineBarHost.cs`
- Modify: `src/StickyMD.App/App.xaml.cs` (temporary dev bootstrap)

**Interfaces:**

- Consumes: `WebViewHost` (Task 8), `WindowGeometry`, `DwmCorners` (Task 6), `NotePalette`, `NoteState`, `NoteTitleResolver`, `MarkdownRenderer`.
- Produces:
  - `interface INoteWindow : IDisposable` — the seam Task 13 tests against
  - `sealed partial class NoteWindow : Window, INoteWindow`
  - `sealed class InlineBarHost` — one message at a time, with up to two actions
  - `sealed record InlineBarRequest(string Id, string Message, string? PrimaryAction, Action? OnPrimary, string? SecondaryAction, Action? OnSecondary, bool Dismissible)`

Tasks 10, 11 and 12 add behaviour to this window. Task 13 drives it through `INoteWindow`.

### `INoteWindow` — why the seam exists

`WindowManager` owns the three-state model, and the spec is explicit that getting it wrong restores zero notes after an exit. That logic must be testable, and a `Window` cannot be constructed on an xUnit thread. So `WindowManager` never sees `NoteWindow` — only this interface:

```csharp
public interface INoteWindow : IDisposable
{
    string NotePath { get; }
    PixelRect Bounds { get; }
    void ShowNote(bool activate);
    void HideNote();
    void FocusNote();
    void ApplyState(NoteState state, NoteTheme theme);
    void ApplyExternalContent(string text, string diskHash);
    void NotifyFileDeleted();
    void NotifyRenamed(string canonicalPath);
    void SaveNow();

    /// <summary>The user clicked the close glyph. THE ONLY thing that may clear isOpen.</summary>
    event Action<string>? CloseRequested;

    event Action<string>? DeleteRequested;
    event Action<string, string>? RenameRequested;
    event Action<string>? OpenNoteRequested;
    event Action<string, NoteState>? StateChanged;
}
```

- [ ] **Step 1: Write the XAML**

`src/StickyMD.App/Windows/NoteWindow.xaml`:

```xml
<Window x:Class="StickyMD.App.Windows.NoteWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:shell="clr-namespace:System.Windows.Shell;assembly=PresentationFramework"
        Title="StickyMD note"
        Width="300" Height="340"
        MinWidth="160" MinHeight="120"
        WindowStartupLocation="Manual"
        ResizeMode="CanResize"
        ShowInTaskbar="False"
        WindowStyle="None"
        AllowsTransparency="True"
        UseLayoutRounding="True"
        SnapsToDevicePixels="True">

  <!--
    AllowsTransparency=True is MANDATORY and is the whole reason this window
    can fade. WS_EX_LAYERED cannot be added to an existing WPF window on
    Windows 11 26200; SetWindowLong returns the previous ex-style with
    lastErr = 0, a textbook success, and changes nothing. WPF applies the style
    at CreateWindowEx, the one moment Windows permits it, and only when this
    property is set in XAML. Spike 0 proved this with a four-test style matrix.

    WindowStyle=None is required by WPF whenever AllowsTransparency is True.
  -->

  <!--
    WindowChrome is MANDATORY, not cosmetic. AllowsTransparency=True removes
    WPF's resize frame entirely (no WS_THICKFRAME), so without this a note
    cannot be resized AT ALL. CaptionHeight=0 makes the whole window client
    area, so the header buttons behave like ordinary buttons.
  -->
  <shell:WindowChrome.WindowChrome>
    <shell:WindowChrome CaptionHeight="0"
                        ResizeBorderThickness="6"
                        GlassFrameThickness="0"
                        CornerRadius="0"
                        UseAeroCaptionButtons="False" />
  </shell:WindowChrome.WindowChrome>

  <Border x:Name="Root" BorderThickness="1">
    <Grid>
      <Grid.RowDefinitions>
        <RowDefinition Height="Auto" />
        <RowDefinition Height="*" />
      </Grid.RowDefinitions>

      <!--
        THE DRAG HANDLER GOES ON THIS ELEMENT, IN CODE-BEHIND, AND NOWHERE
        ELSE. See NoteWindow.xaml.cs. A handler on the Window swallows every
        click over the note body and there is no error when it happens.
      -->
      <Grid x:Name="Header" Row="0" Height="26">
        <Grid.ColumnDefinitions>
          <ColumnDefinition Width="*" />
          <ColumnDefinition Width="Auto" />
        </Grid.ColumnDefinitions>

        <TextBlock x:Name="TitleText"
                   Grid.Column="0"
                   Margin="8,0,4,0"
                   VerticalAlignment="Center"
                   TextTrimming="CharacterEllipsis"
                   FontSize="12"
                   FontWeight="SemiBold"
                   IsHitTestVisible="False"
                   Text="untitled" />

        <!-- Revealed on hover; always present so layout never shifts. -->
        <StackPanel x:Name="HeaderButtons"
                    Grid.Column="1"
                    Orientation="Horizontal"
                    Margin="0,0,4,0"
                    Opacity="0">
          <Button x:Name="ColorButton" Content="&#x25CF;" ToolTip="Colour"
                  Style="{DynamicResource HeaderGlyph}" />
          <Button x:Name="PinButton" Content="&#x1F4CC;" ToolTip="Always on top"
                  Style="{DynamicResource HeaderGlyph}" />
          <Button x:Name="EditButton" Content="&#x270E;" ToolTip="Edit (Ctrl+E)"
                  Style="{DynamicResource HeaderGlyph}" />
          <Button x:Name="MoreButton" Content="&#x22EF;" ToolTip="More"
                  Style="{DynamicResource HeaderGlyph}" />
          <Button x:Name="CloseButton" Content="&#x2715;" ToolTip="Close note"
                  Style="{DynamicResource HeaderGlyph}" />
        </StackPanel>
      </Grid>

      <!--
        The body layers three things. Exactly one of the WebView and the
        TextBox is visible at a time; the bar stack sits on top of whichever
        it is.

        Airspace does NOT apply here. WebView2CompositionControl is an
        ordinary WPF element rendering through D3DImage, so a WPF adornment
        over it is legitimate, which is exactly what the "changed on disk"
        and "couldn't save" bars need. An earlier draft of the spec said
        otherwise, describing the plain WebView2's HwndHost behaviour.
      -->
      <Grid x:Name="Body" Grid.Row="1">
        <ContentControl x:Name="WebViewSlot" />

        <TextBox x:Name="Editor"
                 Visibility="Collapsed"
                 AcceptsReturn="True"
                 AcceptsTab="False"
                 TextWrapping="Wrap"
                 BorderThickness="0"
                 Padding="10,8,10,8"
                 FontSize="14"
                 VerticalScrollBarVisibility="Auto"
                 SpellCheck.IsEnabled="False" />

        <StackPanel x:Name="BarStack"
                    VerticalAlignment="Top"
                    Orientation="Vertical" />
      </Grid>
    </Grid>
  </Border>
</Window>
```

`AcceptsTab="False"` is required by the spec: Tab belongs to the editor's indent operation, and `Esc` is the way out of edit mode.

- [ ] **Step 2: Write `InlineBarHost`**

`src/StickyMD.App/Windows/InlineBarHost.cs`:

```csharp
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
public sealed record InlineBarRequest(
    string Id,
    string Message,
    string? PrimaryAction = null,
    Action? OnPrimary = null,
    string? SecondaryAction = null,
    Action? OnSecondary = null,
    bool Dismissible = true);

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

        AddAction(content, request.PrimaryAction, request.OnPrimary, request.Id);
        AddAction(content, request.SecondaryAction, request.OnSecondary, request.Id);

        if (request.Dismissible)
            AddAction(content, "Dismiss", null, request.Id);

        var bar = new Border
        {
            Padding = new Thickness(8, 5, 8, 5),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = content,
        };

        if (_theme is not null) Paint(bar, _theme);

        return bar;
    }

    private void AddAction(Panel content, string? label, Action? action, string id)
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
            // Dismiss FIRST. An action that opens a dialog or triggers a
            // re-render would otherwise leave its own bar behind while the
            // condition it described is already resolved.
            Dismiss(id);
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
```

- [ ] **Step 3: Write the window code-behind**

`src/StickyMD.App/Windows/NoteWindow.xaml.cs`:

```csharp
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using StickyMD.App.Interop;
using StickyMD.App.Services;
using StickyMD.Core.Geometry;
using StickyMD.Core.Markdown;
using StickyMD.Core.Notes;
using StickyMD.Core.Persistence;
using StickyMD.Core.Theming;

namespace StickyMD.App.Windows;

/// <summary>
/// The seam <c>WindowManager</c> talks to. A WPF <c>Window</c> cannot be
/// constructed on an xUnit thread, and the three-state model this drives is
/// the one piece of window logic that MUST be tested -- the spec is explicit
/// that getting it wrong restores zero notes after an exit.
/// </summary>
public interface INoteWindow : IDisposable
{
    string NotePath { get; }

    /// <summary>Physical screen pixels, read straight from the OS.</summary>
    PixelRect Bounds { get; }

    void ShowNote(bool activate);
    void HideNote();
    void FocusNote();
    void ApplyState(NoteState state, NoteTheme theme);

    /// <summary>An external edit arrived and the buffer was clean.</summary>
    void ApplyExternalContent(string text, string diskHash);

    void NotifyFileDeleted();
    void NotifyRenamed(string canonicalPath);
    void SaveNow();

    /// <summary>
    /// The user clicked the close glyph. THE ONLY event that may clear
    /// <c>isOpen</c>.
    /// </summary>
    event Action<string>? CloseRequested;

    event Action<string>? DeleteRequested;

    /// <summary>Canonical current path, requested new file name.</summary>
    event Action<string, string>? RenameRequested;

    /// <summary>A relative .md link was clicked. Canonical target path.</summary>
    event Action<string>? OpenNoteRequested;

    /// <summary>Colour, opacity, pin, or geometry changed.</summary>
    event Action<string, NoteState>? StateChanged;
}

public sealed partial class NoteWindow : Window, INoteWindow
{
    private readonly MarkdownRenderer _renderer = new();
    private readonly InlineBarHost _bars;

    private WebViewHost _web;
    private NoteState _state;
    private NoteTheme _theme;
    private string _buffer = string.Empty;
    private string _diskHash = string.Empty;
    private bool _webReady;
    private bool _disposed;

    public NoteWindow(string canonicalPath, NoteState state, NoteTheme theme)
    {
        InitializeComponent();

        NotePath = canonicalPath;
        _state = state;
        _theme = theme;

        _bars = new InlineBarHost(BarStack);
        _web = new WebViewHost(canonicalPath);

        WebViewSlot.Content = _web.Control;

        WireHeader();
        ApplyTheme(theme);

        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
    }

    public string NotePath { get; private set; }

    public PixelRect Bounds => _handle == IntPtr.Zero
        ? new PixelRect(_state.X, _state.Y, _state.W, _state.H)
        : WindowGeometry.GetBounds(_handle);

    public event Action<string>? CloseRequested;
    public event Action<string>? DeleteRequested;
    public event Action<string, string>? RenameRequested;
    public event Action<string>? OpenNoteRequested;
    public event Action<string, NoteState>? StateChanged;

    private IntPtr _handle = IntPtr.Zero;

    private void WireHeader()
    {
        // ============================================================
        // THE DRAG HANDLER IS ON THE HEADER ELEMENT. NEVER ON THE WINDOW.
        //
        // MouseLeftButtonDown BUBBLES. A handler on the Window calls
        // DragMove() for every left-click anywhere, INCLUDING over the note
        // content, and swallows the mouse-down before the WebView receives it.
        //
        // Observed symptoms, none of which name the cause:
        //   - task checkboxes silently stop toggling (onclick never fires)
        //   - scrollbar thumb drags move the window instead of scrolling
        //   - text selection inside a note becomes impossible
        //
        // There is no exception, no warning, and no crash. The note simply
        // stops responding to clicks. Spike 0 initially misattributed this to
        // the composition control.
        // ============================================================
        Header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;

            try { DragMove(); }
            catch (InvalidOperationException)
            {
                // Raised when the button was already released. Harmless.
            }
        };

        Header.MouseEnter += (_, _) => FadeHeaderButtons(1.0);
        Header.MouseLeave += (_, _) => FadeHeaderButtons(0.0);

        CloseButton.Click += (_, _) => CloseRequested?.Invoke(NotePath);

        PinButton.Click += (_, _) =>
        {
            _state = _state with { AlwaysOnTop = !_state.AlwaysOnTop };
            Topmost = _state.AlwaysOnTop;
            StateChanged?.Invoke(NotePath, _state);
        };

        // Colour, Opacity, Rename, Delete and the edit toggle are wired in
        // Tasks 10 and 12. Left inert here so this task's deliverable is a
        // window that opens, drags, resizes, and renders.
    }

    private void FadeHeaderButtons(double to)
        => HeaderButtons.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(to, TimeSpan.FromMilliseconds(120)));

    private void ApplyTheme(NoteTheme theme)
    {
        _theme = theme;

        Root.Background = new SolidColorBrush(Parse(theme.ContentBg));
        Root.BorderBrush = new SolidColorBrush(Parse(theme.Border));
        Header.Background = new SolidColorBrush(Parse(theme.ChromeBg));
        TitleText.Foreground = new SolidColorBrush(Parse(theme.ChromeFg));
        Editor.Background = new SolidColorBrush(Parse(theme.ContentBg));
        Editor.Foreground = new SolidColorBrush(Parse(theme.ContentFg));
        Editor.CaretBrush = new SolidColorBrush(Parse(theme.Accent));

        _bars.ApplyTheme(theme);
    }

    private static Color Parse(string hex)
        => (Color)ColorConverter.ConvertFromString(hex)!;

    private async void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;

        // Rounded corners come from DWM only. Per-pixel transparency is
        // unavailable because the WebView backdrop must be opaque, so a
        // clipped-geometry approach would round the chrome and leave the note
        // body square.
        DwmCorners.Round(_handle);

        // Geometry in PHYSICAL pixels, via SetWindowPos. Assigning Left/Top
        // would go through WPF's DIP conversion against the PRIMARY monitor's
        // DPI, which is the bug that puts a note saved at 150% in the wrong
        // place at 100%.
        WindowGeometry.SetBounds(
            _handle, new PixelRect(_state.X, _state.Y, _state.W, _state.H));

        Topmost = _state.AlwaysOnTop;
        Opacity = _state.Opacity;

        await InitialiseWebAsync().ConfigureAwait(true);
    }

    private async Task InitialiseWebAsync()
    {
        _web.TaskToggleRequested += OnTaskToggleRequested;
        _web.LinkClicked += OnLinkClicked;
        _web.EditRequested += OnEditRequested;
        _web.FellBackToPlainText += OnFellBackToPlainText;

        var directory = Path.GetDirectoryName(NotePath) ?? string.Empty;

        await _web.InitializeAsync(
            directory,
            _theme,
            allowRemoteImages: false,
            backdrop: System.Drawing.ColorTranslator.FromHtml(_theme.ContentBg))
            .ConfigureAwait(true);

        _webReady = true;

        LoadFromDisk();
        await RenderAsync().ConfigureAwait(true);
    }

    private void LoadFromDisk()
    {
        try
        {
            var content = NoteFile.Read(NotePath);
            _buffer = content.Text;
            _diskHash = content.ContentHash;
        }
        catch (FileNotFoundException) { _buffer = string.Empty; }
        catch (DirectoryNotFoundException) { _buffer = string.Empty; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.Text.DecoderFallbackException)
        {
            // Task 12 turns each of these into its own bar. Until then, keep
            // the buffer empty rather than half-read -- a partial buffer that
            // later autosaves would overwrite the file with less than it had.
            _buffer = string.Empty;
            Core.Diagnostics.DiagnosticsLog.Write(
                AppPaths.DiagnosticsFile, $"{NotePath}: could not be read -- {ex.Message}");
        }

        TitleText.Text = NoteTitleResolver.Resolve(_buffer, NotePath);
    }

    private async Task RenderAsync()
    {
        if (!_webReady) return;

        var result = _renderer.Render(
            _buffer, new RenderOptions(AllowRemoteImages: false));

        await _web.RenderAsync(result).ConfigureAwait(true);
    }

    // Handlers filled in by Tasks 10-12. Present now so the events have
    // somewhere to go and the file compiles as one piece.
    private void OnTaskToggleRequested(int spanStart, int spanEnd, string? token) { }

    private void OnLinkClicked(string? href) { }

    private void OnEditRequested() { }

    private void OnFellBackToPlainText(string message) { }

    public void ShowNote(bool activate)
    {
        // ShowActivated must be set BEFORE the window is first shown --
        // afterwards it is ignored. Restoring a screenful of notes at logon
        // with activation on makes them fight the logon sequence for focus.
        if (!IsVisible) ShowActivated = activate;

        Show();

        if (activate) Activate();
    }

    public void HideNote()
    {
        // Hide, never Close. Hide All must not touch isOpen, and closing the
        // window would run the Closing path.
        Hide();
    }

    public void FocusNote()
    {
        if (!IsVisible) ShowNote(activate: true);
        Activate();
    }

    public void ApplyState(NoteState state, NoteTheme theme)
    {
        _state = state;

        Topmost = state.AlwaysOnTop;
        Opacity = state.Opacity;

        ApplyTheme(theme);

        if (_handle != IntPtr.Zero)
        {
            WindowGeometry.SetBounds(
                _handle, new PixelRect(state.X, state.Y, state.W, state.H));
        }

        if (_webReady)
        {
            _ = _web.SetThemeAsync(
                theme,
                allowRemoteImages: false,
                System.Drawing.ColorTranslator.FromHtml(theme.ContentBg));
        }
    }

    public void ApplyExternalContent(string text, string diskHash)
    {
        _buffer = text;
        _diskHash = diskHash;
        TitleText.Text = NoteTitleResolver.Resolve(_buffer, NotePath);
        _ = RenderAsync();
    }

    public void NotifyFileDeleted() { /* Task 12 */ }

    public void NotifyRenamed(string canonicalPath)
    {
        NotePath = canonicalPath;
        TitleText.Text = NoteTitleResolver.Resolve(_buffer, NotePath);

        // Remap note.local, or images in the moved note silently stop loading.
        if (_webReady)
            _web.RemapNoteDirectory(Path.GetDirectoryName(canonicalPath) ?? string.Empty);
    }

    public void SaveNow() { /* Task 10 */ }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // GEOMETRY ONLY. This runs during application shutdown, Windows
        // logoff, and ordinary disposal, and WPF closes EVERY window on
        // shutdown. If the close-glyph logic lived here, choosing Exit would
        // clear isOpen on every note and the next boot would restore none of
        // them -- exactly the failure the three-state model exists to prevent,
        // and a direct breach of success criterion 3.
        //
        // The close glyph goes through CloseRequested -> WindowManager
        // instead.
        if (_handle == IntPtr.Zero) return;

        var bounds = WindowGeometry.GetBounds(_handle);

        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        _state = _state with
        {
            X = bounds.X, Y = bounds.Y, W = bounds.Width, H = bounds.Height,
        };

        StateChanged?.Invoke(NotePath, _state);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _web.TaskToggleRequested -= OnTaskToggleRequested;
        _web.LinkClicked -= OnLinkClicked;
        _web.EditRequested -= OnEditRequested;
        _web.FellBackToPlainText -= OnFellBackToPlainText;

        _web.Dispose();

        SourceInitialized -= OnSourceInitialized;
        Closing -= OnClosing;

        Close();
    }
}
```

The `HeaderGlyph` style referenced by the XAML has not been defined yet. Add it to `App.xaml` so every note picks it up:

```xml
<Application x:Class="StickyMD.App.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             ShutdownMode="OnExplicitShutdown">
  <Application.Resources>
    <Style x:Key="HeaderGlyph" TargetType="Button">
      <Setter Property="Width" Value="20" />
      <Setter Property="Height" Value="20" />
      <Setter Property="Padding" Value="0" />
      <Setter Property="FontSize" Value="11" />
      <Setter Property="Background" Value="Transparent" />
      <Setter Property="BorderThickness" Value="0" />
      <Setter Property="Cursor" Value="Arrow" />
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="Button">
            <Border x:Name="Chrome" Background="{TemplateBinding Background}" CornerRadius="3">
              <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" />
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property="IsMouseOver" Value="True">
                <Setter TargetName="Chrome" Property="Background" Value="#25000000" />
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
  </Application.Resources>
</Application>
```

- [ ] **Step 4: Replace the temporary bootstrap with one that opens a real note**

Still temporary — Task 14 writes the real one — but now it puts a note on screen.

`src/StickyMD.App/App.xaml.cs`:

```csharp
using System.Windows;
using StickyMD.App.Services;
using StickyMD.App.Windows;
using StickyMD.Core.Notes;
using StickyMD.Core.Persistence;
using StickyMD.Core.Theming;

namespace StickyMD.App;

public partial class App : Application
{
    // TEMPORARY (Plan B only). Task 14 replaces all of this with the real
    // bootstrap: settings, index, watcher, WindowManager, recovery.
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (WebViewEnvironment.DetectRuntimeVersion() is null)
        {
            MessageBox.Show(
                "The WebView2 runtime is not installed.",
                "StickyMD", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        var repository = new NoteRepository(
            AppSettings.DefaultNotesRoot, new SystemClock());
        repository.EnsureRootExists();

        var existing = repository.EnumerateRoot();
        var path = existing.Count > 0 ? existing[0] : repository.CreateNewRecorded().Path;

        var state = new NoteState(
            X: 200, Y: 200, W: 320, H: 420,
            Monitor: null,
            Color: NoteColor.Yellow,
            Opacity: 0.95,
            AlwaysOnTop: true,
            IsOpen: true,
            LastOpenedUtc: DateTime.UtcNow);

        var window = new NoteWindow(
            path, state, NotePalette.Get(state.Color, ThemeMode.Light));

        window.CloseRequested += _ => Shutdown();

        window.ShowNote(activate: true);
    }
}
```

- [ ] **Step 5: Build**

```bash
dotnet build
```

Expected: clean. Common first failures:

- `WindowChrome` not found — the `shell:` xmlns must be `clr-namespace:System.Windows.Shell;assembly=PresentationFramework`.
- `System.Drawing.ColorTranslator` not found — it lives in `System.Drawing.Common`, which flows in with `UseWPF`; if it does not resolve, parse the hex manually rather than adding a package.

- [ ] **Step 6: Put a note on the screen and verify it by hand**

```bash
dotnet run --project src/StickyMD.App/StickyMD.App.csproj
```

Before running, put some real Markdown in the note the app will pick up — `%USERPROFILE%\StickyMD Notes\` first `.md` file:

```markdown
# Standup

- [ ] unblock the deploy
- [x] review the spec

| what | when |
| ---- | ---- |
| ship | soon |

Some `inline code` and a [link](https://example.com).

> a quote
```

**Check every one of these. Each maps to something Spike 0 verified, and any failure means a specific setting was undone:**

- [ ] The note appears at roughly (200, 200), about 320×420, **on top of other windows**.
- [ ] **Content renders** — heading, table, checkboxes, inline code, blockquote. _Nothing rendering at all means the plain `WebView2` snuck in, or the TFM lost its version suffix._
- [ ] The note is **visibly translucent** at 95%. _Fully opaque means `AllowsTransparency` is not `True` in XAML._
- [ ] Colours match the palette's yellow — header darker than body. _A white body means the theme never reached the shell._
- [ ] There is **no white flash** when it first paints. _A flash means `DefaultBackgroundColor` is being set after `EnsureCoreWebView2Async`._
- [ ] **Dragging the header moves the window.**
- [ ] **Dragging edges and corners resizes it.** _No resize at all means `WindowChrome` is missing — `AllowsTransparency=True` removes WPF's resize frame entirely._
- [ ] Hovering the header **reveals the five glyphs**; leaving hides them.
- [ ] **The mouse wheel scrolls the note content.**
- [ ] **The scrollbar thumb drags and scrolls** — it does not move the window.
- [ ] **Text inside the note can be selected** by click-dragging.
- [ ] **Corners are rounded.**
- [ ] Right-clicking inside the note shows **no browser context menu**.
- [ ] `F12` does **nothing** — dev tools are off.
- [ ] `Ctrl+scroll` does **not** zoom.
- [ ] Clicking the `✕` glyph exits the app (temporary wiring).

> **If the last three body checks fail together — scrollbar drags move the window, text will not select, and clicking a checkbox does nothing — the drag handler is on the `Window` instead of the `Header`.** That is the single most likely mistake in this task and it produces no error of any kind. Re-read `WireHeader`.

- [ ] **Step 7: Move the note to the second monitor and confirm it survives**

Drag the note to `\\.\DISPLAY2`, then close and re-run. It will reappear at (200, 200) on the primary — geometry is not yet persisted, which is Task 13. What matters here is that it **renders correctly on the second monitor** while it is there.

- [ ] **Step 8: Commit**

Hand these to the user:

```bash
git add src/StickyMD.App
git commit -m "Add the NoteWindow chrome: a real note on screen

Three settings in this window are load-bearing and look optional.

AllowsTransparency=True must be in XAML. WS_EX_LAYERED cannot be added to
an existing WPF window on Windows 11 26200 -- SetWindowLong returns the
previous ex-style with lastErr = 0, a textbook success, and changes
nothing; SetLayeredWindowAttributes then fails with
ERROR_INVALID_PARAMETER. WPF applies the style at CreateWindowEx, the one
moment Windows permits it, and only for a window declared this way.

WindowChrome is mandatory, not cosmetic. AllowsTransparency=True removes
WPF's resize frame entirely, so without it a note cannot be resized at
all. CaptionHeight=0 makes the whole window client area so the header
buttons behave normally.

The drag handler is on the Header element and must never move to the
Window. MouseLeftButtonDown bubbles, so a Window-level handler calls
DragMove() for every left-click anywhere -- including over the note --
and swallows the mouse-down before the WebView sees it. Checkboxes stop
toggling, scrollbar drags move the window, and text selection dies, with
no exception, warning, or crash. Spike 0 initially blamed the composition
control for this.

Closing persists geometry and NOTHING else. WPF closes every window
during application shutdown, so close-glyph logic here would mean choosing
Exit clears isOpen on every note and the next boot restores none of them.
The glyph routes through CloseRequested instead.

The inline bars overlay note content, which is legitimate only because
WebView2CompositionControl renders through D3DImage rather than an
HwndHost -- airspace does not apply. An earlier draft of the spec said the
opposite, describing the plain control's behaviour.

Geometry goes through SetWindowPos in physical pixels rather than
Left/Top, and ShowActivated is set before the first Show because it is
ignored afterwards -- restoring a screenful of notes at logon must not
fight the logon sequence for focus."
```

---

## Task 10: The mode toggle, the editor conveniences, and `SaveCoordinator`

The note becomes editable. `SaveCoordinator` is the one piece here with real logic and it is fully tested against fakes: the debounce, the 3-retry schedule, the recovery snapshot, and the ledger record.

**Files:**

- Create: `src/StickyMD.App/Services/NoteFileGateway.cs`
- Create: `src/StickyMD.App/Services/SaveCoordinator.cs`
- Test: `tests/StickyMD.App.Tests/TestSupport/FakeNoteFileGateway.cs`
- Test: `tests/StickyMD.App.Tests/Services/SaveCoordinatorTests.cs`
- Modify: `src/StickyMD.App/Windows/NoteWindow.xaml.cs`

**Interfaces:**

- Consumes: `NoteFile`, `NoteFormat`, `IWriteLedger`, `RecoveryStore`, `MarkdownEditOps`, `DiagnosticsLog`.
- Produces:
  - `interface INoteFileGateway` with `NoteContent Read(string path)`, `NoteFile.WriteOutcome Write(string path, string text, NoteFormat format)`
  - `sealed class NoteFileGateway : INoteFileGateway`
  - `enum SaveStatus { Saved, NothingToDo, Failed }`
  - `sealed record SaveOutcome(SaveStatus Status, string? DiskHash, string? Message, int Attempts)`
  - `sealed class SaveCoordinator` with `MarkDirty(string text)`, `Task<SaveOutcome> FlushAsync()`, `bool IsDirty`, `event Action<SaveOutcome>? Saved`
  - `NoteWindow.EnterEditMode()` / `NoteWindow.ExitEditModeAsync()`

Task 11 calls `FlushAsync` after a checkbox toggle. Task 12 shows the bar for a `Failed` outcome. Task 13 calls `SaveNow` before disposing a window.

### The retry schedule, and why the buffer never leaves memory

Spec §8: retry 3× at 100/300/900ms, then an amber bar, and a recovery snapshot. The ordering matters:

1. The buffer stays in memory and in the editor **throughout**. Nothing is cleared on failure.
2. The snapshot is written **after** the retries are exhausted, not before — a transient OneDrive lock that clears on attempt two must not leave a stale snapshot behind.
3. The snapshot is cleared on the **next successful save**, and `RecoveryStore.Clear` returning `false` is recorded rather than ignored, because a snapshot that outlives its save would later offer older text over newer.

- [ ] **Step 1: Write the fake gateway**

`tests/StickyMD.App.Tests/TestSupport/FakeNoteFileGateway.cs`:

```csharp
using StickyMD.App.Services;
using StickyMD.Core.Notes;

namespace StickyMD.App.Tests.TestSupport;

/// <summary>
/// A note file in memory, with scriptable failures. Lets the retry schedule be
/// tested without a real lock, a real antivirus, or a real OneDrive.
/// </summary>
public sealed class FakeNoteFileGateway : INoteFileGateway
{
    private readonly Queue<Exception> _writeFailures = new();

    public string Content { get; set; } = string.Empty;

    public NoteFormat Format { get; set; } = NoteFormat.Canonical;

    public int WriteAttempts { get; private set; }

    public List<string> WrittenTexts { get; } = [];

    /// <summary>Fail the next <paramref name="count"/> writes.</summary>
    public void FailNextWrites(int count, Exception? with = null)
    {
        for (var i = 0; i < count; i++)
            _writeFailures.Enqueue(with ?? new IOException("the file is locked"));
    }

    public NoteContent Read(string path)
        => new(Content, Format, NoteFile.Sha256(
            System.Text.Encoding.UTF8.GetBytes(Content)));

    public NoteFile.WriteOutcome Write(string path, string text, NoteFormat format)
    {
        WriteAttempts++;

        if (_writeFailures.Count > 0) throw _writeFailures.Dequeue();

        Content = text;
        WrittenTexts.Add(text);

        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        return new NoteFile.WriteOutcome(
            bytes.LongLength,
            new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc),
            NoteFile.Sha256(bytes));
    }
}
```

- [ ] **Step 2: Write the failing `SaveCoordinator` tests**

`tests/StickyMD.App.Tests/Services/SaveCoordinatorTests.cs`:

```csharp
using Shouldly;
using StickyMD.App.Services;
using StickyMD.App.Tests.TestSupport;
using StickyMD.Core.Notes;
using StickyMD.Core.Persistence;

namespace StickyMD.App.Tests.Services;

public class SaveCoordinatorTests
{
    private const string NotePath = @"C:\Notes\standup.md";

    private static (SaveCoordinator Coordinator,
                    FakeNoteFileGateway Gateway,
                    WriteLedger Ledger,
                    RecoveryStore Recovery,
                    TempRecoveryDir Dir) Build(
        IReadOnlyList<int>? backoff = null)
    {
        var dir = new TempRecoveryDir();
        var gateway = new FakeNoteFileGateway();
        var ledger = new WriteLedger();
        var recovery = new RecoveryStore(dir.Path);

        var coordinator = new SaveCoordinator(
            NotePath, gateway, ledger, recovery,
            diagnosticsFile: Path.Combine(dir.Path, "diagnostics.log"),
            // Zeros so the retry SCHEDULE is exercised without the test
            // waiting 1.3 seconds for it.
            retryBackoffMs: backoff ?? [0, 0, 0]);

        return (coordinator, gateway, ledger, recovery, dir);
    }

    /// <summary>A temp directory for recovery snapshots.</summary>
    private sealed class TempRecoveryDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "stickymd-save", Guid.NewGuid().ToString("N"));

        public TempRecoveryDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task A_flush_with_no_changes_does_not_write()
    {
        var (coordinator, gateway, _, _, dir) = Build();
        using var _d = dir;

        var outcome = await coordinator.FlushAsync();

        outcome.Status.ShouldBe(SaveStatus.NothingToDo);
        gateway.WriteAttempts.ShouldBe(0);
    }

    [Fact]
    public async Task A_dirty_buffer_is_written_once()
    {
        var (coordinator, gateway, _, _, dir) = Build();
        using var _d = dir;

        coordinator.MarkDirty("# hello");
        var outcome = await coordinator.FlushAsync();

        outcome.Status.ShouldBe(SaveStatus.Saved);
        outcome.Attempts.ShouldBe(1);
        gateway.WrittenTexts.ShouldBe(["# hello"]);
    }

    [Fact]
    public async Task A_second_flush_after_a_successful_save_writes_nothing()
    {
        var (coordinator, gateway, _, _, dir) = Build();
        using var _d = dir;

        coordinator.MarkDirty("# hello");
        await coordinator.FlushAsync();
        var second = await coordinator.FlushAsync();

        second.Status.ShouldBe(SaveStatus.NothingToDo);
        gateway.WriteAttempts.ShouldBe(1);
    }

    [Fact]
    public async Task Only_the_latest_text_is_written()
    {
        var (coordinator, gateway, _, _, dir) = Build();
        using var _d = dir;

        coordinator.MarkDirty("one");
        coordinator.MarkDirty("two");
        coordinator.MarkDirty("three");
        await coordinator.FlushAsync();

        gateway.WrittenTexts.ShouldBe(["three"]);
    }

    [Fact]
    public async Task A_successful_save_records_the_write_in_the_ledger()
    {
        var (coordinator, gateway, ledger, _, dir) = Build();
        using var _d = dir;

        coordinator.MarkDirty("# hello");
        var outcome = await coordinator.FlushAsync();

        // Without this the watcher reports StickyMD's own save as an external
        // change, and the note reloads itself in a loop.
        var fingerprint = ledger.Peek(NotePath);
        fingerprint.ShouldNotBeNull();
        fingerprint.ContentHash.ShouldBe(outcome.DiskHash);
    }

    [Fact]
    public async Task Two_failures_then_success_reports_three_attempts()
    {
        var (coordinator, gateway, _, _, dir) = Build();
        using var _d = dir;

        gateway.FailNextWrites(2);
        coordinator.MarkDirty("# hello");

        var outcome = await coordinator.FlushAsync();

        outcome.Status.ShouldBe(SaveStatus.Saved);
        outcome.Attempts.ShouldBe(3);
        gateway.Content.ShouldBe("# hello");
    }

    [Fact]
    public async Task Four_failures_exhausts_the_retries_and_reports_failure()
    {
        var (coordinator, gateway, _, _, dir) = Build();
        using var _d = dir;

        // One initial attempt plus three retries = four writes.
        gateway.FailNextWrites(4);
        coordinator.MarkDirty("# hello");

        var outcome = await coordinator.FlushAsync();

        outcome.Status.ShouldBe(SaveStatus.Failed);
        outcome.Attempts.ShouldBe(4);
        outcome.Message.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task An_exhausted_save_writes_a_recovery_snapshot_holding_the_buffer()
    {
        var (coordinator, gateway, _, recovery, dir) = Build();
        using var _d = dir;

        gateway.FailNextWrites(4);
        coordinator.MarkDirty("text that must not be lost");

        await coordinator.FlushAsync();

        var envelope = recovery.TryLoad(NotePath);
        envelope.ShouldNotBeNull();
        envelope.Content.ShouldBe("text that must not be lost");
        envelope.OriginalPath.ShouldBe(NotePath);
    }

    [Fact]
    public async Task A_transient_failure_that_clears_leaves_no_snapshot_behind()
    {
        var (coordinator, gateway, _, recovery, dir) = Build();
        using var _d = dir;

        // Snapshots are written only AFTER the retries are exhausted. A
        // OneDrive lock that clears on attempt two must not leave stale text
        // that a later startup would offer to restore.
        gateway.FailNextWrites(2);
        coordinator.MarkDirty("# hello");

        await coordinator.FlushAsync();

        recovery.TryLoad(NotePath).ShouldBeNull();
    }

    [Fact]
    public async Task A_successful_save_clears_an_earlier_snapshot()
    {
        var (coordinator, gateway, _, recovery, dir) = Build();
        using var _d = dir;

        gateway.FailNextWrites(4);
        coordinator.MarkDirty("first attempt");
        await coordinator.FlushAsync();
        recovery.TryLoad(NotePath).ShouldNotBeNull();

        coordinator.MarkDirty("second attempt");
        var outcome = await coordinator.FlushAsync();

        outcome.Status.ShouldBe(SaveStatus.Saved);
        recovery.TryLoad(NotePath).ShouldBeNull();
    }

    [Fact]
    public async Task The_buffer_survives_a_failed_save_so_a_retry_can_still_write_it()
    {
        var (coordinator, gateway, _, _, dir) = Build();
        using var _d = dir;

        gateway.FailNextWrites(4);
        coordinator.MarkDirty("precious");
        await coordinator.FlushAsync();

        coordinator.IsDirty.ShouldBeTrue("never lose text");

        var retry = await coordinator.FlushAsync();

        retry.Status.ShouldBe(SaveStatus.Saved);
        gateway.Content.ShouldBe("precious");
    }

    [Fact]
    public async Task An_unauthorized_access_failure_is_retried_like_an_io_one()
    {
        var (coordinator, gateway, _, _, dir) = Build();
        using var _d = dir;

        gateway.FailNextWrites(2, new UnauthorizedAccessException("AV scan"));
        coordinator.MarkDirty("# hello");

        (await coordinator.FlushAsync()).Status.ShouldBe(SaveStatus.Saved);
    }

    [Fact]
    public async Task An_encoding_failure_is_NOT_retried()
    {
        var (coordinator, gateway, _, recovery, dir) = Build();
        using var _d = dir;

        // A lone surrogate in the buffer throws from NoteFile's strict
        // encoder, and it will throw identically on every retry. Retrying is
        // three wasted delays before the same answer -- but the snapshot still
        // has to be written, because that text cannot reach the file at all.
        gateway.FailNextWrites(1, new System.Text.EncoderFallbackException());
        coordinator.MarkDirty("bad \ud800 text");

        var outcome = await coordinator.FlushAsync();

        outcome.Status.ShouldBe(SaveStatus.Failed);
        outcome.Attempts.ShouldBe(1);
        recovery.TryLoad(NotePath).ShouldNotBeNull();
    }

    [Fact]
    public async Task The_Saved_event_fires_with_the_outcome()
    {
        var (coordinator, _, _, _, dir) = Build();
        using var _d = dir;

        SaveOutcome? seen = null;
        coordinator.Saved += o => seen = o;

        coordinator.MarkDirty("# hello");
        await coordinator.FlushAsync();

        seen.ShouldNotBeNull();
        seen.Status.ShouldBe(SaveStatus.Saved);
    }

    [Fact]
    public async Task The_on_disk_format_is_preserved_across_a_save()
    {
        var (coordinator, gateway, _, _, dir) = Build();
        using var _d = dir;

        gateway.Format = new NoteFormat(
            NoteEncoding.Utf8Bom, NoteNewline.Lf, TrailingNewline: false);

        coordinator.MarkDirty("# hello");
        await coordinator.FlushAsync();

        // The coordinator must pass through whatever Read reported, or every
        // save silently reformats the user's file.
        coordinator.Format.ShouldBe(gateway.Format);
    }

    [Fact]
    public async Task Concurrent_flushes_do_not_write_twice()
    {
        var (coordinator, gateway, _, _, dir) = Build();
        using var _d = dir;

        coordinator.MarkDirty("# hello");

        var a = coordinator.FlushAsync();
        var b = coordinator.FlushAsync();
        await Task.WhenAll(a, b);

        // The 500ms debounce timer and an explicit flush on blur can land
        // together. Two concurrent AtomicWrites to one path race over the same
        // temp file.
        gateway.WriteAttempts.ShouldBe(1);
    }
}
```

- [ ] **Step 3: Run them to verify they fail**

```bash
dotnet test --filter FullyQualifiedName~SaveCoordinatorTests
```

Expected: FAIL to compile.

- [ ] **Step 4: Write `NoteFileGateway` and `SaveCoordinator`**

`src/StickyMD.App/Services/NoteFileGateway.cs`:

```csharp
using StickyMD.Core.Notes;

namespace StickyMD.App.Services;

/// <summary>
/// Note file I/O behind a seam, so <see cref="SaveCoordinator"/>'s retry
/// schedule can be tested against scripted failures rather than a real lock,
/// a real antivirus scan, or a real OneDrive.
/// </summary>
public interface INoteFileGateway
{
    NoteContent Read(string path);

    NoteFile.WriteOutcome Write(string path, string text, NoteFormat format);
}

public sealed class NoteFileGateway : INoteFileGateway
{
    public NoteContent Read(string path) => NoteFile.Read(path);

    public NoteFile.WriteOutcome Write(string path, string text, NoteFormat format)
        => NoteFile.AtomicWrite(path, text, format);
}
```

`src/StickyMD.App/Services/SaveCoordinator.cs`:

```csharp
using StickyMD.Core.Diagnostics;
using StickyMD.Core.Notes;
using StickyMD.Core.Persistence;

namespace StickyMD.App.Services;

public enum SaveStatus { Saved, NothingToDo, Failed }

/// <param name="DiskHash">The saved content's hash, or null when nothing was written.</param>
/// <param name="Attempts">Total write attempts, including the first.</param>
public sealed record SaveOutcome(
    SaveStatus Status, string? DiskHash, string? Message, int Attempts);

/// <summary>
/// Owns one note's unsaved buffer and everything that happens when it is
/// written: the retry schedule, the write ledger, and the recovery snapshot.
/// </summary>
/// <remarks>
/// THE BUFFER NEVER LEAVES MEMORY ON FAILURE. IsDirty stays true and the text
/// stays here, so the amber bar's Retry has something to write and closing the
/// note still has something to snapshot. "Never lose text" is this class's
/// whole job.
///
/// SNAPSHOTS ARE WRITTEN AFTER THE RETRIES ARE EXHAUSTED, not before. A
/// OneDrive lock that clears on attempt two would otherwise leave a stale
/// snapshot that a later startup offers to restore -- older text presented as
/// a recovery.
///
/// AN ENCODING FAILURE IS NOT RETRIED. NoteFile encodes strictly, so a lone
/// surrogate throws before any file is touched and will throw identically
/// three more times. The snapshot is still written: that text cannot reach the
/// file at all, which makes it exactly the case recovery exists for.
/// </remarks>
public sealed class SaveCoordinator
{
    private readonly string _notePath;
    private readonly INoteFileGateway _gateway;
    private readonly IWriteLedger _ledger;
    private readonly RecoveryStore _recovery;
    private readonly string _diagnosticsFile;
    private readonly IReadOnlyList<int> _retryBackoffMs;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string _buffer = string.Empty;
    private bool _dirty;

    /// <param name="retryBackoffMs">
    /// Spec §8: 100, 300, 900. Injected so tests exercise the schedule without
    /// waiting 1.3 seconds for it.
    /// </param>
    public SaveCoordinator(
        string notePath,
        INoteFileGateway gateway,
        IWriteLedger ledger,
        RecoveryStore recovery,
        string diagnosticsFile,
        IReadOnlyList<int>? retryBackoffMs = null)
    {
        _notePath = notePath;
        _gateway = gateway;
        _ledger = ledger;
        _recovery = recovery;
        _diagnosticsFile = diagnosticsFile;
        _retryBackoffMs = retryBackoffMs ?? [100, 300, 900];
    }

    /// <summary>
    /// The on-disk format to reproduce. Set from a read; defaults to canonical
    /// for a note StickyMD created.
    /// </summary>
    public NoteFormat Format { get; set; } = NoteFormat.Canonical;

    /// <summary>The last hash successfully written or read.</summary>
    public string? DiskHash { get; private set; }

    public bool IsDirty => _dirty;

    public string Buffer => _buffer;

    public event Action<SaveOutcome>? Saved;

    /// <summary>Adopts content read from disk. Does not mark the buffer dirty.</summary>
    public void AdoptFromDisk(NoteContent content)
    {
        _buffer = content.Text;
        Format = content.Format;
        DiskHash = content.ContentHash;
        _dirty = false;
    }

    public void MarkDirty(string text)
    {
        _buffer = text;
        _dirty = true;
    }

    public async Task<SaveOutcome> FlushAsync()
    {
        // Serialised, because the 500ms debounce timer and an explicit flush
        // on blur or close can land together -- and two concurrent
        // AtomicWrites to one path race over the same temp file.
        await _gate.WaitAsync().ConfigureAwait(true);

        try
        {
            if (!_dirty)
                return Report(new SaveOutcome(SaveStatus.NothingToDo, DiskHash, null, 0));

            var text = _buffer;
            var attempts = 0;
            Exception? last = null;

            for (var i = 0; i <= _retryBackoffMs.Count; i++)
            {
                attempts++;

                try
                {
                    var outcome = _gateway.Write(_notePath, text, Format);

                    // Record BEFORE anything else. This is what lets
                    // NoteWatcher tell our own write from an external edit; a
                    // save that is not recorded comes straight back as an
                    // external change and the note reloads itself.
                    _ledger.Record(_notePath, outcome);

                    DiskHash = outcome.ContentHash;

                    // Only clear dirty if the buffer has not moved on. The
                    // user may have typed during the await.
                    if (string.Equals(_buffer, text, StringComparison.Ordinal))
                        _dirty = false;

                    ClearSnapshot();

                    return Report(new SaveOutcome(
                        SaveStatus.Saved, outcome.ContentHash, null, attempts));
                }
                catch (Exception ex) when (IsRetryable(ex))
                {
                    last = ex;

                    if (i < _retryBackoffMs.Count)
                        await Task.Delay(_retryBackoffMs[i]).ConfigureAwait(true);
                }
                catch (Exception ex) when (IsPermanent(ex))
                {
                    // Retrying is three wasted delays before the same answer.
                    last = ex;
                    break;
                }
            }

            return Report(Fail(text, attempts, last));
        }
        finally
        {
            _gate.Release();
        }
    }

    private SaveOutcome Fail(string text, int attempts, Exception? cause)
    {
        // The buffer stays. _dirty stays true. Retry, Save As, and the
        // on-close snapshot all depend on it still being here.
        WriteSnapshot(text);

        var message = cause?.Message ?? "the file could not be written";

        DiagnosticsLog.Write(
            _diagnosticsFile,
            $"{_notePath}: save failed after {attempts} attempt(s) -- {message}");

        return new SaveOutcome(SaveStatus.Failed, DiskHash, message, attempts);
    }

    private void WriteSnapshot(string text)
    {
        try
        {
            _recovery.Save(new RecoveryEnvelope(
                _notePath, text, DateTime.UtcNow, DiskHash ?? string.Empty));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The buffer is still in memory and still in the editor, so this
            // is a degraded outcome rather than a lost one -- but it means a
            // crash from here would lose the text, which is worth recording.
            DiagnosticsLog.Write(
                _diagnosticsFile,
                $"{_notePath}: could not write a recovery snapshot -- {ex.Message}");
        }
    }

    private void ClearSnapshot()
    {
        if (_recovery.Clear(_notePath)) return;

        // Recorded, not ignored. A snapshot that outlives its successful save
        // would be offered on a later startup and would present OLDER text as
        // a recovery. The startup path compares LastKnownDiskHash for exactly
        // this reason.
        DiagnosticsLog.Write(
            _diagnosticsFile,
            $"{_notePath}: a recovery snapshot could not be removed after a successful save.");
    }

    private SaveOutcome Report(SaveOutcome outcome)
    {
        Saved?.Invoke(outcome);
        return outcome;
    }

    /// <summary>
    /// Transient: a lock, an antivirus scan, a sync client holding the file.
    /// Worth waiting for.
    /// </summary>
    private static bool IsRetryable(Exception ex)
        => ex is IOException or UnauthorizedAccessException;

    /// <summary>
    /// Deterministic: the same buffer will fail the same way every time.
    /// NoteFile encodes strictly, so a lone surrogate throws before any file
    /// is touched.
    /// </summary>
    private static bool IsPermanent(Exception ex)
        => ex is System.Text.EncoderFallbackException
            or ArgumentException
            or NotSupportedException;
}
```

- [ ] **Step 5: Run the coordinator tests to verify they pass**

```bash
dotnet test --filter FullyQualifiedName~SaveCoordinatorTests
```

Expected: PASS, 16 tests.

- [ ] **Step 6: Wire the mode toggle and the editor into `NoteWindow`**

Add to `src/StickyMD.App/Windows/NoteWindow.xaml.cs`. The constructor gains the coordinator, and these members are added:

```csharp
    private readonly SaveCoordinator _saves;
    private readonly System.Windows.Threading.DispatcherTimer _autosave = new()
    {
        // Spec: 500ms after the last keystroke.
        Interval = TimeSpan.FromMilliseconds(500),
    };

    private bool _editing;

    // Added to the constructor, after _web is created:
    //   _saves = new SaveCoordinator(
    //       canonicalPath,
    //       new NoteFileGateway(),
    //       ledger,
    //       new RecoveryStore(AppPaths.RecoveryDir),
    //       AppPaths.DiagnosticsFile);
    //   _autosave.Tick += async (_, _) => { _autosave.Stop(); await _saves.FlushAsync(); };
    //   Editor.TextChanged += OnEditorTextChanged;
    //   Editor.LostFocus += async (_, _) => await FlushAsync();
    //   Editor.PreviewKeyDown += OnEditorPreviewKeyDown;
    //   PreviewKeyDown += OnWindowPreviewKeyDown;
    //   EditButton.Click += (_, _) => ToggleEditMode();

    private void OnEditorTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_editing) return;

        _buffer = Editor.Text;
        _saves.MarkDirty(_buffer);
        TitleText.Text = NoteTitleResolver.Resolve(_buffer, NotePath);

        // Restart, not start: the debounce is 500ms after the LAST keystroke,
        // so each one pushes the deadline out.
        _autosave.Stop();
        _autosave.Start();
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.E
            && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            ToggleEditMode();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _editing)
        {
            _ = ExitEditModeAsync();
            e.Handled = true;
        }
    }

    private void OnEditorPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var control = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

        EditResult? result = e.Key switch
        {
            Key.B when control => MarkdownEditOps.ToggleBold(
                Editor.Text, Editor.SelectionStart, Editor.SelectionLength),
            Key.I when control => MarkdownEditOps.ToggleItalic(
                Editor.Text, Editor.SelectionStart, Editor.SelectionLength),
            Key.Enter when !control && !shift => MarkdownEditOps.ContinueList(
                Editor.Text, Editor.SelectionStart, Editor.SelectionLength),
            Key.Tab when !shift => MarkdownEditOps.Indent(
                Editor.Text, Editor.SelectionStart, Editor.SelectionLength),
            Key.Tab when shift => MarkdownEditOps.Outdent(
                Editor.Text, Editor.SelectionStart, Editor.SelectionLength),
            _ => null,
        };

        if (result is null) return;

        ApplyEdit(result.Value);
        e.Handled = true;
    }

    /// <summary>
    /// Applies an <see cref="EditResult"/> through the selection rather than by
    /// assigning Text.
    /// </summary>
    /// <remarks>
    /// Editor.Text = result.Text would CLEAR the undo stack -- a programmatic
    /// Text assignment is not an undoable edit -- so Ctrl+Z after Ctrl+B would
    /// do nothing, or would jump back past several edits. Replacing through
    /// SelectedText goes onto the undo stack as one unit, so Ctrl+Z undoes
    /// exactly the bold.
    /// </remarks>
    private void ApplyEdit(EditResult result)
    {
        Editor.SelectAll();
        Editor.SelectedText = result.Text;
        Editor.Select(result.SelectionStart, result.SelectionLength);
    }

    private void ToggleEditMode()
    {
        if (_editing) _ = ExitEditModeAsync();
        else EnterEditMode();
    }

    public void EnterEditMode()
    {
        _editing = true;

        Editor.Text = _buffer;
        Editor.Visibility = Visibility.Visible;

        // Collapsing the WebView is now a CHOICE, not a requirement. The old
        // rationale -- that an HwndHost paints over WPF content regardless of
        // z-order -- does not apply to WebView2CompositionControl, which
        // renders through D3DImage. It is still worth doing for focus and
        // memory.
        WebViewSlot.Visibility = Visibility.Collapsed;

        Editor.Focus();
        Editor.CaretIndex = Editor.Text.Length;
    }

    public async Task ExitEditModeAsync()
    {
        if (!_editing) return;

        _autosave.Stop();
        await FlushAsync().ConfigureAwait(true);

        _editing = false;

        Editor.Visibility = Visibility.Collapsed;
        WebViewSlot.Visibility = Visibility.Visible;

        await RenderAsync().ConfigureAwait(true);
    }

    private async Task FlushAsync()
    {
        _autosave.Stop();
        await _saves.FlushAsync().ConfigureAwait(true);
    }
```

`OnEditRequested` — the page's double-click — becomes:

```csharp
    private void OnEditRequested()
    {
        if (!_editing) EnterEditMode();
    }
```

`SaveNow` becomes:

```csharp
    public void SaveNow()
    {
        // Synchronous on purpose: called during shutdown and window disposal,
        // where an awaited continuation may never be pumped because the
        // dispatcher is already shutting down. GetAwaiter().GetResult() on the
        // UI thread would deadlock against the coordinator's own await, so
        // FlushAsync is given a dispatcher frame to complete in.
        var flush = _saves.FlushAsync();

        var frame = new System.Windows.Threading.DispatcherFrame();
        flush.ContinueWith(
            _ => frame.Continue = false,
            TaskScheduler.FromCurrentSynchronizationContext());

        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }
```

`Dispose` gains, before `_web.Dispose()`:

```csharp
        _autosave.Stop();

        // Spec: autosave on window close and app exit. The window is going
        // away; the buffer must not go with it.
        if (_saves.IsDirty) SaveNow();
```

And `LoadFromDisk` hands the content to the coordinator:

```csharp
            var content = NoteFile.Read(NotePath);
            _saves.AdoptFromDisk(content);
            _buffer = content.Text;
            _diskHash = content.ContentHash;
```

- [ ] **Step 7: Add the >2MB guard**

Spec §8: a note over 2MB opens in edit mode with the preview skipped, so the WebView does not hang. Add to `NoteWindow`:

```csharp
    /// <summary>
    /// Spec §8. Markdig plus a DOM swap on a multi-megabyte document freezes
    /// the note for seconds; edit mode is a plain TextBox and stays usable.
    /// </summary>
    private const int PreviewSizeLimitBytes = 2 * 1024 * 1024;

    private bool ExceedsPreviewLimit
        => System.Text.Encoding.UTF8.GetByteCount(_buffer) > PreviewSizeLimitBytes;
```

and in `RenderAsync`, before rendering:

```csharp
        if (ExceedsPreviewLimit)
        {
            if (!_editing) EnterEditMode();

            _bars.Show(new InlineBarRequest(
                "size-limit",
                "This note is over 2 MB, so the preview is turned off.",
                Dismissible: false));

            return;
        }
```

- [ ] **Step 8: Build, test, and verify by hand**

```bash
dotnet build
dotnet test
dotnet run --project src/StickyMD.App/StickyMD.App.csproj
```

Check each:

- [ ] `Ctrl+E` switches to a text editor showing the note's raw Markdown.
- [ ] `Esc` returns to the rendered preview, **with the edit visible in it**.
- [ ] The pencil glyph does the same as `Ctrl+E`.
- [ ] **Double-clicking empty space** below the text enters edit mode.
- [ ] **Double-clicking a word** in the rendered view selects the word and does **not** enter edit mode.
- [ ] Type in edit mode, wait ~1s, then check the `.md` file in Notepad — **the change is on disk**.
- [ ] `Ctrl+B` with a selection wraps it in `**`. `Ctrl+B` again unwraps it.
- [ ] `Ctrl+B` with no selection inserts `****` and leaves the caret between them.
- [ ] `Ctrl+I` does the same with `*`.
- [ ] **`Ctrl+Z` after `Ctrl+B` undoes exactly the bold**, not the whole document. _If it clears the note or does nothing, `Editor.Text =` crept back in — see `ApplyEdit`._
- [ ] `Enter` at the end of `- item` starts `- ` on the next line.
- [ ] `Enter` on an empty `- ` **removes the prefix and ends the list**.
- [ ] `Enter` at the end of `1. item` produces `2. `.
- [ ] `Tab` on a list line indents it by two spaces; `Shift+Tab` outdents; `Shift+Tab` at column 0 does nothing.
- [ ] **`Tab` does not move focus out of the editor** — `AcceptsTab="False"` is what makes Tab reach the indent handler.
- [ ] Click away from the note (lose focus) — the edit is saved.
- [ ] Make an edit and close the note with `✕` — the edit is on disk.

- [ ] **Step 9: Commit**

Hand these to the user:

```bash
git add src/StickyMD.App tests/StickyMD.App.Tests
git commit -m "Make the note editable: mode toggle, edit ops, and autosave

SaveCoordinator owns the unsaved buffer and everything that happens when
it is written, and the ordering in it is deliberate.

The buffer never leaves memory on failure. IsDirty stays true and the text
stays put, so the amber bar's Retry has something to write and closing the
note still has something to snapshot.

Recovery snapshots are written AFTER the retries are exhausted, not
before. A OneDrive lock that clears on attempt two would otherwise leave a
stale snapshot that a later startup offers to restore -- older text
presented to the user as a recovery.

An encoding failure is not retried. NoteFile encodes strictly, so a lone
surrogate throws before any file is touched and will throw identically
three more times; retrying is three delays before the same answer. The
snapshot is still written, because that text cannot reach the file at all,
which is precisely what recovery is for.

Every write is recorded in the ledger immediately. A save that is not
recorded comes back from the watcher as an external change and the note
reloads itself.

Flushes are serialised. The 500ms debounce and an explicit flush on blur
or close can land together, and two concurrent AtomicWrites to one path
race over the same temp file.

Editor conveniences apply their result through SelectedText rather than by
assigning Text. A programmatic Text assignment is not an undoable edit and
clears the undo stack, so Ctrl+Z after Ctrl+B would do nothing or jump
back past several edits.

Collapsing the WebView on entering edit mode is now a choice rather than a
requirement. The old rationale -- an HwndHost painting over WPF content
regardless of z-order -- does not apply to the composition control, which
renders through D3DImage. It is kept for focus and memory."
```

---

## Task 11: The checkbox bridge and the link bridge

Clicking a checkbox in the rendered view writes back to the `.md`. This is where the inclusive-span contract and the render token earn their keep.

**Files:**

- Modify: `src/StickyMD.App/Windows/NoteWindow.xaml.cs`
- Test: `tests/StickyMD.App.Tests/Services/CheckboxBridgeTests.cs`

**Interfaces:**

- Consumes: `TaskListToggler`, `MarkdownRenderer.ComputeToken`, `NavigationPolicy.DecideLinkClick`, `SaveCoordinator`.
- Produces: no new public types. `NoteWindow.OnTaskToggleRequested` and `OnLinkClicked` gain their bodies, and one testable helper:
  - `static class CheckboxBridge` with `ToggleDecision Decide(string buffer, int spanStart, int spanEnd, string? token)`
  - `enum ToggleVerdict { Apply, StaleToken, InvalidSpan }`
  - `sealed record ToggleDecision(ToggleVerdict Verdict, string Markdown, string? Reason)`

### Why the decision is extracted

The window cannot be unit tested, but the decision _can_, and it is the one place where getting the arithmetic wrong writes the wrong character into the user's file. Spec §6: _"The failure mode is 'rarely ignores a click and refreshes', never 'edits the wrong task'."_ That is a testable claim, so it gets tests.

The token closes the remaining race. The span is stable across edits that ordinals would not survive, but between render and click the buffer can still change — an external edit arriving, or an autosave of a concurrent edit. The token is the SHA-256 of the markdown at render time; if it no longer matches, the click is dropped and the note re-renders.

- [ ] **Step 1: Write the failing tests**

`tests/StickyMD.App.Tests/Services/CheckboxBridgeTests.cs`:

```csharp
using Shouldly;
using StickyMD.App.Services;
using StickyMD.Core.Markdown;

namespace StickyMD.App.Tests.Services;

public class CheckboxBridgeTests
{
    private const string Buffer = "- [ ] first\n- [x] second\n";

    private static string TokenFor(string markdown)
        => MarkdownRenderer.ComputeToken(markdown);

    /// <summary>
    /// The span of the "[ ]" token in "- [ ] first": '[' is at index 2 and ']'
    /// at index 4. Markdig's SourceSpan.End is INCLUSIVE, so End is 4 and the
    /// span covers three characters -- verified empirically three times in Plan
    /// A, and it holds for CJK, accented text, and emoji.
    /// </summary>
    private const int FirstStart = 2;
    private const int FirstEnd = 4;

    [Fact]
    public void A_matching_token_and_a_valid_span_toggles_the_box()
    {
        var decision = CheckboxBridge.Decide(
            Buffer, FirstStart, FirstEnd, TokenFor(Buffer));

        decision.Verdict.ShouldBe(ToggleVerdict.Apply);
        decision.Markdown.ShouldBe("- [x] first\n- [x] second\n");
    }

    [Fact]
    public void Toggling_the_second_box_leaves_the_first_alone()
    {
        // "- [x] second" starts at index 12; '[' is at 14, ']' at 16.
        var decision = CheckboxBridge.Decide(Buffer, 14, 16, TokenFor(Buffer));

        decision.Verdict.ShouldBe(ToggleVerdict.Apply);
        decision.Markdown.ShouldBe("- [ ] first\n- [ ] second\n");
    }

    [Fact]
    public void A_stale_token_drops_the_click_and_leaves_the_buffer_untouched()
    {
        var decision = CheckboxBridge.Decide(
            Buffer, FirstStart, FirstEnd, TokenFor("something else entirely"));

        decision.Verdict.ShouldBe(ToggleVerdict.StaleToken);
        decision.Markdown.ShouldBe(Buffer);
        decision.Reason.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_click_carrying_no_token_is_dropped()
    {
        CheckboxBridge.Decide(Buffer, FirstStart, FirstEnd, null)
            .Verdict.ShouldBe(ToggleVerdict.StaleToken);
    }

    [Fact]
    public void An_off_by_one_span_is_refused_rather_than_writing_the_wrong_characters()
    {
        // This is the test that catches an EXCLUSIVE-end misreading. A span of
        // 2..5 covers four characters, which is not a task marker, so the
        // toggler refuses it -- and the buffer is unchanged rather than
        // silently mangled.
        var decision = CheckboxBridge.Decide(Buffer, 2, 5, TokenFor(Buffer));

        decision.Verdict.ShouldBe(ToggleVerdict.InvalidSpan);
        decision.Markdown.ShouldBe(Buffer);
    }

    [Theory]
    [InlineData(-1, 4)]
    [InlineData(2, 1)]
    [InlineData(1000, 1002)]
    public void A_nonsensical_span_is_refused(int start, int end)
    {
        CheckboxBridge.Decide(Buffer, start, end, TokenFor(Buffer))
            .Verdict.ShouldBe(ToggleVerdict.InvalidSpan);
    }

    [Fact]
    public void A_span_pointing_at_ordinary_text_is_refused()
    {
        // "fir" rather than "[ ]".
        CheckboxBridge.Decide(Buffer, 6, 8, TokenFor(Buffer))
            .Verdict.ShouldBe(ToggleVerdict.InvalidSpan);
    }

    [Fact]
    public void An_uppercase_X_toggles_off()
    {
        const string upper = "- [X] shouty\n";

        var decision = CheckboxBridge.Decide(upper, 2, 4, TokenFor(upper));

        decision.Verdict.ShouldBe(ToggleVerdict.Apply);
        decision.Markdown.ShouldBe("- [ ] shouty\n");
    }

    [Fact]
    public void Toggling_twice_returns_the_original_text_exactly()
    {
        var once = CheckboxBridge.Decide(
            Buffer, FirstStart, FirstEnd, TokenFor(Buffer)).Markdown;

        var twice = CheckboxBridge.Decide(
            once, FirstStart, FirstEnd, TokenFor(once)).Markdown;

        twice.ShouldBe(Buffer);
    }

    [Fact]
    public void The_token_of_the_result_differs_from_the_token_of_the_input()
    {
        // The next click must carry the NEW token, or the second toggle on a
        // note is always dropped as stale.
        var result = CheckboxBridge.Decide(
            Buffer, FirstStart, FirstEnd, TokenFor(Buffer)).Markdown;

        TokenFor(result).ShouldNotBe(TokenFor(Buffer));
    }

    [Fact]
    public void A_span_beyond_a_shrunken_buffer_is_refused_not_applied()
    {
        // The token guard normally catches this. If a buffer somehow shrank
        // while keeping its token, the span check is the second line of
        // defence -- and the claimed failure mode is "ignores a click", never
        // "edits the wrong task".
        const string shrunk = "- [ ]";

        CheckboxBridge.Decide(shrunk, 20, 22, TokenFor(shrunk))
            .Verdict.ShouldBe(ToggleVerdict.InvalidSpan);
    }

    [Fact]
    public void A_span_over_a_checkbox_after_an_emoji_still_resolves()
    {
        // Spans are UTF-16 code unit offsets and an emoji is a surrogate pair,
        // so this is where an off-by-one in span handling shows up. Verified
        // in Plan A for CJK, accented text, and emoji.
        const string emoji = "- \U0001F600 note\n- [ ] task\n";

        var start = emoji.IndexOf("[ ]", StringComparison.Ordinal);

        var decision = CheckboxBridge.Decide(
            emoji, start, start + 2, TokenFor(emoji));

        decision.Verdict.ShouldBe(ToggleVerdict.Apply);
        decision.Markdown.ShouldContain("- [x] task");
        decision.Markdown.ShouldContain("\U0001F600");
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

```bash
dotnet test --filter FullyQualifiedName~CheckboxBridgeTests
```

Expected: FAIL to compile.

- [ ] **Step 3: Write `CheckboxBridge`**

`src/StickyMD.App/Services/CheckboxBridge.cs`:

```csharp
using StickyMD.Core.Markdown;

namespace StickyMD.App.Services;

public enum ToggleVerdict
{
    /// <summary>Write <c>Markdown</c> to the buffer and save.</summary>
    Apply,

    /// <summary>
    /// The buffer moved since the render this click came from. Drop the click
    /// and re-render.
    /// </summary>
    StaleToken,

    /// <summary>The span does not name a task marker. Drop it and re-render.</summary>
    InvalidSpan,
}

/// <param name="Markdown">
/// The updated text on <see cref="ToggleVerdict.Apply"/>, and the UNCHANGED
/// input otherwise. A refusal must never hand back something half-edited.
/// </param>
public sealed record ToggleDecision(
    ToggleVerdict Verdict, string Markdown, string? Reason);

/// <summary>
/// Decides what a checkbox click does. Extracted from the window because this
/// is the one place a mistake writes the wrong characters into the user's file.
/// </summary>
/// <remarks>
/// TWO GUARDS, IN THIS ORDER.
///
/// The TOKEN closes the render-to-click race. Source spans survive document
/// edits that ordinals would not, but between rendering and clicking the buffer
/// can still move -- an external edit landing, or an autosave of a concurrent
/// edit. The token is the SHA-256 of the markdown at render time; a mismatch
/// means the span belongs to a document that no longer exists.
///
/// The SPAN CHECK is TaskListToggler's, and it is not duplicated here. Markdig's
/// SourceSpan.End is INCLUSIVE, so a marker span is exactly three characters
/// and anything else is refused. Validating spans in two places is how the two
/// places end up disagreeing.
///
/// The guaranteed failure mode is "rarely ignores a click and refreshes", never
/// "edits the wrong task". Both guards fail closed to preserve that.
/// </remarks>
public static class CheckboxBridge
{
    public static ToggleDecision Decide(
        string buffer, int spanStart, int spanEnd, string? token)
    {
        if (string.IsNullOrEmpty(token)
            || !string.Equals(
                token,
                MarkdownRenderer.ComputeToken(buffer),
                StringComparison.OrdinalIgnoreCase))
        {
            return new ToggleDecision(
                ToggleVerdict.StaleToken,
                buffer,
                "the note changed since it was rendered");
        }

        var result = TaskListToggler.Toggle(buffer, spanStart, spanEnd);

        return result.Applied
            ? new ToggleDecision(ToggleVerdict.Apply, result.Markdown, null)
            : new ToggleDecision(ToggleVerdict.InvalidSpan, buffer, result.Reason);
    }
}
```

- [ ] **Step 4: Run the bridge tests to verify they pass**

```bash
dotnet test --filter FullyQualifiedName~CheckboxBridgeTests
```

Expected: PASS, 15 tests.

**If `An_off_by_one_span_is_refused…` fails by reporting `Apply`, the inclusive-span contract has been broken somewhere.** Do not "fix" the test — check `SourceSpanTaskListRenderer` and `TaskListToggler`, whose agreement on inclusivity is what this asserts.

- [ ] **Step 5: Wire it into `NoteWindow`**

Replace the two stub handlers in `src/StickyMD.App/Windows/NoteWindow.xaml.cs`:

```csharp
    private async void OnTaskToggleRequested(int spanStart, int spanEnd, string? token)
    {
        var decision = CheckboxBridge.Decide(_buffer, spanStart, spanEnd, token);

        if (decision.Verdict != ToggleVerdict.Apply)
        {
            // Re-render so the checkbox returns to whatever the file actually
            // says. The page cancelled the default action, so it is currently
            // showing the pre-click state -- but a re-render is what makes
            // that true rather than coincidental.
            Core.Diagnostics.DiagnosticsLog.Write(
                AppPaths.DiagnosticsFile,
                $"{NotePath}: checkbox click refused -- {decision.Reason}");

            await RenderAsync().ConfigureAwait(true);
            return;
        }

        _buffer = decision.Markdown;
        _saves.MarkDirty(_buffer);
        TitleText.Text = NoteTitleResolver.Resolve(_buffer, NotePath);

        // Saved IMMEDIATELY, not on the 500ms debounce. Ticking a box is a
        // deliberate commit, not typing -- and a note closed in the next
        // 500ms must not lose it.
        await FlushAsync().ConfigureAwait(true);

        // Re-render AFTER the save, so the new token matches what is on disk
        // and the next click on the same note is not dropped as stale.
        await RenderAsync().ConfigureAwait(true);
    }

    private void OnLinkClicked(string? href)
    {
        var directory = Path.GetDirectoryName(NotePath) ?? string.Empty;
        var decision = NavigationPolicy.DecideLinkClick(href, directory);

        switch (decision.Action)
        {
            case NavigationAction.OpenInBrowser:
                OpenInBrowser(decision.Target!);
                break;

            case NavigationAction.OpenNote:
                // The manager owns the open-notes map, so it decides whether
                // this is a new window or a focus of an existing one.
                OpenNoteRequested?.Invoke(decision.Target!);
                break;

            default:
                Core.Diagnostics.DiagnosticsLog.Write(
                    AppPaths.DiagnosticsFile, $"{NotePath}: {decision.Reason}");
                break;
        }
    }

    private void OpenInBrowser(string url)
    {
        try
        {
            // UseShellExecute hands the URL to the OS default browser. Without
            // it, .NET tries to execute the string as a program and throws.
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
            or InvalidOperationException or PlatformNotSupportedException)
        {
            _bars.Show(new InlineBarRequest(
                "link-failed", "That link could not be opened."));

            Core.Diagnostics.DiagnosticsLog.Write(
                AppPaths.DiagnosticsFile,
                $"{NotePath}: could not open '{url}' -- {ex.Message}");
        }
    }
```

- [ ] **Step 6: Build, test, and verify by hand**

```bash
dotnet build
dotnet test
dotnet run --project src/StickyMD.App/StickyMD.App.csproj
```

Put this in the note file first:

```markdown
# Tasks

- [ ] unchecked
- [x] checked
- [x] shouty
  - [ ] nested

Links: [external](https://example.com) and [sibling](other.md) and [anchor](#tasks).

![missing](nope.png)
![remote](https://example.com/pixel.png)
```

Also create `%USERPROFILE%\StickyMD Notes\other.md` with any content.

Check each:

- [ ] Clicking `unchecked` ticks it, **and the `.md` file now reads `- [x] unchecked`** — verify in Notepad.
- [ ] Clicking it again unticks it, and the file returns to `- [ ]`.
- [ ] Clicking `shouty` (`- [X]`) unticks it.
- [ ] Clicking the **nested** checkbox toggles the nested item and **not** its parent.
- [ ] Toggle several boxes quickly — **every click lands, and no box ends up disagreeing with the file**.
- [ ] Edit the file in VS Code while the note is open, save, then click a checkbox — it either works against the new content or is ignored and refreshes. **It must never toggle the wrong line.**
- [ ] Clicking `external` opens `https://example.com` **in the default browser**, and the note itself does not navigate.
- [ ] Clicking `anchor` scrolls within the note and does not open anything.
- [ ] Clicking `sibling` logs an `OpenNoteRequested` — nothing visible yet; Task 13 opens the window. Confirm `diagnostics.log` shows no "Blocked" line for it.
- [ ] The remote image shows a **muted "image blocked" placeholder**, not a broken-image icon.
- [ ] `diagnostics.log` under `%LOCALAPPDATA%\StickyMD\` contains no unexpected entries.

- [ ] **Step 7: Commit**

Hand these to the user:

```bash
git add src/StickyMD.App tests/StickyMD.App.Tests
git commit -m "Wire the checkbox and link bridges

The toggle decision is extracted from the window because it is the one
place a mistake writes wrong characters into the user's file, and the
claimed failure mode -- rarely ignores a click and refreshes, never edits
the wrong task -- is a testable promise.

Two guards, in order. The token closes the render-to-click race: source
spans survive edits that ordinals would not, but between rendering and
clicking the buffer can still move, and a token mismatch means the span
belongs to a document that no longer exists. The span check then belongs
to TaskListToggler and is NOT duplicated -- Markdig's SourceSpan.End is
inclusive, so a marker span is exactly three characters, and validating
that in two places is how two places come to disagree.

A toggle saves immediately rather than on the 500ms debounce. Ticking a
box is a deliberate commit, not typing, and a note closed in the next half
second must not lose it. The re-render happens after the save so the new
token matches the file, or the next click on the same note is dropped as
stale.

Links come in as the raw href attribute and go through NavigationPolicy.
Process.Start needs UseShellExecute for a URL -- without it .NET tries to
execute the string as a program and throws.

The bridge posts a link message rather than letting NavigationStarting
see it, because the shell has an opaque origin: relative hrefs resolve
against about:blank, and fragment navigation does not reliably raise the
event at all."
```

---

## Task 12: The inline bars — every §8 condition the window owns

Each row of the spec's error table that a note window is responsible for. The governing rule is the whole point of this task.

**Files:**

- Modify: `src/StickyMD.App/Windows/NoteWindow.xaml.cs`
- Test: `tests/StickyMD.App.Tests/Services/BarDecisionTests.cs`
- Create: `src/StickyMD.App/Services/ExternalChangePolicy.cs`

**Interfaces:**

- Consumes: `InlineBarHost` (Task 9), `SaveCoordinator` (Task 10), `RecoveryStore`, `NoteFile`.
- Produces:
  - `enum ExternalChangeAction { Reload, Ask }`
  - `static ExternalChangeAction ExternalChangePolicy.Decide(bool bufferIsDirty, string? bufferHash, string diskHash)`
  - `NoteWindow` methods: `ShowSaveFailed`, `ShowChangedOnDisk`, `ShowFileGone`, `ShowRemoteImagesAvailable`, `ShowRecoveredContent`, `ShowUnreadable`

### The bar inventory

| Bar id          | Condition                                 | Actions            | Dismissible |
| --------------- | ----------------------------------------- | ------------------ | ----------- |
| `save-failed`   | Save exhausted its retries                | Retry / Save As…   | No          |
| `changed-disk`  | External change while the buffer is dirty | Reload / Keep Mine | No          |
| `file-gone`     | The file was deleted externally           | Recreate / Close   | No          |
| `remote-images` | The render blocked ≥1 remote image        | Load remote images | Yes         |
| `recovered`     | A recovery snapshot survived startup      | Restore / Discard  | No          |
| `unreadable`    | The file is not decodable text            | Open folder        | No          |
| `plain-text`    | The renderer failed twice                 | —                  | No          |
| `size-limit`    | Over 2MB (Task 10)                        | —                  | No          |

**`save-failed`, `changed-disk`, `file-gone`, `recovered` and `unreadable` are not dismissible.** Each one means the file on disk and the text on screen disagree, and a dismissed bar would leave the user believing they agree.

- [ ] **Step 1: Write the failing tests for the external-change decision**

The only branching logic here worth extracting: whether an external change reloads silently or asks.

`tests/StickyMD.App.Tests/Services/BarDecisionTests.cs`:

```csharp
using Shouldly;
using StickyMD.App.Services;

namespace StickyMD.App.Tests.Services;

public class BarDecisionTests
{
    [Fact]
    public void A_clean_buffer_reloads_without_asking()
    {
        // Spec: external change, clean buffer -> reload and re-render, scroll
        // preserved. There is nothing to lose, so asking would be noise.
        ExternalChangePolicy.Decide(
            bufferIsDirty: false, bufferHash: "AAA", diskHash: "BBB")
            .ShouldBe(ExternalChangeAction.Reload);
    }

    [Fact]
    public void A_dirty_buffer_asks_rather_than_choosing()
    {
        // Spec: never auto-clobber either side.
        ExternalChangePolicy.Decide(
            bufferIsDirty: true, bufferHash: "AAA", diskHash: "BBB")
            .ShouldBe(ExternalChangeAction.Ask);
    }

    [Fact]
    public void A_dirty_buffer_whose_content_already_matches_disk_just_reloads()
    {
        // Somebody else typed the same characters, or our own save arrived by
        // a path the ledger missed. There is no conflict to resolve, and a bar
        // here would be a question with one answer.
        ExternalChangePolicy.Decide(
            bufferIsDirty: true, bufferHash: "SAME", diskHash: "SAME")
            .ShouldBe(ExternalChangeAction.Reload);
    }

    [Fact]
    public void Hash_comparison_ignores_case()
    {
        // NoteFile.Sha256 returns uppercase hex; a hash from JSON may not be.
        ExternalChangePolicy.Decide(
            bufferIsDirty: true, bufferHash: "abc123", diskHash: "ABC123")
            .ShouldBe(ExternalChangeAction.Reload);
    }

    [Fact]
    public void A_dirty_buffer_with_an_unknown_hash_asks()
    {
        // Not knowing is not the same as matching. Asking risks a click;
        // guessing risks the text.
        ExternalChangePolicy.Decide(
            bufferIsDirty: true, bufferHash: null, diskHash: "BBB")
            .ShouldBe(ExternalChangeAction.Ask);
    }
}
```

- [ ] **Step 2: Run them to verify they fail, then write the policy**

```bash
dotnet test --filter FullyQualifiedName~BarDecisionTests
```

Expected: FAIL to compile.

`src/StickyMD.App/Services/ExternalChangePolicy.cs`:

```csharp
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
```

- [ ] **Step 3: Add the bars to `NoteWindow`**

Add to `src/StickyMD.App/Windows/NoteWindow.xaml.cs`:

```csharp
    /// <summary>Save exhausted its retries. Not dismissible: the file and the screen disagree.</summary>
    private void ShowSaveFailed(string message)
        => _bars.Show(new InlineBarRequest(
            "save-failed",
            $"Couldn't save — {message}",
            PrimaryAction: "Retry",
            OnPrimary: () => _ = FlushAsync(),
            SecondaryAction: "Save As…",
            OnSecondary: SaveAs,
            Dismissible: false));

    private void SaveAs()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Markdown (*.md)|*.md",
            FileName = Path.GetFileName(NotePath),
            InitialDirectory = Path.GetDirectoryName(NotePath),
            AddExtension = true,
            DefaultExt = ".md",
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            // Deliberately NOT through SaveCoordinator: this writes somewhere
            // else, so it must not record a ledger entry for this note's path
            // or clear this note's recovery snapshot. The original file's
            // problem is unresolved and its bar stays up.
            NoteFile.AtomicWrite(dialog.FileName, _buffer, _saves.Format);

            _bars.Show(new InlineBarRequest(
                "saved-elsewhere",
                $"Saved a copy to {Path.GetFileName(dialog.FileName)}. This note is still unsaved."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _bars.Show(new InlineBarRequest(
                "save-as-failed", $"That copy could not be written — {ex.Message}"));
        }
    }

    /// <summary>An external change arrived while the buffer was dirty.</summary>
    private void ShowChangedOnDisk(string diskText, string diskHash)
        => _bars.Show(new InlineBarRequest(
            "changed-disk",
            "Changed on disk.",
            PrimaryAction: "Reload",
            OnPrimary: () => ApplyExternalContent(diskText, diskHash),
            SecondaryAction: "Keep Mine",
            OnSecondary: () =>
            {
                // Keeping ours means our text must actually reach the file --
                // otherwise "Keep Mine" silently keeps it only until the
                // window closes.
                _saves.MarkDirty(_buffer);
                _ = FlushAsync();
            },
            Dismissible: false));

    /// <summary>The file was deleted or moved out of the watched folder.</summary>
    public void NotifyFileDeleted()
        => _bars.Show(new InlineBarRequest(
            "file-gone",
            "This note's file is gone.",
            PrimaryAction: "Recreate",
            OnPrimary: () =>
            {
                // The buffer is still here, so recreating is a save. That is
                // the whole reason the buffer is never cleared on failure.
                _saves.MarkDirty(_buffer);
                _ = FlushAsync();
            },
            SecondaryAction: "Close",
            OnSecondary: () => CloseRequested?.Invoke(NotePath),
            Dismissible: false));

    /// <summary>The render blocked at least one remote image.</summary>
    private void ShowRemoteImagesAvailable(int count)
        => _bars.Show(new InlineBarRequest(
            "remote-images",
            count == 1
                ? "1 remote image was blocked."
                : $"{count} remote images were blocked.",
            PrimaryAction: "Load remote images",
            OnPrimary: () =>
            {
                // Per SESSION and per NOTE, deliberately. The global opt-in
                // lives in Settings (Plan C); this one is not persisted, so
                // reopening the note blocks them again. Notes sync, and a
                // one-off decision to trust one note must not become a
                // standing one.
                _allowRemoteImages = true;
                _ = ReloadShellAsync();
            }));

    private async Task ReloadShellAsync()
    {
        // A CSP change needs a fresh shell -- a meta-tag CSP is fixed at parse
        // time. Content updates never do this.
        await _web.SetThemeAsync(
            _theme,
            _allowRemoteImages,
            System.Drawing.ColorTranslator.FromHtml(_theme.ContentBg))
            .ConfigureAwait(true);

        await RenderAsync().ConfigureAwait(true);
    }

    /// <summary>A recovery snapshot survived to this startup.</summary>
    public void ShowRecoveredContent(RecoveryEnvelope envelope)
        => _bars.Show(new InlineBarRequest(
            "recovered",
            "Unsaved changes were recovered.",
            PrimaryAction: "Restore",
            OnPrimary: () =>
            {
                _buffer = envelope.Content;
                _saves.MarkDirty(_buffer);
                TitleText.Text = NoteTitleResolver.Resolve(_buffer, NotePath);
                _ = FlushAsync();
                _ = RenderAsync();
            },
            SecondaryAction: "Discard",
            OnSecondary: () => new RecoveryStore(AppPaths.RecoveryDir).Clear(NotePath),
            Dismissible: false));

    /// <summary>
    /// The file is not decodable text -- an ANSI note, or a binary file with a
    /// .md extension.
    /// </summary>
    /// <remarks>
    /// NoteFile decodes STRICTLY and throws rather than substituting U+FFFD,
    /// because a lenient decode followed by a save would write replacement
    /// characters back and destroy the original bytes with no error anywhere.
    /// So the note opens read-only, autosave never runs, and the user is told
    /// why.
    /// </remarks>
    private void ShowUnreadable()
    {
        Editor.IsReadOnly = true;

        _bars.Show(new InlineBarRequest(
            "unreadable",
            "This file isn't UTF-8 text, so StickyMD won't edit it.",
            PrimaryAction: "Show in folder",
            OnPrimary: () =>
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        "explorer.exe", $"/select,\"{NotePath}\"") { UseShellExecute = true });
                }
                catch (System.ComponentModel.Win32Exception) { /* nothing useful to add */ }
            },
            Dismissible: false));
    }

    private void OnFellBackToPlainText(string message)
    {
        // Readable beats rendered. The text is already in memory, so nothing
        // is lost -- the note becomes a plain-text pane.
        WebViewSlot.Visibility = Visibility.Collapsed;
        Editor.Text = _buffer;
        Editor.Visibility = Visibility.Visible;
        _editing = true;

        _bars.Show(new InlineBarRequest("plain-text", message, Dismissible: false));
    }
```

- [ ] **Step 4: Hook the bars up to their triggers**

Three edits in `NoteWindow`:

`_saves.Saved` drives the save bar:

```csharp
        // In the constructor, after _saves is created:
        _saves.Saved += outcome =>
        {
            if (outcome.Status == SaveStatus.Failed)
            {
                ShowSaveFailed(outcome.Message ?? "the file could not be written");
            }
            else if (outcome.Status == SaveStatus.Saved)
            {
                // Spec: cleared on the next success.
                _bars.Dismiss("save-failed");
                _bars.Dismiss("file-gone");
            }
        };
```

`RenderAsync` drives the remote-images bar:

```csharp
        var result = _renderer.Render(
            _buffer, new RenderOptions(_allowRemoteImages));

        if (result.BlockedRemoteImages > 0 && !_allowRemoteImages)
            ShowRemoteImagesAvailable(result.BlockedRemoteImages);
        else
            _bars.Dismiss("remote-images");

        await _web.RenderAsync(result).ConfigureAwait(true);
```

`LoadFromDisk` drives the unreadable bar:

```csharp
        catch (System.Text.DecoderFallbackException)
        {
            _buffer = string.Empty;
            ShowUnreadable();
            return;
        }
```

And `ApplyExternalContent` becomes the decision point:

```csharp
    public void ApplyExternalContent(string text, string diskHash)
    {
        var action = ExternalChangePolicy.Decide(
            _saves.IsDirty, MarkdownRenderer.ComputeToken(_buffer), diskHash);

        if (action == ExternalChangeAction.Ask)
        {
            ShowChangedOnDisk(text, diskHash);
            return;
        }

        _bars.Dismiss("changed-disk");

        _buffer = text;
        _diskHash = diskHash;
        _saves.AdoptFromDisk(new NoteContent(text, _saves.Format, diskHash));

        if (_editing) Editor.Text = text;

        TitleText.Text = NoteTitleResolver.Resolve(_buffer, NotePath);
        _ = RenderAsync();
    }
```

> **`ComputeToken` and `NoteFile.Sha256` are not the same hash.** `ComputeToken` hashes the UTF-8 bytes of the normalised text; `NoteContent.ContentHash` hashes the **raw bytes on disk**, BOM and CRLF included. Comparing them directly would report "different" for a file that is byte-identical in content but stored with CRLF. So `ExternalChangePolicy` is called with `ComputeToken(_buffer)` against a **token computed the same way** — which means `WindowManager` (Task 13) must pass `MarkdownRenderer.ComputeToken(diskText)`, not the raw content hash. Task 13's signature reflects this.

Correct the call to match:

```csharp
        var action = ExternalChangePolicy.Decide(
            _saves.IsDirty,
            MarkdownRenderer.ComputeToken(_buffer),
            MarkdownRenderer.ComputeToken(text));
```

- [ ] **Step 5: Build, test, and verify each bar by hand**

```bash
dotnet build
dotnet test
dotnet run --project src/StickyMD.App/StickyMD.App.csproj
```

Each of these produces one bar. Work through them:

- [ ] **Changed on disk, clean buffer.** Edit the note in VS Code and save. The note updates within ~200ms, **scroll position is preserved**, and **no bar appears**.
- [ ] **Changed on disk, dirty buffer.** Type in the note but do not wait for autosave; edit and save the same file in VS Code. A **"Changed on disk — Reload / Keep Mine"** bar appears. Click **Reload**: the file's version wins. Repeat and click **Keep Mine**: your version is written to the file — confirm in Notepad.
- [ ] **Save failure.** Open the `.md` in an app that locks it exclusively (or run `powershell -c "$f=[IO.File]::Open('<path>','Open','Read','None'); Read-Host"`). Type in the note. After the retries, an **amber "Couldn't save"** bar appears with Retry and Save As…. **The text stays in the editor.** Release the lock, click **Retry** — it saves and the bar disappears.
- [ ] **Recovery snapshot.** With the file still locked, close the note. Check `%LOCALAPPDATA%\StickyMD\recovery\` — **a `.json` snapshot exists containing your text and its `originalPath`**. Release the lock and reopen — Task 14 wires the startup bar; for now confirm the file's contents by eye.
- [ ] **File deleted.** Delete the `.md` from Explorer while the note is open. A **"This note's file is gone — Recreate / Close"** bar appears. Click **Recreate** — the file comes back with the buffer's content.
- [ ] **Remote images.** Put `![x](https://example.com/pixel.png)` in the note. A **placeholder** renders and a **"1 remote image was blocked — Load remote images"** bar appears. Click it: the shell re-navigates and the image is attempted. Close and reopen the note — **it is blocked again**, because the opt-in is per session.
- [ ] **Non-UTF-8 file.** Create a note saved as ANSI with a high-bit character (`é` in Notepad, encoding ANSI). Open it: a **"This file isn't UTF-8 text"** bar appears, the editor is **read-only**, and **the file is never modified** — confirm its bytes are unchanged afterwards.
- [ ] **Over 2MB.** Generate a 3MB `.md` (`python -c "print('word '*600000)" > big.md`). It opens **in edit mode** with a size bar, and **does not hang**.
- [ ] Confirm `%LOCALAPPDATA%\StickyMD\diagnostics.log` recorded the save failure and the blocked link, with timestamps.

- [ ] **Step 6: Commit**

Hand these to the user:

```bash
git add src/StickyMD.App tests/StickyMD.App.Tests
git commit -m "Add the inline bars for every §8 condition the window owns

Five of the eight bars are not dismissible: save-failed, changed-disk,
file-gone, recovered and unreadable each mean the file on disk and the
text on screen disagree, and a dismissed bar would leave the user
believing they agree.

ExternalChangePolicy is extracted because it is the only branch here. A
clean buffer reloads silently -- that is what makes the 200ms external-edit
criterion feel like sync rather than a prompt -- and a dirty buffer asks,
because neither side may be auto-clobbered. The exception is a dirty
buffer that already matches disk, where a bar would be a question with one
answer.

The comparison uses ComputeToken on both sides, NOT NoteContent's hash.
ComputeToken hashes the UTF-8 bytes of the normalised text; ContentHash
hashes the raw bytes on disk, BOM and line endings included. Comparing the
two would report a conflict for a file that is byte-identical in content
but stored with CRLF -- a bar on every save for anyone with a CRLF file.

Keep Mine writes our text to the file rather than only marking the buffer,
or it would keep our version exactly until the window closed. Recreate is
the same operation, which is the reason the buffer is never cleared on a
failed save.

Save As deliberately bypasses SaveCoordinator: it writes elsewhere, so it
must not record a ledger entry for this note's path or clear this note's
recovery snapshot. The original file's problem is unresolved and its bar
stays up.

The remote-image opt-in is per note and per session and is not persisted.
Notes sync, so a one-off decision to trust one note must not quietly
become a standing one; the global opt-in belongs in Settings.

A non-UTF-8 file opens read-only. NoteFile decodes strictly and throws
rather than substituting U+FFFD, because a lenient decode followed by a
save would write replacement characters back and destroy the original
bytes with no error anywhere."
```

---

## Task 13: `WindowManager` — three states, geometry, the watcher, rename, delete

The task with the most testable logic in Plan B, and the one where a mistake loses every note's placement after an exit. It never touches a `Window` type — only `INoteWindow` — so all of it runs headlessly.

**Files:**

- Create: `src/StickyMD.App/Services/WindowManager.cs`
- Create: `src/StickyMD.App/Windows/NoteWindowFactory.cs`
- Test: `tests/StickyMD.App.Tests/TestSupport/FakeNoteWindow.cs`
- Test: `tests/StickyMD.App.Tests/Services/WindowManagerTests.cs`

**Interfaces:**

- Consumes: `INoteWindow` (Task 9), `IMonitorProvider`, `ISystemTheme` (Task 6), `IFileDeletionService` (Task 7), `NoteIndexStore`, `NoteRepository`, `NoteWatcher`, `WindowPlacement`, `NotePath`, `MarkdownRenderer.ComputeToken`.
- Produces:
  - `interface INoteWindowFactory { INoteWindow Create(string canonicalPath, NoteState state, NoteTheme theme); }`
  - `sealed class NoteWindowFactory : INoteWindowFactory`
  - `sealed class WindowManager : IDisposable` with:
    - `void RestoreOpenNotes()`
    - `void OpenNote(string path, bool activate = true)`
    - `string CreateAndOpenNote()`
    - `void CloseNote(string canonicalPath)` — **the only thing that clears `isOpen`**
    - `void HideAll()` / `void ShowAll()`
    - `void DeleteNote(string canonicalPath)`
    - `void OnDisplaySettingsChanged()`
    - `void ShutdownWithoutClosingNotes()`
    - `IReadOnlyCollection<string> OpenPaths { get; }`

Task 14's bootstrapper constructs this and calls `RestoreOpenNotes`.

- [ ] **Step 1: Write the fake window**

`tests/StickyMD.App.Tests/TestSupport/FakeNoteWindow.cs`:

```csharp
using StickyMD.App.Windows;
using StickyMD.Core.Geometry;
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

    public bool IsVisible { get; private set; }
    public bool WasActivated { get; private set; }
    public bool WasFocused { get; private set; }
    public bool IsDisposed { get; private set; }
    public bool WasToldFileDeleted { get; private set; }
    public bool WasSaved { get; private set; }

    public List<string> ExternalContentApplied { get; } = [];
    public List<NoteState> StatesApplied { get; } = [];
    public List<string> RenamesApplied { get; } = [];

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

    public void ApplyState(NoteState state, NoteTheme theme)
    {
        State = state;
        StatesApplied.Add(state);
    }

    public void ApplyExternalContent(string text, string diskHash)
        => ExternalContentApplied.Add(text);

    public void NotifyFileDeleted() => WasToldFileDeleted = true;

    public void NotifyRenamed(string canonicalPath)
    {
        NotePath = canonicalPath;
        RenamesApplied.Add(canonicalPath);
    }

    public void SaveNow() => WasSaved = true;

    public void Dispose() => IsDisposed = true;

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

    public INoteWindow Create(string canonicalPath, NoteState state, NoteTheme theme)
    {
        var window = new FakeNoteWindow(canonicalPath, state);
        Created.Add(window);
        return window;
    }

    public FakeNoteWindow For(string path)
        => Created.Single(w => StickyMD.Core.Notes.NotePath.AreSame(w.NotePath, path));
}
```

- [ ] **Step 2: Write the failing `WindowManager` tests**

`tests/StickyMD.App.Tests/Services/WindowManagerTests.cs`:

```csharp
using Shouldly;
using StickyMD.App.Services;
using StickyMD.App.Tests.TestSupport;
using StickyMD.Core.Geometry;
using StickyMD.Core.Notes;
using StickyMD.Core.Persistence;
using StickyMD.Core.Theming;

namespace StickyMD.App.Tests.Services;

public class WindowManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "stickymd-wm", Guid.NewGuid().ToString("N"));

    private readonly FakeNoteWindowFactory _factory = new();
    private readonly FakeMonitorProvider _monitors = FakeMonitorProvider.TwoAt100Percent();
    private readonly FakeFileDeletionService _deleter = new();

    private NoteIndexStore _indexStore = null!;
    private WindowManager _manager = null!;

    public WindowManagerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _manager?.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }

    private sealed class FixedTheme : ISystemTheme
    {
        public ThemeMode Mode { get; set; } = ThemeMode.Light;

        public ThemeMode Resolve(ThemePreference preference) => Mode;

        public event Action? Changed;

        public void Raise() => Changed?.Invoke();
    }

    private sealed class FakeFileDeletionService : IFileDeletionService
    {
        public List<string> Deleted { get; } = [];

        public DeletionOutcome Next { get; set; } = DeletionOutcome.Deleted;

        public DeletionResult SendToRecycleBin(string path)
        {
            if (Next == DeletionOutcome.Deleted)
            {
                Deleted.Add(path);
                if (File.Exists(path)) File.Delete(path);
            }

            return new DeletionResult(
                Next, Next == DeletionOutcome.Failed ? "nope" : null);
        }
    }

    private string WriteNote(string name, string content = "# note")
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return NotePath.Canonical(path);
    }

    private WindowManager Build(NoteIndex? seed = null)
    {
        _indexStore = new NoteIndexStore(Path.Combine(_root, "notes.json"));
        if (seed is not null) _indexStore.Save(seed);

        _manager = new WindowManager(
            new NoteRepository(_root, new FixedClock(new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc))),
            _indexStore,
            new SettingsStore(Path.Combine(_root, "settings.json")),
            _factory,
            _monitors,
            new FixedTheme(),
            _deleter,
            new WriteLedger(),
            new RecoveryStore(Path.Combine(_root, "recovery")),
            diagnosticsFile: Path.Combine(_root, "diagnostics.log"));

        return _manager;
    }

    private static NoteState StateAt(int x, int y, bool isOpen = true) => new(
        X: x, Y: y, W: 300, H: 340,
        Monitor: null,
        Color: NoteColor.Yellow,
        Opacity: 1.0,
        AlwaysOnTop: false,
        IsOpen: isOpen,
        LastOpenedUtc: new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc));

    // ---- The three-state model -------------------------------------------

    [Fact]
    public void RestoreOpenNotes_opens_only_the_notes_marked_open()
    {
        var open = WriteNote("open.md");
        var closed = WriteNote("closed.md");

        var index = new NoteIndex();
        index.Notes[open] = StateAt(100, 100);
        index.Notes[closed] = StateAt(200, 200, isOpen: false);

        Build(index).RestoreOpenNotes();

        _factory.Created.Count.ShouldBe(1);
        _factory.Created[0].NotePath.ShouldBe(open);
    }

    [Fact]
    public void RestoreOpenNotes_spawns_no_window_for_a_note_that_is_merely_present()
    {
        // Pointing StickyMD at an Obsidian vault must not carpet the desktop.
        WriteNote("a.md");
        WriteNote("b.md");
        WriteNote("c.md");

        Build().RestoreOpenNotes();

        _factory.Created.ShouldBeEmpty();
    }

    [Fact]
    public void The_close_glyph_clears_isOpen_and_disposes_the_window()
    {
        var path = WriteNote("n.md");
        var index = new NoteIndex();
        index.Notes[path] = StateAt(100, 100);

        var manager = Build(index);
        manager.RestoreOpenNotes();

        _factory.For(path).RaiseClose();

        _indexStore.Load().Notes[path].IsOpen.ShouldBeFalse();
        _factory.For(path).IsDisposed.ShouldBeTrue();
        manager.OpenPaths.ShouldBeEmpty();
    }

    [Fact]
    public void HideAll_hides_every_window_and_leaves_isOpen_alone()
    {
        var a = WriteNote("a.md");
        var b = WriteNote("b.md");
        var index = new NoteIndex();
        index.Notes[a] = StateAt(10, 10);
        index.Notes[b] = StateAt(20, 20);

        var manager = Build(index);
        manager.RestoreOpenNotes();

        manager.HideAll();

        _factory.Created.ShouldAllBe(w => !w.IsVisible);
        _indexStore.Load().Notes.Values.ShouldAllBe(s => s.IsOpen);
        manager.OpenPaths.Count.ShouldBe(2, "hidden is not closed");
    }

    [Fact]
    public void ShowAll_brings_hidden_windows_back()
    {
        var path = WriteNote("n.md");
        var index = new NoteIndex();
        index.Notes[path] = StateAt(10, 10);

        var manager = Build(index);
        manager.RestoreOpenNotes();
        manager.HideAll();

        manager.ShowAll();

        _factory.For(path).IsVisible.ShouldBeTrue();
    }

    [Fact]
    public void ShutdownWithoutClosingNotes_leaves_every_isOpen_set()
    {
        // THE HEADLINE HAZARD. WPF closes every window during application
        // shutdown. If the close-glyph logic ran on Window.Closing, choosing
        // Exit would clear isOpen on every note and the next boot would
        // restore none of them -- breaking success criterion 3.
        var a = WriteNote("a.md");
        var b = WriteNote("b.md");
        var index = new NoteIndex();
        index.Notes[a] = StateAt(10, 10);
        index.Notes[b] = StateAt(20, 20);

        var manager = Build(index);
        manager.RestoreOpenNotes();

        manager.ShutdownWithoutClosingNotes();

        var reloaded = _indexStore.Load();
        reloaded.Notes[a].IsOpen.ShouldBeTrue();
        reloaded.Notes[b].IsOpen.ShouldBeTrue();
    }

    [Fact]
    public void ShutdownWithoutClosingNotes_saves_every_dirty_buffer_first()
    {
        var path = WriteNote("n.md");
        var index = new NoteIndex();
        index.Notes[path] = StateAt(10, 10);

        var manager = Build(index);
        manager.RestoreOpenNotes();

        manager.ShutdownWithoutClosingNotes();

        _factory.For(path).WasSaved.ShouldBeTrue("never lose text");
    }

    // ---- Path identity ---------------------------------------------------

    [Fact]
    public void Opening_a_note_twice_focuses_the_existing_window()
    {
        var path = WriteNote("n.md");
        var manager = Build();

        manager.OpenNote(path);
        manager.OpenNote(path);

        _factory.Created.Count.ShouldBe(1, "never two windows on one file");
        _factory.For(path).WasFocused.ShouldBeTrue();
    }

    [Fact]
    public void Opening_the_same_note_through_a_different_spelling_focuses_it()
    {
        // Finding 1, as a behaviour test. Before NotePath, a path from the
        // repository and one from the watcher were different dictionary keys
        // and this produced a second window.
        var path = WriteNote("Standup.md");
        var manager = Build();

        manager.OpenNote(path);
        manager.OpenNote(Path.Combine(_root, "sub", "..", "STANDUP.MD"));

        _factory.Created.Count.ShouldBe(1);
    }

    [Fact]
    public void OpenNote_refuses_a_path_that_is_not_a_file()
    {
        var manager = Build();

        manager.OpenNote(Path.Combine(_root, "absent.md"));

        _factory.Created.ShouldBeEmpty();
    }

    // ---- Geometry --------------------------------------------------------

    [Fact]
    public void A_saved_rect_that_is_fully_visible_is_restored_untouched()
    {
        var path = WriteNote("n.md");
        var index = new NoteIndex();
        index.Notes[path] = StateAt(1200, 80);

        Build(index).RestoreOpenNotes();

        var applied = _factory.For(path).State;
        applied.X.ShouldBe(1200);
        applied.Y.ShouldBe(80);
    }

    [Fact]
    public void A_rect_on_a_monitor_that_is_gone_is_clamped_onto_a_remaining_one()
    {
        var path = WriteNote("n.md");
        var index = new NoteIndex();
        // On DISPLAY2, which the provider below does not have.
        index.Notes[path] = StateAt(3000, 200);

        _monitors.Monitors = FakeMonitorProvider.OnlyPrimary().Monitors;

        Build(index).RestoreOpenNotes();

        var applied = _factory.For(path).State;
        applied.X.ShouldBeLessThan(2560);
        applied.X.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void A_state_change_from_a_window_is_persisted()
    {
        var path = WriteNote("n.md");
        var index = new NoteIndex();
        index.Notes[path] = StateAt(10, 10);

        var manager = Build(index);
        manager.RestoreOpenNotes();

        _factory.For(path).RaiseStateChanged(
            StateAt(10, 10) with { X = 777, Color = NoteColor.Blue });

        var saved = _indexStore.Load().Notes[path];
        saved.X.ShouldBe(777);
        saved.Color.ShouldBe(NoteColor.Blue);
    }

    [Fact]
    public void OnDisplaySettingsChanged_reclamps_a_note_that_went_off_screen()
    {
        var path = WriteNote("n.md");
        var index = new NoteIndex();
        index.Notes[path] = StateAt(3000, 200);

        var manager = Build(index);
        manager.RestoreOpenNotes();

        _factory.For(path).Bounds = new PixelRect(3000, 200, 300, 340);
        _monitors.Monitors = FakeMonitorProvider.OnlyPrimary().Monitors;

        manager.OnDisplaySettingsChanged();

        _factory.For(path).StatesApplied.Last().X.ShouldBeLessThan(2560);
    }

    [Fact]
    public void OnDisplaySettingsChanged_leaves_an_on_screen_note_where_it_is()
    {
        var path = WriteNote("n.md");
        var index = new NoteIndex();
        index.Notes[path] = StateAt(100, 100);

        var manager = Build(index);
        manager.RestoreOpenNotes();

        var before = _factory.For(path).StatesApplied.Count;
        _factory.For(path).Bounds = new PixelRect(100, 100, 300, 340);

        manager.OnDisplaySettingsChanged();

        // Reapplying a rect that is already correct would move the window by a
        // pixel on every display change.
        _factory.For(path).StatesApplied.Count.ShouldBe(before);
    }

    // ---- Watcher ---------------------------------------------------------

    [Fact]
    public void An_external_change_reaches_the_open_window()
    {
        var path = WriteNote("n.md", "# before");
        var manager = Build();
        manager.OpenNote(path);

        File.WriteAllText(path, "# after");
        manager.OnExternalChanged(path);

        _factory.For(path).ExternalContentApplied.ShouldContain("# after");
    }

    [Fact]
    public void An_external_change_to_a_note_with_no_window_is_ignored()
    {
        var path = WriteNote("n.md");
        var manager = Build();

        Should.NotThrow(() => manager.OnExternalChanged(path));
        _factory.Created.ShouldBeEmpty();
    }

    [Fact]
    public void A_deletion_notifies_the_window_and_does_not_clear_isOpen()
    {
        // The file is gone; the note is not closed. The user still gets to
        // choose Recreate, and closing it for them would discard the buffer.
        var path = WriteNote("n.md");
        var index = new NoteIndex();
        index.Notes[path] = StateAt(10, 10);

        var manager = Build(index);
        manager.RestoreOpenNotes();

        File.Delete(path);
        manager.OnDeleted(path);

        _factory.For(path).WasToldFileDeleted.ShouldBeTrue();
        _factory.For(path).IsDisposed.ShouldBeFalse();
        _indexStore.Load().Notes[path].IsOpen.ShouldBeTrue();
    }

    [Fact]
    public void A_rename_re_keys_the_index_and_retargets_the_window()
    {
        var oldPath = WriteNote("old.md");
        var index = new NoteIndex();
        index.Notes[oldPath] = StateAt(10, 10);

        var manager = Build(index);
        manager.RestoreOpenNotes();

        var newPath = NotePath.Canonical(Path.Combine(_root, "new.md"));
        File.Move(oldPath, newPath);

        manager.OnRenamed(oldPath, newPath);

        var reloaded = _indexStore.Load();
        reloaded.Notes.ShouldNotContainKey(oldPath);
        reloaded.Notes.ShouldContainKey(newPath);

        _factory.Created[0].RenamesApplied.ShouldContain(newPath);
        manager.OpenPaths.ShouldContain(newPath);
        manager.OpenPaths.ShouldNotContain(oldPath);
    }

    [Fact]
    public void Watcher_recovery_re_reads_every_open_note()
    {
        // NoteWatcher deliberately does not decide which notes to re-read --
        // it has no idea which are open. Events may have been dropped while it
        // was down, so every open note is re-read.
        var a = WriteNote("a.md", "# a1");
        var b = WriteNote("b.md", "# b1");
        var closed = WriteNote("c.md", "# c1");

        var index = new NoteIndex();
        index.Notes[a] = StateAt(10, 10);
        index.Notes[b] = StateAt(20, 20);
        index.Notes[closed] = StateAt(30, 30, isOpen: false);

        var manager = Build(index);
        manager.RestoreOpenNotes();

        File.WriteAllText(a, "# a2");
        File.WriteAllText(b, "# b2");
        File.WriteAllText(closed, "# c2");

        manager.OnWatcherRecovered(new IOException("buffer overflow"));

        _factory.For(a).ExternalContentApplied.ShouldContain("# a2");
        _factory.For(b).ExternalContentApplied.ShouldContain("# b2");
        _factory.Created.Count.ShouldBe(2, "a closed note has no window to re-read into");
    }

    // ---- Delete ----------------------------------------------------------

    [Fact]
    public void Delete_sends_the_file_to_the_recycle_bin_and_removes_the_entry()
    {
        var path = WriteNote("n.md");
        var index = new NoteIndex();
        index.Notes[path] = StateAt(10, 10);

        var manager = Build(index);
        manager.RestoreOpenNotes();

        _factory.For(path).RaiseDelete();

        _deleter.Deleted.ShouldContain(path);
        _factory.For(path).IsDisposed.ShouldBeTrue();
        _indexStore.Load().Notes.ShouldNotContainKey(path);
    }

    [Fact]
    public void A_failed_delete_leaves_the_note_open_and_the_entry_intact()
    {
        // Never destroy a file, and never pretend to. Closing the window on a
        // failed delete would leave the file behind with nothing on screen.
        var path = WriteNote("n.md");
        var index = new NoteIndex();
        index.Notes[path] = StateAt(10, 10);

        var manager = Build(index);
        manager.RestoreOpenNotes();

        _deleter.Next = DeletionOutcome.Failed;
        _factory.For(path).RaiseDelete();

        _factory.For(path).IsDisposed.ShouldBeFalse();
        _indexStore.Load().Notes.ShouldContainKey(path);
    }

    [Fact]
    public void A_cancelled_delete_changes_nothing()
    {
        var path = WriteNote("n.md");
        var index = new NoteIndex();
        index.Notes[path] = StateAt(10, 10);

        var manager = Build(index);
        manager.RestoreOpenNotes();

        _deleter.Next = DeletionOutcome.Cancelled;
        _factory.For(path).RaiseDelete();

        _factory.For(path).IsDisposed.ShouldBeFalse();
        _indexStore.Load().Notes.ShouldContainKey(path);
    }

    // ---- Rename request from the menu ------------------------------------

    [Fact]
    public void A_rename_request_moves_the_file_and_re_keys_the_index()
    {
        var oldPath = WriteNote("old.md");
        var index = new NoteIndex();
        index.Notes[oldPath] = StateAt(10, 10);

        var manager = Build(index);
        manager.RestoreOpenNotes();

        _factory.For(oldPath).RaiseRename("renamed");

        var newPath = NotePath.Canonical(Path.Combine(_root, "renamed.md"));
        File.Exists(newPath).ShouldBeTrue();
        _indexStore.Load().Notes.ShouldContainKey(newPath);
        manager.OpenPaths.ShouldContain(newPath);
    }

    [Fact]
    public void A_rename_onto_an_existing_name_is_refused_without_losing_either_file()
    {
        var oldPath = WriteNote("old.md", "# old");
        var taken = WriteNote("taken.md", "# taken");
        var index = new NoteIndex();
        index.Notes[oldPath] = StateAt(10, 10);

        var manager = Build(index);
        manager.RestoreOpenNotes();

        _factory.For(oldPath).RaiseRename("taken");

        File.ReadAllText(oldPath).ShouldBe("# old");
        File.ReadAllText(taken).ShouldBe("# taken");
        manager.OpenPaths.ShouldContain(oldPath);
    }

    // ---- Link-driven opening ---------------------------------------------

    [Fact]
    public void A_relative_md_link_opens_a_second_window()
    {
        var first = WriteNote("first.md");
        var second = WriteNote("second.md");

        var manager = Build();
        manager.OpenNote(first);

        _factory.For(first).RaiseOpenNote(second);

        _factory.Created.Count.ShouldBe(2);
        manager.OpenPaths.ShouldContain(second);
    }

    [Fact]
    public void A_new_note_is_created_open_and_marked_open()
    {
        var manager = Build();

        var path = manager.CreateAndOpenNote();

        File.Exists(path).ShouldBeTrue();
        NotePath.IsCanonical(path).ShouldBeTrue();
        _indexStore.Load().Notes[path].IsOpen.ShouldBeTrue();
        _factory.For(path).IsVisible.ShouldBeTrue();
    }
}
```

- [ ] **Step 3: Run them to verify they fail**

```bash
dotnet test --filter FullyQualifiedName~WindowManagerTests
```

Expected: FAIL to compile.

- [ ] **Step 4: Write `WindowManager`**

`src/StickyMD.App/Services/WindowManager.cs`:

```csharp
using StickyMD.App.Interop;
using StickyMD.App.Windows;
using StickyMD.Core.Diagnostics;
using StickyMD.Core.Geometry;
using StickyMD.Core.Markdown;
using StickyMD.Core.Notes;
using StickyMD.Core.Persistence;
using StickyMD.Core.Theming;

namespace StickyMD.App.Services;

/// <summary>
/// Owns every live note window and the index that outlives them.
/// </summary>
/// <remarks>
/// THREE STATES, NOT TWO.
///   isOpen = true   this note belongs on my desktop and returns next startup
///   hidden          temporarily not drawn; isOpen UNCHANGED
///   instantiated    an INoteWindow exists in memory
///
/// ONLY <see cref="CloseNote"/> MAY CLEAR isOpen. Application exit, Windows
/// logoff, and internal disposal must never do so. WPF closes every window
/// during application shutdown, so if the close-glyph logic lived on
/// Window.Closing then choosing Exit would clear isOpen on every note and the
/// next boot would restore none of them -- exactly the failure this model
/// exists to prevent, and a direct breach of success criterion 3.
/// <see cref="ShutdownWithoutClosingNotes"/> is the shutdown path, and it
/// saves buffers and geometry while leaving isOpen alone.
///
/// This class NEVER references a WPF Window type. It talks to INoteWindow, so
/// all of the above is exercised headlessly by WindowManagerTests -- which is
/// the point, because none of it can be tested through a real window.
/// </remarks>
public sealed class WindowManager : IDisposable
{
    private readonly NoteRepository _repository;
    private readonly NoteIndexStore _indexStore;
    private readonly SettingsStore _settingsStore;
    private readonly INoteWindowFactory _factory;
    private readonly IMonitorProvider _monitors;
    private readonly ISystemTheme _theme;
    private readonly IFileDeletionService _deleter;
    private readonly IWriteLedger _ledger;
    private readonly RecoveryStore _recovery;
    private readonly string _diagnosticsFile;

    private readonly Dictionary<string, INoteWindow> _windows =
        NotePath.NewMap<INoteWindow>();

    private NoteIndex _index;
    private AppSettings _settings;
    private bool _disposed;

    public WindowManager(
        NoteRepository repository,
        NoteIndexStore indexStore,
        SettingsStore settingsStore,
        INoteWindowFactory factory,
        IMonitorProvider monitors,
        ISystemTheme theme,
        IFileDeletionService deleter,
        IWriteLedger ledger,
        RecoveryStore recovery,
        string diagnosticsFile)
    {
        _repository = repository;
        _indexStore = indexStore;
        _settingsStore = settingsStore;
        _factory = factory;
        _monitors = monitors;
        _theme = theme;
        _deleter = deleter;
        _ledger = ledger;
        _recovery = recovery;
        _diagnosticsFile = diagnosticsFile;

        _settings = _settingsStore.Load();
        _index = _indexStore.Load(_settings);
    }

    public IReadOnlyCollection<string> OpenPaths => _windows.Keys;

    /// <summary>
    /// Recreates a window for every note marked <c>isOpen</c>.
    /// </summary>
    /// <remarks>
    /// Notes merely PRESENT in the root get no window. Pointing StickyMD at an
    /// Obsidian vault must not carpet the desktop; a .md becomes available to
    /// StickyMD without becoming a sticky note.
    /// </remarks>
    public void RestoreOpenNotes()
    {
        foreach (var (path, state) in _index.Notes.ToList())
        {
            if (!state.IsOpen) continue;

            if (!File.Exists(path))
            {
                // The note was deleted or moved while StickyMD was not
                // running. Leave the entry alone -- it holds geometry that
                // costs nothing and would be missed if the file returns.
                DiagnosticsLog.Write(
                    _diagnosticsFile, $"{path}: marked open but the file is gone.");
                continue;
            }

            // ShowActivated=false, per the spec's --startup rule: a screenful
            // of notes must not fight the logon sequence for focus.
            Instantiate(path, state, activate: false);
        }
    }

    public void OpenNote(string path, bool activate = true)
    {
        if (!NotePath.TryCanonical(path, out var canonical))
        {
            DiagnosticsLog.Write(_diagnosticsFile, $"Refused to open '{path}': unusable path.");
            return;
        }

        // Never two windows on one file. This lookup is the reason every
        // boundary hands out one canonical form -- before that, a path from
        // the repository and one from the watcher were different keys here.
        if (_windows.TryGetValue(canonical, out var existing))
        {
            existing.FocusNote();
            return;
        }

        if (!File.Exists(canonical))
        {
            DiagnosticsLog.Write(
                _diagnosticsFile, $"Refused to open '{canonical}': no such file.");
            return;
        }

        var state = _index.Notes.TryGetValue(canonical, out var saved)
            ? saved with { IsOpen = true, LastOpenedUtc = DateTime.UtcNow }
            : DefaultStateFor();

        Instantiate(canonical, state, activate);
        Persist(canonical, state);
    }

    public string CreateAndOpenNote()
    {
        // NoteRepository.CreateNewRecorded is documented NOT thread-safe and
        // requires callers to serialise. This runs on the UI thread, which
        // satisfies that.
        var (path, outcome) = _repository.CreateNewRecorded();

        // Record the creation write, or the watcher reports StickyMD's own new
        // note as an external change the moment it appears.
        _ledger.Record(path, outcome);

        OpenNote(path);

        return path;
    }

    private NoteState DefaultStateFor()
    {
        var monitors = _monitors.GetMonitors();

        var work = monitors.FirstOrDefault(m => m.IsPrimary)?.WorkArea
            ?? monitors.FirstOrDefault()?.WorkArea
            ?? new PixelRect(0, 0, 1920, 1080);

        // Offset each new note so a burst of them does not stack invisibly on
        // one spot.
        var offset = _windows.Count * 28 % 200;

        return new NoteState(
            X: work.X + 80 + offset,
            Y: work.Y + 80 + offset,
            W: _settings.DefaultWidth,
            H: _settings.DefaultHeight,
            Monitor: null,
            Color: _settings.DefaultColor,
            Opacity: _settings.DefaultOpacity,
            AlwaysOnTop: false,
            IsOpen: true,
            LastOpenedUtc: DateTime.UtcNow);
    }

    private void Instantiate(string canonical, NoteState state, bool activate)
    {
        var clamped = ClampToMonitors(state);
        var theme = NotePalette.Get(clamped.Color, _theme.Resolve(_settings.Theme));

        var window = _factory.Create(canonical, clamped, theme);

        window.CloseRequested += CloseNote;
        window.DeleteRequested += DeleteNote;
        window.RenameRequested += OnRenameRequested;
        window.OpenNoteRequested += path => OpenNote(path);
        window.StateChanged += OnStateChanged;

        _windows[canonical] = window;

        window.ShowNote(activate);

        if (clamped != state) Persist(canonical, clamped);

        OfferRecovery(canonical, window);
    }

    private void OfferRecovery(string canonical, INoteWindow window)
    {
        var envelope = _recovery.TryLoad(canonical);
        if (envelope is null) return;

        // RecoveryStore.Clear is best-effort, so a snapshot can outlive its
        // successful save. Compare LastKnownDiskHash against what is on disk
        // now: if they match, that text already reached the file and offering
        // it would present older content as a recovery.
        try
        {
            var current = NoteFile.Read(canonical);

            if (string.Equals(
                    envelope.LastKnownDiskHash,
                    current.ContentHash,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(envelope.Content, current.Text, StringComparison.Ordinal))
            {
                _recovery.Clear(canonical);
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.Text.DecoderFallbackException)
        {
            // Cannot compare. Offer it -- the user losing a click beats the
            // user losing text.
        }

        if (window is NoteWindow note) note.ShowRecoveredContent(envelope);
    }

    /// <summary>
    /// The close glyph. THE ONLY path that clears <c>isOpen</c>.
    /// </summary>
    public void CloseNote(string path)
    {
        if (!NotePath.TryCanonical(path, out var canonical)) return;

        if (_windows.Remove(canonical, out var window))
        {
            // Save before disposing: the buffer may hold unsaved text, and
            // this is a user action, not a crash.
            window.SaveNow();
            window.Dispose();
        }

        if (_index.Notes.TryGetValue(canonical, out var state))
            Persist(canonical, state with { IsOpen = false });
    }

    /// <summary>Hide All. <c>isOpen</c> is untouched, by design.</summary>
    public void HideAll()
    {
        foreach (var window in _windows.Values) window.HideNote();
    }

    public void ShowAll()
    {
        foreach (var window in _windows.Values) window.ShowNote(activate: false);
    }

    /// <summary>
    /// The shutdown path: application exit, Windows logoff, and
    /// <c>SessionEnding</c>.
    /// </summary>
    /// <remarks>
    /// Saves buffers and persists geometry, and DOES NOT TOUCH isOpen. That
    /// distinction is the whole reason this method exists separately from
    /// <see cref="CloseNote"/>.
    /// </remarks>
    public void ShutdownWithoutClosingNotes()
    {
        foreach (var (path, window) in _windows.ToList())
        {
            // Spec §8.1: a snapshot is also written on app exit if the note is
            // still unsaved. SaveNow tries the file first; the coordinator
            // writes the snapshot if it cannot.
            window.SaveNow();

            var bounds = window.Bounds;

            if (bounds.Width > 0 && bounds.Height > 0
                && _index.Notes.TryGetValue(path, out var state))
            {
                Persist(path, state with
                {
                    X = bounds.X, Y = bounds.Y, W = bounds.Width, H = bounds.Height,
                });
            }

            window.Dispose();
        }

        _windows.Clear();
    }

    public void DeleteNote(string path)
    {
        if (!NotePath.TryCanonical(path, out var canonical)) return;

        var result = _deleter.SendToRecycleBin(canonical);

        if (result.Outcome is DeletionOutcome.Failed or DeletionOutcome.Cancelled)
        {
            // Never destroy a file, and never pretend to. Closing the window
            // here would leave the file behind with nothing on screen saying
            // so.
            DiagnosticsLog.Write(
                _diagnosticsFile,
                $"{canonical}: delete {result.Outcome} -- {result.Message ?? "no reason given"}");
            return;
        }

        if (_windows.Remove(canonical, out var window)) window.Dispose();

        // The entry goes too: geometry for a file in the Recycle Bin is
        // clutter, and restoring the file gives it a fresh default position.
        _index.Notes.Remove(canonical);
        _indexStore.Save(_index);

        _recovery.Clear(canonical);
    }

    private void OnRenameRequested(string currentPath, string newFileName)
    {
        if (!NotePath.TryCanonical(currentPath, out var canonical)) return;

        string renamed;

        try
        {
            renamed = _repository.Rename(canonical, newFileName);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            // The name is taken, or Windows will not accept it. Both files are
            // untouched, which is the property that matters.
            DiagnosticsLog.Write(
                _diagnosticsFile, $"{canonical}: rename refused -- {ex.Message}");
            return;
        }

        Rekey(canonical, renamed);
    }

    // ---- Watcher handlers -------------------------------------------------

    public void OnExternalChanged(string path)
    {
        if (!NotePath.TryCanonical(path, out var canonical)) return;
        if (!_windows.TryGetValue(canonical, out var window)) return;

        try
        {
            var content = NoteFile.Read(canonical);

            // The window compares hashes to decide whether to reload silently
            // or ask, and it uses ComputeToken on both sides -- ContentHash
            // covers the RAW bytes, so a CRLF file would look different from
            // its own content on every save.
            window.ApplyExternalContent(
                content.Text, MarkdownRenderer.ComputeToken(content.Text));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.Text.DecoderFallbackException)
        {
            DiagnosticsLog.Write(
                _diagnosticsFile,
                $"{canonical}: an external change could not be read -- {ex.Message}");
        }
    }

    public void OnDeleted(string path)
    {
        if (!NotePath.TryCanonical(path, out var canonical)) return;
        if (!_windows.TryGetValue(canonical, out var window)) return;

        // The file is gone; the note is NOT closed and isOpen is untouched.
        // The user still gets Recreate, and closing it for them would discard
        // the buffer -- which may be the only remaining copy.
        window.NotifyFileDeleted();
    }

    public void OnRenamed(string oldPath, string newPath)
    {
        if (!NotePath.TryCanonical(oldPath, out var oldCanonical)) return;
        if (!NotePath.TryCanonical(newPath, out var newCanonical)) return;

        Rekey(oldCanonical, newCanonical);
    }

    /// <summary>
    /// <see cref="NoteWatcher.Recovered"/>. Re-reads every OPEN note.
    /// </summary>
    /// <remarks>
    /// NoteWatcher deliberately does not decide which notes to re-read -- it
    /// has no idea which are open, and this is where that knowledge lives.
    /// FileSystemWatcher drops events when its internal buffer overflows, so
    /// after a recovery any open note may be stale and there is no way to know
    /// which.
    /// </remarks>
    public void OnWatcherRecovered(Exception cause)
    {
        DiagnosticsLog.Write(
            _diagnosticsFile,
            $"The file watcher was recreated after {cause.GetType().Name}: {cause.Message}. "
                + $"Re-reading {_windows.Count} open note(s).");

        foreach (var path in _windows.Keys.ToList()) OnExternalChanged(path);
    }

    /// <summary>
    /// <c>WM_DISPLAYCHANGE</c>, via <c>SystemEvents.DisplaySettingsChanged</c>.
    /// </summary>
    public void OnDisplaySettingsChanged()
    {
        var monitors = _monitors.GetMonitors();

        foreach (var (path, window) in _windows.ToList())
        {
            var bounds = window.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0) continue;

            var clamped = WindowPlacement.Clamp(bounds, monitors);

            // Only touch a note that actually moved. Reapplying a correct rect
            // would nudge every window on every display change.
            if (clamped == bounds) continue;

            if (!_index.Notes.TryGetValue(path, out var state)) continue;

            var updated = state with
            {
                X = clamped.X, Y = clamped.Y, W = clamped.Width, H = clamped.Height,
            };

            window.ApplyState(
                updated,
                NotePalette.Get(updated.Color, _theme.Resolve(_settings.Theme)));

            Persist(path, updated);
        }
    }

    // ---- Index plumbing ---------------------------------------------------

    private void OnStateChanged(string path, NoteState state)
    {
        if (!NotePath.TryCanonical(path, out var canonical)) return;
        Persist(canonical, state);
    }

    private void Rekey(string oldCanonical, string newCanonical)
    {
        if (_index.Notes.Remove(oldCanonical, out var state))
        {
            _index.Notes[newCanonical] = state;
            _indexStore.Save(_index);
        }

        if (!_windows.Remove(oldCanonical, out var window)) return;

        _windows[newCanonical] = window;

        // The window remaps note.local as part of this, or images in the moved
        // note silently stop loading.
        window.NotifyRenamed(newCanonical);
    }

    private NoteState ClampToMonitors(NoteState state)
    {
        var clamped = WindowPlacement.Clamp(
            new PixelRect(state.X, state.Y, state.W, state.H),
            _monitors.GetMonitors());

        return state with
        {
            X = clamped.X, Y = clamped.Y, W = clamped.Width, H = clamped.Height,
        };
    }

    private void Persist(string canonical, NoteState state)
    {
        _index.Notes[canonical] = state;
        _indexStore.Save(_index);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Dispose is NOT a close. It goes down the shutdown path, so isOpen
        // survives whatever caused it.
        ShutdownWithoutClosingNotes();
    }
}
```

- [ ] **Step 5: Write `NoteWindowFactory`**

`src/StickyMD.App/Windows/NoteWindowFactory.cs`:

```csharp
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
    INoteWindow Create(string canonicalPath, NoteState state, NoteTheme theme);
}

public sealed class NoteWindowFactory : INoteWindowFactory
{
    public INoteWindow Create(string canonicalPath, NoteState state, NoteTheme theme)
        => new NoteWindow(canonicalPath, state, theme);
}
```

- [ ] **Step 6: Run the manager tests**

```bash
dotnet test --filter FullyQualifiedName~WindowManagerTests
```

Expected: PASS, 26 tests.

`OfferRecovery`'s cast to `NoteWindow` makes that one line untestable through the fake, which is a smell. **Resolve it by adding `void ShowRecovered(RecoveryEnvelope envelope)` to `INoteWindow`** and implementing it on both `NoteWindow` (delegating to `ShowRecoveredContent`) and `FakeNoteWindow` (recording the call). Then add:

```csharp
    [Fact]
    public void A_surviving_recovery_snapshot_is_offered_to_the_window()
    {
        var path = WriteNote("n.md", "# on disk");
        var recovery = new RecoveryStore(Path.Combine(_root, "recovery"));
        recovery.Save(new RecoveryEnvelope(
            path, "# unsaved", DateTime.UtcNow, "STALEHASH"));

        var manager = Build();
        manager.OpenNote(path);

        _factory.For(path).RecoveryOffered?.Content.ShouldBe("# unsaved");
    }

    [Fact]
    public void A_snapshot_that_already_matches_disk_is_cleared_not_offered()
    {
        // RecoveryStore.Clear is best-effort, so a snapshot can outlive its
        // successful save. Offering it would present older text as a recovery.
        var path = WriteNote("n.md", "# same");
        var recovery = new RecoveryStore(Path.Combine(_root, "recovery"));
        var hash = NoteFile.Read(path).ContentHash;
        recovery.Save(new RecoveryEnvelope(path, "# same", DateTime.UtcNow, hash));

        var manager = Build();
        manager.OpenNote(path);

        _factory.For(path).RecoveryOffered.ShouldBeNull();
        recovery.TryLoad(path).ShouldBeNull();
    }
```

- [ ] **Step 7: Run the full suite**

```bash
dotnet test
```

Expected: `failed: 0`. Record the total.

- [ ] **Step 8: Commit**

Hand these to the user:

```bash
git add src/StickyMD.App tests/StickyMD.App.Tests
git commit -m "Add WindowManager: the three-state model, tested headlessly

WindowManager never references a WPF Window type -- only INoteWindow -- so
every behaviour below runs in the test runner. That is the point: none of
it can be tested through a real window, and the spec is explicit that
getting one of these wrong restores zero notes after an exit.

Only CloseNote clears isOpen. ShutdownWithoutClosingNotes is a separate
method for exactly that reason: WPF closes every window during application
shutdown, so close-glyph logic on Window.Closing would mean choosing Exit
clears isOpen on every note and the next boot restores none of them. It
saves buffers and geometry and leaves isOpen alone, and Dispose routes
through it rather than through CloseNote.

Hide All does not touch isOpen either. Hidden and closed are different
states and conflating them is the same bug in a smaller costume.

A note merely PRESENT in the notes root gets no window. Pointing StickyMD
at an Obsidian vault must not carpet the desktop.

An external deletion notifies the window and does NOT close it or clear
isOpen. The user still gets Recreate, and closing it for them would
discard a buffer that may be the only remaining copy of the text.

Watcher recovery re-reads every open note. NoteWatcher deliberately
refuses to choose -- it has no idea which notes are open -- and
FileSystemWatcher drops events on buffer overflow, so after a recovery any
open note may be stale with no way to know which.

A display change reclamps only notes that actually moved. Reapplying a
correct rect would nudge every window on every display change.

A failed or cancelled delete leaves the window open and the entry intact.
Closing on a failed delete would leave the file behind with nothing on
screen saying so.

A recovery snapshot whose LastKnownDiskHash and content both match the
file is cleared rather than offered. RecoveryStore.Clear is best-effort,
so a snapshot can outlive its own successful save, and offering it would
present older text to the user as a recovery."
```

---

## Task 14: The `⋯` menu, the real bootstrap, and the manual smoke checklist

Everything gets wired together, the temporary code comes out, and the whole thing is exercised by hand against spec §9's list.

**Files:**

- Modify: `src/StickyMD.App/Windows/NoteWindow.xaml.cs` (the `⋯` menu)
- Rewrite: `src/StickyMD.App/App.xaml.cs` (the real bootstrap)
- Modify: `docs/STATUS.md`
- Create: `docs/checklists/2026-09-02-plan-b-smoke.md`

**Interfaces:**

- Consumes: everything from Tasks 1–13.
- Produces: `NoteWindow.BuildMoreMenu()`, `App.Bootstrap`. No new public API.

- [ ] **Step 1: Build the `⋯` menu**

Exactly the six entries from the spec, in that order. Nothing else belongs there.

Add to `src/StickyMD.App/Windows/NoteWindow.xaml.cs`:

```csharp
    /// <summary>
    /// The spec's menu, verbatim: Rename…, Color ▸, Opacity ▸, Always on Top ☑,
    /// separator, Delete. Every entry maps to a v1 feature.
    /// </summary>
    private void ShowMoreMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();

        var rename = new System.Windows.Controls.MenuItem { Header = "Rename…" };
        rename.Click += (_, _) => PromptRename();
        menu.Items.Add(rename);

        var colours = new System.Windows.Controls.MenuItem { Header = "Color" };

        foreach (var colour in NotePalette.All)
        {
            var item = new System.Windows.Controls.MenuItem
            {
                Header = colour.ToString(),
                IsCheckable = true,
                IsChecked = colour == _state.Color,
            };

            var chosen = colour;
            item.Click += (_, _) => ApplyColour(chosen);
            colours.Items.Add(item);
        }

        menu.Items.Add(colours);

        var opacities = new System.Windows.Controls.MenuItem { Header = "Opacity" };

        // The floor is StateValidator.MinOpacity for a reason: below roughly
        // 20% a note is invisible and cannot be found with the mouse to be
        // fixed. Offering 10% here would let the user create a state they
        // cannot get out of.
        foreach (var value in new[] { 1.0, 0.9, 0.75, 0.5, 0.3 })
        {
            var item = new System.Windows.Controls.MenuItem
            {
                Header = $"{value * 100:0}%",
                IsCheckable = true,
                IsChecked = Math.Abs(_state.Opacity - value) < 0.001,
            };

            var chosen = value;
            item.Click += (_, _) => ApplyOpacity(chosen);
            opacities.Items.Add(item);
        }

        menu.Items.Add(opacities);

        var pin = new System.Windows.Controls.MenuItem
        {
            Header = "Always on Top",
            IsCheckable = true,
            IsChecked = _state.AlwaysOnTop,
        };
        pin.Click += (_, _) =>
        {
            _state = _state with { AlwaysOnTop = !_state.AlwaysOnTop };
            Topmost = _state.AlwaysOnTop;
            StateChanged?.Invoke(NotePath, _state);
        };
        menu.Items.Add(pin);

        menu.Items.Add(new System.Windows.Controls.Separator());

        var delete = new System.Windows.Controls.MenuItem { Header = "Delete" };
        delete.Click += (_, _) => ConfirmDelete();
        menu.Items.Add(delete);

        menu.PlacementTarget = MoreButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void ApplyColour(NoteColor colour)
    {
        _state = _state with { Color = colour };

        var theme = NotePalette.Get(colour, _resolvedMode);
        ApplyTheme(theme);

        // The WebView needs a fresh shell for the new CSS variables and a new
        // opaque backdrop, or the note's chrome and its content disagree about
        // what this colour is.
        _ = ReloadShellAsync();

        StateChanged?.Invoke(NotePath, _state);
    }

    private void ApplyOpacity(double value)
    {
        _state = _state with { Opacity = value };

        // Window.Opacity, never SetLayeredWindowAttributes. WS_EX_LAYERED
        // cannot be added post-creation on this platform, and WPF has already
        // applied it at CreateWindowEx because AllowsTransparency is True.
        Opacity = value;

        StateChanged?.Invoke(NotePath, _state);
    }

    private void PromptRename()
    {
        var dialog = new RenamePrompt(Path.GetFileNameWithoutExtension(NotePath))
        {
            Owner = this,
        };

        if (dialog.ShowDialog() != true) return;

        // The manager performs the rename: it owns the index key and the
        // open-notes map, and re-keying either from here would leave the other
        // stale.
        RenameRequested?.Invoke(NotePath, dialog.NewName);
    }

    private void ConfirmDelete()
    {
        var answer = MessageBox.Show(
            $"Send \"{Path.GetFileName(NotePath)}\" to the Recycle Bin?",
            "Delete note",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        if (answer != MessageBoxResult.OK) return;

        DeleteRequested?.Invoke(NotePath);
    }
```

`_resolvedMode` is a new field set from the theme the factory passed in; add `private ThemeMode _resolvedMode = ThemeMode.Light;` and set it in `ApplyState`. Wire the button in `WireHeader`:

```csharp
        MoreButton.Click += (_, _) => ShowMoreMenu();
        ColorButton.Click += (_, _) => ShowMoreMenu();
```

And add the rename prompt — a plain modal, since WPF has no input box:

`src/StickyMD.App/Windows/RenamePrompt.xaml`:

```xml
<Window x:Class="StickyMD.App.Windows.RenamePrompt"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Rename note"
        Width="360" SizeToContent="Height"
        WindowStartupLocation="CenterOwner"
        ResizeMode="NoResize"
        ShowInTaskbar="False">
  <StackPanel Margin="14">
    <TextBlock Text="New file name (without .md):" Margin="0,0,0,6" />
    <TextBox x:Name="NameBox" />
    <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,12,0,0">
      <Button x:Name="OkButton" Content="Rename" IsDefault="True"
              Width="80" Margin="0,0,6,0" />
      <Button Content="Cancel" IsCancel="True" Width="80" />
    </StackPanel>
  </StackPanel>
</Window>
```

`src/StickyMD.App/Windows/RenamePrompt.xaml.cs`:

```csharp
using System.Windows;

namespace StickyMD.App.Windows;

public partial class RenamePrompt : Window
{
    public RenamePrompt(string currentName)
    {
        InitializeComponent();

        NameBox.Text = currentName;
        NameBox.SelectAll();
        NameBox.Focus();

        OkButton.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(NameBox.Text)) return;

            NewName = NameBox.Text.Trim();
            DialogResult = true;
        };
    }

    public string NewName { get; private set; } = string.Empty;
}
```

Name validation is **not** duplicated here. `NoteRepository.Rename` already rejects path separators, invalid characters, and collisions, and doing it in two places is how the two places come to disagree. An invalid name is refused there and recorded in the diagnostics log.

- [ ] **Step 2: Write the real bootstrap**

Replace `src/StickyMD.App/App.xaml.cs` entirely:

```csharp
using System.Windows;
using StickyMD.App.Interop;
using StickyMD.App.Services;
using StickyMD.App.Windows;
using StickyMD.Core.Diagnostics;
using StickyMD.Core.Notes;
using StickyMD.Core.Persistence;

namespace StickyMD.App;

public partial class App : Application
{
    private WindowManager? _manager;
    private NoteWatcher? _watcher;
    private SystemTheme? _theme;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Check the runtime FIRST. Without it a note window opens and simply
        // never paints, which looks like a bug in the window rather than a
        // missing dependency.
        if (WebViewEnvironment.DetectRuntimeVersion() is null)
        {
            ShowRuntimeMissingDialog();
            return;
        }

        var settingsStore = new SettingsStore(AppPaths.SettingsFile);
        var settings = settingsStore.Load();

        var indexStore = new NoteIndexStore(AppPaths.NoteIndexFile);

        // Load the index with the validated settings, so an entry with an
        // unusable colour or size falls back to the user's defaults rather
        // than to the type's.
        _ = indexStore.Load(settings);

        ReportStartupState(settingsStore, indexStore);

        var repository = new NoteRepository(settings.NotesRoot, new SystemClock());

        try
        {
            repository.EnsureRootExists();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The app stays alive. Plan C opens Settings with a banner; until
            // then say so plainly and record it, rather than exiting and
            // leaving nothing behind.
            DiagnosticsLog.Write(
                AppPaths.DiagnosticsFile,
                $"The notes root '{settings.NotesRoot}' could not be created -- {ex.Message}");

            MessageBox.Show(
                $"StickyMD could not create its notes folder:\n\n{settings.NotesRoot}\n\n{ex.Message}",
                "StickyMD", MessageBoxButton.OK, MessageBoxImage.Warning);

            Shutdown();
            return;
        }

        var ledger = new WriteLedger();
        _theme = new SystemTheme();

        _manager = new WindowManager(
            repository,
            indexStore,
            settingsStore,
            new NoteWindowFactory(),
            new MonitorEnumerator(),
            _theme,
            new RecycleBinService(),
            ledger,
            new RecoveryStore(AppPaths.RecoveryDir),
            AppPaths.DiagnosticsFile);

        StartWatcher(repository, ledger);

        // SystemEvents raise on their own thread; every handler marshals to
        // the dispatcher before touching a window.
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        SessionEnding += OnSessionEnding;

        _manager.RestoreOpenNotes();

        // First run, or every note closed: give the user something. A new note
        // opens directly in edit mode with focus, per the spec.
        if (_manager.OpenPaths.Count == 0) _manager.CreateAndOpenNote();
    }

    private void StartWatcher(NoteRepository repository, IWriteLedger ledger)
    {
        _watcher = new NoteWatcher(repository.NotesRoot, ledger);

        // Every one of these fires on a watcher thread. Marshalling is not
        // optional -- touching a Window off the dispatcher throws
        // InvalidOperationException, and from a timer callback the throw is
        // unhandled.
        _watcher.ExternalChanged += path => Dispatch(() => _manager?.OnExternalChanged(path));
        _watcher.Deleted += path => Dispatch(() => _manager?.OnDeleted(path));
        _watcher.Renamed += (from, to) => Dispatch(() => _manager?.OnRenamed(from, to));
        _watcher.Recovered += cause => Dispatch(() => _manager?.OnWatcherRecovered(cause));
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        => Dispatch(() => _manager?.OnDisplaySettingsChanged());

    private void Dispatch(Action action)
        => Dispatcher.BeginInvoke(action);

    private void OnSessionEnding(object? sender, SessionEndingCancelEventArgs e)
    {
        // Windows logoff and shutdown follow the SHUTDOWN path. isOpen must
        // survive, or logging off would empty the desktop on the next logon.
        _manager?.ShutdownWithoutClosingNotes();
    }

    private static void ReportStartupState(
        SettingsStore settingsStore, NoteIndexStore indexStore)
    {
        // "Never die silently" covers quiet recovery too. Plan C turns these
        // into tray balloons; Plan B's obligation is that they exist on disk
        // rather than in nobody's hands.
        DiagnosticsLog.WriteAll(
            AppPaths.DiagnosticsFile,
            "settings.json corrections:",
            settingsStore.LastLoadIssues.Select(i => i.ToString()));

        DiagnosticsLog.WriteAll(
            AppPaths.DiagnosticsFile,
            "notes.json corrections:",
            indexStore.LastLoadIssues.Select(i => i.ToString()));

        if (settingsStore.LastCorruptBackupPath is { } settingsBackup)
        {
            DiagnosticsLog.Write(
                AppPaths.DiagnosticsFile,
                $"settings.json was unusable and was kept as {settingsBackup}; defaults are in use.");
        }

        if (indexStore.LastCorruptBackupPath is { } indexBackup)
        {
            // Notes are untouched -- only geometry is lost. Worth saying,
            // because "notes.json.corrupt-1 appeared" otherwise reads as data
            // loss.
            DiagnosticsLog.Write(
                AppPaths.DiagnosticsFile,
                $"notes.json was unusable and was kept as {indexBackup}. "
                    + "The .md files are untouched; only window positions were lost.");
        }
    }

    private void ShowRuntimeMissingDialog()
    {
        const string url = "https://developer.microsoft.com/microsoft-edge/webview2/";

        var answer = MessageBox.Show(
            "StickyMD needs the Microsoft Edge WebView2 runtime, which is not installed.\n\n"
                + "Open the download page?",
            "StickyMD", MessageBoxButton.OKCancel, MessageBoxImage.Error);

        if (answer == MessageBoxResult.OK)
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception) { /* nothing more to offer */ }
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // ShutdownWithoutClosingNotes, NEVER CloseNote. Exiting must not clear
        // isOpen -- that is the failure the three-state model exists to
        // prevent.
        _manager?.ShutdownWithoutClosingNotes();

        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SessionEnding -= OnSessionEnding;

        _watcher?.Dispose();
        _theme?.Dispose();
        _manager?.Dispose();

        base.OnExit(e);
    }
}
```

- [ ] **Step 3: Add the temporary exit and new-note affordances**

`ShutdownMode=OnExplicitShutdown` is correct per the spec, and Plan B has no tray icon — so without this the app cannot be exited except through Task Manager, and the shutdown path (the one that must not clear `isOpen`) could not be exercised at all.

Add to `NoteWindow.OnWindowPreviewKeyDown`:

```csharp
        // TEMPORARY (Plan B only). Removed in Plan C, which gives the tray
        // menu New Note and Exit. Without an exit path the shutdown behaviour
        // -- the one that must NOT clear isOpen -- cannot be tested by hand at
        // all, and Task Manager kills the process before OnExit runs.
        if ((Keyboard.Modifiers
                & (ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt))
            == (ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt))
        {
            if (e.Key == Key.Q)
            {
                Application.Current.Shutdown();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.N)
            {
                NewNoteRequested?.Invoke();
                e.Handled = true;
                return;
            }
        }
```

Add `public event Action? NewNoteRequested;` to `NoteWindow` (not to `INoteWindow` — it is temporary), and in `WindowManager.Instantiate`:

```csharp
        // TEMPORARY (Plan B only). Plan C's tray owns New Note.
        if (window is NoteWindow note)
            note.NewNoteRequested += () => CreateAndOpenNote();
```

- [ ] **Step 4: Build and run the full suite**

```bash
dotnet build
dotnet test
```

Expected: build clean with zero warnings, `failed: 0`. Record the total for the commit message and for `STATUS.md`.

- [ ] **Step 5: Write the smoke checklist**

`docs/checklists/2026-09-02-plan-b-smoke.md`:

```markdown
# Plan B manual smoke checklist

Run this at the end of Plan B and again after any change to window chrome,
WebView2 hosting, or the save path. Spec §9 puts windows, WebView2, the tray,
and hotkeys outside unit testing and covers them here instead.

**Environment this was written against:** Windows 11 10.0.26200, two
2560×1440 displays both at 100%, DISPLAY2 at x=2560, WebView2 runtime
151.0.4129.101, .NET SDK 10.0.400.

**Temporary Plan B keys:** `Ctrl+Shift+Alt+Q` exits, `Ctrl+Shift+Alt+N` makes
a new note. Both are removed in Plan C.

## Chrome and transparency

- [ ] A note opens, renders Markdown, and sits on top of other windows when pinned.
- [ ] The note is visibly translucent at 90% and at 50%, **content included**.
- [ ] Resizing while translucent keeps the content sharp and correctly laid out.
- [ ] A pinned note stays above a **fullscreen** application.
- [ ] Corners are rounded.
- [ ] Dragging the header moves the note; dragging any edge or corner resizes it.
- [ ] Hovering the header reveals the glyphs; leaving hides them.
- [ ] There is no white flash on first paint.

## The click-stealing regression check

Run these four together. **If all four fail, the drag handler moved to the
`Window`.** There is no error message when that happens.

- [ ] The mouse wheel scrolls the note content.
- [ ] The scrollbar thumb drags and scrolls, and does **not** move the window.
- [ ] Text inside the note can be selected by click-dragging.
- [ ] Clicking a task checkbox toggles it.

## Editing and saving

- [ ] `Ctrl+E` enters edit mode; `Esc` returns to preview with the edit visible.
- [ ] Double-clicking empty space enters edit mode; double-clicking a word selects the word instead.
- [ ] Typing, then waiting ~1s, puts the change on disk — verify in Notepad.
- [ ] `Ctrl+B` / `Ctrl+I` wrap and unwrap; empty selection leaves the caret between the markers.
- [ ] **`Ctrl+Z` after `Ctrl+B` undoes only the bold.**
- [ ] `Enter` continues `-`, `*`, `1.` and `- [ ]`; on an empty item it ends the list.
- [ ] `Tab` / `Shift+Tab` indent and outdent; `Shift+Tab` at column 0 does nothing; `Tab` never moves focus.
- [ ] Losing focus saves. Closing with `✕` saves.

## Checkboxes

- [ ] Clicking a box updates the `.md` file — verify the exact characters in Notepad.
- [ ] A nested box toggles itself and not its parent.
- [ ] `- [X]` (uppercase) toggles off.
- [ ] Rapid clicking never leaves a box disagreeing with the file.

## External edits

- [ ] Edit the note in VS Code and save: the note updates within ~200ms with **scroll preserved** and no bar.
- [ ] Type without saving, then edit and save in VS Code: the **Changed on disk** bar appears. **Reload** takes the file's version; **Keep Mine** writes yours to the file.
- [ ] Delete the note from Explorer: the **file is gone** bar appears. **Recreate** brings the file back with the buffer's content.
- [ ] Rename the note in Explorer: the title updates, the note stays open, and images in it still load.
- [ ] Move the note **out** of the folder: it reports as a deletion. Known v1 limitation; there is no Locate feature.

## Save failures

- [ ] Lock the file exclusively, type, and wait: the **Couldn't save** bar appears and the text stays in the editor.
- [ ] Release the lock and click **Retry**: it saves and the bar clears.
- [ ] With the file still locked, close the note: a snapshot appears under `%LOCALAPPDATA%\StickyMD\recovery\` containing the text and its `originalPath`.
- [ ] Release the lock, restart, reopen: the **Unsaved changes were recovered** bar appears. **Restore** writes it; **Discard** removes the snapshot.
- [ ] A later successful save deletes the snapshot file.

## Resources and links

- [ ] A relative image in the note directory loads.
- [ ] A `data:` image loads.
- [ ] A remote `https:` image shows a placeholder and the **remote images blocked** bar. Loading them works; reopening the note blocks them again.
- [ ] An absolute local path image (`C:\...`) shows a placeholder.
- [ ] `../outside.png` shows a placeholder. Known v1 limitation.
- [ ] An `https:` link opens in the default browser and the note does not navigate.
- [ ] A relative `.md` link opens that file as a second note.
- [ ] A `#anchor` link scrolls within the note.
- [ ] A `javascript:` link does nothing, and `diagnostics.log` records the refusal.
- [ ] Right-click shows no context menu; `F12` does nothing; `Ctrl+scroll` does not zoom.

## The `⋯` menu

- [ ] It contains exactly: Rename…, Color ▸, Opacity ▸, Always on Top ☑, ─, Delete.
- [ ] All seven colours apply to **both the chrome and the content**, and they match.
- [ ] Opacity applies immediately and survives a restart.
- [ ] Always on Top toggles and survives a restart.
- [ ] Rename moves the file on disk, updates the title, and keeps images loading.
- [ ] Renaming onto an existing name is refused and **both files survive intact**.
- [ ] Delete asks first, then sends the file to the **Recycle Bin** — confirm it is listed there and restorable.

## Geometry and monitors

- [ ] Move a note, close it, reopen it: it returns to the same place and size.
- [ ] Drag a note to DISPLAY2, restart: it comes back on DISPLAY2 at the same place.
- [ ] Unplug or disable DISPLAY2 with a note on it: the note is clamped onto the remaining display **without a restart**.
- [ ] Change resolution with notes open: any off-screen note is clamped.
- [ ] **Mixed DPI — UNVERIFIED on this hardware.** If a display at a different scale is ever available, drag a note between them, restart, and check the placement. Spike 0 could not exercise this; it remains an assumption.

## The three states

The headline hazard. Get any of these wrong and the desktop empties.

- [ ] Open three notes. Exit with `Ctrl+Shift+Alt+Q`. Restart. **All three come back.**
- [ ] Open three notes. Close one with `✕`. Restart. **Two come back.**
- [ ] Open three. Hide them all (no tray yet — skip, or exercise `HideAll` via a temporary key). Restart. **All three come back.**
- [ ] Log off and back on with notes open. **All of them come back.**
- [ ] Open the same note twice — by path and via a relative `.md` link. **One window, focused.**
- [ ] Add a `.md` to the notes root from Explorer. **No window appears.**

## Degradation

- [ ] A 3MB note opens in edit mode with the size bar and does not hang.
- [ ] An ANSI-encoded note opens read-only with the not-UTF-8 bar, and its bytes are unchanged afterwards.
- [ ] Kill the WebView2 renderer from Task Manager: the note recreates. Kill it twice: the note falls back to a plain-text pane and stays readable.
- [ ] Corrupt `notes.json` by hand: the app starts, the file is kept as `notes.json.corrupt-1`, **every `.md` is intact**, and `diagnostics.log` says only geometry was lost.
- [ ] Put `{"color": 99}` in a `notes.json` entry: the note opens in the default colour and `diagnostics.log` names the correction.
- [ ] Set `"opacity": 0.0`: the note opens at 20%, visible and clickable.

## Verify at the end

- [ ] `dotnet test` — record the total and confirm `failed: 0`.
- [ ] `dotnet build` — zero warnings.
- [ ] `grep -rn "System.Windows\|System.Drawing\|DllImport" src/StickyMD.Core/` returns nothing.
```

- [ ] **Step 6: Work through the checklist**

Run every item. **Record failures rather than fixing them silently** — several of the checks above are testing a specific claim from Spike 0 or the spec, and knowing _which_ one failed is the whole value.

- [ ] **Step 7: Update `STATUS.md`**

Edit `docs/STATUS.md`:

1. Change the plan table's Plan B row to **Complete** and Plan C's note to reflect that it is next.
2. Replace the "Open findings — resolve these in Plan B, not later" section with a **Resolved findings** section recording both decisions:
   - Path identity → `NotePath.Canonical` + `NotePath.Comparer`, applied at production. Note the symlink/8.3 limitation.
   - Validation → `StateValidator` + per-entry index loading + `LastLoadIssues` + `DiagnosticsLog`. Give the actual bounds.
3. Replace "What is left" with Plan C's scope only.
4. Add a **Contracts Plan C must honour** section:
   - `WindowManager.CloseNote` is the only thing that may clear `isOpen`; the tray's Exit and Hide All must use `ShutdownWithoutClosingNotes` and `HideAll`.
   - `NoteRepository.CreateNewRecorded` is not thread-safe; the hotkey must create notes on the UI thread, or add a lock.
   - `LastLoadIssues` on both stores is what the tray balloon should read.
   - The temporary `Ctrl+Shift+Alt+Q` / `Ctrl+Shift+Alt+N` keys and `NoteWindow.NewNoteRequested` must be **removed** when the tray lands.
   - `SystemTheme.Changed` and `SystemEvents.DisplaySettingsChanged` fire off the dispatcher; every handler marshals.
   - The remote-image opt-in is per note, per session, and deliberately unpersisted; the global one belongs in Settings.
   - `HtmlDocumentBuilder`'s CSP is per **shell**, so a Settings change to `allowRemoteImages` must re-navigate every open note's shell, not just re-render.
5. Update the test count to whatever `dotnet test` actually reported.
6. Point at the smoke checklist as the manual-verification record.

- [ ] **Step 8: Commit**

Hand these to the user:

```bash
git add src/StickyMD.App docs/STATUS.md docs/checklists
git commit -m "Wire the shell together: the more menu, the bootstrap, and status

The startup order is deliberate. The WebView2 runtime is checked before
anything else, because without it a note window opens and simply never
paints -- which reads as a bug in the window rather than a missing
dependency. The index is then loaded WITH the validated settings, so an
entry carrying an unusable colour or size falls back to the user's
defaults rather than to the type's.

A notes root that cannot be created does not exit the app. It says so and
records it, because exiting would leave nothing behind to explain
anything; Plan C opens Settings with a banner.

Both the exit path and SessionEnding call
ShutdownWithoutClosingNotes and never CloseNote. Logging off must not
empty the desktop on the next logon.

Every watcher and SystemEvents handler marshals to the dispatcher. Those
callbacks arrive on their own threads, and touching a Window off the
dispatcher throws -- from a timer callback the throw is unhandled and
takes the process with it.

The opacity menu floors at 30% and the validator at 20%, because below
roughly that a note is invisible and cannot be found with the mouse to be
fixed. Offering 10% would let the user build a state they cannot get out
of.

Rename goes through WindowManager rather than acting locally: it owns the
index key and the open-notes map, and re-keying one from the window would
leave the other stale. Name validation is not duplicated in the prompt
either -- NoteRepository.Rename already rejects separators, invalid
characters, and collisions, and two validators is how two validators come
to disagree.

Ctrl+Shift+Alt+Q and Ctrl+Shift+Alt+N are marked temporary and are
removed in Plan C. They exist because ShutdownMode is OnExplicitShutdown
and there is no tray yet, so without them the shutdown path -- the one
that must not clear isOpen -- could not be exercised by hand at all, and
Task Manager kills the process before OnExit runs.

STATUS.md records both previously-open findings as resolved, with the
decisions and their limits, and adds the contracts Plan C has to honour."
```

---

## Self-Review

Run after the plan is written, before execution starts.

### 1. Spec coverage

| Spec section                               | Where it lands                                                                       |
| ------------------------------------------ | ------------------------------------------------------------------------------------ |
| §4 `WindowManager`                         | Task 13                                                                              |
| §4 `MonitorEnumerator`                     | Task 6                                                                               |
| §4 `RecycleBinService`                     | Task 7                                                                               |
| §4 `WebViewHost`                           | Task 8                                                                               |
| §4 `HtmlDocumentBuilder`                   | Task 4                                                                               |
| §5 geometry in physical pixels             | Task 6 (`WindowGeometry`), Task 9 (applied), Task 13 (clamped and persisted)         |
| §5 three states                            | Task 13, with the shutdown-vs-close split tested                                     |
| §5 new-note filenames                      | Task 13 `CreateAndOpenNote` (via Core's `CreateNewRecorded`)                         |
| §5 self-write suppression                  | Task 10 (`SaveCoordinator` records every write), Task 13 (creation write)            |
| §6 chrome, header, drag                    | Task 9                                                                               |
| §6.2 transparency                          | Task 9 (window), Task 8 (backdrop)                                                   |
| §6 mode toggle                             | Task 10                                                                              |
| §6 render pipeline, postMessage            | Task 4 (shell), Task 8 (delivery)                                                    |
| §6 WebView2 lockdown                       | Task 8 `ApplyLockdown`                                                               |
| §6 resource and navigation policy          | Task 5 (decision), Task 8 (`NavigationStarting`), Task 11 (link handling)            |
| §6 CSP built from the setting              | Task 4, with the per-shell resolution stated                                         |
| §6 checkbox write-back                     | Task 11                                                                              |
| §6 editor conveniences                     | Task 10                                                                              |
| §6 autosave                                | Task 10                                                                              |
| §7 theming, light/dark/system              | Task 6 (`SystemTheme`), Task 9 (chrome), Task 4 (content)                            |
| §7 tray, hotkeys, startup, single instance | **Plan C.** Explicitly out of scope, stated in the header                            |
| §8 every window-owned row                  | Task 12; `notes.json`/`settings.json` corruption in Task 3 and Task 14               |
| §8.1 recovery snapshots                    | Task 10 (write/clear), Task 13 (offer, with the stale-snapshot guard), Task 12 (bar) |
| §8.2 moves outside the watcher             | Task 12 (`file-gone` bar), recorded as the known limitation                          |
| §9 not-unit-tested list                    | Task 14's checklist                                                                  |
| §10 target frameworks, packages            | Task 1                                                                               |

**Gaps, deliberately:** the tray, hotkeys, the Run key, single instance, the Settings window, and distribution are Plan C. `monitor` in `notes.json` is written but not read — unchanged from the spec, which says so.

### 2. Placeholder scan

No `TBD`, no "add appropriate error handling", no "similar to Task N". Every code step carries real code. The four handler stubs in Task 9 (`OnTaskToggleRequested`, `OnLinkClicked`, `OnEditRequested`, `OnFellBackToPlainText`) are empty **and say which task fills them** — Tasks 10, 11 and 12 give each one a complete body.

### 3. Type consistency

Checked across tasks:

- `NotePath.Canonical` / `.Comparer` / `.NewMap` — Task 2, used verbatim in 3, 5, 7, 8, 13.
- `ValidationIssue(Scope, Field, Detail)` — Task 3, read in Task 14's `ReportStartupState`.
- `HtmlShellOptions(Theme, AllowRemoteImages, VirtualHost, FontFamily, FontSizePx)` — Task 4, constructed in Task 8.
- `RenderResult(Html, Token, BlockedRemoteImages)` — Task 4, consumed in Tasks 8, 11, 12.
- `NavigationDecision(Action, Target, Reason)` and both `Decide*` methods — Task 5, called in Tasks 8 and 11.
- `IMonitorProvider`, `ISystemTheme` — Task 6, injected in Task 13.
- `IFileDeletionService` / `DeletionResult` — Task 7, injected in Task 13.
- `WebViewHost.SetThemeAsync(theme, allowRemoteImages, backdrop)` — Task 8, called in Tasks 9 and 12.
- `INoteWindow` — declared in Task 9, extended once in Task 13 (`ShowRecovered`), and both `NoteWindow` and `FakeNoteWindow` implement the same set.
- `SaveOutcome(Status, DiskHash, Message, Attempts)` — Task 10, consumed in Task 12's bar hookup.
- `ToggleDecision(Verdict, Markdown, Reason)` — Task 11 only.
- `ExternalChangeAction` — Task 12 only.
- `INoteWindowFactory.Create(canonicalPath, state, theme)` — Task 13, same signature in `NoteWindowFactory` and `FakeNoteWindowFactory`.

One inconsistency found and corrected inside Task 12: `ExternalChangePolicy.Decide` is called with `MarkdownRenderer.ComputeToken` on **both** sides, not with `NoteContent.ContentHash` on one — those hash different bytes (normalised text vs. raw on-disk bytes), and mixing them would report a conflict on every save for anyone with a CRLF file. Task 13's `OnExternalChanged` passes `ComputeToken(content.Text)` to match.

### 4. Findings

Both of Plan A's open findings are resolved in Tasks 2 and 3 — before any WPF code exists, because every later task depends on the answers — and each has behaviour tests, not just unit tests: `PathIdentityTests` proves a repository path and a watcher path hit the same map entry, and `WindowManagerTests` proves opening one note through two spellings yields one window.

---
