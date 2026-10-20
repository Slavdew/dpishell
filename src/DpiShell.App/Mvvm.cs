using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace DpiShell.App;

/// <summary>
/// Строка-состояние, которая собирается при показе: хранится правило сборки (данные в замыкании), а не готовый текст,
/// поэтому после смены языка читается уже на новом (docs/localization.md).
/// </summary>
public sealed class LiveText
{
    Func<string> _f;
    string? _fixed;
    public LiveText(Func<string>? build = null) => _f = build ?? (static () => "");
    public string Value => _f();
    public bool IsEmpty => Value.Length == 0;
    public void Set(string text) { _fixed = text; _f = () => text; }
    public void Set(Func<string> build) { _fixed = null; _f = build; }

    /// <summary>Готовая строка (сообщение последнего действия, ошибка) — на языке окна, если это знакомая фраза; правило сборки и так читается заново.</summary>
    public void Retranslate()
    {
        if (_fixed is { Length: > 0 } t && L.Relocalize(t, own: true) is var n && n != t) Set(n);
    }
}

public abstract class Observable : INotifyPropertyChanged
{
    // Смена языка: все живые модели поднимают PropertyChanged("") — вычисляемые строки читаются заново (docs/localization.md).
    static readonly ConditionalWeakTable<Observable, object?> Live = new();
    static Observable() => L.Changed += () =>
    {
        foreach (var (o, _) in Live.ToArray())
        {
            // Сначала модель пересобирает то, что держит готовой строкой (из своих данных), затем читаются вычисляемые свойства.
            try { o.OnLanguageChanged(); } catch (Exception e) when (e is InvalidOperationException or System.IO.IOException or NotSupportedException or NullReferenceException or ObjectDisposedException) { LanguageHandlerFailed?.Invoke(o, e); }
            o.Raise(string.Empty);
        }
    };

    /// <summary>Пересборка строк модели при смене языка не удалась (модель уже закрыта, чужой поток): окну это не мешает, тесты следят.</summary>
    public static event Action<object, Exception>? LanguageHandlerFailed;

    /// <summary>Только для тестов: живые модели сейчас.</summary>
    public static HashSet<Observable> Snapshot() => [.. Live.Select(x => x.Key)];

    /// <summary>Только для тестов: забыть модели, появившиеся после <paramref name="before"/>. Закрытое окно теста держит модель в памяти ещё долго,
    /// а смена языка пересобирала бы и её.</summary>
    public static void ForgetSince(HashSet<Observable> before)
    {
        foreach (var o in Live.Select(x => x.Key).Where(o => !before.Contains(o)).ToArray()) Live.Remove(o);
    }

    /// <summary>Язык сменился: пересобрать строки, которые модель хранит готовыми (состояние, подписи списков), из данных. Сообщения последних действий не трогать.</summary>
    protected virtual void OnLanguageChanged() { }

    /// <summary>Сообщение последнего действия, собранное на прежнем языке, — на языке окна (если это знакомая фраза), иначе как есть.</summary>
    protected static void Retranslate(ref string field) { if (field.Length > 0) field = L.Relocalize(field, own: true); }

    protected Observable() => Live.AddOrUpdate(this, null);

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>Команда без библиотек. Доступность пересчитывается через CommandManager.</summary>
public sealed class RelayCommand(Action<object?> run, Func<object?, bool>? can = null) : ICommand
{
    public RelayCommand(Action run, Func<bool>? can = null) : this(_ => run(), can is null ? null : _ => can()) { }

    /// <summary>Куда отдавать ошибки асинхронных команд (строка состояния), чтобы они не терялись и не роняли окно.</summary>
    public static Action<Exception>? OnError { get; set; }

    public static RelayCommand Async(Func<Task> run, Func<bool>? can = null) => new(async () =>
    {
        try { await run(); }
        catch (Exception e) { OnError?.Invoke(e); }
    }, can);

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? p) => can?.Invoke(p) ?? true;
    public void Execute(object? p) => run(p);
}
