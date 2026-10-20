using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using DpiShell.App;
using DpiShell.Core;
using static DpiShell.Tests.WindowSmokeTests;

namespace DpiShell.Tests;

/// <summary>
/// Проверка «кириллица в английском окне»: в видимом тексте (TextBlock, TextBox, содержимое кнопок и заголовков, подсказки, карточки «(i)»)
/// не должно быть русских букв, кроме белого списка — имён из данных (сайты, программы, стратегии каталога), которые не переводятся.
/// DPISHELL_CYR_LOG=файл — не бросать исключение, а дописывать находки в файл (для разбора).
/// </summary>
internal static class CyrillicScan
{
    static readonly Regex Cyr = new("[А-Яа-яЁё]", RegexOptions.Compiled);

    /// <summary>Имена, которые приходят из данных теста и не переводятся (названия, введённые человеком, и тексты роутера).</summary>
    static readonly string[] Allowed =
    [
        "Иван",
        // Данные теста, введённые человеком: названия своих стратегий и записей.
        "Стратегия от друга", "Моя правка",
    ];

    static bool Bad(string? s) => !string.IsNullOrEmpty(s) && Cyr.IsMatch(Allowed.Aggregate(s, (t, a) => t.Replace(a, "")));

    public static List<string> Collect(DependencyObject root)
    {
        var found = new List<string>();
        void Add(string kind, string? text) { if (Bad(text)) found.Add($"{kind}: {text!.Replace('\n', ' ')}"); }
        foreach (var t in FindAll<TextBlock>(root, t => t.IsVisible)) { Add("Text", t.Text); Add("Tip", t.ToolTip as string); }
        foreach (var t in FindAll<TextBox>(root, t => t.IsVisible)) { Add("TextBox", t.Text); Add("Tip", t.ToolTip as string); }
        foreach (var c in FindAll<ContentControl>(root, c => c.IsVisible))
        {
            if (c.Content is string s) Add(c.GetType().Name, s);
            if (c is HeaderedContentControl { Header: string h }) Add("Header", h);
            Add("Tip", c.ToolTip as string);
            if (c is InfoButton ib) Add("Info", ib.Text);
        }
        foreach (var t in FindAll<TabItem>(root)) if (t.Header is string h) Add("Tab", h);
        return found.Distinct().ToList();
    }

    /// <summary>
    /// В английском окне — проверить. В русском — переключить на английский на лету (как «Настройки» → «Язык»), проверить то, что
    /// собрано из данных на прежнем языке и не пересобралось, и вернуть русский.
    /// </summary>
    public static void CheckIfEn(DependencyObject root, string where)
    {
        if (L.Current == L.En) { Report(root, where); return; }
        // Диалоги (вход, установка…) строятся один раз и при открытом окне язык не меняют — переключение проверяется на главном окне.
        if (Find<TabControl>(root) is null) return;
        L.Set(L.En);
        try { Pump(); Report(root, where + " (после смены языка)"); }
        finally { L.Set(L.Ru); Pump(); }
    }

    static void Report(DependencyObject root, string where)
    {
        var f = Collect(root);
        if (f.Count == 0) return;
        var log = Environment.GetEnvironmentVariable("DPISHELL_CYR_LOG");
        var text = string.Join("\n", f.Select(x => $"[{where}] {x}"));
        if (log is not null) File.AppendAllText(log, text + "\n");
        else throw new Exception("Кириллица в английском окне:\n" + text);
    }
}
