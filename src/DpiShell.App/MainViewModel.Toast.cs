using System.Windows.Input;

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

    /// <summary>Ошибка операции: и в нижнюю строку (там история), и тостом — чтобы не пропустили.</summary>
    public void Fail(string text)
    {
        Message = text;
        if (text.Length == 0) return;
        ShowToast(text);
        ToastError = true;
    }

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
        // Человек читает (мышь над тостом) — ждём, пока уберёт, и ещё немного.
        while (ToastHovered && id == _toastId) await Task.Delay(ToastTime / 4);
        if (id == _toastId) HideToast();
    }

    void HideToast()
    {
        _toastId++;
        _toastRun = null;
        Toast = "";
        ToastAction = "";
        ToastError = false;
    }
}
