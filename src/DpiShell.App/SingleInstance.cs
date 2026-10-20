using System.Diagnostics;

namespace DpiShell.App;

/// <summary>
/// Одна копия окна: повторный запуск просит работающую показаться и ждёт подтверждения; не ответила за <see cref="Timeout"/>
/// (зависла, осталась без окна) — предложить завершить её и открыться самим. Раньше второй запуск молча закрывался.
/// </summary>
public sealed class SingleInstance
{
    public static string NotRespondingText => L.T("shell.single.not_responding");
    public static string KillFailedText => L.T("shell.single.kill_failed");

    /// <summary>Взять право единственной копии; true — других копий нет (или прежняя завершилась).</summary>
    public required Func<bool> Acquire { get; init; }
    /// <summary>Попросить работающую копию показать окно.</summary>
    public required Action SignalShow { get; init; }
    /// <summary>Ждать подтверждения «окно показано»; true — дождались.</summary>
    public required Func<TimeSpan, bool> WaitShown { get; init; }
    /// <summary>Вопрос «да/нет».</summary>
    public required Func<string, bool> Ask { get; init; }
    /// <summary>Завершить старые копии окна; true — завершены.</summary>
    public required Func<bool> KillOld { get; init; }
    /// <summary>Сообщение пользователю.</summary>
    public Action<string> Tell { get; init; } = _ => { };
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>true — запускаться этой копии; false — выйти (окно показала работающая, или пользователь отказался).</summary>
    public bool ShouldRun()
    {
        if (Acquire()) return true;
        SignalShow();
        if (WaitShown(Timeout)) return false;
        if (!Ask(NotRespondingText)) return false;
        if (KillOld() && Acquire()) return true;
        Tell(KillFailedText);
        return false;
    }

    /// <summary>Старые копии окна — тот же exe в этом же сеансе (помощник-служба — в сеансе 0, его не трогаем).</summary>
    public static bool KillOtherWindows()
    {
        using var me = Process.GetCurrentProcess();
        bool ok = true;
        foreach (var p in Process.GetProcessesByName(me.ProcessName))
        {
            using (p)
            {
                if (p.Id == me.Id || p.SessionId != me.SessionId) continue;
                try
                {
                    p.Kill();
                    ok &= p.WaitForExit(5000);
                }
                catch (Exception) { ok = false; }   // копия с правами администратора окну без прав не по силам
            }
        }
        return ok;
    }
}
