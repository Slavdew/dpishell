using System.Globalization;
using System.Runtime.CompilerServices;
using DpiShell.Core.Localization;

namespace DpiShell.Tests;

/// <summary>
/// Тесты сверяют русские тексты и форматы, а язык окна по умолчанию берётся из языка Windows. На машине CI (GitHub, английская Windows)
/// без этого окно стартовало бы на английском — поэтому до первого теста: русские культура и язык, как у основной аудитории.
/// Тесты, которым нужен английский, переключают язык сами (коллекция lang).
/// </summary>
static class TestCulture
{
    [ModuleInitializer]
    internal static void Init()
    {
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        CultureInfo.DefaultThreadCurrentCulture = ru;
        CultureInfo.DefaultThreadCurrentUICulture = ru;
        CultureInfo.CurrentCulture = ru;
        CultureInfo.CurrentUICulture = ru;
        L.Set(L.Ru);
    }
}
