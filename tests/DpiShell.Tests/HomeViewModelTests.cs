using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.Tests;

/// <summary>
/// Главная и «Свои адреса» в модели окна (UX-аудит 2): итог «Проверить сейчас» — всегда про текущее состояние обхода,
/// несохранённые свои адреса не теряются молча. Помощник — фейк по каналу, проверка сайтов — подмена без сети.
/// </summary>
public sealed class HomeViewModelTests : IDisposable
{
    readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    Shell NewShell() => new(new AppPaths(_tmp.Sub("root"), Repo.Data));

    /// <summary>Модель после загрузки (заставка снята — состояние перечитывается).</summary>
    MainViewModel Model(HelperLink link)
    {
        var vm = TestWindow.Model(NewShell(), link);
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsLoading))!.SetValue(vm, false);
        return vm;
    }

    static HelperStatus Status(bool active) => FakeHelperBackend.DefaultStatus with { Enabled = active, Active = active };

    /// <summary>Проверка без сети: YouTube открывается всегда, остальное — только когда обход «включён».</summary>
    static Func<IReadOnlyList<TargetGroup>, Task<IReadOnlyList<TargetVerdict>>> Probe(Func<bool> on, List<string>? log = null) => groups =>
    {
        log?.AddRange(groups.SelectMany(g => g.Targets.Select(t => t.Url)));
        return Task.FromResult<IReadOnlyList<TargetVerdict>>(groups.SelectMany(g => g.Targets.Select(t =>
            new TargetVerdict(g.Id, t, g.Id == "youtube" || on(), 0.3, []))).ToList());
    };

    [Fact]
    public async Task CheckResult_DroppedOnToggle_AndRunsItselfAfterEnable()
    {
        await using var f = new HelperFixture();
        f.Backend.StatusValue = Status(active: false);
        var vm = Model(await f.LinkAsync());
        bool on = false;
        vm.ProbeGroups = Probe(() => on);
        await vm.RefreshStatusAsync();
        foreach (var g in vm.Groups) g.IsChecked = g.Id is "youtube" or "discord";
        Assert.False(vm.IsRunning);

        // Выключен: Discord не открывается. Счёт — в группах, как отметки рядом (п. 5), а не в адресах.
        await vm.CheckNowAsync();
        Assert.True(vm.HasCheck);
        Assert.Equal("Открывается 1 из 2", vm.CheckSummary);
        Assert.Contains(vm.CheckGroups, g => g.Title == "Discord" && !g.Ok);

        // «Включить»: прежний «✗ Discord» убран, проверка пошла сама — итог про включённый обход.
        on = true;
        f.Backend.StatusValue = Status(active: true);
        await vm.EnableAsync();
        Assert.Contains("enable alt9", f.Backend.Calls);
        Assert.True(vm.IsRunning);
        Assert.False(vm.Checking);
        Assert.Equal("Открывается 2 из 2", vm.CheckSummary);
        Assert.All(vm.CheckGroups, g => Assert.True(g.Ok));

        // «Выключить»: итог про включённый обход больше не верен — скрыт.
        on = false;
        f.Backend.StatusValue = Status(active: false);
        await vm.DisableAsync();
        Assert.False(vm.IsRunning);
        Assert.False(vm.HasCheck);
        Assert.Equal("", vm.CheckSummary);
        Assert.Empty(vm.CheckGroups);
    }

    [Fact]
    public async Task CheckResult_StateChangedWhileChecking_IsNotShown()
    {
        await using var f = new HelperFixture();
        f.Backend.StatusValue = Status(active: true);
        var vm = Model(await f.LinkAsync());
        var release = new TaskCompletionSource();
        vm.ProbeGroups = async groups =>
        {
            await release.Task;
            return groups.SelectMany(g => g.Targets.Select(t => new TargetVerdict(g.Id, t, true, 0.3, []))).ToList();
        };
        await vm.RefreshStatusAsync();
        var check = vm.CheckNowAsync();
        Assert.True(vm.Checking);
        Assert.True(vm.HasCheck);
        Assert.Equal("Проверяю…", vm.CheckSummary);

        // Пока проверяли, обход выключился (из трея, сбой) — итог про включённый не показываем.
        f.Backend.StatusValue = Status(active: false);
        await vm.RefreshStatusAsync();
        release.SetResult();
        await check;
        Assert.False(vm.HasCheck);
        Assert.Empty(vm.CheckRows);
    }
}
