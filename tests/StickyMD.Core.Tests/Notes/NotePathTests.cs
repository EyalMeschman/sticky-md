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
