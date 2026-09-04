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
