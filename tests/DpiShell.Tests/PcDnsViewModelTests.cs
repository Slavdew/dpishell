using DpiShell.App;
using DpiShell.Core;

namespace DpiShell.Tests;

/// <summary>
/// «DNS этого компьютера» в модели окна (architecture.md «DNS этого компьютера»): варианты, переключение через помощника, свои варианты
/// в window.json, кнопка на плашке и повторная проверка. Помощник — фейк по каналу или настоящий над <see cref="FakeDnsSystem"/>.
/// </summary>
public sealed class PcDnsViewModelTests : IDisposable
{
    readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    Shell NewShell() => new(new AppPaths(_tmp.Sub("root"), Repo.Data));

    [Fact]
    public async Task Presets_And_SwitchThroughHelper_ThenRechecks()
    {
        await using var f = new HelperFixture();
        var link = await f.LinkAsync();
        var vm = TestWindow.Model(NewShell(), link);
        int rechecks = 0;
        vm.RecheckAfterDns = () => { rechecks++; return Task.CompletedTask; };
        Assert.Equal(["auto", "google", "cloudflare", "quad9", "yandex"], vm.PcDns.Options.Select(o => o.Id));
        Assert.True(vm.PcDns.ShowShield);

        await vm.RefreshPcDnsAsync();
        Assert.Equal("Сейчас: как у провайдера · Ethernet", vm.PcDns.CurrentText);
        Assert.Contains("Radmin VPN", vm.PcDns.Note);
        Assert.Equal("auto", vm.PcDns.Selected!.Id);
        Assert.False(vm.PcDns.CanRestore);

        vm.PcDns.Selected = vm.PcDns.Options.Single(o => o.Id == "quad9");
        await vm.ApplyPcDnsAsync(vm.PcDns.Selected);
        Assert.Contains("dns_set quad9   False", f.Backend.Calls);
        Assert.Equal(1, rechecks);
        Assert.StartsWith("Сейчас: Quad9, защищённый", vm.PcDns.CurrentText);
        Assert.True(vm.PcDns.CanRestore);
        Assert.Contains("Проверка DNS", vm.Message);
        // Смена без подтверждения, но с тостом «Вернуть» (UX-аудит, «Опасные действия»); он и возвращает.
        Assert.StartsWith("DNS изменён: Quad9", vm.Toast);
        Assert.Equal("Вернуть", vm.ToastAction);

        vm.ToastCmd.Execute(null);
        // «Вернуть как было» снимается раньше, чем пройдёт повторная проверка, — ждём обоих.
        for (int i = 0; i < 200 * 10 && (vm.PcDns.CanRestore || rechecks < 2); i++) await Task.Delay(10);
        Assert.False(vm.HasToast);
        Assert.Contains("dns_restore", f.Backend.Calls);
        Assert.False(vm.PcDns.CanRestore);
        Assert.Equal(2, rechecks);
    }

    [Fact]
    public async Task Failed_NoRecheck_ShowsWhy()
    {
        await using var f = new HelperFixture();
        f.Backend.DnsSetFails = true;
        var vm = TestWindow.Model(NewShell(), await f.LinkAsync());
        int rechecks = 0;
        vm.RecheckAfterDns = () => { rechecks++; return Task.CompletedTask; };
        await vm.ApplyPcDnsAsync(vm.PcDns.Options.Single(o => o.Id == "google"));
        Assert.Equal(0, rechecks);
        Assert.Contains("прежние настройки возвращены", vm.Message);
        Assert.False(vm.HasToast);   // не сменилось — и «Вернуть» не предлагаем
    }

    [Fact]
    public async Task PlateButton_TurnsOnRecommended()
    {
        await using var f = new HelperFixture();
        var vm = TestWindow.Model(NewShell(), await f.LinkAsync());
        vm.RecheckAfterDns = () => Task.CompletedTask;
        Assert.Equal("Включить защищённый DNS", vm.RecommendedDnsText);
        Assert.StartsWith("Google", vm.RecommendedDnsHint);
        vm.FixDnsCmd.Execute(null);
        for (int i = 0; i < 200 * 10 && !f.Backend.Calls.Any(c => c.StartsWith("dns_set")); i++) await Task.Delay(10);
        Assert.Contains("dns_set google   False", f.Backend.Calls);
    }

    [Fact]
    public async Task Custom_SavedInWindowJson_SentWithFallback_AndSharedWithRouter()
    {
        await using var f = new HelperFixture();
        var shell = NewShell();
        var settings = TestWindow.Settings(shell);
        var vm = TestWindow.Model(shell, await f.LinkAsync(), settings);
        vm.RecheckAfterDns = () => Task.CompletedTask;
        var pick = vm.PcDns;
        pick.NewName = "Дом"; pick.NewIps = "192.168.1.53"; pick.NewDoh = "https://dns.home.example/dns-query";
        pick.AddCustom();
        Assert.Equal("", pick.NewError);
        pick.Fallback = true;

        // В window.json — и вариант, и галочка; роутерный выбор видит тот же вариант.
        var again = WindowSettings.Load(settings.File);
        Assert.Equal(new DnsCustomEntry("Дом", ["192.168.1.53"], "https://dns.home.example/dns-query").Name, again.DnsCustom.Single().Name);
        Assert.Equal(["192.168.1.53"], again.DnsCustom.Single().Ips);
        Assert.True(again.DnsFallback);
        Assert.Contains(vm.Router.DnsPick.Options, o => o.Id == "my:Дом");

        await vm.ApplyPcDnsAsync(pick.Selected!);
        Assert.Contains("dns_set custom 192.168.1.53 https://dns.home.example/dns-query True", f.Backend.Calls);

        pick.RemoveCustom(pick.Options.Single(o => o.Id == "my:Дом"));
        Assert.Empty(WindowSettings.Load(settings.File).DnsCustom);
        Assert.DoesNotContain(vm.Router.DnsPick.Options, o => o.Id == "my:Дом");
    }

    [Fact]
    public async Task RealBackend_OverFakeSystem_RecognizesCustomAndWindows10()
    {
        var shell = NewShell();
        var sys = new FakeDnsSystem { DohSupported = false };
        var backend = PcHelper.Over(shell);
        backend = new DpiShell.Core.Helper.ShellHelperBackend(shell)
        {
            InspectService = backend.InspectService, IsActive = backend.IsActive, FindForeign = backend.FindForeign, DnsSystem = sys,
        };
        await using var f = new HelperFixture(backend: backend);
        var settings = TestWindow.Settings(shell);
        var vm = TestWindow.Model(shell, await f.LinkAsync(), settings);
        vm.RecheckAfterDns = () => Task.CompletedTask;
        await vm.RefreshPcDnsAsync();
        Assert.Contains("этой версии Windows", vm.PcDns.Note);
        Assert.False(vm.PcDns.ShowFallback);

        vm.PcDns.NewName = "Свой"; vm.PcDns.NewIps = "10.0.0.53, 10.0.0.54";
        vm.PcDns.AddCustom();
        await vm.ApplyPcDnsAsync(vm.PcDns.Selected!);
        Assert.Equal(["10.0.0.53", "10.0.0.54"], sys.Dns[FakeDnsSystem.Ethernet.Id].Ipv4);
        Assert.Equal("Сейчас: Свой, без шифрования · Ethernet, Wi-Fi", vm.PcDns.CurrentText);
        Assert.Equal("my:Свой", vm.PcDns.CurrentId);
        Assert.False(sys.Dns.ContainsKey(FakeDnsSystem.Radmin.Id));
    }

    [Fact]
    public async Task WithoutHelper_ReadsLocally()
    {
        var vm = TestWindow.Model(NewShell());
        var sys = new FakeDnsSystem();
        sys.Dns[FakeDnsSystem.Ethernet.Id] = new AdapterDns(false, ["8.8.8.8", "8.8.4.4"], true, [],
            new Dictionary<string, DohSetting> { ["8.8.8.8"] = new(0x11, null), ["8.8.4.4"] = new(0x11, null) });
        sys.Dns[FakeDnsSystem.Wifi.Id] = sys.Dns[FakeDnsSystem.Ethernet.Id];
        vm.LocalDns = sys;
        await vm.RefreshPcDnsAsync();
        Assert.Equal("Сейчас: Google, защищённый · Ethernet, Wi-Fi", vm.PcDns.CurrentText);
        Assert.Equal("google", vm.PcDns.CurrentId);
        Assert.Empty(sys.Log);   // только чтение
    }
}
