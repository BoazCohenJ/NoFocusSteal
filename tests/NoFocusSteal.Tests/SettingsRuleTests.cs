using Xunit;

namespace NoFocusSteal.Tests;

public class SettingsRuleTests
{
    [Fact]
    public void WindowRuleBeatsAppRuleAndOnlyCoversThatClass()
    {
        var s = new Settings();
        s.SetRule("explorer.exe", AppRule.Block);
        s.SetRule(Settings.WindowKey("explorer.exe", "Foo"), AppRule.Allow);
        Assert.Equal(AppRule.Allow, s.RuleFor("explorer.exe", "Foo"));
        Assert.Equal(AppRule.Block, s.RuleFor("explorer.exe", "Bar"));
        Assert.Equal(AppRule.Default, s.RuleFor("other.exe", "Foo"));
    }
}

public class SystemWindowTests
{
    [Theory]
    [InlineData("explorer.exe", "DV2ControlHost", true)]
    [InlineData("explorer.exe", "Shell_TrayWnd", true)]
    [InlineData("other.exe", "DV2ControlHost", false)]
    [InlineData("explorer.exe", "CabinetWClass", false)]
    public void ClassicStartMenuIsSystemUI(string exe, string cls, bool expected) =>
        Assert.Equal(expected, new WindowInfo { ExeName = exe, ClassName = cls }.IsSystemUI);
}
