namespace DpiShell.Core;

/// <summary>Можно ли откатить запись журнала отдельно от остальных («Отменить» у пункта списка «Изменения DpiShell»).</summary>
public enum SingleUndo
{
    /// <summary>Можно: откат проверит, что сейчас стоит поставленное нами, и вернёт прежнее.</summary>
    Yes,
    /// <summary>Загрузка настроек: откатывается вместе со своими пунктами (hosts, DNS, профили) — нужен помощник 1.15+.</summary>
    Group,
    /// <summary>Пункт сделан внутри загрузки настроек — отменяется вместе с ней.</summary>
    InGroup,
    /// <summary>Есть более поздний пункт того же вида: сначала он (иначе откат увидит «изменено после нас» и закроет пункт зря).</summary>
    NewerFirst,
    /// <summary>Только вместе со всем: ядро нужно службе и оптимизации, отдельно его не убрать.</summary>
    OnlyAll,
    /// <summary>Запись другой версии программы — отменить нечем.</summary>
    Unknown,
}

/// <summary>Правила отмены по пунктам. Окно решает по виду и порядку записей — значения и пути не нужны.</summary>
public static class ChangeUndoPolicy
{
    /// <summary>Виды, где у каждой записи свой предмет (профиль — свой id): более поздняя запись того же вида раннюю не перекрывает.</summary>
    static readonly HashSet<string> Independent = [ChangeKinds.AppProfile, ChangeKinds.SettingsImport];

    /// <summary>Только вместе со всем.</summary>
    static readonly HashSet<string> AllOnly = [ChangeKinds.Core];

    /// <summary>
    /// Решение для каждой записи. <paramref name="items"/> — записи одного журнала (помощника или окна) в любом порядке; более поздняя — по времени.
    /// </summary>
    public static IReadOnlyDictionary<string, SingleUndo> Decide(IReadOnlyList<ChangeInfo> items)
    {
        var result = new Dictionary<string, SingleUndo>();
        var newest = items.Where(i => !Independent.Contains(i.Kind))
            .GroupBy(i => i.Kind).ToDictionary(g => g.Key, g => g.OrderByDescending(i => i.At).First().Id);
        foreach (var i in items)
            result[i.Id] =
                !i.Undoable ? SingleUndo.Unknown
                : i.Group is { Length: > 0 } g && items.Any(x => x.Id == g) ? SingleUndo.InGroup
                : i.Kind == ChangeKinds.SettingsImport ? SingleUndo.Group
                : AllOnly.Contains(i.Kind) ? SingleUndo.OnlyAll
                : newest.TryGetValue(i.Kind, out var n) && n != i.Id ? SingleUndo.NewerFirst
                : SingleUndo.Yes;
        return result;
    }
}
