using DpiShell.App;

namespace DpiShell.Tests;

/// <summary>
/// Вкладка «Сеть» в тестах: DNS, «Прокси» и «Починить адреса» — блоки одной вкладки. «Страницы» ниже — условные номера для циклов по экранам:
/// <see cref="GoTo"/> открывает вкладку «Сеть» с раскрытым только этим блоком (остальные свёрнуты), как раньше открывалась отдельная вкладка.
/// </summary>
public static class NetPages
{
    public const int Dns = 1000, Proxy = 1001, Hosts = 1002;

    public static bool IsNet(int page) => page is >= Dns and <= Hosts;

    public static void GoTo(this MainViewModel vm, int page)
    {
        if (!IsNet(page)) { vm.Page = page; return; }
        vm.DnsBlockOpen = page == Dns;
        vm.ProxyBlockOpen = page == Proxy;
        vm.HostsBlockOpen = page == Hosts;
        vm.Page = Pages.Net;
    }
}
