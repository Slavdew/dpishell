using DpiShell.Core;

namespace DpiShell.Tests;

public class AppStateTests
{
    [Fact]
    public void SaveLoad_RoundTrip()
    {
        using var t = new TempDir();
        var p = Make.Paths(t);
        var at = new DateTime(2026, 10, 16, 12, 30, 0, DateTimeKind.Utc);
        var s = new AppState
        {
            StrategyId = "alt",
            Enabled = true,
            UserTargets = ["a.com", "https://b.org/x"],
            Groups = ["discord", "user"],
            Networks = { ["Home WiFi"] = new("alt", at, 0.75) },
        };
        s.Save(p);

        Assert.True(File.Exists(p.StateFile));
        Assert.False(File.Exists(p.StateFile + ".tmp"));

        var l = AppState.Load(p);
        Assert.Equal("alt", l.StrategyId);
        Assert.True(l.Enabled);
        Assert.Equal(["a.com", "https://b.org/x"], l.UserTargets);
        Assert.Equal(["discord", "user"], l.Groups);
        Assert.Equal(new NetworkMemory("alt", at, 0.75), l.Networks["Home WiFi"]);

        // Повторное сохранение поверх существующего файла.
        l.Enabled = false;
        l.Save(p);
        Assert.False(AppState.Load(p).Enabled);
    }

    [Fact]
    public void Save_UsesSnakeCase()
    {
        using var t = new TempDir();
        var p = Make.Paths(t);
        new AppState { StrategyId = "x" }.Save(p);
        var json = File.ReadAllText(p.StateFile);
        Assert.Contains("\"strategy_id\"", json);
        Assert.Contains("\"user_targets\"", json);
    }

    [Fact]
    public void Load_MissingFile_Defaults()
    {
        using var t = new TempDir();
        var l = AppState.Load(Make.Paths(t));
        Assert.Null(l.StrategyId);
        Assert.False(l.Enabled);
        Assert.Equal(["youtube", "discord"], l.Groups);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"enabled\": \"yes\"}")]
    public void Load_Broken_ReturnsFreshObject(string content)
    {
        using var t = new TempDir();
        var p = Make.Paths(t);
        p.EnsureDirectories();
        File.WriteAllText(p.StateFile, content);
        var l = AppState.Load(p);
        Assert.NotNull(l);
        Assert.Null(l.StrategyId);
        Assert.False(l.Enabled);
        Assert.Equal(["youtube", "discord"], l.Groups);
    }

    [Fact]
    public void Load_PartialJson_KeepsDefaultsForMissingFields()
    {
        using var t = new TempDir();
        var p = Make.Paths(t);
        p.EnsureDirectories();
        File.WriteAllText(p.StateFile, "{\"strategy_id\": \"alt\"}");
        var l = AppState.Load(p);
        Assert.Equal("alt", l.StrategyId);
        Assert.Equal(["youtube", "discord"], l.Groups);
        Assert.NotNull(l.Networks);
    }
}
