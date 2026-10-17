namespace DpiShell.App;

/// <summary>
/// Выход из программы, который завершает процесс при любом исходе: сбой любого шага уборки пишется в журнал и не мешает
/// остальным, в конце — Shutdown, а если процесс через <see cref="Grace"/> всё ещё жив (что-то держит его: модальный
/// диалог, чужой поток) — жёсткое завершение. Раньше исключение в выходе терялось в отброшенной задаче, и процесс без окна
/// и без значка в трее жил часами, а повторный запуск молча закрывался (одна копия).
/// </summary>
public sealed class AppExit
{
    /// <summary>Журнал сбоев выхода.</summary>
    public Action<Exception> Log { get; init; } = _ => { };
    /// <summary>Обычное завершение (Application.Shutdown).</summary>
    public Action Shutdown { get; init; } = () => { };
    /// <summary>Жёсткое завершение процесса (Environment.Exit).</summary>
    public Action<int> Kill { get; init; } = Environment.Exit;
    /// <summary>Сколько ждать обычного завершения до жёсткого.</summary>
    public TimeSpan Grace { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// prepare — то, что ждёт (вопросы, конец текущей операции); cleanup — уборка, каждый шаг сам по себе.
    /// Задача не падает: всё, что пошло не так, — в журнал.
    /// </summary>
    public async Task RunAsync(Func<Task> prepare, params Action[] cleanup)
    {
        try { await prepare(); }
        catch (Exception ex) { Log(ex); }
        foreach (var step in cleanup)
        {
            try { step(); }
            catch (Exception ex) { Log(ex); }
        }
        // Страховка — на пуле потоков: сработает, даже если поток окна завис или процесс держит чужой поток.
        _ = Task.Delay(Grace).ContinueWith(_ => Kill(0), TaskScheduler.Default);
        try { Shutdown(); }
        catch (Exception ex)
        {
            Log(ex);
            Kill(1);
        }
    }
}
