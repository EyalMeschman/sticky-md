using Shouldly;
using StickyMD.Core.Tests.TestSupport;
using Xunit.Sdk;

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
    public async Task Wait_Until_returns_when_condition_becomes_true()
    {
        var flag = false;
        var t = Task.Run(async () => { await Task.Delay(50); flag = true; });

        Wait.Until(() => flag, "flag to be set");

        await t;
        flag.ShouldBeTrue();
    }

    [Fact]
    public void Wait_StaysFalse_passes_when_nothing_happens()
    {
        Wait.StaysFalse(() => false, "nothing to happen", forMs: 100);
    }

    [Fact]
    public void Wait_StaysFalse_throws_when_the_condition_becomes_true()
        => Should.Throw<XunitException>(
            () => Wait.StaysFalse(() => true, "an event that must not fire", forMs: 50));
}
