namespace DpiShell.Core;

/// <summary>
/// «Адреса сайтов для проверок» (Настройки): откуда подбор и проверки берут адреса. По умолчанию — через защищённый DNS (DoH): итог зависит от
/// обхода блокировок, а не от DNS провайдера, и DNS компьютера не меняется. «Как у системы» — DoH не используется совсем: в корпоративной сети
/// запросы к внешним DoH-серверам могут быть запрещены, а внутренние сайты известны только DNS сети; проверка видит то же, что браузер.
/// </summary>
public static class CheckDns
{
    /// <summary>Через защищённый DNS (по умолчанию).</summary>
    public const string Secure = "doh";
    /// <summary>Как у системы: DoH не используется.</summary>
    public const string System = "system";

    /// <summary>Откуда адреса взяла последняя проверка (<see cref="IResolverSource"/>): защищённый DNS, система по выбору, система из-за недоступного DoH.</summary>
    public const string SourceDoh = "doh", SourceSystem = "system", SourceFallback = "doh_fallback";

    public static string Normalize(string? mode) => mode == System ? System : Secure;

    public static bool IsSystem(string? mode) => mode == System;
}

/// <summary>Резолвер, который знает, откуда на самом деле пришли адреса: пометка в итоге проверки.</summary>
public interface IResolverSource
{
    /// <summary><see cref="CheckDns.SourceDoh"/>, <see cref="CheckDns.SourceSystem"/> или <see cref="CheckDns.SourceFallback"/>.</summary>
    string AddressSource { get; }
}
