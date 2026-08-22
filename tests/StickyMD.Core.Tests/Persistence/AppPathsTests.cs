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
