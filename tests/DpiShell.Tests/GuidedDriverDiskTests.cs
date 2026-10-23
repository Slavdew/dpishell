using System.Text.RegularExpressions;
using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.Tests;

/// <summary>
/// Карточки «Драйвер перехвата не загрузился» (577, 1275, 1260 — защита Windows) и «Мало места на диске» (112, 39):
/// распознавание, тексты на двух языках, кнопки открывают нужный адрес через подменённое открытие.
/// </summary>
[Collection(LangCollection.Name)]
public sealed class GuidedDriverDiskTests
{
    static readonly List<string> Opened = [];

    static MainViewModel Window(TempDir t, HelperLink link)
    {
        var vm = TestWindow.Model(new Shell(new AppPaths(t.Sub("root"), Repo.Data)), link);
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsLoading))!.SetValue(vm, false);
        vm.GuidedEnabled = true;
        vm.AskHelper = _ => true;
        vm.NetDiagnose = _ => Task.FromResult(new NetVerdict(NetCause.Ok));
        vm.SystemFindings = () => [];
        Opened.Clear();
        vm.LinkOpener = Opened.Add;
        return vm;
    }

    [Theory]
    [InlineData("windivert: error opening filter: 577", false)]            // нет слова «code»: разбор по «error» — отдельный путь Explain
    [InlineData("Windows blocked the WinDivert driver (code 1275): possibly Memory integrity", true)]
    [InlineData("Windows не приняла подпись драйвера WinDivert (код 577): возможно, «Целостность памяти»", true)]
    [InlineData("Windows заблокировала драйвер WinDivert политикой (код 1260)", true)]
    [InlineData("Драйвер WinDivert не загрузился (код 5)", false)]
    [InlineData("Драйвер WinDivert не загрузился (код 15770)", false)]
    [InlineData("Без драйвера: код 577", false)]
    [InlineData("", false)]
    public void Driver_block_is_recognized_by_code_in_either_language(string error, bool blocked) =>
        Assert.Equal(blocked, WinwsFailure.IsDriverBlocked(error));

    [Theory]
    [InlineData("windivert: error 577", "577")]
    [InlineData("windivert: error 1275", "1275")]
    [InlineData("windivert: error 1260", "1260")]
    public void Driver_codes_from_winws_output_give_a_recognizable_message(string output, string code)
    {
        var text = WinwsFailure.Explain(output)!;
        Assert.Contains(code, text);
        Assert.True(WinwsFailure.IsDriverBlocked(text), text);
    }

    [Theory]
    [InlineData(unchecked((int)0x80070070), true)]
    [InlineData(unchecked((int)0x80070027), true)]
    [InlineData(unchecked((int)0x80070020), false)]
    public void Disk_full_is_recognized_by_hresult(int hresult, bool full) =>
        Assert.Equal(full, DiskSpace.IsDiskFull(new IOException("x", hresult)));

    [Fact]
    public void Disk_full_is_not_a_non_io_exception() =>
        Assert.False(DiskSpace.IsDiskFull(new InvalidOperationException("x", new IOException())));

    [Theory]
    [InlineData(@"C:\ProgramData\DpiShell\core", "C:")]
    [InlineData(@"D:\x", "D:")]
    public void Disk_letter_comes_from_the_path(string path, string disk) => Assert.Equal(disk, DiskSpace.DiskOf(path));

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public void Disk_messages_are_parsed_back_in_both_languages(string lang)
    {
        var was = L.Current;
        L.Set(lang);
        try
        {
            Assert.True(DiskSpace.TryParse(DiskSpace.Message("D:"), out var disk, out var need));
            Assert.Equal("D:", disk);
            Assert.Null(need);
            var upd = L.T("core.upd.no_space", new { disk = "C:", need = 600, free = 120 });
            Assert.True(DiskSpace.TryParse(upd, out disk, out need));
            Assert.Equal("C:", disk);
            Assert.Equal(600, need);
            Assert.False(DiskSpace.TryParse("Нет доступа в интернет", out _, out _));
            Assert.False(DiskSpace.TryParse(null, out _, out _));
        }
        finally { L.Set(was); }
    }

    [Fact]
    public async Task Driver_failure_shows_a_card_with_settings_button_and_info()
    {
        using var t = new TempDir();
        await using var f = new HelperFixture();
        f.Backend.EnableThrows = new InvalidOperationException(L.T("core.winws.driver.signature"));
        f.Backend.StatusValue = FakeHelperBackend.DefaultStatus with { Enabled = false, Active = false };
        var vm = Window(t, await f.LinkAsync());
        await vm.RefreshStatusAsync();
        await vm.EnableAsync();
        Assert.Equal(GuidedKind.Driver, vm.Guided!.Kind);
        Assert.Equal("Windows не дала запустить драйвер перехвата — без него оптимизация не работает.", vm.GuidedText);
        Assert.Equal("Открыть настройку", vm.GuidedActionText);
        Assert.True(vm.GuidedHasAction);
        Assert.True(vm.GuidedIsDriver);
        Assert.False(vm.GuidedIsUnknown);
        await vm.GuidedActionAsync();
        Assert.Equal(["windowsdefender://coreisolation"], Opened);
        Assert.NotNull(vm.Guided);
    }

    [Fact]
    public async Task Disk_full_failure_shows_a_card_with_need_and_storage_button()
    {
        using var t = new TempDir();
        await using var f = new HelperFixture();
        f.Backend.EnableThrows = new InvalidOperationException(L.T("core.upd.no_space", new { disk = "C:", need = 600, free = 90 }));
        f.Backend.StatusValue = FakeHelperBackend.DefaultStatus with { Enabled = false, Active = false };
        var vm = Window(t, await f.LinkAsync());
        await vm.RefreshStatusAsync();
        await vm.EnableAsync();
        Assert.Equal(GuidedKind.DiskFull, vm.Guided!.Kind);
        Assert.Equal("Мало места на диске C: — нужно ≈600 МБ.", vm.GuidedText);
        Assert.Equal("Открыть «Память»", vm.GuidedActionText);
        Assert.False(vm.GuidedIsDriver);
        await vm.GuidedActionAsync();
        Assert.Equal(["ms-settings:storagesense"], Opened);
    }

    [Fact]
    public async Task Disk_full_without_known_size_has_no_number()
    {
        using var t = new TempDir();
        await using var f = new HelperFixture();
        f.Backend.EnableThrows = new InvalidOperationException(DiskSpace.Message("E:"));
        f.Backend.StatusValue = FakeHelperBackend.DefaultStatus with { Enabled = false, Active = false };
        var vm = Window(t, await f.LinkAsync());
        await vm.RefreshStatusAsync();
        await vm.EnableAsync();
        Assert.Equal("Мало места на диске E:.", vm.GuidedText);
    }

    [Fact]
    public void Card_and_info_texts_exist_in_both_languages_and_warn_honestly()
    {
        var was = L.Current;
        try
        {
            foreach (var lang in new[] { "ru", "en" })
            {
                L.Set(lang);
                foreach (var key in new[] { "home.guided.driver", "home.guided.driver_open", "home.guided.disk", "home.guided.disk_need", "home.guided.disk_open", "core.disk.full", "core.winws.driver.policy", InfoTexts.DriverBlock })
                    Assert.True(L.Has(key), $"{lang}: нет {key}");
                var info = InfoTexts.Get(InfoTexts.DriverBlock);
                if (lang == "en")
                {
                    foreach (var s in new[] { L.T("home.guided.driver"), L.T("home.guided.driver_open"), L.T("home.guided.disk_need", new { disk = "C:", need = 5 }), L.T("home.guided.disk_open"), info, L.T("core.disk.full", new { disk = "C:" }) })
                        Assert.DoesNotMatch(new Regex("[а-яА-ЯёЁ]"), s);
                    Assert.Contains("own risk", info);
                    Assert.Contains("restart", info);
                    Assert.Contains("may be missing", info);
                    Assert.Contains("same place", info);
                }
                else
                {
                    Assert.Contains("на свой страх и риск", info);
                    Assert.Contains("перезагрузить", info);
                    Assert.Contains("может не быть", info);
                    Assert.Contains("там же", info);
                    Assert.Contains("Целостность памяти", info);
                    Assert.Contains("Список блокировки уязвимых драйверов", info);
                }
            }
        }
        finally { L.Set(was); }
    }
}
