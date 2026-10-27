using DpiShell.Core.Router;

namespace DpiShell.Core;

/// <summary>
/// Пересекается ли подбор на ПК с тем, что уже оптимизирует роутер (architecture.md «Подбор на ПК рядом с роутером»).
/// <see cref="AllSites"/> — роутер ведёт все сайты («Все сайты, кроме исключений» или «Остальные адреса — для всех»);
/// <see cref="Items"/> — что именно пересекается: «YouTube», «Discord», сайты ПК, которые есть в списках роутера;
/// <see cref="DecoyClash"/> — имя приманки из фейков стратегий совпало с адресом роутера: роутер может принять соединение ПК за своё (стенд 2026-10-20).
/// </summary>
public sealed record SelectOverlap(bool AllSites, IReadOnlyList<string> Items, bool DecoyClash = false)
{
    /// <summary>Не пересекается: подбор можно вести на ПК рядом с работающим роутером.</summary>
    public bool None => !AllSites && Items.Count == 0 && !DecoyClash;

    public static readonly SelectOverlap All = new(true, []);

    /// <summary>Встроенные списки роутера, которые читает окно (<c>lists_get</c>, роутер 0.2.0-r9), и свой список сайтов роутера.</summary>
    public static readonly IReadOnlyList<string> RouterLists = ["google", "discord", "general", "user-general"];

    /// <summary>Роутер ведёт все сайты — списки читать незачем.</summary>
    public static bool RouterAll(string? mode, RouterAdvanced? adv) =>
        adv is { Enabled: true } ? adv.Others == AdvancedSites.OthersAll : mode != "lists";

    /// <summary>
    /// Разбор. <paramref name="mode"/> — <c>status.mode</c> роутера; <paramref name="adv"/> — его продвинутый режим (null — выключен или его нет);
    /// <paramref name="lists"/> — строки списков <see cref="RouterLists"/>; <paramref name="groups"/> — группы подбора на ПК (youtube, discord, user);
    /// <paramref name="pcSites"/> — свои сайты ПК и сайты строк его продвинутого режима (winws подбора их обрабатывает, даже если группа «Свои сайты» не отмечена);
    /// <paramref name="decoys"/> — имена приманок из фейков поставки.
    /// </summary>
    public static SelectOverlap Of(string? mode, RouterAdvanced? adv, IReadOnlyDictionary<string, IReadOnlyList<string>> lists,
        IReadOnlyList<string> groups, IEnumerable<string> pcSites, IEnumerable<string> decoys)
    {
        if (RouterAll(mode, adv)) return All;
        // «Остальные адреса — напрямую»: роли каталога на роутере не включаются, он ведёт только строки.
        bool catalog = adv is not { Enabled: true } || adv.Others != AdvancedSites.OthersDirect;
        var items = new List<string>();
        if (catalog && groups.Contains("youtube")) items.Add("YouTube");
        if (catalog && groups.Contains("discord")) items.Add("Discord");
        var names = new List<string>();
        if (catalog)
        {
            foreach (var l in RouterLists) names.AddRange(lists.GetValueOrDefault(l) ?? []);
            names.Add("discord.media");   // роль discord_media: --hostlist-domains=discord.media
        }
        if (adv is { Enabled: true })
            names.AddRange(adv.Rows.Where(r => r.Strategy != AdvancedSites.Direct).SelectMany(r => r.Domains));
        var router = JointRules.Clean(names.Select(n => n.Trim().TrimStart('^')));
        foreach (var site in pcSites.Select(DataStore.NormalizeDomain).OfType<string>().Distinct())
            if (JointRules.OwnedBy(site, router)) items.Add(site);
        bool clash = JointRules.Clean(decoys).Any(d => JointRules.OwnedBy(d, router));
        return new(false, items, clash);
    }
}
