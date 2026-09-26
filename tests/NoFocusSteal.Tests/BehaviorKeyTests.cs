using Xunit;

namespace NoFocusSteal.Tests;

public class BehaviorKeyTests
{
    private static WindowInfo Window(string exe, string cls) => new WindowInfo { ExeName = exe, ClassName = cls };

    [Fact]
    public void ExplorerDesktopAndFolderWindowsAreSeparateOffenders()
    {
        // A wallpaper slideshow that hands focus to the desktop must not get folder windows blocked.
        Assert.NotEqual(FocusMonitor.BehaviorKey(Window("explorer.exe", "Progman")),
                        FocusMonitor.BehaviorKey(Window("explorer.exe", "CabinetWClass")));
    }

    [Fact]
    public void SameAppSameWindowTypeIsTheSameOffender()
    {
        Assert.Equal(FocusMonitor.BehaviorKey(Window("Teams.exe", "TeamsWebView")),
                     FocusMonitor.BehaviorKey(Window("Teams.exe", "TeamsWebView")));
    }
}
