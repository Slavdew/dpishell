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
    static readonly Lazy<HttpClient> SharedHttp = new(() => new HttpClient { Timeout = TimeSpan.FromMinutes(5) });
    /// <summary>Загрузка ядра обхода и пакетов обновления на этот ПК.</summary>
    public HttpClient Http { get; set; } = SharedHttp.Value;
    /// <summary>Готовые копии архива ядра на ПК (проверяются по хешу).</summary>
    public IReadOnlyList<string> CoreCandidates { get; set; } = [];
    /// <summary>Выпуски для роутера (data/router-releases.json из поставки).</summary>
    public Func<RouterReleases>? Releases { get; set; }

    internal RouterInstallFlow Flow() =>
        new(SshConnector, (Releases ?? (() => RouterReleases.Load(Path.Combine(_shell.Paths.Seed, "router-releases.json"))))(), new RouterCoreArchive(RouterCoreManifest.Embedded(), _settings.RouterCoreCache), Http) { LocalCoreCandidates = CoreCandidates };

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
        RouterSshException { Failure: RouterSshFailure.HostKeyChanged } => HostKeyCheck.ChangedText,
        RouterSshException s => s.Message,
        HttpRequestException => "Не удалось скачать ядро обхода на этот компьютер. Проверьте интернет и повторите.",
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
        for (int i = 0; i < 4; i++)
        {
            if (i > 0) await Task.Delay(PollInterval * 2);
            if (await ConnectAsync(password, noPassword: password.Length == 0)) return true;
            if (_detection?.Found == RouterFound.Ok && ConnectionText.Contains("пароль")) return false;
        }
        return false;
    }

    internal void SetBusy(bool busy) => Busy = busy;

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
        Steps = [new("Проверка и подготовка"), new("Ключ витрины пакетов"), new("Пакеты DpiShell"), new("Ядро обхода"), new("Запуск")];
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
            foreach (var n in new[] { nameof(Working), nameof(CanEdit), nameof(CanCheck), nameof(CanInstall), nameof(ShowSteps), nameof(IsDone), nameof(CanGoSelect), nameof(ShowCheck) })
                Raise(n);
        }
    }

    public bool Working => Stage is RouterInstallStage.Checking or RouterInstallStage.Installing;
    public bool CanEdit => !Working && Stage != RouterInstallStage.Done;
    public bool CanCheck => CanEdit;
    public bool ShowCheck => Stage != RouterInstallStage.Done;
    public bool CanInstall => Stage == RouterInstallStage.Checked && _verdict?.CanInstall == true;
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
               || line.StartsWith("Ядро обхода: загрузка") || line.StartsWith("Загрузка ядра") => 0,
        _ when line.StartsWith("Ключ витрины") || line.StartsWith("Витрина пакетов") => 1,
        _ when line.StartsWith("Пакеты") => 2,
        _ when line.StartsWith("Ядро обхода") => 3,
        "Проверка" => 4,
        _ => null,
    };

    /// <summary>Шаг сценария из итога (step) → шаг диалога.</summary>
    static int StepOfResult(string step) => step switch { "key" or "feed" => 1, "packages" => 2, "core" => 3, "status" or "done" => 4, _ => 0 };

    static string FailText(RouterInstallResult r) => r.Step switch
    {
        "packages" => "Не удалось поставить пакеты DpiShell. Чаще всего роутер не достал до витрины пакетов — проверьте интернет на роутере и повторите.",
        "core" => "Не удалось загрузить ядро обхода на роутер. Повторите установку.",
        "key" => "Ключ витрины пакетов не совпал — установка отменена.",
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
        if (!CanInstall || Prepare(password, noPassword) is not { } p) return false;
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
                                      or System.Security.SecurityException or InvalidDataException)
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
            ? $"DpiShell {res.Version} установлен и подключён. Осталось подобрать стратегию — это 2–3 минуты."
            : $"DpiShell {res.Version} установлен. Подключиться не вышло: {_router.ConnectionText}. Нажмите «Войти» на вкладке «Роутер».";
        Stage = RouterInstallStage.Done;
        Raise(nameof(CanGoSelect));
        _router.Say(connected ? "DpiShell установлен на роутер и подключён." : "DpiShell установлен на роутер.");
        return true;
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
