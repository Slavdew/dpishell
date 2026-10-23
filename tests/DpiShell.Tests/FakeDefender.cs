using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using DpiShell.Core;

namespace DpiShell.Tests;

/// <summary>Настоящий Защитник под тестами недоступен: исключения машины не меняются, WMI не читается.</summary>
static class NoRealDefender
{
    [ModuleInitializer]
    internal static void Init() => WmiDefender.Forbidden = true;
}

/// <summary>Защитник в памяти: режим, сторонний антивирус, список исключений и отказы (ошибка WMI, «принял, но не записал», скрытый список).</summary>
public sealed class FakeDefender : IDefenderSystem
{
    public DefenderMode ModeValue { get; set; } = DefenderMode.Active;
    public List<AvProduct> ProductsValue { get; } = [];
    public List<string> Paths { get; } = [];
    /// <summary>Список скрыт политикой (HideExclusionsFromLocalAdmins).</summary>
    public bool Hidden { get; set; }
    /// <summary>Список не читается вовсе (окно без прав, сбой WMI).</summary>
    public bool Unreadable { get; set; }
    /// <summary>Add/Remove бросают ошибку WMI (защита от изменений, политика).</summary>
    public bool Refuse { get; set; }
    /// <summary>Add проходит без ошибки, а папка в списке не появляется (политика молча отбрасывает).</summary>
    public bool SilentDrop { get; set; }
    public List<string> Calls { get; } = [];

    public static FakeDefender Off() => new() { ModeValue = DefenderMode.Off };
    public static FakeDefender Active(params string[] paths) { var d = new FakeDefender(); d.Paths.AddRange(paths); return d; }
    public static FakeDefender ThirdParty(string name) =>
        new() { ModeValue = DefenderMode.Passive, ProductsValue = { new(name, true, false), new("Microsoft Defender Antivirus", false, true) } };

    public IReadOnlyList<AvProduct> Products() => [.. ProductsValue];
    public DefenderMode Mode() => ModeValue;
    public ExclusionList? Exclusions() => Unreadable ? null : Hidden ? new([], true) : new([.. Paths], false);

    public void AddPath(string path)
    {
        Calls.Add("add " + path);
        if (Refuse) throw new System.Management.ManagementException("Operation failed with 0x800106ba");
        if (!SilentDrop && !Paths.Contains(path)) Paths.Add(path);
    }

    /// <summary>Журнал обнаружений (MSFT_MpThreatDetection); ThreatsUnreadable — не прочитать (нет прав, WMI).</summary>
    public List<DefenderThreat> ThreatsValue { get; } = [];
    public bool ThreatsUnreadable { get; set; }
    public IReadOnlyList<DefenderThreat> Threats() =>
        ThreatsUnreadable ? throw new System.Management.ManagementException("Access denied") : [.. ThreatsValue];

    public void RemovePath(string path)
    {
        Calls.Add("remove " + path);
        if (Refuse) throw new System.Management.ManagementException("Operation failed");
        Paths.Remove(path);
    }
}

/// <summary>Журнал изменений в памяти (как <see cref="ChangeJournal"/>, без файла).</summary>
public sealed class MemoryRecorder : IChangeRecorder
{
    readonly List<ChangeEntry> _items = [];

    public IReadOnlyList<ChangeEntry> Items => _items;

    public string Begin(string kind, string title, JsonNode? before, JsonNode? after)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        _items.Add(new(id, DateTime.UtcNow, kind, title, before?.DeepClone(), after?.DeepClone(), ChangeState.Active, false, Order: _items.Count));
        return id;
    }

    void Set(string id, Func<ChangeEntry, ChangeEntry> f)
    {
        int i = _items.FindIndex(e => e.Id == id);
        if (i >= 0) _items[i] = f(_items[i]);
    }

    public void Done(string id, JsonNode? after = null) => Set(id, e => e with { Completed = true, After = after ?? e.After });
    public void Abort(string id) => Set(id, e => e with { State = ChangeState.Aborted });
    public void Update(string id, JsonNode? after) => Set(id, e => e with { After = after });
    public void Close(string kind, string why, Func<ChangeEntry, bool>? filter = null)
    {
        foreach (var e in _items.Where(e => e.State == ChangeState.Active && e.Kind == kind && (filter?.Invoke(e) ?? true)).ToList())
            Set(e.Id, x => x with { State = ChangeState.Closed, Note = why });
    }
    public IReadOnlyList<ChangeEntry> Active(string kind) => [.. _items.Where(e => e.State == ChangeState.Active && e.Kind == kind)];
}
