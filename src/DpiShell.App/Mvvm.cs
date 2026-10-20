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
    public LiveText(Func<string>? build = null) => _f = build ?? (static () => "");
    public string Value => _f();
    public bool IsEmpty => Value.Length == 0;
    public void Set(string text) => _f = () => text;
    public void Set(Func<string> build) => _f = build;
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
            try { o.OnLanguageChanged(); } catch (Exception e) when (e is InvalidOperationException or System.IO.IOException or NotSupportedException or NullReferenceException or ObjectDisposedException) { }
            o.Raise(string.Empty);
        }
    };

    /// <summary>Язык сменился: пересобрать строки, которые модель хранит готовыми (состояние, подписи списков), из данных. Сообщения последних действий не трогать.</summary>
    protected virtual void OnLanguageChanged() { }

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
