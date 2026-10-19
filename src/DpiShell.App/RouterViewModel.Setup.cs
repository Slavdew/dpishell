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

    public const string HostKeyText =
        "Ключ роутера изменился. Так бывает после переустановки прошивки (OpenWrt).\n\n"
        + "Если вы переустанавливали прошивку — нажмите «Доверять новому». Если нет — нажмите «Отмена»: кто-то может подменять роутер.";

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
                    throw new RouterSshException(RouterSshFailure.HostKeyChanged, "Ключ роутера изменился — подключение отменено. Если прошивку переустанавливали, повторите и нажмите «Доверять новому».", e);
                vm._settings.HostKeys.Remove(target.HostKeyId);
                vm.Say("Новый ключ роутера принят");
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
        Say("Пароль роутера забыт — на этом компьютере его больше нет");
    }, () => HasSavedPassword);
    ICommand? _forgetPasswordCmd;

    /// <summary>«Пароль сохранён на этом компьютере» (зашифрован, только для этого пользователя Windows).</summary>
    public string SavedPasswordText => RouterSecrets.Unprotect(_settings.RouterPassword) == ""
        ? "Пароль не задан — так и запомнено на этом компьютере" : "Пароль сохранён на этом компьютере (зашифрован)";

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
            $"Роутер{(host is null ? "" : " " + host)} не отвечает по SSH. Проверьте адрес; в настройках роутера (LuCI) — «Система» → «Администрирование» → SSH должен быть включён.",
        RouterSshException { Failure: RouterSshFailure.AuthFailed } => "Неверный пароль роутера",
        RouterSshException { Failure: RouterSshFailure.HostKeyChanged } s => s.InnerException is RouterSshException ? s.Message : HostKeyCheck.ChangedText,
        RouterSshException s => s.Message,
        HttpRequestException => "Не удалось скачать ядро zapret на этот компьютер. Проверьте интернет и повторите.",
        _ => Texts.Friendly(e),
    };

    // ---------- «Установить на роутер» ----------

    /// <summary>Окно показывает диалог установки.</summary>
    public event Action? InstallRequested;

    public string InstallText => Found ? "Установить заново…" : "Установить на роутер…";

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
            if (_detection?.Found == RouterFound.Ok && ConnectionText.Contains("пароль")) return false;
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
            SetConnection($"Подключено, DpiShell {_status?.Version ?? _detection?.Status?.Version}", ok: true);
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
    public string? MethodsTip => Compat is { Level: RouterCompatLevel.Warning, Methods: not null } ? "Неактивно: DpiShell на роутере этого не умеет — обновите его" : null;

    /// <summary>Кнопка метода: вошли, не заняты и роутер этот метод умеет.</summary>
    bool Can(string method) => CanAct && (Compat?.CanCall(method) ?? true);

    // ---------- строка «DpiShell на роутере» на вкладке «Информация и обновления» (только чтение состояния, роутеру ничего не уходит) ----------

    /// <summary>Версия пакета: подключён — из статуса; найден, но не вошли — из поиска; иначе «Не установлено».</summary>
    public string PackageVersionText => Connected ? (_status?.Version is { Length: > 0 } v ? v : "неизвестно")
        : Found ? (_detection?.Status?.Version is { Length: > 0 } dv ? dv + " · нужен вход на вкладке «Роутер»" : "найден · нужен вход на вкладке «Роутер»")
        : "Не установлено";
    public string PackageUpdateText => UpdateAvailable ? $"Доступно обновление {_status?.Update?.Version}" : "";
    public bool ShowPackageInstall => !Found && CanInstall;

    void RaiseAbout()
    {
        foreach (var n in new[] { nameof(PackageVersionText), nameof(PackageUpdateText), nameof(ShowPackageInstall) }) Raise(n);
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

    public const string RiskText = "DpiShell на роутере новее этой программы. Менять его настройки отсюда можно, но под вашу ответственность: программа может не знать его новых правил. Продолжить?";
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
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Failed("Не удалось открыть браузер: " + e.Message); }
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
    static readonly (string Id, string Title)[] UpdateStepNames =
        [("download", "Скачиваю"), ("install", "Устанавливаю"), ("restart", "Перезапуск службы"), ("done", "Готово")];

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
                .Select(s => new UpdateStepRow((s.done ? "✓ " : "") + s.x.Title, s.i == current, s.done, s.i == UpdateStepNames.Length - 1)).ToList();
        }
    }
    public bool HasUpdateSteps => UpdateSteps.Count > 0;

    /// <summary>Хвост журнала обновления («Подробнее»), если роутер его отдаёт.</summary>
    public string UpdateLog { get => _updateLog; private set { if (Set(ref _updateLog, value)) Raise(nameof(HasUpdateLog)); } }
    public bool HasUpdateLog => UpdateLog.Length > 0;

    /// <summary>Прошлое обновление не завершилось (версия прежняя) — кнопка «Повторить».</summary>
    public string UpdateButtonText => _updateFailed ? "Повторить" : "Обновить";

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
        if (Busy) { Say("Подождите: роутер ещё выполняет прошлую команду"); return false; }
        if (!Connected) { Say("Сначала подключитесь к роутеру"); return false; }
        Busy = true;
        _updateStep = null;
        _updateFailed = false;
        UpdateLog = "";
        _updateStarted = DateTime.UtcNow;
        Updating = true;
        Say("Обновляю DpiShell на роутере…");
        var target = _status?.Update?.Version;
        try
        {
            var job = await UpdateFromAsync(RouterUpdateSource.Feed, target);
            if (job?.Ok == true) return await UpdatedAsync(job);
            if (job is { Running: true })   // завис: роутер отвечает, версия прежняя — повторить, а не качать через ПК поверх идущего apk
            {
                _updateFailed = true;
                Failed("Обновление не завершилось: роутер отвечает, но версия DpiShell прежняя" + (_status?.Version is { } v ? $" ({v})" : "") + ". Можно повторить.");
                return false;
            }
            // Роутер не достал витрину — пакеты через этот ПК: скачать, проверить подпись и хеши, положить по SSH, from: "local".
            if (_status?.Update?.Version is { } version && Compat?.CanCall("update_install") != false)
            {
                Say("Роутер не достал витрину пакетов — загружаю обновление через этот компьютер…");
                job = await UpdateLocalAsync(version);
                if (job?.Ok == true) return await UpdatedAsync(job);
            }
            _updateFailed = true;
            Failed((job is { Running: true } ? "Обновление не завершилось" : "Обновить DpiShell на роутере не удалось")
                + (job is { Running: false, Message.Length: > 0 } ? ": " + job.Message : ""));
            return false;
        }
        catch (RouterException e) { Failed(RouterTexts.Friendly(e)); return false; }
        catch (Exception e) when (e is RouterSshException or HttpRequestException or DpiShell.Core.SecurityException or IOException or UnauthorizedAccessException)
        {
            Failed("Обновить через этот компьютер не удалось: " + (e is HttpRequestException ? "не скачались пакеты, проверьте интернет" : SshText(e)));
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
        var address = SetupAddress() ?? throw new RouterSshException(RouterSshFailure.Unreachable, "Не удалось узнать адрес роутера");
        var pw = SshPassword(null, false) ?? throw new RouterSshException(RouterSshFailure.AuthFailed, "Нет сохранённого пароля роутера — войдите заново");
        await using (var ssh = await Ssh.ConnectAsync(new RouterSshTarget(address), () => pw))
            await new RouterSsh(ssh).UploadUpdateAsync(files);
        return await UpdateFromAsync(RouterUpdateSource.Local, version);
    }

    // ---------- «Доступно обновление» и «Проверить обновления» ----------

    /// <summary>status.update.available: на вкладке «Роутер» — строка «Доступно обновление X», «Что нового», «Обновить».</summary>
    public bool UpdateAvailable => _status?.Update is { Available: true } && !Updating;
    public bool ShowUpdate => UpdateAvailable || Updating;
    /// <summary>Идёт — «Обновляю DpiShell на роутере… 1:23» (прошедшее время: видно, что не зависло); не завершилось — так и сказано.</summary>
    public string UpdateTitle => Updating ? $"Обновляю DpiShell на роутере… прошло {DateTime.UtcNow - _updateStarted:m\\:ss}"
        : _updateFailed ? $"Обновление до {_status?.Update?.Version} не завершилось" + (_status?.Version is { Length: > 0 } cur ? $" — работает прежняя {cur}" : "")
        : $"Доступно обновление {_status?.Update?.Version}";
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
        Say("Проверяю обновления DpiShell на роутере…");
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
            Say(_status?.Update is { Available: true } u ? $"Доступно обновление DpiShell для роутера: {u.Version}" : "Обновлений нет — на роутере последняя версия DpiShell");
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
            Say("DpiShell на роутере обновлён, но роутер ещё перезапускается — обновите вкладку через минуту");
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

        SetConnection($"Подключено, DpiShell {_status?.Version}", ok: true);
        Say(job.Message is { Length: > 0 } m ? m : $"DpiShell на роутере обновлён до {_status?.Version}");
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
                    ? new RouterUpdateJob("update", job?.From, false, true, $"DpiShell на роутере обновлён до {s.Version} (роутер не дописал итог)", Log: job?.Log)
                    : (job ?? new RouterUpdateJob("update", null, true, null, null)) with { Running = true };
            }
            if (job is not null && (sawRunning || job != before)) return job;
        }
        throw new RouterException(RouterFailure.NoResponse, "Обновление на роутере идёт дольше обычного — проверьте позже");
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
                    throw new RouterSshException(RouterSshFailure.Failed, "Удаление идёт дольше обычного — посмотрите позже: если DpiShell пропал с роутера, всё в порядке");
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
        if (!Found) SetConnection("DpiShell с роутера удалён");
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
        Steps = [new("Проверка и подготовка"), new("Проверка источника пакетов"), new("Пакеты DpiShell"), new("Ядро zapret"), new("Запуск")];
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
            foreach (var n in new[] { nameof(Working), nameof(CanEdit), nameof(CanCheck), nameof(CanInstall), nameof(CanTryInstall), nameof(InstallText), nameof(ShowSteps), nameof(IsDone), nameof(CanGoSelect), nameof(ShowCheck) })
                Raise(n);
        }
    }

    public bool Working => Stage is RouterInstallStage.Checking or RouterInstallStage.Installing;
    public bool CanEdit => !Working && Stage != RouterInstallStage.Done;
    public bool CanCheck => CanEdit;
    public bool ShowCheck => Stage != RouterInstallStage.Done;
    public bool CanInstall => Stage == RouterInstallStage.Checked && _verdict?.CanInstall == true;
    /// <summary>«Установить» активна всегда, пока можно править: непроверенный роутер она проверит сама.</summary>
    public bool CanTryInstall => CanEdit;
    /// <summary>После сбоя установки главная кнопка — «Повторить».</summary>
    public string InstallText => Stage == RouterInstallStage.Failed ? "Повторить" : "Установить";
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

    public const string TunnelText = "У роутера нет прямого доступа к пакетам или GitHub — установка пойдёт через интернет этого компьютера. Это нормально.";

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

    /// <summary>Строка хода → шаг диалога (0..4); null — служебная строка (только в журнал).</summary>
    public static int? StepOf(string line) => line switch
    {
        _ when line.StartsWith("Подключение") || line.StartsWith("Проверка роутера") || line.StartsWith("Интернет роутеру")
               || line.StartsWith("Ядро zapret: загрузка") || line.StartsWith("Загрузка ядра") => 0,
        _ when line.StartsWith("Ключ витрины") || line.StartsWith("Витрина пакетов") => 1,
        _ when line.StartsWith("Пакеты") => 2,
        _ when line.StartsWith("Ядро zapret") => 3,
        "Проверка" => 4,
        _ => null,
    };

    /// <summary>Шаг сценария из итога (step) → шаг диалога.</summary>
    static int StepOfResult(string step) => step switch { "key" or "feed" => 1, "packages" => 2, "core" => 3, "status" or "done" => 4, _ => 0 };

    static string FailText(RouterInstallResult r) => r.Step switch
    {
        "packages" => "Не удалось поставить пакеты DpiShell. Чаще всего роутер не достал до витрины пакетов — проверьте интернет на роутере и повторите.",
        "core" => "Не удалось загрузить ядро zapret на роутер. Повторите установку.",
        "key" => "Источник пакетов не прошёл проверку — установка отменена.",
        _ => r.Message,
    };

    void Step(int i, int state = RouterInstallStep.Running)
    {
        for (int k = 0; k < Steps.Count; k++)
            if (k < i && Steps[k].State != RouterInstallStep.Done) Steps[k].State = RouterInstallStep.Done;
        Steps[i].State = state;
    }

    void OnProgress(string line)
    {
        Log("# " + line);
        if (StepOf(line) is { } i)
        {
            Step(i);
            ProgressText = line;
        }
    }

    (RouterSshTarget Target, string Password)? Prepare(string? typed, bool noPassword)
    {
        ErrorText = "";
        var address = _router.SetupAddress();
        if (address is null) { ErrorText = "Не удалось узнать адрес роутера — укажите его в «Дополнительно»"; return null; }
        if (_router.SshPassword(typed, noPassword) is not { } pw)
        {
            ErrorText = "Введите пароль роутера или отметьте «Пароль не задан»";
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
        ProgressText = "Проверяю роутер…";
        Log($"Проверка роутера {p.Target.Host}");
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
            ? v.AlreadyInstalled ? $"Роутер подходит. DpiShell {v.InstalledVersion} уже стоит — установка обновит его." : $"Роутер подходит — можно ставить DpiShell {v.Release?.Version}."
            : "Роутер не подходит";
        var lines = v.Blockers.Select(b => "• " + b.Text)
            .Concat(v.Warnings.Where(w => w.Code is not ("tunnel" or "installed")).Select(w => "• " + w.Text));
        VerdictText = string.Join("\n", lines);
        TunnelNote = v.CanInstall && v.NeedsTunnel ? TunnelText : "";
        Log(VerdictTitle + (VerdictText.Length > 0 ? "\n" + VerdictText : ""));
        Raise(nameof(CanInstall));
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
            o = await _router.Flow().InstallAsync(p.Target, () => pw, new SyncProgress(OnProgress), _cts.Token);
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
            ErrorText = "Роутер не подходит — ничего не установлено.";
            return false;
        }
        Log(res.Message);
        if (!res.Ok)
        {
            Step(StepOfResult(res.Step), RouterInstallStep.Failed);
            Stage = RouterInstallStage.Failed;
            ErrorText = FailText(res);
            _router.Failed("Установка на роутер не удалась");
            return false;
        }
        foreach (var s in Steps) s.State = RouterInstallStep.Done;
        ResultText = $"DpiShell {res.Version} установлен. Подключаюсь…";
        var connected = await _router.ConnectAfterInstallAsync(p.Password);
        ResultText = connected
            ? $"DpiShell {res.Version} установлен и подключён. Осталось подобрать стратегию — обычно несколько минут."
            : $"DpiShell {res.Version} установлен. Подключиться не вышло: {_router.ConnectionText}. Нажмите «Войти» на вкладке «Роутер».";
        Stage = RouterInstallStage.Done;
        Raise(nameof(CanGoSelect));
        _router.Say(connected ? "DpiShell установлен на роутер и подключён." : "DpiShell установлен на роутер.");
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
        ErrorText = e is OperationCanceledException ? "Прервано." : RouterViewModel.SshText(e, host);
        Log("Ошибка: " + e.Message);
        Raise(nameof(HasDetails));
    }

    /// <summary>Закрыли диалог во время работы — прервать.</summary>
    public void Cancel() => _cts?.Cancel();

    sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
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
    /// <summary>Сохранить настройки (стратегию, свои адреса, расписание) — для установки заново.</summary>
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
        if (address is null) { ErrorText = "Не удалось узнать адрес роутера — укажите его в «Дополнительно»"; return false; }
        if (router.SshPassword(password, noPassword) is not { } pw)
        {
            ErrorText = "Введите пароль роутера или отметьте «Пароль не задан»";
            return false;
        }
        var target = new RouterSshTarget(address);
        Stage = RouterUninstallStage.Working;
        ProgressText = "Удаляю DpiShell с роутера…";
        _cts = new();
        router.SetBusy(true);
        RouterUninstallOutcome? o = null;
        Exception? sshError = null;
        try
        {
            bool viaApi = false;
            if (router.UninstallViaApi)
            {
                Log($"Удаление через API ({(KeepSettings ? "с сохранением настроек" : "полностью")})");
                try { (o, sshError) = await router.UninstallViaApiAsync(target, pw, KeepSettings, _cts.Token); viaApi = true; }
                catch (RouterException e) { Log("API: " + e.Message + " — удаляю по SSH"); }
            }
            if (!viaApi)
            {
                Log($"Удаление по SSH ({target.Host})");
                o = await router.Flow().UninstallAsync(target, () => pw, KeepSettings, _cts.Token);
            }
        }
        catch (Exception e) when (e is RouterSshException or IOException or OperationCanceledException)
        {
            Log("Ошибка: " + e.Message);
            router.SetBusy(false);
            ProgressText = "";
            Stage = RouterUninstallStage.Failed;
            ErrorText = e is OperationCanceledException ? "Прервано." : RouterViewModel.SshText(e, target.Host);
            Raise(nameof(HasDetails));
            return false;
        }
        router.SetBusy(false);
        ProgressText = "";

        if (o is null)
        {
            // Удаление запущено через API, а по SSH не зайти — итог не проверить.
            Log("SSH: " + sshError?.Message);
            ResultTitle = "Удаление запущено";
            LeftText = "Проверить, что осталось, не удалось: " + RouterViewModel.SshText(sshError!, target.Host);
            Clean = false;
        }
        else if (o.Result.Ok != true)
        {
            Log(o.Result.Message ?? "Удаление не удалось");
            Stage = RouterUninstallStage.Failed;
            ErrorText = "Удалить не получилось: " + (o.Result.Message ?? "роутер не сообщил причину");
            Raise(nameof(HasDetails));
            await router.AfterUninstallAsync();
            return false;
        }
        else
        {
            Log(o.Result.Message ?? "Удалено");
            var left = o.Left?.Left ?? [];
            Clean = o.Left?.Clean == true;
            ResultTitle = Clean ? "DpiShell удалён. Ничего не осталось." : "DpiShell удалён";
            var lines = left.Select(l => "• " + l.What + (string.IsNullOrEmpty(l.Detail) ? "" : " — " + l.Detail)).ToList();
            if (lines.Count > 0) lines.Insert(0, "Осталось на роутере:");
            if (o.Left?.Kept?.Settings == true) lines.Add("Настройки сохранены — вернутся при установке заново.");
            if (o.Left?.Kept?.HttpsDnsProxy == true) lines.Add("Пакет защищённого DNS оставлен: его ставили не через DpiShell.");
            if (o.Left is null) lines.Add("Проверить, что осталось, не удалось.");
            LeftText = string.Join("\n", lines);
        }
        Stage = RouterUninstallStage.Done;
        await router.AfterUninstallAsync();
        router.Say(ResultTitle);
        return true;
    }

    public void Cancel() => _cts?.Cancel();
}
