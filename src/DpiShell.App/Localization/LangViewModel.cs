using System.IO;
using System.Windows.Input;

namespace DpiShell.App;

/// <summary>Язык в списке выбора: флаг, название на своём языке, отметка текущего.</summary>
public sealed class LangItem(LangInfo info, LangViewModel owner) : Observable
{
    public LangInfo Info => info;
    public string Code => info.Code;
    public string Name => info.Name;
    /// <summary>Код для значка: RU, EN, DE.</summary>
    public string Short => info.Code.Split('-')[0].ToUpperInvariant();
    /// <summary>Флаг: код страны ISO 3166 (ru, gb…); нет — код языка (рисуется плашкой с кодом).</summary>
    public string Flag => info.Flag ?? info.Code;
    public bool IsCurrent
    {
        get => string.Equals(L.Current, info.Code, StringComparison.OrdinalIgnoreCase);
        set { if (value) owner.Select(info.Code); }
    }
    public ICommand SelectCmd => _select ??= new RelayCommand(() => owner.Select(info.Code));
    ICommand? _select;
}

/// <summary>
/// Выбор языка (значок внизу меню и строка «Язык» в «Настройках»): применяется сразу, сохраняется в window.json.
/// Сюда же — «Выгрузить шаблон перевода» и причины, по которым свои переводы не взяты (для «Диагностики»).
/// </summary>
public sealed class LangViewModel : Observable
{
    readonly WindowSettings _settings;

    public LangViewModel(WindowSettings settings)
    {
        _settings = settings;
        Reload();   // при смене языка отметки и Current перечитываются сами: Observable поднимает PropertyChanged("")
    }

    public List<LangItem> Items { get; private set; } = [];

    /// <summary>Перечитать список (после поиска своих переводов).</summary>
    public void Reload()
    {
        Items = L.Available.Select(i => new LangItem(i, this)).ToList();
        Raise(nameof(Items)); Raise(nameof(Current)); Raise(nameof(Problems)); Raise(nameof(HasProblems));
    }

    public LangItem Current => Items.FirstOrDefault(i => i.IsCurrent) ?? Items[0];

    bool _open;
    /// <summary>Список языков у значка раскрыт.</summary>
    public bool PickerOpen { get => _open; set { if (Set(ref _open, value)) Raise(nameof(PickerClosed)); } }
    /// <summary>Пока список открыт, значок не ловит щелчок: иначе щелчок по нему закрыл бы список и тут же открыл снова.</summary>
    public bool PickerClosed => !_open;

    /// <summary>Подсказка и имя для диктора у значка: «Язык интерфейса: Русский».</summary>
    public string Tip => L.T("common.lang.tip", new { language = Current.Name });

    public void Select(string code)
    {
        PickerOpen = false;
        L.Set(code);
        if (_settings.Language == L.Current) return;
        _settings.Language = L.Current;
        try { _settings.Save(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public IReadOnlyList<string> Problems => L.Problems;
    public bool HasProblems => L.Problems.Count > 0;

    /// <summary>Куда выгружается шаблон перевода — рядом с window.json.</summary>
    public string TemplateDir => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_settings.File))!, "lang-template");

    /// <summary>Сохранить шаблон перевода; путь к файлу.</summary>
    public string ExportTemplate()
    {
        Directory.CreateDirectory(TemplateDir);
        var file = Path.Combine(TemplateDir, "dpishell-translation.json");
        var version = typeof(L).Assembly.GetName().Version?.ToString(3) ?? "";
        File.WriteAllText(file, LangFiles.Template(L.BuiltIn[L.Ru], L.BuiltIn.GetValueOrDefault(L.En), version));
        return file;
    }
}
