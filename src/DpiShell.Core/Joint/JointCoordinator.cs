using System.Text.Json.Nodes;
using DpiShell.Core.Helper;
using DpiShell.Core.Router;

namespace DpiShell.Core;

public enum JointState
{
    /// <summary>Совместная работа выключена (по умолчанию): всё как раньше.</summary>
    Off,
    /// <summary>Включена, но связки сейчас нет: оптимизация на этом ПК не работает или у ПК нет своих сайтов. Поднимется сама, когда будет что вести.</summary>
    Waiting,
    /// <summary>Действует: роутер ведёт YouTube, Discord и остальное, этот ПК — только свои сайты.</summary>
    Active,
    /// <summary>Включена, но роутер недоступен или не принял связку: этот ПК работает по полной своей настройке. Роутер вернулся — связка поднимется сама.</summary>
    Fallback,
}

/// <summary>Роутер, как его видит координатор. Реализует окно над <see cref="RouterClient"/>.</summary>
public interface IJointRouter
{
    /// <summary>Адрес роутера (для MAC своего адаптера).</summary>
    string Address { get; }
    /// <summary>Почему совместная работа с этим роутером сейчас невозможна (текст из <see cref="JointTexts"/>); null — можно. Видимость, вход, включённая оптимизация, методы pc_link_*.</summary>
    string? NotReady { get; }
    /// <summary>Роутер отвечает и мы на нём вошли — ему можно отправить снятие связки.</summary>
    bool Reachable { get; }
    Task<IReadOnlyList<string>> ListAsync(string name, CancellationToken ct);
    Task<RouterPcLinkSet> SetAsync(string id, IReadOnlyList<string> macs, IReadOnlyList<string> domains, CancellationToken ct);
    Task DelAsync(string id, CancellationToken ct);
    Task<RouterPcLinks> GetAsync(CancellationToken ct);
}

/// <summary>Этот ПК, как его видит координатор. Реализует окно над помощником.</summary>
public interface IJointPc
{
    /// <summary>Оптимизация на ПК включена (winws работает).</summary>
    bool Running { get; }
    /// <summary>joint_get; null — помощника нет.</summary>
    Task<JointInfo?> GetAsync(CancellationToken ct);
    /// <summary>joint_set; оптимизация перезапускается, если изменилась командная строка. Отказ помощника — исключение с текстом для человека.</summary>
    Task<JointInfo?> SetAsync(bool active, IReadOnlyList<string> owned, CancellationToken ct);
    /// <summary>Включить оптимизацию на ПК (связка уже действует: запускается сразу с настройкой «только своё»). false — не вышло, причина сказана.</summary>
    Task<bool> StartAsync(CancellationToken ct);
    /// <summary>Выключить оптимизацию на ПК (нечего вести).</summary>
    Task StopAsync(CancellationToken ct);
}

/// <summary>
/// Совместная работа ПК и роутера, сторона ПК (docs/research/mixed-strategies.md §7, вариант А; договор §5.2 pc_link_*).
/// Только при обоих приложениях: DpiShell на ПК и роутер с DpiShell, который отвечает по договору, у которого есть pc_link_* и включена оптимизация.
/// У адреса один хозяин: роутер ведёт YouTube и Discord для всей сети, ПК — только свои сайты и строки продвинутого режима; строка ПК, совпавшая с адресом
/// роутера, не применяется («ведёт роутер»). Связка продлевается не реже раза в час; любая неудача — продление, <c>active: false</c>, выключенная оптимизация
/// на роутере, пропавший роутер — и ПК СРАЗУ возвращается к полной своей настройке (сначала помощник, потом снятие связки на роутере).
/// MAC — текущего активного адаптера (Windows может случайно менять MAC Wi-Fi по сетям): сменился адаптер или сеть — <c>pc_link_set</c> заново.
/// </summary>
public sealed class JointCoordinator(IJointRouter router, IJointPc pc)
{
    readonly SemaphoreSlim _gate = new(1, 1);
    bool _again;

    /// <summary>Часы (UTC); тесты подставляют свои.</summary>
    public Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;
    /// <summary>MAC своего адаптера по адресу роутера; в тестах — подмена.</summary>
    public Func<string, string?> OwnMac { get; set; } = LanIdentity.OwnMac;
    public TimeSpan RenewEvery { get; set; } = JointRules.RenewEvery;
    /// <summary>Журнал окна (откат всего): связка на роутере — запись <see cref="ChangeKinds.RouterJoint"/>.</summary>
    public IChangeRecorder Recorder { get; set; } = NullRecorder.Instance;

    /// <summary>Человек включил совместную работу (переключатель). Сохраняет окно.</summary>
    public bool Wanted { get; private set; }

    public JointState State { get; private set; } = JointState.Off;
    /// <summary>Короткий статус: «Роутер ведёт YouTube и Discord, этот ПК — свои сайты (3)».</summary>
    public string Status { get; private set; } = "";
    /// <summary>Пояснения под статусом: что ведёт роутер, что отброшено.</summary>
    public IReadOnlyList<string> Notes { get; private set; } = [];
    /// <summary>Сайты, которые ведёт этот ПК (свои сайты и строки продвинутого режима, без отданных роутеру).</summary>
    public IReadOnlyList<string> Mine { get; private set; } = [];
    /// <summary>Сайты ПК, совпавшие с адресами роутера: «ведёт роутер», на ПК не применяются.</summary>
    public IReadOnlyList<string> RouterOwned { get; private set; } = [];
    /// <summary>Имена приманок этого ПК, отданные роутеру.</summary>
    public IReadOnlyList<string> Decoys { get; private set; } = [];
    public bool DecoysReliable { get; private set; } = true;
    public DateTime? LastSetAt => _lastSetAt;
    public string? LastMac => _mac;
    /// <summary>Что отправлено роутеру последним <c>pc_link_set</c> (для проверки и отчёта).</summary>
    public IReadOnlyList<string> Sent => _sent;

    /// <summary>Что-то поменялось (состояние, статус, списки).</summary>
    public event Action? Changed;

    DateTime? _lastSetAt;
    string? _mac, _sentKey, _linkRecord, _pcId;
    IReadOnlyList<string> _routerNames = [], _ignored = [], _sent = [];
    bool _namesLoaded, _pcActive;

    // ---------- переключатель ----------

    /// <summary>
    /// Включить. Условия проверяются заранее: помощник, роутер (найден, вход, оптимизация включена, pc_link_*), свои сайты у ПК. Не выполнено — текст причины,
    /// ничего не меняется. Выполнено — связка на роутере, затем помощник переводит ПК на «только своё», затем (если оптимизация на ПК не работала) она включается.
    /// </summary>
    public async Task<string?> EnableAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var info = await SafeGetAsync(ct);
            if (info is null) return JointTexts.FallbackNotInstalled;
            if (router.NotReady is { } why) return why;
            Wanted = true;
            await TickCoreAsync(info, enabling: true, ct);
            if (State != JointState.Active)
            {
                var reason = Status;
                Wanted = false;
                SetOff();
                return reason;
            }
            if (!pc.Running && !await pc.StartAsync(ct))
            {
                await DropAsync(null, fallback: false, ct);
                Wanted = false;
                SetOff();
                return JointTexts.StartFailed;
            }
            return null;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Перед включением оптимизации на ПК при включённой совместной работе: связка и «только своё» уже действуют, чтобы winws не запустился с полной настройкой
    /// рядом с роутером. false — не вышло (роутер недоступен и т. п.): окно спросит человека, как раньше.
    /// </summary>
    public async Task<bool> PrepareStartAsync(CancellationToken ct = default)
    {
        if (!Wanted) return false;
        await _gate.WaitAsync(ct);
        try
        {
            if (await SafeGetAsync(ct) is not { } info || router.NotReady is not null) return false;
            await TickCoreAsync(info, enabling: true, ct);
            return State == JointState.Active;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Выключить (переключатель, «Удалить полностью», «Отменить всё»): сначала ПК возвращается к полной настройке, потом связка на роутере снимается.</summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            Wanted = false;
            await DropAsync(null, fallback: false, ct);
            SetOff();
        }
        finally { _gate.Release(); }
    }

    /// <summary>Дождаться, пока идущая проверка закончится (тесты, выход из окна).</summary>
    public async Task WhenIdleAsync()
    {
        await _gate.WaitAsync();
        _gate.Release();
    }

    /// <summary>Начальное состояние окна: переключатель был включён до перезапуска. Связка поднимется первой проверкой.</summary>
    public void Restore(bool wanted)
    {
        Wanted = wanted;
        if (wanted && State == JointState.Off) { State = JointState.Waiting; Status = JointTexts.Starting; Changed?.Invoke(); }
    }

    // ---------- проверка ----------

    /// <summary>
    /// Раз в минуту и при любой перемене (роутер, оптимизация на ПК, свои сайты): держит связку. Параллельный вызов не теряется — проверка повторится.
    /// </summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        if (!Wanted) return;
        if (!await _gate.WaitAsync(0, ct)) { _again = true; return; }
        try
        {
            do
            {
                _again = false;
                if (!Wanted) break;
                await TickCoreAsync(await SafeGetAsync(ct), enabling: false, ct);
            }
            while (_again);
        }
        finally { _gate.Release(); }
    }

    async Task<JointInfo?> SafeGetAsync(CancellationToken ct)
    {
        try { return await pc.GetAsync(ct); }
        catch (Exception e) when (e is HelperException or HelperUnavailableException or IOException or TimeoutException or InvalidOperationException) { return null; }
    }

    /// <param name="enabling">Включение (переключатель или подготовка запуска ПК): оптимизация на ПК ещё не обязана работать, и «нечего вести» ничего не выключает.</param>
    async Task TickCoreAsync(JointInfo? info, bool enabling, CancellationToken ct)
    {
        if (info is null) { await DropAsync(JointTexts.FallbackNotInstalled, fallback: true, ct); return; }
        _pcId = info.PcId;
        Decoys = info.Decoys; DecoysReliable = info.DecoysReliable;

        // Оптимизация на ПК выключена: вести нечего, связки на роутере быть не должно (иначе сайты ПК не обработает никто).
        if (!enabling && !pc.Running)
        {
            await DropAsync(null, fallback: false, ct, info);
            State = JointState.Waiting; Status = JointTexts.WaitingPc; Notes = []; Changed?.Invoke();
            return;
        }
        if (router.NotReady is { } why) { await DropAsync(why, fallback: true, ct, info); return; }
        var mac = OwnMac(router.Address);
        if (mac is null) { await DropAsync(JointTexts.FallbackNoMac, fallback: true, ct, info); return; }

        try
        {
            if (!_namesLoaded) await LoadRouterNamesAsync(ct);
            var sel = JointRules.Select(info, _routerNames).WithIgnored(_ignored);
            // Своих сайтов нет (или все — адреса роутера): вести нечего. Оптимизация на ПК при роутере без совместной работы тоже не нужна.
            if (!HasWork(info, sel))
            {
                await DropAsync(null, fallback: false, ct, info);
                if (!enabling && pc.Running) await pc.StopAsync(ct);
                State = JointState.Waiting; Status = JointTexts.NoOwnSites; Notes = []; Changed?.Invoke();
                return;
            }
            string key = mac + "|" + string.Join(',', sel.Send);
            bool due = _lastSetAt is null || Now() - _lastSetAt >= RenewEvery || key != _sentKey;
            if (!due)
            {
                var links = await router.GetAsync(ct);
                // Действует ли связка (роутер мог выключить оптимизацию, перезагрузиться, связка — истечь) и не поменялись ли его адреса.
                due = links.Items.FirstOrDefault(l => l.Id == info.PcId) is not { Active: true } mine || mine.Ignored != _ignored.Count;
            }
            if (due)
            {
                await LoadRouterNamesAsync(ct);   // списки роутера могли поменяться с обновлением
                sel = JointRules.Select(info, _routerNames);
                key = mac + "|" + string.Join(',', sel.Send);
                string rec = _linkRecord is null
                    ? Recorder.Begin(ChangeKinds.RouterJoint, JointTexts.RouterJournalTitle,
                        new JsonObject { ["router"] = "связки этого ПК не было" }, new JsonObject { ["id"] = info.PcId })
                    : "";
                RouterPcLinkSet set;
                try { set = await router.SetAsync(info.PcId, [mac], sel.Send, ct); }
                catch { Recorder.Abort(rec); throw; }
                Recorder.Done(rec);
                if (rec.Length > 0) _linkRecord = rec;
                _ignored = JointRules.Clean(set.Ignored ?? []);
                sel = sel.WithIgnored(_ignored);
                _lastSetAt = Now(); _mac = mac; _sentKey = key; _sent = sel.Send;
                var after = await router.GetAsync(ct);
                if (after.Items.FirstOrDefault(l => l.Id == info.PcId) is not { Active: true })
                {
                    await DropAsync(JointTexts.FallbackRenew, fallback: true, ct, info);
                    return;
                }
                if (!HasWork(info, sel))
                {
                    await DropAsync(null, fallback: false, ct, info);
                    if (!enabling && pc.Running) await pc.StopAsync(ct);
                    State = JointState.Waiting; Status = JointTexts.NoOwnSites; Notes = []; Changed?.Invoke();
                    return;
                }
            }

            // Связка на роутере действует — теперь ПК переходит на «только своё» (или продлевает срок у помощника).
            var owned = sel.RouterOwned;
            if (due || !info.Active || !SameSet(info.Owned, owned))
            {
                try { await pc.SetAsync(true, owned, ct); _pcActive = true; }
                catch (HelperException he) when (he.Code == HelperErrors.Busy) { return; }   // помощник занят подбором или другой командой — следующая проверка повторит
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    await DropAsync(e.Message.Length > 0 ? e.Message : JointTexts.FallbackRenew, fallback: true, ct, info);
                    return;
                }
            }
            Mine = sel.Mine; RouterOwned = sel.RouterOwned;
            Notes = NotesOf(sel, info);
            State = JointState.Active; Status = JointTexts.Active(sel.Mine.Count);
            Changed?.Invoke();
        }
        catch (RouterException e)
        {
            await DropAsync(e.Failure == RouterFailure.NotSupported ? JointTexts.FallbackOld
                : e.Failure is RouterFailure.Unreachable or RouterFailure.NoResponse ? JointTexts.Fallback : JointTexts.FallbackRenew, fallback: true, ct, info);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or TimeoutException or IOException && !ct.IsCancellationRequested)
        {
            await DropAsync(JointTexts.Fallback, fallback: true, ct, info);
        }
    }

    /// <summary>Есть что вести: хотя бы один свой сайт или строка со своей стратегией, не отданные роутеру, или профиль программы.</summary>
    static bool HasWork(JointInfo i, JointSelection sel) => i.Profiles > 0 || i.Work.Any(sel.Mine.Contains);

    async Task LoadRouterNamesAsync(CancellationToken ct)
    {
        var names = new List<string>();
        foreach (var list in new[] { "google", "discord" })
        {
            try { names.AddRange(await router.ListAsync(list, ct)); }
            catch (RouterException e) when (e.Failure is RouterFailure.App or RouterFailure.InvalidArgs) { }   // старее r9: адреса роутера узнаем из ignored
        }
        _routerNames = JointRules.Clean(names);
        _namesLoaded = true;
    }

    static bool SameSet(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.Count == b.Count && a.Order(StringComparer.Ordinal).SequenceEqual(b.Order(StringComparer.Ordinal));

    static List<string> NotesOf(JointSelection sel, JointInfo info)
    {
        var notes = new List<string>();
        if (sel.RouterOwned.Count > 0)
            notes.Add("Ведёт роутер: " + string.Join(", ", sel.RouterOwned.Take(5)) + (sel.RouterOwned.Count > 5 ? $" и ещё {sel.RouterOwned.Count - 5}" : "") + " — на этом ПК не применяется.");
        if (sel.Trimmed > 0) notes.Add($"Роутер принимает до {JointRules.MaxDomains} имён: {JointTexts.Plural(sel.Trimmed)} этого ПК в связку не вошли.");
        if (sel.DecoyGaps.Count > 0) notes.Add("Имена, которые подставляет стратегия этого ПК, совпали с адресами роутера: " + string.Join(", ", sel.DecoyGaps.Take(3)) + ".");
        if (!info.DecoysReliable) notes.Add("Имена приманок стратегии определены приблизительно — роутеру отданы все имена фейков из поставки.");
        return notes;
    }

    // ---------- возврат к полной настройке ----------

    /// <summary>
    /// ПК — по полной настройке сразу (помощник), потом связка снимается на роутере (если он отвечает). <paramref name="reason"/> — статус для окна;
    /// <paramref name="fallback"/> — это откат при включённой совместной работе (состояние «Fallback»), а не её выключение.
    /// </summary>
    async Task DropAsync(string? reason, bool fallback, CancellationToken ct, JointInfo? info = null)
    {
        if (_pcActive || info is null || info.Active)
        {
            try { await pc.SetAsync(false, [], ct); _pcActive = false; }
            catch (Exception e) when (e is HelperException or HelperUnavailableException or IOException or TimeoutException or InvalidOperationException) { }
        }
        var id = _pcId ?? info?.PcId;
        if (id is not null && (_lastSetAt is not null || _linkRecord is not null) && router.Reachable)
        {
            try { await router.DelAsync(id, ct); _linkRecord = null; Recorder.Close(ChangeKinds.RouterJoint, "связка снята"); }
            catch (Exception e) when (e is RouterException or HttpRequestException or TaskCanceledException) { }
        }
        _lastSetAt = null; _sentKey = null; _mac = null; _ignored = []; _sent = []; _namesLoaded = false;
        Mine = []; RouterOwned = [];
        if (fallback && reason is not null)
        {
            State = JointState.Fallback; Status = reason; Notes = [];
            Changed?.Invoke();
        }
    }

    void SetOff()
    {
        State = JointState.Off; Status = ""; Notes = [];
        Changed?.Invoke();
    }
}
