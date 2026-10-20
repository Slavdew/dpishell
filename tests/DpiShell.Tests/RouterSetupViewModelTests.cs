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

    public const string InstallOk = "# Ключ витрины DpiShell\n# Витрина пакетов\n# Пакеты DpiShell и зависимости\n# Ядро zapret (zapret) — загрузка и проверка хеша\n# Проверка\n"
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
        Assert.Null(Settings.RouterPassword);
        Assert.True(await i.CheckAsync("secret"));
        // Пароль подошёл по SSH — сохранён сразу (вход после установки может не успеть: rpcd перезапускается), дальше не спросят.
        Assert.Equal("secret", RouterSecrets.Unprotect(Settings.RouterPassword));
        Assert.True(vm.HasSavedPassword);
        Assert.True(i.VerdictOk);
        Assert.Equal(L.T("router.install.verdict_ok", new { version = "0.2.0" }), i.VerdictTitle);
        Assert.False(i.HasTunnelNote);
        Assert.True(i.CanInstall);
        Assert.Empty(ssh.Uploads);    // проверка ничего не кладёт

        r.RpcdRestartCalls = 3;   // после установки rpcd перезапускается: вход проходит, а объекта dpishell ещё нет
        Assert.True(await i.InstallAsync("secret"));
        Assert.Equal(0, r.RpcdRestartCalls);
        Assert.StartsWith("Подключено", vm.ConnectionText);   // дождались, ошибку не показали
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

        // «Подобрать на роутере» — сразу полный подбор по группам по умолчанию, ход — на экране «Подбор».
        int page = -1;
        vm.Navigate = p => page = p;
        vm.SelectOptions = () => (["user"], false);   // выбор экрана не мешает: после установки — по умолчанию
        await i.SelectNowAsync();
        Assert.Equal(Pages.Select, page);
        Assert.True(vm.SelectOnRouter);
        System.Text.Json.Nodes.JsonNode start;
        lock (r.Calls) start = r.Calls.Last(c => c["params"]![2]!.GetValue<string>() == "select_start")["params"]![3]!;
        Assert.Equal(["youtube", "discord"], start["groups"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("full", start["mode"]!.GetValue<string>());
    }

    [Fact]
    public async Task Install_WithoutCheck_ChecksItselfThenInstalls()
    {
        using var r = new FakeRouter().As020();
        r.Remove();
        var vm = Make(r, new FakeSshConnector(Ssh(r)));
        var i = vm.NewInstall();
        Assert.True(i.CanTryInstall);
        Assert.Equal("Установить", i.InstallText);
        Assert.False(i.CanInstall);
        Assert.True(await i.InstallAsync("secret"));   // «Проверить роутер» не нажимали
        Assert.Equal(RouterInstallStage.Done, i.Stage);
    }

    [Fact]
    public async Task Install_StepsFollowScriptLines()
    {
        // Шаги — коды установщика (DpiShell.Core), а не сравнение русского текста окна.
        Assert.Equal(RouterInstallStepCode.Feed, RouterInstallSteps.FromScriptLine("Ключ витрины DpiShell"));
        Assert.Equal(RouterInstallStepCode.Feed, RouterInstallSteps.FromScriptLine("Витрина пакетов"));
        Assert.Equal(RouterInstallStepCode.Packages, RouterInstallSteps.FromScriptLine("Пакеты DpiShell и зависимости"));
        Assert.Equal(RouterInstallStepCode.Core, RouterInstallSteps.FromScriptLine("Ядро zapret (zapret) — загрузка и проверка хеша"));
        Assert.Equal(RouterInstallStepCode.Start, RouterInstallSteps.FromScriptLine("Проверка"));
        Assert.Null(RouterInstallSteps.FromScriptLine("CONNECT github.com:443"));
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
        Assert.Contains("«Система» → «Программное обеспечение»", i.VerdictText);
        Assert.Equal("Проверка источника пакетов", i.Steps[1].Title);
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
        int resets = 0;
        vm.SetSelectGroups = g => { Assert.Equal(["youtube", "discord"], g); resets++; };
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
        Assert.Equal(1, resets);   // выбор групп подбора — снова по умолчанию

        // Поставить заново: поле пароля пустое — сохранённый.
        var i = vm.NewInstall();
        Assert.True(await i.CheckAsync(""));
        Assert.True(await i.InstallAsync(""));
        Assert.True(vm.Connected);
        Assert.True(vm.Found);
        Assert.NotEqual("DpiShell с роутера удалён", vm.EmptyTitle);
        Assert.Contains("Подключено", vm.ConnectionText);
        Assert.Equal(2, resets);
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
        Assert.Contains("DpiShell на роутере нужна программа 99.0.0 или новее", vm.CompatText);
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

    // ---------- 3. обновление ----------

    const string Rel = RouterUpdatePackages.DownloadBase + "router-v0.2.1/";

    /// <summary>Выпуск 0.2.1 на «GitHub»: release.json с подписью и два пакета (+ посторонний файл, который не берётся).</summary>
    internal static MapHandler ServeRelease(TestSigner signer, string version = "0.2.1", Func<byte[], byte[]>? corrupt = null)
    {
        var h = new MapHandler();
        var assets = new List<string>();
        foreach (var name in new[] { "dpishell-0.2.1-r1.apk", "luci-app-dpishell-0.2.1-r1.apk", "notes.txt" })
        {
            var body = Encoding.UTF8.GetBytes("пакет " + name);
            assets.Add($$"""{"name":"{{name}}","sha256":"{{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(body))}}"}""");
            h.Ok(Rel + name, corrupt?.Invoke(body) ?? body);
        }
        var json = Encoding.UTF8.GetBytes($$"""{"schema":1,"version":"{{version}}","tag":"router-v{{version}}","api":"1.0","min_app":"1.0.0","pkg":"apk","changes":["Глубокий поиск"],"assets":[{{string.Join(",", assets)}}]}""");
        return h.Ok(Rel + "release.json", json).Ok(Rel + "release.json.sig", signer.Sign(json));
    }

    [Fact]
    public async Task Update_Available_LineNotesHome_ThenFeedUpdate()
    {
        using var r = new FakeRouter().As020();
        var vm = Make(r, new FakeSshConnector(new FakeSsh()));
        Assert.True(await vm.ConnectAsync("secret"));
        Assert.True(vm.UpdateAvailable);
        Assert.Equal("Доступно обновление 0.2.1-r1", vm.UpdateTitle);
        Assert.Equal("• Глубокий поиск стратегий на самом роутере", vm.UpdateNotes);
        Assert.Contains("доступно обновление 0.2.1-r1", vm.HomeDetail);

        r.RpcdRestartAfterUpdate = 3;   // rpcd перезапускается после обновления — окно ждёт dpishell, а не падает
        Assert.True(await vm.UpdateRouterAsync());
        Assert.Equal(0, r.RpcdRestartCalls);
        Assert.Contains(r.Calls, c => Method(c) == "update_install" && c["params"]![3]!["from"]!.GetValue<string>() == "feed");
        Assert.False(vm.UpdateAvailable);
        Assert.Equal("Подключено, DpiShell 0.2.1-r1", vm.ConnectionText);
        Assert.Contains("Обновлено до 0.2.1-r1", _said);
        Assert.DoesNotContain("обновление", vm.HomeDetail);
        Assert.False(vm.Busy);
    }

    [Fact]
    public async Task SavedPassword_ReusedForSshAndLogin_ForgetClears_WrongErases()
    {
        using var r = new FakeRouter().As020();
        var ssh = new FakeSsh();
        var conn = new FakeSshConnector(ssh);
        var vm = Make(r, conn);
        Assert.True(await vm.ConnectAsync("secret"));
        Assert.Equal(L.T("router.password.saved"), vm.SavedPasswordText);
        Assert.True(vm.ForgetPasswordCmd.CanExecute(null));
        vm.ForgetPasswordCmd.Execute(null);
        Assert.Null(Settings.RouterPassword);
        Assert.False(vm.HasSavedPassword);
        Assert.Contains(L.T("router.password.forgotten"), _said);

        // Сохранённый перестал подходить (пароль сменили) — стирается при входе им, а не держится.
        Assert.True(await vm.ConnectAsync("secret"));
        r.Password = "new";
        var again = Make(r, conn);   // новый запуск окна: тот же window.json
        Assert.True(again.HasSavedPassword);
        Assert.False(await again.ConnectAsync(null));
        Assert.Null(Settings.RouterPassword);
    }

    [Fact]
    public void WindowSettings_FieldOfOtherShape_KeepsPasswordAndRest()
    {
        var pw = RouterSecrets.Protect("secret");
        var s = WindowSettings.Parse($$"""{ "theme": "dark", "router_password": "{{pw}}", "router_address": "192.168.1.1", "dns_custom": "не список", "autostart": 5 }""");
        Assert.Equal(("dark", "192.168.1.1"), (s.Theme, s.RouterAddress));
        Assert.Equal("secret", RouterSecrets.Unprotect(s.RouterPassword));
        Assert.Empty(s.DnsCustom);
        Assert.Null(s.Autostart);
        Assert.Equal("system", WindowSettings.Parse("{битый").Theme);
    }

    [Fact]
    public async Task Update_Steps_ElapsedAndLog()
    {
        using var r = new FakeRouter().As020();
        r.UpdateStepsOnRouter = ["download", "install", "restart"];
        var vm = Make(r, new FakeSshConnector(new FakeSsh()));
        Assert.True(await vm.ConnectAsync("secret"));
        var current = new List<string>();
        var titles = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.UpdateSteps) && vm.UpdateSteps.FirstOrDefault(s => s.Current) is { } c && (current.Count == 0 || current[^1] != c.Title))
                current.Add(c.Title);
            if (e.PropertyName == nameof(vm.UpdateTitle)) titles.Add(vm.UpdateTitle);
        };
        Assert.True(await vm.UpdateRouterAsync());
        // Шаги по step: текущий выделен, пройденные — с ✓.
        Assert.Equal([L.T("router.update.step.download"), L.T("router.update.step.install"), L.T("router.update.step.restart"), "✓ " + L.T("router.update.step.done")], current);
        Assert.Contains(titles, t => t.StartsWith(L.T("router.update.title_running", new { time = "0:0" })));   // прошедшее время в заголовке
        Assert.Equal("# download\n# install\n# restart", vm.UpdateLog);   // «Подробнее» — хвост журнала
        Assert.Empty(vm.UpdateSteps);   // закончилось — шагов не видно
        Assert.Contains("Обновлено до 0.2.1-r1", _said);
        Assert.Equal("Подключено, DpiShell 0.2.1-r1", vm.ConnectionText);
    }

    [Fact]
    public void UpdateJob_LogAsArrayOrString_StepOptional()
    {
        var o = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower };
        var a = System.Text.Json.JsonSerializer.Deserialize<RouterUpdateJob>("""{ "action": "update", "running": true, "step": "install", "log": ["a", "b"] }""", o)!;
        Assert.Equal("install", a.Step);
        Assert.Equal(["a", "b"], a.LogLines);
        var b = System.Text.Json.JsonSerializer.Deserialize<RouterUpdateJob>("""{ "action": "update", "running": true, "log": "a\nb" }""", o)!;
        Assert.Null(b.Step);
        Assert.Equal(["a", "b"], b.LogLines);
        Assert.Empty(System.Text.Json.JsonSerializer.Deserialize<RouterUpdateJob>("""{ "action": "update", "running": true }""", o)!.LogLines);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Update_RunningTooLong_DecidedByVersion(bool newVersion)
    {
        using var r = new FakeRouter().As020();
        r.UpdateHangs = true;
        r.UpdateHangNewVersion = newVersion;
        var vm = Make(r, new FakeSshConnector(new FakeSsh()));
        vm.UpdateStuckAfter = TimeSpan.FromMilliseconds(150);
        Assert.True(await vm.ConnectAsync("secret"));
        Assert.Equal(newVersion, await vm.UpdateRouterAsync());
        Assert.False(vm.Busy || vm.Updating);
        // Через ПК поверх висящего apk не качаем.
        Assert.DoesNotContain(r.Calls, c => Method(c) == "update_install" && c["params"]![3]!["from"]!.GetValue<string>() == "local");
        if (newVersion)
        {
            Assert.Contains(_said, s => s == L.T("router.update.done_no_summary", new { version = "0.2.1-r1" }));
            Assert.Equal("Подключено, DpiShell 0.2.1-r1", vm.ConnectionText);
            Assert.False(vm.UpdateAvailable);
        }
        else
        {
            Assert.Contains(_said, s => s == L.T("router.update.same_version_v", new { version = "0.2.0-r3" }));
            Assert.Equal(L.T("router.update.title_failed_cur", new { version = "0.2.1-r1", current = "0.2.0-r3" }), vm.UpdateTitle);
            Assert.Equal(L.T("common.retry"), vm.UpdateButtonText);
            Assert.True(vm.UpdateAvailable && vm.UpdateCmd.CanExecute(null));
        }
    }

    [Fact]
    public async Task Update_FeedUnreachable_PackagesThroughPc_SignedAndHashed_FromLocal()
    {
        using var r = new FakeRouter().As020();
        r.FeedUnreachable = true;
        r.LocalUpdateReady = true;
        var signer = new TestSigner();
        var ssh = new FakeSsh();
        var vm = Make(r, new FakeSshConnector(ssh));
        vm.Http = new HttpClient(ServeRelease(signer));
        vm.ReleaseKey = signer.PublicKey;
        Assert.True(await vm.ConnectAsync("secret"));

        Assert.True(await vm.UpdateRouterAsync());
        Assert.Equal(["/tmp/dpishell-update/dpishell-0.2.1-r1.apk", "/tmp/dpishell-update/luci-app-dpishell-0.2.1-r1.apk", "/tmp/dpishell-update/SHA256SUMS"], ssh.Uploads);
        var froms = r.Calls.Where(c => Method(c) == "update_install").Select(c => c["params"]![3]!["from"]!.GetValue<string>()).ToList();
        Assert.Equal(["feed", "local"], froms);
        Assert.Contains(_said, s => s.StartsWith("Роутер не достал витрину пакетов"));
        Assert.False(vm.UpdateAvailable);
    }

    [Theory]
    [InlineData("sign", "Подпись выпуска для роутера не сошлась")]
    [InlineData("hash", "не совпал по sha256")]
    [InlineData("version", "Выпуск 0.2.2 вместо 0.2.1")]
    public async Task Update_Local_BadReleaseRejected_NothingUploaded(string bad, string text)
    {
        using var r = new FakeRouter().As020();
        r.FeedUnreachable = true;
        r.LocalUpdateReady = true;
        var signer = new TestSigner();
        var ssh = new FakeSsh();
        var vm = Make(r, new FakeSshConnector(ssh));
        vm.Http = new HttpClient(ServeRelease(bad == "sign" ? new TestSigner() : signer, bad == "version" ? "0.2.2" : "0.2.1",
            bad == "hash" ? b => [.. b, 1] : null));
        vm.ReleaseKey = signer.PublicKey;
        await vm.ConnectAsync("secret");
        Assert.False(await vm.UpdateRouterAsync());
        Assert.Contains(_said, s => s.StartsWith("Обновить через этот компьютер не удалось") && s.Contains(text));
        Assert.Empty(ssh.Uploads);
        Assert.DoesNotContain(r.Calls, c => Method(c) == "update_install" && c["params"]![3]!["from"]!.GetValue<string>() == "local");
    }

    [Fact]
    public async Task UpdateCheck_Manual_NoUpdate()
    {
        using var r = new FakeRouter().As020();
        r.UpdateAvailable = false;
        var vm = Make(r, new FakeSshConnector(new FakeSsh()));
        await vm.ConnectAsync("secret");
        Assert.False(vm.UpdateAvailable);
        Assert.True(vm.UpdateCheckCmd.CanExecute(null));
        await vm.CheckUpdatesAsync();
        Assert.Equal(1, r.UpdateCheckCalls);
        Assert.Contains(L.T("router.update.none"), _said);
    }

    // ---------- 5. ключ роутера изменился ----------

    /// <summary>Как SshNetConnector: первый ключ запоминается в хранилище окна, другой — HostKeyChanged.</summary>
    sealed class KeyedConnector(IRouterHostKeyStore store, string fingerprint, FakeSsh ssh) : ISshConnector
    {
        public int Attempts { get; private set; }
        public Task<ISshSession> ConnectAsync(RouterSshTarget target, Func<string> password, CancellationToken ct = default)
        {
            Attempts++;
            var stored = store.Get(target.HostKeyId);
            if (HostKeyCheck.Decide(stored, fingerprint) == HostKeyVerdict.Changed)
                throw new RouterSshException(RouterSshFailure.HostKeyChanged, HostKeyCheck.ChangedText);
            if (stored is null) store.Set(target.HostKeyId, fingerprint);
            return Task.FromResult<ISshSession>(ssh);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HostKeyChanged_AskTrustNew_ElseCancel(bool trust)
    {
        using var r = new FakeRouter().As020();
        r.Remove();
        var vm = Make(r, null!);
        var id = new RouterSshTarget(r.Address).HostKeyId;
        Settings.HostKeys.Set(id, "ssh-ed25519 SHA256:old");   // ключ от прежней прошивки
        var conn = new KeyedConnector(Settings.HostKeys, "ssh-ed25519 SHA256:new", Ssh(r));
        vm.SshConnector = conn;
        var asked = new List<string>();
        vm.TrustNewKey = t => { asked.Add(t); return trust; };

        var i = vm.NewInstall();
        Assert.Equal(trust, await i.CheckAsync("secret"));
        Assert.Equal([RouterViewModel.HostKeyText], asked);
        if (trust)
        {
            Assert.Equal("ssh-ed25519 SHA256:new", Settings.HostKeys.Get(id));
            Assert.Equal(2, conn.Attempts);
            Assert.True(i.VerdictOk);
            // Дальше — без вопросов: новый ключ запомнен.
            Assert.True(await i.InstallAsync("secret"));
            Assert.Single(asked);
        }
        else
        {
            Assert.Equal("ssh-ed25519 SHA256:old", Settings.HostKeys.Get(id));
            Assert.Equal(1, conn.Attempts);
            Assert.StartsWith("Ключ роутера изменился — подключение отменено", i.ErrorText);
        }
        // Хранилище — рядом с window.json.
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(Settings.File)!, "router-hostkeys.json")));
    }

    sealed class FailingConnector(Exception e) : ISshConnector
    {
        public Task<ISshSession> ConnectAsync(RouterSshTarget target, Func<string> password, CancellationToken ct = default) => throw e;
    }
}
