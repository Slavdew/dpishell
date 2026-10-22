using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using DpiShell.Core;

namespace DpiShell.App;

/// <summary>Шаг «Удалить DpiShell с этого ПК»: подпись, итог (ok — готово, fail — не удалось, остальное — серым) и пояснение.</summary>
public sealed class UninstallStep(string titleKey) : Observable
{
    string _statusKey = "settings.uninstall.st.wait", _mark = "", _note = "";

    public string Title => L.T(titleKey);
    public string Status => L.T(_statusKey);
    /// <summary>ok, fail, skip, run — или пусто (ждёт).</summary>
    public string Mark => _mark;
    public string Note => _note;
    public bool HasNote => _note.Length > 0;

    void SetState(string status, string mark, string note)
    {
        _statusKey = status; _mark = mark; _note = note;
        Raise(nameof(Status)); Raise(nameof(Mark)); Raise(nameof(Note)); Raise(nameof(HasNote));
    }

    public void Run() => SetState("settings.uninstall.st.run", "run", "");
    public void Done(string note = "") => SetState("settings.uninstall.st.done", "ok", note);
    public void Skip(string note) => SetState("settings.uninstall.st.skip", "skip", note);
    public void Fail(string note) => SetState("settings.uninstall.st.fail", "fail", note);
    public void Stopped() => SetState("settings.uninstall.st.stopped", "", "");

    protected override void OnLanguageChanged()
    {
        Raise(nameof(Title)); Raise(nameof(Status));
    }
}

/// <summary>
/// «Настройки» → «Очистка и удаление» → «Удалить DpiShell с этого ПК…» (аудит, С-25): по шагам «Отменить всё» → служба оптимизации → помощник
/// вместе с данными программы (%ProgramData%\DpiShell, <c>--uninstall-helper --purge</c>), затем — какие папки стереть вручную: папку программы
/// (в ней работает само окно) и %LocalAppData%\DpiShell (настройки окна и журналы). Удаляется только своё: точные пути, по ссылкам не заходя.
/// Шаг не удался — дальше не идём: повтор той же кнопкой.
/// </summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<UninstallStep> UninstallSteps { get; } = [];
    public bool HasUninstallSteps => UninstallSteps.Count > 0;

    string _uninstallLeft = "";
    /// <summary>Какие папки осталось стереть вручную (после удачного удаления).</summary>
    public string UninstallLeftText { get => _uninstallLeft; private set { if (Set(ref _uninstallLeft, value)) Raise(nameof(HasUninstallLeft)); } }
    public bool HasUninstallLeft => UninstallLeftText.Length > 0;

    /// <summary>Пакет на роутере — отдельно (только если роутер с DpiShell найден).</summary>
    public string UninstallRouterText => HasUninstallRouterText ? L.T("settings.uninstall.router") : "";
    public bool HasUninstallRouterText => Router.Found;

    /// <summary>Папка программы (в тестах — подмена).</summary>
    public Func<string> ProgramFolder { get; set; } = () => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
    /// <summary>Папка окна: настройки, журнал окна, журналы (в тестах — подмена).</summary>
    public Func<string> WindowFolder { get; set; } = () => WindowSettings.DefaultDir;

    public ICommand UninstallPcCmd => _uninstallPcCmd ??= RelayCommand.Async(async () => await UninstallPcAsync(), () => !Busy && !IsLoading);
    ICommand? _uninstallPcCmd;

    public ICommand CopyUninstallLeftCmd => _copyUninstallLeftCmd ??= new RelayCommand(() =>
    {
        try { CopyText(UninstallLeftText); Message = L.T("settings.uninstall.copied"); }
        catch (System.Runtime.InteropServices.ExternalException) { Fail(L.T("home.clipboard_busy")); }
    }, () => HasUninstallLeft);
    ICommand? _copyUninstallLeftCmd;

    /// <summary>Все шаги по очереди. true — дошли до конца (остались только папки вручную).</summary>
    public async Task<bool> UninstallPcAsync()
    {
        var data = _shell.Paths.Root;
        if (!Confirm(L.T("settings.uninstall.confirm", new { data }))) return false;
        var undo = new UninstallStep("settings.uninstall.step.undo");
        var service = new UninstallStep("settings.uninstall.step.service");
        var helper = new UninstallStep("settings.uninstall.step.helper");
        UninstallSteps.Clear();
        foreach (var s in new[] { undo, service, helper }) UninstallSteps.Add(s);
        UninstallLeftText = "";
        Raise(nameof(HasUninstallSteps)); Raise(nameof(UninstallRouterText)); Raise(nameof(HasUninstallRouterText));
        Message = L.T("settings.uninstall.busy");

        // Шаг не удался: остальные — «не выполнено». Ошибку уже показал сам шаг (тост с «Подробнее»); иначе — строка состояния.
        bool Stop(string? error, params UninstallStep[] rest)
        {
            foreach (var s in rest) s.Stopped();
            if (error is { Length: > 0 }) Fail(error);
            else if (!ToastError) Message = L.T("settings.uninstall.stopped");
            return false;
        }

        // 1. Отменить всё — пока помощник на месте: журнал системных изменений хранит он.
        undo.Run();
        await Changes.RefreshAsync();
        if (Changes.Count == 0) undo.Skip(L.T("settings.changes.summary_none"));
        else
        {
            if (!await Changes.UndoAllAsync(ask: false)) { undo.Fail(""); return Stop(null, service, helper); }
            if (Changes.LastUndoAll.Any(i => i.Status == UndoStatus.Failed))
            {
                undo.Fail(Changes.Summary);
                if (!Confirm(L.T("settings.uninstall.partial_confirm", new { summary = Changes.Summary }))) return Stop(null, service, helper);
            }
            else undo.Done(Changes.Summary);
        }
        // Ключ автозапуска окна — и без записи в журнале (старые версии): после удаления окну запускаться не с чего.
        Autostart.Enabled = false;

        // 2. Служба оптимизации и драйвер — через помощника.
        service.Run();
        if (Helper.State == HelperState.NotInstalled) service.Skip(L.T("settings.uninstall.note.no_helper"));
        else
        {
            bool ok = await WithHelper(L.T("settings.remove_service.busy"), async c =>
            {
                await Router.JointShutdownAsync();
                await c.RemoveAllAsync();
            });
            if (!ok) { service.Fail(_error ?? ""); return Stop(null, helper); }
            service.Done();
        }

        // 3. Помощник и данные программы (%ProgramData%\DpiShell) — один запрос прав.
        helper.Run();
        if (Helper.State == HelperState.NotInstalled && !Directory.Exists(data)) helper.Skip(L.T("settings.uninstall.note.no_data"));
        else
        {
            Busy = true; BusyText = L.T("shell.helper.busy_uninstall"); UpdateStatus();
            SetupOutcome r;
            try { r = await Helper.UninstallAsync(purge: true); }
            catch (Exception e) { r = new(SetupResult.Failed, Texts.Friendly(e)); }
            finally { Busy = false; BusyText = ""; UpdateStatus(); }
            switch (r.Result)
            {
                case SetupResult.Declined: helper.Fail(L.T("settings.uninstall.note.declined")); return Stop(null);
                case SetupResult.Failed: helper.Fail(r.Error ?? ""); return Stop(L.T("shell.helper.uninstall_failed", new { error = r.Error }));
            }
            helper.Done();
        }

        UninstallLeftText = string.Join(Environment.NewLine,
            L.T("settings.uninstall.left_program", new { path = ProgramFolder() }),
            L.T("settings.uninstall.left_window", new { path = WindowFolder() }));
        Message = L.T("settings.uninstall.done");
        await RefreshStatusAsync();
        ReloadChoices();
        Net.Refresh();
        await Hosts.RefreshAsync();
        return true;
    }
}
