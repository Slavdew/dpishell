using System.Windows.Input;

namespace DpiShell.App;

/// <summary>
/// Тост внизу окна: итог обратимого действия и кнопка «Вернуть» (UX-аудит, «Опасные действия»: DNS этого ПК меняется
/// без подтверждения, но с быстрым путём назад). Сам скрывается через <see cref="ToastTime"/>; новый тост заменяет прежний.
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

    public void ShowToast(string text, string action = "", Func<Task>? run = null)
    {
        var id = ++_toastId;
        _toastRun = run;
        Toast = text;
        ToastAction = run is null ? "" : action;
        CommandManager.InvalidateRequerySuggested();
        _ = HideLaterAsync(id);
    }

    async Task HideLaterAsync(int id)
    {
        await Task.Delay(ToastTime);
        if (id == _toastId) HideToast();
    }

    void HideToast()
    {
        _toastId++;
        _toastRun = null;
        Toast = "";
        ToastAction = "";
    }
}
