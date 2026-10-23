using System.IO;
using System.Windows.Input;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.App;

/// <summary>
/// «Указать архив ядра…» (ТЗ av-exclusion, п. 9): GitHub не открывается — ядро из файла выпуска zapret-core-offline.zip (или zapret-vX.zip от bol-van).
/// Окно без прав читает выбранный файл само и передаёт помощнику только байты (core_offline_part, API 1.17); путь помощнику не уходит.
/// Помощник собирает их в своей закрытой папке и ставит ядро, только если sha256 совпал с core.json, — тем же путём, что скачанное.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Выбор файла архива (заголовок, фильтр) → путь или null. В тестах — подмена без настоящего диалога.</summary>
    public Func<string, string, string?> ChooseCoreZip { get; set; } = (title, filter) =>
    {
        var d = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return d.ShowDialog() == true ? d.FileName : null;
    };

    public ICommand CoreOfflineCmd => _coreOfflineCmd ??= RelayCommand.Async(CoreOfflineAsync, () => !Busy && !IsLoading);
    ICommand? _coreOfflineCmd;

    /// <summary>Кнопка видна там же, где «Восстановить»: ядро не найдено или изменено (и на «Диагностике»).</summary>
    const string OfflineCommand = "core_offline_install";

    async Task CoreOfflineAsync()
    {
        if (ChooseCoreZip(L.T("home.core.offline.title"), L.T("home.core.offline.filter")) is not { } file) return;
        long size;
        try { size = new FileInfo(file).Length; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Fail(L.T("home.core.offline.unreadable", new { error = e.Message })); return; }
        if (size > CoreInstaller.MaxOfflineZipBytes) { Fail(L.T("core.core.offline_too_big", new { mb = CoreInstaller.MaxOfflineZipBytes / 1024 / 1024 })); return; }
        await WithHelper(L.T("home.core.offline.busy"), async c =>
        {
            if (!c.Compat.Supports(OfflineCommand)) { Message = L.T("home.core.offline.update_helper"); return; }
            await using (var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                await c.CoreOfflineAsync(s, new Progress<string>(m => Message = m));
            await RefreshDiagAsync();
            await RefreshStatusAsync();
        });
        RaiseAv();
    }
}
