# StickyMD Plan A — Spike, Scaffold, and Core Implementation Plan

**Goal:** Verify the transparency approach, scaffold the solution, and build a fully tested `StickyMD.Core` library containing every piece of StickyMD logic that does not touch WPF or Win32.

**Architecture:** Two-project solution. `StickyMD.Core` targets plain `net10.0` — not `net10.0-windows` — which makes the spec's "zero WPF/Win32 in Core" boundary a **compiler-enforced** rule rather than a convention. Everything in this plan is reachable from a headless test runner. The WPF shell (Plan B) and the shell services (Plan C) consume Core through the interfaces produced here.

**Tech Stack:** .NET 10 (LTS), C#, Markdig, System.Text.Json, xUnit, Shouldly.

**Source spec:** `docs/specs/2026-08-22-stickymd-design.md`

## Plan Sequence

| Plan              | Scope                                                                                                                        | Ends with            |
| ----------------- | ---------------------------------------------------------------------------------------------------------------------------- | -------------------- |
| **A (this plan)** | Spike 0, solution scaffold, all of `StickyMD.Core`                                                                           | A tested library     |
| B                 | WPF shell: `NoteWindow`, `AllowsTransparency` chrome, `WebView2CompositionControl` host, render/edit toggle, checkbox bridge | A usable sticky note |
| C                 | Tray, hotkeys, startup, single instance, Settings window, distribution                                                       | The finished app     |

### Read this before starting: Plans B and C are NOT yet written

Only Plan A exists as a file. **Do not go looking for `plan-b-*.md` or `plan-c-*.md` — they do not exist, and you must not invent them.** The table above is a roadmap, not an index.

This is deliberate: writing B before A executes would mean guessing the shape Core actually took.

**When Task 18 is finished, stop.** Do not begin WPF work. Report to the user:

1. That Plan A is complete and the full test suite passes (give the count).
2. Anything a task discovered that Plan B needs to know — in particular whether Task 16's `Task_checkbox_span_indexes_exactly_the_bracket_token` passed as written or needed its span arithmetic adjusted, since Plan B's checkbox bridge depends on that convention.
3. That Plan B should now be written, scoped to: `NoteWindow` chrome, `MonitorEnumerator`, `WebViewHost` with the shared `CoreWebView2Environment`, `HtmlDocumentBuilder` (Core, but written there alongside its consumer), the render/edit mode toggle, the checkbox message bridge, `IFileDeletionService` for Recycle Bin deletion, and the resource/navigation policy from spec §6.

**Plan B must start from the Spike 0 findings, not from intuition.** Read
`docs/spikes/2026-08-22-spike-0-transparency.md` first. The non-negotiables it
established:

- `StickyMD.App` targets **`net10.0-windows10.0.17763.0`**
- `AllowsTransparency=True`, `WindowStyle=None`, opacity via **`Window.Opacity`**
- **`WebView2CompositionControl`**, never the plain `WebView2`
- `DefaultBackgroundColor` **opaque** — so no per-pixel transparency; rounded corners via DWM
- `WindowChrome` is **required** for any resizing to work at all
- **Drag handler on the header element only** — a `Window`-level one silently kills all clicks
  inside the note

## Global Constraints

Every task's requirements implicitly include this section. Values are copied verbatim from the spec.

- **Target framework:** `.NET 10` (LTS), SDK **10.0.400 already installed**. `StickyMD.Core` targets `net10.0`; test project targets `net10.0`. **Plan B's `StickyMD.App` must target `net10.0-windows10.0.17763.0`** — the Windows version suffix is required by `WebView2CompositionControl`; plain `net10.0-windows` throws `FileNotFoundException: Microsoft.Windows.SDK.NET` at runtime. Nothing in Plan A needs it.
- **NuGet:** package sources are **disabled machine-wide** (`%APPDATA%\NuGet\nuget.config` has an empty `<packageSources>`, a deliberate posture that stays untouched). A repo-scoped `NuGet.config` at the solution root — **already created** — enables `nuget.org` for this repo only. Without it every `dotnet add package` fails with "There are no versions available".
- **Core purity:** `StickyMD.Core` must reference **zero** WPF, WinForms, or Win32 APIs. `System.IO` and `System.Security.Cryptography` are permitted; `System.Windows.*`, `System.Drawing.*`, and `[DllImport]` are not.
- **Hashing:** SHA-256 for both the self-write fingerprint and the render token.
- **App data root:** `%LOCALAPPDATA%\StickyMD\` — never `%APPDATA%`.
- **Notes root default:** `%USERPROFILE%\StickyMD Notes\`, configurable.
- **Nothing app-owned is ever written into the notes root.** Only `.md` files the user created.
- **New file format:** UTF-8 **without** BOM, CRLF, trailing newline.
- **Existing file format is preserved:** encoding, BOM presence, CRLF-vs-LF, trailing-newline-or-not.
- **Note colors:** exactly seven — `yellow`, `green`, `blue`, `pink`, `purple`, `gray`, `charcoal` — each with a light and a dark variant.
- **Watcher debounce:** 150ms.
- **Autosave debounce:** 500ms.
- **Settings defaults:** `defaultColor` = `yellow`, `defaultOpacity` = `1.0`, `defaultSize` = `300×340`, `allowRemoteImages` = `false`.
- **`launchAtStartup` must NOT appear in `settings.json`** — the registry is the single source of truth.
- **Markdig pipeline:** `UseAdvancedExtensions()`, `UseSoftlineBreakAsHardlineBreak()`, `UsePreciseSourceLocation()`, `DisableHtml()`.
- **Virtual host mapping** uses `DenyCors`, never `Allow`.
- **Governing rule for all error paths:** never lose text, never destroy a file, never die silently.

## Before You Start: Git

**This directory is not yet a git repository, and this plan commits after every task.** Task 2 Step 1 runs `git init`. Confirm with the user before running any `git init`, `git add`, or `git commit` step in this plan — do not run them unprompted.

---

## File Structure

| File                                                | Responsibility                                     |
| --------------------------------------------------- | -------------------------------------------------- |
| `StickyMD.sln`                                      | Solution                                           |
| `Directory.Build.props`                             | Shared TFM, nullable, warnings-as-errors           |
| `.gitignore`, `.editorconfig`                       | Standard .NET hygiene                              |
| `spike/TransparencySpike/`                          | **Throwaway.** Task 1 only; deleted in Task 2      |
| `src/StickyMD.Core/Notes/NoteTitleResolver.cs`      | Content → display title                            |
| `src/StickyMD.Core/Notes/NoteFormat.cs`             | Encoding/newline/trailing-newline descriptor       |
| `src/StickyMD.Core/Notes/NoteFile.cs`               | Format-preserving read + atomic write              |
| `src/StickyMD.Core/Notes/WriteLedger.cs`            | Self-write fingerprints                            |
| `src/StickyMD.Core/Notes/NoteWatcher.cs`            | Debounced external-change events                   |
| `src/StickyMD.Core/Notes/NoteRepository.cs`         | Enumerate root, create, rename                     |
| `src/StickyMD.Core/Editing/MarkdownEditOps.cs`      | Bold/italic/list-continue/indent as pure functions |
| `src/StickyMD.Core/Markdown/MarkdownRenderer.cs`    | Markdig pipeline + span-carrying checkboxes        |
| `src/StickyMD.Core/Markdown/HtmlDocumentBuilder.cs` | Shell HTML, CSP, theme vars                        |
| `src/StickyMD.Core/Markdown/TaskListToggler.cs`     | Span-targeted checkbox flip                        |
| `src/StickyMD.Core/Persistence/NoteIndex.cs`        | Index model                                        |
| `src/StickyMD.Core/Persistence/NoteIndexStore.cs`   | `notes.json` load/save/recover                     |
| `src/StickyMD.Core/Persistence/AppSettings.cs`      | Settings model + defaults                          |
| `src/StickyMD.Core/Persistence/SettingsStore.cs`    | `settings.json` load/save/recover                  |
| `src/StickyMD.Core/Persistence/RecoveryStore.cs`    | Self-describing unsaved-buffer envelopes           |
| `src/StickyMD.Core/Geometry/WindowPlacement.cs`     | Clamp a rect to supplied monitors                  |
| `src/StickyMD.Core/Theming/NotePalette.cs`          | The 14 themes, single source of truth              |
| `tests/StickyMD.Core.Tests/**`                      | One test file per unit above                       |
| `tests/StickyMD.Core.Tests/TestSupport/TempDir.cs`  | Disposable temp directory                          |
| `tests/StickyMD.Core.Tests/TestSupport/Wait.cs`     | Poll-until-condition helper                        |

---

## Task 1: Spike 0 — COMPLETE, do not redo

**Status: DONE on 2026-08-22. Verdict: PASS via a revised approach.**

Full evidence: `docs/spikes/2026-08-22-spike-0-transparency.md`

**Do not recreate the spike project.** It served its purpose and was deleted. The findings below
are the deliverable, and spec §6.2 has already been rewritten against them.

### What it established

The approach originally specced — `WS_EX_LAYERED` + `SetLayeredWindowAttributes` on a window
with `AllowsTransparency=False` — is **impossible** on Windows 11 26200. `SetWindowLong` reports
success (`lastErr = 0`, returns the prior ex-style) and changes nothing. Proven not to be
WebView2's fault: the same call sets `WS_EX_TOOLWINDOW` and `WS_EX_TRANSPARENT` fine, and a bare
WPF window with no WebView2 anywhere fails identically.

**The working configuration**, verified end to end:

```
TFM            net10.0-windows10.0.17763.0     NOT plain net10.0-windows
Window         AllowsTransparency = true       (WPF layers it at CreateWindowEx)
               WindowStyle        = None
Opacity        Window.Opacity                  NOT SetLayeredWindowAttributes
Control        WebView2CompositionControl      NOT the plain WebView2
Backdrop       DefaultBackgroundColor opaque   (alpha 0 composites to black)
Resize         WindowChrome { CaptionHeight = 0, ResizeBorderThickness = 6 }
Drag           attach to the HEADER element    NEVER to the Window
```

Verified working: rendering (headings, tables, code, checkboxes, links), opacity at 90/50/30%,
dynamic repaint, edge resize, header drag, wheel scroll, scrollbar dragging, checkbox `onclick`,
link hover, second monitor, DWM rounded corners.

**Not verified:** mixed-DPI — both test monitors run at 100%, so it was not exercisable.

### Two traps recorded for Plan B

1. **Plain `net10.0-windows` throws** `FileNotFoundException: Microsoft.Windows.SDK.NET` from
   inside `TryInitializeD3DImage()`. The stack points at D3D and never mentions the TFM.
2. **A `Window`-level `MouseLeftButtonDown` → `DragMove()` handler silently steals every click
   from the WebView.** No error, no warning. Task checkboxes stop toggling, scrollbar drags move
   the window, text selection dies. Scope drag to the header element.

### Environment confirmed

|                  |                                                 |
| ---------------- | ----------------------------------------------- |
| .NET SDK         | 10.0.400 installed                              |
| Desktop runtime  | Microsoft.WindowsDesktop.App 10.0.11            |
| WebView2 Runtime | 151.0.4129.101 present — no bootstrapper needed |
| OS               | Windows 11 10.0.26200.0                         |
| Monitors         | 2 × 2560×1440, both 100%, DISPLAY2 at x=2560    |

- [x] **Complete.** Proceed to Task 2.

---

## Task 2: Solution scaffold

**Files:**

- Create: `.gitignore`, `.editorconfig`, `Directory.Build.props`, `StickyMD.sln`
- Create: `src/StickyMD.Core/StickyMD.Core.csproj`
- Create: `tests/StickyMD.Core.Tests/StickyMD.Core.Tests.csproj`
- Create: `tests/StickyMD.Core.Tests/TestSupport/TempDir.cs`, `Wait.cs`
- Create: `tests/StickyMD.Core.Tests/ScaffoldTests.cs`
- **Already exists, do not recreate:** `NuGet.config` at the solution root. Created during Task 1 because no package could be restored without it. Verify it is present before Step 5 — `dotnet nuget list source` must report `nuget.org [Enabled]`.
- Delete: `spike/` (its job is done; the verdict lives in `docs/spikes/`)

**Interfaces:**

- Consumes: the Task 1 verdict (recorded in docs, not code).
- Produces: `TempDir` and `Wait` test helpers used by every I/O task in this plan:
  - `sealed class TempDir : IDisposable` with `string Path { get; }`, `string File(string name)`
  - `static class Wait` with `static void Until(Func<bool> condition, string because, int timeoutMs = 3000)`

- [ ] **Step 1: Initialize the repository**

**Confirm with the user before running this.**

```bash
cd C:/Users/Eyal/dev/sticky-md
git init
```

- [ ] **Step 2: Write `.gitignore`**

```gitignore
bin/
obj/
.vs/
*.user
TestResults/
artifacts/
publish/
```

- [ ] **Step 3: Write `.editorconfig`**

```ini
root = true

[*]
charset = utf-8
end_of_line = crlf
insert_final_newline = true
indent_style = space
indent_size = 4
trim_trailing_whitespace = true

[*.{json,yml,yaml,md}]
indent_size = 2

[*.cs]
dotnet_sort_system_directives_first = true
csharp_style_namespace_declarations = file_scoped:warning
dotnet_diagnostic.CA1707.severity = none
```

- [ ] **Step 4: Write `Directory.Build.props`**

`TreatWarningsAsErrors` is deliberate: it is what actually stops a stray `using System.Windows;` from slipping into Core.

```xml
<Project>
  <PropertyGroup>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
</Project>
```

- [ ] **Step 5: Create the projects and solution**

`net10.0`, not `net10.0-windows` — this is what makes Core's purity compiler-enforced.

```bash
cd C:/Users/Eyal/dev/sticky-md
dotnet new classlib -o src/StickyMD.Core -n StickyMD.Core -f net10.0
dotnet new xunit -o tests/StickyMD.Core.Tests -n StickyMD.Core.Tests -f net10.0
dotnet new sln -n StickyMD
dotnet sln add src/StickyMD.Core/StickyMD.Core.csproj
dotnet sln add tests/StickyMD.Core.Tests/StickyMD.Core.Tests.csproj
dotnet add tests/StickyMD.Core.Tests/StickyMD.Core.Tests.csproj reference src/StickyMD.Core/StickyMD.Core.csproj
dotnet add tests/StickyMD.Core.Tests/StickyMD.Core.Tests.csproj package Shouldly
dotnet add src/StickyMD.Core/StickyMD.Core.csproj package Markdig
```

Delete the placeholder files `dotnet new` generates:

```bash
rm src/StickyMD.Core/Class1.cs
rm tests/StickyMD.Core.Tests/UnitTest1.cs
```

- [ ] **Step 6: Write the `TempDir` test helper**

`tests/StickyMD.Core.Tests/TestSupport/TempDir.cs`:

```csharp
namespace StickyMD.Core.Tests.TestSupport;

/// <summary>A unique temp directory that deletes itself on dispose.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "stickymd-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public string WriteFile(string name, string content)
    {
        var full = File(name);
        System.IO.File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { /* a watcher may still hold a handle; harmless */ }
        catch (UnauthorizedAccessException) { /* ditto */ }
    }
}
```

- [ ] **Step 7: Write the `Wait` test helper**

Polling, never `Thread.Sleep` followed by a bare assert — a sleep-and-assert test is either slow or flaky, usually both.

`tests/StickyMD.Core.Tests/TestSupport/Wait.cs`:

```csharp
namespace StickyMD.Core.Tests.TestSupport;

public static class Wait
{
    /// <summary>Polls until the condition holds, or fails the test on timeout.</summary>
    public static void Until(Func<bool> condition, string because, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return;
            Thread.Sleep(15);
        }

        throw new Xunit.Sdk.XunitException(
            $"Timed out after {timeoutMs}ms waiting for: {because}");
    }

    /// <summary>
    /// Waits out a window in which the condition must NOT become true.
    /// Used to prove an event does not fire.
    /// </summary>
    public static void StaysFalse(Func<bool> condition, string because, int forMs = 600)
    {
        var deadline = Environment.TickCount64 + forMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
                throw new Xunit.Sdk.XunitException($"Expected to stay false: {because}");
            Thread.Sleep(15);
        }
    }
}
```

- [ ] **Step 8: Write a scaffold test proving the harness works**

`tests/StickyMD.Core.Tests/ScaffoldTests.cs`:

```csharp
using Shouldly;
using StickyMD.Core.Tests.TestSupport;

namespace StickyMD.Core.Tests;

public class ScaffoldTests
{
    [Fact]
    public void TempDir_creates_and_cleans_up()
    {
        string path;
        using (var dir = new TempDir())
        {
            path = dir.Path;
            Directory.Exists(path).ShouldBeTrue();
            dir.WriteFile("a.md", "hello");
            File.ReadAllText(dir.File("a.md")).ShouldBe("hello");
        }

        Directory.Exists(path).ShouldBeFalse();
    }

    [Fact]
    public void Wait_Until_returns_when_condition_becomes_true()
    {
        var flag = false;
        var t = Task.Run(async () => { await Task.Delay(50); flag = true; });

        Wait.Until(() => flag, "flag to be set");

        t.Wait();
        flag.ShouldBeTrue();
    }

    [Fact]
    public void Wait_StaysFalse_passes_when_nothing_happens()
    {
        Wait.StaysFalse(() => false, "nothing to happen", forMs: 100);
    }
}
```

- [ ] **Step 9: Run the tests**

```bash
cd C:/Users/Eyal/dev/sticky-md
dotnet test
```

Expected: PASS, 3 tests.

- [ ] **Step 10: Delete the spike**

Its verdict is recorded in `docs/spikes/`. The code has no further value and would rot.

```bash
rm -rf spike
```

- [ ] **Step 11: Commit**

```bash
git add .gitignore .editorconfig Directory.Build.props NuGet.config StickyMD.sln src tests docs
git commit -m "chore: scaffold solution with Core and test projects

Core targets net10.0 (not net10.0-windows) so the zero-WPF/Win32
boundary is enforced by the compiler.

Includes the repo-scoped NuGet.config: this machine has package
sources disabled globally, which is left untouched."
```

---

## Task 3: NoteTitleResolver

A note's display title comes from its content, not its filename. This is pure string work with no dependencies, which makes it the right first real unit.

**Files:**

- Create: `src/StickyMD.Core/Notes/NoteTitleResolver.cs`
- Test: `tests/StickyMD.Core.Tests/Notes/NoteTitleResolverTests.cs`

**Interfaces:**

- Consumes: nothing.
- Produces: `static string NoteTitleResolver.Resolve(string content, string filePath)` — used by Plan B's `NoteViewModel` for the header, and Plan C's tray Recent Notes list.

**Resolution order** (first match wins):

1. First ATX `# ` heading outside a fenced code block
2. First setext H1 (a non-empty line followed by a line of `=`), outside a fence
3. First non-empty line, with leading `#` and whitespace stripped, truncated to 60 characters
4. Filename without extension

- [ ] **Step 1: Write the failing tests**

`tests/StickyMD.Core.Tests/Notes/NoteTitleResolverTests.cs`:

````csharp
using Shouldly;
using StickyMD.Core.Notes;

namespace StickyMD.Core.Tests.Notes;

public class NoteTitleResolverTests
{
    private const string Path = @"C:\notes\my-file.md";

    [Fact]
    public void Uses_first_atx_h1()
        => NoteTitleResolver.Resolve("# Hello world\n\nbody", Path)
            .ShouldBe("Hello world");

    [Fact]
    public void Skips_leading_blank_lines_before_h1()
        => NoteTitleResolver.Resolve("\n\n# Hello\n", Path).ShouldBe("Hello");

    [Fact]
    public void Trims_trailing_hashes_from_closed_atx_heading()
        => NoteTitleResolver.Resolve("# Hello ###\n", Path).ShouldBe("Hello");

    [Fact]
    public void Uses_setext_h1()
        => NoteTitleResolver.Resolve("Hello world\n===========\n\nbody", Path)
            .ShouldBe("Hello world");

    [Fact]
    public void Ignores_headings_inside_fenced_code()
        => NoteTitleResolver.Resolve("```\n# not a title\n```\n# Real title\n", Path)
            .ShouldBe("Real title");

    [Fact]
    public void Ignores_headings_inside_tilde_fenced_code()
        => NoteTitleResolver.Resolve("~~~\n# nope\n~~~\n# Yes\n", Path)
            .ShouldBe("Yes");

    [Fact]
    public void Falls_back_to_first_non_empty_line_stripping_hashes()
        => NoteTitleResolver.Resolve("## Sub heading only\n\nbody", Path)
            .ShouldBe("Sub heading only");

    [Fact]
    public void Falls_back_to_first_non_empty_line_for_plain_text()
        => NoteTitleResolver.Resolve("\n   just some text\nmore\n", Path)
            .ShouldBe("just some text");

    [Fact]
    public void Truncates_long_fallback_to_60_chars()
    {
        var line = new string('x', 100);

        NoteTitleResolver.Resolve(line, Path).Length.ShouldBe(60);
    }

    [Fact]
    public void Does_not_truncate_a_real_heading()
    {
        var heading = new string('y', 100);

        NoteTitleResolver.Resolve("# " + heading, Path).ShouldBe(heading);
    }

    [Fact]
    public void Falls_back_to_filename_when_content_is_empty()
        => NoteTitleResolver.Resolve("", Path).ShouldBe("my-file");

    [Fact]
    public void Falls_back_to_filename_when_content_is_whitespace()
        => NoteTitleResolver.Resolve("\n\t  \n \n", Path).ShouldBe("my-file");

    [Fact]
    public void Handles_crlf_line_endings()
        => NoteTitleResolver.Resolve("# Hello\r\n\r\nbody\r\n", Path).ShouldBe("Hello");

    [Fact]
    public void Setext_underline_must_be_all_equals()
        => NoteTitleResolver.Resolve("Hello\n=== not underline\n", Path)
            .ShouldBe("Hello");
}
````

The last test deserves a note: `=== not underline` is not a valid setext underline, so rule 2 does not fire — but rule 3 (first non-empty line) returns `"Hello"` anyway. The test pins that the _result_ is right regardless of which rule produced it.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test --filter FullyQualifiedName~NoteTitleResolverTests
```

Expected: FAIL to compile — `The type or namespace name 'NoteTitleResolver' could not be found`.

- [ ] **Step 3: Write the implementation**

`src/StickyMD.Core/Notes/NoteTitleResolver.cs`:

````csharp
namespace StickyMD.Core.Notes;

/// <summary>
/// Derives a note's display title from its content. The title is independent
/// of the filename; see the spec section "New note filenames".
/// </summary>
public static class NoteTitleResolver
{
    private const int MaxFallbackLength = 60;

    public static string Resolve(string content, string filePath)
    {
        var lines = (content ?? string.Empty).Split('\n');
        var inFence = false;
        string? firstNonEmpty = null;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var trimmed = line.Trim();

            if (IsFenceDelimiter(trimmed))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence || trimmed.Length == 0) continue;

            if (trimmed.StartsWith("# ", StringComparison.Ordinal) || trimmed == "#")
            {
                var heading = trimmed[1..].Trim().TrimEnd('#').Trim();
                if (heading.Length > 0) return heading;
            }

            if (i + 1 < lines.Length && IsSetextUnderline(lines[i + 1].TrimEnd('\r')))
                return trimmed;

            firstNonEmpty ??= trimmed;
        }

        if (firstNonEmpty is not null)
        {
            var stripped = firstNonEmpty.TrimStart('#').Trim();
            if (stripped.Length == 0) stripped = firstNonEmpty;
            return stripped.Length > MaxFallbackLength
                ? stripped[..MaxFallbackLength]
                : stripped;
        }

        return Path.GetFileNameWithoutExtension(filePath);
    }

    private static bool IsFenceDelimiter(string trimmed)
        => trimmed.StartsWith("```", StringComparison.Ordinal)
        || trimmed.StartsWith("~~~", StringComparison.Ordinal);

    private static bool IsSetextUnderline(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length > 0 && trimmed.All(c => c == '=');
    }
}
````

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test --filter FullyQualifiedName~NoteTitleResolverTests
```

Expected: PASS, 14 tests.

- [ ] **Step 5: Commit**

```bash
git add src/StickyMD.Core/Notes/NoteTitleResolver.cs tests/StickyMD.Core.Tests/Notes/NoteTitleResolverTests.cs
git commit -m "feat(core): derive note display titles from content"
```

---

## Task 4: NotePalette

The single source of truth for all seven colors. Both the WPF chrome (Plan B) and the rendered HTML read from here, which is what stops "WPF yellow" and "WebView yellow" from drifting apart.

**Files:**

- Create: `src/StickyMD.Core/Theming/NotePalette.cs`
- Test: `tests/StickyMD.Core.Tests/Theming/NotePaletteTests.cs`

**Interfaces:**

- Consumes: nothing.
- Produces:
  - `enum NoteColor { Yellow, Green, Blue, Pink, Purple, Gray, Charcoal }`
  - `enum ThemeMode { Light, Dark }`
  - `sealed record NoteTheme(string ChromeBg, string ChromeFg, string Border, string ContentBg, string ContentFg, string Accent, string CodeBg, string Muted)`
  - `static NoteTheme NotePalette.Get(NoteColor color, ThemeMode mode)`
  - `static IReadOnlyDictionary<string, string> NotePalette.ToCssVariables(NoteTheme theme)`
  - `static IReadOnlyList<NoteColor> NotePalette.All`

`HtmlDocumentBuilder` (Task 11) consumes `ToCssVariables`. Plan B's `NoteWindow` consumes `Get`.

- [ ] **Step 1: Write the failing tests**

`tests/StickyMD.Core.Tests/Theming/NotePaletteTests.cs`:

```csharp
using System.Text.RegularExpressions;
using Shouldly;
using StickyMD.Core.Theming;

namespace StickyMD.Core.Tests.Theming;

public class NotePaletteTests
{
    private static readonly Regex Hex = new("^#[0-9A-F]{6}$", RegexOptions.Compiled);

    [Fact]
    public void Exposes_exactly_seven_colors()
        => NotePalette.All.Count.ShouldBe(7);

    [Fact]
    public void All_matches_the_enum()
        => NotePalette.All.ShouldBe(Enum.GetValues<NoteColor>());

    [Fact]
    public void Every_color_and_mode_has_a_theme()
    {
        foreach (var color in NotePalette.All)
        foreach (var mode in Enum.GetValues<ThemeMode>())
            NotePalette.Get(color, mode).ShouldNotBeNull();
    }

    [Fact]
    public void Every_theme_value_is_an_uppercase_six_digit_hex()
    {
        foreach (var color in NotePalette.All)
        foreach (var mode in Enum.GetValues<ThemeMode>())
        {
            var theme = NotePalette.Get(color, mode);
            foreach (var value in Values(theme))
                Hex.IsMatch(value).ShouldBeTrue($"{color}/{mode} has bad hex '{value}'");
        }
    }

    [Fact]
    public void Light_and_dark_differ_for_every_color_except_charcoal_content()
    {
        foreach (var color in NotePalette.All.Where(c => c != NoteColor.Charcoal))
        {
            var light = NotePalette.Get(color, ThemeMode.Light);
            var dark = NotePalette.Get(color, ThemeMode.Dark);
            light.ContentBg.ShouldNotBe(dark.ContentBg, $"{color}");
        }
    }

    [Fact]
    public void ToCssVariables_emits_all_eight_note_prefixed_keys()
    {
        var vars = NotePalette.ToCssVariables(
            NotePalette.Get(NoteColor.Yellow, ThemeMode.Light));

        vars.Keys.ShouldBe(new[]
        {
            "--note-chrome-bg", "--note-chrome-fg", "--note-border",
            "--note-content-bg", "--note-content-fg", "--note-accent",
            "--note-code-bg", "--note-muted",
        }, ignoreOrder: true);
    }

    [Fact]
    public void ToCssVariables_carries_the_theme_values_through()
    {
        var theme = NotePalette.Get(NoteColor.Blue, ThemeMode.Dark);

        var vars = NotePalette.ToCssVariables(theme);

        vars["--note-content-bg"].ShouldBe(theme.ContentBg);
        vars["--note-accent"].ShouldBe(theme.Accent);
    }

    private static IEnumerable<string> Values(NoteTheme t) =>
        [t.ChromeBg, t.ChromeFg, t.Border, t.ContentBg,
         t.ContentFg, t.Accent, t.CodeBg, t.Muted];
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test --filter FullyQualifiedName~NotePaletteTests
```

Expected: FAIL to compile — `NotePalette` does not exist.

- [ ] **Step 3: Write the implementation**

`src/StickyMD.Core/Theming/NotePalette.cs`:

```csharp
namespace StickyMD.Core.Theming;

public enum NoteColor { Yellow, Green, Blue, Pink, Purple, Gray, Charcoal }

public enum ThemeMode { Light, Dark }

/// <param name="ChromeBg">Header bar background.</param>
/// <param name="ChromeFg">Header bar text and glyphs.</param>
/// <param name="Border">1px window border.</param>
/// <param name="ContentBg">Rendered/edited note body background.</param>
/// <param name="ContentFg">Body text.</param>
/// <param name="Accent">Links, checkbox ticks, focus rings.</param>
/// <param name="CodeBg">Inline code and fenced block background.</param>
/// <param name="Muted">Secondary text, rules, blockquote bars.</param>
public sealed record NoteTheme(
    string ChromeBg,
    string ChromeFg,
    string Border,
    string ContentBg,
    string ContentFg,
    string Accent,
    string CodeBg,
    string Muted);

/// <summary>
/// The one place note colors are defined. WPF chrome and rendered HTML both
/// read from here so a note's window and its content can never disagree.
/// </summary>
public static class NotePalette
{
    public static IReadOnlyList<NoteColor> All { get; } = Enum.GetValues<NoteColor>();

    public static NoteTheme Get(NoteColor color, ThemeMode mode) => (color, mode) switch
    {
        (NoteColor.Yellow, ThemeMode.Light) => new("#FCEE9B", "#3A3320", "#E8D77E", "#FFF7C0", "#3A3320", "#B08900", "#F5E9A8", "#857A50"),
        (NoteColor.Yellow, ThemeMode.Dark) => new("#2E2A19", "#F0E9C8", "#554E2E", "#3A3520", "#F0E9C8", "#E0C34A", "#464026", "#A79E78"),

        (NoteColor.Green, ThemeMode.Light) => new("#C9EDC4", "#23361F", "#A8DCA0", "#DFF5DC", "#23361F", "#2E7D32", "#D0EBCB", "#5E7458"),
        (NoteColor.Green, ThemeMode.Dark) => new("#1B2818", "#D6E9D2", "#35492F", "#22321F", "#D6E9D2", "#7CC47F", "#2A3B26", "#8FA88B"),

        (NoteColor.Blue, ThemeMode.Light) => new("#C4DDF5", "#1D2B38", "#9FC6E8", "#DCEBFA", "#1D2B38", "#1565C0", "#CFE2F5", "#566B7D"),
        (NoteColor.Blue, ThemeMode.Dark) => new("#17222B", "#D3E3F0", "#2F4150", "#1E2B36", "#D3E3F0", "#6FAEDB", "#26353F", "#8AA0B2"),

        (NoteColor.Pink, ThemeMode.Light) => new("#F5C8D9", "#3A2029", "#E8A3BD", "#FBE0EA", "#3A2029", "#C2185B", "#F5D2E0", "#7D5665"),
        (NoteColor.Pink, ThemeMode.Dark) => new("#2B1920", "#F0D8E2", "#4D2F3A", "#362028", "#F0D8E2", "#E086AC", "#402631", "#B08D9C"),

        (NoteColor.Purple, ThemeMode.Light) => new("#DACCF0", "#2B2138", "#BFA9E0", "#EBE2F7", "#2B2138", "#6A3FB5", "#E0D4F2", "#6B5C85"),
        (NoteColor.Purple, ThemeMode.Dark) => new("#211B2B", "#E1D6F0", "#3E3350", "#2B2338", "#E1D6F0", "#A886DB", "#332A42", "#9C8CB2"),

        (NoteColor.Gray, ThemeMode.Light) => new("#DCE0E4", "#23282D", "#BFC6CC", "#ECEEF0", "#23282D", "#455A64", "#E0E4E8", "#667079"),
        (NoteColor.Gray, ThemeMode.Dark) => new("#1C1F22", "#DDE2E6", "#383D42", "#24282C", "#DDE2E6", "#8FA8B5", "#2C3135", "#949CA3"),

        // Charcoal is a dark note by design, so its two variants are close.
        (NoteColor.Charcoal, ThemeMode.Light) => new("#24272C", "#E4E7EA", "#454A52", "#2E3238", "#E4E7EA", "#7FB3D5", "#383D44", "#9AA3AC"),
        (NoteColor.Charcoal, ThemeMode.Dark) => new("#1A1C1F", "#E4E7EA", "#383C42", "#23262A", "#E4E7EA", "#7FB3D5", "#2C3035", "#9AA3AC"),

        _ => throw new ArgumentOutOfRangeException(nameof(color), color, "Unmapped note color."),
    };

    public static IReadOnlyDictionary<string, string> ToCssVariables(NoteTheme theme)
        => new Dictionary<string, string>
        {
            ["--note-chrome-bg"] = theme.ChromeBg,
            ["--note-chrome-fg"] = theme.ChromeFg,
            ["--note-border"] = theme.Border,
            ["--note-content-bg"] = theme.ContentBg,
            ["--note-content-fg"] = theme.ContentFg,
            ["--note-accent"] = theme.Accent,
            ["--note-code-bg"] = theme.CodeBg,
            ["--note-muted"] = theme.Muted,
        };
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test --filter FullyQualifiedName~NotePaletteTests
```

Expected: PASS, 7 tests.

- [ ] **Step 5: Commit**

```bash
git add src/StickyMD.Core/Theming tests/StickyMD.Core.Tests/Theming
git commit -m "feat(core): add NotePalette as the single source of note colors"
```

---

## Task 5: WindowPlacement

Pure geometry. The spec's rule: **monitor discovery lives in App, placement math lives in Core.** Core never asks Windows what monitors exist — it is handed a list.

**Files:**

- Create: `src/StickyMD.Core/Geometry/WindowPlacement.cs`
- Test: `tests/StickyMD.Core.Tests/Geometry/WindowPlacementTests.cs`

**Interfaces:**

- Consumes: nothing.
- Produces:
  - `readonly record struct PixelRect(int X, int Y, int Width, int Height)` with `int Right => X + Width;` and `int Bottom => Y + Height;`
  - `sealed record MonitorInfo(PixelRect Bounds, PixelRect WorkArea, double Dpi, bool IsPrimary, string DeviceName)`
  - `static PixelRect WindowPlacement.Clamp(PixelRect saved, IReadOnlyList<MonitorInfo> monitors)`

Plan B's `MonitorEnumerator` produces the `MonitorInfo` list from Win32 and calls `Clamp`.

**Rules:**

1. Empty monitor list → return `saved` unchanged (nothing to clamp against).
2. `saved` fully inside some monitor's `WorkArea` → unchanged.
3. Otherwise pick the target monitor: largest `WorkArea` intersection with `saved`; if none intersect, the primary; if no primary, the first.
4. Clamp `saved` fully inside the target `WorkArea`, shrinking width/height first if they exceed it.

`Dpi` is carried as data but **never used by `Clamp`** — geometry is already in physical pixels, so DPI cannot affect the math. A test pins that invariant.

- [ ] **Step 1: Write the failing tests**

`tests/StickyMD.Core.Tests/Geometry/WindowPlacementTests.cs`:

```csharp
using Shouldly;
using StickyMD.Core.Geometry;

namespace StickyMD.Core.Tests.Geometry;

public class WindowPlacementTests
{
    // Primary 1920x1080 at origin, work area inset by a 40px taskbar.
    private static MonitorInfo Primary => new(
        new PixelRect(0, 0, 1920, 1080),
        new PixelRect(0, 0, 1920, 1040),
        Dpi: 96, IsPrimary: true, DeviceName: @"\\.\DISPLAY1");

    // Secondary sitting to the LEFT of primary, so it has negative coordinates.
    private static MonitorInfo LeftSecondary => new(
        new PixelRect(-1920, 0, 1920, 1080),
        new PixelRect(-1920, 0, 1920, 1040),
        Dpi: 144, IsPrimary: false, DeviceName: @"\\.\DISPLAY2");

    [Fact]
    public void Empty_monitor_list_returns_the_rect_unchanged()
    {
        var saved = new PixelRect(100, 100, 320, 420);

        WindowPlacement.Clamp(saved, []).ShouldBe(saved);
    }

    [Fact]
    public void Rect_fully_inside_a_work_area_is_unchanged()
    {
        var saved = new PixelRect(100, 100, 320, 420);

        WindowPlacement.Clamp(saved, [Primary]).ShouldBe(saved);
    }

    [Fact]
    public void Rect_on_a_negative_coordinate_monitor_is_unchanged()
    {
        var saved = new PixelRect(-1800, 60, 320, 420);

        WindowPlacement.Clamp(saved, [Primary, LeftSecondary]).ShouldBe(saved);
    }

    [Fact]
    public void Rect_overhanging_the_right_edge_is_pulled_back_in()
    {
        var saved = new PixelRect(1800, 100, 320, 420);

        var result = WindowPlacement.Clamp(saved, [Primary]);

        result.ShouldBe(new PixelRect(1600, 100, 320, 420));
        result.Right.ShouldBe(1920);
    }

    [Fact]
    public void Rect_overhanging_the_bottom_respects_the_taskbar_inset()
    {
        var saved = new PixelRect(100, 900, 320, 420);

        var result = WindowPlacement.Clamp(saved, [Primary]);

        result.Bottom.ShouldBe(1040);
        result.Y.ShouldBe(620);
    }

    [Fact]
    public void Rect_with_negative_position_is_pushed_to_the_origin()
    {
        var saved = new PixelRect(-50, -80, 320, 420);

        WindowPlacement.Clamp(saved, [Primary])
            .ShouldBe(new PixelRect(0, 0, 320, 420));
    }

    [Fact]
    public void Rect_on_a_removed_monitor_lands_inside_the_primary()
    {
        // Saved while DISPLAY2 existed at x = -1920. DISPLAY2 is now gone.
        var saved = new PixelRect(-1700, 200, 320, 420);

        var result = WindowPlacement.Clamp(saved, [Primary]);

        result.X.ShouldBeGreaterThanOrEqualTo(0);
        result.Right.ShouldBeLessThanOrEqualTo(1920);
        result.Bottom.ShouldBeLessThanOrEqualTo(1040);
        result.Width.ShouldBe(320);
        result.Height.ShouldBe(420);
    }

    [Fact]
    public void Rect_larger_than_the_work_area_is_shrunk_to_fit()
    {
        var saved = new PixelRect(0, 0, 3000, 2000);

        WindowPlacement.Clamp(saved, [Primary])
            .ShouldBe(new PixelRect(0, 0, 1920, 1040));
    }

    [Fact]
    public void Picks_the_monitor_with_the_largest_overlap()
    {
        // Mostly on the left monitor, slightly overhanging into primary.
        var saved = new PixelRect(-400, 100, 320, 420);

        var result = WindowPlacement.Clamp(saved, [Primary, LeftSecondary]);

        result.X.ShouldBeGreaterThanOrEqualTo(-1920);
        result.Right.ShouldBeLessThanOrEqualTo(0);
    }

    [Fact]
    public void Falls_back_to_the_first_monitor_when_none_is_primary()
    {
        var noPrimary = LeftSecondary with { IsPrimary = false };
        var saved = new PixelRect(9000, 9000, 320, 420);

        var result = WindowPlacement.Clamp(saved, [noPrimary]);

        result.X.ShouldBeGreaterThanOrEqualTo(-1920);
        result.Right.ShouldBeLessThanOrEqualTo(0);
    }

    [Fact]
    public void Dpi_does_not_affect_the_result()
    {
        // Geometry is stored in physical pixels, so Clamp must be DPI-agnostic.
        var saved = new PixelRect(1800, 100, 320, 420);
        var at96 = Primary;
        var at192 = Primary with { Dpi = 192 };

        WindowPlacement.Clamp(saved, [at96])
            .ShouldBe(WindowPlacement.Clamp(saved, [at192]));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test --filter FullyQualifiedName~WindowPlacementTests
```

Expected: FAIL to compile — `WindowPlacement` does not exist.

- [ ] **Step 3: Write the implementation**

`src/StickyMD.Core/Geometry/WindowPlacement.cs`:

```csharp
namespace StickyMD.Core.Geometry;

/// <summary>
/// A rectangle in PHYSICAL screen pixels. Never device-independent units --
/// see the spec section "Geometry" for why.
/// </summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

/// <summary>
/// Plain data describing one monitor. Produced by the App layer's monitor
/// enumeration; Core never queries the OS for this.
/// </summary>
public sealed record MonitorInfo(
    PixelRect Bounds,
    PixelRect WorkArea,
    double Dpi,
    bool IsPrimary,
    string DeviceName);

public static class WindowPlacement
{
    /// <summary>
    /// Returns a rect guaranteed to sit inside one of the supplied monitors'
    /// work areas. A rect already fully visible is returned untouched.
    /// </summary>
    public static PixelRect Clamp(PixelRect saved, IReadOnlyList<MonitorInfo> monitors)
    {
        if (monitors.Count == 0) return saved;

        foreach (var monitor in monitors)
            if (Contains(monitor.WorkArea, saved)) return saved;

        var target = PickTarget(saved, monitors);
        return Fit(saved, target.WorkArea);
    }

    private static MonitorInfo PickTarget(
        PixelRect saved, IReadOnlyList<MonitorInfo> monitors)
    {
        MonitorInfo? best = null;
        var bestArea = 0L;

        foreach (var monitor in monitors)
        {
            var area = IntersectionArea(monitor.WorkArea, saved);
            if (area > bestArea)
            {
                bestArea = area;
                best = monitor;
            }
        }

        return best
            ?? monitors.FirstOrDefault(m => m.IsPrimary)
            ?? monitors[0];
    }

    private static PixelRect Fit(PixelRect rect, PixelRect work)
    {
        var width = Math.Min(rect.Width, work.Width);
        var height = Math.Min(rect.Height, work.Height);

        var x = Math.Clamp(rect.X, work.X, work.Right - width);
        var y = Math.Clamp(rect.Y, work.Y, work.Bottom - height);

        return new PixelRect(x, y, width, height);
    }

    private static bool Contains(PixelRect outer, PixelRect inner)
        => inner.X >= outer.X
        && inner.Y >= outer.Y
        && inner.Right <= outer.Right
        && inner.Bottom <= outer.Bottom;

    private static long IntersectionArea(PixelRect a, PixelRect b)
    {
        var width = Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X);
        var height = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y);
        return width <= 0 || height <= 0 ? 0 : (long)width * height;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test --filter FullyQualifiedName~WindowPlacementTests
```

Expected: PASS, 11 tests.

- [ ] **Step 5: Commit**

```bash
git add src/StickyMD.Core/Geometry tests/StickyMD.Core.Tests/Geometry
git commit -m "feat(core): clamp saved window geometry to visible monitors"
```

---

## Task 6: MarkdownEditOps — bold and italic

**Files:**

- Create: `src/StickyMD.Core/Editing/MarkdownEditOps.cs`
- Test: `tests/StickyMD.Core.Tests/Editing/MarkdownEditOpsEmphasisTests.cs`

**Interfaces:**

- Consumes: nothing.
- Produces:
  - `readonly record struct EditResult(string Text, int SelectionStart, int SelectionLength)`
  - `static EditResult MarkdownEditOps.ToggleBold(string text, int selStart, int selLen)`
  - `static EditResult MarkdownEditOps.ToggleItalic(string text, int selStart, int selLen)`

Tasks 7 and 8 add `ContinueList`, `Indent`, and `Outdent` to the same class. Plan B's editor binds `Ctrl+B` / `Ctrl+I` to these.

**Contract:** the returned selection covers the _inner_ text, excluding markers, so toggling twice is a round trip. An empty selection inserts the marker pair and places the caret between them.

- [ ] **Step 1: Write the failing tests**

`tests/StickyMD.Core.Tests/Editing/MarkdownEditOpsEmphasisTests.cs`:

```csharp
using Shouldly;
using StickyMD.Core.Editing;

namespace StickyMD.Core.Tests.Editing;

public class MarkdownEditOpsEmphasisTests
{
    [Fact]
    public void Bold_wraps_the_selection_and_keeps_it_selected()
    {
        var result = MarkdownEditOps.ToggleBold("hello", 0, 5);

        result.Text.ShouldBe("**hello**");
        result.SelectionStart.ShouldBe(2);
        result.SelectionLength.ShouldBe(5);
    }

    [Fact]
    public void Bold_wraps_a_selection_in_the_middle_of_text()
    {
        var result = MarkdownEditOps.ToggleBold("say hello now", 4, 5);

        result.Text.ShouldBe("say **hello** now");
        result.SelectionStart.ShouldBe(6);
        result.SelectionLength.ShouldBe(5);
    }

    [Fact]
    public void Bold_is_a_round_trip()
    {
        var once = MarkdownEditOps.ToggleBold("hello", 0, 5);

        var twice = MarkdownEditOps.ToggleBold(
            once.Text, once.SelectionStart, once.SelectionLength);

        twice.Text.ShouldBe("hello");
        twice.SelectionStart.ShouldBe(0);
        twice.SelectionLength.ShouldBe(5);
    }

    [Fact]
    public void Bold_unwraps_when_markers_surround_the_selection()
    {
        var result = MarkdownEditOps.ToggleBold("say **hello** now", 6, 5);

        result.Text.ShouldBe("say hello now");
        result.SelectionStart.ShouldBe(4);
        result.SelectionLength.ShouldBe(5);
    }

    [Fact]
    public void Bold_unwraps_when_markers_are_inside_the_selection()
    {
        var result = MarkdownEditOps.ToggleBold("say **hello** now", 4, 9);

        result.Text.ShouldBe("say hello now");
        result.SelectionStart.ShouldBe(4);
        result.SelectionLength.ShouldBe(5);
    }

    [Fact]
    public void Bold_on_an_empty_selection_places_the_caret_between_markers()
    {
        var result = MarkdownEditOps.ToggleBold("", 0, 0);

        result.Text.ShouldBe("****");
        result.SelectionStart.ShouldBe(2);
        result.SelectionLength.ShouldBe(0);
    }

    [Fact]
    public void Bold_on_an_empty_selection_mid_text_inserts_at_the_caret()
    {
        var result = MarkdownEditOps.ToggleBold("ab", 1, 0);

        result.Text.ShouldBe("a****b");
        result.SelectionStart.ShouldBe(3);
    }

    [Fact]
    public void Italic_wraps_with_a_single_asterisk()
    {
        var result = MarkdownEditOps.ToggleItalic("hello", 0, 5);

        result.Text.ShouldBe("*hello*");
        result.SelectionStart.ShouldBe(1);
        result.SelectionLength.ShouldBe(5);
    }

    [Fact]
    public void Italic_is_a_round_trip()
    {
        var once = MarkdownEditOps.ToggleItalic("hello", 0, 5);

        var twice = MarkdownEditOps.ToggleItalic(
            once.Text, once.SelectionStart, once.SelectionLength);

        twice.Text.ShouldBe("hello");
    }

    [Fact]
    public void Italic_inside_bold_does_not_unwrap_the_bold()
    {
        // Selection is "hello" inside "**hello**". Italic must not see the
        // surrounding ** as its own single-asterisk markers.
        var result = MarkdownEditOps.ToggleItalic("**hello**", 2, 5);

        result.Text.ShouldBe("***hello***");
        result.SelectionStart.ShouldBe(3);
        result.SelectionLength.ShouldBe(5);
    }

    [Fact]
    public void Null_text_is_treated_as_empty()
    {
        var result = MarkdownEditOps.ToggleBold(null!, 0, 0);

        result.Text.ShouldBe("****");
    }

    [Fact]
    public void Out_of_range_selection_is_clamped()
    {
        var result = MarkdownEditOps.ToggleBold("abc", 10, 10);

        result.Text.ShouldBe("abc****");
        result.SelectionStart.ShouldBe(5);
    }
}
```

The `Italic_inside_bold` case is the one that catches a naive implementation: a single-asterisk unwrap check must not match the second asterisk of a `**` pair.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test --filter FullyQualifiedName~MarkdownEditOpsEmphasisTests
```

Expected: FAIL to compile — `MarkdownEditOps` does not exist.

- [ ] **Step 3: Write the implementation**

`src/StickyMD.Core/Editing/MarkdownEditOps.cs`:

```csharp
namespace StickyMD.Core.Editing;

/// <summary>The result of an edit: new text plus where the selection lands.</summary>
public readonly record struct EditResult(string Text, int SelectionStart, int SelectionLength);

/// <summary>
/// Editor conveniences as pure functions over (text, selection). No UI type
/// appears here, which is what makes every case unit-testable.
/// </summary>
public static partial class MarkdownEditOps
{
    public static EditResult ToggleBold(string text, int selStart, int selLen)
        => ToggleWrap(text, selStart, selLen, "**");

    public static EditResult ToggleItalic(string text, int selStart, int selLen)
        => ToggleWrap(text, selStart, selLen, "*");

    private static EditResult ToggleWrap(
        string text, int selStart, int selLen, string marker)
    {
        text ??= string.Empty;
        (selStart, selLen) = ClampSelection(text, selStart, selLen);

        var m = marker.Length;

        // Case 1: the selection itself includes the markers.
        if (selLen >= m * 2
            && Slice(text, selStart, m) == marker
            && Slice(text, selStart + selLen - m, m) == marker
            && !IsPartOfLongerRun(text, selStart, marker)
            && !IsPartOfLongerRun(text, selStart + selLen - m, marker))
        {
            var inner = text.Substring(selStart + m, selLen - (m * 2));
            var updated = text.Remove(selStart, selLen).Insert(selStart, inner);
            return new EditResult(updated, selStart, inner.Length);
        }

        // Case 2: the markers sit immediately outside the selection.
        if (selStart >= m
            && selStart + selLen + m <= text.Length
            && Slice(text, selStart - m, m) == marker
            && Slice(text, selStart + selLen, m) == marker
            && !IsPartOfLongerRun(text, selStart - m, marker)
            && !IsPartOfLongerRun(text, selStart + selLen, marker))
        {
            var updated = text
                .Remove(selStart + selLen, m)
                .Remove(selStart - m, m);
            return new EditResult(updated, selStart - m, selLen);
        }

        // Case 3: wrap.
        var wrapped = text
            .Insert(selStart + selLen, marker)
            .Insert(selStart, marker);
        return new EditResult(wrapped, selStart + m, selLen);
    }

    /// <summary>
    /// True when the marker at <paramref name="index"/> is part of a longer run
    /// of the same character. Stops a single-asterisk unwrap from tearing apart
    /// a "**" pair.
    /// </summary>
    private static bool IsPartOfLongerRun(string text, int index, string marker)
    {
        var c = marker[0];
        var runStart = index;
        while (runStart > 0 && text[runStart - 1] == c) runStart--;

        var runEnd = index + marker.Length;
        while (runEnd < text.Length && text[runEnd] == c) runEnd++;

        return runEnd - runStart > marker.Length;
    }

    private static string Slice(string text, int start, int length)
        => start < 0 || start + length > text.Length
            ? string.Empty
            : text.Substring(start, length);

    internal static (int Start, int Length) ClampSelection(
        string text, int selStart, int selLen)
    {
        var start = Math.Clamp(selStart, 0, text.Length);
        var length = Math.Clamp(selLen, 0, text.Length - start);
        return (start, length);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test --filter FullyQualifiedName~MarkdownEditOpsEmphasisTests
```

Expected: PASS, 12 tests.

- [ ] **Step 5: Commit**

```bash
git add src/StickyMD.Core/Editing tests/StickyMD.Core.Tests/Editing
git commit -m "feat(core): add bold/italic toggle edit operations"
```

---

## Task 7: MarkdownEditOps — list continuation

**Files:**

- Create: `src/StickyMD.Core/Editing/MarkdownEditOps.Lists.cs`
- Test: `tests/StickyMD.Core.Tests/Editing/MarkdownEditOpsListTests.cs`

**Interfaces:**

- Consumes: `EditResult`, `MarkdownEditOps.ClampSelection` from Task 6.
- Produces: `static EditResult MarkdownEditOps.ContinueList(string text, int selStart, int selLen)`. Plan B binds `Enter` in the editor to this; when it returns text identical to the input, the editor lets the default newline through.

**Contract:**

- A non-empty list item → insert `\n` + the same indent + the same marker + a space. Ordered markers increment. Task markers continue as **unchecked** `[ ] `.
- An empty list item (prefix only) → strip the prefix, leaving an empty line, caret at line start. **No newline is inserted** — the list ends.
- Not a list line → return the input unchanged so the caller inserts a plain newline.
- A non-empty selection is deleted first, then the rule applies at the collapsed caret.

- [ ] **Step 1: Write the failing tests**

`tests/StickyMD.Core.Tests/Editing/MarkdownEditOpsListTests.cs`:

```csharp
using Shouldly;
using StickyMD.Core.Editing;

namespace StickyMD.Core.Tests.Editing;

public class MarkdownEditOpsListTests
{
    [Fact]
    public void Continues_a_dash_bullet()
    {
        var text = "- milk";

        var result = MarkdownEditOps.ContinueList(text, text.Length, 0);

        result.Text.ShouldBe("- milk\n- ");
        result.SelectionStart.ShouldBe(result.Text.Length);
        result.SelectionLength.ShouldBe(0);
    }

    [Fact]
    public void Continues_an_asterisk_bullet()
    {
        var text = "* milk";

        MarkdownEditOps.ContinueList(text, text.Length, 0)
            .Text.ShouldBe("* milk\n* ");
    }

    [Fact]
    public void Continues_a_plus_bullet()
    {
        var text = "+ milk";

        MarkdownEditOps.ContinueList(text, text.Length, 0)
            .Text.ShouldBe("+ milk\n+ ");
    }

    [Fact]
    public void Increments_an_ordered_marker()
    {
        var text = "1. first";

        MarkdownEditOps.ContinueList(text, text.Length, 0)
            .Text.ShouldBe("1. first\n2. ");
    }

    [Fact]
    public void Increments_from_an_arbitrary_ordered_number()
    {
        var text = "- a\n7. seventh";

        MarkdownEditOps.ContinueList(text, text.Length, 0)
            .Text.ShouldBe("- a\n7. seventh\n8. ");
    }

    [Fact]
    public void Preserves_indentation()
    {
        var text = "  - nested";

        MarkdownEditOps.ContinueList(text, text.Length, 0)
            .Text.ShouldBe("  - nested\n  - ");
    }

    [Fact]
    public void Continues_a_task_item_as_unchecked()
    {
        var text = "- [ ] buy milk";

        MarkdownEditOps.ContinueList(text, text.Length, 0)
            .Text.ShouldBe("- [ ] buy milk\n- [ ] ");
    }

    [Fact]
    public void Continues_a_checked_task_item_as_unchecked()
    {
        var text = "- [x] done thing";

        MarkdownEditOps.ContinueList(text, text.Length, 0)
            .Text.ShouldBe("- [x] done thing\n- [ ] ");
    }

    [Fact]
    public void Empty_bullet_terminates_the_list()
    {
        var text = "- milk\n- ";

        var result = MarkdownEditOps.ContinueList(text, text.Length, 0);

        result.Text.ShouldBe("- milk\n");
        result.SelectionStart.ShouldBe(7);
        result.SelectionLength.ShouldBe(0);
    }

    [Fact]
    public void Empty_indented_bullet_terminates_the_list_and_drops_the_indent()
    {
        var text = "- a\n  - ";

        var result = MarkdownEditOps.ContinueList(text, text.Length, 0);

        result.Text.ShouldBe("- a\n");
        result.SelectionStart.ShouldBe(4);
    }

    [Fact]
    public void Empty_task_item_terminates_the_list()
    {
        var text = "- [ ] a\n- [ ] ";

        MarkdownEditOps.ContinueList(text, text.Length, 0)
            .Text.ShouldBe("- [ ] a\n");
    }

    [Fact]
    public void Empty_ordered_item_terminates_the_list()
    {
        var text = "1. a\n2. ";

        MarkdownEditOps.ContinueList(text, text.Length, 0)
            .Text.ShouldBe("1. a\n");
    }

    [Fact]
    public void Non_list_line_is_returned_unchanged()
    {
        var text = "just prose";

        var result = MarkdownEditOps.ContinueList(text, text.Length, 0);

        result.Text.ShouldBe(text);
        result.SelectionStart.ShouldBe(text.Length);
    }

    [Fact]
    public void Empty_document_is_returned_unchanged()
    {
        MarkdownEditOps.ContinueList("", 0, 0).Text.ShouldBe("");
    }

    [Fact]
    public void Continues_from_a_caret_in_the_middle_of_the_document()
    {
        var text = "- milk\ntrailing";

        var result = MarkdownEditOps.ContinueList(text, 6, 0);

        result.Text.ShouldBe("- milk\n- \ntrailing");
        result.SelectionStart.ShouldBe(9);
    }

    [Fact]
    public void Deletes_a_non_empty_selection_before_continuing()
    {
        var text = "- milk and eggs";

        // Select " and eggs", press Enter.
        var result = MarkdownEditOps.ContinueList(text, 6, 9);

        result.Text.ShouldBe("- milk\n- ");
    }

    [Fact]
    public void Handles_crlf_documents()
    {
        var text = "- milk\r\n- eggs";

        var result = MarkdownEditOps.ContinueList(text, text.Length, 0);

        result.Text.ShouldBe("- milk\r\n- eggs\n- ");
    }
}
```

Note the CRLF case: `ContinueList` always inserts `\n`. The editor's buffer uses `\n` internally and `NoteFile` (Task 10) restores the file's own newline convention on write, so the op does not need to care.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test --filter FullyQualifiedName~MarkdownEditOpsListTests
```

Expected: FAIL to compile — `ContinueList` does not exist.

- [ ] **Step 3: Write the implementation**

`src/StickyMD.Core/Editing/MarkdownEditOps.Lists.cs`:

```csharp
using System.Text.RegularExpressions;

namespace StickyMD.Core.Editing;

public static partial class MarkdownEditOps
{
    [GeneratedRegex(@"^(?<indent>[ \t]*)(?<marker>[-*+]|(?<number>\d+)\.)(?<gap>[ \t]+)(?<task>\[[ xX]\][ \t]+)?")]
    private static partial Regex ListPrefixRegex();

    internal sealed record ListPrefix(
        string Indent, string Marker, int? Number, string Gap, bool IsTask, int Length);

    public static EditResult ContinueList(string text, int selStart, int selLen)
    {
        text ??= string.Empty;
        (selStart, selLen) = ClampSelection(text, selStart, selLen);

        if (selLen > 0)
        {
            text = text.Remove(selStart, selLen);
            selLen = 0;
        }

        var lineStart = LineStart(text, selStart);
        var lineEnd = LineEnd(text, selStart);
        var line = text[lineStart..lineEnd];

        var prefix = ParseListPrefix(line);
        if (prefix is null) return new EditResult(text, selStart, 0);

        var rest = line[prefix.Length..];

        // Empty item -> strip the prefix and end the list. No newline added.
        if (rest.Trim().Length == 0)
        {
            var updated = text.Remove(lineStart, lineEnd - lineStart);
            return new EditResult(updated, lineStart, 0);
        }

        var nextMarker = prefix.Number is int n
            ? $"{n + 1}."
            : prefix.Marker;

        var insertion = "\n" + prefix.Indent + nextMarker + prefix.Gap
            + (prefix.IsTask ? "[ ] " : string.Empty);

        var withInsertion = text.Insert(selStart, insertion);
        return new EditResult(withInsertion, selStart + insertion.Length, 0);
    }

    internal static ListPrefix? ParseListPrefix(string line)
    {
        var match = ListPrefixRegex().Match(line);
        if (!match.Success) return null;

        var numberGroup = match.Groups["number"];
        return new ListPrefix(
            Indent: match.Groups["indent"].Value,
            Marker: match.Groups["marker"].Value,
            Number: numberGroup.Success ? int.Parse(numberGroup.Value) : null,
            Gap: match.Groups["gap"].Value,
            IsTask: match.Groups["task"].Success,
            Length: match.Length);
    }

    internal static int LineStart(string text, int index)
    {
        var i = text.LastIndexOf('\n', Math.Max(0, Math.Min(index - 1, text.Length - 1)));
        return i < 0 ? 0 : i + 1;
    }

    internal static int LineEnd(string text, int index)
    {
        var i = text.IndexOf('\n', Math.Min(index, text.Length));
        return i < 0 ? text.Length : i;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test --filter FullyQualifiedName~MarkdownEditOpsListTests
```

Expected: PASS, 17 tests.

If `Handles_crlf_documents` fails on the line-slicing, check `LineEnd` — the `\r` stays at the end of the sliced line, and `ParseListPrefix` must still match because the prefix is at the _start_.

- [ ] **Step 5: Commit**

```bash
git add src/StickyMD.Core/Editing/MarkdownEditOps.Lists.cs tests/StickyMD.Core.Tests/Editing/MarkdownEditOpsListTests.cs
git commit -m "feat(core): continue and terminate markdown lists on Enter"
```

---

## Task 8: MarkdownEditOps — indent and outdent

**Files:**

- Create: `src/StickyMD.Core/Editing/MarkdownEditOps.Indent.cs`
- Test: `tests/StickyMD.Core.Tests/Editing/MarkdownEditOpsIndentTests.cs`

**Interfaces:**

- Consumes: `EditResult`, `ClampSelection`, `ParseListPrefix`, `LineStart`, `LineEnd` from Tasks 6-7.
- Produces:
  - `static EditResult MarkdownEditOps.Indent(string text, int selStart, int selLen)`
  - `static EditResult MarkdownEditOps.Outdent(string text, int selStart, int selLen)`

Plan B binds `Tab` / `Shift+Tab` to these.

**Contract.** The spec specifies list items; it is silent on non-list lines, and `Tab` must do _something_, so this plan fixes the behavior explicitly:

| Line            | Indent (Tab)                 | Outdent (Shift+Tab)                              |
| --------------- | ---------------------------- | ------------------------------------------------ |
| List item       | Prepend 2 spaces             | Remove up to 2 leading spaces; no-op at column 0 |
| Not a list item | Insert 2 spaces at the caret | Remove up to 2 leading spaces from the line      |

The indent unit is **2 spaces**. Selections spanning multiple lines shift every line they touch. Selection offsets are adjusted by the characters inserted or removed before and within them.

- [ ] **Step 1: Write the failing tests**

`tests/StickyMD.Core.Tests/Editing/MarkdownEditOpsIndentTests.cs`:

```csharp
using Shouldly;
using StickyMD.Core.Editing;

namespace StickyMD.Core.Tests.Editing;

public class MarkdownEditOpsIndentTests
{
    [Fact]
    public void Indent_shifts_a_single_list_line()
    {
        var text = "- milk";

        var result = MarkdownEditOps.Indent(text, 6, 0);

        result.Text.ShouldBe("  - milk");
        result.SelectionStart.ShouldBe(8);
    }

    [Fact]
    public void Indent_shifts_every_selected_list_line()
    {
        var text = "- a\n- b\n- c";

        // Select from inside line 1 through inside line 3.
        var result = MarkdownEditOps.Indent(text, 2, 7);

        result.Text.ShouldBe("  - a\n  - b\n  - c");
    }

    [Fact]
    public void Indent_leaves_untouched_lines_alone()
    {
        var text = "- a\n- b\n- c";

        var result = MarkdownEditOps.Indent(text, 0, 3);

        result.Text.ShouldBe("  - a\n- b\n- c");
    }

    [Fact]
    public void Indent_on_a_non_list_line_inserts_two_spaces_at_the_caret()
    {
        var text = "prose here";

        var result = MarkdownEditOps.Indent(text, 5, 0);

        result.Text.ShouldBe("prose   here");
        result.SelectionStart.ShouldBe(7);
    }

    [Fact]
    public void Indent_preserves_an_already_indented_item()
    {
        var text = "  - nested";

        MarkdownEditOps.Indent(text, 10, 0).Text.ShouldBe("    - nested");
    }

    [Fact]
    public void Indent_handles_task_items()
    {
        var text = "- [ ] buy milk";

        MarkdownEditOps.Indent(text, 14, 0).Text.ShouldBe("  - [ ] buy milk");
    }

    [Fact]
    public void Indent_handles_ordered_items()
    {
        var text = "1. first";

        MarkdownEditOps.Indent(text, 8, 0).Text.ShouldBe("  1. first");
    }

    [Fact]
    public void Outdent_removes_two_leading_spaces()
    {
        var text = "  - nested";

        var result = MarkdownEditOps.Outdent(text, 10, 0);

        result.Text.ShouldBe("- nested");
        result.SelectionStart.ShouldBe(8);
    }

    [Fact]
    public void Outdent_at_column_zero_is_a_no_op()
    {
        var text = "- milk";

        var result = MarkdownEditOps.Outdent(text, 6, 0);

        result.Text.ShouldBe("- milk");
        result.SelectionStart.ShouldBe(6);
    }

    [Fact]
    public void Outdent_removes_only_one_space_when_only_one_exists()
    {
        var text = " - milk";

        MarkdownEditOps.Outdent(text, 7, 0).Text.ShouldBe("- milk");
    }

    [Fact]
    public void Outdent_shifts_every_selected_line()
    {
        var text = "  - a\n  - b";

        var result = MarkdownEditOps.Outdent(text, 0, text.Length);

        result.Text.ShouldBe("- a\n- b");
    }

    [Fact]
    public void Outdent_on_a_non_list_line_removes_leading_spaces()
    {
        var text = "    prose";

        MarkdownEditOps.Outdent(text, 9, 0).Text.ShouldBe("  prose");
    }

    [Fact]
    public void Indent_then_outdent_is_a_round_trip()
    {
        var text = "- a\n- b";

        var indented = MarkdownEditOps.Indent(text, 0, text.Length);
        var back = MarkdownEditOps.Outdent(
            indented.Text, indented.SelectionStart, indented.SelectionLength);

        back.Text.ShouldBe(text);
    }

    [Fact]
    public void Indent_extends_a_multi_line_selection_to_cover_inserted_spaces()
    {
        var text = "- a\n- b";

        var result = MarkdownEditOps.Indent(text, 0, 7);

        result.Text.ShouldBe("  - a\n  - b");
        result.SelectionStart.ShouldBe(0);
        result.SelectionLength.ShouldBe(11);
    }

    [Fact]
    public void Empty_document_is_unchanged()
    {
        MarkdownEditOps.Indent("", 0, 0).Text.ShouldBe("  ");
        MarkdownEditOps.Outdent("", 0, 0).Text.ShouldBe("");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test --filter FullyQualifiedName~MarkdownEditOpsIndentTests
```

Expected: FAIL to compile — `Indent` does not exist.

- [ ] **Step 3: Write the implementation**

`src/StickyMD.Core/Editing/MarkdownEditOps.Indent.cs`:

```csharp
using System.Text;

namespace StickyMD.Core.Editing;

public static partial class MarkdownEditOps
{
    private const string IndentUnit = "  ";

    public static EditResult Indent(string text, int selStart, int selLen)
        => ShiftLines(text, selStart, selLen, outdent: false);

    public static EditResult Outdent(string text, int selStart, int selLen)
        => ShiftLines(text, selStart, selLen, outdent: true);

    private static EditResult ShiftLines(
        string text, int selStart, int selLen, bool outdent)
    {
        text ??= string.Empty;
        (selStart, selLen) = ClampSelection(text, selStart, selLen);

        var firstLineStart = LineStart(text, selStart);
        var lastLineEnd = LineEnd(text, selStart + selLen);

        var builder = new StringBuilder(text.Length + 16);
        builder.Append(text, 0, firstLineStart);

        var newStart = selStart;
        var newLength = selLen;
        var cursor = firstLineStart;
        var isSingleCaretOnNonList = false;

        while (cursor <= lastLineEnd)
        {
            var lineEnd = LineEnd(text, cursor);
            var line = text[cursor..lineEnd];
            var isList = ParseListPrefix(line) is not null;

            string newLine;
            int delta;

            if (outdent)
            {
                var removable = 0;
                while (removable < IndentUnit.Length
                       && removable < line.Length
                       && line[removable] == ' ')
                    removable++;

                newLine = line[removable..];
                delta = -removable;
            }
            else if (isList || selLen > 0 || cursor != firstLineStart)
            {
                newLine = IndentUnit + line;
                delta = IndentUnit.Length;
            }
            else
            {
                // Single caret on a non-list line: insert at the caret itself.
                isSingleCaretOnNonList = true;
                var offset = selStart - cursor;
                newLine = line[..offset] + IndentUnit + line[offset..];
                delta = IndentUnit.Length;
            }

            builder.Append(newLine);

            if (delta != 0)
            {
                if (isSingleCaretOnNonList)
                {
                    newStart += delta;
                }
                else if (cursor < selStart)
                {
                    // Line begins before the selection: shift start, and shift
                    // length too if the change lands before the selection start.
                    newStart += delta;
                }
                else
                {
                    newLength += delta;
                }
            }

            if (lineEnd >= text.Length) { cursor = lineEnd + 1; break; }

            builder.Append('\n');
            cursor = lineEnd + 1;
        }

        if (cursor - 1 < text.Length)
            builder.Append(text, cursor - 1 < 0 ? 0 : cursor - 1, 0);

        if (lastLineEnd < text.Length)
            builder.Append(text, lastLineEnd, text.Length - lastLineEnd);

        var result = builder.ToString();

        newStart = Math.Clamp(newStart, 0, result.Length);
        newLength = Math.Clamp(newLength, 0, result.Length - newStart);

        return new EditResult(result, newStart, newLength);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test --filter FullyQualifiedName~MarkdownEditOpsIndentTests
```

Expected: PASS, 15 tests.

The selection-offset accounting in `ShiftLines` is the fiddly part. If `Indent_extends_a_multi_line_selection_to_cover_inserted_spaces` or `Indent_shifts_a_single_list_line` fails, fix the accounting rather than the assertion — the assertions encode the intended contract. Two rules: characters inserted strictly _before_ `selStart` move the start; characters inserted _within_ the selection extend the length.

- [ ] **Step 5: Run the whole suite to check for regressions**

```bash
dotnet test
```

Expected: PASS, all tests so far.

- [ ] **Step 6: Commit**

```bash
git add src/StickyMD.Core/Editing/MarkdownEditOps.Indent.cs tests/StickyMD.Core.Tests/Editing/MarkdownEditOpsIndentTests.cs
git commit -m "feat(core): indent and outdent list items by two spaces"
```

---

## Task 9: NoteFile — format-preserving read

Reading a note means learning its format, not just its text. That format descriptor is what makes the write in Task 10 non-destructive.

**Files:**

- Create: `src/StickyMD.Core/Notes/NoteFormat.cs`
- Create: `src/StickyMD.Core/Notes/NoteFile.cs`
- Test: `tests/StickyMD.Core.Tests/Notes/NoteFileReadTests.cs`

**Interfaces:**

- Consumes: `TempDir` from Task 2.
- Produces:
  - `enum NoteEncoding { Utf8NoBom, Utf8Bom, Utf16Le, Utf16Be }`
  - `enum NoteNewline { Crlf, Lf }`
  - `sealed record NoteFormat(NoteEncoding Encoding, NoteNewline Newline, bool TrailingNewline)` with `static NoteFormat Canonical { get; }` = `(Utf8NoBom, Crlf, true)`
  - `sealed record NoteContent(string Text, NoteFormat Format, string ContentHash)`
  - `static NoteContent NoteFile.Read(string path)`
  - `static string NoteFile.Sha256(byte[] bytes)`

`NoteContent.Text` always uses `\n` internally, whatever the file uses. `ContentHash` is SHA-256 of the **raw bytes on disk**, not the normalized text — that is what makes it comparable against a later read in Task 12's ledger.

- [ ] **Step 1: Write the failing tests**

`tests/StickyMD.Core.Tests/Notes/NoteFileReadTests.cs`:

```csharp
using System.Text;
using Shouldly;
using StickyMD.Core.Notes;
using StickyMD.Core.Tests.TestSupport;

namespace StickyMD.Core.Tests.Notes;

public class NoteFileReadTests
{
    [Fact]
    public void Reads_utf8_without_bom_and_lf()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        File.WriteAllBytes(path, "# Hi\nbody\n"u8.ToArray());

        var note = NoteFile.Read(path);

        note.Text.ShouldBe("# Hi\nbody\n");
        note.Format.Encoding.ShouldBe(NoteEncoding.Utf8NoBom);
        note.Format.Newline.ShouldBe(NoteNewline.Lf);
        note.Format.TrailingNewline.ShouldBeTrue();
    }

    [Fact]
    public void Reads_utf8_with_bom()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        File.WriteAllText(path, "# Hi\r\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var note = NoteFile.Read(path);

        note.Format.Encoding.ShouldBe(NoteEncoding.Utf8Bom);
        note.Text.ShouldBe("# Hi\n");
        note.Text.ShouldNotStartWith("\uFEFF");
    }

    [Fact]
    public void Reads_utf16_le()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        File.WriteAllText(path, "# Hi\r\n", new UnicodeEncoding(bigEndian: false, byteOrderMark: true));

        var note = NoteFile.Read(path);

        note.Format.Encoding.ShouldBe(NoteEncoding.Utf16Le);
        note.Text.ShouldBe("# Hi\n");
    }

    [Fact]
    public void Reads_utf16_be()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        File.WriteAllText(path, "# Hi\n", new UnicodeEncoding(bigEndian: true, byteOrderMark: true));

        var note = NoteFile.Read(path);

        note.Format.Encoding.ShouldBe(NoteEncoding.Utf16Be);
        note.Text.ShouldBe("# Hi\n");
    }

    [Fact]
    public void Detects_crlf_newlines()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        File.WriteAllBytes(path, "a\r\nb\r\n"u8.ToArray());

        var note = NoteFile.Read(path);

        note.Format.Newline.ShouldBe(NoteNewline.Crlf);
        note.Text.ShouldBe("a\nb\n");
    }

    [Fact]
    public void Mixed_newlines_resolve_to_the_dominant_convention()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        File.WriteAllBytes(path, "a\r\nb\r\nc\nd\r\n"u8.ToArray());

        NoteFile.Read(path).Format.Newline.ShouldBe(NoteNewline.Crlf);
    }

    [Fact]
    public void A_file_with_no_newlines_reports_the_canonical_convention()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        File.WriteAllBytes(path, "single line"u8.ToArray());

        var note = NoteFile.Read(path);

        note.Format.Newline.ShouldBe(NoteNewline.Crlf);
        note.Format.TrailingNewline.ShouldBeFalse();
    }

    [Fact]
    public void Detects_a_missing_trailing_newline()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        File.WriteAllBytes(path, "a\nb"u8.ToArray());

        NoteFile.Read(path).Format.TrailingNewline.ShouldBeFalse();
    }

    [Fact]
    public void An_empty_file_reads_as_empty_with_the_canonical_format()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        File.WriteAllBytes(path, []);

        var note = NoteFile.Read(path);

        note.Text.ShouldBe("");
        note.Format.ShouldBe(NoteFormat.Canonical with { TrailingNewline = false });
    }

    [Fact]
    public void Hash_is_over_the_raw_bytes_and_is_stable()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        File.WriteAllBytes(path, "hello"u8.ToArray());

        var first = NoteFile.Read(path);
        var second = NoteFile.Read(path);

        first.ContentHash.ShouldBe(second.ContentHash);
        first.ContentHash.Length.ShouldBe(64);
        first.ContentHash.ShouldBe(NoteFile.Sha256("hello"u8.ToArray()));
    }

    [Fact]
    public void Different_newline_conventions_produce_different_hashes()
    {
        using var dir = new TempDir();
        var lf = dir.File("lf.md");
        var crlf = dir.File("crlf.md");
        File.WriteAllBytes(lf, "a\nb"u8.ToArray());
        File.WriteAllBytes(crlf, "a\r\nb"u8.ToArray());

        NoteFile.Read(lf).ContentHash
            .ShouldNotBe(NoteFile.Read(crlf).ContentHash);
    }

    [Fact]
    public void Preserves_non_ascii_content()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        File.WriteAllText(path, "héllo — naïve 日本語\n", new UTF8Encoding(false));

        NoteFile.Read(path).Text.ShouldBe("héllo — naïve 日本語\n");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test --filter FullyQualifiedName~NoteFileReadTests
```

Expected: FAIL to compile — `NoteFile` does not exist.

- [ ] **Step 3: Write `NoteFormat`**

`src/StickyMD.Core/Notes/NoteFormat.cs`:

```csharp
namespace StickyMD.Core.Notes;

public enum NoteEncoding { Utf8NoBom, Utf8Bom, Utf16Le, Utf16Be }

public enum NoteNewline { Crlf, Lf }

/// <summary>
/// How a note is physically stored. Read from an existing file and reproduced
/// on write, so StickyMD never silently reformats a user's file.
/// </summary>
public sealed record NoteFormat(
    NoteEncoding Encoding,
    NoteNewline Newline,
    bool TrailingNewline)
{
    /// <summary>The format StickyMD gives to files it creates itself.</summary>
    public static NoteFormat Canonical { get; } =
        new(NoteEncoding.Utf8NoBom, NoteNewline.Crlf, TrailingNewline: true);
}

/// <param name="Text">Note text, always normalized to '\n' newlines.</param>
/// <param name="Format">The on-disk format, to be reproduced on write.</param>
/// <param name="ContentHash">SHA-256 hex of the RAW bytes on disk.</param>
public sealed record NoteContent(string Text, NoteFormat Format, string ContentHash);
```

- [ ] **Step 4: Write `NoteFile.Read`**

`src/StickyMD.Core/Notes/NoteFile.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace StickyMD.Core.Notes;

/// <summary>
/// Reads and writes a single note file without changing its physical format.
/// </summary>
public static class NoteFile
{
    public static NoteContent Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var encoding = DetectEncoding(bytes);
        var raw = Decode(bytes, encoding);

        var newline = DetectNewline(raw);
        var trailing = raw.EndsWith('\n');
        var text = raw.Replace("\r\n", "\n");

        return new NoteContent(
            text,
            new NoteFormat(encoding, newline, trailing),
            Sha256(bytes));
    }

    public static string Sha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes));

    private static NoteEncoding DetectEncoding(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
            return NoteEncoding.Utf8Bom;
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE)
            return NoteEncoding.Utf16Le;
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF)
            return NoteEncoding.Utf16Be;
        return NoteEncoding.Utf8NoBom;
    }

    private static string Decode(byte[] bytes, NoteEncoding encoding) => encoding switch
    {
        NoteEncoding.Utf8Bom => new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3),
        NoteEncoding.Utf16Le => new UnicodeEncoding(false, false).GetString(bytes, 2, bytes.Length - 2),
        NoteEncoding.Utf16Be => new UnicodeEncoding(true, false).GetString(bytes, 2, bytes.Length - 2),
        _ => new UTF8Encoding(false).GetString(bytes),
    };

    private static NoteNewline DetectNewline(string raw)
    {
        var crlf = 0;
        var loneLf = 0;

        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] != '\n') continue;
            if (i > 0 && raw[i - 1] == '\r') crlf++;
            else loneLf++;
        }

        if (crlf == 0 && loneLf == 0) return NoteFormat.Canonical.Newline;
        return crlf >= loneLf ? NoteNewline.Crlf : NoteNewline.Lf;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

```bash
dotnet test --filter FullyQualifiedName~NoteFileReadTests
```

Expected: PASS, 12 tests.

- [ ] **Step 6: Commit**

```bash
git add src/StickyMD.Core/Notes/NoteFormat.cs src/StickyMD.Core/Notes/NoteFile.cs tests/StickyMD.Core.Tests/Notes/NoteFileReadTests.cs
git commit -m "feat(core): read notes while detecting encoding and newlines"
```

---

## Task 10: NoteFile — atomic, format-preserving write

The single most destructive operation in the app. The spec's governing rule applies hardest here: **never lose text, never destroy a file.**

**Files:**

- Modify: `src/StickyMD.Core/Notes/NoteFile.cs`
- Test: `tests/StickyMD.Core.Tests/Notes/NoteFileWriteTests.cs`

**Interfaces:**

- Consumes: `NoteFormat`, `NoteContent`, `Sha256` from Task 9.
- Produces: `static WriteOutcome NoteFile.AtomicWrite(string path, string text, NoteFormat format)` and `readonly record struct WriteOutcome(long Size, DateTime LastWriteUtc, string ContentHash)`.

The outcome is exactly what Task 12's ledger records. The contract is **atomicity**, not a specific syscall: `File.Replace` when the target exists, `File.Move` when it does not, and a `File.Move(overwrite: true)` fallback when `File.Replace` is unsupported by the filesystem.

- [ ] **Step 1: Write the failing tests**

`tests/StickyMD.Core.Tests/Notes/NoteFileWriteTests.cs`:

```csharp
using System.Text;
using Shouldly;
using StickyMD.Core.Notes;
using StickyMD.Core.Tests.TestSupport;

namespace StickyMD.Core.Tests.Notes;

public class NoteFileWriteTests
{
    [Fact]
    public void Round_trips_utf8_no_bom_with_lf()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        File.WriteAllBytes(path, "a\nb\n"u8.ToArray());
        var original = NoteFile.Read(path);

        NoteFile.AtomicWrite(path, original.Text, original.Format);

        File.ReadAllBytes(path).ShouldBe("a\nb\n"u8.ToArray());
    }

    [Fact]
    public void Round_trips_utf8_with_bom_and_crlf()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        var expected = File.ReadAllBytes(WriteWith(path, "a\r\nb\r\n", new UTF8Encoding(true)));
        var original = NoteFile.Read(path);

        NoteFile.AtomicWrite(path, original.Text, original.Format);

        File.ReadAllBytes(path).ShouldBe(expected);
    }

    [Fact]
    public void Round_trips_utf16_le()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        var expected = File.ReadAllBytes(
            WriteWith(path, "a\r\nb\r\n", new UnicodeEncoding(false, true)));
        var original = NoteFile.Read(path);

        NoteFile.AtomicWrite(path, original.Text, original.Format);

        File.ReadAllBytes(path).ShouldBe(expected);
    }

    [Fact]
    public void Round_trips_utf16_be()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        var expected = File.ReadAllBytes(
            WriteWith(path, "a\nb\n", new UnicodeEncoding(true, true)));
        var original = NoteFile.Read(path);

        NoteFile.AtomicWrite(path, original.Text, original.Format);

        File.ReadAllBytes(path).ShouldBe(expected);
    }

    [Fact]
    public void Preserves_a_missing_trailing_newline()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        File.WriteAllBytes(path, "a\nb"u8.ToArray());
        var original = NoteFile.Read(path);

        NoteFile.AtomicWrite(path, original.Text, original.Format);

        File.ReadAllText(path).ShouldBe("a\nb");
    }

    [Fact]
    public void Adds_a_trailing_newline_when_the_format_says_so()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");

        NoteFile.AtomicWrite(path, "a\nb", NoteFormat.Canonical);

        File.ReadAllBytes(path).ShouldBe("a\r\nb\r\n"u8.ToArray());
    }

    [Fact]
    public void Does_not_double_the_trailing_newline()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");

        NoteFile.AtomicWrite(path, "a\n", NoteFormat.Canonical);

        File.ReadAllBytes(path).ShouldBe("a\r\n"u8.ToArray());
    }

    [Fact]
    public void New_files_get_the_canonical_format()
    {
        using var dir = new TempDir();
        var path = dir.File("new.md");

        NoteFile.AtomicWrite(path, "hello", NoteFormat.Canonical);

        var bytes = File.ReadAllBytes(path);
        bytes.ShouldBe("hello\r\n"u8.ToArray());
        bytes[0].ShouldNotBe((byte)0xEF); // no BOM
    }

    [Fact]
    public void Outcome_reports_size_hash_and_timestamp()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");

        var outcome = NoteFile.AtomicWrite(path, "hello", NoteFormat.Canonical);

        var bytes = File.ReadAllBytes(path);
        outcome.Size.ShouldBe(bytes.Length);
        outcome.ContentHash.ShouldBe(NoteFile.Sha256(bytes));
        outcome.ContentHash.ShouldBe(NoteFile.Read(path).ContentHash);
        outcome.LastWriteUtc.ShouldBe(
            File.GetLastWriteTimeUtc(path), tolerance: TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Leaves_no_temp_file_behind_on_success()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");

        NoteFile.AtomicWrite(path, "hello", NoteFormat.Canonical);

        Directory.GetFiles(dir.Path).ShouldBe([path]);
    }

    [Fact]
    public void A_failed_write_leaves_the_original_file_intact()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        File.WriteAllBytes(path, "ORIGINAL"u8.ToArray());
        File.SetAttributes(path, FileAttributes.ReadOnly);

        try
        {
            Should.Throw<Exception>(
                () => NoteFile.AtomicWrite(path, "REPLACEMENT", NoteFormat.Canonical));

            File.ReadAllText(path).ShouldBe("ORIGINAL");
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public void A_failed_write_leaves_no_temp_file_behind()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        File.WriteAllBytes(path, "ORIGINAL"u8.ToArray());
        File.SetAttributes(path, FileAttributes.ReadOnly);

        try
        {
            Should.Throw<Exception>(
                () => NoteFile.AtomicWrite(path, "REPLACEMENT", NoteFormat.Canonical));

            Directory.GetFiles(dir.Path).ShouldBe([path]);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public void Overwrites_an_existing_file_completely_not_partially()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        File.WriteAllText(path, "a very long original body that must not survive");

        NoteFile.AtomicWrite(path, "short", NoteFormat.Canonical);

        File.ReadAllText(path).ShouldBe("short\r\n");
    }

    private static string WriteWith(string path, string content, Encoding encoding)
    {
        File.WriteAllText(path, content, encoding);
        return path;
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test --filter FullyQualifiedName~NoteFileWriteTests
```

Expected: FAIL to compile — `AtomicWrite` does not exist.

- [ ] **Step 3: Add `AtomicWrite` to `NoteFile`**

Append inside the `NoteFile` class in `src/StickyMD.Core/Notes/NoteFile.cs`:

```csharp
    /// <summary>What a completed write produced. Feeds the write ledger.</summary>
    public readonly record struct WriteOutcome(
        long Size, DateTime LastWriteUtc, string ContentHash);

    private const string TempSuffix = ".stickymd-tmp";

    /// <summary>
    /// Writes the note so a reader never observes a partial file. The contract
    /// is atomicity, not a particular mechanism.
    /// </summary>
    public static WriteOutcome AtomicWrite(string path, string text, NoteFormat format)
    {
        var bytes = Encode(text ?? string.Empty, format);
        var temp = path + TempSuffix;

        try
        {
            File.WriteAllBytes(temp, bytes);

            if (File.Exists(path))
            {
                try
                {
                    File.Replace(temp, path, destinationBackupFileName: null);
                }
                catch (PlatformNotSupportedException)
                {
                    // Some network and virtual filesystems reject ReplaceFile.
                    File.Move(temp, path, overwrite: true);
                }
                catch (IOException)
                {
                    File.Move(temp, path, overwrite: true);
                }
            }
            else
            {
                File.Move(temp, path);
            }
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        return new WriteOutcome(
            bytes.LongLength,
            File.GetLastWriteTimeUtc(path),
            Sha256(bytes));
    }

    private static byte[] Encode(string text, NoteFormat format)
    {
        var normalized = text.Replace("\r\n", "\n");

        if (format.TrailingNewline)
        {
            if (!normalized.EndsWith('\n')) normalized += "\n";
        }
        else
        {
            normalized = normalized.TrimEnd('\n');
        }

        if (format.Newline == NoteNewline.Crlf)
            normalized = normalized.Replace("\n", "\r\n");

        return format.Encoding switch
        {
            NoteEncoding.Utf8Bom =>
                [.. Preamble(0xEF, 0xBB, 0xBF), .. new UTF8Encoding(false).GetBytes(normalized)],
            NoteEncoding.Utf16Le =>
                [.. Preamble(0xFF, 0xFE), .. new UnicodeEncoding(false, false).GetBytes(normalized)],
            NoteEncoding.Utf16Be =>
                [.. Preamble(0xFE, 0xFF), .. new UnicodeEncoding(true, false).GetBytes(normalized)],
            _ => new UTF8Encoding(false).GetBytes(normalized),
        };
    }

    private static byte[] Preamble(params byte[] bytes) => bytes;

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test --filter FullyQualifiedName~NoteFileWriteTests
```

Expected: PASS, 14 tests.

If `A_failed_write_leaves_the_original_file_intact` does not throw, the read-only attribute did not block `File.Replace` on this filesystem. Substitute a different induced failure — hold an exclusive `FileStream` open on `path` with `FileShare.None` for the duration of the call — and keep the same assertions.

- [ ] **Step 5: Run the whole suite**

```bash
dotnet test
```

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/StickyMD.Core/Notes/NoteFile.cs tests/StickyMD.Core.Tests/Notes/NoteFileWriteTests.cs
git commit -m "feat(core): write notes atomically preserving on-disk format

A failed write leaves the original file byte-identical and removes its
temp file."
```

---

## Tasks 11-18

### Task 11: WriteLedger

The mechanism that stops StickyMD reacting to its own writes. Matching on **size + content hash only** — never the timestamp — is the whole point; see the spec's "Self-write suppression".

**Files:**

- Create: `src/StickyMD.Core/Notes/WriteLedger.cs`
- Test: `tests/StickyMD.Core.Tests/Notes/WriteLedgerTests.cs`

**Interfaces:**

- Consumes: `NoteFile.WriteOutcome`, `NoteFile.Sha256` (Tasks 9-10).
- Produces:
  - `sealed record WriteFingerprint(string NormalizedPath, long Size, DateTime LastWriteUtc, string ContentHash)`
  - `interface IWriteLedger { void Record(string path, NoteFile.WriteOutcome outcome); bool IsOwnWrite(string path, long size, string contentHash); WriteFingerprint? Peek(string path); }`
  - `sealed class WriteLedger : IWriteLedger` with `static string Normalize(string path)`

Task 12's `NoteWatcher` takes an `IWriteLedger`. Plan B records into it after every save.

- [ ] **Step 1: Write the failing tests**

`tests/StickyMD.Core.Tests/Notes/WriteLedgerTests.cs`:

```csharp
using Shouldly;
using StickyMD.Core.Notes;
using StickyMD.Core.Tests.TestSupport;

namespace StickyMD.Core.Tests.Notes;

public class WriteLedgerTests
{
    [Fact]
    public void Recognises_a_write_it_recorded()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        var ledger = new WriteLedger();

        var outcome = NoteFile.AtomicWrite(path, "hello", NoteFormat.Canonical);
        ledger.Record(path, outcome);

        ledger.IsOwnWrite(path, outcome.Size, outcome.ContentHash).ShouldBeTrue();
    }

    [Fact]
    public void Rejects_a_different_hash()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        var ledger = new WriteLedger();
        var outcome = NoteFile.AtomicWrite(path, "hello", NoteFormat.Canonical);
        ledger.Record(path, outcome);

        ledger.IsOwnWrite(path, outcome.Size, NoteFile.Sha256("different"u8.ToArray()))
            .ShouldBeFalse();
    }

    [Fact]
    public void Rejects_a_different_size()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        var ledger = new WriteLedger();
        var outcome = NoteFile.AtomicWrite(path, "hello", NoteFormat.Canonical);
        ledger.Record(path, outcome);

        ledger.IsOwnWrite(path, outcome.Size + 1, outcome.ContentHash).ShouldBeFalse();
    }

    [Fact]
    public void Rejects_an_unknown_path()
    {
        new WriteLedger().IsOwnWrite(@"C:\never\seen.md", 5, "abc").ShouldBeFalse();
    }

    [Fact]
    public void A_changed_timestamp_does_not_break_recognition()
    {
        // The fingerprint stores the timestamp for diagnostics but must never
        // compare it -- that fragility is exactly what content hashing avoids.
        using var dir = new TempDir();
        var path = dir.File("a.md");
        var ledger = new WriteLedger();
        var outcome = NoteFile.AtomicWrite(path, "hello", NoteFormat.Canonical);
        ledger.Record(path, outcome);

        File.SetLastWriteTimeUtc(path, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        ledger.IsOwnWrite(path, outcome.Size, outcome.ContentHash).ShouldBeTrue();
    }

    [Fact]
    public void Path_comparison_is_case_insensitive()
    {
        using var dir = new TempDir();
        var path = dir.File("Note.md");
        var ledger = new WriteLedger();
        var outcome = NoteFile.AtomicWrite(path, "hello", NoteFormat.Canonical);
        ledger.Record(path, outcome);

        ledger.IsOwnWrite(path.ToUpperInvariant(), outcome.Size, outcome.ContentHash)
            .ShouldBeTrue();
    }

    [Fact]
    public void Path_comparison_normalises_relative_segments()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        var ledger = new WriteLedger();
        var outcome = NoteFile.AtomicWrite(path, "hello", NoteFormat.Canonical);
        ledger.Record(path, outcome);

        var awkward = Path.Combine(dir.Path, "sub", "..", "a.md");

        ledger.IsOwnWrite(awkward, outcome.Size, outcome.ContentHash).ShouldBeTrue();
    }

    [Fact]
    public void Recording_twice_keeps_only_the_latest()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        var ledger = new WriteLedger();

        var first = NoteFile.AtomicWrite(path, "one", NoteFormat.Canonical);
        ledger.Record(path, first);
        var second = NoteFile.AtomicWrite(path, "two", NoteFormat.Canonical);
        ledger.Record(path, second);

        ledger.IsOwnWrite(path, second.Size, second.ContentHash).ShouldBeTrue();
        ledger.IsOwnWrite(path, first.Size, first.ContentHash).ShouldBeFalse();
    }

    [Fact]
    public void Peek_exposes_the_stored_fingerprint()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        var ledger = new WriteLedger();
        var outcome = NoteFile.AtomicWrite(path, "hello", NoteFormat.Canonical);
        ledger.Record(path, outcome);

        var fingerprint = ledger.Peek(path).ShouldNotBeNull();

        fingerprint.Size.ShouldBe(outcome.Size);
        fingerprint.ContentHash.ShouldBe(outcome.ContentHash);
        fingerprint.LastWriteUtc.ShouldBe(outcome.LastWriteUtc);
        fingerprint.NormalizedPath.ShouldBe(WriteLedger.Normalize(path));
    }

    [Fact]
    public void Peek_returns_null_for_an_unknown_path()
        => new WriteLedger().Peek(@"C:\never\seen.md").ShouldBeNull();
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test --filter FullyQualifiedName~WriteLedgerTests
```

Expected: FAIL to compile — `WriteLedger` does not exist.

- [ ] **Step 3: Write the implementation**

`src/StickyMD.Core/Notes/WriteLedger.cs`:

```csharp
namespace StickyMD.Core.Notes;

/// <param name="LastWriteUtc">
/// Stored for diagnostics only. Never compared -- timestamps are unreliable
/// across OneDrive, differing filesystems, and metadata-touching tools.
/// </param>
public sealed record WriteFingerprint(
    string NormalizedPath,
    long Size,
    DateTime LastWriteUtc,
    string ContentHash);

public interface IWriteLedger
{
    void Record(string path, NoteFile.WriteOutcome outcome);

    /// <summary>
    /// True when the file's current size and content hash match StickyMD's own
    /// most recent write to that path.
    /// </summary>
    bool IsOwnWrite(string path, long size, string contentHash);

    WriteFingerprint? Peek(string path);
}

/// <summary>
/// Remembers what StickyMD last wrote to each note so the file watcher can
/// tell its own echo from a genuine external edit.
/// </summary>
public sealed class WriteLedger : IWriteLedger
{
    private readonly Dictionary<string, WriteFingerprint> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly object _gate = new();

    public static string Normalize(string path) => Path.GetFullPath(path);

    public void Record(string path, NoteFile.WriteOutcome outcome)
    {
        var normalized = Normalize(path);
        var fingerprint = new WriteFingerprint(
            normalized, outcome.Size, outcome.LastWriteUtc, outcome.ContentHash);

        lock (_gate) _entries[normalized] = fingerprint;
    }

    public bool IsOwnWrite(string path, long size, string contentHash)
    {
        var fingerprint = Peek(path);
        return fingerprint is not null
            && fingerprint.Size == size
            && string.Equals(fingerprint.ContentHash, contentHash, StringComparison.OrdinalIgnoreCase);
    }

    public WriteFingerprint? Peek(string path)
    {
        var normalized = Normalize(path);
        lock (_gate) return _entries.GetValueOrDefault(normalized);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test --filter FullyQualifiedName~WriteLedgerTests
```

Expected: PASS, 10 tests.

- [ ] **Step 5: Commit**

```bash
git add src/StickyMD.Core/Notes/WriteLedger.cs tests/StickyMD.Core.Tests/Notes/WriteLedgerTests.cs
git commit -m "feat(core): track own writes by content hash, not timestamp"
```

---

### Task 12: NoteWatcher

**Files:**

- Create: `src/StickyMD.Core/Notes/NoteWatcher.cs`
- Test: `tests/StickyMD.Core.Tests/Notes/NoteWatcherTests.cs`

**Interfaces:**

- Consumes: `IWriteLedger` (Task 11), `NoteFile` (Tasks 9-10), `Wait` helper (Task 2).
- Produces:
  - `sealed class NoteWatcher : IDisposable`
  - `NoteWatcher(string directory, IWriteLedger ledger, int debounceMs = 150)`
  - `event Action<string>? ExternalChanged`
  - `event Action<string>? Deleted`
  - `event Action<string, string>? Renamed` — `(oldPath, newPath)`
  - `event Action<Exception>? Recovered`

**Behavioral contract:**

- Watches `*.md`, top-level only.
- Change events are debounced by `debounceMs`; a burst of raw events for one path produces **one** logical event.
- On flush: file gone → `Deleted`; file present and `ledger.IsOwnWrite` → **silence**; otherwise → `ExternalChanged`.
- `Renamed` fires immediately, undebounced — it is already a single event, and it carries both paths. Per spec §8.2, a move _out_ of the watched directory arrives as `Deleted`, not `Renamed`; that is accepted behavior.
- On `FileSystemWatcher.Error` the inner watcher is disposed and recreated, then `Recovered` fires. `NoteWatcher` deliberately does **not** decide which notes to re-read — it has no idea which are open. Plan B's `WindowManager` handles `Recovered` by re-reading its open notes, because that is where the knowledge lives.

- [ ] **Step 1: Write the failing tests**

`tests/StickyMD.Core.Tests/Notes/NoteWatcherTests.cs`:

```csharp
using Shouldly;
using StickyMD.Core.Notes;
using StickyMD.Core.Tests.TestSupport;

namespace StickyMD.Core.Tests.Notes;

public class NoteWatcherTests
{
    [Fact]
    public void Raises_ExternalChanged_for_an_outside_write()
    {
        using var dir = new TempDir();
        var path = dir.WriteFile("a.md", "original");
        var changed = new List<string>();

        using var watcher = new NoteWatcher(dir.Path, new WriteLedger());
        watcher.ExternalChanged += p => changed.Add(p);

        File.WriteAllText(path, "edited by another app");

        Wait.Until(() => changed.Count > 0, "ExternalChanged to fire");
        changed.Single().ShouldBe(WriteLedger.Normalize(path));
    }

    [Fact]
    public void Stays_silent_for_a_write_made_through_NoteFile()
    {
        // THE critical test: StickyMD must never react to its own save.
        using var dir = new TempDir();
        var path = dir.WriteFile("a.md", "original");
        var ledger = new WriteLedger();
        var fired = false;

        using var watcher = new NoteWatcher(dir.Path, ledger);
        watcher.ExternalChanged += _ => fired = true;

        var outcome = NoteFile.AtomicWrite(path, "saved by StickyMD", NoteFormat.Canonical);
        ledger.Record(path, outcome);

        Wait.StaysFalse(() => fired, "ExternalChanged must not fire for our own write");
    }

    [Fact]
    public void Coalesces_a_burst_of_writes_into_one_event()
    {
        using var dir = new TempDir();
        var path = dir.WriteFile("a.md", "original");
        var count = 0;

        using var watcher = new NoteWatcher(dir.Path, new WriteLedger(), debounceMs: 400);
        watcher.ExternalChanged += _ => Interlocked.Increment(ref count);

        for (var i = 0; i < 6; i++) File.WriteAllText(path, $"edit {i}");

        Wait.Until(() => count > 0, "at least one event");
        Thread.Sleep(300);
        count.ShouldBe(1);
    }

    [Fact]
    public void Raises_Deleted_when_the_file_disappears()
    {
        using var dir = new TempDir();
        var path = dir.WriteFile("a.md", "original");
        var deleted = new List<string>();

        using var watcher = new NoteWatcher(dir.Path, new WriteLedger());
        watcher.Deleted += p => deleted.Add(p);

        File.Delete(path);

        Wait.Until(() => deleted.Count > 0, "Deleted to fire");
        deleted[0].ShouldBe(WriteLedger.Normalize(path));
    }

    [Fact]
    public void Raises_Renamed_with_both_paths_for_an_in_folder_rename()
    {
        using var dir = new TempDir();
        var oldPath = dir.WriteFile("old.md", "body");
        var newPath = dir.File("new.md");
        (string Old, string New)? seen = null;

        using var watcher = new NoteWatcher(dir.Path, new WriteLedger());
        watcher.Renamed += (o, n) => seen = (o, n);

        File.Move(oldPath, newPath);

        Wait.Until(() => seen is not null, "Renamed to fire");
        seen!.Value.Old.ShouldBe(WriteLedger.Normalize(oldPath));
        seen!.Value.New.ShouldBe(WriteLedger.Normalize(newPath));
    }

    [Fact]
    public void A_move_out_of_the_folder_is_reported_as_a_deletion()
    {
        // Spec 8.2: FileSystemWatcher only reports Renamed when both paths are
        // inside the watched directory.
        using var dir = new TempDir();
        using var elsewhere = new TempDir();
        var path = dir.WriteFile("a.md", "body");
        var deleted = false;
        var renamed = false;

        using var watcher = new NoteWatcher(dir.Path, new WriteLedger());
        watcher.Deleted += _ => deleted = true;
        watcher.Renamed += (_, _) => renamed = true;

        File.Move(path, elsewhere.File("a.md"));

        Wait.Until(() => deleted, "Deleted to fire for a move-out");
        renamed.ShouldBeFalse();
    }

    [Fact]
    public void Ignores_files_that_are_not_markdown()
    {
        using var dir = new TempDir();
        dir.WriteFile("a.md", "body");
        var fired = false;

        using var watcher = new NoteWatcher(dir.Path, new WriteLedger());
        watcher.ExternalChanged += _ => fired = true;

        File.WriteAllText(dir.File("notes.txt"), "not a note");

        Wait.StaysFalse(() => fired, "a .txt file must not raise a note event");
    }

    [Fact]
    public void Raises_ExternalChanged_for_a_newly_created_md_file()
    {
        using var dir = new TempDir();
        var changed = new List<string>();

        using var watcher = new NoteWatcher(dir.Path, new WriteLedger());
        watcher.ExternalChanged += p => changed.Add(p);

        File.WriteAllText(dir.File("fresh.md"), "dropped in by Obsidian");

        Wait.Until(() => changed.Count > 0, "ExternalChanged for a new file");
    }

    [Fact]
    public void Stops_raising_events_after_disposal()
    {
        using var dir = new TempDir();
        var path = dir.WriteFile("a.md", "original");
        var fired = false;

        var watcher = new NoteWatcher(dir.Path, new WriteLedger());
        watcher.ExternalChanged += _ => fired = true;
        watcher.Dispose();

        File.WriteAllText(path, "after disposal");

        Wait.StaysFalse(() => fired, "a disposed watcher must be inert");
    }

    [Fact]
    public void Construction_on_a_missing_directory_does_not_throw()
    {
        using var dir = new TempDir();
        var missing = Path.Combine(dir.Path, "not-created-yet");

        Should.NotThrow(() =>
        {
            using var watcher = new NoteWatcher(missing, new WriteLedger());
        });
    }
}
```

The `Coalesces` test uses a longer debounce so the six writes reliably land inside one window; the production default stays 150ms.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test --filter FullyQualifiedName~NoteWatcherTests
```

Expected: FAIL to compile — `NoteWatcher` does not exist.

- [ ] **Step 3: Write the implementation**

`src/StickyMD.Core/Notes/NoteWatcher.cs`:

```csharp
namespace StickyMD.Core.Notes;

/// <summary>
/// Debounced external-change notifications for the .md files in one directory.
/// Suppresses StickyMD's own writes via <see cref="IWriteLedger"/>.
/// </summary>
public sealed class NoteWatcher : IDisposable
{
    private const int TickMs = 25;

    private readonly string _directory;
    private readonly IWriteLedger _ledger;
    private readonly int _debounceMs;
    private readonly Dictionary<string, long> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly Timer _flushTimer;

    private FileSystemWatcher? _watcher;
    private bool _disposed;

    public event Action<string>? ExternalChanged;
    public event Action<string>? Deleted;
    public event Action<string, string>? Renamed;

    /// <summary>
    /// Fired after the underlying watcher was lost and recreated. Consumers
    /// should re-read whatever they currently have open -- events may have been
    /// dropped while the watcher was down.
    /// </summary>
    public event Action<Exception>? Recovered;

    public NoteWatcher(string directory, IWriteLedger ledger, int debounceMs = 150)
    {
        _directory = directory;
        _ledger = ledger;
        _debounceMs = debounceMs;

        _flushTimer = new Timer(_ => Flush(), null, TickMs, TickMs);
        TryStartWatcher();
    }

    private void TryStartWatcher()
    {
        try
        {
            if (!Directory.Exists(_directory)) return;

            var watcher = new FileSystemWatcher(_directory, "*.md")
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.LastWrite
                    | NotifyFilters.FileName
                    | NotifyFilters.Size,
            };

            watcher.Changed += OnChanged;
            watcher.Created += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Renamed += OnRenamed;
            watcher.Error += OnError;
            watcher.EnableRaisingEvents = true;

            _watcher = watcher;
        }
        catch (ArgumentException)
        {
            // Invalid path. Nothing to watch; the app surfaces this elsewhere.
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => Enqueue(e.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        Renamed?.Invoke(
            WriteLedger.Normalize(e.OldFullPath),
            WriteLedger.Normalize(e.FullPath));
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        // FileSystemWatcher drops events when its internal buffer overflows.
        // A watcher must never be allowed to silently stop watching.
        var watcher = _watcher;
        _watcher = null;

        if (watcher is not null)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= OnChanged;
            watcher.Created -= OnChanged;
            watcher.Deleted -= OnChanged;
            watcher.Renamed -= OnRenamed;
            watcher.Error -= OnError;
            watcher.Dispose();
        }

        if (_disposed) return;

        TryStartWatcher();
        Recovered?.Invoke(e.GetException());
    }

    private void Enqueue(string fullPath)
    {
        var normalized = WriteLedger.Normalize(fullPath);
        lock (_gate) _pending[normalized] = Environment.TickCount64 + _debounceMs;
    }

    private void Flush()
    {
        List<string> due;

        lock (_gate)
        {
            if (_pending.Count == 0) return;

            var now = Environment.TickCount64;
            due = _pending.Where(p => p.Value <= now).Select(p => p.Key).ToList();
            foreach (var path in due) _pending.Remove(path);
        }

        foreach (var path in due) Dispatch(path);
    }

    private void Dispatch(string path)
    {
        if (_disposed) return;

        if (!File.Exists(path))
        {
            Deleted?.Invoke(path);
            return;
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            if (_ledger.IsOwnWrite(path, bytes.LongLength, NoteFile.Sha256(bytes)))
                return;

            ExternalChanged?.Invoke(path);
        }
        catch (IOException)
        {
            // Still being written. Re-arm so the next tick tries again.
            Enqueue(path);
        }
        catch (UnauthorizedAccessException)
        {
            Enqueue(path);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _flushTimer.Dispose();

        var watcher = _watcher;
        _watcher = null;
        if (watcher is null) return;

        watcher.EnableRaisingEvents = false;
        watcher.Changed -= OnChanged;
        watcher.Created -= OnChanged;
        watcher.Deleted -= OnChanged;
        watcher.Renamed -= OnRenamed;
        watcher.Error -= OnError;
        watcher.Dispose();
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test --filter FullyQualifiedName~NoteWatcherTests
```

Expected: PASS, 10 tests.

These are the flakiest tests in the plan because they involve the real filesystem. If one fails intermittently, raise the `Wait.Until` timeout rather than adding sleeps — and never "fix" a flake by deleting the assertion.

- [ ] **Step 5: Commit**

```bash
git add src/StickyMD.Core/Notes/NoteWatcher.cs tests/StickyMD.Core.Tests/Notes/NoteWatcherTests.cs
git commit -m "feat(core): debounced note watcher that ignores its own writes

Recreates the underlying FileSystemWatcher on Error so a buffer
overflow cannot silently stop watching."
```

---

### Task 13: AppPaths, NoteIndex, and NoteIndexStore

**Files:**

- Create: `src/StickyMD.Core/Persistence/AppPaths.cs`
- Create: `src/StickyMD.Core/Persistence/NoteIndex.cs`
- Create: `src/StickyMD.Core/Persistence/NoteIndexStore.cs`
- Create: `src/StickyMD.Core/Persistence/JsonFile.cs`
- Modify: `src/StickyMD.Core/Notes/NoteFile.cs` (extract `AtomicWriteBytes`)
- Test: `tests/StickyMD.Core.Tests/Persistence/AppPathsTests.cs`
- Test: `tests/StickyMD.Core.Tests/Persistence/NoteIndexStoreTests.cs`

**Interfaces:**

- Consumes: `NoteColor` (Task 4), `NoteFile` (Tasks 9-10).
- Produces:
  - `static class AppPaths` with `AppData`, `NoteIndexFile`, `SettingsFile`, `RecoveryDir`
  - `sealed record NoteState(int X, int Y, int W, int H, string? Monitor, NoteColor Color, double Opacity, bool AlwaysOnTop, bool IsOpen, DateTime LastOpenedUtc)`
  - `sealed class NoteIndex { int Version {get;set;} = 1; Dictionary<string, NoteState> Notes {get;set;} }`
  - `sealed class NoteIndexStore(string filePath)` with `NoteIndex Load()`, `void Save(NoteIndex)`, `string? LastCorruptBackupPath { get; }`
  - `static class JsonFile` with `T? TryRead<T>(string path)`, `void Write<T>(string path, T value)`, `string BackupCorrupt(string path)`
  - `static NoteFile.WriteOutcome NoteFile.AtomicWriteBytes(string path, byte[] bytes)`

`Load()` **never throws.** A corrupt or unknown-version file is renamed to `notes.json.corrupt-N` and an empty index is returned — the notes themselves are untouched, only geometry is lost.

- [ ] **Step 1: Extract `AtomicWriteBytes` from `AtomicWrite`**

The JSON stores need the same atomicity guarantee as note writes, so hoist the primitive rather than duplicating it. In `src/StickyMD.Core/Notes/NoteFile.cs`, change `AtomicWrite` to delegate:

```csharp
    public static WriteOutcome AtomicWrite(string path, string text, NoteFormat format)
        => AtomicWriteBytes(path, Encode(text ?? string.Empty, format));

    /// <summary>
    /// Writes bytes so a reader never observes a partial file. The contract is
    /// atomicity, not a particular mechanism.
    /// </summary>
    public static WriteOutcome AtomicWriteBytes(string path, byte[] bytes)
    {
        var temp = path + TempSuffix;

        try
        {
            File.WriteAllBytes(temp, bytes);

            if (File.Exists(path))
            {
                try
                {
                    File.Replace(temp, path, destinationBackupFileName: null);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Move(temp, path, overwrite: true);
                }
                catch (IOException)
                {
                    File.Move(temp, path, overwrite: true);
                }
            }
            else
            {
                File.Move(temp, path);
            }
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        return new WriteOutcome(
            bytes.LongLength,
            File.GetLastWriteTimeUtc(path),
            Sha256(bytes));
    }
```

- [ ] **Step 2: Run Task 10's tests to prove the refactor changed nothing**

```bash
dotnet test --filter FullyQualifiedName~NoteFileWriteTests
```

Expected: PASS, 14 tests. A pure refactor must not move a single assertion.

- [ ] **Step 3: Write the failing tests**

`tests/StickyMD.Core.Tests/Persistence/AppPathsTests.cs`:

```csharp
using Shouldly;
using StickyMD.Core.Persistence;

namespace StickyMD.Core.Tests.Persistence;

public class AppPathsTests
{
    [Fact]
    public void AppData_lives_under_LocalApplicationData()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        AppPaths.AppData.ShouldStartWith(local);
        Path.GetFileName(AppPaths.AppData).ShouldBe("StickyMD");
    }

    [Fact]
    public void AppData_is_not_the_roaming_folder()
    {
        // Window coordinates are machine-specific; roaming them makes things worse.
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        AppPaths.AppData.ShouldNotStartWith(roaming);
    }

    [Fact]
    public void Exposes_the_three_expected_locations()
    {
        Path.GetFileName(AppPaths.NoteIndexFile).ShouldBe("notes.json");
        Path.GetFileName(AppPaths.SettingsFile).ShouldBe("settings.json");
        Path.GetFileName(AppPaths.RecoveryDir).ShouldBe("recovery");
    }

    [Fact]
    public void All_locations_sit_inside_AppData()
    {
        AppPaths.NoteIndexFile.ShouldStartWith(AppPaths.AppData);
        AppPaths.SettingsFile.ShouldStartWith(AppPaths.AppData);
        AppPaths.RecoveryDir.ShouldStartWith(AppPaths.AppData);
    }
}
```

`tests/StickyMD.Core.Tests/Persistence/NoteIndexStoreTests.cs`:

```csharp
using Shouldly;
using StickyMD.Core.Persistence;
using StickyMD.Core.Tests.TestSupport;
using StickyMD.Core.Theming;

namespace StickyMD.Core.Tests.Persistence;

public class NoteIndexStoreTests
{
    private static NoteState Sample => new(
        X: 1200, Y: 80, W: 320, H: 420,
        Monitor: @"\\.\DISPLAY2",
        Color: NoteColor.Blue,
        Opacity: 0.95,
        AlwaysOnTop: true,
        IsOpen: true,
        LastOpenedUtc: new DateTime(2026, 8, 22, 10, 14, 0, DateTimeKind.Utc));

    [Fact]
    public void Round_trips_every_field()
    {
        using var dir = new TempDir();
        var store = new NoteIndexStore(dir.File("notes.json"));
        var index = new NoteIndex();
        index.Notes[@"C:\notes\standup.md"] = Sample;

        store.Save(index);
        var loaded = store.Load();

        loaded.Version.ShouldBe(1);
        loaded.Notes[@"C:\notes\standup.md"].ShouldBe(Sample);
    }

    [Fact]
    public void Missing_file_loads_an_empty_index()
    {
        using var dir = new TempDir();
        var store = new NoteIndexStore(dir.File("notes.json"));

        var loaded = store.Load();

        loaded.Notes.ShouldBeEmpty();
        loaded.Version.ShouldBe(1);
    }

    [Fact]
    public void Path_keys_are_case_insensitive()
    {
        using var dir = new TempDir();
        var store = new NoteIndexStore(dir.File("notes.json"));
        var index = new NoteIndex();
        index.Notes[@"C:\Notes\Standup.md"] = Sample;
        store.Save(index);

        var loaded = store.Load();

        loaded.Notes.ContainsKey(@"c:\notes\standup.md").ShouldBeTrue();
    }

    [Fact]
    public void Corrupt_json_is_backed_up_and_an_empty_index_returned()
    {
        using var dir = new TempDir();
        var path = dir.File("notes.json");
        File.WriteAllText(path, "{ this is not json");
        var store = new NoteIndexStore(path);

        var loaded = store.Load();

        loaded.Notes.ShouldBeEmpty();
        store.LastCorruptBackupPath.ShouldNotBeNull();
        File.Exists(store.LastCorruptBackupPath!).ShouldBeTrue();
        File.Exists(path).ShouldBeFalse();
    }

    [Fact]
    public void An_unknown_version_is_treated_as_corrupt()
    {
        using var dir = new TempDir();
        var path = dir.File("notes.json");
        File.WriteAllText(path, """{"version":99,"notes":{}}""");
        var store = new NoteIndexStore(path);

        var loaded = store.Load();

        loaded.Notes.ShouldBeEmpty();
        store.LastCorruptBackupPath.ShouldNotBeNull();
    }

    [Fact]
    public void Corrupt_backups_get_increasing_suffixes()
    {
        using var dir = new TempDir();
        var path = dir.File("notes.json");

        File.WriteAllText(path, "broken");
        new NoteIndexStore(path).Load();
        File.WriteAllText(path, "broken again");
        new NoteIndexStore(path).Load();

        File.Exists(path + ".corrupt-1").ShouldBeTrue();
        File.Exists(path + ".corrupt-2").ShouldBeTrue();
    }

    [Fact]
    public void Serialised_json_uses_the_property_names_from_the_spec()
    {
        using var dir = new TempDir();
        var path = dir.File("notes.json");
        var store = new NoteIndexStore(path);
        var index = new NoteIndex();
        index.Notes[@"C:\notes\a.md"] = Sample;

        store.Save(index);
        var json = File.ReadAllText(path);

        json.ShouldContain("\"version\"");
        json.ShouldContain("\"notes\"");
        json.ShouldContain("\"alwaysOnTop\"");
        json.ShouldContain("\"lastOpenedUtc\"");
        json.ShouldContain("\"isOpen\"");
        json.ShouldContain("\"monitor\"");
    }

    [Fact]
    public void Color_is_serialised_as_a_name_not_a_number()
    {
        using var dir = new TempDir();
        var path = dir.File("notes.json");
        var store = new NoteIndexStore(path);
        var index = new NoteIndex();
        index.Notes[@"C:\notes\a.md"] = Sample;

        store.Save(index);

        File.ReadAllText(path).ShouldContain("\"Blue\"");
    }

    [Fact]
    public void Save_creates_the_directory_if_needed()
    {
        using var dir = new TempDir();
        var nested = Path.Combine(dir.Path, "StickyMD", "notes.json");
        var store = new NoteIndexStore(nested);

        store.Save(new NoteIndex());

        File.Exists(nested).ShouldBeTrue();
    }

    [Fact]
    public void Save_leaves_no_temp_file_behind()
    {
        using var dir = new TempDir();
        var path = dir.File("notes.json");
        var store = new NoteIndexStore(path);

        store.Save(new NoteIndex());

        Directory.GetFiles(dir.Path).ShouldBe([path]);
    }

    [Fact]
    public void Load_never_throws_for_an_empty_file()
    {
        using var dir = new TempDir();
        var path = dir.File("notes.json");
        File.WriteAllBytes(path, []);

        Should.NotThrow(() => new NoteIndexStore(path).Load());
    }
}
```

- [ ] **Step 4: Run the tests to verify they fail**

```bash
dotnet test --filter "FullyQualifiedName~AppPathsTests|FullyQualifiedName~NoteIndexStoreTests"
```

Expected: FAIL to compile — `AppPaths` and `NoteIndexStore` do not exist.

- [ ] **Step 5: Write `AppPaths`**

`src/StickyMD.Core/Persistence/AppPaths.cs`:

```csharp
namespace StickyMD.Core.Persistence;

/// <summary>
/// Where StickyMD keeps its own state. LOCAL app data, never roaming --
/// window coordinates and monitor placement are machine-specific.
/// </summary>
public static class AppPaths
{
    public static string AppData { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StickyMD");

    public static string NoteIndexFile { get; } = Path.Combine(AppData, "notes.json");

    public static string SettingsFile { get; } = Path.Combine(AppData, "settings.json");

    public static string RecoveryDir { get; } = Path.Combine(AppData, "recovery");
}
```

- [ ] **Step 6: Write `JsonFile`**

`src/StickyMD.Core/Persistence/JsonFile.cs`:

```csharp
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StickyMD.Core.Notes;

namespace StickyMD.Core.Persistence;

/// <summary>Atomic, corruption-tolerant JSON persistence for app state.</summary>
public static class JsonFile
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Returns null when the file is absent, empty, or unparseable.</summary>
    public static T? TryRead<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;

            var json = File.ReadAllText(path);
            return string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static void Write<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(value, Options);
        NoteFile.AtomicWriteBytes(path, new UTF8Encoding(false).GetBytes(json));
    }

    /// <summary>
    /// Moves an unusable file aside so the app can start clean, and returns the
    /// backup path. Nothing the user authored is ever destroyed here.
    /// </summary>
    public static string BackupCorrupt(string path)
    {
        for (var n = 1; ; n++)
        {
            var candidate = $"{path}.corrupt-{n}";
            if (File.Exists(candidate)) continue;

            try { File.Move(path, candidate); }
            catch (IOException) { /* leave the original in place */ }

            return candidate;
        }
    }
}
```

- [ ] **Step 7: Write `NoteIndex` and `NoteIndexStore`**

`src/StickyMD.Core/Persistence/NoteIndex.cs`:

```csharp
using StickyMD.Core.Theming;

namespace StickyMD.Core.Persistence;

/// <summary>
/// Per-note StickyMD state. Geometry is in PHYSICAL screen pixels.
/// <paramref name="IsOpen"/> means "belongs on my desktop and returns next
/// startup" -- only an explicit Close Note may clear it.
/// </summary>
public sealed record NoteState(
    int X,
    int Y,
    int W,
    int H,
    string? Monitor,
    NoteColor Color,
    double Opacity,
    bool AlwaysOnTop,
    bool IsOpen,
    DateTime LastOpenedUtc);

public sealed class NoteIndex
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public Dictionary<string, NoteState> Notes { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}
```

`src/StickyMD.Core/Persistence/NoteIndexStore.cs`:

```csharp
namespace StickyMD.Core.Persistence;

/// <summary>
/// Loads and saves notes.json. Load never throws: an unusable file is moved
/// aside and an empty index returned. The .md files are never touched, so the
/// worst case is losing window geometry.
/// </summary>
public sealed class NoteIndexStore(string filePath)
{
    public string FilePath { get; } = filePath;

    public string? LastCorruptBackupPath { get; private set; }

    public NoteIndex Load()
    {
        if (!File.Exists(FilePath)) return new NoteIndex();

        var index = JsonFile.TryRead<NoteIndex>(FilePath);

        if (index is null || index.Version != NoteIndex.CurrentVersion)
        {
            LastCorruptBackupPath = JsonFile.BackupCorrupt(FilePath);
            return new NoteIndex();
        }

        // System.Text.Json rebuilds the dictionary with the default comparer,
        // so restore case-insensitive path keys.
        index.Notes = new Dictionary<string, NoteState>(
            index.Notes, StringComparer.OrdinalIgnoreCase);

        return index;
    }

    public void Save(NoteIndex index) => JsonFile.Write(FilePath, index);
}
```

- [ ] **Step 8: Run the tests to verify they pass**

```bash
dotnet test --filter "FullyQualifiedName~AppPathsTests|FullyQualifiedName~NoteIndexStoreTests"
```

Expected: PASS, 15 tests.

- [ ] **Step 9: Commit**

```bash
git add src/StickyMD.Core/Persistence src/StickyMD.Core/Notes/NoteFile.cs tests/StickyMD.Core.Tests/Persistence
git commit -m "feat(core): persist the note index under LOCALAPPDATA

Load never throws; a corrupt index is moved aside so notes survive."
```

---

### Task 14: AppSettings and SettingsStore

**Files:**

- Create: `src/StickyMD.Core/Persistence/AppSettings.cs`
- Create: `src/StickyMD.Core/Persistence/SettingsStore.cs`
- Test: `tests/StickyMD.Core.Tests/Persistence/SettingsStoreTests.cs`

**Interfaces:**

- Consumes: `JsonFile` (Task 13), `NoteColor` (Task 4).
- Produces:
  - `enum ThemePreference { Light, Dark, System }`
  - `sealed record AppSettings` with the defaults from Global Constraints and `static string DefaultNotesRoot`
  - `sealed class SettingsStore(string filePath)` with `AppSettings Load()`, `void Save(AppSettings)`, `string? LastCorruptBackupPath`

**`ThemePreference` is deliberately a different type from Task 4's `ThemeMode`.** `ThemePreference` is what the user chose and includes `System`; `ThemeMode` is the resolved light-or-dark that `NotePalette` needs. Plan B resolves one into the other by reading the OS setting. Collapsing them would force `NotePalette` to answer a question it cannot see the input for.

**`launchAtStartup` must not exist as a member.** A test asserts it never appears in the serialized JSON.

- [ ] **Step 1: Write the failing tests**

`tests/StickyMD.Core.Tests/Persistence/SettingsStoreTests.cs`:

```csharp
using Shouldly;
using StickyMD.Core.Persistence;
using StickyMD.Core.Tests.TestSupport;
using StickyMD.Core.Theming;

namespace StickyMD.Core.Tests.Persistence;

public class SettingsStoreTests
{
    [Fact]
    public void Defaults_match_the_spec()
    {
        var defaults = new AppSettings();

        defaults.DefaultColor.ShouldBe(NoteColor.Yellow);
        defaults.DefaultOpacity.ShouldBe(1.0);
        defaults.DefaultWidth.ShouldBe(300);
        defaults.DefaultHeight.ShouldBe(340);
        defaults.AllowRemoteImages.ShouldBeFalse();
        defaults.Theme.ShouldBe(ThemePreference.System);
        defaults.NewNoteHotkey.ShouldBe("Ctrl+Alt+N");
        defaults.ShowHideHotkey.ShouldBe("Ctrl+Alt+S");
    }

    [Fact]
    public void Default_notes_root_is_under_the_user_profile()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        AppSettings.DefaultNotesRoot.ShouldBe(Path.Combine(profile, "StickyMD Notes"));
        new AppSettings().NotesRoot.ShouldBe(AppSettings.DefaultNotesRoot);
    }

    [Fact]
    public void Round_trips_every_field()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        var settings = new AppSettings
        {
            NotesRoot = @"D:\my notes",
            DefaultColor = NoteColor.Charcoal,
            DefaultOpacity = 0.8,
            DefaultWidth = 420,
            DefaultHeight = 500,
            Theme = ThemePreference.Dark,
            NewNoteHotkey = "Ctrl+Shift+N",
            ShowHideHotkey = "Ctrl+Shift+S",
            AllowRemoteImages = true,
        };

        store.Save(settings);

        store.Load().ShouldBe(settings);
    }

    [Fact]
    public void Missing_file_loads_defaults()
    {
        using var dir = new TempDir();

        new SettingsStore(dir.File("settings.json")).Load().ShouldBe(new AppSettings());
    }

    [Fact]
    public void Corrupt_file_is_backed_up_and_defaults_returned()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        File.WriteAllText(path, "not json at all {{{");
        var store = new SettingsStore(path);

        store.Load().ShouldBe(new AppSettings());

        store.LastCorruptBackupPath.ShouldNotBeNull();
        File.Exists(store.LastCorruptBackupPath!).ShouldBeTrue();
    }

    [Fact]
    public void Serialised_json_never_contains_launchAtStartup()
    {
        // The Run registry key is the single source of truth. Caching it here
        // would create two states to reconcile.
        using var dir = new TempDir();
        var path = dir.File("settings.json");

        new SettingsStore(path).Save(new AppSettings());

        File.ReadAllText(path).ShouldNotContain("launchAtStartup", Case.Insensitive);
    }

    [Fact]
    public void AppSettings_has_no_launch_at_startup_member()
    {
        typeof(AppSettings).GetProperties()
            .Select(p => p.Name)
            .ShouldNotContain(n => n.Contains("Startup", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Theme_is_serialised_as_a_name()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");

        new SettingsStore(path).Save(new AppSettings { Theme = ThemePreference.Dark });

        File.ReadAllText(path).ShouldContain("\"Dark\"");
    }

    [Fact]
    public void Unknown_json_properties_are_ignored_rather_than_fatal()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        File.WriteAllText(path, """{"defaultWidth":444,"somethingFromTheFuture":true}""");

        var loaded = new SettingsStore(path).Load();

        loaded.DefaultWidth.ShouldBe(444);
        loaded.DefaultColor.ShouldBe(NoteColor.Yellow);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test --filter FullyQualifiedName~SettingsStoreTests
```

Expected: FAIL to compile — `AppSettings` does not exist.

- [ ] **Step 3: Write `AppSettings`**

`src/StickyMD.Core/Persistence/AppSettings.cs`:

```csharp
using StickyMD.Core.Theming;

namespace StickyMD.Core.Persistence;

/// <summary>
/// What the user asked for. Distinct from <see cref="ThemeMode"/>, which is the
/// resolved light-or-dark that the palette needs.
/// </summary>
public enum ThemePreference { Light, Dark, System }

/// <summary>
/// App-wide preferences. Deliberately contains NO launch-at-startup flag --
/// the HKCU Run key is the single source of truth for that.
/// </summary>
public sealed record AppSettings
{
    public static string DefaultNotesRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "StickyMD Notes");

    public string NotesRoot { get; init; } = DefaultNotesRoot;

    public NoteColor DefaultColor { get; init; } = NoteColor.Yellow;

    public double DefaultOpacity { get; init; } = 1.0;

    public int DefaultWidth { get; init; } = 300;

    public int DefaultHeight { get; init; } = 340;

    public ThemePreference Theme { get; init; } = ThemePreference.System;

    public string NewNoteHotkey { get; init; } = "Ctrl+Alt+N";

    public string ShowHideHotkey { get; init; } = "Ctrl+Alt+S";

    public bool AllowRemoteImages { get; init; }
}
```

- [ ] **Step 4: Write `SettingsStore`**

`src/StickyMD.Core/Persistence/SettingsStore.cs`:

```csharp
namespace StickyMD.Core.Persistence;

/// <summary>
/// Loads and saves settings.json. Load never throws; an unusable file is moved
/// aside and defaults returned.
/// </summary>
public sealed class SettingsStore(string filePath)
{
    public string FilePath { get; } = filePath;

    public string? LastCorruptBackupPath { get; private set; }

    public AppSettings Load()
    {
        if (!File.Exists(FilePath)) return new AppSettings();

        var settings = JsonFile.TryRead<AppSettings>(FilePath);
        if (settings is not null) return settings;

        LastCorruptBackupPath = JsonFile.BackupCorrupt(FilePath);
        return new AppSettings();
    }

    public void Save(AppSettings settings) => JsonFile.Write(FilePath, settings);
}
```

- [ ] **Step 5: Run the tests to verify they pass**

```bash
dotnet test --filter FullyQualifiedName~SettingsStoreTests
```

Expected: PASS, 9 tests.

- [ ] **Step 6: Commit**

```bash
git add src/StickyMD.Core/Persistence/AppSettings.cs src/StickyMD.Core/Persistence/SettingsStore.cs tests/StickyMD.Core.Tests/Persistence/SettingsStoreTests.cs
git commit -m "feat(core): persist app settings with no launch-at-startup duplication"
```

---

### Task 15: RecoveryStore

The last line of defence against losing typed text. The envelope is **self-describing** on purpose: the filename is a hash and therefore not reversible, so the note's identity must live inside the file.

**Files:**

- Create: `src/StickyMD.Core/Persistence/RecoveryStore.cs`
- Test: `tests/StickyMD.Core.Tests/Persistence/RecoveryStoreTests.cs`

**Interfaces:**

- Consumes: `JsonFile` (Task 13), `NoteFile.Sha256` (Task 9), `WriteLedger.Normalize` (Task 11).
- Produces:
  - `sealed record RecoveryEnvelope(string OriginalPath, string Content, DateTime CreatedUtc, string LastKnownDiskHash)`
  - `sealed class RecoveryStore(string directory)` with `Save`, `TryLoad`, `Clear`, `LoadAll`, and `static string FileNameFor(string notePath)`

- [ ] **Step 1: Write the failing tests**

`tests/StickyMD.Core.Tests/Persistence/RecoveryStoreTests.cs`:

```csharp
using Shouldly;
using StickyMD.Core.Persistence;
using StickyMD.Core.Tests.TestSupport;

namespace StickyMD.Core.Tests.Persistence;

public class RecoveryStoreTests
{
    private static RecoveryEnvelope Envelope(string path, string content = "unsaved text")
        => new(path, content, new DateTime(2026, 8, 22, 10, 14, 0, DateTimeKind.Utc), "sha256:abc");

    [Fact]
    public void Round_trips_every_field()
    {
        using var dir = new TempDir();
        var store = new RecoveryStore(dir.Path);
        var envelope = Envelope(@"C:\notes\standup.md");

        store.Save(envelope);

        store.TryLoad(@"C:\notes\standup.md").ShouldBe(envelope);
    }

    [Fact]
    public void Saving_twice_keeps_one_snapshot_per_note()
    {
        using var dir = new TempDir();
        var store = new RecoveryStore(dir.Path);
        var path = @"C:\notes\a.md";

        store.Save(Envelope(path, "first"));
        store.Save(Envelope(path, "second"));

        store.TryLoad(path)!.Content.ShouldBe("second");
        Directory.GetFiles(dir.Path).Length.ShouldBe(1);
    }

    [Fact]
    public void Clear_removes_the_snapshot()
    {
        using var dir = new TempDir();
        var store = new RecoveryStore(dir.Path);
        var path = @"C:\notes\a.md";
        store.Save(Envelope(path));

        store.Clear(path);

        store.TryLoad(path).ShouldBeNull();
        Directory.GetFiles(dir.Path).ShouldBeEmpty();
    }

    [Fact]
    public void Clear_on_a_note_with_no_snapshot_is_harmless()
    {
        using var dir = new TempDir();
        var store = new RecoveryStore(dir.Path);

        Should.NotThrow(() => store.Clear(@"C:\notes\never-saved.md"));
    }

    [Fact]
    public void TryLoad_returns_null_for_an_unknown_note()
    {
        using var dir = new TempDir();

        new RecoveryStore(dir.Path).TryLoad(@"C:\notes\unknown.md").ShouldBeNull();
    }

    [Fact]
    public void LoadAll_returns_every_snapshot()
    {
        using var dir = new TempDir();
        var store = new RecoveryStore(dir.Path);
        store.Save(Envelope(@"C:\notes\a.md", "A"));
        store.Save(Envelope(@"C:\notes\b.md", "B"));

        var all = store.LoadAll();

        all.Count.ShouldBe(2);
        all.Select(e => e.Content).ShouldBe(new[] { "A", "B" }, ignoreOrder: true);
    }

    [Fact]
    public void LoadAll_identifies_notes_without_needing_the_index()
    {
        // The whole reason the envelope is self-describing: a corrupt or missing
        // notes.json must not orphan recovered text.
        using var dir = new TempDir();
        var store = new RecoveryStore(dir.Path);
        store.Save(Envelope(@"C:\notes\standup.md", "typed but never saved"));

        var recovered = new RecoveryStore(dir.Path).LoadAll().Single();

        recovered.OriginalPath.ShouldBe(@"C:\notes\standup.md");
        recovered.Content.ShouldBe("typed but never saved");
    }

    [Fact]
    public void LoadAll_skips_a_corrupt_envelope_instead_of_throwing()
    {
        using var dir = new TempDir();
        var store = new RecoveryStore(dir.Path);
        store.Save(Envelope(@"C:\notes\good.md", "good"));
        File.WriteAllText(Path.Combine(dir.Path, "garbage.json"), "not json {{{");

        var all = store.LoadAll();

        all.Count.ShouldBe(1);
        all[0].Content.ShouldBe("good");
    }

    [Fact]
    public void LoadAll_on_a_missing_directory_returns_empty()
    {
        using var dir = new TempDir();
        var missing = Path.Combine(dir.Path, "no-recovery-here");

        new RecoveryStore(missing).LoadAll().ShouldBeEmpty();
    }

    [Fact]
    public void FileNameFor_is_a_sha256_hex_json_file()
    {
        var name = RecoveryStore.FileNameFor(@"C:\notes\a.md");

        name.ShouldEndWith(".json");
        Path.GetFileNameWithoutExtension(name).Length.ShouldBe(64);
    }

    [Fact]
    public void FileNameFor_ignores_path_casing_and_relative_segments()
    {
        var a = RecoveryStore.FileNameFor(@"C:\notes\a.md");
        var b = RecoveryStore.FileNameFor(@"C:\NOTES\sub\..\A.MD");

        a.ShouldBe(b);
    }

    [Fact]
    public void Different_notes_get_different_files()
    {
        RecoveryStore.FileNameFor(@"C:\notes\a.md")
            .ShouldNotBe(RecoveryStore.FileNameFor(@"C:\notes\b.md"));
    }

    [Fact]
    public void Save_creates_the_recovery_directory()
    {
        using var dir = new TempDir();
        var nested = Path.Combine(dir.Path, "recovery");

        new RecoveryStore(nested).Save(Envelope(@"C:\notes\a.md"));

        Directory.Exists(nested).ShouldBeTrue();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test --filter FullyQualifiedName~RecoveryStoreTests
```

Expected: FAIL to compile — `RecoveryStore` does not exist.

- [ ] **Step 3: Write the implementation**

`src/StickyMD.Core/Persistence/RecoveryStore.cs`:

```csharp
using System.Text;
using StickyMD.Core.Notes;

namespace StickyMD.Core.Persistence;

/// <summary>
/// An unsaved buffer plus enough metadata to identify where it came from.
/// Self-describing because the snapshot's filename is a one-way hash.
/// </summary>
public sealed record RecoveryEnvelope(
    string OriginalPath,
    string Content,
    DateTime CreatedUtc,
    string LastKnownDiskHash);

/// <summary>
/// Holds text that could not be written to its note. One snapshot per note,
/// cleared as soon as a real save succeeds.
/// </summary>
public sealed class RecoveryStore(string directory)
{
    public string Directory { get; } = directory;

    public static string FileNameFor(string notePath)
    {
        var normalized = WriteLedger.Normalize(notePath).ToLowerInvariant();
        var hash = NoteFile.Sha256(new UTF8Encoding(false).GetBytes(normalized));
        return hash + ".json";
    }

    public void Save(RecoveryEnvelope envelope)
        => JsonFile.Write(PathFor(envelope.OriginalPath), envelope);

    public RecoveryEnvelope? TryLoad(string notePath)
        => JsonFile.TryRead<RecoveryEnvelope>(PathFor(notePath));

    public void Clear(string notePath)
    {
        var path = PathFor(notePath);
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    public IReadOnlyList<RecoveryEnvelope> LoadAll()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];

        var envelopes = new List<RecoveryEnvelope>();

        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            var envelope = JsonFile.TryRead<RecoveryEnvelope>(file);
            if (envelope is not null) envelopes.Add(envelope);
        }

        return envelopes;
    }

    private string PathFor(string notePath)
        => Path.Combine(Directory, FileNameFor(notePath));
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test --filter FullyQualifiedName~RecoveryStoreTests
```

Expected: PASS, 13 tests.

- [ ] **Step 5: Commit**

```bash
git add src/StickyMD.Core/Persistence/RecoveryStore.cs tests/StickyMD.Core.Tests/Persistence/RecoveryStoreTests.cs
git commit -m "feat(core): self-describing recovery snapshots for unsaved buffers"
```

---

### Task 16: MarkdownRenderer

The biggest task in this plan, and the one carrying the most risk: **the whole clickable-checkbox feature rests on Markdig's source spans actually pointing at the `[ ]` token.** Step 1 tests that assumption directly, before anything is built on it.

**Files:**

- Create: `src/StickyMD.Core/Markdown/MarkdownRenderer.cs`
- Create: `src/StickyMD.Core/Markdown/SourceSpanTaskListRenderer.cs`
- Test: `tests/StickyMD.Core.Tests/Markdown/MarkdownRendererTests.cs`

**Interfaces:**

- Consumes: `NoteFile.Sha256` (Task 9).
- Produces:
  - `sealed record RenderOptions(bool AllowRemoteImages, string VirtualHost = "note.local")`
  - `sealed record RenderResult(string Html, string Token)`
  - `sealed class MarkdownRenderer` with `RenderResult Render(string markdown, RenderOptions options)` and `static string ComputeToken(string markdown)`
  - `const string MarkdownRenderer.BlockedScheme = "stickymd-blocked:"`

Task 17's `TaskListToggler` consumes the emitted spans. Plan B's `HtmlDocumentBuilder` consumes `RenderResult`.

**Span convention: Markdig's `SourceSpan.End` is INCLUSIVE.** A `[ ]` at index 2 has `Start = 2`, `End = 4`, so the source text is `markdown.Substring(Start, End - Start + 1)`. Getting this wrong by one is the most likely bug in the feature; a test pins it.

**Image URL policy:**

| URL form                             | Result                                           |
| ------------------------------------ | ------------------------------------------------ |
| `img.png`, `images/d.png`            | `https://note.local/img.png`                     |
| `data:image/png;base64,…`            | unchanged                                        |
| `https://…` with `AllowRemoteImages` | unchanged                                        |
| `https://…` without                  | `stickymd-blocked:<original>`                    |
| `../escape.png`                      | `stickymd-blocked:` — escapes the note directory |
| `/rooted.png`                        | `stickymd-blocked:` — escapes the note directory |
| `C:\abs.png`, `file:///…`            | `stickymd-blocked:`                              |

- [ ] **Step 1: Write the failing tests**

`tests/StickyMD.Core.Tests/Markdown/MarkdownRendererTests.cs`:

````csharp
using System.Text.RegularExpressions;
using Shouldly;
using StickyMD.Core.Markdown;

namespace StickyMD.Core.Tests.Markdown;

public class MarkdownRendererTests
{
    private static readonly MarkdownRenderer Renderer = new();
    private static readonly RenderOptions Default = new(AllowRemoteImages: false);
    private static readonly RenderOptions RemoteAllowed = new(AllowRemoteImages: true);

    private static string Html(string markdown, RenderOptions? options = null)
        => Renderer.Render(markdown, options ?? Default).Html;

    // ---- the load-bearing assumption ----

    [Fact]
    public void Task_checkbox_span_indexes_exactly_the_bracket_token()
    {
        // If this fails, Markdig's precise source locations are not what the
        // checkbox feature assumes and Task 17 must be redesigned. Note that
        // SourceSpan.End is INCLUSIVE.
        const string markdown = "- [ ] buy milk";

        var (start, end) = FirstSpan(Html(markdown));

        markdown.Substring(start, end - start + 1).ShouldBe("[ ]");
    }

    [Fact]
    public void Span_is_correct_when_the_checkbox_is_not_at_the_start_of_the_document()
    {
        const string markdown = "# Shopping\n\nsome prose\n\n- [ ] buy milk\n";

        var (start, end) = FirstSpan(Html(markdown));

        markdown.Substring(start, end - start + 1).ShouldBe("[ ]");
    }

    [Fact]
    public void Every_checkbox_span_indexes_its_own_token()
    {
        const string markdown = "- [ ] one\n\nprose\n\n- [x] two\n- [ ] three\n";

        var spans = AllSpans(Html(markdown));

        spans.Count.ShouldBe(3);
        markdown.Substring(spans[0].Start, 3).ShouldBe("[ ]");
        markdown.Substring(spans[1].Start, 3).ShouldBe("[x]");
        markdown.Substring(spans[2].Start, 3).ShouldBe("[ ]");
    }

    [Fact]
    public void Checked_boxes_render_as_checked()
    {
        Html("- [x] done").ShouldContain("checked");
        Html("- [ ] not done").ShouldNotContain("checked");
    }

    [Fact]
    public void Checkboxes_render_as_inputs_with_span_attributes()
    {
        var html = Html("- [ ] a");

        html.ShouldContain("type=\"checkbox\"");
        html.ShouldContain("data-span-start=");
        html.ShouldContain("data-span-end=");
    }

    // ---- markdown fidelity ----

    [Fact]
    public void Renders_headings()
        => Html("# Title").ShouldContain("<h1");

    [Fact]
    public void Renders_tables_via_advanced_extensions()
        => Html("| a | b |\n|---|---|\n| 1 | 2 |").ShouldContain("<table");

    [Fact]
    public void Renders_fenced_code()
        => Html("```\ncode\n```").ShouldContain("<pre");

    [Fact]
    public void A_single_newline_becomes_a_hard_break()
    {
        // Nobody expects markdown soft-wrap semantics in a sticky note.
        Html("line one\nline two").ShouldContain("<br");
    }

    [Fact]
    public void Raw_html_is_escaped_not_executed()
    {
        var html = Html("<script>alert(1)</script>");

        html.ShouldNotContain("<script>");
        html.ShouldContain("&lt;script&gt;");
    }

    [Fact]
    public void Raw_html_attributes_are_escaped_too()
        => Html("<img src=x onerror=alert(1)>").ShouldNotContain("onerror=alert");

    // ---- image policy ----

    [Fact]
    public void Relative_image_is_rewritten_to_the_virtual_host()
        => Html("![d](diagram.png)").ShouldContain("https://note.local/diagram.png");

    [Fact]
    public void Nested_relative_image_keeps_its_subpath()
        => Html("![d](images/diagram.png)")
            .ShouldContain("https://note.local/images/diagram.png");

    [Fact]
    public void Data_uri_image_is_left_alone()
    {
        const string uri = "data:image/png;base64,iVBORw0KGgo=";

        Html($"![d]({uri})").ShouldContain(uri);
    }

    [Fact]
    public void Remote_image_is_blocked_by_default()
    {
        var html = Html("![t](https://example.com/tracker?id=123)");

        html.ShouldContain(MarkdownRenderer.BlockedScheme);
        html.ShouldNotContain("src=\"https://example.com");
    }

    [Fact]
    public void Remote_image_is_allowed_when_the_option_is_set()
        => Html("![t](https://example.com/pic.png)", RemoteAllowed)
            .ShouldContain("https://example.com/pic.png");

    [Fact]
    public void Parent_relative_image_is_blocked()
        => Html("![d](../shared/diagram.png)")
            .ShouldContain(MarkdownRenderer.BlockedScheme);

    [Fact]
    public void Root_relative_image_is_blocked()
        => Html("![d](/rooted.png)").ShouldContain(MarkdownRenderer.BlockedScheme);

    [Fact]
    public void Absolute_windows_path_image_is_blocked()
        => Html(@"![d](C:\pics\x.png)").ShouldContain(MarkdownRenderer.BlockedScheme);

    [Fact]
    public void File_uri_image_is_blocked()
        => Html("![d](file:///C:/pics/x.png)").ShouldContain(MarkdownRenderer.BlockedScheme);

    [Fact]
    public void Links_are_not_treated_as_images()
    {
        // Only image URLs are rewritten. Link navigation is the host's job.
        var html = Html("[docs](https://example.com)");

        html.ShouldContain("https://example.com");
        html.ShouldNotContain(MarkdownRenderer.BlockedScheme);
    }

    // ---- token ----

    [Fact]
    public void Token_is_stable_for_identical_input()
        => Renderer.Render("# a", Default).Token
            .ShouldBe(Renderer.Render("# a", Default).Token);

    [Fact]
    public void Token_changes_when_the_markdown_changes()
        => Renderer.Render("# a", Default).Token
            .ShouldNotBe(Renderer.Render("# b", Default).Token);

    [Fact]
    public void Token_matches_ComputeToken()
        => Renderer.Render("# a", Default).Token
            .ShouldBe(MarkdownRenderer.ComputeToken("# a"));

    [Fact]
    public void Empty_markdown_renders_without_throwing()
        => Should.NotThrow(() => Renderer.Render("", Default));

    [Fact]
    public void Null_markdown_is_treated_as_empty()
        => Renderer.Render(null!, Default).Html.ShouldNotBeNull();

    // ---- helpers ----

    private static readonly Regex SpanPair = new(
        @"data-span-start=""(?<start>\d+)""\s+data-span-end=""(?<end>\d+)""",
        RegexOptions.Compiled);

    private static (int Start, int End) FirstSpan(string html) => AllSpans(html)[0];

    private static List<(int Start, int End)> AllSpans(string html)
        => SpanPair.Matches(html)
            .Select(m => (int.Parse(m.Groups["start"].Value),
                          int.Parse(m.Groups["end"].Value)))
            .ToList();
}
````

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test --filter FullyQualifiedName~MarkdownRendererTests
```

Expected: FAIL to compile — `MarkdownRenderer` does not exist.

- [ ] **Step 3: Write the task-list renderer**

`src/StickyMD.Core/Markdown/SourceSpanTaskListRenderer.cs`:

```csharp
using Markdig.Extensions.TaskLists;
using Markdig.Renderers;
using Markdig.Renderers.Html;

namespace StickyMD.Core.Markdown;

/// <summary>
/// Emits each task checkbox carrying the source span of its "[ ]" token, so a
/// click can be applied to that exact range instead of an ordinal position.
/// Markdig's SourceSpan.End is INCLUSIVE.
/// </summary>
internal sealed class SourceSpanTaskListRenderer : HtmlObjectRenderer<TaskList>
{
    protected override void Write(HtmlRenderer renderer, TaskList task)
    {
        renderer.Write("<input type=\"checkbox\" data-span-start=\"");
        renderer.Write(task.Span.Start.ToString());
        renderer.Write("\" data-span-end=\"");
        renderer.Write(task.Span.End.ToString());
        renderer.Write('"');

        if (task.Checked) renderer.Write(" checked=\"checked\"");

        renderer.Write(" />");
    }
}
```

- [ ] **Step 4: Write the renderer**

`src/StickyMD.Core/Markdown/MarkdownRenderer.cs`:

```csharp
using System.Text;
using Markdig;
using Markdig.Extensions.TaskLists;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using StickyMD.Core.Notes;

namespace StickyMD.Core.Markdown;

/// <param name="AllowRemoteImages">
/// False by default. Notes sync, and a shared note must not make network
/// requests just because StickyMD rendered it.
/// </param>
public sealed record RenderOptions(bool AllowRemoteImages, string VirtualHost = "note.local");

/// <param name="Token">
/// SHA-256 of the markdown at render time. A checkbox click carries it back so
/// a stale click is rejected rather than misapplied.
/// </param>
public sealed record RenderResult(string Html, string Token);

public sealed class MarkdownRenderer
{
    /// <summary>Marks an image the resource policy refused to load.</summary>
    public const string BlockedScheme = "stickymd-blocked:";

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseSoftlineBreakAsHardlineBreak()
        .UsePreciseSourceLocation()
        .DisableHtml()
        .Build();

    public static string ComputeToken(string markdown)
        => NoteFile.Sha256(new UTF8Encoding(false).GetBytes(markdown ?? string.Empty));

    public RenderResult Render(string markdown, RenderOptions options)
    {
        markdown ??= string.Empty;

        var document = Markdig.Markdown.Parse(markdown, Pipeline);
        RewriteImageUrls(document, options);

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        renderer.ObjectRenderers.Replace<TaskListRenderer>(new SourceSpanTaskListRenderer());
        renderer.Render(document);
        writer.Flush();

        return new RenderResult(writer.ToString(), ComputeToken(markdown));
    }

    private static void RewriteImageUrls(MarkdownDocument document, RenderOptions options)
    {
        foreach (var link in document.Descendants<LinkInline>())
        {
            if (!link.IsImage) continue;
            link.Url = ResolveImageUrl(link.Url, options);
        }
    }

    internal static string ResolveImageUrl(string? url, RenderOptions options)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;

        var trimmed = url.Trim();

        if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return trimmed;

        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return options.AllowRemoteImages ? trimmed : BlockedScheme + trimmed;
        }

        if (EscapesNoteDirectory(trimmed)) return BlockedScheme + trimmed;

        return $"https://{options.VirtualHost}/{trimmed.TrimStart('.', '/')}";
    }

    private static bool EscapesNoteDirectory(string url)
        => url.StartsWith('/')
        || url.StartsWith('\\')
        || url.Contains("..", StringComparison.Ordinal)
        || url.Contains("://", StringComparison.Ordinal)
        || (url.Length > 1 && url[1] == ':');
}
```

- [ ] **Step 5: Run the tests to verify they pass**

```bash
dotnet test --filter FullyQualifiedName~MarkdownRendererTests
```

Expected: PASS, 27 tests.

**If `Task_checkbox_span_indexes_exactly_the_bracket_token` fails, stop and report it.** Two likely causes: `SourceSpan.End` is exclusive rather than inclusive on this Markdig version (adjust the arithmetic and the test comment together), or `TaskList.Span` covers something wider than `[ ]`. In the second case Task 17's validation will reject every click, so the renderer must be changed to compute the bracket offset itself — do not paper over it by loosening the assertion.

A third, quieter failure mode: `ObjectRenderers.Replace<TaskListRenderer>` returns `false` and does nothing if `UseAdvancedExtensions()` registered a differently-typed task-list renderer. `Checkboxes_render_as_inputs_with_span_attributes` catches this — the output will have no `data-span-*` attributes at all. If it happens, `Remove` the existing renderer and `Add` ours instead of `Replace`.

- [ ] **Step 6: Commit**

```bash
git add src/StickyMD.Core/Markdown tests/StickyMD.Core.Tests/Markdown
git commit -m "feat(core): render markdown with span-carrying checkboxes

Raw HTML disabled and remote images blocked by default; relative
images rewritten to the note.local virtual host."
```

---

### Task 17: TaskListToggler

**Files:**

- Create: `src/StickyMD.Core/Markdown/TaskListToggler.cs`
- Test: `tests/StickyMD.Core.Tests/Markdown/TaskListTogglerTests.cs`

**Interfaces:**

- Consumes: `MarkdownRenderer` (Task 16) for the end-to-end test.
- Produces:
  - `readonly record struct ToggleResult(bool Applied, string Markdown, string? Reason)`
  - `static ToggleResult TaskListToggler.Toggle(string markdown, int spanStart, int spanEndInclusive)`

**Contract:** validate before mutating. The span must be in range, exactly three characters, and read `[ ]`, `[x]`, or `[X]`. Anything else returns `Applied: false` with the markdown unchanged and a reason — a rejected click is a correct outcome, corruption is not. Exactly three characters change; nothing else in the document is reformatted.

- [ ] **Step 1: Write the failing tests**

`tests/StickyMD.Core.Tests/Markdown/TaskListTogglerTests.cs`:

````csharp
using Shouldly;
using StickyMD.Core.Markdown;

namespace StickyMD.Core.Tests.Markdown;

public class TaskListTogglerTests
{
    [Fact]
    public void Checks_an_unchecked_box()
    {
        var result = TaskListToggler.Toggle("- [ ] buy milk", 2, 4);

        result.Applied.ShouldBeTrue();
        result.Markdown.ShouldBe("- [x] buy milk");
    }

    [Fact]
    public void Unchecks_a_checked_box()
    {
        var result = TaskListToggler.Toggle("- [x] buy milk", 2, 4);

        result.Applied.ShouldBeTrue();
        result.Markdown.ShouldBe("- [ ] buy milk");
    }

    [Fact]
    public void Unchecks_an_uppercase_checked_box()
    {
        TaskListToggler.Toggle("- [X] buy milk", 2, 4)
            .Markdown.ShouldBe("- [ ] buy milk");
    }

    [Fact]
    public void Toggling_twice_returns_the_original()
    {
        var once = TaskListToggler.Toggle("- [ ] a", 2, 4);

        TaskListToggler.Toggle(once.Markdown, 2, 4).Markdown.ShouldBe("- [ ] a");
    }

    [Fact]
    public void Changes_only_the_targeted_box()
    {
        const string markdown = "- [ ] one\n- [ ] two\n- [ ] three\n";
        var second = markdown.IndexOf("[ ] two", StringComparison.Ordinal);

        var result = TaskListToggler.Toggle(markdown, second, second + 2);

        result.Markdown.ShouldBe("- [ ] one\n- [x] two\n- [ ] three\n");
    }

    [Fact]
    public void Leaves_the_rest_of_the_document_byte_identical()
    {
        const string markdown = "# T\r\n\r\n- [ ] a\r\n\r\n> quote\r\n\r\n```\ncode\n```\r\n";
        var span = markdown.IndexOf("[ ]", StringComparison.Ordinal);

        var result = TaskListToggler.Toggle(markdown, span, span + 2);

        result.Markdown.Length.ShouldBe(markdown.Length);
        result.Markdown.Remove(span, 3).ShouldBe(markdown.Remove(span, 3));
    }

    [Fact]
    public void Rejects_a_span_that_is_not_a_checkbox()
    {
        const string markdown = "- [ ] buy milk";

        var result = TaskListToggler.Toggle(markdown, 6, 8);

        result.Applied.ShouldBeFalse();
        result.Markdown.ShouldBe(markdown);
        result.Reason.ShouldNotBeNull();
    }

    [Fact]
    public void Rejects_a_span_of_the_wrong_length()
    {
        const string markdown = "- [ ] buy milk";

        TaskListToggler.Toggle(markdown, 2, 5).Applied.ShouldBeFalse();
        TaskListToggler.Toggle(markdown, 2, 3).Applied.ShouldBeFalse();
    }

    [Fact]
    public void Rejects_a_span_past_the_end_of_the_document()
    {
        const string markdown = "- [ ] a";

        var result = TaskListToggler.Toggle(markdown, 100, 102);

        result.Applied.ShouldBeFalse();
        result.Markdown.ShouldBe(markdown);
    }

    [Fact]
    public void Rejects_a_negative_span()
        => TaskListToggler.Toggle("- [ ] a", -1, 1).Applied.ShouldBeFalse();

    [Fact]
    public void Rejects_an_inverted_span()
        => TaskListToggler.Toggle("- [ ] a", 4, 2).Applied.ShouldBeFalse();

    [Fact]
    public void Rejects_null_markdown()
        => TaskListToggler.Toggle(null!, 2, 4).Applied.ShouldBeFalse();

    [Fact]
    public void Rejects_a_bracket_pair_that_is_not_a_task_marker()
    {
        // "[y]" is not a checkbox, so a span pointing at it must be refused.
        const string markdown = "- [y] weird";

        TaskListToggler.Toggle(markdown, 2, 4).Applied.ShouldBeFalse();
    }

    [Fact]
    public void End_to_end_a_rendered_span_toggles_the_right_box()
    {
        // The proof that Task 16 and Task 17 agree on the span convention.
        const string markdown = "# Shopping\n\n- [ ] milk\n- [ ] eggs\n";
        var rendered = new MarkdownRenderer()
            .Render(markdown, new RenderOptions(AllowRemoteImages: false));

        var spans = System.Text.RegularExpressions.Regex
            .Matches(rendered.Html,
                @"data-span-start=""(\d+)""\s+data-span-end=""(\d+)""")
            .Select(m => (Start: int.Parse(m.Groups[1].Value),
                          End: int.Parse(m.Groups[2].Value)))
            .ToList();

        spans.Count.ShouldBe(2);

        var result = TaskListToggler.Toggle(markdown, spans[1].Start, spans[1].End);

        result.Applied.ShouldBeTrue();
        result.Markdown.ShouldBe("# Shopping\n\n- [ ] milk\n- [x] eggs\n");
    }
}
````

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test --filter FullyQualifiedName~TaskListTogglerTests
```

Expected: FAIL to compile — `TaskListToggler` does not exist.

- [ ] **Step 3: Write the implementation**

`src/StickyMD.Core/Markdown/TaskListToggler.cs`:

```csharp
namespace StickyMD.Core.Markdown;

/// <param name="Applied">
/// False means the click was refused and <paramref name="Markdown"/> is the
/// unchanged input. Refusing is a correct outcome; corrupting is not.
/// </param>
public readonly record struct ToggleResult(bool Applied, string Markdown, string? Reason);

/// <summary>
/// Flips a task checkbox at an exact source span, validating the span before
/// touching anything. Span end is INCLUSIVE, matching Markdig.
/// </summary>
public static class TaskListToggler
{
    private const int TokenLength = 3;

    public static ToggleResult Toggle(string markdown, int spanStart, int spanEndInclusive)
    {
        if (markdown is null)
            return new ToggleResult(false, string.Empty, "Markdown was null.");

        if (spanStart < 0 || spanEndInclusive < spanStart)
            return new ToggleResult(false, markdown, "Span is inverted or negative.");

        var length = spanEndInclusive - spanStart + 1;

        if (length != TokenLength)
            return new ToggleResult(false, markdown,
                $"Span covers {length} characters; a task marker is {TokenLength}.");

        if (spanEndInclusive >= markdown.Length)
            return new ToggleResult(false, markdown, "Span extends past the document.");

        var token = markdown.Substring(spanStart, TokenLength);

        var replacement = token switch
        {
            "[ ]" => "[x]",
            "[x]" or "[X]" => "[ ]",
            _ => null,
        };

        if (replacement is null)
            return new ToggleResult(false, markdown,
                $"Span reads '{token}', which is not a task marker.");

        var updated = string.Concat(
            markdown.AsSpan(0, spanStart),
            replacement,
            markdown.AsSpan(spanEndInclusive + 1));

        return new ToggleResult(true, updated, null);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test --filter FullyQualifiedName~TaskListTogglerTests
```

Expected: PASS, 14 tests.

- [ ] **Step 5: Commit**

```bash
git add src/StickyMD.Core/Markdown/TaskListToggler.cs tests/StickyMD.Core.Tests/Markdown/TaskListTogglerTests.cs
git commit -m "feat(core): toggle task checkboxes by validated source span"
```

---

### Task 18: NoteRepository

The last Core unit. **Deletion is deliberately absent** — Recycle Bin deletion is platform behavior and lives in Plan B's `IFileDeletionService`, which is what keeps Core Win32-free.

**Files:**

- Create: `src/StickyMD.Core/Notes/IClock.cs`
- Create: `src/StickyMD.Core/Notes/NoteRepository.cs`
- Test: `tests/StickyMD.Core.Tests/Notes/NoteRepositoryTests.cs`

**Interfaces:**

- Consumes: `NoteFile`, `NoteFormat` (Tasks 9-10).
- Produces:
  - `interface IClock { DateTime UtcNow { get; } }` and `sealed class SystemClock : IClock`
  - `sealed class NoteRepository(string notesRoot, IClock clock)` with `EnsureRootExists()`, `IReadOnlyList<string> EnumerateRoot()`, `string CreateNew()`, `string Rename(string currentPath, string newFileName)`

The clock is injected so the date-based filename is testable without freezing real time.

**`CreateNew`** writes `<yyyy-MM-dd>-untitled.md` in the root, suffixing `-2`, `-3`… on collision, with the canonical format (UTF-8 no BOM, CRLF, trailing newline). A brand-new note is therefore a 2-byte `\r\n` file, not zero bytes — that follows from the canonical rule and is pinned by a test rather than left to surprise someone.

- [ ] **Step 1: Write the failing tests**

`tests/StickyMD.Core.Tests/Notes/NoteRepositoryTests.cs`:

```csharp
using Shouldly;
using StickyMD.Core.Notes;
using StickyMD.Core.Tests.TestSupport;

namespace StickyMD.Core.Tests.Notes;

public class NoteRepositoryTests
{
    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }

    private static IClock On(int y, int m, int d)
        => new FixedClock(new DateTime(y, m, d, 12, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void EnsureRootExists_creates_the_directory()
    {
        using var dir = new TempDir();
        var root = Path.Combine(dir.Path, "StickyMD Notes");
        var repo = new NoteRepository(root, On(2026, 8, 22));

        repo.EnsureRootExists();

        Directory.Exists(root).ShouldBeTrue();
    }

    [Fact]
    public void EnumerateRoot_on_a_missing_directory_returns_empty()
    {
        using var dir = new TempDir();
        var repo = new NoteRepository(Path.Combine(dir.Path, "nope"), On(2026, 8, 22));

        repo.EnumerateRoot().ShouldBeEmpty();
    }

    [Fact]
    public void EnumerateRoot_returns_only_markdown_files()
    {
        using var dir = new TempDir();
        dir.WriteFile("a.md", "a");
        dir.WriteFile("b.md", "b");
        dir.WriteFile("notes.txt", "not a note");
        var repo = new NoteRepository(dir.Path, On(2026, 8, 22));

        var notes = repo.EnumerateRoot();

        notes.Count.ShouldBe(2);
        notes.ShouldAllBe(p => p.EndsWith(".md"));
    }

    [Fact]
    public void EnumerateRoot_does_not_descend_into_subdirectories()
    {
        using var dir = new TempDir();
        dir.WriteFile("top.md", "top");
        var sub = Directory.CreateDirectory(Path.Combine(dir.Path, "sub"));
        File.WriteAllText(Path.Combine(sub.FullName, "nested.md"), "nested");
        var repo = new NoteRepository(dir.Path, On(2026, 8, 22));

        repo.EnumerateRoot().Count.ShouldBe(1);
    }

    [Fact]
    public void EnumerateRoot_returns_full_paths_in_a_stable_order()
    {
        using var dir = new TempDir();
        dir.WriteFile("b.md", "b");
        dir.WriteFile("a.md", "a");
        var repo = new NoteRepository(dir.Path, On(2026, 8, 22));

        var notes = repo.EnumerateRoot();

        notes[0].ShouldBe(Path.Combine(dir.Path, "a.md"));
        notes[1].ShouldBe(Path.Combine(dir.Path, "b.md"));
    }

    [Fact]
    public void CreateNew_uses_the_injected_date()
    {
        using var dir = new TempDir();
        var repo = new NoteRepository(dir.Path, On(2026, 8, 22));

        var path = repo.CreateNew();

        Path.GetFileName(path).ShouldBe("2026-08-22-untitled.md");
        File.Exists(path).ShouldBeTrue();
    }

    [Fact]
    public void CreateNew_suffixes_on_collision()
    {
        using var dir = new TempDir();
        var repo = new NoteRepository(dir.Path, On(2026, 8, 22));

        repo.CreateNew();
        var second = repo.CreateNew();
        var third = repo.CreateNew();

        Path.GetFileName(second).ShouldBe("2026-08-22-untitled-2.md");
        Path.GetFileName(third).ShouldBe("2026-08-22-untitled-3.md");
    }

    [Fact]
    public void CreateNew_creates_the_root_if_it_is_missing()
    {
        using var dir = new TempDir();
        var root = Path.Combine(dir.Path, "StickyMD Notes");
        var repo = new NoteRepository(root, On(2026, 8, 22));

        var path = repo.CreateNew();

        File.Exists(path).ShouldBeTrue();
    }

    [Fact]
    public void CreateNew_writes_the_canonical_format()
    {
        using var dir = new TempDir();
        var repo = new NoteRepository(dir.Path, On(2026, 8, 22));

        var path = repo.CreateNew();

        File.ReadAllBytes(path).ShouldBe("\r\n"u8.ToArray());
        NoteFile.Read(path).Format.ShouldBe(NoteFormat.Canonical);
    }

    [Fact]
    public void CreateNew_leaves_nothing_but_the_note_in_the_root()
    {
        // The notes root holds user content ONLY -- no temp files, no app state.
        using var dir = new TempDir();
        var repo = new NoteRepository(dir.Path, On(2026, 8, 22));

        var path = repo.CreateNew();

        Directory.GetFiles(dir.Path).ShouldBe([path]);
    }

    [Fact]
    public void Rename_moves_the_file_and_returns_the_new_path()
    {
        using var dir = new TempDir();
        var original = dir.WriteFile("old.md", "body");
        var repo = new NoteRepository(dir.Path, On(2026, 8, 22));

        var renamed = repo.Rename(original, "standup.md");

        renamed.ShouldBe(Path.Combine(dir.Path, "standup.md"));
        File.Exists(original).ShouldBeFalse();
        File.ReadAllText(renamed).ShouldBe("body");
    }

    [Fact]
    public void Rename_appends_the_md_extension_when_omitted()
    {
        using var dir = new TempDir();
        var original = dir.WriteFile("old.md", "body");
        var repo = new NoteRepository(dir.Path, On(2026, 8, 22));

        Path.GetFileName(repo.Rename(original, "standup")).ShouldBe("standup.md");
    }

    [Fact]
    public void Rename_rejects_a_path_separator()
    {
        using var dir = new TempDir();
        var original = dir.WriteFile("old.md", "body");
        var repo = new NoteRepository(dir.Path, On(2026, 8, 22));

        Should.Throw<ArgumentException>(() => repo.Rename(original, @"sub\standup.md"));
        Should.Throw<ArgumentException>(() => repo.Rename(original, "sub/standup.md"));
        File.Exists(original).ShouldBeTrue();
    }

    [Fact]
    public void Rename_rejects_invalid_filename_characters()
    {
        using var dir = new TempDir();
        var original = dir.WriteFile("old.md", "body");
        var repo = new NoteRepository(dir.Path, On(2026, 8, 22));

        Should.Throw<ArgumentException>(() => repo.Rename(original, "sta:ndup.md"));
    }

    [Fact]
    public void Rename_rejects_an_empty_name()
    {
        using var dir = new TempDir();
        var original = dir.WriteFile("old.md", "body");
        var repo = new NoteRepository(dir.Path, On(2026, 8, 22));

        Should.Throw<ArgumentException>(() => repo.Rename(original, "   "));
    }

    [Fact]
    public void Rename_refuses_to_overwrite_an_existing_note()
    {
        using var dir = new TempDir();
        var original = dir.WriteFile("old.md", "body");
        dir.WriteFile("taken.md", "someone else");
        var repo = new NoteRepository(dir.Path, On(2026, 8, 22));

        Should.Throw<IOException>(() => repo.Rename(original, "taken.md"));

        File.ReadAllText(dir.File("taken.md")).ShouldBe("someone else");
        File.Exists(original).ShouldBeTrue();
    }

    [Fact]
    public void Rename_to_the_same_name_is_a_no_op()
    {
        using var dir = new TempDir();
        var original = dir.WriteFile("same.md", "body");
        var repo = new NoteRepository(dir.Path, On(2026, 8, 22));

        repo.Rename(original, "same.md").ShouldBe(original);
        File.ReadAllText(original).ShouldBe("body");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test --filter FullyQualifiedName~NoteRepositoryTests
```

Expected: FAIL to compile — `NoteRepository` does not exist.

- [ ] **Step 3: Write `IClock`**

`src/StickyMD.Core/Notes/IClock.cs`:

```csharp
namespace StickyMD.Core.Notes;

/// <summary>Injected so date-derived filenames are testable.</summary>
public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
```

- [ ] **Step 4: Write `NoteRepository`**

`src/StickyMD.Core/Notes/NoteRepository.cs`:

```csharp
namespace StickyMD.Core.Notes;

/// <summary>
/// The notes root as a collection of .md files. Nothing app-owned is ever
/// written here.
/// </summary>
/// <remarks>
/// Deletion is absent by design: sending a file to the Recycle Bin is platform
/// behavior and belongs in the App layer, keeping Core free of Win32.
/// </remarks>
public sealed class NoteRepository(string notesRoot, IClock clock)
{
    private const string UntitledStem = "untitled";

    public string NotesRoot { get; } = notesRoot;

    public void EnsureRootExists() => Directory.CreateDirectory(NotesRoot);

    public IReadOnlyList<string> EnumerateRoot()
    {
        if (!Directory.Exists(NotesRoot)) return [];

        return Directory
            .EnumerateFiles(NotesRoot, "*.md", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string CreateNew()
    {
        EnsureRootExists();

        var date = clock.UtcNow.ToString("yyyy-MM-dd");
        var path = Path.Combine(NotesRoot, $"{date}-{UntitledStem}.md");

        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(NotesRoot, $"{date}-{UntitledStem}-{n}.md");

        NoteFile.AtomicWrite(path, string.Empty, NoteFormat.Canonical);
        return path;
    }

    /// <summary>
    /// Renames a note on disk. The caller is responsible for re-keying the
    /// index entry.
    /// </summary>
    public string Rename(string currentPath, string newFileName)
    {
        if (string.IsNullOrWhiteSpace(newFileName))
            throw new ArgumentException("A note name cannot be empty.", nameof(newFileName));

        var name = newFileName.Trim();

        if (name.Contains('/') || name.Contains('\\'))
            throw new ArgumentException(
                "A note name cannot contain a path separator.", nameof(newFileName));

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException(
                "A note name contains characters Windows does not allow.", nameof(newFileName));

        if (!name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            name += ".md";

        var directory = Path.GetDirectoryName(Path.GetFullPath(currentPath))
            ?? NotesRoot;
        var target = Path.Combine(directory, name);

        if (string.Equals(Path.GetFullPath(currentPath), target, StringComparison.OrdinalIgnoreCase))
            return currentPath;

        if (File.Exists(target))
            throw new IOException($"A note named '{name}' already exists.");

        File.Move(currentPath, target);
        return target;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

```bash
dotnet test --filter FullyQualifiedName~NoteRepositoryTests
```

Expected: PASS, 17 tests.

- [ ] **Step 6: Run the entire suite**

```bash
dotnet test
```

Expected: PASS. Roughly 190 tests across all 18 tasks, and **Plan A is done** — Core is complete and fully tested.

- [ ] **Step 7: Commit**

```bash
git add src/StickyMD.Core/Notes/IClock.cs src/StickyMD.Core/Notes/NoteRepository.cs tests/StickyMD.Core.Tests/Notes/NoteRepositoryTests.cs
git commit -m "feat(core): enumerate, create, and rename notes in the notes root"
```

---

### Interface summary (reference)

| Task | Unit                            | Key interface                                                                                                                                                                                                                                                                                                                                                                                                       |
| ---- | ------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 11   | `WriteLedger`                   | `interface IWriteLedger { void Record(string path, NoteFile.WriteOutcome o); bool IsOwnWrite(string path, long size, string hash); }` — matches on **size + hash only**; the timestamp is stored for diagnostics and never compared                                                                                                                                                                                 |
| 12   | `NoteWatcher`                   | `sealed class NoteWatcher : IDisposable` with `event Action<string> ExternalChange`, `event Action<string> Deleted`, `event Action<string, string> Renamed`; 150ms debounce; consults `IWriteLedger`; recreates itself on `FileSystemWatcher.Error`                                                                                                                                                                 |
| 13   | `NoteIndex` + `NoteIndexStore`  | `sealed record NoteState(int X, int Y, int W, int H, string? Monitor, NoteColor Color, double Opacity, bool AlwaysOnTop, bool IsOpen, DateTime LastOpenedUtc)`; `NoteIndex.Load/Save`; corrupt file → `notes.json.corrupt-N`; case-insensitive path keys; unknown `version` treated as corrupt                                                                                                                      |
| 14   | `AppSettings` + `SettingsStore` | Defaults per Global Constraints; **no `launchAtStartup` member** — a test asserts the serialized JSON does not contain that key                                                                                                                                                                                                                                                                                     |
| 15   | `RecoveryStore`                 | `sealed record RecoveryEnvelope(string OriginalPath, string Content, DateTime CreatedUtc, string LastKnownDiskHash)`; file name `<sha256-of-path>.json`; `Save`, `TryLoad`, `Clear`, `LoadAll`                                                                                                                                                                                                                      |
| 16   | `MarkdownRenderer`              | `sealed record RenderResult(string Html, string Token)`; Markdig pipeline per Global Constraints; custom `HtmlObjectRenderer<TaskList>` emitting `data-span-start` / `data-span-end`; image `src` rewriting to `https://note.local/...`; remote images stripped unless allowed. **A test asserts the emitted span actually indexes the `[ ]` in the source** — this is the assumption the checkbox feature rests on |
| 17   | `TaskListToggler`               | `static ToggleResult Toggle(string markdown, int spanStart, int spanEnd)`; validates the span reads `[ ]`/`[x]`/`[X]` before mutating; rejects a span pointing anywhere else                                                                                                                                                                                                                                        |
| 18   | `NoteRepository`                | `EnumerateRoot`, `CreateNew` (`<yyyy-MM-dd>-untitled.md`, numeric suffix on collision), `Rename`; takes an injected clock so the date-based filename is testable                                                                                                                                                                                                                                                    |

**Deletion is deliberately absent from `NoteRepository`.** Recycle Bin deletion is platform behavior and lives in Plan B's `IFileDeletionService`, keeping Core Win32-free.

---

## Self-Review

**Spec coverage of Plan A's scope.** Walked each Core-relevant spec section against a task: §4 architecture → Task 2 (project layout, `net10.0` boundary); §5 locations/index/settings → Tasks 13-14; §5 geometry → Task 5; §5 file-format preservation → Tasks 9-10; §5 self-write suppression → Tasks 11-12; §5 new-note filenames → Task 18; §6 render pipeline → Task 16; §6 resource policy image rewriting → Task 16; §6 checkbox write-back → Tasks 16-17; §6 editor conveniences → Tasks 6-8; §7 theming → Task 4; §8.1 recovery envelopes → Task 15; §8.2 watcher move-out limitation → Task 12; §8 `FileSystemWatcher.Error` → Task 12; §9 testing → every task; §10 prerequisites and Spike 0 → Task 1.

Deliberately **not** in Plan A, and correctly so: everything requiring WPF or Win32 (chrome, transparency interop, WebView2 hosting, tray, hotkeys, startup registry, single-instance pipe, monitor enumeration, Recycle Bin) → Plans B and C. `HtmlDocumentBuilder` is Core but pairs tightly with the WebView2 shell, so it is written in Plan B alongside its consumer rather than stranded here without one; Task 4's `ToCssVariables` is the interface it will consume.

**Placeholder scan.** No "TBD", no "add appropriate error handling", no "similar to Task N". All 18 tasks carry real, complete code in every step, and the interface summary table at the end of the task list is a reference index, not a substitute for any task.

An earlier draft of this plan left Tasks 11-18 as an interface table only. That was a placeholder failure and has been corrected — every task is now written out in full step-by-step form.

**Type consistency.** Checked names across tasks: `EditResult` (Tasks 6-8) is one type used by all five edit ops; `NoteFormat`/`NoteContent` (Task 9) feed `AtomicWrite` (Task 10) and `WriteOutcome` (Task 10) feeds `IWriteLedger.Record` (Task 11) with matching parameters; `PixelRect`/`MonitorInfo` (Task 5) are what Plan B's enumerator must produce; `NoteColor` (Task 4) is the type `NoteState.Color` uses (Task 13); `NoteFile.Sha256` (Task 9) is the one hash entry point used by Tasks 10, 11, 15, and 16. `ClampSelection`, `ParseListPrefix`, `LineStart`, and `LineEnd` are `internal` in Task 6-7 and consumed by Task 8 within the same assembly — the test project needs no access to them, so no `InternalsVisibleTo` is required.

**Type consistency, Tasks 11-18** (checked after they were written out): `NoteFile.WriteOutcome` is referenced with its nesting intact in `IWriteLedger.Record`; `JsonFile.Write` depends on `NoteFile.AtomicWriteBytes`, which Task 13 Step 1 extracts before first use; `NoteState.Color` is Task 4's `NoteColor`; `RecoveryStore` uses `WriteLedger.Normalize` and `NoteFile.Sha256`, both in `StickyMD.Core.Notes`; Task 17's end-to-end test constructs `RenderOptions(AllowRemoteImages: false)` matching Task 16's signature; `IClock` is consumed only by `NoteRepository`.

Two deliberate naming decisions that look like inconsistencies but are not:

- **`ThemePreference` (Task 14) vs `ThemeMode` (Task 4)** are separate types. `ThemePreference` is the user's choice and includes `System`; `ThemeMode` is the resolved light-or-dark the palette needs. Plan B resolves one into the other by reading the OS setting. Merging them would force `NotePalette` to answer a question whose input it cannot see.
- **`MarkdownRenderer` must fully qualify `Markdig.Markdown.Parse`** because the namespace `StickyMD.Core.Markdown` collides with the type `Markdig.Markdown`. The code already does this; do not "simplify" it.

**Two gaps found and fixed during review:**

1. Task 8's contract for non-list lines was unspecified in the source spec. Rather than leave the implementer guessing, the task now states the behavior in a table and tests it.
2. `RecoveryStore` exposes a `Directory` property that shadows `System.IO.Directory`, so the implementation qualifies the static calls. This is a mild wart kept for a clearer public API; it compiles and is tested.

