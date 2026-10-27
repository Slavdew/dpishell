using System.Net.Http;
using DpiShell.Core;
using DpiShell.Core.Router;

namespace DpiShell.App;

/// <summary>Что из целей подбора на ПК уже оптимизирует роутер (architecture.md «Подбор на ПК рядом с роутером»).</summary>
public sealed partial class RouterViewModel
{
    /// <summary>
    /// Пересечение подбора на ПК с роутером: режим роутера, его продвинутый режим и списки (<c>lists_get</c> встроенных google, discord, general и своих сайтов роутера).
    /// null — наверняка не узнать: нет входа, старый роутер (без <c>methods</c> или встроенных списков), ошибка связи. Ничего не меняет.
    /// </summary>
    public async Task<SelectOverlap?> SelectOverlapAsync(IReadOnlyList<string> groups, IReadOnlyList<string> pcSites, IReadOnlyList<string> decoys)
    {
        if (!Found || !Connected || _status is not { Mode: { } mode, Methods: not null } s || Compat is not { } compat) return null;
        try
        {
            RouterAdvanced? adv = null;
            if (s.Advanced is { Enabled: true })
            {
                if (!compat.CanCall("advanced_get")) return null;
                adv = await Call(c => c.AdvancedGetAsync());
            }
            if (SelectOverlap.RouterAll(mode, adv)) return SelectOverlap.All;
            if (!compat.CanCall("lists_get")) return null;
            var lists = new Dictionary<string, IReadOnlyList<string>>();
            foreach (var name in SelectOverlap.RouterLists)
                lists[name] = (await Call(c => c.ListsGetAsync(name))).Lines;
            return SelectOverlap.Of(mode, adv, lists, groups, pcSites, decoys);
        }
        catch (Exception e) when (e is RouterException or HttpRequestException or TaskCanceledException) { return null; }
    }
}
