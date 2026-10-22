using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows.Input;
using DpiShell.Core;
using DpiShell.Core.Router;

namespace DpiShell.App;

// Установка, удаление и обновление DpiShell на роутере из окна (architecture.md «Установка, обновление, удаление»):
// по SSH — RouterInstallFlow/RouterSsh, через API — ядро договора §5.4. Окно без прав: роутер — по сети.
public sealed partial class RouterViewModel
{
    // ---------- SSH (в тестах подменяется) ----------

    ISshConnector? _ssh;
    /// <summary>Подключение по SSH; по умолчанию — SSH.NET с отпечатками ключей из папки настроек окна.</summary>
    public ISshConnector SshConnector { get => _ssh ??= new SshNetConnector(_settings.HostKeys); set => _ssh = value; }

    /// <summary>Подключение с вопросом «Ключ роутера изменился»: «Доверять новому» — забыть старый отпечаток и войти ещё раз.</summary>
    ISshConnector Ssh => new HostKeyAware(this);

    /// <summary>
    /// Ключ роутера изменился (переустановили прошивку?) — спросить. true — доверять новому. По умолчанию — диалог, «Отмена» в нём главная.
    /// </summary>
    public Func<string, bool> TrustNewKey { get; set; } = text =>
        System.Windows.Application.Current?.Dispatcher is { } d ? d.Invoke(() => HostKeyDialog.Ask(text)) : false;

    public static string HostKeyText => L.T("router.hostkey.text");

    // ---------- сертификат HTTPS роутера ----------

    /// <summary>Сертификат HTTPS роутера сменился — спросить. true — доверять новому. По умолчанию — тот же диалог, что для ключа SSH.</summary>
    public Func<string, bool> TrustNewCert { get; set; } = text =>
        System.Windows.Application.Current?.Dispatcher is { } d ? d.Invoke(() => HostKeyDialog.Ask(text, L.T("router.cert.title"))) : false;

    public static string CertText => L.T("router.cert.text");

    /// <summary>
    /// Новый сертификат HTTPS: совпал с сертификатом панели на роутере (по SSH, ключ SSH уже закреплён, пароль сохранён) — принять молча;
    /// иначе при <paramref name="interactive"/> — вопрос, без него — «сертификат изменился» в состоянии подключения. Проверку не отключаем.
    /// </summary>
    internal async Task<bool> AcceptCertificateAsync(RouterCertChange cert, bool interactive)
    {
        if (await CertMatchesBySshAsync(cert))
        {
            _settings.HostKeys.Set(cert.PinId, cert.Fingerprint);
            Say(L.T("router.cert.accepted_ssh"));
            return true;
        }
        if (!interactive) { SetConnection(() => L.T("router.cert.changed")); return false; }
        if (!TrustNewCert(CertText)) { SetConnection(() => L.T("router.cert.cancelled")); return false; }
        _settings.HostKeys.Set(cert.PinId, cert.Fingerprint);
        Say(L.T("router.cert.accepted"));
        return true;
    }

    /// <summary>
    /// Сверка по SSH: только если ключ SSH роутера уже закреплён (иначе пароль ушёл бы неизвестно кому) и пароль сохранён.
    /// Любой сбой — «не сверили», решает пользователь.
    /// </summary>
    async Task<bool> CertMatchesBySshAsync(RouterCertChange cert)
    {
        if (_client is null || RouterSecrets.Unprotect(_settings.RouterPassword) is not { } pw) return false;
        var target = new RouterSshTarget(_client.Address);
        if (_settings.HostKeys.Get(target.HostKeyId) is null) return false;
        try
        {
            await using var s = await SshConnector.ConnectAsync(target, () => pw);
            return await new RouterSsh(s).WebCertificateAsync() == cert.Fingerprint;
        }
        catch (Exception e) when (e is RouterSshException or IOException or InvalidOperationException or System.Net.Sockets.SocketException or TimeoutException) { return false; }
    }

    /// <summary>
    /// SSH с вопросом о сменившемся ключе. Пароль подошёл — он и есть пароль root (тот же, что для входа в окне и в LuCI):
    /// сохраняется, как после входа, — установка, проверка, удаление и обновление по SSH больше его не спросят.
    /// </summary>
    sealed class HostKeyAware(RouterViewModel vm) : ISshConnector
    {
        public async Task<ISshSession> ConnectAsync(RouterSshTarget target, Func<string> password, CancellationToken ct = default)
        {
            ISshSession session;
            try { session = await vm.SshConnector.ConnectAsync(target, password, ct); }
            catch (RouterSshException e) when (e.Failure == RouterSshFailure.HostKeyChanged)
            {
                if (!vm.TrustNewKey(HostKeyText))
                    throw new RouterSshException(RouterSshFailure.HostKeyChanged, L.T("router.hostkey.cancelled"), e);
                vm._settings.HostKeys.Remove(target.HostKeyId);
                vm.Say(L.T("router.hostkey.accepted"));
                session = await vm.SshConnector.ConnectAsync(target, password, ct);
            }
            vm.RememberPassword(password());
            return session;
        }
    }

    /// <summary>Пароль подошёл (вход или SSH) — сохранить зашифрованным, если он другой.</summary>
    internal void RememberPassword(string password)
    {
        if (RouterSecrets.Unprotect(_settings.RouterPassword) == password) return;
        _settings.RouterPassword = RouterSecrets.Protect(password);
        TrySave();
        RaisePassword();
    }

    /// <summary>«Забыть» сохранённый пароль: дальше вход, установка и удаление снова спросят его.</summary>
    public ICommand ForgetPasswordCmd => _forgetPasswordCmd ??= new RelayCommand(() =>
    {
        _settings.RouterPassword = null;
        TrySave();
        RaisePassword();
        Say(L.T("router.password.forgotten"));
    }, () => HasSavedPassword);
    ICommand? _forgetPasswordCmd;

    /// <summary>«Пароль сохранён на этом компьютере» (зашифрован, только для этого пользователя Windows).</summary>
    public string SavedPasswordText => RouterSecrets.Unprotect(_settings.RouterPassword) == ""
        ? L.T("settings.router.password_empty") : L.T("settings.router.password_saved");

    void RaisePassword()
    {
        Raise(nameof(HasSavedPassword)); Raise(nameof(SavedPasswordText));
        Requery();
    }
    static readonly Lazy<HttpClient> SharedHttp = new(() => new HttpClient { Timeout = TimeSpan.FromMinutes(5) });
    /// <summary>Загрузка ядра обхода и пакетов обновления на этот ПК.</summary>
    public HttpClient Http { get; set; } = SharedHttp.Value;
    /// <summary>Готовые копии архива ядра на ПК (проверяются по хешу).</summary>
    public IReadOnlyList<string> CoreCandidates { get; set; } = [];
    /// <summary>Выпуски для роутера (data/router-releases.json из поставки).</summary>
    public Func<RouterReleases>? Releases { get; set; }

    internal RouterInstallFlow Flow() =>
        new(Ssh, (Releases ?? (() => RouterReleases.Load(Path.Combine(_shell.Paths.Seed, "router-releases.json"))))(), new RouterCoreArchive(RouterCoreManifest.Embedded(), _settings.RouterCoreCache), Http) { LocalCoreCandidates = CoreCandidates };

    /// <summary>Адрес роутера для SSH — тот же, что для входа (шлюз или свой из «Дополнительно»).</summary>
    internal string? SetupAddress()
    {
        _settings.RouterAddress = AddressAuto ? null : CustomAddress.Trim();
        TrySave();
        return EffectiveAddress();
    }

    /// <summary>Показать в «Дополнительно» адрес шлюза (диалоги установки и удаления).</summary>
    public void RefreshGateway()
    {
        if (!AddressAuto) return;
        try { _gateway = Gateway(); }
        catch (Exception e) when (e is InvalidOperationException or System.Net.NetworkInformation.NetworkInformationException) { _gateway = null; }
        Raise(nameof(AutoAddressText));
    }

    /// <summary>Пароль для SSH: «Пароль не задан» — пустой; поле пустое — сохранённый; его нет — null.</summary>
    internal string? SshPassword(string? typed, bool noPassword) =>
        noPassword ? "" : !string.IsNullOrEmpty(typed) ? typed : RouterSecrets.Unprotect(_settings.RouterPassword);

    /// <summary>Сбой SSH → понятный текст.</summary>
    public static string SshText(Exception e, string? host = null) => e switch
    {
        RouterSshException { Failure: RouterSshFailure.Unreachable } =>
            host is null ? L.T("router.ssh.unreachable") : L.T("router.ssh.unreachable_host", new { address = host }),
        RouterSshException { Failure: RouterSshFailure.AuthFailed } => L.T("router.ssh.auth_failed"),
        RouterSshException { Failure: RouterSshFailure.HostKeyChanged } s => s.InnerException is RouterSshException ? s.Message : HostKeyCheck.ChangedText,
        RouterSshException s => s.Message,
        HttpRequestException => L.T("router.ssh.core_download"),
        _ => Texts.Friendly(e),
    };

    // ---------- «Установить на роутер» ----------

    /// <summary>Окно показывает диалог установки.</summary>
    public event Action? InstallRequested;

    public string InstallText => Found ? L.T("router.install.reinstall") : L.T("router.install.install");

    /// <summary>Новый ход установки для диалога.</summary>
    public RouterInstallViewModel NewInstall() => new(this);

    /// <summary>После установки: войти тем же паролем (сохраняется как при обычном входе). Пакет только что поднялся — несколько попыток.</summary>
    internal async Task<bool> ConnectAfterInstallAsync(string password)
    {
        ClearRemoved();
        ForgetRouterSetup();
        var deadline = DateTime.UtcNow + RpcdSettle;
        for (int i = 0; ; i++)
        {
            if (i > 0) await Task.Delay(PollInterval * 2);
            if (await ConnectAsync(password, noPassword: password.Length == 0))
            {
                // Вошли, а объект dpishell ещё не поднялся (rpcd перезапускается) — дождаться и перечитать, а не показывать ошибку.
                if (await WaitDpishellAsync() > 0) await ReloadAfterRestartAsync();
                return true;
            }
            if (_detection?.Found == RouterFound.Ok && _connNeedsPassword) return false;
            if (i >= 3 && DateTime.UtcNow >= deadline) return false;
        }
    }

    /// <summary>
    /// После установки или обновления DpiShell rpcd на роутере перезапускается (~2 с после конца задания): объект dpishell пропадает
    /// и появляется снова. Столько окно ждёт его (повторяя status), прежде чем показать ошибку. По умолчанию — 8 опросов.
    /// </summary>
    public TimeSpan RpcdSettle => PollInterval * 8;

    static bool Restarting(RouterException e) => e.Failure is RouterFailure.Unreachable or RouterFailure.NoResponse or RouterFailure.NotSupported;

    /// <summary>
    /// Ждать, пока dpishell ответит на status, не дольше <see cref="RpcdSettle"/>: сколько раз пришлось повторить (0 — ответил сразу),
    /// -1 — так и не ответил (ошибка уже в строке состояния).
    /// </summary>
    async Task<int> WaitDpishellAsync()
    {
        var deadline = DateTime.UtcNow + RpcdSettle;
        for (int retries = 0; ; retries++)
        {
            try
            {
                Status = await Call(c => c.StatusAsync());
                return retries;
            }
            catch (RouterException e) when (Restarting(e))
            {
                if (DateTime.UtcNow >= deadline) { Failed(RouterTexts.Friendly(e)); return -1; }
                await Task.Delay(PollInterval);
            }
        }
    }

    /// <summary>Объект dpishell поднялся после перезапуска rpcd — перечитать состояние и стратегии, строка «Подключено».</summary>
    async Task ReloadAfterRestartAsync()
    {
        try
        {
            await RefreshStatusCoreAsync();
            if (Compat?.CanCall("strategies") != false) await LoadStrategiesAsync();
            SetConnection(() => L.T("router.conn.connected", new { version = _status?.Version ?? _detection?.Status?.Version }), ok: true);
        }
        catch (RouterException e) { Failed(RouterTexts.Friendly(e)); }
    }

    internal void SetBusy(bool busy) => Busy = busy;

    // ---------- совместимость окна и роутера (router-api §5.4) ----------

    /// <summary>Версия этой программы (в тестах подменяется).</summary>
    public Version AppVersion { get; set; } = RouterCompatibility.ParseVersion(AppRelease.CurrentVersion) ?? new Version(0, 0, 0);

    /// <summary>Совместимость по status (api, methods, min_app); null — состояния ещё нет.</summary>
    public RouterCompatibility? Compat => _status is { } s ? RouterCompatibility.FromStatus(s, AppVersion) : null;
    /// <summary>Другой мажор или min_app новее: доступны только состояние, обновление и удаление.</summary>
    public bool Blocked => Compat?.Level == RouterCompatLevel.Blocked;
    public bool HasCompat => Compat is { Level: not RouterCompatLevel.Ok };
    public string CompatText => Compat?.Text ?? "";
    /// <summary>Несовместимость лечится новой программой (роутер новее или требует новее), а не обновлением роутера.</summary>
    public bool CompatNeedsApp => _status is { } s && HasCompat
        && (s.Api.Major > RouterClient.SupportedApi.Major
            || s.Api.Major == RouterClient.SupportedApi.Major && s.Api.Minor > RouterClient.SupportedApi.Minor
            || RouterCompatibility.ParseVersion(s.MinApp) is { } need && need > AppVersion);
    public bool CompatNeedsRouter => HasCompat && !CompatNeedsApp;
    /// <summary>Роутер новее программы (минор): действия, меняющие настройки, — с подтверждением «под вашу ответственность».</summary>
    public bool RouterNewer => Compat?.Level == RouterCompatLevel.Warning && CompatNeedsApp;
    /// <summary>Подсказка у неактивных кнопок: роутер не умеет этого метода (нет в status.methods).</summary>
    public string? MethodsTip => Compat is { Level: RouterCompatLevel.Warning, Methods: not null } ? L.T("router.methods_tip") : null;

    /// <summary>Кнопка метода: вошли, не заняты и роутер этот метод умеет.</summary>
    bool Can(string method) => CanAct && (Compat?.CanCall(method) ?? true);

    // ---------- строка «DpiShell на роутере» на вкладке «Информация и обновления» (только чтение состояния, роутеру ничего не уходит) ----------

    /// <summary>Версия пакета: подключён — из статуса; найден, но не вошли — из поиска; иначе «Не установлено».</summary>
    public string PackageVersionText => Connected ? ConnectedPackageText()
        : Found ? (_detection?.Status?.Version is { Length: > 0 } dv ? L.T("router.pkg.need_login", new { version = dv }) : L.T("router.pkg.found_need_login"))
        : L.T("router.pkg.not_installed");

    /// <summary>
    /// Версия пакета DpiShell; роутер с status.packages (0.2.0-r8) отдаёт и версию страницы LuCI — её добавляем, если отличается
    /// (две части одного выпуска обновляются разными пакетами). Старый роутер — одна версия из status.version.
    /// </summary>
    string ConnectedPackageText()
    {
        var main = _status?.Packages?.Dpishell is { Length: > 0 } p ? p : _status?.Version is { Length: > 0 } v ? v : L.T("router.pkg.unknown");
        return _status?.Packages?.LuciAppDpishell is { Length: > 0 } luci && luci != main ? L.T("router.pkg.luci", new { version = main, luci }) : main;
    }

    /// <summary>
    /// Строка «Ядро на роутере»: версия, установленная в /opt/zapret, или «не установлено». Закреплённая в пакете отличается — добавляем.
    /// Нет входа — пусто (строка скрыта); старый роутер без installed_version — версия из пакета.
    /// </summary>
    public string RouterCoreText
    {
        get
        {
            if (!Connected || _status?.Core is not { } core) return "";
            if (core.ShownVersion is not { } shown) return L.T("router.core.not_installed");
            if (shown.Length == 0) return L.T("router.core.installed");
            return core.InstalledVersion is { Length: > 0 } && core.Version is { Length: > 0 } pinned && pinned != shown ? L.T("router.core.in_package", new { version = shown, pinned }) : shown;
        }
    }
    public bool HasRouterCore => RouterCoreText.Length > 0;
    public string PackageUpdateText => UpdateAvailable ? L.T("router.update.available", new { version = _status?.Update?.Version }) : "";
    public bool ShowPackageInstall => !Found && CanInstall;

    void RaiseAbout()
    {
        foreach (var n in new[] { nameof(PackageVersionText), nameof(PackageUpdateText), nameof(ShowPackageInstall), nameof(RouterCoreText), nameof(HasRouterCore) }) Raise(n);
    }

    void RaiseCompat()
    {
        RaiseAbout();
        foreach (var n in new[] { nameof(Compat), nameof(Blocked), nameof(HasCompat), nameof(CompatText), nameof(CompatNeedsApp), nameof(CompatNeedsRouter),
                                  nameof(RouterNewer), nameof(MethodsTip), nameof(CanAct), nameof(ScheduleEditable) })
            Raise(n);
        RaiseUpdate();
        Requery();
    }

    public static string RiskText => L.T("router.risk.text");
    bool _riskAccepted;

    /// <summary>Роутер новее — один раз за сеанс спросить «под вашу ответственность».</summary>
    bool RiskAccepted()
    {
        if (!RouterNewer || _riskAccepted) return true;
        if (!Confirm(RiskText)) return false;
        _riskAccepted = true;
        return true;
    }

    /// <summary>Страница выпусков программы — выпуск программы (само обновление — на вкладке «Информация и обновления», см. <see cref="GoUpdatesCmd"/>).</summary>
    public const string AppReleasesUrl = "https://github.com/Slavdew/dpishell/releases/latest";
    public ICommand OpenAppReleasesCmd => _openAppCmd ??= new RelayCommand(() =>
    {
        try { OpenUrl(AppReleasesUrl); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Failed(L.T("router.err.browser", new { error = e.Message })); }
    });
    ICommand? _openAppCmd;

    /// <summary>«Обновить программу →»: программа обновляется на вкладке «Информация и обновления» (блок «Версия»), а не отсюда.</summary>
    public ICommand GoUpdatesCmd => _goUpdatesCmd ??= new RelayCommand(() => Navigate(Pages.About));
    ICommand? _goUpdatesCmd;

    // ---------- обновление DpiShell на роутере (ядро договора §5.4) ----------

    /// <summary>Ждать итог обновления не дольше (apk upgrade и перезапуск rpcd), если роутер перестал отвечать.</summary>
    public TimeSpan UpdateTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// running держится дольше — роутер отвечает, но итог не пишет: решаем по версии (новая — обновлено, «роутер не дописал итог»;
    /// прежняя — «Обновление не завершилось» и «Повторить»).
    /// </summary>
    public TimeSpan UpdateStuckAfter { get; set; } = TimeSpan.FromMinutes(4);

    /// <summary>Шаги обновления на роутере (status.update.job.step) — подписи для «Скачиваю → Устанавливаю → Перезапуск службы → Готово».</summary>
    static readonly (string Id, string Key)[] UpdateStepNames =
        [("download", "router.update.step.download"), ("install", "router.update.step.install"), ("restart", "router.update.step.restart"), ("done", "router.update.step.done")];

    string? _updateStep;
    DateTime _updateStarted;
    string _updateLog = "";
    bool _updateFailed;

    /// <summary>Шаги с отметками: пройденные — ✓, текущий — выделен. Роутер не отдаёт step — пусто (видно только «Обновляю…» и время).</summary>
    public IReadOnlyList<UpdateStepRow> UpdateSteps
    {
        get
        {
            int current = Array.FindIndex(UpdateStepNames, x => x.Id == _updateStep);
            if (!Updating || current < 0) return [];
            bool finished = UpdateStepNames[current].Id == "done";
            return UpdateStepNames.Select((x, i) => (x, i, done: i < current || finished))
                .Select(s => new UpdateStepRow((s.done ? "✓ " : "") + L.T(s.x.Key), s.i == current, s.done, s.i == UpdateStepNames.Length - 1)).ToList();
        }
    }
    public bool HasUpdateSteps => UpdateSteps.Count > 0;

    /// <summary>Хвост журнала обновления («Подробнее»), если роутер его отдаёт.</summary>
    public string UpdateLog { get => _updateLog; private set { if (Set(ref _updateLog, value)) Raise(nameof(HasUpdateLog)); } }
    public bool HasUpdateLog => UpdateLog.Length > 0;

    /// <summary>Прошлое обновление не завершилось (версия прежняя) — кнопка «Повторить».</summary>
    public string UpdateButtonText => _updateFailed ? L.T("common.retry") : L.T("router.update.update");

    /// <summary>Очередной ответ status во время обновления: шаг, журнал, прошедшее время.</summary>
    void ShowUpdateJob(RouterUpdateJob? job)
    {
        if (job is not null)
        {
            _updateStep = job.Step;
            if (job.LogLines is { Count: > 0 } lines) UpdateLog = string.Join("\n", lines.TakeLast(30));
        }
        RaiseUpdate();
    }

    public ICommand UpdateCmd => _updateCmd ??= RelayCommand.Async(() => UpdateRouterAsync(), () => Connected && !Busy && (Compat?.CanCall("update_install") ?? true));
    ICommand? _updateCmd;

    bool _updating;
    public bool Updating { get => _updating; private set { if (Set(ref _updating, value)) { RaiseUpdate(); UpdateHome(); } } }

    /// <summary>«Обновить роутер»: update_install из витрины и ждать status.update.job; true — обновлено.</summary>
    public async Task<bool> UpdateRouterAsync()
    {
        if (Busy) { Say(L.T("router.guard.busy")); return false; }
        if (!Connected) { Say(L.T("router.guard.not_connected")); return false; }
        Busy = true;
        _updateStep = null;
        _updateFailed = false;
        UpdateLog = "";
        _updateStarted = DateTime.UtcNow;
        Updating = true;
        Say(L.T("router.update.busy"));
        var target = _status?.Update?.Version;
        try
        {
            var job = await UpdateFromAsync(RouterUpdateSource.Feed, target);
            if (job?.Ok == true) return await UpdatedAsync(job);
            if (job is { Running: true })   // завис: роутер отвечает, версия прежняя — повторить, а не качать через ПК поверх идущего apk
            {
                _updateFailed = true;
                Failed(_status?.Version is { } v ? L.T("router.update.same_version_v", new { version = v }) : L.T("router.update.same_version"));
                return false;
            }
            // Роутер не достал витрину — пакеты через этот ПК: скачать, проверить подпись и хеши, положить по SSH, from: "local".
            if (_status?.Update?.Version is { } version && Compat?.CanCall("update_install") != false)
            {
                Say(L.T("router.update.via_pc"));
                job = await UpdateLocalAsync(version);
                if (job?.Ok == true) return await UpdatedAsync(job);
            }
            _updateFailed = true;
            Failed(job is { Running: true } ? L.T("router.update.not_finished")
                : job is { Message.Length: > 0 } ? L.T("router.update.failed_msg", new { message = job.Message })
                : L.T("router.update.failed"));
            return false;
        }
        catch (RouterException e) { Failed(RouterTexts.Friendly(e)); return false; }
        catch (Exception e) when (e is RouterSshException or HttpRequestException or DpiShell.Core.SecurityException or IOException or UnauthorizedAccessException)
        {
            Failed(L.T("router.update.pc_failed", new { reason = e is HttpRequestException ? L.T("router.update.pc_no_packages") : SshText(e) }));
            return false;
        }
        finally { Busy = false; Updating = false; RaiseUpdate(); }
    }

    /// <summary>Проверочный ключ выпусков; null — встроенный (в тестах — тестовый).</summary>
    public byte[]? ReleaseKey { get; set; }

    RouterReleases? LoadReleases()
    {
        try { return (Releases ?? (() => RouterReleases.Load(Path.Combine(_shell.Paths.Seed, "router-releases.json"))))(); }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidDataException or UnauthorizedAccessException) { return null; }
    }

    async Task<RouterUpdateJob?> UpdateLocalAsync(string version)
    {
        var dir = Path.Combine(Path.GetDirectoryName(_settings.File)!, "router-update");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        var files = await RouterUpdatePackages.DownloadAsync(Http, LoadReleases(), version, dir, ReleaseKey);
        var address = SetupAddress() ?? throw new RouterSshException(RouterSshFailure.Unreachable, L.T("router.err.no_address"));
        var pw = SshPassword(null, false) ?? throw new RouterSshException(RouterSshFailure.AuthFailed, L.T("router.err.no_saved_password"));
        await using (var ssh = await Ssh.ConnectAsync(new RouterSshTarget(address), () => pw))
            await new RouterSsh(ssh).UploadUpdateAsync(files, _status?.Update?.LocalSigned == true ? RouterUpdatePackages.ReleaseFilesIn(dir) : null);
        return await UpdateFromAsync(RouterUpdateSource.Local, version);
    }

    // ---------- «Доступно обновление» и «Проверить обновления» ----------

    /// <summary>status.update.available: на вкладке «Роутер» — строка «Доступно обновление X», «Что нового», «Обновить».</summary>
    public bool UpdateAvailable => _status?.Update is { Available: true } && !Updating;
    public bool ShowUpdate => UpdateAvailable || Updating;
    /// <summary>Идёт — «Обновляю DpiShell на роутере… 1:23» (прошедшее время: видно, что не зависло); не завершилось — так и сказано.</summary>
    public string UpdateTitle => Updating ? L.T("router.update.title_running", new { time = (DateTime.UtcNow - _updateStarted).ToString("m\\:ss") })
        : _updateFailed ? (_status?.Version is { Length: > 0 } cur
            ? L.T("router.update.title_failed_cur", new { version = _status?.Update?.Version, current = cur })
            : L.T("router.update.title_failed", new { version = _status?.Update?.Version }))
        : L.T("router.update.available", new { version = _status?.Update?.Version });
    public string UpdateNotes => string.Join("\n", (_status?.Update?.Notes ?? []).Select(n => "• " + n));
    public bool HasUpdateNotes => UpdateNotes.Length > 0 && !Updating;

    void RaiseUpdate()
    {
        foreach (var n in new[] { nameof(UpdateAvailable), nameof(ShowUpdate), nameof(UpdateTitle), nameof(UpdateNotes), nameof(HasUpdateNotes),
                     nameof(UpdateSteps), nameof(HasUpdateSteps), nameof(UpdateButtonText) }) Raise(n);
    }

    /// <summary>Ждать итог update_check не дольше.</summary>
    public TimeSpan UpdateCheckTimeout { get; set; } = TimeSpan.FromMinutes(1);

    public ICommand UpdateCheckCmd => _updateCheckCmd ??= RelayCommand.Async(CheckUpdatesAsync, () => Connected && !Busy && Compat?.CanCall("update_check") != false);
    ICommand? _updateCheckCmd;

    /// <summary>«Проверить обновления»: update_check и ждать новый status.update.checked_at.</summary>
    public async Task CheckUpdatesAsync()
    {
        if (Busy || !Connected) return;
        Busy = true;
        Say(L.T("router.update.check_busy"));
        try
        {
            var before = _status?.Update?.CheckedAt;
            await Call(c => c.UpdateCheckAsync());
            var started = DateTime.UtcNow;
            while (DateTime.UtcNow - started < UpdateCheckTimeout)
            {
                await Task.Delay(PollInterval);
                var s = await Call(c => c.StatusAsync());
                Status = s;
                if (s.Jobs?.Update != true && s.Update?.CheckedAt != before) break;
            }
            Say(_status?.Update is { Available: true } u ? L.T("router.update.available_say", new { version = u.Version }) : L.T("router.update.none"));
        }
        catch (RouterException e) { Failed(RouterTexts.Friendly(e)); }
        finally { Busy = false; }
    }

    /// <summary>
    /// Обновление закончилось — rpcd перезапустится через ~2 с: переждать перезапуск и дождаться dpishell (<see cref="WaitDpishellAsync"/>),
    /// чтобы следующая команда не упала «объекта нет».
    /// </summary>
    async Task<bool> UpdatedAsync(RouterUpdateJob job)
    {
        await Task.Delay(PollInterval * 3);
        if (await WaitDpishellAsync() < 0)
        {
            Say(L.T("router.update.restarting"));
            return true;
        }
        if (Compat?.CanCall("strategies") != false)
        {
            try { await LoadStrategiesAsync(); } catch (RouterException e) when (Restarting(e)) { }
        }
        return Updated(job);
    }

    bool Updated(RouterUpdateJob job)
    {

        SetConnection(() => L.T("router.conn.connected", new { version = _status?.Version }), ok: true);
        Say(job.Message is { Length: > 0 } m ? m : L.T("router.update.done", new { version = _status?.Version }));
        return true;
    }

    /// <summary>
    /// update_install и ждать новое задание до конца. Пока rpcd перезапускается, роутер может не отвечать — ждём дальше.
    /// running держится дольше <see cref="UpdateStuckAfter"/>, а роутер отвечает: версия уже <paramref name="target"/> — успех
    /// («роутер не дописал итог»), прежняя — задание с Running = true (не завершилось).
    /// </summary>
    async Task<RouterUpdateJob?> UpdateFromAsync(RouterUpdateSource from, string? target)
    {
        var before = _status?.Update?.Job;
        var versionBefore = _status?.Version;
        await Call(c => c.UpdateInstallAsync(from));
        var started = DateTime.UtcNow;
        bool sawRunning = false;
        while (DateTime.UtcNow - started < UpdateTimeout)
        {
            await Task.Delay(PollInterval);
            RaiseUpdate();   // прошедшее время в заголовке — и пока роутер не отвечает
            RouterStatus s;
            try { s = await Call(c => c.StatusAsync()); }
            catch (RouterException e) when (Restarting(e)) { continue; }   // rpcd перезапускается — объекта dpishell пока нет
            Status = s;
            var job = s.Update?.Job;
            ShowUpdateJob(job);
            if (s.Jobs?.Update == true || job?.Running == true)
            {
                sawRunning = true;
                if (DateTime.UtcNow - started < UpdateStuckAfter) continue;
                bool updated = target is not null ? s.Version == target : s.Version != versionBefore;
                return updated
                    ? new RouterUpdateJob("update", job?.From, false, true, L.T("router.update.done_no_summary", new { version = s.Version }), Log: job?.Log)
                    : (job ?? new RouterUpdateJob("update", null, true, null, null)) with { Running = true };
            }
            if (job is not null && (sawRunning || job != before)) return job;
        }
        throw new RouterException(RouterFailure.NoResponse, L.T("router.update.too_long"));
    }

    // ---------- «Удалить DpiShell с роутера» ----------

    /// <summary>Окно показывает диалог удаления (с подтверждением).</summary>
    public event Action? UninstallRequested;
    public ICommand UninstallCmd => _uninstallCmd ??= new RelayCommand(() => UninstallRequested?.Invoke(), () => Found && !Busy);
    ICommand? _uninstallCmd;

    public RouterUninstallViewModel NewUninstall() => new(this);

    /// <summary>Удаление идёт дольше этого — итог не ждём, сообщаем «идёт дольше обычного».</summary>
    public TimeSpan UninstallTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>Через API можно: вошли (uninstall — ядро договора, доступно при любой совместимости).</summary>
    internal bool UninstallViaApi => Connected && _client is { LoggedIn: true };

    internal Task<RouterStarted> CallUninstallAsync(bool keepSettings) => Call(c => c.UninstallAsync(keepSettings));

    /// <summary>
    /// Через API: сначала SSH (стереть прошлый итог — иначе он примется за новый), потом uninstall, ждать итог и проверить «что осталось».
    /// SSH не вышел — удаление всё равно запускается, итог — «проверить не удалось» (sshError).
    /// </summary>
    internal async Task<(RouterUninstallOutcome? Outcome, Exception? SshError)> UninstallViaApiAsync(RouterSshTarget target, string password, bool keepSettings, CancellationToken ct)
    {
        ISshSession? ssh = null;
        Exception? sshError = null;
        try { ssh = await Ssh.ConnectAsync(target, () => password, ct); }
        catch (RouterSshException e) when (e.Failure != RouterSshFailure.HostKeyChanged) { sshError = e; }
        try
        {
            var r = ssh is null ? null : new RouterSsh(ssh);
            if (ssh is not null) await ssh.RunAsync($"rm -f {RouterSsh.UninstallResultPath}", null, null, ct);
            await CallUninstallAsync(keepSettings);
            if (r is null) return (null, sshError);
            var started = DateTime.UtcNow;
            while (true)
            {
                if (await r.ReadUninstallResultAsync(ct) is { Running: false } res)
                    return (new RouterUninstallOutcome(res, await r.AfterUninstallAsync(ct)), null);
                if (DateTime.UtcNow - started > UninstallTimeout)
                    throw new RouterSshException(RouterSshFailure.Failed, L.T("router.uninstall.too_long"));
                await Task.Delay(PollInterval, ct);
            }
        }
        finally { if (ssh is not null) await ssh.DisposeAsync(); }
    }

    /// <summary>После удаления: входа больше нет, ищем заново — вкладка переходит в пустое состояние с «Установить на роутер».</summary>
    internal async Task AfterUninstallAsync()
    {
        Connected = false;
        Status = null;
        Strategies.Clear();
        ForgetRouterSetup();
        ForgetBypass();
        _removed = true;
        try { await DetectAsync(); }
        catch (RouterException) { SetDetection(null); }
        if (!Found) SetConnection(() => L.T("router.empty.removed"));
        Raise(nameof(EmptyTitle));
    }

    bool _removed;
    /// <summary>Пустое состояние после удаления из окна — «удалён», а не «не найден».</summary>
    internal bool Removed => _removed;
    internal void ClearRemoved() { if (_removed) { _removed = false; Raise(nameof(EmptyTitle)); } }
}

/// <summary>Шаг установки в диалоге: ждёт, идёт, готово, сбой.</summary>
public sealed class RouterInstallStep(string title) : Observable
{
    public const int Waiting = 0, Running = 1, Done = 2, Failed = 3;
    public string Title { get; } = title;
    int _state;
    public int State { get => _state; set { if (Set(ref _state, value)) Raise(nameof(Mark)); } }
    public string Mark => State switch { Running => "…", Done => "✓", Failed => "✕", _ => "·" };
}

public enum RouterInstallStage { Input, Checking, Checked, Installing, Done, Failed }

/// <summary>
/// Диалог «Установить на роутер»: адрес и пароль → «Проверить роутер» (предпроверка простыми словами) → «Установить»
/// (ход по шагам из строк «# …» сценария) → итог, при успехе — вход тем же паролем и «Подобрать на роутере».
/// </summary>
public sealed class RouterInstallViewModel : Observable
{
    readonly RouterViewModel _router;
    readonly List<string> _log = [];
    RouterVerdict? _verdict;
    CancellationTokenSource? _cts;

    public RouterInstallViewModel(RouterViewModel router)
    {
        _router = router;
        Steps = [new(L.T("router.install.step.prepare")), new(L.T("router.install.step.feed")), new(L.T("router.install.step.packages")), new(L.T("router.install.step.core")), new(L.T("router.install.step.start"))];
    }

    public RouterViewModel Router => _router;
    public ObservableCollection<RouterInstallStep> Steps { get; }

    RouterInstallStage _stage;
    public RouterInstallStage Stage
    {
        get => _stage;
        private set
        {
            if (!Set(ref _stage, value)) return;
            foreach (var n in new[] { nameof(Working), nameof(CanEdit), nameof(CanCheck), nameof(CanInstall), nameof(CanTryInstall), nameof(InstallText), nameof(ShowSteps), nameof(IsDone), nameof(CanGoSelect), nameof(ShowCheck), nameof(Unsuitable), nameof(ShowCheckPlain), nameof(ShowCheckPrimary) })
                Raise(n);
        }
    }

    public bool Working => Stage is RouterInstallStage.Checking or RouterInstallStage.Installing;
    public bool CanEdit => !Working && Stage != RouterInstallStage.Done;
    public bool CanCheck => CanEdit;
    public bool ShowCheck => Stage != RouterInstallStage.Done;
    public bool CanInstall => Stage == RouterInstallStage.Checked && _verdict?.CanInstall == true;
    /// <summary>
    /// «Установить» активна, пока можно править: непроверенный роутер она проверит сама. Роутер проверен и не подходит — недоступна (причина — в итоге выше),
    /// главной становится «Проверить роутер» после исправления (UX-аудит 7, п. 11).
    /// </summary>
    public bool CanTryInstall => CanEdit && !Unsuitable;
    /// <summary>Проверка показала «Роутер не подходит»: ставить нельзя, пока причины не устранены.</summary>
    public bool Unsuitable => _verdict is { CanInstall: false } && Stage is RouterInstallStage.Checked or RouterInstallStage.Failed;
    /// <summary>«Проверить роутер» — обычная кнопка; при «не подходит» вместо неё главная (<see cref="ShowCheckPrimary"/>).</summary>
    public bool ShowCheckPlain => ShowCheck && !Unsuitable;
    public bool ShowCheckPrimary => ShowCheck && Unsuitable;
    /// <summary>После сбоя установки главная кнопка — «Повторить».</summary>
    public string InstallText => Stage == RouterInstallStage.Failed ? L.T("common.retry") : L.T("router.install.go");
    public bool IsDone => Stage == RouterInstallStage.Done;
    public bool CanGoSelect => IsDone && _router.Connected;
    bool _installStarted;
    public bool ShowSteps => _installStarted;

    string _verdictTitle = "";
    public string VerdictTitle { get => _verdictTitle; private set { if (Set(ref _verdictTitle, value)) Raise(nameof(HasVerdict)); } }
    public bool HasVerdict => VerdictTitle.Length > 0;
    bool _verdictOk;
    public bool VerdictOk { get => _verdictOk; private set => Set(ref _verdictOk, value); }
    string _verdictText = "";
    /// <summary>Причины отказа и предупреждения — по строке.</summary>
    public string VerdictText { get => _verdictText; private set { if (Set(ref _verdictText, value)) Raise(nameof(HasVerdictText)); } }
    public bool HasVerdictText => VerdictText.Length > 0;
    string _tunnelNote = "";
    public string TunnelNote { get => _tunnelNote; private set { if (Set(ref _tunnelNote, value)) Raise(nameof(HasTunnelNote)); } }
    public bool HasTunnelNote => TunnelNote.Length > 0;

    public static string TunnelText => L.T("router.tunnel.text");

    string _progress = "";
    public string ProgressText { get => _progress; private set => Set(ref _progress, value); }

    string _result = "";
    public string ResultText { get => _result; private set { if (Set(ref _result, value)) Raise(nameof(HasResult)); } }
    public bool HasResult => ResultText.Length > 0;
    string _error = "";
    public string ErrorText { get => _error; private set { if (Set(ref _error, value)) Raise(nameof(HasError)); } }
    public bool HasError => ErrorText.Length > 0;
    string _details = "";
    /// <summary>«Подробнее»: хвост журнала установки.</summary>
    public string Details { get => _details; private set { if (Set(ref _details, value)) Raise(nameof(HasDetails)); } }
    public bool HasDetails => Details.Length > 0 && HasError;

    void Log(string line)
    {
        lock (_log)
        {
            _log.Add(line);
            Details = string.Join("\n", _log.TakeLast(40));
        }
    }

    /// <summary>Шаг сценария из итога (step) → шаг диалога.</summary>
    static int StepOfResult(string step) => step switch { "key" or "feed" => 1, "packages" => 2, "core" => 3, "status" or "done" => 4, _ => 0 };

    /// <summary>Что делать после сбоя установки: совет по пути, которым шла установка (через интернет ПК или роутера), и в каком состоянии остался роутер (по шагу сбоя).</summary>
    string FailText(RouterInstallResult r)
    {
        bool viaPc = _verdict is { NeedsTunnel: true };
        return r.Step switch
        {
            "packages" => L.T(viaPc ? "router.install.fail_packages_pc" : "router.install.fail_packages"),
            "core" => L.T(viaPc ? "router.install.fail_core_pc" : "router.install.fail_core"),
            "key" => L.T("router.install.fail_key"),
            _ => r.Message,
        };
    }

    /// <summary>Под «Роутер не подходит»: исправимые причины (чужой пакет) и неисправимые (архитектура, память, версия) — разные слова.</summary>
    static readonly HashSet<string> FixableBlockers = ["foreign_package"];
    public string UnsuitableText
    {
        get
        {
            if (_verdict is not { CanInstall: false } v || v.Blockers.Count == 0) return L.T("router.install.unavailable");
            int fixable = v.Blockers.Count(b => FixableBlockers.Contains(b.Code));
            return fixable == v.Blockers.Count ? L.T("router.install.unavailable")
                : fixable == 0 ? L.T("router.install.unavailable_hard")
                : L.T("router.install.unavailable_mixed");
        }
    }

    void Step(int i, int state = RouterInstallStep.Running)
    {
        for (int k = 0; k < Steps.Count; k++)
            if (k < i && Steps[k].State != RouterInstallStep.Done) Steps[k].State = RouterInstallStep.Done;
        Steps[i].State = state;
    }

    void OnProgress(string line) => Log("# " + line);

    /// <summary>Установщик сообщил шаг (код, не текст): отметка в списке и подпись шага в строке хода.</summary>
    void OnStep(RouterInstallStepCode code)
    {
        var i = (int)code;
        Step(i);
        ProgressText = Steps[i].Title;
    }

    (RouterSshTarget Target, string Password)? Prepare(string? typed, bool noPassword)
    {
        ErrorText = "";
        var address = _router.SetupAddress();
        if (address is null) { ErrorText = L.T("router.addr.unknown"); return null; }
        if (_router.SshPassword(typed, noPassword) is not { } pw)
        {
            ErrorText = L.T("router.install.need_password");
            return null;
        }
        return (new RouterSshTarget(address), pw);
    }

    /// <summary>«Проверить роутер»: предпроверка по SSH и решение простыми словами.</summary>
    public async Task<bool> CheckAsync(string? password, bool noPassword = false)
    {
        if (Working || Prepare(password, noPassword) is not { } p) return false;
        Stage = RouterInstallStage.Checking;
        VerdictTitle = ""; VerdictText = ""; TunnelNote = "";
        ProgressText = L.T("router.install.checking");
        Log(L.T("router.log.checking", new { host = p.Target.Host }));
        _cts = new();
        try
        {
            var pw = p.Password;
            ShowVerdict(await _router.Flow().CheckAsync(p.Target, () => pw, _cts.Token));
            Stage = RouterInstallStage.Checked;
            return _verdict!.CanInstall;
        }
        catch (Exception e) when (e is RouterSshException or IOException or HttpRequestException or OperationCanceledException)
        {
            Fail(e, p.Target.Host);
            return false;
        }
        finally { ProgressText = ""; }
    }

    void ShowVerdict(RouterVerdict v)
    {
        _verdict = v;
        VerdictOk = v.CanInstall;
        VerdictTitle = v.CanInstall
            ? v.AlreadyInstalled ? L.T("router.install.verdict_updated", new { version = v.InstalledVersion }) : L.T("router.install.verdict_ok", new { version = v.Release?.Version })
            : L.T("router.install.verdict_bad");
        var lines = v.Blockers.Select(b => "• " + b.Text)
            .Concat(v.Warnings.Where(w => w.Code is not ("tunnel" or "installed")).Select(w => "• " + w.Text));
        VerdictText = string.Join("\n", lines);
        TunnelNote = v.CanInstall && v.NeedsTunnel ? TunnelText : "";
        Log(VerdictTitle + (VerdictText.Length > 0 ? "\n" + VerdictText : ""));
        Raise(nameof(CanInstall));
        foreach (var n in new[] { nameof(Unsuitable), nameof(UnsuitableText), nameof(CanTryInstall), nameof(ShowCheckPlain), nameof(ShowCheckPrimary) }) Raise(n);
    }

    /// <summary>«Установить»: установка целиком; при успехе — вход тем же паролем.</summary>
    public async Task<bool> InstallAsync(string? password, bool noPassword = false)
    {
        // Не проверяли (или прошлая попытка сорвалась) — сначала сама проверка роутера, потом установка.
        if (!CanInstall && (!await CheckAsync(password, noPassword) || !CanInstall)) return false;
        if (Prepare(password, noPassword) is not { } p) return false;
        _installStarted = true; Raise(nameof(ShowSteps));
        foreach (var s in Steps) s.State = RouterInstallStep.Waiting;
        Stage = RouterInstallStage.Installing;
        ResultText = "";
        _cts = new();
        _router.SetBusy(true);
        RouterInstallOutcome o;
        try
        {
            var pw = p.Password;
            o = await _router.Flow().InstallAsync(p.Target, () => pw, new SyncProgress(OnProgress), _cts.Token, new SyncSteps(OnStep));
        }
        catch (Exception e) when (e is RouterSshException or IOException or HttpRequestException or OperationCanceledException or UnauthorizedAccessException
                                      or DpiShell.Core.SecurityException or InvalidDataException)
        {
            MarkFailed();
            Fail(e, p.Target.Host);
            return false;
        }
        finally { _router.SetBusy(false); ProgressText = ""; }

        if (o.Install is not { } res)
        {
            // Модуль ядра так и не нашёлся через туннель — решение «не подходит».
            Steps[0].State = RouterInstallStep.Failed;
            ShowVerdict(o.Verdict);
            Stage = RouterInstallStage.Failed;
            ErrorText = L.T("router.install.not_compatible");
            return false;
        }
        Log(res.Message);
        if (!res.Ok)
        {
            Step(StepOfResult(res.Step), RouterInstallStep.Failed);
            Stage = RouterInstallStage.Failed;
            ErrorText = FailText(res);
            _router.Failed(L.T("router.install.failed"));
            return false;
        }
        foreach (var s in Steps) s.State = RouterInstallStep.Done;
        ResultText = L.T("router.install.installed_connecting", new { version = res.Version });
        var connected = await _router.ConnectAfterInstallAsync(p.Password);
        ResultText = connected
            ? L.T("router.install.installed_connected", new { version = res.Version })
            : L.T("router.install.installed_not_connected", new { version = res.Version, reason = _router.ConnectionText });
        Stage = RouterInstallStage.Done;
        Raise(nameof(CanGoSelect));
        _router.Say(connected ? L.T("router.install.say_connected") : L.T("router.install.say_installed"));
        return true;
    }

    /// <summary>
    /// «Подобрать на роутере» после установки: сразу запустить полный подбор по группам по умолчанию и показать ход
    /// на экране «Подбор» (пользователь ждёт старта, а не перехода).
    /// </summary>
    public Task SelectNowAsync()
    {
        _router.SetSelectGroups(RouterViewModel.DefaultSelectGroups);
        _router.GoSelectCmd.Execute(null);
        return _router.StartSelectAsync(deep: false, (RouterViewModel.DefaultSelectGroups, true));
    }

    void MarkFailed()
    {
        var running = Steps.LastOrDefault(s => s.State == RouterInstallStep.Running) ?? Steps[0];
        running.State = RouterInstallStep.Failed;
    }

    void Fail(Exception e, string host)
    {
        Stage = RouterInstallStage.Failed;
        ErrorText = e is OperationCanceledException ? L.T("router.install.cancelled") : RouterViewModel.SshText(e, host);
        Log(L.T("router.log.error", new { error = e.Message }));
        Raise(nameof(HasDetails));
    }

    /// <summary>Закрыли диалог во время работы — прервать.</summary>
    public void Cancel() => _cts?.Cancel();

    sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    sealed class SyncSteps(Action<RouterInstallStepCode> report) : IProgress<RouterInstallStepCode>
    {
        public void Report(RouterInstallStepCode value) => report(value);
    }
}

public enum RouterUninstallStage { Confirm, Working, Done, Failed }

/// <summary>
/// Диалог «Удалить DpiShell с роутера»: что произойдёт, «сохранить настройки / удалить полностью», подтверждение галочкой (не в один клик),
/// «Отмена» по умолчанию. Через API, если роутер отвечает; иначе — по SSH. После — «что осталось».
/// </summary>
public sealed class RouterUninstallViewModel(RouterViewModel router) : Observable
{
    readonly List<string> _log = [];
    CancellationTokenSource? _cts;

    public RouterViewModel Router => router;

    bool _keep = true;
    /// <summary>Сохранить настройки (стратегию, свои сайты, расписание) — для установки заново.</summary>
    public bool KeepSettings { get => _keep; set { if (Set(ref _keep, value)) Raise(nameof(Full)); } }
    public bool Full { get => !_keep; set => KeepSettings = !value; }

    bool _understood;
    public bool Understood { get => _understood; set { if (Set(ref _understood, value)) Raise(nameof(CanRemove)); } }

    /// <summary>Через API не выйдет (не вошли) — нужен пароль для SSH.</summary>
    public bool NeedsPassword { get; } = !router.UninstallViaApi;

    RouterUninstallStage _stage;
    public RouterUninstallStage Stage
    {
        get => _stage;
        private set
        {
            if (!Set(ref _stage, value)) return;
            foreach (var n in new[] { nameof(Working), nameof(CanRemove), nameof(IsConfirm), nameof(IsDone), nameof(ShowPassword) }) Raise(n);
        }
    }

    public bool Working => Stage == RouterUninstallStage.Working;
    public bool IsConfirm => Stage is RouterUninstallStage.Confirm or RouterUninstallStage.Failed;
    public bool IsDone => Stage == RouterUninstallStage.Done;
    public bool ShowPassword => NeedsPassword && IsConfirm;
    public bool CanRemove => Understood && IsConfirm;

    string _result = "";
    public string ResultTitle { get => _result; private set => Set(ref _result, value); }
    string _left = "";
    /// <summary>Что осталось на роутере и что сохранено — по строке.</summary>
    public string LeftText { get => _left; private set { if (Set(ref _left, value)) Raise(nameof(HasLeft)); } }
    public bool HasLeft => LeftText.Length > 0;
    bool _clean;
    public bool Clean { get => _clean; private set => Set(ref _clean, value); }
    string _error = "";
    public string ErrorText { get => _error; private set { if (Set(ref _error, value)) Raise(nameof(HasError)); } }
    public bool HasError => ErrorText.Length > 0;
    string _details = "";
    public string Details { get => _details; private set { if (Set(ref _details, value)) Raise(nameof(HasDetails)); } }
    public bool HasDetails => Details.Length > 0 && HasError;
    string _progress = "";
    public string ProgressText { get => _progress; private set => Set(ref _progress, value); }

    void Log(string line) { lock (_log) { _log.Add(line); Details = string.Join("\n", _log.TakeLast(40)); } }

    public async Task<bool> UninstallAsync(string? password = null, bool noPassword = false)
    {
        if (!CanRemove) return false;
        ErrorText = "";
        var address = router.SetupAddress();
        if (address is null) { ErrorText = L.T("router.addr.unknown"); return false; }
        if (router.SshPassword(password, noPassword) is not { } pw)
        {
            ErrorText = L.T("router.install.need_password");
            return false;
        }
        var target = new RouterSshTarget(address);
        Stage = RouterUninstallStage.Working;
        ProgressText = L.T("router.uninstall.busy");
        _cts = new();
        router.SetBusy(true);
        RouterUninstallOutcome? o = null;
        Exception? sshError = null;
        try
        {
            bool viaApi = false;
            if (router.UninstallViaApi)
            {
                Log(L.T("router.log.uninstall_api", new { mode = L.T(KeepSettings ? "router.log.keep_settings" : "router.log.full") }));
                try { (o, sshError) = await router.UninstallViaApiAsync(target, pw, KeepSettings, _cts.Token); viaApi = true; }
                catch (RouterException e) { Log(L.T("router.log.api_failed", new { error = e.Message })); }
            }
            if (!viaApi)
            {
                Log(L.T("router.log.uninstall_ssh", new { host = target.Host }));
                o = await router.Flow().UninstallAsync(target, () => pw, KeepSettings, _cts.Token);
            }
        }
        catch (Exception e) when (e is RouterSshException or IOException or OperationCanceledException)
        {
            Log(L.T("router.log.error", new { error = e.Message }));
            router.SetBusy(false);
            ProgressText = "";
            Stage = RouterUninstallStage.Failed;
            ErrorText = e is OperationCanceledException ? L.T("router.install.cancelled") : RouterViewModel.SshText(e, target.Host);
            Raise(nameof(HasDetails));
            return false;
        }
        router.SetBusy(false);
        ProgressText = "";

        if (o is null)
        {
            // Удаление запущено через API, а по SSH не зайти — итог не проверить.
            Log("SSH: " + sshError?.Message);
            ResultTitle = L.T("router.uninstall.started");
            LeftText = L.T("router.uninstall.left_unknown_ssh", new { reason = RouterViewModel.SshText(sshError!, target.Host) });
            Clean = false;
        }
        else if (o.Result.Ok != true)
        {
            Log(o.Result.Message ?? L.T("router.log.uninstall_failed"));
            Stage = RouterUninstallStage.Failed;
            ErrorText = L.T("router.uninstall.failed", new { reason = o.Result.Message ?? L.T("router.uninstall.no_reason") });
            Raise(nameof(HasDetails));
            await router.AfterUninstallAsync();
            return false;
        }
        else
        {
            Log(o.Result.Message ?? L.T("router.log.removed"));
            var left = o.Left?.Left ?? [];
            Clean = o.Left?.Clean == true;
            ResultTitle = Clean ? L.T("router.uninstall.clean") : L.T("router.uninstall.done");
            var lines = left.Select(l => "• " + l.What + (string.IsNullOrEmpty(l.Detail) ? "" : " — " + l.Detail)).ToList();
            if (lines.Count > 0) lines.Insert(0, L.T("router.uninstall.left_header"));
            if (o.Left?.Kept?.Settings == true) lines.Add(L.T("router.uninstall.kept_settings"));
            if (o.Left?.Kept?.HttpsDnsProxy == true) lines.Add(L.T("router.uninstall.kept_dns_pkg"));
            if (o.Left is null) lines.Add(L.T("router.uninstall.left_unknown"));
            LeftText = string.Join("\n", lines);
        }
        Stage = RouterUninstallStage.Done;
        await router.AfterUninstallAsync();
        router.Say(ResultTitle);
        return true;
    }

    public void Cancel() => _cts?.Cancel();
}
