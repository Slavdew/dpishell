using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using DpiShell.Core.Helper;

namespace DpiShell.App;

/// <summary>Связь окна с помощником.</summary>
public enum HelperState
{
    /// <summary>Ещё не пробовали подключиться.</summary>
    Unknown,
    /// <summary>Службы помощника нет — режим просмотра; действия с правами предложат установку.</summary>
    NotInstalled,
    Connected,
    /// <summary>Помощник старее окна — читать можно, действия предложат обновление (тот же один запрос прав).</summary>
    Outdated,
    /// <summary>Служба есть, но не отвечает или связь оборвалась — «Переустановить».</summary>
    NotResponding,
    /// <summary>Помощник установлен другим пользователем этого компьютера.</summary>
    Denied,
}

public enum SetupResult
{
    Done,
    /// <summary>Пользователь отказал в запросе прав (UAC).</summary>
    Declined,
    Failed,
}

public sealed record SetupOutcome(SetupResult Result, string? Error = null);

/// <summary>Запуск установки/удаления помощника с правами. В тестах — подмена без UAC.</summary>
public interface IHelperSetup
{
    /// <param name="arguments">«--install-helper &lt;SID&gt;» или «--uninstall-helper».</param>
    Task<SetupOutcome> RunElevatedAsync(string arguments);
}

/// <summary>Сам DpiShell.exe через ShellExecute «runas» — единственный запрос прав (helper-protocol §2).</summary>
public sealed class ElevatedSelf : IHelperSetup
{
    const int ErrorCancelled = 1223;

    public async Task<SetupOutcome> RunElevatedAsync(string arguments)
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return new(SetupResult.Failed, L.T("shell.helper.exe_missing"));
        Process? p;
        try
        {
            p = Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
        }
        catch (Win32Exception e) when (e.NativeErrorCode == ErrorCancelled) { return new(SetupResult.Declined); }
        catch (Win32Exception e) { return new(SetupResult.Failed, e.Message); }
        if (p is null) return new(SetupResult.Failed, L.T("shell.helper.install_not_started"));
        using (p)
        {
            await p.WaitForExitAsync();
            return p.ExitCode == 0 ? new(SetupResult.Done)
                : new(SetupResult.Failed, L.T("shell.helper.install_exit", new { code = p.ExitCode }));
        }
    }
}

/// <summary>
/// Подключение к помощнику: держит канал открытым, пока работает окно (в режиме без службы помощник гасит winws,
/// когда окно отключилось). Установка/обновление/удаление — через <see cref="IHelperSetup"/> (один UAC).
/// </summary>
public sealed class HelperLink : IAsyncDisposable
{
    HelperClient? _client;
    bool _quiet;   // идёт установка/удаление: обрыв связи ожидаем

    /// <summary>Подключение (в тестах — к каналу фикстуры).</summary>
    public Func<CancellationToken, Task<HelperClient>> Connector { get; init; } = ct => HelperClient.ConnectAsync(null, ct);
    public IHelperSetup Setup { get; init; } = new ElevatedSelf();
    /// <summary>Версия окна: помощник старее — обновить.</summary>
    public string WindowVersion { get; init; } = HelperProtocol.AppVersion;
    /// <summary>Сколько ждать помощника после установки.</summary>
    public TimeSpan ConnectWait { get; init; } = TimeSpan.FromSeconds(20);
    /// <summary>SID пользователя окна: при вводе пароля другого администратора в UAC установка идёт от его имени.</summary>
    public Func<string> UserSid { get; init; } = () => WindowsIdentity.GetCurrent().User!.Value;
    /// <summary>Перевод событий канала (чужой поток) на поток окна.</summary>
    public Action<Action> Post { get; set; } = a => a();

    public HelperState State { get; private set; }
    /// <summary>Понятный текст, почему помощник недоступен (пусто — всё в порядке или не установлен).</summary>
    public string Problem { get; private set; } = "";

    /// <summary>Действия с правами можно выполнять.</summary>
    public bool Ready => State == HelperState.Connected && _client is { IsConnected: true };
    /// <summary>Клиент для чтения (и при устаревшем помощнике).</summary>
    public HelperClient? Reader => _client is { IsConnected: true } ? _client : null;
    public HelperClient? Client => Ready ? _client : null;

    public HelperStatus? Status { get; set; }
    public ResultsSnapshot? Results { get; set; }
    public HelperDetails? Details { get; set; }

    /// <summary>Состояние связи изменилось.</summary>
    public event Action? Changed;

    /// <summary>
    /// Перечитать у помощника: status, а полностью — ещё результаты и сведения. Помощника нет — ничего.
    /// Возвращает текст ошибки помощника (null — всё в порядке); обрыв связи отмечается сам.
    /// </summary>
    public async Task<string?> FetchAsync(bool full)
    {
        if (Reader is not { } c) return null;
        try
        {
            Status = await c.StatusAsync();
            if (!full) return null;
            Results = await c.ReadResultsAsync();
            Details = await c.DetailsAsync();
            return null;
        }
        catch (HelperUnavailableException e) { MarkLost(e); return null; }
        catch (HelperException e) { return e.Message; }
    }

    /// <summary>Подключиться без запроса прав. True — можно действовать.</summary>
    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        if (Ready) return true;
        Drop();
        try
        {
            var c = await Connector(ct);
            _client = c;
            c.Disconnected += OnDisconnected;
            if (IsOlder(c.HelperVersion, WindowVersion))
            {
                State = HelperState.Outdated;
                Problem = L.T("shell.helper.connect_outdated");
            }
            else if (c.MachineAllowed == false && !IsOlder(WindowVersion, c.HelperVersion))
            {
                // Та же версия, но другая сборка (API 1.14): действия на весь компьютер помощник не примет — переустановить из этого окна.
                // Окно старее помощника сюда не попадает: ему помощник сам скажет «обновите программу» на такие действия, остальное работает.
                State = HelperState.Outdated;
                Problem = L.T("shell.helper.connect_other_build");
            }
            else { State = HelperState.Connected; Problem = ""; }
        }
        catch (HelperUnavailableException e)
        {
            State = e.Reason == HelperUnavailableReason.NotInstalled ? HelperState.NotInstalled : HelperState.NotResponding;
            Problem = State == HelperState.NotInstalled ? "" : e.Message;
        }
        catch (HelperException e)
        {
            State = e.Code == HelperErrors.Denied ? HelperState.Denied : HelperState.NotResponding;
            Problem = e.Message;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Окно без помощника работает в режиме просмотра — что бы ни случилось при подключении.
            State = HelperState.NotResponding;
            Problem = L.T("shell.helper.not_responding", new { reason = e.Message });
        }
        Changed?.Invoke();
        return Ready;
    }

    public static bool IsOlder(string helper, string window) =>
        Version.TryParse(helper, out var h) && Version.TryParse(window, out var w) && h < w;

    void OnDisconnected(object? sender, EventArgs e)
    {
        if (_quiet || !ReferenceEquals(sender, _client)) return;
        Post(() =>
        {
            if (_quiet || !ReferenceEquals(sender, _client)) return;
            State = HelperState.NotResponding;
            Problem = L.T("shell.helper.stopped");
            Changed?.Invoke();
        });
    }

    /// <summary>Связь оборвалась при вызове — то же, что событие обрыва.</summary>
    public void MarkLost(HelperUnavailableException e)
    {
        if (_quiet) return;
        State = e.Reason == HelperUnavailableReason.NotInstalled ? HelperState.NotInstalled : HelperState.NotResponding;
        Problem = State == HelperState.NotInstalled ? "" : L.T("shell.helper.stopped");
        Changed?.Invoke();
    }

    void Drop()
    {
        var c = _client;
        _client = null;
        if (c is null) return;
        c.Disconnected -= OnDisconnected;
        c.Dispose();
    }

    /// <summary>
    /// Установить или обновить помощника: один запрос прав, затем ждать, пока он ответит.
    /// Done — подключены и можно действовать.
    /// </summary>
    public async Task<SetupOutcome> InstallAsync(CancellationToken ct = default)
    {
        _quiet = true;
        try
        {
            var r = await Setup.RunElevatedAsync("--install-helper " + UserSid());
            if (r.Result != SetupResult.Done) return r;
            Drop();
            var until = DateTime.UtcNow + ConnectWait;
            while (true)
            {
                if (await ConnectAsync(ct)) return r;
                if (DateTime.UtcNow >= until) return new(SetupResult.Failed, Problem.Length > 0 ? Problem : L.T("shell.helper.installed_silent"));
                await Task.Delay(500, ct);
            }
        }
        finally { _quiet = false; }
    }

    /// <summary>«Удалить помощника» (тоже один запрос прав).</summary>
    public async Task<SetupOutcome> UninstallAsync()
    {
        _quiet = true;
        try
        {
            var r = await Setup.RunElevatedAsync("--uninstall-helper");
            if (r.Result != SetupResult.Done) return r;
            Drop();
            State = HelperState.NotInstalled;
            Problem = "";
            Status = null; Results = null; Details = null;
            Changed?.Invoke();
            return r;
        }
        finally { _quiet = false; }
    }

    public ValueTask DisposeAsync()
    {
        _quiet = true;
        Drop();
        return ValueTask.CompletedTask;
    }
}
