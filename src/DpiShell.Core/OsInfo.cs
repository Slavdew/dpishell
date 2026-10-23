namespace DpiShell.Core;

/// <summary>
/// Версия Windows — одна точка для всех развилок «Windows 10 / 11»: сборка (<see cref="Build"/>) и границы возможностей.
/// Environment.OSVersion в .NET спрашивает RtlGetVersion, поэтому сборка настоящая даже без supportedOS в манифесте.
/// В тестах — <see cref="Assume"/>: своя сборка на поток выполнения (19045 — Windows 10 22H2, 22631 — Windows 11 23H2), система не спрашивается.
/// </summary>
public static class OsInfo
{
    /// <summary>Минимальная поддерживаемая сборка: Windows 10 1809 (то же, что <c>min_os</c> в release.json).</summary>
    public const int MinSupportedBuild = 17763;
    /// <summary>Первая сборка Windows 10, где тёмный заголовок включается атрибутом DWM 20 (до неё — недокументированный 19).</summary>
    public const int DarkTitleAttr20Build = 18985;
    /// <summary>Сборка Windows 10 22H2 — последняя Windows 10 (для тестов и документации).</summary>
    public const int Windows10_22H2 = 19045;
    /// <summary>Первая сборка Windows 11.</summary>
    public const int Windows11Build = 22000;

    static readonly AsyncLocal<int?> Local = new();

    /// <summary>Сборка Windows (19045, 22631…); в тестах — подменённая.</summary>
    public static int Build => Local.Value ?? Environment.OSVersion.Version.Build;

    /// <summary>Windows 11 и новее.</summary>
    public static bool IsWindows11 => Build >= Windows11Build;

    /// <summary>Встроенный DoH (Set-DnsClientDohServerAddress, netsh dnsclient … encryption) — только с Windows 11.</summary>
    public static bool DohSupported => IsWindows11;

    /// <summary>Номер атрибута DwmSetWindowAttribute для тёмного заголовка: 20, 19 (Windows 10 1809–1909) или 0 — не поддерживается.</summary>
    public static int DarkTitleAttribute(int build) => build >= DarkTitleAttr20Build ? 20 : build >= MinSupportedBuild ? 19 : 0;

    /// <summary>Для тестов: как будто сборка Windows — <paramref name="build"/>, на этом потоке и во всём, что из него запущено.</summary>
    public static IDisposable Assume(int build)
    {
        var prev = Local.Value;
        Local.Value = build;
        return new Undo(() => Local.Value = prev);
    }

    sealed class Undo(Action a) : IDisposable { public void Dispose() => a(); }
}
