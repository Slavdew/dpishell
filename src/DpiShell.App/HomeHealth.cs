using System.Windows.Input;

namespace DpiShell.App;

/// <summary>Состояние пункта на главной: ✓ — в порядке, ! — рекомендация, ✗ — ошибка; None — не проверялось (серым).</summary>
public enum Health { None, Ok, Warn, Error }

/// <summary>
/// Пункт панели «Этот ПК: …» / «Роутер: …» на главной (вариант A макета): «Метка: значение», у проблемного — кнопка исправления.
/// Данные — из уже существующих моделей (помехи, итог проверки, предупреждения и diag роутера); текст собирается при каждом показе на языке окна.
/// </summary>
/// <param name="Short">Коротко для подсказки значка и заголовка панели («Discord не открывается»); пусто — «Метка: значение».</param>
public sealed record HealthItem(Health State, string Label, string Value, string? Action = null, ICommand? Command = null, object? Parameter = null,
    bool Shield = false, string Short = "", ICommand? HideCommand = null, string? HideKey = null)
{
    public bool HasAction => Action is not null && Command is not null;
    /// <summary>«Скрыть»: рекомендация с постоянной причиной не показывается, пока набор причин тот же (<see cref="HideKey"/>), и после перезапуска.</summary>
    public bool HasHide => HideCommand is not null && HideKey is not null;
    /// <summary>«Метка: » жирным перед значением; без метки — только значение.</summary>
    public string LabelPrefix => Label.Length > 0 ? L.T("home.health.label", new { label = Label }) : "";
    /// <summary>Кнопка со щитом (спросит права) — только пока помощника нет (шаблон Shield сам смотрит NeedsHelper).</summary>
    public bool ShowShield => Shield && HasAction;
    public bool ShowPlain => !Shield && HasAction;
    public string Summary => Short.Length > 0 ? Short : LabelPrefix + Value;

    /// <summary>Худшее из состояний: ошибка важнее рекомендации, рекомендация — «в порядке».</summary>
    public static Health Worst(IEnumerable<HealthItem> items)
    {
        var list = items.ToList();
        if (list.Any(i => i.State == Health.Error)) return Health.Error;
        if (list.Any(i => i.State == Health.Warn)) return Health.Warn;
        return list.Any(i => i.State == Health.Ok) ? Health.Ok : Health.None;
    }

    /// <summary>Подсказка значка и заголовок панели: «Всё в порядке» / «2 рекомендации» / «Ошибка: Discord не открывается».</summary>
    public static string Tip(IReadOnlyList<HealthItem> items) => Worst(items) switch
    {
        Health.Error => L.T("home.health.error", new { text = items.First(i => i.State == Health.Error).Summary }),
        Health.Warn => L.Plural("home.health.warn", items.Count(i => i.State == Health.Warn)),
        Health.Ok => L.T("home.health.ok"),
        _ => L.T("home.health.none"),
    };
}

/// <summary>Плитка сайта на главной: цветная полоса по состоянию, короткая подпись, ниже — время и кто ведёт.</summary>
public sealed record SiteTile(string Title, Health State, string Status, string Detail, string? Action = null, ICommand? Command = null, bool Shield = false)
{
    public bool HasDetail => Detail.Length > 0;
    public bool HasAction => Action is not null && Command is not null;
    /// <summary>Кнопка исправления на плитке: со щитом (спросит права) — только пока помощника нет.</summary>
    public bool ShowShield => Shield && HasAction;
    public bool ShowPlain => !Shield && HasAction;
}

/// <summary>Строка «Проверяется само»: что проверяется и как часто (значения — из настроек расписания и сторожа).</summary>
public sealed record AutoRow(string Key, string Value);

/// <summary>Что открыто на месте плиток сайтов: плитки или панель подробностей ПК / роутера.</summary>
public enum HomePanel { None, Pc, Router }

/// <summary>Итог «Проверить сайт» на главной.</summary>
/// <remarks>FailsWith — на ПК с оптимизацией не открылось; без неё проверится после добавления в исключения. NotInList — режим «Только из списков», сайта нет в списках и он не открылся.</remarks>
public enum SiteCheckOutcome { None, Checking, Opens, OnlyWithout, FailsWith, Closed, NotInList, Message }
