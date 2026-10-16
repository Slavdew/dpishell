using DpiShell.App;
using DpiShell.Core;

namespace DpiShell.Tests;

/// <summary>Автозапуск окна при входе (02-spec §3.8, решение 22): ключ Run — через подменяемое хранилище, настоящий реестр не трогаем.</summary>
public sealed class AutostartTests
{
    static (Autostart, MemoryRunKeyStore, WindowSettings) Create(TempDir t)
    {
        var settings = WindowSettings.Load(t.Sub("window.json"));
        var store = new MemoryRunKeyStore();
        return (new Autostart(settings) { Store = store, ExePath = @"D:\Apps\DpiShell\DpiShell.exe" }, store, settings);
    }

    [Fact]
    public void WithoutService_OnByDefault_WritesTrayCommand()
    {
        using var t = new TempDir();
        var (a, store, _) = Create(t);
        a.OnRunMode(service: false);
        Assert.True(a.Enabled);
        Assert.Equal("\"D:\\Apps\\DpiShell\\DpiShell.exe\" --tray", store.Values[Autostart.ValueName]);
    }

    [Fact]
    public void ServiceMode_OffByDefault_RemovesKey()
    {
        using var t = new TempDir();
        var (a, store, _) = Create(t);
        store.Values[Autostart.ValueName] = "\"C:\\old\\DpiShell.exe\" --tray";
        a.OnRunMode(service: true);
        Assert.False(a.Enabled);
        Assert.False(store.Values.ContainsKey(Autostart.ValueName));
    }

    [Fact]
    public void ModeUnknown_NoChoice_KeyUntouched()
    {
        using var t = new TempDir();
        var (a, store, _) = Create(t);
        store.Values[Autostart.ValueName] = "something";
        a.Apply();
        Assert.Equal("something", store.Values[Autostart.ValueName]);
    }

    [Fact]
    public void ExplicitChoice_WinsOverMode_AndIsSaved()
    {
        using var t = new TempDir();
        var (a, store, settings) = Create(t);
        a.OnRunMode(service: true);
        a.Enabled = true;
        Assert.True(store.Values.ContainsKey(Autostart.ValueName));
        Assert.True(WindowSettings.Load(settings.File).Autostart);

        a.OnRunMode(service: false);
        a.Enabled = false;
        Assert.False(store.Values.ContainsKey(Autostart.ValueName));
        Assert.False(WindowSettings.Load(settings.File).Autostart);
        // Смена режима выбор не отменяет.
        a.OnRunMode(service: true);
        a.OnRunMode(service: false);
        Assert.False(store.Values.ContainsKey(Autostart.ValueName));
    }

    [Fact]
    public void MovedFolder_PathRewritten()
    {
        using var t = new TempDir();
        var (a, store, _) = Create(t);
        store.Values[Autostart.ValueName] = "\"C:\\Old\\DpiShell.exe\" --tray";
        a.OnRunMode(service: false);
        Assert.Equal(a.Command, store.Values[Autostart.ValueName]);
    }

    [Fact]
    public void WindowModel_DefaultsToMemoryStore()
    {
        using var t = new TempDir();
        var vm = TestWindow.Model(new Shell(DpiShell.Tests.Make.Paths(t)));
        Assert.IsType<MemoryRunKeyStore>(vm.Autostart.Store);
        Assert.Null(vm.Updates.Http);
    }

}
