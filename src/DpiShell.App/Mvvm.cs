using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace DpiShell.App;

public abstract class Observable : INotifyPropertyChanged
{
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
