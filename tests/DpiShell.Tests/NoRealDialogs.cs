using System.Runtime.CompilerServices;
using DpiShell.App;

namespace DpiShell.Tests;

static class NoRealDialogs
{
    /// <summary>Сюда в конце прогона пишутся попытки открыть ссылку или папку, которые команда окна поймала сама (тест при этом мог пройти).</summary>
    internal static readonly string HitsFile = Path.Combine(Path.GetTempPath(), "dpishell-tests-open-attempts.txt");

    [ModuleInitializer]
    internal static void Init()
    {
        UiGuard.Forbidden = true;
        try { File.Delete(HitsFile); } catch (IOException) { }
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            if (UiGuard.Hits.IsEmpty) return;
            try { File.WriteAllLines(HitsFile, UiGuard.Hits.Distinct()); } catch (IOException) { }
        };
    }
}
