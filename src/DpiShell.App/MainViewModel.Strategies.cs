using System.IO;
using System.Windows;
using System.Windows.Input;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.App;

/// <summary>
/// Свои стратегии (architecture.md «Свои стратегии»): «Скопировать стратегию» (строка <c>dpishell:1:…</c>), «Вставить стратегию»
/// (строка DpiShell или запуск winws из .bat Flowseal), «Изменить» (готовые — копией), «Удалить». Хранит помощник
/// (strategy_add / strategy_delete): папка программы закрыта для окна. Вставленная и собранная руками стратегия сначала проходит
/// проверку сайтов («Проверить»), «Применить» — после неё или с подтверждением.
/// </summary>
public sealed partial class MainViewModel
{
    public ICommand CopyStrategyCmd { get; private set; } = null!;
    public ICommand PasteStrategyCmd { get; private set; } = null!;
    public ICommand EditStrategyCmd { get; private set; } = null!;
    public ICommand DeleteStrategyCmd { get; private set; } = null!;
    public ICommand CheckStrategyCmd { get; private set; } = null!;

    /// <summary>Текст из буфера обмена (в тестах — подмена); null — там не текст.</summary>
    public Func<string?> ReadText { get; set; } = () => Clipboard.ContainsText() ? Clipboard.GetText() : null;

    /// <summary>Окно правки (в тестах — подмена): true — «Сохранить».</summary>
    public Func<StrategyEditor, bool> EditStrategy { get; set; } = StrategyEditView.ShowDialog;

    void InitStrategies()
    {
        CopyStrategyCmd = new RelayCommand(p => CopyStrategy(IdOf(p)));
        PasteStrategyCmd = RelayCommand.Async(PasteStrategyAsync, () => !Busy && !IsLoading);
        EditStrategyCmd = new RelayCommand(p => _ = EditStrategyAsync(IdOf(p)), _ => !Busy && !IsLoading);
        DeleteStrategyCmd = new RelayCommand(p => _ = DeleteStrategyAsync(IdOf(p)), _ => !Busy && !IsLoading);
        CheckStrategyCmd = new RelayCommand(p => _ = CheckOneAsync(IdOf(p) ?? PendingCheckId ?? SelectedScore?.Id), _ => !Busy && !IsLoading);
    }

    static string? IdOf(object? p) => p switch { StrategyChoice c => c.Id, ScoreRow r => r.Id, string s => s, _ => null };

    /// <summary>Стратегия по id: готовые — из каталога окна, свои и найденные — у помощника.</summary>
    Strategy? StrategyOf(string? id) =>
        id is null ? null : Helper.Details?.Found.FirstOrDefault(x => x.Id == id) ?? _shell.Catalog.Find(id);

    /// <summary>Файл фейка есть в данных программы (окно читает поставку; помощник проверяет ещё раз у себя).</summary>
    bool FakeExists(string name) => File.Exists(Path.Combine(_shell.Paths.Seed, "fake", name)) || File.Exists(Path.Combine(_shell.Paths.Fake, name));

    string DisplayTitleOf(Strategy s) => StrategyNaming.DisplayTitle(s.Title, s.Roles);

    // ---------- скопировать ----------

    public void CopyStrategy(string? id)
    {
        if (StrategyOf(id) is not { } s) { Fail(L.T("select.unavailable")); return; }
        try
        {
            CopyText(StrategyExchange.Encode(DisplayTitleOf(s), s.Roles));
            ShowToast(L.T("select.copied"));
        }
        catch (StrategyFormatException e) { Fail(e.Message + "."); }
        catch (System.Runtime.InteropServices.ExternalException) { Fail(L.T("select.clipboard_busy")); }
    }

    // ---------- вставить ----------

    public async Task PasteStrategyAsync()
    {
        string? text;
        try { text = ReadText(); }
        catch (System.Runtime.InteropServices.ExternalException) { Fail(L.T("select.clipboard_read_busy")); return; }
        ParsedStrategy parsed;
        try { parsed = StrategyExchange.Parse(text, FakeExists); }
        catch (StrategyFormatException e) { Fail(L.T("select.paste.failed", new { reason = e.Message })); return; }
        await SavePersonalAsync(parsed.Title, parsed.Roles, "pasted", "select.paste.pasted");
    }

    /// <summary>Сохранить у помощника и показать на «Подборе» как не проверенную: дальше «Проверить».</summary>
    async Task SavePersonalAsync(string title, IReadOnlyDictionary<string, string> roles, string source, string resultKey)
    {
        Strategy? added = null;
        if (!await WithHelper(L.T("select.saving"), async c =>
            {
                added = await c.StrategyAddAsync(title, roles, source);
                await FetchAsync(full: true);
            }) || added is null)
            return;
        ReloadChoices();
        PresentUnchecked(added, resultKey);
    }

    string? _pendingCheck;
    /// <summary>Стратегия, ждущая проверки сайтов («Проверить» в карточке итога).</summary>
    public string? PendingCheckId { get => _pendingCheck; private set { if (Set(ref _pendingCheck, value)) Raise(nameof(HasPendingCheck)); } }
    public bool HasPendingCheck => _pendingCheck is not null;

    /// <param name="resultKey">Ключ строки итога с {name}: «Вставлена: …» или «Сохранена: …».</param>
    void PresentUnchecked(Strategy s, string resultKey)
    {
        var title = DisplayTitleOf(s);
        _titles[s.Id] = (s.Title, s.Roles);
        var row = new ScoreRow(new StrategyScore(s, [], 0, 0), Names, StrategyTitle);
        var old = Scores.FirstOrDefault(r => r.Id == s.Id);
        if (old is not null) Scores.Remove(old);
        Scores.Insert(0, row);
        SelectedScore = row;
        ResultSummary = ""; ResultExpanded = false;
        SetBest(null);
        OfferDeep = false;
        PendingCheckId = s.Id;
        ResultText = L.T(resultKey, new { name = title });
        ResultHint = L.T("select.pending_hint");
        Page = Pages.Select;
    }

    // ---------- проверить только её ----------

    public async Task CheckOneAsync(string? id)
    {
        if (id is null || Busy) return;
        if (await NeedHelperAsync() is not { } c) return;
        if (!await SaveDraftBeforeSelectAsync()) return;
        if (TakeGroups() is not { } ids) return;
        if (IsRunning && !Confirm(L.T("select.confirm.pause_check"))) return;
        if (!await RouterGateAsync(select: true)) return;

        var title = StrategyTitle(id) is { Length: > 0 } t ? t : id;
        ResultText = L.T("select.checking_one", new { name = title });
        ResultHint = "";
        SetBest(null);
        SelectProgressMax = 1; SelectProgress = 0; SelectIndeterminate = true;
        SelectProgressText = L.T("select.preparing");
        using var cts = new CancellationTokenSource();
        _selectCts = cts;
        Selecting = true;
        var progress = new Progress<SelectorProgress>(p => { SelectProgressText = p.Message; UpdateStatus(); });
        try
        {
            await Guard(L.T("select.busy.check"), async () =>
            {
                try { ShowOneReport(id, await c.StrategyCheckAsync(id, ids, progress, cts.Token)); }
                catch (OperationCanceledException) { ResultText = L.T("select.check.stopped"); PendingCheckId = id; }
            });
        }
        finally
        {
            _selectCts = null;
            Selecting = false;
            SelectIndeterminate = false;
            SelectProgressText = "";
            UpdateStatus();
        }
        await RefreshStatusAsync();
        await RouterResumeAsync();
    }

    void ShowOneReport(string id, SelectionReport r)
    {
        var title = StrategyTitle(id);
        if (r.Scores.FirstOrDefault() is not { } score)
        {
            ResultText = L.T(r.Cancelled ? "select.check.stopped" : r.NothingBlocked ? "select.check.nothing_blocked" : "select.check.unable");
            PendingCheckId = id;
            return;
        }
        var row = new ScoreRow(score, Names, StrategyTitle);
        var at = Scores.ToList().FindIndex(x => x.Id == id);
        if (at >= 0) Scores[at] = row; else Scores.Insert(0, row);
        SelectedScore = row;
        PendingCheckId = null;
        _ = ReloadChoicesAsync();
        if (row.Usable)
        {
            SetBest(id);
            ResultText = L.T("select.checked.ok", new { name = title, share = row.Share });
            ResultHint = L.T("select.checked.hint", new { groups = row.Groups });
        }
        else
        {
            ResultText = L.T("select.checked.bad", new { name = title });
            ResultHint = row.ErrorFull is { Length: > 0 } e ? e + "." : L.T("select.checked.none");
        }
    }

    // ---------- применить без проверки ----------

    public async Task ApplySelectedAsync()
    {
        if (SelectedScore is not { } row) return;
        if (row.Unchecked && !Confirm(L.T("select.confirm.unchecked"))) return;
        await ApplyAsync(row.Id);
    }

    /// <summary>Выбор в списке на главной: своя или вставленная стратегия без единой проверки — только с подтверждением.</summary>
    bool ConfirmUnchecked(string id)
    {
        if (Helper.Status?.Strategies.FirstOrDefault(s => s.Id == id) is not { } info || !UserStrategies.IsPersonal(info.Source)) return true;
        if (Helper.Results?.Stats.TryGetValue(id, out var st) == true && st.Runs > 0) return true;
        return Confirm(L.T("home.strategy.unchecked_confirm", new { name = StrategyTitle(id) }));
    }

    // ---------- изменить ----------

    public async Task EditStrategyAsync(string? id)
    {
        if (StrategyOf(id) is not { } s) { Fail(L.T("select.unavailable")); return; }
        bool copy = !UserStrategies.IsPersonal(s.Source);
        var shown = DisplayTitleOf(s);
        var first = copy ? L.T("select.edit.copy_title", new { name = shown }) : shown + " 2";
        var editor = new StrategyEditor(shown, StrategyExchange.CleanTitle(first), s.Roles, copy, FakeExists).Start();
        if (!EditStrategy(editor) || !editor.Saved) return;
        var (title, roles) = editor.Result;
        await SavePersonalAsync(title, roles, "user", "select.edit.saved");
    }

    // ---------- удалить ----------

    public async Task DeleteStrategyAsync(string? id)
    {
        if (StrategyOf(id) is not { } s || s.Source is "flowseal") return;
        if (CurrentStrategyId == s.Id) { Fail(L.T("select.delete.current")); return; }
        if (!Confirm(L.T("select.delete.confirm", new { name = DisplayTitleOf(s) }))) return;
        if (!await WithHelper(L.T("select.deleting"), async c =>
            {
                await c.StrategyDeleteAsync(s.Id);
                await FetchAsync(full: true);
            }))
            return;
        if (Scores.FirstOrDefault(r => r.Id == s.Id) is { } row) Scores.Remove(row);
        if (PendingCheckId == s.Id) { PendingCheckId = null; ResultText = ""; ResultHint = ""; }
        ReloadChoices();
        ShowToast(L.T("select.deleted"));
    }
}
