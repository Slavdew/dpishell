using DpiShell.App;
using DpiShell.Core;

namespace DpiShell.Tests;

public sealed class StrategyOrderTests
{
    [Fact]
    public void ByName_NaturalNumbers_PlainGeneralFirst()
    {
        var titles = new[] { "general (ALT10)", "general (ALT2)", "general (ALT11)", "general", "general (ALT3)", "general (FAKE TLS AUTO ALT2)" };
        var sorted = StrategyOrder.SortByName(titles, t => t);
        Assert.Equal(["general", "general (ALT2)", "general (ALT3)", "general (ALT10)", "general (ALT11)", "general (FAKE TLS AUTO ALT2)"], sorted);
    }

    [Fact]
    public void ByOpened_Descending_NoRunLast_StableOnTies()
    {
        var items = new (string Id, int? Opened)[] { ("a", 3), ("b", null), ("c", 9), ("d", 3) };
        Assert.Equal(["c", "a", "d", "b"], StrategyOrder.SortByOpened(items, x => x.Opened).Select(x => x.Id));
    }

    [Fact]
    public void Opened_CountsOkNonControlTargetsOfLastRun()
    {
        var last = new StrategyStats.LastRun("f", [
            new("youtube", "https://a", true, true, false, 0.1, []),
            new("youtube", "https://b", false, true, false, 0.1, []),
            new("control", "https://c", true, false, true, 0.1, []),
            new("user", "https://d", true, false, false, 0.1, [])]);
        Assert.Equal(2, StrategyOrder.Opened(new StrategyStats.Stat(1, 1, 0, Last: last)));
        Assert.Null(StrategyOrder.Opened(new StrategyStats.Stat(1, 1, 0)));
        Assert.Null(StrategyOrder.Opened(null));
    }

    [Fact]
    public void Setting_PersistsInWindowJson_AndRaisesEvent()
    {
        using var tmp = new TempDir();
        var file = Path.Combine(tmp.Sub("w"), "window.json");
        var s = WindowSettings.Load(file);
        Assert.False(s.SortByName);
        int raised = 0;
        s.StrategySortChanged += () => raised++;
        s.SetStrategySort(true);
        s.SetStrategySort(true);
        Assert.Equal(1, raised);
        Assert.True(WindowSettings.Load(file).SortByName);
    }
}
