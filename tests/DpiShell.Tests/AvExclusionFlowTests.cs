using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.Tests;

/// <summary>
/// Порядок первого запуска (ТЗ av-exclusion, п. 10): установка помощника (UAC) → вопрос об исключении (добавляет уже помощник, av_exclusion_add) →
/// только потом скачивание ядра (core_repair). Помощник после установки ядро сам не качает (CoreWait). Отказ — качать сразу. Диалоги подменены.
/// </summary>
public class AvExclusionFlowTests
{
    internal static Task Probe(MainViewModel vm) =>
        (Task)typeof(MainViewModel).GetMethod("ProbeLocalAvAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(vm, [])!;

    /// <summary>Связь, которая до установки отвечает «не установлен», а после — подключается к помощнику фикстуры (ядра у него нет).</summary>
    static async Task<(HelperLink Link, OkSetup Setup)> FirstRunLink(HelperFixture f)
    {
        f.Backend.StatusValue = FakeHelperBackend.DefaultStatus with { Core = CoreState.Missing };
        f.Backend.Release.TrySetResult();   // core_repair не висит
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

    static MainViewModel Window(TempDir t, HelperLink link)
    {
        var vm = TestWindow.Model(new Shell(new AppPaths(t.Sub("root"), Repo.Data)), link);
        vm.Defender = FakeDefender.Active();
        return vm;
    }

    static int Index(FakeHelperBackend b, string call) => b.Calls.ToList().IndexOf(call);

    [Theory]
    [InlineData(0, true)]     // «Добавить и продолжить»
    [InlineData(1, false)]    // «Продолжить без исключения» — качать сразу
    [InlineData(-1, false)]   // вопрос закрыт — то же, что отказ: ядро нужно для действия, ради которого ставили помощника
    public async Task Install_then_question_then_core(int choice, bool added)
    {
        using var t = new TempDir();
        await using var f = new HelperFixture();
        f.Backend.Defender = FakeDefender.Active();
        var (link, setup) = await FirstRunLink(f);
        var vm = Window(t, link);
        await Probe(vm);
        var order = new List<string>();
        vm.AskHelper = q => { order.Add("ask_install"); Assert.Equal(L.T("shell.helper.ask_install"), q); return true; };
        vm.Choose = (text, options) =>
        {
            // Вопрос — уже после установки: помощник стоит, ядро ещё не качалось.
            Assert.Single(setup.Calls);
            Assert.DoesNotContain("core_repair", f.Backend.Calls);
            order.Add("ask_av");
            Assert.Equal(L.T("home.av.ask"), text);
            Assert.Equal([L.T("home.av.ask_add"), L.T("home.av.ask_skip")], options);
            return choice;
        };

        Assert.NotNull(await vm.NeedHelperAsync());
        Assert.Equal(["ask_install", "ask_av"], order);
        Assert.Equal("--install-helper", Assert.Single(setup.Calls).Split(' ')[0]);
        Assert.DoesNotContain("--av-exclusion", setup.Calls[0]);
        Assert.True(Index(f.Backend, "core_repair") >= 0);
        if (added)
        {
            Assert.True(Index(f.Backend, "av_exclusion_add") is >= 0 and var i && i < Index(f.Backend, "core_repair"));
            Assert.True(vm.AvOk);
        }
        else Assert.DoesNotContain("av_exclusion_add", f.Backend.Calls);
        // Само окно в Защитник не пишет никогда.
        Assert.Empty(((FakeDefender)vm.Defender).Calls);
        await vm.Helper.DisposeAsync();
    }

    /// <summary>Защитник не главный (выключен, сторонний антивирус) или ядро уже есть — вопроса нет; ядро качается, только если его нет.</summary>
    [Fact]
    public async Task No_question_when_defender_off_or_core_present()
    {
        using var t = new TempDir();
        await using var f = new HelperFixture();
        f.Backend.Defender = FakeDefender.ThirdParty("Kaspersky");
        var (link, _) = await FirstRunLink(f);
        var vm = Window(t, link);
        vm.AskHelper = _ => true;
        vm.Choose = (_, _) => throw new InvalidOperationException("Вопрос об исключении при стороннем антивирусе");
        Assert.NotNull(await vm.NeedHelperAsync());
        Assert.Contains("core_repair", f.Backend.Calls);
        Assert.DoesNotContain("av_exclusion_add", f.Backend.Calls);
        await vm.Helper.DisposeAsync();

        using var t2 = new TempDir();
        await using var f2 = new HelperFixture();
        f2.Backend.Defender = FakeDefender.Active();
        var (link2, _) = await FirstRunLink(f2);
        f2.Backend.StatusValue = FakeHelperBackend.DefaultStatus;   // ядро уже стоит (переустановка помощника)
        var vm2 = Window(t2, link2);
        vm2.AskHelper = _ => true;
        vm2.Choose = (_, _) => throw new InvalidOperationException("Вопрос при целом ядре");
        Assert.NotNull(await vm2.NeedHelperAsync());
        Assert.DoesNotContain("core_repair", f2.Backend.Calls);
        await vm2.Helper.DisposeAsync();
    }

    [Fact]
    public async Task Cancel_installs_nothing()
    {
        using var t = new TempDir();
        await using var f = new HelperFixture();
        var (link, setup) = await FirstRunLink(f);
        var vm = Window(t, link);
        vm.AskHelper = _ => false;
        Assert.Null(await vm.NeedHelperAsync());
        Assert.Empty(setup.Calls);
        await vm.Helper.DisposeAsync();
    }

    /// <summary>Защитник не дал (папка не появилась) — на карточке ручной путь; ядро всё равно качается.</summary>
    [Fact]
    public async Task Refused_shows_manual_path_and_core_still_downloads()
    {
        using var t = new TempDir();
        await using var f = new HelperFixture();
        f.Backend.Defender = new FakeDefender { SilentDrop = true };
        var (link, _) = await FirstRunLink(f);
        var vm = Window(t, link);
        vm.AskHelper = _ => true;
        vm.Choose = (_, _) => 0;
        Assert.NotNull(await vm.NeedHelperAsync());
        Assert.Contains("av_exclusion_add", f.Backend.Calls);
        Assert.Contains("core_repair", f.Backend.Calls);
        Assert.True(vm.AvManual);
        Assert.Equal(L.T("home.av.refused"), vm.AvManualText);
        Assert.True(vm.AvShowDefender);
        Assert.Equal(FakeHelperBackend.AvRoot, vm.AvPath);
        await vm.Helper.DisposeAsync();
    }

    /// <summary>Кнопка карточки до установки помощника: «Нужен помощник» → установка → исключение командой помощника → ядро. Без второго вопроса.</summary>
    [Fact]
    public async Task Card_button_without_helper_installs_then_adds_then_downloads()
    {
        using var t = new TempDir();
        await using var f = new HelperFixture();
        f.Backend.Defender = FakeDefender.Active();
        var (link, setup) = await FirstRunLink(f);
        var vm = Window(t, link);
        await Probe(vm);
        Assert.True(vm.AvOffer);
        var asked = new List<string>();
        vm.AskHelper = q => { asked.Add(q); return true; };
        vm.Choose = (_, _) => throw new InvalidOperationException("Второй вопрос после нажатия «Добавить в исключения»");
        await (Task)typeof(MainViewModel).GetMethod("AvAddAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(vm, [])!;
        Assert.Equal([L.T("shell.helper.ask_install")], asked);
        Assert.DoesNotContain("--av-exclusion", Assert.Single(setup.Calls));
        Assert.True(Index(f.Backend, "av_exclusion_add") is >= 0 and var i && i < Index(f.Backend, "core_repair"));
        Assert.True(vm.AvOk);
        Assert.False(vm.AvOffer);
        Assert.Empty(((FakeDefender)vm.Defender).Calls);
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
