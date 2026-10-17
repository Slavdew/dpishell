using System.Text;
using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Router;

namespace DpiShell.Tests;

/// <summary>
/// Установка, удаление и обновление DpiShell на роутере из окна — на FakeRouter (API) и FakeSsh (SSH), без сети:
/// полный цикл «удалить → установить → подключиться», отказ «не подходит», туннель через ПК.
/// </summary>
public sealed class RouterSetupViewModelTests : IDisposable
{
    readonly TempDir _tmp = new();
    readonly List<string> _said = [];
    WindowSettings Settings = new();

    public void Dispose() => _tmp.Dispose();

    public const string InstallOk = "# Ключ витрины DpiShell\n# Витрина пакетов\n# Пакеты DpiShell и зависимости\n# Ядро обхода (zapret) — загрузка и проверка хеша\n# Проверка\n"
                                    + "{\"schema\":1,\"ok\":true,\"step\":\"done\",\"message\":\"DpiShell установлен. Дальше — «Подобрать»\",\"version\":\"0.2.0-r3\",\"api\":\"1.0\",\"core\":true}\n";
    const string AfterClean = """{"schema":1,"mode":"after-uninstall","left":[],"kept":{"settings":false,"https_dns_proxy":false}}""";

    public static string Vendor => Path.Combine(Path.GetDirectoryName(Repo.Data)!, "vendor", "zapret-v72.13.zip");

    public sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => throw new InvalidOperationException("сети быть не должно");
    }

    /// <summary>Один и тот же роутер для API (FakeRouter) и SSH (FakeSsh): установка по SSH «ставит» пакет в FakeRouter.</summary>
    public static FakeSsh Ssh(FakeRouter r, string preflight = RouterSshScenarioTests.PreflightOk, string install = InstallOk)
    {
        FakeSsh ssh = null!;
        ssh = new FakeSsh
        {
            OnScript = (cmd, script) =>
            {
                if (script.Contains("dpishell-preflight"))
                    return cmd.EndsWith("--after-uninstall") ? AfterClean : preflight;
                if (script.Contains("dpishell-install") || cmd.EndsWith("sh -s"))
                {
                    if (install.Contains("\"ok\":true")) r.Reinstall();
                    return install;
                }
                // uninstall.sh по SSH
                r.Remove();
                ssh.Files[RouterSsh.UninstallResultPath] = Encoding.UTF8.GetBytes(
                    $$"""{"running":false,"ok":true,"keep_settings":{{(cmd.EndsWith("--keep") ? "true" : "false")}},"message":"DpiShell удалён","at":1}""");
                return "";
            },
        };
        return ssh;
    }

    RouterViewModel Make(FakeRouter r, ISshConnector ssh)
    {
        var shell = new Shell(new AppPaths(_tmp.Sub("root"), Repo.Data));
        Settings = WindowSettings.Load(Path.Combine(_tmp.Sub("window"), "window.json"));
        var vm = new RouterViewModel(shell, Settings)
        {
            AddressAuto = false,
            CustomAddress = r.Address,
            PollInterval = TimeSpan.FromMilliseconds(10),
            CheckTimeout = TimeSpan.FromSeconds(5),
            PcTimestamps = () => true,
            Gateway = () => throw new InvalidOperationException("в тестах — только фейковый роутер"),
            SshConnector = ssh,
            Http = new HttpClient(new NoNetwork()),
            CoreCandidates = [Vendor],
        };
        vm.Say = s => { lock (_said) _said.Add(s); };
        return vm;
    }

    // ---------- 1. установка ----------

    [Fact]
    public async Task Install_FromScratch_CheckThenInstall_ConnectsWithSamePassword()
    {
        using var r = new FakeRouter().As020();
        r.Remove();
        var ssh = Ssh(r);
        var conn = new FakeSshConnector(ssh);
        var vm = Make(r, conn);
        await vm.ConnectAsync("secret");
        Assert.False(vm.Found);
        Assert.True(vm.CanInstall);

        var i = vm.NewInstall();
        Assert.False(i.CanInstall);   // сначала — «Проверить роутер»
        Assert.True(await i.CheckAsync("secret"));
        Assert.True(i.VerdictOk);
        Assert.Equal("Роутер подходит — можно ставить DpiShell 0.2.0.", i.VerdictTitle);
        Assert.False(i.HasTunnelNote);
        Assert.True(i.CanInstall);
        Assert.Empty(ssh.Uploads);    // проверка ничего не кладёт

        Assert.True(await i.InstallAsync("secret"));
        Assert.Equal(RouterInstallStage.Done, i.Stage);
        Assert.All(i.Steps, s => Assert.Equal(RouterInstallStep.Done, s.State));
        Assert.Equal("secret", conn.LastPassword);
        Assert.True(ssh.Files.ContainsKey(RouterSsh.CoreZip));
        // Сразу вошли тем же паролем и сохранили его, как при обычном входе; дальше — «Подобрать на роутере».
        Assert.True(vm.Connected);
        Assert.True(i.CanGoSelect);
        Assert.Equal("secret", RouterSecrets.Unprotect(Settings.RouterPassword));
        Assert.StartsWith("DpiShell 0.2.0-r3 установлен и подключён", i.ResultText);
        Assert.False(vm.Busy);
    }

    [Fact]
    public async Task Install_StepsFollowScriptLines()
    {
        Assert.Equal(0, RouterInstallViewModel.StepOf("Подключение к роутеру"));
        Assert.Equal(0, RouterInstallViewModel.StepOf("Ядро обхода: загрузка на роутер"));
        Assert.Equal(1, RouterInstallViewModel.StepOf("Ключ витрины DpiShell"));
        Assert.Equal(2, RouterInstallViewModel.StepOf("Пакеты DpiShell и зависимости"));
        Assert.Equal(3, RouterInstallViewModel.StepOf("Ядро обхода (zapret) — загрузка и проверка хеша"));
        Assert.Equal(4, RouterInstallViewModel.StepOf("Проверка"));
        Assert.Null(RouterInstallViewModel.StepOf("CONNECT github.com:443"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Install_Unsuitable_ReasonsInPlainWords_NothingInstalled()
    {
        using var r = new FakeRouter().As020();
        r.Remove();
        var pre = RouterSshScenarioTests.PreflightOk.Replace("\"foreign\":[]", "\"foreign\":[{\"kind\":\"package\",\"name\":\"zapret\"}]")
            .Replace("\"fw4\":true", "\"fw4\":false");
        var ssh = Ssh(r, pre);
        var vm = Make(r, new FakeSshConnector(ssh));
        var i = vm.NewInstall();
        Assert.False(await i.CheckAsync("secret"));
        Assert.False(i.VerdictOk);
        Assert.Equal("Роутер не подходит", i.VerdictTitle);
        Assert.Contains("Удалите пакет «zapret»", i.VerdictText);
        Assert.Contains("fw4", i.VerdictText);
        Assert.False(i.CanInstall);
        Assert.False(await i.InstallAsync("secret"));
        Assert.Empty(ssh.Uploads);
    }

    [Fact]
    public async Task Install_NoInternetOnRouter_TunnelNoteAndInstallsThroughPc()
    {
        using var r = new FakeRouter().As020();
        r.Remove();
        var offline = RouterSshScenarioTests.PreflightOk.Replace("\"net\":{\"feed\":true,\"fw_feed\":true,\"github\":true}", "\"net\":{\"feed\":false,\"fw_feed\":true,\"github\":false}");
        var ssh = Ssh(r, offline);
        var vm = Make(r, new FakeSshConnector(ssh));
        var i = vm.NewInstall();
        Assert.True(await i.CheckAsync("secret"));
        Assert.True(i.VerdictOk);
        Assert.Equal(RouterInstallViewModel.TunnelText, i.TunnelNote);
        Assert.DoesNotContain("через этот компьютер", i.VerdictText);   // пояснение — одной строкой, без повтора
        Assert.True(await i.InstallAsync("secret"));
        Assert.Single(ssh.Forwards);
        Assert.Equal(1, ssh.ForwardsClosed);
        Assert.Contains(ssh.Runs, x => x.Command.StartsWith("https_proxy=") && x.Command.EndsWith("sh -s"));
        Assert.True(vm.Connected);
    }

    [Fact]
    public async Task Install_PackagesFail_PlainTextAndDetails()
    {
        using var r = new FakeRouter().As020();
        r.Remove();
        var fail = "# Ключ витрины DpiShell\n# Витрина пакетов\n# Пакеты DpiShell и зависимости\n"
                   + "{\"schema\":1,\"ok\":false,\"step\":\"packages\",\"message\":\"ERROR: unable to select packages: dpishell (no such package)\",\"version\":null,\"api\":null,\"core\":null}\n";
        var vm = Make(r, new FakeSshConnector(Ssh(r, install: fail)));
        var i = vm.NewInstall();
        await i.CheckAsync("secret");
        Assert.False(await i.InstallAsync("secret"));
        Assert.Equal(RouterInstallStage.Failed, i.Stage);
        Assert.StartsWith("Не удалось поставить пакеты DpiShell", i.ErrorText);
        Assert.True(i.HasDetails);
        Assert.Contains("no such package", i.Details);
        Assert.Equal(RouterInstallStep.Done, i.Steps[1].State);
        Assert.Equal(RouterInstallStep.Failed, i.Steps[2].State);
        Assert.False(vm.Connected);
        Assert.True(i.CanCheck);   // можно повторить
    }

    [Fact]
    public async Task Install_NoPasswordAndNoneSaved_AsksForIt()
    {
        using var r = new FakeRouter().As020();
        r.Remove();
        var ssh = Ssh(r);
        var vm = Make(r, new FakeSshConnector(ssh));
        var i = vm.NewInstall();
        Assert.False(await i.CheckAsync(""));
        Assert.Equal("Введите пароль роутера или отметьте «Пароль не задан»", i.ErrorText);
        Assert.Empty(ssh.Runs);
        // «Пароль не задан» — пустой пароль (свежая OpenWrt).
        var conn = new FakeSshConnector(ssh);
        vm.SshConnector = conn;
        Assert.True(await i.CheckAsync(null, noPassword: true));
        Assert.Equal("", conn.LastPassword);
    }

    [Fact]
    public async Task Install_SshUnreachable_PlainText()
    {
        using var r = new FakeRouter().As020();
        r.Remove();
        var vm = Make(r, new FailingConnector(new RouterSshException(RouterSshFailure.Unreachable, "нет")));
        var i = vm.NewInstall();
        Assert.False(await i.CheckAsync("secret"));
        Assert.Contains("не отвечает по SSH", i.ErrorText);
        Assert.Contains("Администрирование", i.ErrorText);
    }

    // ---------- 2. удаление и полный цикл ----------

    /// <summary>Удаление через API: итог роутер пишет в /tmp/dpishell-uninstall.json, status.json пропадает.</summary>
    internal static void UninstallWritesResult(FakeRouter r, FakeSsh ssh) => r.OnUninstall = () =>
    {
        r.Remove();
        ssh.Files[RouterSsh.UninstallResultPath] = Encoding.UTF8.GetBytes(
            $$"""{"running":false,"ok":true,"keep_settings":{{(r.UninstallKeepSettings == true ? "true" : "false")}},"message":"DpiShell удалён","at":2}""");
    };

    /// <summary>Главный сценарий владельца: удалить из окна → поставить заново из окна → снова подключено.</summary>
    [Fact]
    public async Task FullCycle_UninstallViaApi_ThenInstallAgain_Connected()
    {
        using var r = new FakeRouter().As020();
        var ssh = Ssh(r);
        UninstallWritesResult(r, ssh);
        // Прошлое удаление оставило итог — он не должен приняться за новый.
        ssh.Files[RouterSsh.UninstallResultPath] = Encoding.UTF8.GetBytes("""{"running":false,"ok":false,"keep_settings":true,"message":"старое","at":1}""");
        var vm = Make(r, new FakeSshConnector(ssh));
        Assert.True(await vm.ConnectAsync("secret"));
        Assert.True(vm.UninstallCmd.CanExecute(null));

        var u = vm.NewUninstall();
        Assert.False(u.NeedsPassword);   // вошли — через API, пароль сохранённый
        Assert.True(u.KeepSettings);     // по умолчанию — сохранить настройки
        Assert.False(u.CanRemove);       // не в один клик: сначала «Да, удалить»
        Assert.False(await u.UninstallAsync());
        Assert.Null(r.UninstallKeepSettings);

        u.Full = true;
        u.Understood = true;
        Assert.True(await u.UninstallAsync());
        Assert.False(r.UninstallKeepSettings);
        Assert.True(u.Clean);
        Assert.Equal("DpiShell удалён. Ничего не осталось.", u.ResultTitle);
        var cmds = ssh.Runs.Select(x => x.Command).ToList();
        Assert.True(cmds.IndexOf("rm -f /tmp/dpishell-uninstall.json") < cmds.IndexOf("sh -s -- --after-uninstall"));
        // Вкладка — в пустом состоянии с «Установить на роутер».
        Assert.False(vm.Found);
        Assert.False(vm.Connected);
        Assert.Equal("DpiShell с роутера удалён", vm.EmptyTitle);
        Assert.Equal("Установить на роутер…", vm.InstallText);
        Assert.False(vm.Busy);

        // Поставить заново: поле пароля пустое — сохранённый.
        var i = vm.NewInstall();
        Assert.True(await i.CheckAsync(""));
        Assert.True(await i.InstallAsync(""));
        Assert.True(vm.Connected);
        Assert.True(vm.Found);
        Assert.NotEqual("DpiShell с роутера удалён", vm.EmptyTitle);
        Assert.Contains("Подключено", vm.ConnectionText);
    }

    [Fact]
    public async Task Uninstall_NotLoggedIn_BySsh_WithPassword()
    {
        using var r = new FakeRouter().As020();
        var ssh = Ssh(r);
        var conn = new FakeSshConnector(ssh);
        var vm = Make(r, conn);
        await vm.ConnectAsync("");   // найден, но без входа
        Assert.True(vm.Found);
        var u = vm.NewUninstall();
        Assert.True(u.NeedsPassword);
        u.Understood = true;
        Assert.False(await u.UninstallAsync(""));
        Assert.Equal("Введите пароль роутера или отметьте «Пароль не задан»", u.ErrorText);
        Assert.True(await u.UninstallAsync("secret"));
        Assert.Equal("secret", conn.LastPassword);
        Assert.Equal("sh -s -- --keep", ssh.Runs[0].Command);
        Assert.Null(r.UninstallKeepSettings);   // API не звали
        Assert.True(u.Clean);
        Assert.False(vm.Found);
    }

    [Fact]
    public async Task Uninstall_SomethingLeft_Listed()
    {
        using var r = new FakeRouter().As020();
        var ssh = new FakeSsh
        {
            OnScript = (cmd, _) => cmd.EndsWith("--after-uninstall")
                ? """{"schema":1,"mode":"after-uninstall","left":[{"what":"Папка /opt/zapret","detail":"занята процессом"}],"kept":{"settings":true,"https_dns_proxy":true}}"""
                : "",
        };
        UninstallWritesResult(r, ssh);
        var vm = Make(r, new FakeSshConnector(ssh));
        await vm.ConnectAsync("secret");
        var u = vm.NewUninstall();
        u.Understood = true;
        Assert.True(await u.UninstallAsync());
        Assert.True(r.UninstallKeepSettings);
        Assert.False(u.Clean);
        Assert.Equal("DpiShell удалён", u.ResultTitle);
        Assert.Contains("Осталось на роутере:\n• Папка /opt/zapret — занята процессом", u.LeftText);
        Assert.Contains("Настройки сохранены", u.LeftText);
        Assert.Contains("Пакет защищённого DNS оставлен", u.LeftText);
    }

    [Fact]
    public async Task Uninstall_ApiStarted_SshUnavailable_SaysCouldNotCheck()
    {
        using var r = new FakeRouter().As020();
        r.OnUninstall = r.Remove;
        var vm = Make(r, new FailingConnector(new RouterSshException(RouterSshFailure.Unreachable, "нет")));
        await vm.ConnectAsync("secret");
        var u = vm.NewUninstall();
        u.Understood = true;
        Assert.True(await u.UninstallAsync());
        Assert.True(r.UninstallKeepSettings);
        Assert.Equal("Удаление запущено", u.ResultTitle);
        Assert.Contains("не отвечает по SSH", u.LeftText);
        Assert.False(vm.Found);
    }

    // ---------- 4. совместимость ----------

    static string Method(System.Text.Json.Nodes.JsonObject call) => call["params"]![2]!.GetValue<string>();

    [Fact]
    public async Task Compat_Ok_NoPlate()
    {
        using var r = new FakeRouter().As020();
        var vm = Make(r, new FakeSshConnector(new FakeSsh()));
        Assert.True(await vm.ConnectAsync("secret"));
        Assert.Equal(RouterCompatLevel.Ok, vm.Compat!.Level);
        Assert.False(vm.HasCompat);
        Assert.False(vm.Blocked);
        Assert.Null(vm.MethodsTip);
        Assert.True(vm.CheckCmd.CanExecute(null));
    }

    [Fact]
    public async Task Compat_RouterNewerMinor_Warning_MissingMethodsInactive_RiskConfirmOncePerSession()
    {
        using var r = new FakeRouter().As020();
        r.StatusApi = "\"1.3\"";
        r.MethodsJson = r.MethodsJson!.Replace("\"check_now\",", "");
        var vm = Make(r, new FakeSshConnector(new FakeSsh()));
        var asked = new List<string>();
        var answer = false;
        vm.Confirm = t => { asked.Add(t); return answer; };
        Assert.True(await vm.ConnectAsync("secret"));
        Assert.Equal(RouterCompatLevel.Warning, vm.Compat!.Level);
        Assert.True(vm.HasCompat);
        Assert.True(vm.CompatNeedsApp);
        Assert.True(vm.RouterNewer);
        Assert.False(vm.CheckCmd.CanExecute(null));          // check_now нет в methods — неактивна, с подсказкой
        Assert.NotNull(vm.MethodsTip);
        Assert.True(vm.ToggleCmd.CanExecute(null));

        // Меняет настройки — «под вашу ответственность»; отказ — ничего не зовём.
        await vm.ToggleAsync();
        Assert.Equal([RouterViewModel.RiskText], asked);
        Assert.DoesNotContain(r.Calls, c => Method(c) is "enable" or "disable");
        answer = true;
        await vm.ToggleAsync();
        Assert.Contains(r.Calls, c => Method(c) == "disable");
        await vm.ToggleAsync();
        Assert.Equal(2, asked.Count);   // согласие — на весь сеанс
        Assert.Contains(r.Calls, c => Method(c) == "enable");
    }

    [Fact]
    public async Task Compat_MinAppNewer_Blocked_OnlyUpdateAndUninstall()
    {
        using var r = new FakeRouter().As020();
        r.MinApp = "99.0.0";
        var vm = Make(r, new FakeSshConnector(new FakeSsh()));
        Assert.True(await vm.ConnectAsync("secret"));
        Assert.True(vm.Blocked);
        Assert.Contains("Роутеру нужно приложение 99.0.0", vm.CompatText);
        Assert.True(vm.CompatNeedsApp);
        Assert.False(vm.CanAct);
        Assert.False(vm.GoSelectCmd.CanExecute(null));
        Assert.True(vm.UninstallCmd.CanExecute(null));
        Assert.True(vm.UpdateCmd.CanExecute(null));
        var opened = new List<string>();
        vm.OpenUrl = opened.Add;
        vm.OpenAppReleasesCmd.Execute(null);
        Assert.Equal([RouterViewModel.AppReleasesUrl], opened);
    }

    sealed class FailingConnector(Exception e) : ISshConnector
    {
        public Task<ISshSession> ConnectAsync(RouterSshTarget target, Func<string> password, CancellationToken ct = default) => throw e;
    }
}
