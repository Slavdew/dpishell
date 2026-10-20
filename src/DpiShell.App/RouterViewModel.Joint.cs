using System.Text.Json.Nodes;
using DpiShell.Core;
using DpiShell.Core.Router;

namespace DpiShell.App;

/// <summary>
/// Совместная работа с роутером (вкладка «Роутер», docs/research/mixed-strategies.md §7, договор §5.2 pc_link_*): роутер ведёт YouTube и Discord для всей сети,
/// этот ПК — только свои сайты. Логика — <see cref="JointCoordinator"/> в Core; здесь переключатель, статус и связь с роутером. Выключена по умолчанию.
/// Работает только при обоих приложениях; роутер пропал, оптимизация на нём выключена, связка не продлилась — этот ПК сразу работает по полной своей настройке.
/// </summary>
public sealed partial class RouterViewModel
{
    JointCoordinator? _joint;
    bool _jointBusy;
    string _jointSignature = "";

    /// <summary>Координатор совместной работы; создаётся, когда окно подключило ПК (<see cref="AttachJoint"/>).</summary>
    public JointCoordinator? Joint => _joint;

    /// <summary>Своя сетевая карта по адресу роутера — подмена в тестах (в настоящем окне — <see cref="LanIdentity.OwnMac"/>).</summary>
    public Func<string, string?> OwnMac { get; set; } = LanIdentity.OwnMac;

    /// <summary>Окно подключает ПК-сторону: помощник, включение и выключение оптимизации. Восстанавливает выбор, сделанный до перезапуска окна.</summary>
    public void AttachJoint(IJointPc pc)
    {
        _joint = new JointCoordinator(new JointRouterAdapter(this), pc) { Recorder = Recorder, OwnMac = a => OwnMac(a) };
        _joint.Changed += RaiseJoint;
        _joint.Restore(_settings.JointWanted);
        RaiseJoint();
    }

    /// <summary>Переключатель «Совместная работа с роутером» (выключен по умолчанию). Включение проверяет условия и говорит причину отказа.</summary>
    public bool JointWanted
    {
        get => _joint?.Wanted ?? false;
        set { if (value != JointWanted && !_jointBusy) _ = SetJointAsync(value); }
    }

    /// <summary>Совместную работу можно включить: роутер подключён (карточка видна только тогда) и ПК-сторона подключена.</summary>
    public bool JointAvailable => _joint is not null && (Connected || JointWanted) && !_jointBusy;

    /// <summary>
    /// Карточка видна, когда роутер подключён, и пока совместная работа включена — даже если роутер пропал (ноутбук вне дома): её нужно уметь выключить,
    /// а статус «Роутер недоступен — этот ПК работает сам» должен быть виден там же.
    /// </summary>
    public bool JointCardVisible => _joint is not null && (Connected || JointWanted);

    /// <summary>Короткий статус: «Роутер ведёт YouTube и Discord, этот ПК — свои сайты (3)» / «Роутер недоступен — этот ПК работает сам». Пусто, пока выключено.</summary>
    public string JointStatus => _joint?.Status ?? "";
    public bool HasJointStatus => JointStatus.Length > 0;
    public bool JointIsFallback => _joint?.State == JointState.Fallback;

    /// <summary>Пояснения: что ведёт роутер, что не вошло в связку.</summary>
    public string JointNotes => string.Join("\n", _joint?.Notes ?? []);
    public bool HasJointNotes => JointNotes.Length > 0;

    /// <summary>Подсказка под выключенным переключателем.</summary>
    public string JointHint => JointWanted
        ? "Выключите, чтобы оптимизацию вёл только роутер: на этом ПК она выключится."
        : "Роутер ведёт YouTube и Discord сам, а этот ПК — свои сайты и строки «Свои настройки для сайтов». Нужен DpiShell на роутере с включённой оптимизацией.";

    /// <summary>Сайты ПК, которые ведёт роутер: на ПК не применяются («ведёт роутер»).</summary>
    public IReadOnlyList<string> JointRouterOwned => _joint?.RouterOwned ?? [];

    /// <summary>Связка действует или включена и ждёт (роутер вернётся — поднимется сама): строки ПК и главный экран объясняют, кто что ведёт.</summary>
    public bool JointOn => _joint is { Wanted: true };

    /// <summary>Поменялась связка: карточка, главный экран, подсказки строк.</summary>
    public event Action? JointChanged;

    void RaiseJoint()
    {
        foreach (var n in new[] { nameof(JointWanted), nameof(JointAvailable), nameof(JointCardVisible), nameof(JointStatus), nameof(HasJointStatus), nameof(JointIsFallback), nameof(JointNotes),
                     nameof(HasJointNotes), nameof(JointHint), nameof(JointRouterOwned), nameof(JointOn), nameof(BothOn) })
            Raise(n);
        JointChanged?.Invoke();
    }

    async Task SetJointAsync(bool on)
    {
        if (_joint is null) return;
        _jointBusy = true;
        Raise(nameof(JointAvailable));
        try
        {
            if (on)
            {
                Say("Включаю совместную работу…");
                var refuse = await _joint.EnableAsync();
                if (refuse is not null) { Failed(refuse); return; }
                SaveJointWanted(true);
                Say(_joint.Status);
            }
            else
            {
                // Роутер ведёт оптимизацию для всех, в том числе для этого ПК: как до совместной работы, на ПК она выключается.
                // Сначала выключается ПК, потом снимается связка — полной настройки ПК рядом с роутером не будет ни на миг.
                bool pcOff = RouterEnabled && Found && PcRunning() && await StopPc();
                await _joint.StopAsync();
                SaveJointWanted(false);
                Say(pcOff ? "Совместная работа выключена. Оптимизацию ведёт роутер, на этом ПК она выключена." : "Совместная работа выключена.");
            }
        }
        catch (RouterException e) { Failed(RouterTexts.Friendly(e)); }
        finally
        {
            _jointBusy = false;
            RaiseJoint();
            Changed?.Invoke();
        }
    }

    void SaveJointWanted(bool wanted)
    {
        if (_settings.JointWanted == wanted) return;
        _settings.JointWanted = wanted;
        TrySave();
    }

    /// <summary>
    /// «Удалить полностью» и «Отменить всё»: связка снимается и переключатель выключается. ПК возвращается к полной настройке (её же и удаляют),
    /// на роутере — pc_link_del. Роутер недоступен — связка на нём истечёт сама (до суток).
    /// </summary>
    public async Task JointShutdownAsync()
    {
        if (_joint is null || !_joint.Wanted && _joint.State == JointState.Off && Recorder.Active(ChangeKinds.RouterJoint).Count == 0) { SaveJointWanted(false); return; }
        await _joint.StopAsync();
        SaveJointWanted(false);
        RaiseJoint();
    }

    /// <summary>Откат записи журнала окна «связка на роутере»: pc_link_del и выключенный переключатель.</summary>
    public async Task<UndoOutcome> UndoJointAsync(ChangeEntry e)
    {
        if (_joint is null) return UndoOutcome.Fail("совместная работа не готова — подождите и отмените снова");
        var id = e.After?["id"]?.GetValue<string>();
        if (Connected && id is not null)
        {
            try
            {
                var links = await Call(c => c.PcLinkGetAsync());
                if (!links.Items.Any(l => l.Id == id) && !_joint.Wanted) return UndoOutcome.Already("связки этого ПК на роутере уже нет");
            }
            catch (RouterException) { /* ответит снятие ниже */ }
        }
        else if (!Connected)
            return UndoOutcome.Fail("роутер не подключён — подключитесь на вкладке «Роутер» и отмените снова");
        await _joint.StopAsync();
        SaveJointWanted(false);
        RaiseJoint();
        return UndoOutcome.Restored("роутер больше не пропускает сайты этого ПК");
    }

    /// <summary>
    /// Перед включением оптимизации на ПК при включённой совместной работе (роутер уже ведёт всех): связка и «только своё» поднимаются заранее.
    /// false — не вышло: окно ведёт себя как обычно (спрашивает, что делать с роутером).
    /// </summary>
    public async Task<bool> JointPrepareStartAsync()
    {
        if (_joint is not { Wanted: true }) return false;
        try { return await _joint.PrepareStartAsync(); }
        catch (RouterException) { return false; }
    }

    /// <summary>Проверка связки: раз в минуту (<see cref="TickAsync"/>), при перемене роутера и оптимизации на ПК.</summary>
    public async Task JointTickAsync()
    {
        if (_joint is not { Wanted: true } || _jointBusy) return;
        await _joint.TickAsync();
    }

    /// <summary>Дождаться, пока проверка связки закончится (тесты и выход из окна).</summary>
    public Task JointWhenIdleAsync() => _joint?.WhenIdleAsync() ?? Task.CompletedTask;

    /// <summary>
    /// Перемена, о которой связке нужно знать сразу, не дожидаясь минуты: роутер нашёлся или пропал, вошли или вышли, на нём включили или выключили оптимизацию,
    /// на этом ПК её включили или выключили. Вызывается из <c>UpdateHome</c> и <c>PcChanged</c>.
    /// </summary>
    void JointKick()
    {
        if (_joint is not { Wanted: true }) return;
        var sig = $"{Found}|{Connected}|{RouterEnabled}|{PcRunning()}|{_status?.PcLinks}|{_status?.Methods?.Count}";
        if (sig == _jointSignature) return;
        _jointSignature = sig;
        if (_jointBusy) return;
        _ = JointTickSafeAsync();
    }

    /// <summary>Поменялось то, что отдаётся роутеру (свои сайты, строки, стратегия ПК): проверить связку сейчас, не дожидаясь минуты.</summary>
    public void JointRecheck()
    {
        if (_joint is { Wanted: true } && !_jointBusy) _ = JointTickSafeAsync();
    }

    async Task JointTickSafeAsync()
    {
        try { await JointTickAsync(); }
        catch (Exception e) when (e is RouterException or OperationCanceledException) { }
    }

    /// <summary>Роутер для координатора: что окно знает о нём и вызовы pc_link_* через общий вход.</summary>
    sealed class JointRouterAdapter(RouterViewModel vm) : IJointRouter
    {
        public string Address => vm._detection?.Address ?? vm._client?.Address ?? "";

        public bool Reachable => vm.Found && vm.Connected;

        public string? NotReady =>
            !vm.Found ? JointTexts.Fallback
            : !vm.Connected ? JointTexts.FallbackLogin
            : vm.Blocked || !HasMethods() ? JointTexts.FallbackOld
            : !vm.RouterEnabled ? JointTexts.FallbackDisabled
            : null;

        // Старый пакет без поля methods умеет только API 1.0 — pc_link_* там нет.
        bool HasMethods() => vm._status?.Methods is { } m && RouterClient.PcLinkMethods.All(m.Contains);

        public async Task<IReadOnlyList<string>> ListAsync(string name, CancellationToken ct) => (await vm.Call(c => c.ListsGetAsync(name, ct))).Lines;

        public Task<RouterPcLinkSet> SetAsync(string id, IReadOnlyList<string> macs, IReadOnlyList<string> domains, CancellationToken ct) =>
            vm.Call(c => c.PcLinkSetAsync(id, macs, domains, ct));

        public Task DelAsync(string id, CancellationToken ct) => vm.Call(c => c.PcLinkDelAsync(id, ct));

        public Task<RouterPcLinks> GetAsync(CancellationToken ct) => vm.Call(c => c.PcLinkGetAsync(ct));
    }
}
