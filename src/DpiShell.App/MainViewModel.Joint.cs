using System.IO;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.App;

/// <summary>
/// Совместная работа с роутером, сторона ПК в окне (docs/research/mixed-strategies.md §7). Что ведёт ПК, считает помощник (<c>joint_get</c>/<c>joint_set</c>, протокол 1.9):
/// у него стратегии, фейки, свои сайты и строки продвинутого режима. Окно держит связку на роутере (<see cref="RouterViewModel.Joint"/>) и сообщает помощнику,
/// когда ПК должен вести только своё, а когда — всё сам. Пока связка включена, оптимизация ПК не выключается из-за того, что роутер «уже ведёт».
/// </summary>
public sealed partial class MainViewModel
{
    void InitJoint()
    {
        Router.AttachJoint(new JointPc(this));
        Router.JointChanged += OnJointChanged;
        // «Свои настройки для сайтов»: строка, совпавшая с адресом роутера, не применяется — «ведёт роутер».
        AdvancedPc.RouterOwned = () => Router.JointOn ? Router.JointRouterOwned : [];
    }

    void OnJointChanged()
    {
        UpdateStatus();
        AdvancedPc.RefreshHints();
    }

    /// <summary>
    /// Выключатель оптимизации на ПК при включённой совместной работе: сначала связка и «только своё», потом winws. Так на ПК не окажется полной настройки
    /// рядом с роутером. false — совместной работы нет или она не поднялась: дальше как обычно.
    /// </summary>
    async Task<bool> JointBeforeEnableAsync() => Router.JointOn && Router.Found && Router.RouterEnabled && await Router.JointPrepareStartAsync();

    /// <summary>ПК для координатора: помощник и включение/выключение оптимизации. Без помощника <c>GetAsync</c> отвечает null — связка откатывается.</summary>
    sealed class JointPc(MainViewModel vm) : IJointPc
    {
        public bool Running => vm.IsRunning;

        public async Task<JointInfo?> GetAsync(CancellationToken ct)
        {
            if (vm.Helper.Client is not { } c || !c.Compat.Supports("joint_get")) return null;
            try { return await c.JointGetAsync(ct); }
            catch (HelperUnavailableException e) { vm.Helper.MarkLost(e); return null; }
        }

        public async Task<JointInfo?> SetAsync(bool active, IReadOnlyList<string> owned, CancellationToken ct)
        {
            if (vm.Helper.Client is not { } c || !c.Compat.Supports("joint_set"))
                throw new HelperUnavailableException(HelperUnavailableReason.NotInstalled);
            try { return await c.JointSetAsync(active, owned, ct); }
            catch (HelperUnavailableException e) { vm.Helper.MarkLost(e); throw; }
        }

        public async Task<bool> StartAsync(CancellationToken ct)
        {
            if (vm.CurrentStrategyId is not { } id) { vm.Message = L.T("router.joint.pick_strategy"); return false; }
            // Как «Включить», но без перехода на главную: переключатель стоит на вкладке «Роутер».
            return await vm.WithHelper(L.T("router.enable.busy_short"), async c =>
            {
                await c.EnableAsync(id);
                vm._draftStrategy = null;
                await vm.RefreshStatusAsync();
            });
        }

        public async Task StopAsync(CancellationToken ct)
        {
            if (vm.IsRunning) await vm.DisableAsync();
        }
    }
}
