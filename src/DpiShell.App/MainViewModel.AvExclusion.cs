using System.Windows;
using System.Windows.Input;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.App;

/// <summary>
/// Карточка «Первый запуск» на Главной (ТЗ av-exclusion): исключение Защитника для папки ядра и «Восстановить» ядро на месте, без отсылки в «Диагностику».
/// Исключение добавляет помощник (путь — свой, окно его не передаёт), только по нажатию. Порядок первого запуска (п. 10): установка помощника (UAC) →
/// вопрос об исключении (добавляет уже помощник) → скачивание ядра; помощник после установки ядро сам не качает (<see cref="CoreWait"/>). Отказ Защитника или сторонний антивирус — ручной путь: папка выделяемым текстом,
/// «Скопировать путь», «Открыть Защитник».
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Защитник для окна без прав (только чтение: кто антивирус и работает ли Защитник) — до установки помощника. В тестах — подмена.</summary>
    public IDefenderSystem Defender { get; set; } = new WmiDefender();

    /// <summary>Состояние от помощника (API 1.16); null — не спрашивали или помощник его не знает.</summary>
    AvExclusionStatus? _av;
    /// <summary>Что окно видит само, пока помощника нет: список исключений без прав не читается, поэтому «Защитник работает» = Unknown.</summary>
    AvExclusionStatus? _avLocal;
    /// <summary>Последняя попытка не удалась: исход (refused / third_party) и антивирус — текст собирается при показе.</summary>
    string? _avFail;
    string? _avFailProduct;
    /// <summary>Исключение добавили в этом сеансе — строка «✓» видна, пока карточка не закроется.</summary>
    bool _avJustAdded;

    const string AvAddCommand = "av_exclusion_add";

    /// <summary>Помощника нет (или ещё не пробовали подключиться): исключение поставит установка.</summary>
    bool HelperMissing => Helper.State is HelperState.NotInstalled or HelperState.Unknown;

    /// <summary>Помощник знает команды исключения (1.16+). Старый — кнопки нет, строка «обновите помощника».</summary>
    bool AvSupported => Helper.Reader is { } r && r.Compat.Supports(AvAddCommand) && r.Compat.Supports("av_exclusion_status");

    AvExclusionStatus? AvNow => HelperMissing ? _avLocal : AvSupported ? _av : null;

    /// <summary>Ядро не найдено или изменено — и есть кому восстановить (помощник установлен).</summary>
    bool AvCoreBad => !HelperMissing && _coreState != CoreState.Ok;

    /// <summary>Предложить «Добавить в исключения»: Защитник работает и папки нет; до помощника или при сломанном ядре — и когда список не прочитать.</summary>
    public bool AvOffer => _avFail is null && AvNow?.State is { } s
        && (s == AvStates.Missing || s == AvStates.Unknown && (HelperMissing || AvCoreBad));

    /// <summary>Ручной путь: Защитник не дал или антивирус сторонний (при первом запуске или сломанном ядре).</summary>
    public bool AvManual => AvManualText.Length > 0;

    public string AvManualText =>
        _avFail == AvOutcomes.ThirdParty ? L.T("home.av.third_party", new { name = _avFailProduct ?? "" })
        : _avFail is not null ? L.T("home.av.refused")
        : AvNow is { State: AvStates.ThirdParty } t && (HelperMissing || AvCoreBad) ? L.T("home.av.third_party", new { name = t.Product ?? "" })
        : "";

    /// <summary>«Открыть Защитник» — только когда отказал сам Защитник (у стороннего антивируса свои настройки).</summary>
    public bool AvShowDefender => AvManual && _avFail != AvOutcomes.ThirdParty && AvNow?.State != AvStates.ThirdParty;

    /// <summary>«Папка ядра в исключениях Защитника ✓» — когда карточка видна из-за ядра или исключение только что добавили.</summary>
    public bool AvOk => AvNow?.State == AvStates.Excluded && (AvCoreBad || _avJustAdded);

    /// <summary>Старый помощник: исключение он не добавит — подсказка обновить (при сломанном ядре, иначе карточка не нужна).</summary>
    public bool AvNeedsUpdate => AvCoreBad && !HelperMissing && !AvSupported;

    /// <summary>Отдельная «Восстановить» — ядро сломано, а исключение добавлять не нужно или нельзя (иначе одна кнопка «Добавить в исключения и восстановить»).</summary>
    public bool AvShowRestore => AvCoreBad && !AvOffer;

    public bool ShowAvCard => AvOffer || AvManual || AvOk || AvCoreBad;

    /// <summary>«Указать архив ядра…» — рядом с восстановлением: ядро не найдено или изменено (GitHub может не открываться).</summary>
    public bool AvShowOffline => AvCoreBad;

    /// <summary>Что случилось или зачем исключение; только что добавили и ядро в порядке — текста нет, одна строка «✓».</summary>
    public string AvCardText => AvCoreBad
        ? L.T(_coreState == CoreState.Missing ? "home.av.core_missing" : "home.av.core_damaged")
        : AvOk ? "" : L.T("home.av.offer");
    public bool AvHasText => AvCardText.Length > 0;

    public string AvAddText => L.T(AvCoreBad ? "home.av.add_restore" : "home.av.add");

    /// <summary>Папка исключения — у помощника своя (%ProgramData%\DpiShell); окно показывает её же.</summary>
    public string AvPath => AvNow?.Path ?? _shell.Paths.Root;

    public ICommand AvAddCmd => _avAddCmd ??= RelayCommand.Async(AvAddAsync, () => !Busy && !IsLoading);
    ICommand? _avAddCmd;

    public ICommand CopyAvPathCmd => _copyAvPathCmd ??= new RelayCommand(() =>
    {
        try { Clipboard.SetText(AvPath); Message = L.T("diag.say.path_copied", new { path = AvPath }); }
        catch (System.Runtime.InteropServices.ExternalException) { Fail(L.T("diag.err.copy_busy")); }
    });
    ICommand? _copyAvPathCmd;

    static readonly string[] AvProps =
    [
        nameof(AvOffer), nameof(AvManual), nameof(AvManualText), nameof(AvShowDefender), nameof(AvOk), nameof(AvNeedsUpdate),
        nameof(AvShowRestore), nameof(ShowAvCard), nameof(AvShowOffline), nameof(AvCardText), nameof(AvHasText), nameof(AvAddText), nameof(AvPath),
    ];

    /// <summary>Сообщить окну то, что изменилось (Главная пересобирается часто — как <see cref="RaiseIssues"/>).</summary>
    void RaiseAv()
    {
        foreach (var p in AvProps)
        {
            var v = GetType().GetProperty(p)!.GetValue(this)!;
            if (_homeShown.TryGetValue(p, out var old) && Equals(old, v)) continue;
            _homeShown[p] = v;
            Raise(p);
        }
    }

    /// <summary>Спросить помощника (1.16+); старый или нет связи — состояние неизвестно, карточка решает по ядру.</summary>
    async Task RefreshAvAsync()
    {
        if (Helper.Reader is { } c && AvSupported)
        {
            try { _av = await c.AvExclusionStatusAsync(); }
            catch (Exception e) when (e is HelperException or HelperUnavailableException) { _av = null; }
        }
        else _av = null;
        RaiseAv();
    }

    /// <summary>Помощника нет: прочитать самим (без прав — только режим Защитника и «Центр безопасности»).</summary>
    async Task ProbeLocalAvAsync()
    {
        var defender = Defender;
        var root = _shell.Paths.Root;
        try { _avLocal = await Task.Run(() => new AvExclusions(defender, root, NullRecorder.Instance).Status()); }
        catch (Exception e) when (AvExclusions.IsWmiError(e)) { _avLocal = null; }
        RaiseAv();
    }

    /// <summary>
    /// Сразу после установки помощника: ядра нет — сначала исключение, потом скачивание (помощник ждёт этой команды, <see cref="CoreWait"/>).
    /// Вопрос — только если главный антивирус Защитник и папки в исключениях нет (или список скрыт); отказ или закрытый вопрос — качать сразу.
    /// <paramref name="add"/>: null — спросить, true — уже выбрано кнопкой карточки. False — ядро не скачалось (причина в строке состояния).
    /// </summary>
    async Task<bool> FirstRunCoreAsync(HelperClient c, bool? add)
    {
        // Состояние ядра — из ответа помощника сразу после подключения (status), не из строки окна: она обновляется позже.
        if ((Helper.Status?.Core ?? _coreState) != CoreState.Missing) return true;
        if (AvSupported && _av?.State is AvStates.Missing or AvStates.Unknown)
        {
            add ??= Choose(L.T("home.av.ask"), [L.T("home.av.ask_add"), L.T("home.av.ask_skip")]) == 0;
            if (add == true) await Step(L.T("home.av.busy"), () => AddExclusionAsync(c));
        }
        bool ok = await Step(L.T("home.av.busy_core"), async () =>
        {
            await c.CoreRepairAsync(new Progress<string>(s => Message = s));
            await RefreshStatusAsync();
        });
        RaiseAv();
        return ok;
    }

    /// <summary>Шаг первого запуска: под <see cref="Guard"/>, а если вызвавшая кнопка уже держит Busy — внутри неё, с той же обработкой ошибок.</summary>
    async Task<bool> Step(string busyText, Func<Task> work)
    {
        if (!Busy) return await Guard(busyText, work);
        try { Message = busyText; await work(); return true; }
        catch (Exception e)
        {
            if (e is HelperUnavailableException u) Helper.MarkLost(u);
            _error = Texts.Friendly(e);
            Fail(_error, e);
            return false;
        }
    }

    /// <summary>Команда помощнику: исключение для его папки; отказ или сторонний антивирус — ручной путь на карточке.</summary>
    async Task AddExclusionAsync(HelperClient c)
    {
        _avFail = null; _avFailProduct = null;
        var r = await c.AvExclusionAddAsync();
        _av = r.Status;
        if (!r.Ok) { _avFail = r.Outcome == AvOutcomes.ThirdParty ? AvOutcomes.ThirdParty : AvOutcomes.Refused; _avFailProduct = r.Status.Product; }
        else { _avJustAdded = r.Outcome == AvOutcomes.Added || _avJustAdded; Message = r.Message; }
        RaiseAv();
    }

    /// <summary>
    /// «Добавить в исключения» (и «…и восстановить», если ядро сломано). Помощника нет — установка, затем исключение и только потом скачивание ядра.
    /// Отказ Защитника или сторонний антивирус — ручной путь на карточке; восстановление при этом всё равно идёт, если его просили.
    /// </summary>
    async Task AvAddAsync()
    {
        _avFail = null; _avFailProduct = null;
        if (HelperMissing)
        {
            // Как у остальных кнопок со щитом: сначала «Нужен помощник» (один запрос прав), затем установка; исключение добавит уже помощник, потом — ядро.
            if (!AskHelper(L.T("shell.helper.ask_install"))) { Message = L.T("shell.helper.view_only"); return; }
            if (!await InstallHelperAsync() || Helper.Client is not { } fresh) { RaiseAv(); return; }
            if ((Helper.Status?.Core ?? _coreState) == CoreState.Missing) await FirstRunCoreAsync(fresh, add: true);
            else if (AvSupported && _av?.State is AvStates.Missing or AvStates.Unknown) await Step(L.T("home.av.busy"), () => AddExclusionAsync(fresh));
            return;
        }
        bool repair = AvCoreBad;
        await WithHelper(L.T(repair ? "about.core.busy" : "home.av.busy"), async c =>
        {
            if (!c.Compat.Supports(AvAddCommand)) { Message = L.T("home.av.update_helper"); return; }
            await AddExclusionAsync(c);
            if (repair)
            {
                await c.CoreRepairAsync(new Progress<string>(s => Message = s));
                await RefreshDiagAsync();
            }
            await RefreshStatusAsync();
        });
        RaiseAv();
    }
}
