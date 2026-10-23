using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.Tests;

/// <summary>
/// Первое «Включить» (ТЗ av-exclusion, п. 2): ядро качается сразу после установки помощника, поэтому при работающем Защитнике вопрос об исключении —
/// в том же диалоге, что и установка, и исключение ставит сама установка (ключ --av-exclusion) до запуска службы. Диалоги подменены.
/// </summary>
public class AvExclusionFlowTests
{
    internal static Task Probe(MainViewModel vm) =>
        (Task)typeof(MainViewModel).GetMethod("ProbeLocalAvAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(vm, [])!;

    /// <summary>Связь, которая до установки отвечает «не установлен», а после — подключается к помощнику фикстуры.</summary>
    static async Task<(HelperLink Link, OkSetup Setup)> FirstRunLink(HelperFixture f)
    {
        var setup = new OkSetup();
        var link = new HelperLink
        {
            Connector = ct => setup.Calls.Count == 0 ? throw new HelperUnavailableException(HelperUnavailableReason.NotInstalled) : HelperClient.ConnectAsync(f.ClientOptions, ct),
            Setup = setup, WindowVersion = "0.0", ConnectWait = TimeSpan.FromSeconds(5),
        };
        await link.ConnectAsync();
        Assert.Equal(HelperState.NotInstalled, link.State);
        return (link, setup);
    }

    [Theory]
    [InlineData(0, true)]    // «Добавить и продолжить»
    [InlineData(1, false)]   // «Продолжить без исключения»
    public async Task First_install_asks_in_same_dialog_and_passes_flag(int choice, bool flag)
    {
        using var t = new TempDir();
        await using var f = new HelperFixture();
        // Установка с ключом «добавила» исключение — помощник его видит; без ключа — нет.
        f.Backend.Defender = flag ? FakeDefender.Active(FakeHelperBackend.AvRoot) : FakeDefender.Active();
        var (link, setup) = await FirstRunLink(f);
        var vm = TestWindow.Model(new Shell(new AppPaths(t.Sub("root"), Repo.Data)), link);
        vm.Defender = FakeDefender.Active();
        await Probe(vm);
        Assert.True(vm.ShowAvCard);
        Assert.True(vm.AvOffer);
        string? asked = null;
        vm.AskHelper = _ => throw new InvalidOperationException("Отдельный вопрос об установке при работающем Защитнике");
        vm.Choose = (text, options) => { asked = text; Assert.Equal([L.T("home.av.ask_add"), L.T("home.av.ask_skip")], options); return choice; };

        Assert.NotNull(await vm.NeedHelperAsync());
        Assert.Contains(L.T("home.av.ask"), asked);
        Assert.Contains(L.T("shell.helper.ask_install"), asked);
        var args = Assert.Single(setup.Calls);
        Assert.StartsWith("--install-helper ", args);
        Assert.Equal(flag, args.Split(' ').Contains(HelperInstaller.AvExclusionArg));
        if (flag) Assert.True(vm.AvOk);   // «✓» после установки
        // Само окно в Защитник не пишет никогда.
        Assert.DoesNotContain("av_exclusion_add", f.Backend.Calls);
        await vm.Helper.DisposeAsync();
    }

    [Fact]
    public async Task Cancel_installs_nothing_and_defender_off_asks_as_before()
    {
        using var t = new TempDir();
        await using var f = new HelperFixture();
        var (link, setup) = await FirstRunLink(f);
        var vm = TestWindow.Model(new Shell(new AppPaths(t.Sub("root"), Repo.Data)), link);
        vm.Defender = FakeDefender.Active();
        await Probe(vm);
        vm.Choose = (_, _) => -1;
        Assert.Null(await vm.NeedHelperAsync());
        Assert.Empty(setup.Calls);

        // Защитник выключен — карточки нет, вопрос прежний, ключа нет.
        vm.Defender = FakeDefender.Off();
        await Probe(vm);
        Assert.False(vm.ShowAvCard);
        bool asked = false;
        vm.AskHelper = _ => asked = true;
        vm.Choose = (_, _) => throw new InvalidOperationException("Вопрос об исключении при выключенном Защитнике");
        Assert.NotNull(await vm.NeedHelperAsync());
        Assert.True(asked);
        Assert.DoesNotContain(HelperInstaller.AvExclusionArg, Assert.Single(setup.Calls));
        await vm.Helper.DisposeAsync();
    }

    /// <summary>Установка с ключом, а Защитник не дал (папки нет) — на карточке ручной путь.</summary>
    [Fact]
    public async Task Install_with_flag_but_refused_shows_manual_path()
    {
        using var t = new TempDir();
        await using var f = new HelperFixture();
        f.Backend.Defender = FakeDefender.Active();
        var (link, _) = await FirstRunLink(f);
        var vm = TestWindow.Model(new Shell(new AppPaths(t.Sub("root"), Repo.Data)), link);
        vm.Defender = FakeDefender.Active();
        await Probe(vm);
        vm.Choose = (_, _) => 0;
        Assert.NotNull(await vm.NeedHelperAsync());
        Assert.True(vm.AvManual);
        Assert.Equal(L.T("home.av.refused"), vm.AvManualText);
        Assert.True(vm.AvShowDefender);
        Assert.Equal(FakeHelperBackend.AvRoot, vm.AvPath);
        await vm.Helper.DisposeAsync();
    }

    /// <summary>Кнопка карточки до установки помощника: «Нужен помощник» → установка с ключом (исключение ставит она); окно в Защитник само не пишет.</summary>
    [Fact]
    public async Task Card_button_without_helper_installs_with_flag()
    {
        using var t = new TempDir();
        await using var f = new HelperFixture();
        f.Backend.Defender = FakeDefender.Active(FakeHelperBackend.AvRoot);
        var (link, setup) = await FirstRunLink(f);
        var vm = TestWindow.Model(new Shell(new AppPaths(t.Sub("root"), Repo.Data)), link);
        vm.Defender = FakeDefender.Active();
        await Probe(vm);
        var asked = new List<string>();
        vm.AskHelper = q => { asked.Add(q); return true; };
        await (Task)typeof(MainViewModel).GetMethod("AvAddAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(vm, [])!;
        Assert.Equal([L.T("shell.helper.ask_install")], asked);
        Assert.Contains(HelperInstaller.AvExclusionArg, Assert.Single(setup.Calls).Split(' '));
        Assert.True(vm.AvOk);
        Assert.False(vm.AvOffer);
        Assert.DoesNotContain("av_exclusion_add", f.Backend.Calls);
        Assert.Empty(vm.Defender is FakeDefender d ? d.Calls : []);
        await vm.Helper.DisposeAsync();
    }

    /// <summary>Старый помощник (нет av_exclusion_* в commands): окно команды не шлёт, кнопки нет.</summary>
    [Fact]
    public void Old_helper_without_commands_is_not_asked()
    {
        var names = HelperCommands.Names.Where(n => !n.StartsWith("av_exclusion")).ToList();
        var compat = HelperCompat.Evaluate("1.15", names, null, windowApi: HelperProtocol.Api, windowVersion: "0.0");
        Assert.Equal(HelperCompatLevel.MinorDiffers, compat.Level);
        Assert.False(compat.Supports("av_exclusion_add"));
        Assert.False(compat.Supports("av_exclusion_status"));
        Assert.True(compat.Supports("core_repair"));
    }
}
