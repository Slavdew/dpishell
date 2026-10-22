using System.Windows.Input;
using DpiShell.Core;

namespace DpiShell.App;

/// <summary>
/// Тост внизу окна: итог обратимого действия и кнопка «Вернуть» (UX-аудит, «Опасные действия»: DNS этого ПК меняется
/// без подтверждения, но с быстрым путём назад) и ошибки операций (UX-аудит 2, п. 16: в нижней строке их не замечают).
/// Сам скрывается через <see cref="ToastTime"/>, но не пока над ним мышь; новый тост заменяет прежний.
/// </summary>
public sealed partial class MainViewModel
{
    string _toast = "", _toastAction = "";
    Func<Task>? _toastRun;
    int _toastId;

    public string Toast { get => _toast; private set { if (Set(ref _toast, value)) Raise(nameof(HasToast)); } }
    public bool HasToast => Toast.Length > 0;
    /// <summary>Подпись кнопки тоста («Вернуть»); пусто — тост без кнопки.</summary>
    public string ToastAction { get => _toastAction; private set { if (Set(ref _toastAction, value)) Raise(nameof(HasToastAction)); } }
    public bool HasToastAction => ToastAction.Length > 0;

    bool _toastError;
    /// <summary>Тост — об ошибке: красная рамка, без «Вернуть».</summary>
    public bool ToastError { get => _toastError; private set => Set(ref _toastError, value); }

    /// <summary>Мышь над тостом (ставит окно): пока она там, тост не скрывается.</summary>
    public bool ToastHovered { get; set; }

    /// <summary>Сколько тост виден (в тестах — короче).</summary>
    public TimeSpan ToastTime { get; set; } = TimeSpan.FromSeconds(12);

    public ICommand ToastCmd => _toastCmd ??= RelayCommand.Async(async () =>
    {
        var run = _toastRun;
        HideToast();
        if (run is not null) await run();
    }, () => !Busy && _toastRun is not null);
    ICommand? _toastCmd;

    public ICommand CloseToastCmd => _closeToastCmd ??= new RelayCommand(HideToast);
    ICommand? _closeToastCmd;

    /// <summary>Показать тост; возвращает его номер — по нему <see cref="UpdateToast"/> дописывает итог, если тост ещё тот же.</summary>
    public int ShowToast(string text, string action = "", Func<Task>? run = null)
    {
        var id = ++_toastId;
        _toastRun = run;
        Toast = text;
        ToastAction = run is null ? "" : action;
        ToastError = false;
        CommandManager.InvalidateRequerySuggested();
        _ = HideLaterAsync(id);
        return id;
    }

    /// <summary>
    /// Ошибка операции — одно место для всего окна: в нижнюю строку (там история) и тостом, чтобы не пропустили; у тоста — «Подробнее»
    /// (ошибка, действие, время, версии) и «Скопировать» (личное скрыто, как в отчёте). <paramref name="error"/> — исключение, если оно есть.
    /// </summary>
    public void Fail(string text) => Fail(text, null);

    public void Fail(string text, Exception? error)
    {
        Message = text;
        if (text.Length == 0) return;
        ShowToast(text);
        ToastError = true;
        LastError = new ErrorInfo(text, BusyText.Length > 0 ? BusyText : null, DateTime.Now, ErrorInfo.Describe(error));
    }

    ErrorInfo? _lastError;
    /// <summary>Последняя ошибка (для «Подробнее»).</summary>
    public ErrorInfo? LastError
    {
        get => _lastError;
        private set { _lastError = value; ErrorDetailsOpen = false; Raise(); Raise(nameof(ErrorDetailsText)); }
    }

    bool _errorDetailsOpen;
    /// <summary>Раскрыто «Подробнее» в тосте ошибки: тост не скрывается, пока раскрыто.</summary>
    public bool ErrorDetailsOpen
    {
        get => _errorDetailsOpen;
        set { if (Set(ref _errorDetailsOpen, value)) { Raise(nameof(ErrorDetailsLabel)); if (value) Raise(nameof(ErrorDetailsText)); } }
    }

    public string ErrorDetailsLabel => L.T(ErrorDetailsOpen ? "shell.error.hide" : "shell.error.details");

    /// <summary>Полное описание последней ошибки: то, что показывает «Подробнее» и кладёт в буфер «Скопировать».</summary>
    public string ErrorDetailsText => LastError is { } e ? ErrorReport.Build(e, VersionLines(), PrivateNames()) : "";

    /// <summary>Версии окна, помощника и роутера — строками отчёта.</summary>
    IEnumerable<string> VersionLines()
    {
        string Or(string? s) => string.IsNullOrWhiteSpace(s) ? L.T("diag.report.unknown") : s.Trim();
        yield return L.T("diag.report.v.app", new { v = AppVersionText });
        yield return L.T("diag.report.v.helper", new { v = Helper.State == HelperState.NotInstalled ? L.T("shell.error.none") : Or(Helper.Reader?.HelperVersion) });
        yield return L.T("shell.error.router", new { v = Router.Found ? Or(Router.PackageVersionText) : L.T("shell.error.none") });
        yield return L.T("diag.report.v.windows", new { v = System.Runtime.InteropServices.RuntimeInformation.OSDescription });
    }

    public ICommand ErrorDetailsCmd => _errorDetailsCmd ??= new RelayCommand(() => ErrorDetailsOpen = !ErrorDetailsOpen, () => LastError is not null);
    ICommand? _errorDetailsCmd;

    public ICommand CopyErrorCmd => _copyErrorCmd ??= new RelayCommand(() =>
    {
        if (LastError is null) return;
        try { CopyText(ErrorDetailsText); Message = L.T("shell.error.copied"); }
        catch (System.Runtime.InteropServices.ExternalException) { Message = L.T("home.clipboard_busy"); }
    }, () => LastError is not null);
    ICommand? _copyErrorCmd;

    /// <summary>Дописать в тот же тост (итог проверки после смены DNS); срок показа — заново. Тост уже другой или закрыт — ничего.</summary>
    public void UpdateToast(int id, string text)
    {
        if (id != _toastId || !HasToast) return;
        Toast = text;
        _ = HideLaterAsync(++_toastId);
    }

    async Task HideLaterAsync(int id)
    {
        await Task.Delay(ToastTime);
        // Человек читает (мышь над тостом или раскрыто «Подробнее») — ждём, пока уберёт, и ещё немного.
        while ((ToastHovered || ErrorDetailsOpen) && id == _toastId) await Task.Delay(ToastTime / 4);
        if (id == _toastId) HideToast();
    }

    void HideToast()
    {
        _toastId++;
        _toastRun = null;
        Toast = "";
        ToastAction = "";
        ToastError = false;
        ErrorDetailsOpen = false;
    }
}
