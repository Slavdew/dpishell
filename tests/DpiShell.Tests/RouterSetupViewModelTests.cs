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

    sealed class FailingConnector(Exception e) : ISshConnector
    {
        public Task<ISshSession> ConnectAsync(RouterSshTarget target, Func<string> password, CancellationToken ct = default) => throw e;
    }
}
