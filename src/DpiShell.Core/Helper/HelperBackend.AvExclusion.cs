using DpiShell.Core.Localization;

namespace DpiShell.Core.Helper;

/// <summary>Исключение Защитника для папки ядра (API 1.16): путь — корень программы помощника, из запроса не берётся.</summary>
public sealed partial class ShellHelperBackend
{
    /// <summary>Защитник; в тестах — подмена в памяти (настоящие исключения машины не трогаются).</summary>
    public IDefenderSystem Defender { get; init; } = new WmiDefender();

    AvExclusions Av => new(Defender, shell.Paths.Root, shell.Journal);

    public AvExclusionStatus AvExclusionStatus() => Av.Status();

    public AvExclusionResult AvExclusionAdd()
    {
        shell.Paths.EnsureDirectories();
        return Av.Add();
    }

    /// <summary>Снять своё: каждая незакрытая запись журнала — откатом (снимает, только если до нас исключения не было).</summary>
    public AvExclusionResult AvExclusionRemove()
    {
        var open = shell.Journal.Active(ChangeKinds.AvExclusion);
        if (open.Count == 0) return new(false, AvOutcomes.NotOurs, L.T("core.av.not_ours"), Av.Status());
        var items = open.Reverse().SelectMany(e => Changes.UndoAsync(e.Id).GetAwaiter().GetResult().Items).ToList();
        if (items.FirstOrDefault(i => i.Status == UndoStatus.Failed) is { } failed)
            return new(false, AvOutcomes.Refused, failed.Note, Av.Status());
        if (items.All(i => i.Status == UndoStatus.ChangedByOthers))
            return new(false, AvOutcomes.NotOurs, L.T("core.av.not_ours"), Av.Status());
        return new(true, AvOutcomes.Removed, L.T("core.av.removed"), Av.Status());
    }

    /// <summary>«Удалить полностью»: своё исключение — снять (чужое остаётся). Ошибка Защитника удаление не останавливает.</summary>
    void RemoveOwnExclusion()
    {
        try { AvExclusionUndoer.UndoOwn(shell.Journal, Defender, shell.Paths.Root); }
        catch (Exception e) when (AvExclusions.IsWmiError(e) || e is IOException) { }
    }
}
